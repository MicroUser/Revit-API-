using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using LiraToRevit.Rebar;

namespace DAN_Plugin
{
    // ─────────────────────────────────────────────────────────────────────────
    // Диагностика подбора Г/П-образной формы у края плиты (см. SupportDetector.HasParallelSupport
    // в RebarZones\SupportDetector.cs): выбираем плиту и точку рядом со стеной/колонной, которую
    // нужно проверить, и получаем — сколько опор («Категория именования» = Стены/Несущие
    // колонны) вообще найдено в проекте, их габариты и LongAxisIsX, ближайшую грань контура
    // плиты у указанной точки и итоговый вердикт (параллельна/перпендикулярна) для каждой
    // опоры рядом с точкой. Без выбора элементов сборки — сразу по указанной точке, без
    // привязки к конкретному размещаемому стержню (что упрощает разбор конкретного случая).
    // Как и другие команды модуля — без кнопки на ленте, запуск через Add-In Manager.
    // ─────────────────────────────────────────────────────────────────────────
    [Transaction(TransactionMode.Manual)]
    public class DiagnosePylonOrientation : IExternalCommand
    {
        private sealed class FloorFilter : ISelectionFilter
        {
            public bool AllowElement(Element elem) => elem is Floor;
            public bool AllowReference(Reference reference, XYZ position) => false;
        }

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uiDoc = commandData.Application.ActiveUIDocument;
            Document doc = uiDoc.Document;

            Floor floor;
            try
            {
                Reference r = uiDoc.Selection.PickObject(ObjectType.Element, new FloorFilter(), "Выберите плиту/фундамент");
                floor = doc.GetElement(r) as Floor;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }
            if (floor == null)
            {
                message = "Выбранный элемент не является плитой.";
                return Result.Failed;
            }

            XYZ pt;
            try { pt = uiDoc.Selection.PickPoint("Укажите точку рядом со стеной/колонной, которую нужно проверить"); }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }

            var settings = new PlacementSettings();
            List<SupportDetector.SupportInfo> allSupports = SupportDetector.ResolveSupports(doc, settings);
            SlabGeometry slab = SlabGeometry.From(doc, floor);
            XYZ tangent = slab.NearestBoundaryTangent(pt);

            // Сборка может содержать стены/колонны сразу нескольких этажей (одна и та же стена,
            // стоящая друг над другом на уровнях 1,2,3...) — оставляем только те, что по высоте
            // относятся к ВЫБРАННОЙ плите (см. SupportDetector.SupportZToleranceMm).
            Level floorLevel = doc.GetElement(floor.LevelId) as Level;
            double floorZ = floorLevel?.Elevation ?? pt.Z;
            double zTol = UnitUtils.ConvertToInternalUnits(SupportDetector.SupportZToleranceMm, UnitTypeId.Millimeters);
            List<SupportDetector.SupportInfo> supports = allSupports
                .Where(s => floorZ >= s.BBoxMin.Z - zTol && floorZ <= s.BBoxMax.Z + zTol)
                .ToList();

            double MmX(double ft) => UnitUtils.ConvertFromInternalUnits(ft, UnitTypeId.Millimeters);
            double DistTo(SupportDetector.SupportInfo s, XYZ p)
            {
                double cx = Math.Max(s.BBoxMin.X, Math.Min(p.X, s.BBoxMax.X));
                double cy = Math.Max(s.BBoxMin.Y, Math.Min(p.Y, s.BBoxMax.Y));
                double dx = p.X - cx, dy = p.Y - cy;
                return Math.Sqrt(dx * dx + dy * dy);
            }

            var sb = new StringBuilder();
            sb.AppendLine($"Опор найдено в проекте (\"Категория именования\" = Стены/Несущие колонны): {allSupports.Count}");
            sb.AppendLine($"  из них по высоте относятся к выбранной плите (уровень={MmX(floorZ):0}мм, допуск±{SupportDetector.SupportZToleranceMm:0}мм): {supports.Count}");
            sb.AppendLine($"Точка: ({MmX(pt.X):0};{MmX(pt.Y):0}) мм");

            // Сырые данные — чтобы понять, на каком шаге теряются опоры, если supports.Count==0:
            // параметр не найден на сборке (LookupParameter==null), найден но пустой текст, или
            // текст есть но не содержит "Стены"/"Несущие колонны".
            var allAssemblies = new FilteredElementCollector(doc).OfClass(typeof(AssemblyInstance))
                .Cast<AssemblyInstance>().ToList();
            int withParam = 0, withText = 0;
            foreach (var a in allAssemblies)
            {
                Parameter p = a.LookupParameter(settings.NamingCategoryParam);
                if (p != null) withParam++;
                if (!string.IsNullOrEmpty(SupportDetector.SupportParamText(doc, p))) withText++;
            }
            sb.AppendLine();
            sb.AppendLine($"[Сырые данные] Всего сборок (AssemblyInstance) в проекте: {allAssemblies.Count}");
            sb.AppendLine($"  из них с параметром \"{settings.NamingCategoryParam}\" (LookupParameter != null): {withParam}");
            sb.AppendLine($"  из них с непустым текстом параметра: {withText}");
            sb.AppendLine("  Первые 15 сборок (Id, текст параметра):");
            foreach (var a in allAssemblies.Take(15))
            {
                Parameter p = a.LookupParameter(settings.NamingCategoryParam);
                string text = SupportDetector.SupportParamText(doc, p);
                sb.AppendLine($"    Id={a.Id} текст=\"{text}\"");
            }
            sb.AppendLine();

            if (tangent == null)
            {
                sb.AppendLine("Не удалось найти ближайшую грань контура плиты у указанной точки — контур не построен?");
            }
            else
            {
                bool edgeAlongX = Math.Abs(tangent.X) >= Math.Abs(tangent.Y);
                sb.AppendLine($"Касательная ближайшей грани плиты: ({tangent.X:0.00};{tangent.Y:0.00}) → edgeAlongX={edgeAlongX}");
                sb.AppendLine();
                sb.AppendLine("Опоры (по возрастанию расстояния до точки):");
                foreach (var sup in supports.OrderBy(s => DistTo(s, pt)))
                {
                    double x1 = MmX(sup.BBoxMin.X), x2 = MmX(sup.BBoxMax.X);
                    double y1 = MmX(sup.BBoxMin.Y), y2 = MmX(sup.BBoxMax.Y);
                    bool parallel = edgeAlongX == sup.LongAxisIsX;
                    sb.AppendLine($"  расст.={MmX(DistTo(sup, pt)):0}мм bbox X=[{x1:0};{x2:0}] Y=[{y1:0};{y2:0}] " +
                        $"LongAxisIsX={sup.LongAxisIsX} → {(parallel ? "ПАРАЛЛЕЛЬНА (П-шка)" : "перпендикулярна (Г-шка)")}");
                } 
            }

            TaskDialog.Show("Диагностика ориентации пилона/колонны", sb.ToString());
            return Result.Succeeded;
        }
    }
}
 