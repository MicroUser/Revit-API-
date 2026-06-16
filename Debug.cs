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

    // ─────────────────────────────────────────────────────────────────────────
    // Диагностика: выбрать стену → показать найденные проёмы двумя методами.
    // ─────────────────────────────────────────────────────────────────────────
    [Transaction(TransactionMode.Manual)]
    public class DebugWallOpenings : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uiDoc = commandData.Application.ActiveUIDocument;
            Document doc    = uiDoc.Document;
            View activeView = doc.ActiveView;

            Reference pickedRef;
            try
            {
                pickedRef = uiDoc.Selection.PickObject(ObjectType.Element, "Выберите стену");
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled;
            }

            Wall wall = doc.GetElement(pickedRef) as Wall;
            if (wall == null)
            {
                TaskDialog.Show("Ошибка", "Выбранный элемент не является стеной.");
                return Result.Failed;
            }

            // Ориентация грани берётся из ГЕОМЕТРИИ СТЕНЫ (не из активного вида).
            // Это позволяет запускать диагностику из любого вида — плана, разреза, фасада.
            LocationCurve wallLocCurve = wall.Location as LocationCurve;
            Line wallLocLine = wallLocCurve?.Curve as Line;
            XYZ faceRight, faceDir;
            if (wallLocLine != null)
            {
                faceRight = wallLocLine.Direction.Normalize();           // вдоль длины стены
                faceDir   = new XYZ(-faceRight.Y, faceRight.X, 0).Normalize(); // нормаль к грани
            }
            else
            {
                faceRight = activeView.RightDirection.Normalize();
                faceDir   = activeView.ViewDirection.Normalize();
            }

            double Mm(double v) => Math.Round(UnitUtils.ConvertFromInternalUnits(v, UnitTypeId.Millimeters));

            BoundingBoxXYZ wallBbGlobal = wall.get_BoundingBox(null);
            double wallHeightGlobal = wallBbGlobal != null
                ? wallBbGlobal.Max.Z - wallBbGlobal.Min.Z : double.MaxValue;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Стена Id={wall.Id}  Тип: {wall.WallType?.Name}");
            sb.AppendLine($"faceDir=({faceDir.X:F2},{faceDir.Y:F2},{faceDir.Z:F2})  faceRight=({faceRight.X:F2},{faceRight.Y:F2},{faceRight.Z:F2})");
            sb.AppendLine($"Высота стены (BBox): {Mm(wallHeightGlobal)}мм");
            sb.AppendLine();

            // ── Метод 1: inner loops через геометрию с видом ─────────────────
            sb.AppendLine("=== Метод 1: inner loops (геометрия с View) ===");
            var openingsGeom = new List<(double minR, double maxR, double botZ, double topZ)>();

            // Для view-зависимых вырезов (семейства проёмов) нужен вид-разрез или фасад.
            // Из плана или 3D эти вырезы не видны. Используем активный вид если он подходит,
            // иначе берём геометрию без вида (профильные вырезы видны в любом режиме).
            ViewType avt = activeView.ViewType;
            bool viewIsSection = avt == ViewType.Section || avt == ViewType.Elevation || avt == ViewType.Detail;
            Options geomOpts = viewIsSection
                ? new Options { ComputeReferences = false, View = activeView }
                : new Options { ComputeReferences = false };

            GeometryElement geom = wall.get_Geometry(geomOpts);

            if (!viewIsSection)
                sb.AppendLine($"  Вид «{activeView.Name}» — не разрез. Геометрия без View: семейства проёмов могут отсутствовать.");

            if (geom == null)
            {
                sb.AppendLine("  Геометрия не получена (null).");
            }
            else
            {
                int solidIdx = 0;
                foreach (GeometryObject go in geom)
                {
                    Solid s = go as Solid;
                    if (s == null || s.Faces.IsEmpty) continue;
                    solidIdx++;

                    foreach (Face f in s.Faces)
                    {
                        PlanarFace pf = f as PlanarFace;
                        if (pf == null) continue;
                        if (Math.Abs(pf.FaceNormal.Z) > 1e-3) continue;
                        // Лицевая грань: нормаль совпадает с faceDir (из геометрии стены)
                        double dot = pf.FaceNormal.DotProduct(faceDir);
                        if (Math.Abs(dot) < 0.9) continue;
                        if (dot < 0) continue; // пропустить заднюю грань

                        IList<CurveLoop> loops;
                        try { loops = pf.GetEdgesAsCurveLoops(); } catch { continue; }

                        sb.AppendLine($"  Solid#{solidIdx}: лицевая грань (dot={dot:F2}), loops={loops?.Count ?? 0}");

                        if (loops == null || loops.Count == 0) continue;

                        var boxes = loops.Select(loop =>
                        {
                            double mn = double.MaxValue, mx = double.MinValue;
                            double bz = double.MaxValue, tz = double.MinValue;
                            foreach (Curve c in loop)
                                foreach (XYZ p in c.Tessellate())
                                {
                                    double r = p.DotProduct(faceRight);
                                    if (r < mn) mn = r; if (r > mx) mx = r;
                                    if (p.Z < bz) bz = p.Z; if (p.Z > tz) tz = p.Z;
                                }
                            return (mn, mx, bz, tz);
                        }).ToList();

                        // Outer loop = контур с максимальной площадью (ширина × высота)
                        int outerIdx = -1;
                        double maxArea = double.MinValue;
                        for (int li = 0; li < boxes.Count; li++)
                        {
                            double area = (boxes[li].mx - boxes[li].mn) * (boxes[li].tz - boxes[li].bz);
                            if (area > maxArea) { maxArea = area; outerIdx = li; }
                        }

                        double outerMinR = boxes[outerIdx].mn;
                        double outerMaxR = boxes[outerIdx].mx;
                        double edgeTolDbg = UnitUtils.ConvertToInternalUnits(30, UnitTypeId.Millimeters);

                        // Вывод всех контуров
                        for (int li = 0; li < boxes.Count; li++)
                        {
                            double lw = Mm(boxes[li].mx - boxes[li].mn);
                            double lh = Mm(boxes[li].tz - boxes[li].bz);
                            if (li == outerIdx)
                            {
                                sb.AppendLine($"    Loop#{li}: {lw}×{lh}мм  ← ВНЕШНИЙ контур");
                            }
                            else
                            {
                                bool touchesLeft  = Math.Abs(boxes[li].mn - outerMinR) < edgeTolDbg;
                                bool touchesRight = Math.Abs(boxes[li].mx - outerMaxR) < edgeTolDbg;
                                bool isArtifact   = touchesLeft || touchesRight;
                                string tag = isArtifact
                                    ? $"← АРТЕФАКТ (касается {(touchesLeft ? "лев." : "прав.")} края, игнорируется)"
                                    : "← ПРОЁМ";
                                sb.AppendLine($"    Loop#{li}: {lw}×{lh}мм  {tag}");
                                if (!isArtifact)
                                    openingsGeom.Add((boxes[li].mn, boxes[li].mx, boxes[li].bz, boxes[li].tz));
                            }
                        }
                    }
                }
            }
            sb.AppendLine($"  Итого проёмов (inner loops): {openingsGeom.Count}");
            sb.AppendLine();

            // ── Метод 2: FindInserts ──────────────────────────────────────────
            sb.AppendLine("=== Метод 2: FindInserts ===");
            ICollection<ElementId> inserts = wall.FindInserts(true, false, true, true);
            int insertCount = inserts?.Count ?? 0;
            sb.AppendLine($"  Найдено вставок: {insertCount}");
            if (inserts != null)
            {
                foreach (ElementId insId in inserts)
                {
                    Element ins = doc.GetElement(insId);
                    string cat  = ins?.Category?.Name ?? "—";
                    string name = ins?.Name ?? "—";
                    sb.AppendLine($"    Id={insId}  Категория: {cat}  Имя: {name}");
                }
            }

            TaskDialog td = new TaskDialog("Диагностика проёмов")
            {
                MainInstruction = $"Проёмов найдено (inner loops): {openingsGeom.Count}  |  FindInserts: {insertCount}",
                MainContent     = sb.ToString(),
                CommonButtons   = TaskDialogCommonButtons.Close
            };
            td.Show();

            return Result.Succeeded;
        }
    }
}