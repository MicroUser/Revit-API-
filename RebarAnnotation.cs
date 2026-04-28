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

        // 3. КЭШ ТИПОВ (ВАЖНО: один раз)
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
                // 4. Геометрия
                IList<Curve> curves = rebar.GetCenterlineCurves(
                    false,
                    false,
                    false,
                    MultiplanarOption.IncludeOnlyPlanarCurves,
                    0);

                if (curves == null || curves.Count == 0 || !(curves[0] is Line))
                    continue;

                Line line = curves[0] as Line;

                XYZ startPoint = line.GetEndPoint(0);
                XYZ endPoint = line.GetEndPoint(1);

                XYZ rebarDir = (endPoint - startPoint).Normalize();

                XYZ dimDir = rebarDir
                    .CrossProduct(view.ViewDirection)
                    .Normalize();

                XYZ midPoint = (startPoint + endPoint) / 2.0;

                // 5. шаг и количество
                int count = rebar.NumberOfBarPositions;

                double spacing = 0;

                Parameter spacingParam = rebar.get_Parameter(
                    BuiltInParameter.REBAR_ELEM_BAR_SPACING);

                if (spacingParam != null && spacingParam.HasValue)
                {
                    spacing = spacingParam.AsDouble();
                }

                double spacingMm = UnitUtils.ConvertFromInternalUnits(
                    spacing,
                    UnitTypeId.Millimeters);

                double centerOffset = 0;

                if (count > 1 && spacing > 0)
                {
                    centerOffset = (count - 1) * spacingMm / 2.0;
                }

                // 6. ВЫБОР ТИПА (ОПТИМИЗИРОВАНО)
                MultiReferenceAnnotationType typeToUse;

                if (centerOffset < 601)
                {
                    typeCache.TryGetValue(typeSmallName, out typeToUse);
                }
                else
                {
                    typeCache.TryGetValue(typeBigName, out typeToUse);
                }

                if (typeToUse == null)
                    continue;

                // 7. OPTIONS
                MultiReferenceAnnotationOptions options =
                    new MultiReferenceAnnotationOptions(typeToUse);

                options.SetElementsToDimension(
                    new List<ElementId> { rebar.Id });

                options.DimensionPlaneNormal = view.ViewDirection;
                options.DimensionLineDirection = dimDir;

                double dimOffset =
                    UnitUtils.ConvertToInternalUnits(
                        10,
                        UnitTypeId.Millimeters);

                XYZ dimOrigin = midPoint + dimDir * dimOffset;
                options.DimensionLineOrigin = dimOrigin;

                double tagOffset =
                    UnitUtils.ConvertToInternalUnits(
                        centerOffset,
                        UnitTypeId.Millimeters);

                XYZ tagPosition = dimOrigin - dimDir * tagOffset;
                options.TagHeadPosition = tagPosition;

                // 8. СОЗДАНИЕ
                try
                {
                    MultiReferenceAnnotation.Create(
                        doc,
                        view.Id,
                        options);
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