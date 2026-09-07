using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

namespace RebarPlugin.FoundationStarters
{
    // ======================================================================
    //  Скелет команды: выпуски арматуры в плиты от вертикальной арматуры стены
    //  Revit 2023 (.NET Framework 4.8)
    //
    //  Логика:
    //   1. Пользователь выбирает сборку стены (AssemblyInstance).
    //   2. Из параметра "Комментарии" сборки читаем марку (СБм-1 / СБм-5 ...).
    //   3. Пользователь выбирает плиту фундамента (StructuralFoundation) под стеной.
    //   4. Собираем Rebar, у которых BI_марка_конструкции == марка.
    //   5. Делим наборы на грани (внутренняя/наружная) и по высоте (низкий/высокий).
    //   6. Для каждого набора строим набор Г-образных выпусков по существующей форме.
    //
    //  Помечено // CALIBRATE — то, что нужно доуточнить, когда придут форма,
    //  имена её параметров и таблица вылетов.
    // ======================================================================

    [Transaction(TransactionMode.Manual)]
    public class FoundationStarterCommand : IExternalCommand
    {
        // ---- Константы спеки -------------------------------------------------
        private const string MarkParamName = "BI_марка_конструкции"; // параметр марки на Rebar
        private const string ShapeName     = "(форма)11";             // CALIBRATE: имя RebarShape
        private const string PolkaParam    = "BI_B";                     // CALIBRATE: имя размера полки в форме
        private const string VertParam     = "BI_A";                     // CALIBRATE: имя размера вертикали в форме

        // Длина полки выпуска, мм: зависит от диаметра стержня.
        private const int DiaThresholdMm      = 22;   // граница диаметра
        private const double PolkaLenSmallMm  = 200.0; // диаметр < DiaThresholdMm
        private const double PolkaLenLargeMm  = 400.0; // диаметр >= DiaThresholdMm

        private const double CoverMm     = 75.0; // защитный слой, мм

        // Вылет над обрезом фундамента, мм: диаметр(мм) -> (низкий набор, высокий набор)
        // CALIBRATE: заполнить реальными значениями из своего списка.
        private static readonly Dictionary<int, (double Low, double High)> VyletByDiaMm =
            new Dictionary<int, (double, double)>
            {
                { 10, (1050, 1770) },
                { 12, (1160, 2020) },
                { 14, (1270, 2280) },
                { 16, (1380, 2530) },
                { 18, (1490, 2780) },
                { 20, (1600, 3030) },
                { 22, (700, 1200) },
                { 25, (700, 1200) },
                { 28, (700, 1200) }
            };

        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            UIDocument uidoc = data.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            try
            {
                // 1. Выбор сборки стены
                AssemblyInstance assembly = PickAssembly(uidoc);
                if (assembly == null) return Result.Cancelled;

                // 2. Марка из "Комментариев"
                string mark = assembly.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString();
                if (string.IsNullOrWhiteSpace(mark))
                {
                    message = "У сборки не заполнен параметр \"Комментарии\" (марка).";
                    return Result.Failed;
                }

                // 3. Выбор плиты фундамента
                Element foundation = PickFoundation(uidoc);
                if (foundation == null) return Result.Cancelled;

                // 4. Сбор арматуры стены по марке
                List<Rebar> wallRebars = CollectWallRebar(doc, mark);
                if (wallRebars.Count == 0)
                {
                    message = $"Не найдена арматура с {MarkParamName} = \"{mark}\".";
                    return Result.Failed;
                }

                // Форма выпуска
                RebarShape shape = new FilteredElementCollector(doc)
                    .OfClass(typeof(RebarShape))
                    .Cast<RebarShape>()
                    .FirstOrDefault(s => s.Name == ShapeName);
                if (shape == null)
                {
                    message = $"Не найдена форма арматуры \"{ShapeName}\".";
                    return Result.Failed;
                }

                // Стена-хост и её геометрия (ось + наружная нормаль)
                Wall wall = GetHostWall(doc, wallRebars[0]);
                if (wall == null)
                {
                    message = "Не удалось получить стену-хост для арматуры.";
                    return Result.Failed;
                }
                XYZ wallExterior = wall.Orientation.Normalize();         // наружу от стены
                Line wallAxis = (wall.Location as LocationCurve)?.Curve as Line;

                // Отметки фундамента (плоская плита -> BBox достаточно)
                BoundingBoxXYZ fbb = foundation.get_BoundingBox(null);
                double foundTopZ = fbb.Max.Z;
                double foundBotZ = fbb.Min.Z;
                double coverFt = MmToFt(CoverMm);

                using (Transaction t = new Transaction(doc, "Выпуски арматуры в фундамент"))
                {
                    t.Start();

                    foreach (Rebar wallSet in wallRebars)
                    {
                        SetInfo info = ClassifySet(wallSet, wall, wallExterior);
                        BuildStarterSet(doc, shape, wallSet, foundation,
                                        info, wallExterior, foundTopZ, foundBotZ, coverFt);
                    }

                    t.Commit();
                }

                return Result.Succeeded;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }

        // ---- Выбор элементов -------------------------------------------------

        private AssemblyInstance PickAssembly(UIDocument uidoc)
        {
            Reference r = uidoc.Selection.PickObject(
                ObjectType.Element, new AssemblyFilter(), "Выберите сборку стены");
            return uidoc.Document.GetElement(r) as AssemblyInstance;
        }

        private Element PickFoundation(UIDocument uidoc)
        {
            Reference r = uidoc.Selection.PickObject(
                ObjectType.Element, new CategoryFilter(BuiltInCategory.OST_StructuralFoundation),
                "Выберите плиту фундамента под стеной");
            return uidoc.Document.GetElement(r);
        }

