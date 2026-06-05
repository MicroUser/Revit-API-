using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using System;
using System.Collections.Generic;
using System.Linq;

public class WallHostSelectionFilter : ISelectionFilter
{
    public bool AllowElement(Element elem) => elem is Wall;
    public bool AllowReference(Reference reference, XYZ position) => false;
}

[Transaction(TransactionMode.Manual)]
public class WallRebarAnnotation : IExternalCommand
{
    // Имя формы (RebarShape) у П-шек в торцах стены.
    private const string P_SHAPE_NAME = "(форма)П-шка равносторонний";

    public Result Execute(
        ExternalCommandData commandData,
        ref string message,
        ElementSet elements)
    {
        UIDocument uidoc = commandData.Application.ActiveUIDocument;
        Document doc = uidoc.Document;
        View view = doc.ActiveView;

        // ── ШАГ 1: выбор стены ───────────────────────────────────────────────
        Reference wref;
        try
        {
            wref = uidoc.Selection.PickObject(
                ObjectType.Element,
                new WallHostSelectionFilter(),
                "Выберите стену");
        }
        catch (Autodesk.Revit.Exceptions.OperationCanceledException)
        {
            return Result.Cancelled;
        }

        Wall wall = doc.GetElement(wref.ElementId) as Wall;
        if (wall == null)
        {
            message = "Выбранный элемент не является стеной.";
            return Result.Failed;
        }

        var log = new System.Text.StringBuilder();

        // Вся арматура, хостящаяся в этой стене
        var hosted = new FilteredElementCollector(doc)
            .OfClass(typeof(Rebar))
            .Cast<Rebar>()
            .Where(r => r.GetHostId() == wall.Id)
            .ToList();

        // Горизонтальный рабочий набор (на него «3000» и «50»):
        // ориентация «горизонт», незамкнутый, с наибольшим числом стержней.
        Rebar rebar1 = hosted
            .Where(r =>
            {
                string o = ClassifyOrient(r, view, out bool closed, out double _);
                return o == "Гориз" && !closed;
            })
            .OrderByDescending(r => r.NumberOfBarPositions)
            .FirstOrDefault();

        if (rebar1 == null)
        {
            message = "В стене не найдено горизонтальной рабочей арматуры.";
            return Result.Failed;
        }

        // Правая П-шка (на неё марка): по имени формы и по стороне (правее центра стены).
        double wallCenterR = WallCenterAlong(wall, view, view.RightDirection);
        Rebar rebar2 = hosted
            .Where(r => ShapeName(doc, r) == P_SHAPE_NAME)
            .Select(r =>
            {
                ClassifyOrient(r, view, out bool _, out double cR);
                return new { Rebar = r, CenterR = cR };
            })
            .Where(x => x.CenterR >= wallCenterR)
            .OrderByDescending(x => x.CenterR)
            .Select(x => x.Rebar)
            .FirstOrDefault();

        if (rebar2 == null)
            log.AppendLine($"Правая П-шка по форме «{P_SHAPE_NAME}» не найдена — марка не создаётся.");

        // ── ШАГ 2 (АВТО): точка размещения аннотации — 800 мм от края стены ──
        // Раньше тут был PickPoint; теперь положение линии аннотаций вычисляется
        // автоматически: вбок (вдоль стержней) на 800 мм от грани стены.
        const double ANNOTATION_OFFSET_MM = 800.0;
        XYZ annotationPoint = ComputeAutoAnnotationPoint(
            doc, view, rebar1, ANNOTATION_OFFSET_MM, log);
        if (annotationPoint == null)
        {
            message = "Не удалось вычислить точку аннотации " +
                      "(нет стены-хоста или геометрии стержня)." +
                      (log.Length > 0 ? "\n" + log : "");
            return Result.Failed;
        }

        // ── ШАГ 2: стержень для марки выбран автоматически (правая П-шка, см. выше) ──

        // ── КЭШ ТИПОВ АННОТАЦИЙ ───────────────────────────────────────────────
        string typeBigName = "шаг_количество_длина/поз.(_)";
        string typeSmallName = "шаг_количество_длина/поз.(_)";

        Dictionary<string, MultiReferenceAnnotationType> typeCache =
            new FilteredElementCollector(doc)
                .OfClass(typeof(MultiReferenceAnnotationType))
                .Cast<MultiReferenceAnnotationType>()
                .Where(x => x.Name == typeBigName || x.Name == typeSmallName)
                .ToDictionary(x => x.Name, x => x);

        // ── ТИП РАЗМЕРА ───────────────────────────────────────────────────────
        DimensionType rebarDimType = new FilteredElementCollector(doc)
            .OfClass(typeof(DimensionType))
            .Cast<DimensionType>()
            .FirstOrDefault(x => x.Name == "BI_основной_2,5мм_округление_до_5мм");

        using (Transaction t = new Transaction(doc, "Аннотации арматуры"))
        {
            t.Start();

            // REBAR 1 → MultiReferenceAnnotation
            XYZ dimDir1 = null;
            XYZ tagHead1 = null;
            CreateMultiReferenceAnnotation(doc, view, rebar1,
                typeCache, typeBigName, typeSmallName, annotationPoint, log,
                out dimDir1, out tagHead1);

            // REBAR 1 → размеры защитного слоя по краям (торец стены → крайний стержень)
            CreateCoverDimensions(doc, view, rebar1, annotationPoint, rebarDimType, log);

            // REBAR 2 → IndependentTag без выноски (если найдена правая П-шка)
            if (rebar2 != null)
                CreateCategoryTag(doc, view, rebar2, tagHead1, log);

            t.Commit();
        }

        string diagInfo = log.Length > 0 ? $"\n\nДиагностика:\n{log}" : string.Empty;
        string rebar2Info = rebar2 != null ? rebar2.Id.IntegerValue.ToString() : "не найдена";

        TaskDialog.Show("Готово",
            $"Горизонтальная рабочая: стержень {rebar1.Id.IntegerValue}\n" +
            $"Правая П-шка (марка): {rebar2Info}" +
            diagInfo);

        return Result.Succeeded;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // MultiReferenceAnnotation
    // ─────────────────────────────────────────────────────────────────────────
    private static void CreateMultiReferenceAnnotation(
        Document doc, View view, Rebar rebar,
        Dictionary<string, MultiReferenceAnnotationType> typeCache,
        string typeBigName, string typeSmallName,
        XYZ annotationPoint,
        System.Text.StringBuilder log,
        out XYZ outDimDir,
        out XYZ outTagHeadPosition)
    {
        outDimDir = null;
        outTagHeadPosition = null;
        // Геометрия первого стержня в наборе (позиция 0)
        IList<Curve> curves = rebar.GetCenterlineCurves(
            false, false, false, MultiplanarOption.IncludeOnlyPlanarCurves, 0);

        if (curves == null || curves.Count == 0)
        {
            log.AppendLine($"Rebar {rebar.Id}: нет кривых центральной оси.");
            return;
        }

        GetRebarDirAndMid(curves, out XYZ rebarDir, out XYZ firstBarMid);

        XYZ dimDir = SafeCrossProduct(rebarDir, view.ViewDirection);
        if (dimDir == null)
        {
            log.AppendLine($"Rebar {rebar.Id}: стержень перпендикулярен плоскости вида.");
            return;
        }
        outDimDir = dimDir;

        // ── Шаг и количество ─────────────────────────────────────────────────
        int count = rebar.NumberOfBarPositions;
        double spacing = 0;
        Parameter sp = rebar.get_Parameter(BuiltInParameter.REBAR_ELEM_BAR_SPACING);
        if (sp != null && sp.HasValue) spacing = sp.AsDouble(); // в футах

        double spacingMm = UnitUtils.ConvertFromInternalUnits(spacing, UnitTypeId.Millimeters);
        double totalSpanMm = (count > 1 && spacing > 0) ? (count - 1) * spacingMm : 0;
        double halfSpanMm = totalSpanMm / 2.0;
        double halfSpanFt = UnitUtils.ConvertToInternalUnits(halfSpanMm, UnitTypeId.Millimeters);

        // ── Центр массива: первый стержень + dimDir * halfSpan ────────────────
        // dimDir направлен в сторону распределения стержней в массиве,
        // поэтому сдвигаем от первого стержня на половину суммарного шага.
        XYZ arrayCenter = firstBarMid + dimDir * halfSpanFt;

        // Единственный тип аннотации
        MultiReferenceAnnotationType typeToUse = null;
        typeCache.TryGetValue(typeBigName, out typeToUse);

        if (typeToUse == null)
        {
            log.AppendLine($"Rebar {rebar.Id}: тип MultiReferenceAnnotation не найден.");
            return;
        }

        var options = new MultiReferenceAnnotationOptions(typeToUse);
        options.SetElementsToDimension(new List<ElementId> { rebar.Id });
        options.DimensionPlaneNormal = view.ViewDirection;
        options.DimensionLineDirection = dimDir;

        // DimensionLineOrigin — точка, указанная пользователем (где лежит линия размера).
        XYZ origin = ProjectOntoViewPlane(annotationPoint, view);
        options.DimensionLineOrigin = origin;

        // TagHeadPosition — геометрический центр массива, вычисленный через шаг и количество.
        // Проецируем arrayCenter на линию размера (сохраняем компонент вдоль rebarDir от origin,
        // но берём компонент вдоль dimDir от arrayCenter).
        double arrayCenterOnDimDir = arrayCenter.DotProduct(dimDir);
        double originOnDimDir = origin.DotProduct(dimDir);
        XYZ tagHead = origin + dimDir * (arrayCenterOnDimDir - originOnDimDir);
        options.TagHeadPosition = tagHead;
        outTagHeadPosition = tagHead;

        try
        {
            MultiReferenceAnnotation.Create(doc, view.Id, options);
        }
        catch (Exception ex)
        {
            log.AppendLine($"Rebar {rebar.Id}: ошибка создания аннотации — {ex.Message}");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // IndependentTag без выноски (марка по категории)
    // ─────────────────────────────────────────────────────────────────────────
    private static void CreateCategoryTag(
        Document doc, View view, Rebar rebar,
        XYZ tagHead1,
        System.Text.StringBuilder log)
    {
        FamilySymbol tagSymbol = new FilteredElementCollector(doc)
            .OfClass(typeof(FamilySymbol))
            .OfCategory(BuiltInCategory.OST_RebarTags)
            .Cast<FamilySymbol>()
            .FirstOrDefault(fs =>
                fs.Name.Equals("Позиция_(_)_без полки", StringComparison.OrdinalIgnoreCase));

        if (tagSymbol == null)
        {
            log.AppendLine($"Rebar {rebar.Id}: не найден тип марки «Позиция_(_)_без полки» " +
                           "в категории OST_RebarTags.");
            return;
        }

        if (!tagSymbol.IsActive)
            tagSymbol.Activate();

        // Ссылка для марки — из Subelement стержня (без ручного выбора).
        Reference tagRef = GetTagReference(rebar);
        if (tagRef == null)
        {
            log.AppendLine($"Rebar {rebar.Id}: не удалось получить ссылку для марки.");
            return;
        }

        double offsetUpFt = UnitUtils.ConvertToInternalUnits(350, UnitTypeId.Millimeters);
        double offsetRightFt = UnitUtils.ConvertToInternalUnits(225, UnitTypeId.Millimeters);
        XYZ basePos = tagHead1 ?? (rebar.get_BoundingBox(view) is BoundingBoxXYZ bb2
            ? (bb2.Min + bb2.Max) / 2.0 : XYZ.Zero);
        XYZ tagPos = basePos + view.UpDirection * offsetUpFt + view.RightDirection * offsetRightFt;

        try
        {
            IndependentTag tag = IndependentTag.Create(
                doc,
                tagSymbol.Id,
                view.Id,
                tagRef,
                false,                  // hasLeader = false → БЕЗ ВЫНОСКИ
                TagOrientation.Vertical, // вертикальное расположение текста
                tagPos);

            tag.HasLeader = false;
        }
        catch (Exception ex)
        {
            log.AppendLine($"Rebar {rebar.Id}: ошибка создания марки — {ex.Message}");
        }
    }

    // Ссылка для марки: Revit 2023+ тегирует отдельный стержень (Subelement),
    // а не весь набор и не геометрическую ссылку осевой.
    private static Reference GetTagReference(Rebar rebar)
    {
        IList<Subelement> subs = rebar.GetSubelements();
        if (subs != null && subs.Count > 0)
            return subs[0].GetReference();

        try { return new Reference(rebar); }
        catch { return null; }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Размеры защитного слоя: торец стены → крайний стержень (с каждого края)
    // ─────────────────────────────────────────────────────────────────────────
    private struct RefWithCoord
    {
        public Reference Reference;
        public double Coord;
    }

    private static void CreateCoverDimensions(
        Document doc, View view, Rebar rebar,
        XYZ annotationPoint, DimensionType dimType,
        System.Text.StringBuilder log)
    {
        // 1. Направления массива из реальной геометрии (не зависим от ориентации разреза)
        IList<Curve> curves = rebar.GetCenterlineCurves(
            false, false, false, MultiplanarOption.IncludeOnlyPlanarCurves, 0);
        if (curves == null || curves.Count == 0)
        {
            log.AppendLine("Защ. слой: нет кривых оси у rebar1.");
            return;
        }

        GetRebarDirAndMid(curves, out XYZ rebarDir, out XYZ _);

        XYZ dimDir = SafeCrossProduct(rebarDir, view.ViewDirection);
        if (dimDir == null)
        {
            log.AppendLine("Защ. слой: стержень перпендикулярен плоскости вида.");
            return;
        }

        // 2. Хост-стена
        Wall wall = doc.GetElement(rebar.GetHostId()) as Wall;
        if (wall == null)
        {
            log.AppendLine("Защ. слой: у арматуры нет хоста-стены (GetHostId).");
            return;
        }

        // 3. Линейные ссылки крайних стержней (вдоль dimDir)
        var barRefs = GetLineReferencesAlong(rebar, view, rebarDir, dimDir, log, "стержень");
        if (barRefs.Count < 1)
        {
            log.AppendLine("Защ. слой: не получено ни одной ссылки на стержень.");
            return;
        }
        // Ось ПЕРВОГО стержня (позиция 0) — GetCenterlineCurves(...,0) даёт её надёжно.
        double firstAxis = BarPositionCoord(rebar, 0, dimDir);

        // Радиус стержня — чтобы из внешнего ребра поверхности получить ось.
        double barRadius = 0;
        if (doc.GetElement(rebar.GetTypeId()) is RebarBarType barType)
            barRadius = barType.BarModelDiameter / 2.0;

        // Ось ПОСЛЕДНЕГО стержня берём из самой геометрии, не полагаясь на параметр шага
        // (он может не иметь значения для данной раскладки): самая дальняя от первого
        // стержня собранная координата = внешнее ребро последнего стержня; ось = ребро − радиус.
        RefWithCoord farthest = barRefs
            .OrderByDescending(r => Math.Abs(r.Coord - firstAxis))
            .First();
        double sideSign = Math.Sign(farthest.Coord - firstAxis);
        if (sideSign == 0) sideSign = 1;
        double lastAxis = farthest.Coord - sideSign * barRadius;

        RefWithCoord firstBar = ClosestRef(barRefs, firstAxis);
        RefWithCoord lastBar = ClosestRef(barRefs, lastAxis);

        // 4. Ссылки граней-торцов стены (нормаль вдоль dimDir).
        //    Для стены нужны ИМЕННО грани солида, а не осевые линии (их у стены нет) —
        //    поэтому отдельный сборщик по PlanarFace, а не GetLineReferencesAlong.
        var wallRefs = GetWallFaceReferencesAlong(wall, view, dimDir, log, "стена");
        if (wallRefs.Count < 2)
        {
            log.AppendLine($"Защ. слой: граней стены вдоль dimDir найдено {wallRefs.Count} (нужно ≥2).");
            return;
        }
        wallRefs.Sort((a, b) => a.Coord.CompareTo(b.Coord));
        RefWithCoord wallLow = wallRefs.First();  // торец со стороны первого стержня
        RefWithCoord wallHigh = wallRefs.Last();  // торец со стороны последнего стержня

        // 5. Базовая точка линии размера — на оффсете аннотации.
        //    Линия «50» остаётся НА ТОЙ ЖЕ ОСИ, что и основной размер «3000».
        XYZ basePt = ProjectOntoViewPlane(annotationPoint, view);
        double basePtOnDim = basePt.DotProduct(dimDir);

        // Надпись «50» смещаем по диагонали в координатах ВИДА (однозначные «вверх/вправо»):
        // нижний размер → вверх-вправо, верхний → вниз-вправо.
        const double TEXT_RIGHT_MM = 350.0; // по горизонтали вида (RightDirection)
        const double TEXT_UP_MM = 350.0;    // по вертикали вида (UpDirection)

        double rightFt = UnitUtils.ConvertToInternalUnits(TEXT_RIGHT_MM, UnitTypeId.Millimeters);
        double upFt = UnitUtils.ConvertToInternalUnits(TEXT_UP_MM, UnitTypeId.Millimeters);

        XYZ up = view.UpDirection;
        XYZ right = view.RightDirection;
        XYZ diagUR = up * upFt + right * rightFt;  // вверх-вправо (для нижнего)
        XYZ diagDR = -up * upFt + right * rightFt; // вниз-вправо (для верхнего)

        // Точки на линии размера у торцов стены
        XYZ anchorLow = basePt + dimDir * (wallLow.Coord - basePtOnDim);
        XYZ anchorHigh = basePt + dimDir * (wallHigh.Coord - basePtOnDim);

        // Нижний на экране → вверх-вправо, верхний на экране → вниз-вправо.
        XYZ textPosLow, textPosHigh;
        if (anchorLow.DotProduct(up) <= anchorHigh.DotProduct(up))
        {
            textPosLow = anchorLow + diagUR;   // нижний → вверх-вправо
            textPosHigh = anchorHigh + diagDR; // верхний → вниз-вправо
        }
        else
        {
            textPosLow = anchorLow + diagDR;
            textPosHigh = anchorHigh + diagUR;
        }

        // 6. Два размера защитного слоя (линия на оси, текст смещён по диагонали)
        CreateOneCoverDim(doc, view, dimDir, basePt,
            wallLow.Reference, wallLow.Coord, firstBar.Reference, firstBar.Coord,
            dimType, log, "нижний", textPosLow);

        CreateOneCoverDim(doc, view, dimDir, basePt,
            lastBar.Reference, lastBar.Coord, wallHigh.Reference, wallHigh.Coord,
            dimType, log, "верхний", textPosHigh);
    }

    // Сбор линейных ссылок, идущих вдоль rebarDir (⟂ dimDir).
    // Ключ: ComputeReferences + IncludeNonVisibleObjects одновременно — иначе Reference == null.
    private static List<RefWithCoord> GetLineReferencesAlong(
        Element elem, View view, XYZ rebarDir, XYZ dimDir,
        System.Text.StringBuilder log, string tag)
    {
        var result = new List<RefWithCoord>();

        Options opt = new Options
        {
            View = view,
            ComputeReferences = true,
            IncludeNonVisibleObjects = true
        };

        GeometryElement geom = elem.get_Geometry(opt);
        if (geom == null)
        {
            log.AppendLine($"Защ. слой [{tag}]: get_Geometry вернул null.");
            return result;
        }

        CollectParallelLineRefs(geom, rebarDir, dimDir, result);
        return result;
    }

    // Координата оси стержня указанной позиции вдоль dimDir.
    private static double BarPositionCoord(Rebar rebar, int index, XYZ dimDir)
    {
        IList<Curve> c = rebar.GetCenterlineCurves(
            false, false, false, MultiplanarOption.IncludeOnlyPlanarCurves, index);
        XYZ mid = GetCurvesMidPoint(c);
        return mid.DotProduct(dimDir);
    }

    // Ссылка из списка, ближайшая по координате к целевой (к оси стержня).
    private static RefWithCoord ClosestRef(List<RefWithCoord> refs, double targetCoord)
    {
        RefWithCoord best = refs[0];
        double bestDist = Math.Abs(best.Coord - targetCoord);
        foreach (var r in refs)
        {
            double d = Math.Abs(r.Coord - targetCoord);
            if (d < bestDist) { bestDist = d; best = r; }
        }
        return best;
    }

    private static void CollectParallelLineRefs(
        GeometryElement geom, XYZ rebarDir, XYZ dimDir, List<RefWithCoord> acc)
    {
        foreach (GeometryObject obj in geom)
        {
            if (obj is GeometryInstance gi)
            {
                // GetInstanceGeometry() уже в координатах модели
                CollectParallelLineRefs(gi.GetInstanceGeometry(), rebarDir, dimDir, acc);
                continue;
            }

            Line ln = obj as Line;
            if (ln == null || ln.Reference == null) continue;

            XYZ dir = ln.Direction.Normalize();
            if (Math.Abs(dir.DotProduct(dimDir)) > 1e-3) continue;    // не ⟂ dimDir → пропуск
            if (Math.Abs(dir.DotProduct(rebarDir)) < 0.99) continue;  // не вдоль стержня → пропуск

            double coord = ln.GetEndPoint(0).DotProduct(dimDir);
            acc.Add(new RefWithCoord { Reference = ln.Reference, Coord = coord });
        }
    }

    // Сбор ссылок плоских граней стены, нормаль которых направлена вдоль dimDir.
    // Грань-торец стены в разрезе видна с ребра, её Reference (SURFACE) корректно
    // образмеривается совместно с линейной ссылкой стержня.
    private static List<RefWithCoord> GetWallFaceReferencesAlong(
        Element elem, View view, XYZ dimDir,
        System.Text.StringBuilder log, string tag)
    {
        var result = new List<RefWithCoord>();

        Options opt = new Options
        {
            View = view,
            ComputeReferences = true,
            IncludeNonVisibleObjects = false
        };

        GeometryElement geom = elem.get_Geometry(opt);
        if (geom == null)
        {
            log.AppendLine($"Защ. слой [{tag}]: get_Geometry вернул null.");
            return result;
        }

        CollectFacesAlong(geom, dimDir, result);

        if (result.Count == 0)
            log.AppendLine($"Защ. слой [{tag}]: плоских граней с нормалью вдоль dimDir не найдено.");

        return result;
    }

    private static void CollectFacesAlong(
        GeometryElement geom, XYZ dimDir, List<RefWithCoord> acc)
    {
        foreach (GeometryObject obj in geom)
        {
            if (obj is GeometryInstance gi)
            {
                CollectFacesAlong(gi.GetInstanceGeometry(), dimDir, acc);
                continue;
            }

            Solid solid = obj as Solid;
            if (solid == null || solid.Faces.Size == 0) continue;

            foreach (Face f in solid.Faces)
            {
                PlanarFace pf = f as PlanarFace;
                if (pf == null || pf.Reference == null) continue;

                XYZ n = pf.FaceNormal.Normalize();
                if (Math.Abs(n.DotProduct(dimDir)) < 0.99) continue; // нормаль не вдоль dimDir

                double coord = pf.Origin.DotProduct(dimDir);
                acc.Add(new RefWithCoord { Reference = pf.Reference, Coord = coord });
            }
        }
    }

    private static void CreateOneCoverDim(
        Document doc, View view, XYZ dimDir, XYZ basePt,
        Reference rLow, double coordLow,
        Reference rHigh, double coordHigh,
        DimensionType dimType, System.Text.StringBuilder log, string tag,
        XYZ textPos)
    {
        double baseOnDim = basePt.DotProduct(dimDir);
        double margin = UnitUtils.ConvertToInternalUnits(20, UnitTypeId.Millimeters);

        // Линия размера вдоль dimDir на оффсете basePt, с запасом по краям.
        // Reference'ы Revit спроецирует на неё сам.
        XYZ p1 = basePt + dimDir * (coordLow - baseOnDim - margin);
        XYZ p2 = basePt + dimDir * (coordHigh - baseOnDim + margin);

        if ((p2 - p1).GetLength() < 1e-6)
        {
            log.AppendLine($"Защ. слой [{tag}]: нулевая длина линии размера.");
            return;
        }

        Line dimLine;
        try { dimLine = Line.CreateBound(p1, p2); }
        catch (Exception ex)
        {
            log.AppendLine($"Защ. слой [{tag}]: линия размера — {ex.Message}");
            return;
        }

        var ra = new ReferenceArray();
        ra.Append(rLow);
        ra.Append(rHigh);

        try
        {
            Dimension dim = dimType != null
                ? doc.Create.NewDimension(view, dimLine, ra, dimType)
                : doc.Create.NewDimension(view, dimLine, ra);

            if (dim == null)
            {
                log.AppendLine($"Защ. слой [{tag}]: NewDimension вернул null.");
                return;
            }

            // Смещаем надпись «50» — линия размера остаётся на оси,
            // Revit рисует полку-выноску к смещённому тексту.
            if (textPos != null)
            {
                try
                {
                    doc.Regenerate();          // позиция текста доступна только после регенерации
                    dim.HasLeader = true;
                    dim.TextPosition = textPos;
                }
                catch (Exception exT)
                {
                    log.AppendLine($"Защ. слой [{tag}]: не удалось сместить текст — {exT.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            log.AppendLine($"Защ. слой [{tag}]: ОШИБКА NewDimension — {ex.Message}");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Размер от крайних стержней rebar1 до границ стены-хоста (цепочка, не используется)
    // ─────────────────────────────────────────────────────────────────────────
    private static void CreateWallBoundaryDimension(
        Document doc, View view, Rebar rebar,
        XYZ annotationPoint, XYZ dimDir,
        Reference refWallBottom, Reference refWallTop,
        Reference refRebarFirst, Reference refRebarLast,
        DimensionType dimType,
        System.Text.StringBuilder log)
    {
        // Геометрия первого стержня для определения направления и точки линии размера
        IList<Curve> firstCurves = rebar.GetCenterlineCurves(
            false, false, false, MultiplanarOption.IncludeOnlyPlanarCurves, 0);

        if (firstCurves == null || firstCurves.Count == 0)
        {
            log.AppendLine($"Rebar {rebar.Id}: нет кривых центральной оси.");
            return;
        }

        GetRebarDirAndMid(firstCurves, out XYZ rebarDir, out XYZ firstMid);

        // Линия размера — на той же горизонтальной позиции что и аннотация
        XYZ ap = ProjectOntoViewPlane(annotationPoint, view);
        double pos = ap.DotProduct(dimDir);
        XYZ dimMid = firstMid + dimDir * (pos - firstMid.DotProduct(dimDir));

        Line dimLine = TryCreateDimLine(dimMid, rebarDir, view.Origin.Z);
        if (dimLine == null)
        {
            log.AppendLine($"Rebar {rebar.Id}: не удалось создать линию размера.");
            return;
        }

        // Цепочка: низ стены → первый стержень → последний стержень → верх стены
        var ra = new ReferenceArray();
        ra.Append(refWallBottom);
        ra.Append(refRebarFirst);
        ra.Append(refRebarLast);
        ra.Append(refWallTop);

        try
        {
            Dimension dim = dimType != null
                ? doc.Create.NewDimension(view, dimLine, ra, dimType)
                : doc.Create.NewDimension(view, dimLine, ra);

            if (dim == null)
                log.AppendLine($"Rebar {rebar.Id}: NewDimension вернул null.");
        }
        catch (Exception ex)
        {
            log.AppendLine($"Rebar {rebar.Id}: ОШИБКА NewDimension — {ex.Message}");
        }
    }

    // ─── Вспомогательные методы ───────────────────────────────────────────────

    private static void GetRebarDirAndMid(IList<Curve> curves, out XYZ dir, out XYZ mid)
    {
        if (curves.Count == 1 && curves[0] is Line sl)
        {
            dir = (sl.GetEndPoint(1) - sl.GetEndPoint(0)).Normalize();
            mid = (sl.GetEndPoint(0) + sl.GetEndPoint(1)) / 2.0;
            return;
        }

        XYZ chord = curves.Last().GetEndPoint(1) - curves.First().GetEndPoint(0);
        dir = chord.GetLength() > 1e-6
            ? chord.Normalize()
            : curves[0].ComputeDerivatives(0.5, true).BasisX.Normalize();

        var pts = new List<XYZ>();
        foreach (Curve c in curves) pts.AddRange(c.Tessellate());
        mid = new XYZ(
            (pts.Min(p => p.X) + pts.Max(p => p.X)) / 2.0,
            (pts.Min(p => p.Y) + pts.Max(p => p.Y)) / 2.0,
            (pts.Min(p => p.Z) + pts.Max(p => p.Z)) / 2.0);
    }

    private static XYZ GetCurvesMidPoint(IList<Curve> curves)
    {
        if (curves.Count == 1 && curves[0] is Line line)
            return (line.GetEndPoint(0) + line.GetEndPoint(1)) / 2.0;

        var pts = new List<XYZ>();
        foreach (Curve c in curves) pts.AddRange(c.Tessellate());
        return new XYZ(
            (pts.Min(p => p.X) + pts.Max(p => p.X)) / 2.0,
            (pts.Min(p => p.Y) + pts.Max(p => p.Y)) / 2.0,
            (pts.Min(p => p.Z) + pts.Max(p => p.Z)) / 2.0);
    }

    private static Line TryCreateDimLine(XYZ mid, XYZ dir, double z)
    {
        if (dir.GetLength() < 1e-9) return null;
        XYZ d = dir.Normalize();
        double half = 2.0; // 2 фута — заведомо достаточно
        XYZ p1 = new XYZ((mid - d * half).X, (mid - d * half).Y, z);
        XYZ p2 = new XYZ((mid + d * half).X, (mid + d * half).Y, z);
        if ((p2 - p1).GetLength() < 0.1) return null;
        try { return Line.CreateBound(p1, p2); }
        catch { return null; }
    }

    private static XYZ SafeCrossProduct(XYZ a, XYZ b)
    {
        XYZ cross = a.CrossProduct(b);
        return cross.GetLength() > 1e-6 ? cross.Normalize() : null;
    }

    // ─── Классификация наборов арматуры для авто-выбора ───────────────────────

    // Ориентация набора по габаритам осевой (позиция 0) вдоль осей вида:
    // "Гориз" / "Верт" / "Поперёк" (вдоль толщины) / "Гнутый" (П, хомут).
    private static string ClassifyOrient(Rebar rb, View view, out bool closed, out double centerR)
    {
        XYZ R = view.RightDirection, U = view.UpDirection, N = view.ViewDirection;

        IList<Curve> cs = rb.GetCenterlineCurves(
            false, false, false, MultiplanarOption.IncludeOnlyPlanarCurves, 0);

        var pts = new List<XYZ>();
        if (cs != null) foreach (Curve c in cs) pts.AddRange(c.Tessellate());

        closed = pts.Count > 1 && pts.First().DistanceTo(pts.Last()) < 1e-3;
        centerR = pts.Count > 0 ? pts.Average(p => p.DotProduct(R)) : 0;

        double dR = Extent(pts, R);
        double dU = Extent(pts, U);
        double dN = Extent(pts, N);

        if (dN > Math.Max(dR, dU)) return "Поперёк"; // вдоль толщины (шпилька/часть хомутов)
        if (dR >= dU * 2.0) return "Гориз";
        if (dU >= dR * 2.0) return "Верт";
        return "Гнутый";                              // П / хомут в плоскости вида
    }

    private static double Extent(List<XYZ> pts, XYZ dir)
    {
        if (pts == null || pts.Count == 0) return 0;
        double min = double.MaxValue, max = double.MinValue;
        foreach (XYZ p in pts)
        {
            double d = p.DotProduct(dir);
            if (d < min) min = d;
            if (d > max) max = d;
        }
        return max - min;
    }

    // Центр стены вдоль dir (середина габарита видимой геометрии).
    private static double WallCenterAlong(Wall wall, View view, XYZ dir)
    {
        Options opt = new Options { View = view };
        GeometryElement ge = wall.get_Geometry(opt);
        double min = double.MaxValue, max = double.MinValue;
        if (ge != null)
            foreach (GeometryObject o in ge)
                if (o is Solid s)
                    foreach (Edge e in s.Edges)
                    {
                        Curve c = e.AsCurve();
                        if (c == null) continue;
                        foreach (XYZ p in new[] { c.GetEndPoint(0), c.GetEndPoint(1) })
                        {
                            double d = p.DotProduct(dir);
                            if (d < min) min = d;
                            if (d > max) max = d;
                        }
                    }
        return (min == double.MaxValue) ? 0 : (min + max) / 2.0;
    }

    private static string ShapeName(Document doc, Rebar rb)
    {
        RebarShape sh = doc.GetElement(rb.GetShapeId()) as RebarShape;
        return sh?.Name ?? string.Empty;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Авто-точка размещения аннотаций: offsetMm от грани стены, вбок (вдоль стержней),
    // на уровне центра набора. Сторона — в сторону RightDirection вида.
    // ─────────────────────────────────────────────────────────────────────────
    private static XYZ ComputeAutoAnnotationPoint(
        Document doc, View view, Rebar rebar, double offsetMm, System.Text.StringBuilder log)
    {
        IList<Curve> curves = rebar.GetCenterlineCurves(
            false, false, false, MultiplanarOption.IncludeOnlyPlanarCurves, 0);
        if (curves == null || curves.Count == 0)
        {
            log.AppendLine("Авто-точка: нет кривых оси у стержня.");
            return null;
        }

        GetRebarDirAndMid(curves, out XYZ rebarDir, out XYZ _);

        // Сторона размещения — туда, куда смотрит «вправо» на виде.
        XYZ outDir = rebarDir.DotProduct(view.RightDirection) >= 0 ? rebarDir : rebarDir.Negate();

        Wall wall = doc.GetElement(rebar.GetHostId()) as Wall;
        if (wall == null)
        {
            log.AppendLine("Авто-точка: у арматуры нет хоста-стены (GetHostId).");
            return null;
        }

        double wallEdge = WallExtentAlong(wall, view, outDir, log);
        if (double.IsNegativeInfinity(wallEdge))
        {
            log.AppendLine("Авто-точка: не удалось определить край стены.");
            return null;
        }

        double offsetFt = UnitUtils.ConvertToInternalUnits(offsetMm, UnitTypeId.Millimeters);
        double targetOut = wallEdge + offsetFt;

        // Центр набора — для позиционирования вдоль линии размера.
        XYZ setCenter;
        BoundingBoxXYZ bb = rebar.get_BoundingBox(view);
        setCenter = (bb != null)
            ? bb.Transform.OfPoint((bb.Min + bb.Max) / 2.0)
            : GetCurvesMidPoint(curves);

        XYZ pt = setCenter + outDir * (targetOut - setCenter.DotProduct(outDir));
        return ProjectOntoViewPlane(pt, view);
    }

    // Максимальная проекция геометрии стены на dir (= край стены со стороны +dir).
    private static double WallExtentAlong(
        Wall wall, View view, XYZ dir, System.Text.StringBuilder log)
    {
        double maxProj = double.NegativeInfinity;

        Options opt = new Options
        {
            View = view,
            ComputeReferences = false,
            IncludeNonVisibleObjects = false
        };

        GeometryElement geom = wall.get_Geometry(opt);
        if (geom == null)
        {
            log.AppendLine("Авто-точка: get_Geometry стены вернул null.");
            return maxProj;
        }

        foreach (GeometryObject obj in geom)
        {
            Solid s = obj as Solid;
            if (s == null || s.Edges.Size == 0) continue;

            foreach (Edge e in s.Edges)
            {
                Curve c = e.AsCurve();
                if (c == null) continue;
                double p0 = c.GetEndPoint(0).DotProduct(dir);
                double p1 = c.GetEndPoint(1).DotProduct(dir);
                if (p0 > maxProj) maxProj = p0;
                if (p1 > maxProj) maxProj = p1;
            }
        }

        return maxProj;
    }

    /// <summary>
    /// Проецирует точку на плоскость вида, убирая компонент вдоль ViewDirection.
    /// Это нужно чтобы точка, подобранная PickPoint, лежала строго в плоскости вида.
    /// </summary>
    private static XYZ ProjectOntoViewPlane(XYZ point, View view)
    {
        XYZ origin = view.Origin;
        XYZ normal = view.ViewDirection;
        double dist = (point - origin).DotProduct(normal);
        return point - normal * dist;
    }
}