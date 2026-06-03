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
        int totalRebars = 0;          // стержней, для которых пытались строить размеры до оси
        int createdDims = 0;          // продольных размеров до оси
        int createdTransDims = 0;     // поперечных размеров до оси
        int annotationCount = 0;

        // Данные продольных размеров — для дедупликации соседних одинаковых
        var longRecords = new List<LongRecord>();

        // Размещение поперечного размера: выносится за торец стержней, чтобы его было видно.
        // true — за дальний конец (по +rebarDir), false — за ближний (по -rebarDir).
        const bool transverseOnRight = false;
        // Отступ линии размера от торца стержней
        double transverseSideMargin = UnitUtils.ConvertToInternalUnits(
            400, UnitTypeId.Millimeters);

        // Отступ продольного размера от стержня. Линия ставится на этом расстоянии
        // от торцов, к которым идёт размер, поэтому это же значение = длина выносных
        // линий ("ножек"). Уменьшайте, чтобы укоротить ножки.
        double longitudinalOffset = UnitUtils.ConvertToInternalUnits(
            350, UnitTypeId.Millimeters);

        // Сторона выноса продольного размера за массив: true — за дальний край (+dimDir),
        // false — за ближний край (-dimDir). Размер привязывается к торцам крайнего
        // стержня этой стороны, поэтому ножки остаются короткими.
        const bool longitudinalAbove = true;

        // Во сколько шагов массива допускается разрыв вдоль раскладки, чтобы соседние
        // массивы одинаковой длины считались одной группой (один продольный размер).
        // Такой же массив дальше этого разрыва (в другой части) получит свой размер.
        const double longDedupGapFactor = 3.0;

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
                    // Прямой стержень
                    rebarDir = (singleLine.GetEndPoint(1)
                               - singleLine.GetEndPoint(0)).Normalize();
                    midPoint = (singleLine.GetEndPoint(0)
                               + singleLine.GetEndPoint(1)) / 2.0;
                }
                else
                {
                    // Г-образный (и любой составной) стержень: направление берём
                    // по самому длинному прямому сегменту — это основной ход
                    // стержня, а не диагональ "первая точка → последняя точка".
                    Line mainLine = null;
                    double maxLen = 0;
                    foreach (Curve c in curves)
                    {
                        if (c is Line ln && ln.Length > maxLen)
                        {
                            maxLen = ln.Length;
                            mainLine = ln;
                        }
                    }

                    if (mainLine != null)
                    {
                        rebarDir = (mainLine.GetEndPoint(1)
                                   - mainLine.GetEndPoint(0)).Normalize();
                        midPoint = (mainLine.GetEndPoint(0)
                                   + mainLine.GetEndPoint(1)) / 2.0;
                    }
                    else
                    {
                        // Запасной вариант — по хорде (если прямых сегментов нет)
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

                // 11. ПРОДОЛЬНЫЙ РАЗМЕР ДО БЛИЖАЙШЕЙ ОСИ
                // Размер идёт вдоль rebarDir (вдоль стержней).
                // Цепочка: ось → конец стержня → конец стержня.
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
                            skipReasons.AppendLine(
                                $"Rebar {rebar.Id}: продол. — нет Line-References");
                        }
                        else
                        {
                            // Торцевые линии (перпендикулярные rebarDir) с уровнем вдоль dimDir
                            Reference refEnd0 = null;
                            Reference refEnd1 = null;
                            XYZ ptEnd0 = null;
                            XYZ ptEnd1 = null;

                            var endLines = new List<(Reference reference, XYZ mid, double level)>();
                            foreach (var (ln, rf) in rebarLineRefs)
                            {
                                XYZ lnDir = (ln.GetEndPoint(1)
                                               - ln.GetEndPoint(0)).Normalize();
                                if (Math.Abs(lnDir.DotProduct(rebarDir)) < 0.3)
                                {
                                    XYZ lnMid = (ln.GetEndPoint(0)
                                                + ln.GetEndPoint(1)) / 2.0;
                                    endLines.Add((rf, lnMid, lnMid.DotProduct(dimDir)));
                                }
                            }

                            if (endLines.Count >= 2)
                            {
                                // Уровень КРАЙНЕГО стержня на выбранной стороне массива
                                double edgeLevel = longitudinalAbove
                                    ? endLines.Max(e => e.level)
                                    : endLines.Min(e => e.level);

                                double lvlTol = UnitUtils.ConvertToInternalUnits(
                                    5, UnitTypeId.Millimeters);

                                // Два торца крайнего стержня (его концы вдоль rebarDir)
                                var edgeEnds = endLines
                                    .Where(e => Math.Abs(e.level - edgeLevel) < lvlTol)
                                    .OrderBy(e => e.mid.DotProduct(rebarDir))
                                    .ToList();

                                if (edgeEnds.Count >= 2)
                                {
                                    refEnd0 = edgeEnds.First().reference;
                                    ptEnd0 = edgeEnds.First().mid;
                                    refEnd1 = edgeEnds.Last().reference;
                                    ptEnd1 = edgeEnds.Last().mid;
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
                                // Ближайшая ось, перпендикулярная rebarDir
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
                                    skipReasons.AppendLine(
                                        $"Rebar {rebar.Id}: продол. — нет перп. оси");
                                }
                                else
                                {
                                    Reference refGrid = null;
                                    foreach (GeometryObject go in
                                        nearestGrid.get_Geometry(geomOpts))
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
                                        skipReasons.AppendLine(
                                            $"Rebar {rebar.Id}: продол. — нет Reference оси");
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
                                            // Не создаём размер сразу — копим данные,
                                            // чтобы после цикла убрать дубли для соседних
                                            // массивов одинаковой длины и привязки.
                                            longRecords.Add(new LongRecord
                                            {
                                                GridId = nearestGrid.Id,
                                                GridRef = refGrid,
                                                End0Ref = refEnd0,
                                                End1Ref = refEnd1,
                                                PtEnd0 = ptEnd0,
                                                PtEnd1 = ptEnd1,
                                                Dist0Mm = distEnd0Mm,
                                                Dist1Mm = distEnd1Mm,
                                                End0Proj = ptEnd0.DotProduct(rebarDir),
                                                End1Proj = ptEnd1.DotProduct(rebarDir),
                                                EdgeLevel =
                                                    (ptEnd0.DotProduct(dimDir)
                                                     + ptEnd1.DotProduct(dimDir)) / 2.0,
                                                Lo = endLines.Count > 0
                                                    ? endLines.Min(e => e.level)
                                                    : ptEnd0.DotProduct(dimDir),
                                                Hi = endLines.Count > 0
                                                    ? endLines.Max(e => e.level)
                                                    : ptEnd0.DotProduct(dimDir),
                                                DimDir = dimDir,
                                                RebarDir = rebarDir,
                                                Spacing = spacing
                                            });
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        skipReasons.AppendLine(
                            $"Rebar {rebar.Id}: продол. — {ex.Message}");
                    }
                }

                // 12. ПОПЕРЕЧНЫЙ РАЗМЕР ДО БЛИЖАЙШЕЙ ОСИ
                // Размер идёт вдоль dimDir (направление раскладки массива).
                // Цепочка: ближайшая ось (перпендикулярная dimDir) → крайние стержни.
                // Заменяет прежний "размер между крайними стержнями":
                // межстержневой размер уже входит сегментом в эту цепочку.
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

                    // Все линии стержня, параллельные rebarDir (осевые + кромки): при показе
                    // арматуры телом видны кромки сечения, а осевые приходят как невидимые
                    // объекты благодаря IncludeNonVisibleObjects. Нужную осевую выберем ниже
                    // по близости к расчётному центру стержня (кромки смещены на ±радиус).
                    // Отгибы г-образных стержней перпендикулярны rebarDir и сюда не попадают.
                    var barLines = new List<(Reference reference, double proj)>();

                    // Габарит поля стержней вдоль rebarDir (для выноса размера за торец)
                    double rebarMin = double.MaxValue;
                    double rebarMax = double.MinValue;

                    foreach (GeometryObject go in rebar.get_Geometry(geomOptsT))
                    {
                        IEnumerable<GeometryObject> objs =
                            go is GeometryInstance giT
                                ? giT.GetInstanceGeometry()
                                : Enumerable.Repeat(go, 1);

                        foreach (GeometryObject o in objs)
                        {
                            if (!(o is Line ln) || ln.Reference == null) continue;

                            XYZ lnDir = (ln.GetEndPoint(1)
                                           - ln.GetEndPoint(0)).Normalize();
                            // только продольные линии (вдоль rebarDir)
                            if (Math.Abs(lnDir.DotProduct(rebarDir)) < 0.9) continue;

                            XYZ lnMid = (ln.GetEndPoint(0) + ln.GetEndPoint(1)) / 2.0;
                            barLines.Add((ln.Reference, lnMid.DotProduct(dimDir)));

                            double e0 = ln.GetEndPoint(0).DotProduct(rebarDir);
                            double e1 = ln.GetEndPoint(1).DotProduct(rebarDir);
                            rebarMin = Math.Min(rebarMin, Math.Min(e0, e1));
                            rebarMax = Math.Max(rebarMax, Math.Max(e0, e1));
                        }
                    }

                    if (barLines.Count < 2)
                    {
                        skipReasons.AppendLine(
                            $"Rebar {rebar.Id}: попереч. — не найдены осевые линии стержней");
                        continue;
                    }

                    // Центры крайних стержней массива вычисляем из самих линий, НЕ через
                    // индекс стержня (GetCenterlineCurves с индексом нестабилен и может
                    // вернуть один и тот же стержень). Крайние кромки дают габарит массива,
                    // а центр каждого крайнего стержня лежит внутрь на радиус.
                    double barRadius = 0;
                    RebarBarType barType = doc.GetElement(rebar.GetTypeId()) as RebarBarType;
                    if (barType != null)
                        barRadius = barType.BarModelDiameter / 2.0;

                    double pMin = barLines.Min(b => b.proj);
                    double pMax = barLines.Max(b => b.proj);
                    double firstTarget = pMin + barRadius;
                    double lastTarget = pMax - barRadius;

                    // Из всех линий берём ближайшую к центру — это осевая, а не кромка
                    var firstBar = barLines
                        .OrderBy(b => Math.Abs(b.proj - firstTarget)).First();
                    var lastBar = barLines
                        .OrderBy(b => Math.Abs(b.proj - lastTarget)).First();

                    // Подстраховка: если выбрался один и тот же стержень — берём
                    // самую дальнюю от него осевую как второй край
                    if (Math.Abs(firstBar.proj - lastBar.proj) < 1e-6)
                        lastBar = barLines
                            .OrderByDescending(b => Math.Abs(b.proj - firstBar.proj))
                            .First();

                    Reference refFirstT = firstBar.reference;
                    Reference refLastT = lastBar.reference;
                    double firstPosT = firstBar.proj;
                    double lastPosT = lastBar.proj;

                    // Ближайшая ось, ПЕРПЕНДИКУЛЯРНАЯ dimDir (расстояние меряем вдоль dimDir)
                    Grid nearestGridT = null;
                    double minDistT = double.MaxValue;

                    foreach (Grid grid in grids)
                    {
                        Curve gridCurve = grid.Curve;
                        if (gridCurve == null) continue;

                        XYZ gridDir = (gridCurve.GetEndPoint(1)
                                         - gridCurve.GetEndPoint(0)).Normalize();
                        if (Math.Abs(gridDir.DotProduct(dimDir)) > 0.3) continue;

                        double dist = Math.Abs(
                            gridCurve.GetEndPoint(0).DotProduct(dimDir)
                            - midPoint.DotProduct(dimDir));

                        if (dist < minDistT)
                        {
                            minDistT = dist;
                            nearestGridT = grid;
                        }
                    }

                    if (nearestGridT == null)
                    {
                        skipReasons.AppendLine(
                            $"Rebar {rebar.Id}: попереч. — нет оси, перпендикулярной раскладке");
                        continue;
                    }

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
                    {
                        skipReasons.AppendLine(
                            $"Rebar {rebar.Id}: попереч. — нет Reference оси");
                        continue;
                    }

                    // Положение оси вдоль dimDir
                    double gridPos = nearestGridT.Curve
                        .GetEndPoint(0).DotProduct(dimDir);
                    double distFirstMm = UnitUtils.ConvertFromInternalUnits(
                        Math.Abs(firstPosT - gridPos), UnitTypeId.Millimeters);
                    double distLastMm = UnitUtils.ConvertFromInternalUnits(
                        Math.Abs(lastPosT - gridPos), UnitTypeId.Millimeters);

                    // Оба крайних стержня на оси — нечего мерить
                    if (distFirstMm < 1.0 && distLastMm < 1.0)
                        continue;

                    // Линию размера выносим за торец стержней (вбок), чтобы её было
                    // видно: к нужному концу поля вдоль rebarDir + отступ.
                    // transverseOnRight: true — за дальний конец (+rebarDir),
                    // false — за ближний (-rebarDir).
                    double targetRebarProj = transverseOnRight
                        ? rebarMax + transverseSideMargin
                        : rebarMin - transverseSideMargin;
                    double shift = targetRebarProj - midPoint.DotProduct(rebarDir);
                    XYZ transMid = midPoint + rebarDir * shift;
                    XYZ dimLineMidT = new XYZ(transMid.X, transMid.Y, view.Origin.Z);

                    Line dimLineT = Line.CreateBound(
                        new XYZ((dimLineMidT - dimDir * 2.0).X,
                                (dimLineMidT - dimDir * 2.0).Y, view.Origin.Z),
                        new XYZ((dimLineMidT + dimDir * 2.0).X,
                                (dimLineMidT + dimDir * 2.0).Y, view.Origin.Z));

                    var chainRaT = new ReferenceArray();
                    if (distFirstMm < 1.0)
                    {
                        chainRaT.Append(refGridT);
                        chainRaT.Append(refLastT);
                    }
                    else if (distLastMm < 1.0)
                    {
                        chainRaT.Append(refGridT);
                        chainRaT.Append(refFirstT);
                    }
                    else
                    {
                        chainRaT.Append(refGridT);
                        chainRaT.Append(refFirstT);
                        chainRaT.Append(refLastT);
                    }

                    if (rebarDimType != null)
                        doc.Create.NewDimension(view, dimLineT, chainRaT, rebarDimType);
                    else
                        doc.Create.NewDimension(view, dimLineT, chainRaT);

                    createdTransDims++;
                }
                catch (Exception ex)
                {
                    skipReasons.AppendLine($"Rebar {rebar.Id}: попереч. — {ex.Message}");
                }

            } // конец foreach (Rebar rebar)

            // ПРОДОЛЬНЫЕ РАЗМЕРЫ: один размер на группу соседних массивов одинаковой
            // длины и привязки. Подпись = ось + положения торцов вдоль стержня.
            // Внутри подписи массивы кластеризуются по близости вдоль раскладки:
            // соседние → один размер; такой же массив в другой части → отдельный.
            foreach (var grp in longRecords.GroupBy(r => LongKey(r)))
            {
                var sorted = grp.OrderBy(r => r.Lo).ToList();

                var clusters = new List<List<LongRecord>>();
                foreach (var r in sorted)
                {
                    if (clusters.Count == 0)
                    {
                        clusters.Add(new List<LongRecord> { r });
                        continue;
                    }
                    var last = clusters[clusters.Count - 1];
                    double lastHi = last.Max(x => x.Hi);
                    double gapTol = longDedupGapFactor
                        * Math.Max(r.Spacing, last.Max(x => x.Spacing));
                    if (r.Lo - lastHi <= gapTol)
                        last.Add(r);
                    else
                        clusters.Add(new List<LongRecord> { r });
                }

                foreach (var cluster in clusters)
                {
                    try
                    {
                        // представитель — крайний массив на стороне выноса размера
                        LongRecord rep = longitudinalAbove
                            ? cluster.OrderByDescending(r => r.EdgeLevel).First()
                            : cluster.OrderBy(r => r.EdgeLevel).First();

                        XYZ endsMid = (rep.PtEnd0 + rep.PtEnd1) / 2.0;
                        XYZ midProjected = new XYZ(
                            endsMid.X, endsMid.Y, view.Origin.Z);
                        double outward = longitudinalAbove ? 1.0 : -1.0;
                        XYZ dimLineMid = midProjected
                            + rep.DimDir * longitudinalOffset * outward;

                        Line dimLine = Line.CreateBound(
                            new XYZ((dimLineMid - rep.RebarDir * 2.0).X,
                                    (dimLineMid - rep.RebarDir * 2.0).Y, view.Origin.Z),
                            new XYZ((dimLineMid + rep.RebarDir * 2.0).X,
                                    (dimLineMid + rep.RebarDir * 2.0).Y, view.Origin.Z));

                        var chainRa = new ReferenceArray();
                        if (rep.Dist0Mm < 1.0)
                        {
                            chainRa.Append(rep.GridRef);
                            chainRa.Append(rep.End1Ref);
                        }
                        else if (rep.Dist1Mm < 1.0)
                        {
                            chainRa.Append(rep.GridRef);
                            chainRa.Append(rep.End0Ref);
                        }
                        else
                        {
                            chainRa.Append(rep.GridRef);
                            chainRa.Append(rep.End0Ref);
                            chainRa.Append(rep.End1Ref);
                        }

                        if (rebarDimType != null)
                            doc.Create.NewDimension(view, dimLine, chainRa, rebarDimType);
                        else
                            doc.Create.NewDimension(view, dimLine, chainRa);

                        createdDims++;
                    }
                    catch (Exception ex)
                    {
                        skipReasons.AppendLine($"Продол. размер: {ex.Message}");
                    }
                }
            }

            t.Commit();
        }

        // Итоговое сообщение
        TaskDialog.Show("Готово",
            $"Аннотаций: {annotationCount}\n" +
            $"Продольных размеров до оси: {createdDims} из {totalRebars}\n" +
            $"Поперечных размеров до оси: {createdTransDims}" +
            (skipReasons.Length > 0 ? $"\n\nДиагностика:\n{skipReasons}" : ""));

        return Result.Succeeded;
    }

    // Данные одного массива для дедупликации продольных размеров
    private class LongRecord
    {
        public ElementId GridId;
        public Reference GridRef;
        public Reference End0Ref;
        public Reference End1Ref;
        public XYZ PtEnd0;
        public XYZ PtEnd1;
        public double Dist0Mm;
        public double Dist1Mm;
        public double End0Proj;   // положение торца вдоль rebarDir (для подписи)
        public double End1Proj;
        public double EdgeLevel;  // уровень крайнего стержня вдоль dimDir
        public double Lo;         // габарит массива вдоль dimDir (для кластеризации)
        public double Hi;
        public XYZ DimDir;
        public XYZ RebarDir;
        public double Spacing;
    }

    // Подпись продольного размера: ось + положения торцов вдоль стержня (округл. до 5 мм).
    // Одинаковая подпись = одинаковая длина и привязка к оси.
    private static string LongKey(LongRecord r)
    {
        double aMm = UnitUtils.ConvertFromInternalUnits(
            Math.Min(r.End0Proj, r.End1Proj), UnitTypeId.Millimeters);
        double bMm = UnitUtils.ConvertFromInternalUnits(
            Math.Max(r.End0Proj, r.End1Proj), UnitTypeId.Millimeters);
        long a = (long)Math.Round(aMm / 5.0);
        long b = (long)Math.Round(bMm / 5.0);
        return r.GridId.ToString() + "_" + a + "_" + b;
    }
}