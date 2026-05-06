using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using System.Collections.Generic;
using System.Linq;

[Transaction(TransactionMode.Manual)]
public class CreateRebarAnnotation : IExternalCommand
{
    public Result Execute(
        ExternalCommandData commandData,
        ref string message,
        ElementSet elements)
    {
        UIDocument uidoc = commandData.Application.ActiveUIDocument;
        Document doc = uidoc.Document;
        View view = doc.ActiveView;

        // 1. ВСЯ АРМАТУРА НА ВИДЕ
        List<Rebar> rebars = new FilteredElementCollector(doc, view.Id)
            .OfClass(typeof(Rebar))
            .Cast<Rebar>()
            .ToList();

        if (rebars.Count == 0)
        {
            TaskDialog.Show("Ошибка", "На виде нет арматуры");
            return Result.Failed;
        }

        // 2. Порог (601 мм → футы)
        double threshold = UnitUtils.ConvertToInternalUnits(
            601,
            UnitTypeId.Millimeters);

        // 3. КЭШ ТИПОВ (один раз)
        string typeBigName = "шаг_количество_длина/поз.(фон)";
        string typeSmallName = "шаг_количество/поз.(фон)";

        Dictionary<string, MultiReferenceAnnotationType> typeCache =
            new FilteredElementCollector(doc)
                .OfClass(typeof(MultiReferenceAnnotationType))
                .Cast<MultiReferenceAnnotationType>()
                .Where(x => x.Name == typeBigName || x.Name == typeSmallName)
                .ToDictionary(x => x.Name, x => x);

        using (Transaction t = new Transaction(doc, "Аннотации арматуры"))
        {
            t.Start();

            foreach (Rebar rebar in rebars)
            {
                // 4. Геометрия — все кривые стержня
                IList<Curve> curves = rebar.GetCenterlineCurves(
                    false,
                    false,
                    false,
                    MultiplanarOption.IncludeOnlyPlanarCurves,
                    0);

                if (curves == null || curves.Count == 0)
                    continue;

                // 5. Направление размерной линии и midPoint
                //    — работает и для прямых, и для непрямых стержней
                XYZ rebarDir;
                XYZ midPoint;

                if (curves.Count == 1 && curves[0] is Line singleLine)
                {
                    // Прямой стержень — старая логика
                    rebarDir = (singleLine.GetEndPoint(1)
                               - singleLine.GetEndPoint(0)).Normalize();
                    midPoint = (singleLine.GetEndPoint(0)
                               + singleLine.GetEndPoint(1)) / 2.0;
                }
                else
                {
                    // Непрямой стержень:
                    // направление — от первой точки до последней точки
                    // midPoint    — геометрический центр bbox всех кривых
                    XYZ firstPt = curves.First().GetEndPoint(0);
                    XYZ lastPt = curves.Last().GetEndPoint(1);

                    XYZ chord = lastPt - firstPt;

                    // Если стержень замкнут (кольцо), chord ≈ 0 —
                    // берём касательную в середине первой кривой
                    rebarDir = chord.GetLength() > 1e-6
                        ? chord.Normalize()
                        : curves[0].ComputeDerivatives(0.5, true)
                                   .BasisX.Normalize();

                    // Центр через BoundingBox точек дискретизации
                    var pts = new List<XYZ>();
                    foreach (Curve c in curves)
                    {
                        // tessellate даёт равномерные точки по каждой кривой
                        pts.AddRange(c.Tessellate());
                    }

                    double minX = pts.Min(p => p.X), maxX = pts.Max(p => p.X);
                    double minY = pts.Min(p => p.Y), maxY = pts.Max(p => p.Y);
                    double minZ = pts.Min(p => p.Z), maxZ = pts.Max(p => p.Z);

                    midPoint = new XYZ(
                        (minX + maxX) / 2.0,
                        (minY + maxY) / 2.0,
                        (minZ + maxZ) / 2.0);
                }

                XYZ dimDir = rebarDir
                    .CrossProduct(view.ViewDirection)
                    .Normalize();

                // 6. Шаг и количество
                int count = rebar.NumberOfBarPositions;

                double spacing = 0;

                Parameter spacingParam = rebar.get_Parameter(
                    BuiltInParameter.REBAR_ELEM_BAR_SPACING);

                if (spacingParam != null && spacingParam.HasValue)
                    spacing = spacingParam.AsDouble();

                double spacingMm = UnitUtils.ConvertFromInternalUnits(
                    spacing,
                    UnitTypeId.Millimeters);

                double centerOffset = 0;

                if (count > 1 && spacing > 0)
                    centerOffset = (count - 1) * spacingMm / 2.0;

                // 7. Выбор типа аннотации
                MultiReferenceAnnotationType typeToUse;

                if (centerOffset < 601)
                    typeCache.TryGetValue(typeSmallName, out typeToUse);
                else
                    typeCache.TryGetValue(typeBigName, out typeToUse);

                if (typeToUse == null)
                    continue;

                // 8. Options
                MultiReferenceAnnotationOptions options =
                    new MultiReferenceAnnotationOptions(typeToUse);

                options.SetElementsToDimension(
                    new List<ElementId> { rebar.Id });

                options.DimensionPlaneNormal = view.ViewDirection;
                options.DimensionLineDirection = dimDir;

                double dimOffset = UnitUtils.ConvertToInternalUnits(
                    10, UnitTypeId.Millimeters);

                XYZ dimOrigin = midPoint + dimDir * dimOffset;
                options.DimensionLineOrigin = dimOrigin;

                double tagOffset = UnitUtils.ConvertToInternalUnits(
                    centerOffset, UnitTypeId.Millimeters);

                XYZ tagPosition = dimOrigin - dimDir * tagOffset;
                options.TagHeadPosition = tagPosition;

                // 9. Создание
                try
                {
                    MultiReferenceAnnotation.Create(doc, view.Id, options);
                }
                catch
                {
                    continue;
                }
            }

            t.Commit();
        }

        return Result.Succeeded;
    }
}