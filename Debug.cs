using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

namespace DAN_Plugin
{
    [Transaction(TransactionMode.Manual)]
    public class CreateWallSection : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uiDoc = commandData.Application.ActiveUIDocument;
            Document doc = uiDoc.Document;

            // Шаг 1: выбор сборки
            AssemblyInstance assembly = null;
            try
            {
                Reference pickedRef = uiDoc.Selection.PickObject(
                    ObjectType.Element,
                    new AssemblySelectionFilter(),
                    "Выберите сборку стен");
                assembly = doc.GetElement(pickedRef) as AssemblyInstance;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled;
            }

            if (assembly == null)
            {
                TaskDialog.Show("Ошибка", "Не удалось получить сборку.");
                return Result.Failed;
            }

            // Шаг 2: получаем стены из сборки
            List<Wall> walls = assembly.GetMemberIds()
                .Select(id => doc.GetElement(id))
                .OfType<Wall>()
                .ToList();

            if (!walls.Any())
            {
                TaskDialog.Show("Ошибка", "В сборке не найдено стен.");
                return Result.Failed;
            }

            // Шаг 3: берём первую стену для определения направления и габаритов
            Wall wall = walls.First();
            LocationCurve locationCurve = wall.Location as LocationCurve;
            if (locationCurve == null)
            {
                TaskDialog.Show("Ошибка", "Не удалось получить линию стены.");
                return Result.Failed;
            }

            Line wallLine = locationCurve.Curve as Line;
            if (wallLine == null)
            {
                TaskDialog.Show("Ошибка", "Стена не прямолинейная.");
                return Result.Failed;
            }

            // Шаг 4: вычисляем параметры разреза
            // Направление вдоль стены
            XYZ wallDir = wallLine.Direction.Normalize();
            // Направление разреза (перпендикулярно стене в горизонтальной плоскости)
            XYZ sectionDir = new XYZ(-wallDir.Y, wallDir.X, 0).Normalize();
            XYZ upDir = XYZ.BasisZ;

            // Высота разреза — 1100 мм
            double sectionHeight = UnitUtils.ConvertToInternalUnits(1100, UnitTypeId.Millimeters);

            // Смещение базовой точки проекта
            double elevationOffset = 0;
            Level anyLevel = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .FirstOrDefault();
            if (anyLevel != null)
                elevationOffset = anyLevel.ProjectElevation - anyLevel.Elevation;

            // Центр разреза по длине стены и на высоте 1100 мм
            XYZ wallStart = wallLine.GetEndPoint(0);
            XYZ wallEnd = wallLine.GetEndPoint(1);
            double wallLength = wallLine.Length;

            // Центр по длине стены
            XYZ wallMid = (wallStart + wallEnd) / 2.0;

            // BoundingBox стены для определения высоты
            BoundingBoxXYZ wallBB = wall.get_BoundingBox(null);
            double wallBottom = wallBB.Min.Z;
            double wallTop = wallBB.Max.Z;
            double wallActualHeight = wallTop - wallBottom;

            // Центр разреза на высоте 1100 мм от низа стены
            XYZ sectionOrigin = new XYZ(
                wallMid.X,
                wallMid.Y,
                wallBottom + sectionHeight);

            // Шаг 5: строим Transform для разреза
            // X — вдоль стены, Y — вверх, Z — направление взгляда (перпендикулярно стене)
            Transform sectionTransform = Transform.Identity;
            sectionTransform.BasisX = wallDir;
            sectionTransform.BasisY = upDir;
            sectionTransform.BasisZ = sectionDir;
            sectionTransform.Origin = sectionOrigin;

            // Шаг 6: BoundingBox разреза
            // X — по длине стены (половина в каждую сторону + 500 мм отступ)
            // Y — по высоте (от -1100 до +wallHeight-1100 + 500 мм отступ)
            // Z — глубина разреза (толщина стены + 500 мм с каждой стороны)
            double wallThickness = wall.Width;
            double offsetH = UnitUtils.ConvertToInternalUnits(500, UnitTypeId.Millimeters);
            double offsetD = UnitUtils.ConvertToInternalUnits(500, UnitTypeId.Millimeters);

            BoundingBoxXYZ sectionBox = new BoundingBoxXYZ();
            sectionBox.Transform = sectionTransform;
            sectionBox.Min = new XYZ(
                -wallLength / 2.0 - offsetH,
                -sectionHeight - offsetH,
                -wallThickness / 2.0 - offsetD);
            sectionBox.Max = new XYZ(
                wallLength / 2.0 + offsetH,
                wallActualHeight - sectionHeight + offsetH,
                wallThickness / 2.0 + offsetD);

            // Шаг 7: тип разреза — берём первый доступный
            ViewFamilyType sectionType = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewFamilyType))
                .Cast<ViewFamilyType>()
                .FirstOrDefault(vft => vft.ViewFamily == ViewFamily.Section);

            if (sectionType == null)
            {
                TaskDialog.Show("Ошибка", "Тип разреза не найден в проекте.");
                return Result.Failed;
            }

            // Шаг 8: генерируем имя разреза
            string assemblyComment = assembly
                .get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString()
                ?? assembly.Name;

            string sectionName = GenerateSectionName(doc, assemblyComment);

            using (Transaction tx = new Transaction(doc, "Создать разрез стены"))
            {
                tx.Start();
                try
                {
                    ViewSection newSection = ViewSection.CreateSection(doc, sectionType.Id, sectionBox);
                    newSection.Name = sectionName;

                    // Заполняем параметр BI_марка_конструкции из комментария сборки
                    Parameter markParam = newSection.LookupParameter("BI_марка_конструкции");
                    if (markParam != null && !markParam.IsReadOnly)
                        markParam.Set(assemblyComment);

                    tx.Commit();
                    TaskDialog.Show("Готово", $"Создан разрез: {sectionName}");
                }
                catch (Exception ex)
                {
                    tx.RollBack();
                    TaskDialog.Show("Ошибка", ex.Message);
                    return Result.Failed;
                }
            }

            return Result.Succeeded;
        }

        /// <summary>
        /// Генерирует имя разреза вида "Комментарий_Разрез_1-1".
        /// Если "1-1" уже занят — берёт следующий номер "2-2" и т.д.
        /// </summary>
        private string GenerateSectionName(Document doc, string assemblyComment)
        {
            // Собираем все существующие имена видов
            var existingNames = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSection))
                .Cast<ViewSection>()
                .Select(v => v.Name)
                .ToHashSet();

            for (int i = 1; i <= 100; i++)
            {
                string candidate = $"{assemblyComment}_Разрез_{i}-{i}";
                if (!existingNames.Contains(candidate))
                    return candidate;
            }

            return $"{assemblyComment}_Разрез_{Guid.NewGuid():N}";
        }
    }
}