using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using System;
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

        // 2. КЭШ ТИПОВ АННОТАЦИЙ
        string typeBigName = "шаг_количество_длина/поз.(фон)";
        string typeSmallName = "шаг_количество/поз.(фон)";

        Dictionary<string, MultiReferenceAnnotationType> typeCache =
            new FilteredElementCollector(doc)
                .OfClass(typeof(MultiReferenceAnnotationType))
                .Cast<MultiReferenceAnnotationType>()
                .Where(x => x.Name == typeBigName || x.Name == typeSmallName)
                .ToDictionary(x => x.Name, x => x);

        // 3. КЭШ СУЩЕСТВУЮЩИХ АННОТАЦИЙ
        HashSet<ElementId> annotatedRebarIds = new HashSet<ElementId>();
        foreach (MultiReferenceAnnotation mra in
            new FilteredElementCollector(doc, view.Id)
                .OfClass(typeof(MultiReferenceAnnotation))
                .Cast<MultiReferenceAnnotation>())
        {
            Dimension existingDim = doc.GetElement(mra.DimensionId) as Dimension;
            if (existingDim == null) continue;
            foreach (Reference r in existingDim.References)
                if (r.ElementId != ElementId.InvalidElementId)
                    annotatedRebarIds.Add(r.ElementId);
        }

        // 4. ВСЕ ОСИ НА ВИДЕ
        List<Grid> grids = new FilteredElementCollector(doc, view.Id)
            .OfClass(typeof(Grid))
            .Cast<Grid>()
            .ToList();

        // 5. ТИП РАЗМЕРА
        DimensionType rebarDimType = new FilteredElementCollector(doc)
            .OfClass(typeof(DimensionType))
            .Cast<DimensionType>()
            .FirstOrDefault(x => x.Name == "BI_основной_2,5мм_округление_до_5мм");

        // Счётчики диагностики
        var skipReasons = new System.Text.StringBuilder();
        int totalRebars = 0;
        int skippedRefs = 0;
        int skippedGrid = 0;
        int skippedGridRef = 0;
        int createdDims = 0;
        int annotationCount = 0;

        using (Transaction t = new Transaction(doc, "Аннотации арматуры"))
        {
            t.Start();

            foreach (Rebar rebar in rebars)
            {
                // Пропускаем уже зааннотированные стержни
                if (annotatedRebarIds.Contains(rebar.Id))
                    continue;

                // 6. ГЕОМЕТРИЯ СТЕРЖНЯ
                IList<Curve> curves = rebar.GetCenterlineCurves(
                    false, false, false,
                    MultiplanarOption.IncludeOnlyPlanarCurves, 0);

                if (curves == null || curves.Count == 0)
                    continue;

                // 7. НАПРАВЛЕНИЕ И СЕРЕДИНА
                XYZ rebarDir;
                XYZ midPoint;

                if (curves.Count == 1 && curves[0] is Line singleLine)
                {
                    rebarDir = (singleLine.GetEndPoint(1)
                               - singleLine.GetEndPoint(0)).Normalize();
                    midPoint = (singleLine.GetEndPoint(0)
                               + singleLine.GetEndPoint(1)) / 2.0;
                }
                else
                {
                    XYZ firstPt = curves.First().GetEndPoint(0);
                    XYZ lastPt = curves.Last().GetEndPoint(1);
                    XYZ chord = lastPt - firstPt;

                    rebarDir = chord.GetLength() > 1e-6
                        ? chord.Normalize()
                        : curves[0].ComputeDerivatives(0.5, true)
                                   .BasisX.Normalize();

                    var pts = new List<XYZ>();
                    foreach (Curve c in curves)
                        pts.AddRange(c.Tessellate());

                    midPoint = new XYZ(
                        (pts.Min(p => p.X) + pts.Max(p => p.X)) / 2.0,
                        (pts.Min(p => p.Y) + pts.Max(p => p.Y)) / 2.0,
                        (pts.Min(p => p.Z) + pts.Max(p => p.Z)) / 2.0);
                }

                // dimDir — направление распределения массива
                // (перпендикуляр к rebarDir в плоскости вида)
                XYZ dimDir = rebarDir.CrossProduct(view.ViewDirection).Normalize();

                // 8. ШАГ И КОЛИЧЕСТВО
                int count = rebar.NumberOfBarPositions;
                double spacing = 0;

                Parameter spacingParam = rebar.get_Parameter(
                    BuiltInParameter.REBAR_ELEM_BAR_SPACING);
                if (spacingParam != null && spacingParam.HasValue)
                    spacing = spacingParam.AsDouble();

                double spacingMm = UnitUtils.ConvertFromInternalUnits(
                    spacing, UnitTypeId.Millimeters);
                double centerOffset = (count > 1 && spacing > 0)
                    ? (count - 1) * spacingMm / 2.0
                    : 0;

                // 9. ВЫБОР ТИПА АННОТАЦИИ
                MultiReferenceAnnotationType typeToUse;
                if (centerOffset < 601)
                    typeCache.TryGetValue(typeSmallName, out typeToUse);
                else
                    typeCache.TryGetValue(typeBigName, out typeToUse);

                if (typeToUse == null)
                    continue;

                // 10. СОЗДАНИЕ АННОТАЦИИ
                MultiReferenceAnnotationOptions options =
                    new MultiReferenceAnnotationOptions(typeToUse);

                options.SetElementsToDimension(new List<ElementId> { rebar.Id });
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

                try
                {
                    MultiReferenceAnnotation.Create(doc, view.Id, options);
                    annotationCount++;
                }
                catch
                {
                    continue;
                }

                // 11. РАЗМЕР МЕЖДУ КРАЙНИМИ СТЕРЖНЯМИ В МАССИВЕ (продольный)
                if (count >= 2 && spacing > 0)
                {
                    try
                    {
                        IList<Curve> firstBarCurves = rebar.GetCenterlineCurves(
                            false, false, false,
                            MultiplanarOption.IncludeOnlyPlanarCurves, 0);
                        IList<Curve> lastBarCurves = rebar.GetCenterlineCurves(
                            false, false, false,
                            MultiplanarOption.IncludeOnlyPlanarCurves, count - 1);

                        if (firstBarCurves?.Count > 0 && lastBarCurves?.Count > 0)
                        {
                            XYZ firstBarMid = GetCurvesMidPoint(firstBarCurves);
                            XYZ lastBarMid = GetCurvesMidPoint(lastBarCurves);
                            XYZ spreadDir = lastBarMid - firstBarMid;

                            if (spreadDir.GetLength() > 1e-6)
                            {
                                spreadDir = spreadDir.Normalize();

                                XYZ spanDimDir = spreadDir
                                    .CrossProduct(view.ViewDirection).Normalize();

                                double spanDimOffset = UnitUtils.ConvertToInternalUnits(
                                    50, UnitTypeId.Millimeters);

                                XYZ spanMid = (firstBarMid + lastBarMid) / 2.0;
                                XYZ dimLinePos = spanMid + spanDimDir * spanDimOffset;

                                double spanLengthMm = UnitUtils.ConvertFromInternalUnits(
                                    (lastBarMid - firstBarMid).GetLength(),
                                    UnitTypeId.Millimeters);
                                if (spanLengthMm < 1.0)
                                    goto SkipToStep12;

                                Options geomOptions = new Options
                                {
                                    View = view,
                                    ComputeReferences = true
                                };

                                double firstProj = firstBarMid.DotProduct(spreadDir);
                                double lastProj = lastBarMid.DotProduct(spreadDir);
                                double tolerance = UnitUtils.ConvertToInternalUnits(
                                    5, UnitTypeId.Millimeters);

                                Reference refFirst = null;
                                Reference refLast = null;

                                foreach (GeometryObject geomObj in
                                    rebar.get_Geometry(geomOptions))
                                {
                                    IEnumerable<GeometryObject> candidates =
                                        geomObj is GeometryInstance gi
                                            ? gi.GetInstanceGeometry()
                                            : Enumerable.Repeat(geomObj, 1);

                                    foreach (GeometryObject candidate in candidates)
                                    {
                                        if (!(candidate is Curve geomCurve)) continue;

                                        XYZ curveMid = geomCurve.Evaluate(0.5, true);
                                        double proj = curveMid.DotProduct(spreadDir);

                                        if (refFirst == null &&
                                            Math.Abs(proj - firstProj) < tolerance)
                                            refFirst = geomCurve.Reference;
                                        else if (refLast == null &&
                                                 Math.Abs(proj - lastProj) < tolerance)
                                            refLast = geomCurve.Reference;

                                        if (refFirst != null && refLast != null) break;
                                    }

                                    if (refFirst != null && refLast != null) break;
                                }

                                if (refFirst != null && refLast != null)
                                {
                                    XYZ p1 = new XYZ(
                                        (dimLinePos - spreadDir * 1.0).X,
                                        (dimLinePos - spreadDir * 1.0).Y,
                                        view.Origin.Z);
                                    XYZ p2 = new XYZ(
                                        (dimLinePos + spreadDir * 1.0).X,
                                        (dimLinePos + spreadDir * 1.0).Y,
                                        view.Origin.Z);
                                    Line dimensionLine = Line.CreateBound(p1, p2);

                                    var ra = new ReferenceArray();
                                    ra.Append(refFirst);
                                    ra.Append(refLast);

                                    if (rebarDimType != null)
                                        doc.Create.NewDimension(
                                            view, dimensionLine, ra, rebarDimType);
                                    else
                                        doc.Create.NewDimension(
                                            view, dimensionLine, ra);
                                }
                            }
                        }
                    }
                    catch { }
                }

            SkipToStep12:

                // 12. ПРОДОЛЬНЫЙ РАЗМЕР ДО БЛИЖАЙШЕЙ ОСИ
                // Цепочка: ось → конец стержня → конец стержня
                if (grids.Count > 0)
                {
                    try
                    {
                        totalRebars++;

                        Options geomOpts = new Options
                        {
                            View = view,
                            ComputeReferences = true,
                            IncludeNonVisibleObjects = true
                        };

                        // Собираем все Line-References стержня
                        var rebarLineRefs = new List<(Line line, Reference reference)>();

                        foreach (GeometryObject go in rebar.get_Geometry(geomOpts))
                        {
                            if (go is Solid) continue;

                            if (go is Line refLine && refLine.Reference != null)
                                rebarLineRefs.Add((refLine, refLine.Reference));
                        }

                        if (rebarLineRefs.Count == 0)
                        {
                            skippedRefs++;
                            skipReasons.AppendLine(
                                $"Rebar {rebar.Id}: нет Line-References");
                        }
                        else
                        {
                            // Ищем торцевые линии (перпендикулярные rebarDir)
                            Reference refEnd0 = null;
                            Reference refEnd1 = null;
                            XYZ ptEnd0 = null;
                            XYZ ptEnd1 = null;

                            foreach (var (ln, rf) in rebarLineRefs)
                            {
                                XYZ lnDir = (ln.GetEndPoint(1)
                                               - ln.GetEndPoint(0)).Normalize();
                                double dot = Math.Abs(lnDir.DotProduct(rebarDir));

                                if (dot < 0.3)
                                {
                                    XYZ lnMid = (ln.GetEndPoint(0)
                                                + ln.GetEndPoint(1)) / 2.0;
                                    if (refEnd0 == null)
                                    {
                                        refEnd0 = rf;
                                        ptEnd0 = lnMid;
                                    }
                                    else if (refEnd1 == null)
                                    {
                                        refEnd1 = rf;
                                        ptEnd1 = lnMid;
                                        break;
                                    }
                                }
                            }

                            // Запасной вариант
                            if (refEnd0 == null || refEnd1 == null)
                            {
                                var sorted = rebarLineRefs
                                    .OrderBy(x => x.line.GetEndPoint(0)
                                                  .DotProduct(rebarDir))
                                    .ToList();

                                refEnd0 = sorted.First().reference;
                                ptEnd0 = sorted.First().line.GetEndPoint(0);
                                refEnd1 = sorted.Last().reference;
                                ptEnd1 = sorted.Last().line.GetEndPoint(0);
                            }

                            if (refEnd0 != null && refEnd1 != null)
                            {
                                // Ближайшая ось перпендикулярная rebarDir
                                Grid nearestGrid = null;
                                double minDist = double.MaxValue;

                                foreach (Grid grid in grids)
                                {
                                    Curve gridCurve = grid.Curve;
                                    if (gridCurve == null) continue;

                                    XYZ gridDir = (gridCurve.GetEndPoint(1)
                                                     - gridCurve.GetEndPoint(0))
                                                    .Normalize();
                                    double dot = Math.Abs(
                                        gridDir.DotProduct(rebarDir));

                                    if (dot > 0.3) continue;

                                    double gridProj = gridCurve.GetEndPoint(0)
                                                               .DotProduct(rebarDir);
                                    double barProj = midPoint.DotProduct(rebarDir);
                                    double dist = Math.Abs(barProj - gridProj);

                                    if (dist < minDist)
                                    {
                                        minDist = dist;
                                        nearestGrid = grid;
                                    }
                                }

                                if (nearestGrid == null)
                                {
                                    skippedGrid++;
                                    skipReasons.AppendLine(
                                        $"Rebar {rebar.Id}: нет перп. оси.");
                                }
                                else
                                {
                                    Options geomOpts12 = new Options
                                    {
                                        View = view,
                                        ComputeReferences = true,
                                        IncludeNonVisibleObjects = true
                                    };

                                    Reference refGrid = null;
                                    foreach (GeometryObject go in
                                        nearestGrid.get_Geometry(geomOpts12))
                                    {
                                        if (go is Line gridLine
                                            && gridLine.Reference != null)
                                        {
                                            refGrid = gridLine.Reference;
                                            break;
                                        }
                                    }
                                    if (refGrid == null)
                                        refGrid = new Reference(nearestGrid);

                                    if (refGrid == null)
                                    {
                                        skippedGridRef++;
                                        skipReasons.AppendLine(
                                            $"Rebar {rebar.Id}: нет Reference оси.");
                                    }
                                    else
                                    {
                                        double gridProjValue = nearestGrid.Curve
                                            .GetEndPoint(0).DotProduct(rebarDir);

                                        double distEnd0Mm = UnitUtils
                                            .ConvertFromInternalUnits(
                                                Math.Abs(ptEnd0.DotProduct(rebarDir)
                                                         - gridProjValue),
                                                UnitTypeId.Millimeters);
                                        double distEnd1Mm = UnitUtils
                                            .ConvertFromInternalUnits(
                                                Math.Abs(ptEnd1.DotProduct(rebarDir)
                                                         - gridProjValue),
                                                UnitTypeId.Millimeters);

                                        if (!(distEnd0Mm < 1.0 && distEnd1Mm < 1.0))
                                        {
                                            double gridDimOffset =
                                                UnitUtils.ConvertToInternalUnits(
                                                    100, UnitTypeId.Millimeters);

                                            XYZ midProjected = new XYZ(
                                                midPoint.X, midPoint.Y, view.Origin.Z);
                                            XYZ dimLineMid = midProjected
                                                           + dimDir * gridDimOffset;

                                            Line dimLine = Line.CreateBound(
                                                new XYZ(
                                                    (dimLineMid - rebarDir * 2.0).X,
                                                    (dimLineMid - rebarDir * 2.0).Y,
                                                    view.Origin.Z),
                                                new XYZ(
                                                    (dimLineMid + rebarDir * 2.0).X,
                                                    (dimLineMid + rebarDir * 2.0).Y,
                                                    view.Origin.Z));

                                            var chainRa = new ReferenceArray();

                                            if (distEnd0Mm < 1.0)
                                            {
                                                chainRa.Append(refGrid);
                                                chainRa.Append(refEnd1);
                                            }
                                            else if (distEnd1Mm < 1.0)
                                            {
                                                chainRa.Append(refGrid);
                                                chainRa.Append(refEnd0);
                                            }
                                            else
                                            {
                                                chainRa.Append(refGrid);
                                                chainRa.Append(refEnd0);
                                                chainRa.Append(refEnd1);
                                            }

                                            if (rebarDimType != null)
                                                doc.Create.NewDimension(
                                                    view, dimLine, chainRa,
                                                    rebarDimType);
                                            else
                                                doc.Create.NewDimension(
                                                    view, dimLine, chainRa);

                                            createdDims++;
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        skipReasons.AppendLine(
                            $"Rebar {rebar.Id}: exception — {ex.Message}");
                    }
                }

                // 13. ПОПЕРЕЧНЫЙ РАЗМЕР ДО БЛИЖАЙШЕЙ ОСИ
                // Цепочка: ось → первый крайний стержень → последний крайний стержень
                // Ось параллельна dimDir (направлению распределения массива)
                if (count < 2 || spacing <= 0 || grids.Count == 0)
                    continue;

                try
                {
                    Options geomOptsT = new Options
                    {
                        View = view,
                        ComputeReferences = true,
                        IncludeNonVisibleObjects = true
                    };

                    // Физические позиции крайних стержней вдоль dimDir
                    double halfSpan = (count - 1) * spacing / 2.0;
                    XYZ firstMidT = midPoint - dimDir * halfSpan;
                    XYZ lastMidT = midPoint + dimDir * halfSpan;

                    // References крайних стержней — проецируем на dimDir
                    double firstProjT = firstMidT.DotProduct(dimDir);
                    double lastProjT = lastMidT.DotProduct(dimDir);
                    double toleranceT = UnitUtils.ConvertToInternalUnits(
                        5, UnitTypeId.Millimeters);

                    Reference refFirstT = null;
                    Reference refLastT = null;

                    foreach (GeometryObject geomObj in rebar.get_Geometry(geomOptsT))
                    {
                        IEnumerable<GeometryObject> candidates =
                            geomObj is GeometryInstance giT
                                ? giT.GetInstanceGeometry()
                                : Enumerable.Repeat(geomObj, 1);

                        foreach (GeometryObject candidate in candidates)
                        {
                            if (!(candidate is Curve geomCurve)) continue;

                            XYZ cm = geomCurve.Evaluate(0.5, true);
                            double proj = cm.DotProduct(dimDir);

                            if (refFirstT == null &&
                                Math.Abs(proj - firstProjT) < toleranceT)
                                refFirstT = geomCurve.Reference;
                            else if (refLastT == null &&
                                     Math.Abs(proj - lastProjT) < toleranceT)
                                refLastT = geomCurve.Reference;

                            if (refFirstT != null && refLastT != null) break;
                        }

                        if (refFirstT != null && refLastT != null) break;
                    }

                    if (refFirstT == null || refLastT == null)
                        continue;

                    // Ближайшая ось параллельная dimDir
                    // dot(gridDir, dimDir) > 0.7
                    Grid nearestGridT = null;
                    double minDistT = double.MaxValue;

                    foreach (Grid grid in grids)
                    {
                        Curve gridCurve = grid.Curve;
                        if (gridCurve == null) continue;

                        XYZ gridDir = (gridCurve.GetEndPoint(1)
                                         - gridCurve.GetEndPoint(0)).Normalize();
                        double dot = Math.Abs(gridDir.DotProduct(dimDir));

                        // Только параллельные dimDir
                        if (dot < 0.7) continue;

                        // Расстояние вдоль rebarDir от стержня до оси
                        double gridProj = gridCurve.GetEndPoint(0)
                                                   .DotProduct(rebarDir);
                        double barProj = midPoint.DotProduct(rebarDir);
                        double dist = Math.Abs(barProj - gridProj);

                        if (dist < minDistT)
                        {
                            minDistT = dist;
                            nearestGridT = grid;
                        }
                    }

                    if (nearestGridT == null)
                        continue;

                    // Reference на ось
                    Reference refGridT = null;
                    foreach (GeometryObject go in nearestGridT.get_Geometry(geomOptsT))
                    {
                        if (go is Line gridLine && gridLine.Reference != null)
                        {
                            refGridT = gridLine.Reference;
                            break;
                        }
                    }
                    if (refGridT == null)
                        refGridT = new Reference(nearestGridT);

                    if (refGridT == null)
                        continue;

                    // Расстояния от крайних стержней до оси вдоль rebarDir
                    double gridProjValueT = nearestGridT.Curve
                        .GetEndPoint(0).DotProduct(rebarDir);

                    double distFirstMm = UnitUtils.ConvertFromInternalUnits(
                        Math.Abs(firstMidT.DotProduct(rebarDir) - gridProjValueT),
                        UnitTypeId.Millimeters);
                    double distLastMm = UnitUtils.ConvertFromInternalUnits(
                        Math.Abs(lastMidT.DotProduct(rebarDir) - gridProjValueT),
                        UnitTypeId.Millimeters);

                    // Если оба на оси — пропускаем
                    if (distFirstMm < 1.0 && distLastMm < 1.0)
                        continue;

                    // Линия размера вдоль dimDir, отступает вдоль rebarDir
                    double transOffset = UnitUtils.ConvertToInternalUnits(
                        100, UnitTypeId.Millimeters);

                    XYZ transMid = midPoint + rebarDir * transOffset;
                    XYZ dimLineMidT = new XYZ(transMid.X, transMid.Y, view.Origin.Z);

                    Line dimLineT = Line.CreateBound(
                        new XYZ(
                            (dimLineMidT - dimDir * 2.0).X,
                            (dimLineMidT - dimDir * 2.0).Y,
                            view.Origin.Z),
                        new XYZ(
                            (dimLineMidT + dimDir * 2.0).X,
                            (dimLineMidT + dimDir * 2.0).Y,
                            view.Origin.Z));

                    // Формируем цепочку с проверкой нулевых значений
                    var chainRaT = new ReferenceArray();

                    if (distFirstMm < 1.0)
                    {
                        // Первый стержень на оси
                        chainRaT.Append(refGridT);
                        chainRaT.Append(refLastT);
                    }
                    else if (distLastMm < 1.0)
                    {
                        // Последний стержень на оси
                        chainRaT.Append(refGridT);
                        chainRaT.Append(refFirstT);
                    }
                    else
                    {
                        // Полная цепочка: ось → первый → последний
                        chainRaT.Append(refGridT);
                        chainRaT.Append(refFirstT);
                        chainRaT.Append(refLastT);
                    }

                    if (rebarDimType != null)
                        doc.Create.NewDimension(
                            view, dimLineT, chainRaT, rebarDimType);
                    else
                        doc.Create.NewDimension(
                            view, dimLineT, chainRaT);
                }
                catch { }

            } // конец foreach (Rebar rebar)

            t.Commit();
        }

        // Итоговое сообщение
        string diagInfo = createdDims < totalRebars && skipReasons.Length > 0
            ? $"\n\nДиагностика:\n{skipReasons}"
            : string.Empty;

        TaskDialog.Show("Готово",
            $"На данном виде создано {annotationCount} аннотаций\n" +
            $"Размеров до оси создано: {createdDims} из {totalRebars}" +
            diagInfo);

        return Result.Succeeded;
    }

    // Геометрический центр набора кривых
    private static XYZ GetCurvesMidPoint(IList<Curve> curves)
    {
        if (curves.Count == 1 && curves[0] is Line line)
            return (line.GetEndPoint(0) + line.GetEndPoint(1)) / 2.0;

        var pts = new List<XYZ>();
        foreach (Curve c in curves)
            pts.AddRange(c.Tessellate());

        return new XYZ(
            (pts.Min(p => p.X) + pts.Max(p => p.X)) / 2.0,
            (pts.Min(p => p.Y) + pts.Max(p => p.Y)) / 2.0,
            (pts.Min(p => p.Z) + pts.Max(p => p.Z)) / 2.0);
    }
}