        // ---- Сбор и классификация -------------------------------------------

        private List<Rebar> CollectWallRebar(Document doc, string mark)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(Rebar))
                .Cast<Rebar>()
                .Where(rb => string.Equals(
                    rb.LookupParameter(MarkParamName)?.AsString(), mark,
                    StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        private Wall GetHostWall(Document doc, Rebar rebar)
        {
            return doc.GetElement(rebar.GetHostId()) as Wall;
        }

        // Грани (внутр./наружн.) + высота (низкий/высокий)
        private SetInfo ClassifySet(Rebar wallSet, Wall wall, XYZ exterior)
        {
            var acc = wallSet.GetShapeDrivenAccessor();
            XYZ p0 = acc.GetBarPositionTransform(0).Origin;

            // Сторона грани: знак проекции (позиция бара - ось стены) на наружную нормаль
            XYZ axisPt = (wall.Location as LocationCurve).Curve.Project(p0).XYZPoint;
            double side = (p0 - axisPt).DotProduct(exterior);
            bool isExterior = side > 0;

            // Низкий/высокий: по отметке низа. CALIBRATE:
            // если у наборов близкие Z — сравнивать попарно на грани, а не абсолютным порогом.
            double bottomZ = p0.Z;

            return new SetInfo
            {
                IsExterior = isExterior,
                BottomZ = bottomZ
                // IsHigh проставим ниже, после сравнения наборов одной грани
            };
        }

        // ---- Построение выпусков --------------------------------------------

        private void BuildStarterSet(
            Document doc, RebarShape shape, Rebar wallSet, Element host,
            SetInfo info, XYZ exterior, double foundTopZ, double foundBotZ, double coverFt)
        {
            RebarBarType barType = doc.GetElement(wallSet.GetTypeId()) as RebarBarType;
            double diaFt = barType.BarModelDiameter;
            int diaMm = (int)Math.Round(FtToMm(diaFt));

            // Геометрия выпуска
            double polkaLen = MmToFt(SelectPolkaLen(diaMm));            // фикс. длина полки по диаметру
            double vyletMm  = SelectVylet(diaMm, info.IsHigh);          // из таблицы
            double vyletFt  = MmToFt(vyletMm);
            double polkaZ   = foundBotZ + coverFt;                      // полка по нижнему слою
            double vertLen  = (foundTopZ - polkaZ) + vyletFt;           // вертикаль до верх+вылет

            // Направление полки: наружу от центра стены для своей грани
            XYZ polkaDir = info.IsExterior ? exterior : exterior.Negate();

            // Раскладка исходного набора (стержень-в-стержень)
            var acc = wallSet.GetShapeDrivenAccessor();
            int count = wallSet.NumberOfBarPositions;
            XYZ first = acc.GetBarPositionTransform(0).Origin;
            XYZ last  = acc.GetBarPositionTransform(count - 1).Origin;
            double spacing = count > 1 ? first.DistanceTo(last) / (count - 1) : 0.0;

            // Точка вставки первого выпуска (план — как у стержня стены, Z — на полке)
            XYZ origin = new XYZ(first.X, first.Y, polkaZ);
            XYZ xVec = polkaDir;      // CALIBRATE: сверить с локальной системой формы
            XYZ yVec = XYZ.BasisZ;

            Rebar starter = Rebar.CreateFromRebarShape(
                doc, shape, barType, host, origin, xVec, yVec);

            // Размеры формы
            SetShapeParam(starter, PolkaParam, polkaLen);
            SetShapeParam(starter, VertParam, vertLen);

            // Тиражирование в набор вдоль оси стены
            if (count > 1)
            {
                starter.GetShapeDrivenAccessor().SetLayoutAsNumberWithSpacing(
                    count, spacing, true, true, true);
                // CALIBRATE: если раскладка пошла в обратную сторону — развернуть normal.
            }
        }

        private static double SelectPolkaLen(int diaMm) =>
            diaMm < DiaThresholdMm ? PolkaLenSmallMm : PolkaLenLargeMm;

        private double SelectVylet(int diaMm, bool isHigh)
        {
            if (VyletByDiaMm.TryGetValue(diaMm, out var v))
                return isHigh ? v.High : v.Low;
            throw new InvalidOperationException($"Нет вылета для диаметра {diaMm} мм.");
        }

        private void SetShapeParam(Rebar rebar, string paramName, double valueFt)
        {
            Parameter p = rebar.LookupParameter(paramName);
            if (p != null && !p.IsReadOnly) p.Set(valueFt);
        }

        // ---- Утилиты ---------------------------------------------------------

        private static double MmToFt(double mm) =>
            UnitUtils.ConvertToInternalUnits(mm, UnitTypeId.Millimeters);

        private static double FtToMm(double ft) =>
            UnitUtils.ConvertFromInternalUnits(ft, UnitTypeId.Millimeters);

        private class SetInfo
        {
            public bool IsExterior;
            public bool IsHigh;
            public double BottomZ;
        }
    }

    // ---- Фильтры выбора ------------------------------------------------------

    internal class AssemblyFilter : ISelectionFilter
    {
        public bool AllowElement(Element e) => e is AssemblyInstance;
        public bool AllowReference(Reference r, XYZ p) => false;
    }

    internal class CategoryFilter : ISelectionFilter
    {
        private readonly BuiltInCategory _cat;
        public CategoryFilter(BuiltInCategory cat) => _cat = cat;
        public bool AllowElement(Element e) =>
            e.Category != null && e.Category.Id.IntegerValue == (int)_cat;
        public bool AllowReference(Reference r, XYZ p) => false;
    }
}
