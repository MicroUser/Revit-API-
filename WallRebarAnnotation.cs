using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using System;
using System.Collections.Generic;
using System.Linq;

public class AssemblySelectionFilter : ISelectionFilter
{
    public bool AllowElement(Element elem) => elem is AssemblyInstance;
    public bool AllowReference(Reference reference, XYZ position) => false;
}

[Transaction(TransactionMode.Manual)]
public class WallRebarAnnotation : IExternalCommand
{
    // Имя формы (RebarShape) у П-шек в торцах стены.
    private const string P_SHAPE_NAME = "(форма)П-шка равносторонний";

    // Линия аннотаций — на этом расстоянии от края стены (мм).
    private const double ANNOTATION_OFFSET_MM = 800.0;

    private const string TYPE_BIG_NAME = "шаг_количество_длина/поз.(_)";
    private const string TYPE_SMALL_NAME = "шаг_количество_длина/поз.(_)";
    private const string DIM_TYPE_NAME = "BI_основной_2,5мм_округление_до_5мм";

    public Result Execute(
        ExternalCommandData commandData,
        ref string message,
        ElementSet elements)
    {
        UIDocument uidoc = commandData.Application.ActiveUIDocument;
        Document doc = uidoc.Document;
        View view = doc.ActiveView;

        // ── ШАГ 1: выбор сборки ──────────────────────────────────────────────
        Reference aref;
        try
        {
            aref = uidoc.Selection.PickObject(
                ObjectType.Element,
                new AssemblySelectionFilter(),
                "Выберите сборку");
        }
        catch (Autodesk.Revit.Exceptions.OperationCanceledException)
        {
            return Result.Cancelled;
        }

        AssemblyInstance assembly = doc.GetElement(aref.ElementId) as AssemblyInstance;
        if (assembly == null)
        {
            message = "Выбранный элемент не является сборкой.";
            return Result.Failed;
        }

        // Все стены — члены сборки
        var walls = assembly.GetMemberIds()
            .Select(id => doc.GetElement(id) as Wall)
            .Where(w => w != null)
            .ToList();

        if (walls.Count == 0)
        {
            message = "В сборке не найдено стен.";
            return Result.Failed;
        }

        var log = new System.Text.StringBuilder();

        // ── Выбор стороны аннотаций ──────────────────────────────────────────
        TaskDialog sideDlg = new TaskDialog("Сторона аннотаций")
        {
            MainInstruction = "С какой стороны от стены создавать марки и размеры?",
            CommonButtons = TaskDialogCommonButtons.Cancel
        };
        sideDlg.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Справа от стены");
        sideDlg.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Слева от стены");

        TaskDialogResult sideRes = sideDlg.Show();
        if (sideRes != TaskDialogResult.CommandLink1 && sideRes != TaskDialogResult.CommandLink2)
            return Result.Cancelled;

        bool onRight = sideRes == TaskDialogResult.CommandLink1;
        XYZ sideDir = onRight ? view.RightDirection : view.RightDirection.Negate();

        // ── Кэш типов аннотаций и тип размера (один раз) ─────────────────────
        Dictionary<string, MultiReferenceAnnotationType> typeCache =
            new FilteredElementCollector(doc)
                .OfClass(typeof(MultiReferenceAnnotationType))
                .Cast<MultiReferenceAnnotationType>()
                .Where(x => x.Name == TYPE_BIG_NAME || x.Name == TYPE_SMALL_NAME)
                .ToDictionary(x => x.Name, x => x);

        DimensionType rebarDimType = new FilteredElementCollector(doc)
            .OfClass(typeof(DimensionType))
            .Cast<DimensionType>()
            .FirstOrDefault(x => x.Name == DIM_TYPE_NAME);

        // Арматура документа, сгруппированная по хосту — один проход на всю сборку.
        Dictionary<ElementId, List<Rebar>> rebarByHost =
            new FilteredElementCollector(doc)
                .OfClass(typeof(Rebar))
                .Cast<Rebar>()
                .GroupBy(r => r.GetHostId())
                .ToDictionary(g => g.Key, g => g.ToList());

        using (Transaction t = new Transaction(doc, "Аннотации арматуры (сборка)"))
        {
            t.Start();

            foreach (Wall wall in walls)
            {
                rebarByHost.TryGetValue(wall.Id, out List<Rebar> hosted);
                AnnotateWall(doc, view, wall, hosted, typeCache, rebarDimType, sideDir, log);
            }

            t.Commit();
        }

        return Result.Succeeded;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Обработка одной стены: отбор арматуры + создание размеров и марки.
    // Возвращает true, если что-то было создано.
    // ─────────────────────────────────────────────────────────────────────────
    private static bool AnnotateWall(
        Document doc, View view, Wall wall,
        List<Rebar> hosted,
        Dictionary<string, MultiReferenceAnnotationType> typeCache,
        DimensionType rebarDimType,
        XYZ sideDir,
        System.Text.StringBuilder log)
    {
        string tag = $"Стена {wall.Id.IntegerValue}";

        // Знак стороны: +1 если sideDir совпадает с «вправо» вида, иначе −1.
        double sideSign = sideDir.DotProduct(view.RightDirection) >= 0 ? 1.0 : -1.0;

        if (hosted == null || hosted.Count == 0)
        {
            log.AppendLine($"{tag}: арматуры не найдено — пропуск.");
            return false;
        }

        // Горизонтальный рабочий набор (на него «3000» и «50»):
        // ориентация «горизонт», незамкнутый, с наибольшим числом стержней.
        Rebar rebar1 = hosted
            .Where(r => IsInsideCrop(view, r))
            .Where(r =>
            {
                string o = ClassifyOrient(r, view, out bool closed, out double _);
                return o == "Гориз" && !closed;
            })
            .OrderByDescending(r => r.NumberOfBarPositions)
            .FirstOrDefault();

        // П-шка на ВЫБРАННОЙ стороне (на неё марка): по имени формы, по ориентации
        // (только ГОРИЗОНТАЛЬНЫЕ — полки вдоль горизонтали вида) и по стороне.
        double wallCenterR = WallCenterAlong(wall, view, view.RightDirection);
        Rebar rebar2 = hosted
            .Where(r => IsInsideCrop(view, r))
            .Where(r => ShapeName(doc, r) == P_SHAPE_NAME)
            .Where(r => IsHorizontalPShape(r, view))
            .Select(r =>
            {
                ClassifyOrient(r, view, out bool _, out double cR);
                return new { Rebar = r, CenterR = cR };
            })
            // на выбранной стороне: (centerR - center) одного знака с sideSign
            .Where(x => (x.CenterR - wallCenterR) * sideSign >= 0)
            // самый крайний на этой стороне
            .OrderByDescending(x => (x.CenterR - wallCenterR) * sideSign)
            .Select(x => x.Rebar)
            .FirstOrDefault();

        if (rebar2 == null)
            log.AppendLine($"{tag}: П-шка по форме «{P_SHAPE_NAME}» на выбранной стороне не найдена.");

        if (rebar1 == null && rebar2 == null)
        {
            log.AppendLine($"{tag}: ни горизонтальной рабочей, ни П-шки — пропуск.");
            return false;
        }

        XYZ tagHead1 = null;

        if (rebar1 != null)
        {
            XYZ annotationPoint = ComputeAutoAnnotationPoint(
                doc, view, rebar1, ANNOTATION_OFFSET_MM, sideDir, log);

            if (annotationPoint == null)
            {
                log.AppendLine($"{tag}: не удалось вычислить точку аннотации.");
            }
            else
            {
                XYZ dimDir1 = null;
                CreateMultiReferenceAnnotation(doc, view, rebar1,
                    typeCache, TYPE_BIG_NAME, TYPE_SMALL_NAME, annotationPoint, log,
                    out dimDir1, out tagHead1);

                CreateCoverDimensions(doc, view, rebar1, annotationPoint, rebarDimType, sideDir, log);
            }
        }
        else if (rebar2 != null)
        {
            // Горизонтального нет → привязочный размер от центра П-шки
            // по вертикали к верхней/нижней грани стены.
            CreatePShapeAnchorDimensions(doc, view, rebar2, rebarDimType, sideDir, log);
        }

        // Марка (если найдена П-шка на выбранной стороне)
        if (rebar2 != null)
            CreateCategoryTag(doc, view, rebar2, tagHead1, log);

        log.AppendLine($"{tag}: рабочая={(rebar1 != null ? rebar1.Id.IntegerValue.ToString() : "нет")}, " +
                       $"П-шка={(rebar2 != null ? rebar2.Id.IntegerValue.ToString() : "нет")}.");
        return true;
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

        // ── Центр массива — по габариту набора в виде (надёжная середина,
        // не зависит от знака dimDir). При необходимости опускаем марку ниже.
        XYZ arrayCenter;
        BoundingBoxXYZ rbb = rebar.get_BoundingBox(view);
        arrayCenter = (rbb != null)
            ? rbb.Transform.OfPoint((rbb.Min + rbb.Max) / 2.0)
            : firstBarMid + dimDir * halfSpanFt;

        // Опускание марки MRA ниже центра набора (вдоль вертикали вида), мм.
        // Марка П-шки привязана к этой же точке (tagHead1) и опустится вместе с ней.
        const double MRA_MARK_DOWN_MM = 150;
        double mraDownFt = UnitUtils.ConvertToInternalUnits(MRA_MARK_DOWN_MM, UnitTypeId.Millimeters);
        arrayCenter = arrayCenter - view.UpDirection * mraDownFt;

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

        // Вертикальный сдвиг марки относительно центра (мм): + вверх / − вниз.
        // 0 = ровно по центру набора, рядом с маркой MRA.
        const double TAG_VERTICAL_MM = 350;
        double offsetUpFt = UnitUtils.ConvertToInternalUnits(TAG_VERTICAL_MM, UnitTypeId.Millimeters);
        double offsetSideFt = UnitUtils.ConvertToInternalUnits(250, UnitTypeId.Millimeters);
        XYZ basePos = tagHead1 ?? (rebar.get_BoundingBox(view) is BoundingBoxXYZ bb2
            ? (bb2.Min + bb2.Max) / 2.0 : XYZ.Zero);
        // Марку всегда сдвигаем к подписи аннотации (фиксированно вправо по виду),
        // независимо от выбранной стороны — иначе слева она уезжает наружу.
        XYZ tagPos = basePos + view.UpDirection * offsetUpFt + view.RightDirection * offsetSideFt;

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
    // Привязка П-шки к стене (когда нет горизонтального набора):
    // вертикальные размеры от верхней/нижней грани стены к полкам П-шки.
    // ─────────────────────────────────────────────────────────────────────────
    private static void CreatePShapeAnchorDimensions(
        Document doc, View view, Rebar pRebar,
        DimensionType dimType, XYZ sideDir, System.Text.StringBuilder log)
    {
        XYZ dimDir = view.UpDirection;     // меряем по вертикали
        XYZ legDir = view.RightDirection;  // полки П идут горизонтально

        Wall wall = doc.GetElement(pRebar.GetHostId()) as Wall;
        if (wall == null)
        {
            log.AppendLine("П-привязка: у П-шки нет хоста-стены.");
            return;
        }

        // Линейные ссылки полок П (горизонтальные линии, ⟂ dimDir)
        var legRefs = GetLineReferencesAlong(pRebar, view, legDir, dimDir, log, "П-полки");
        if (legRefs.Count < 1)
        {
            log.AppendLine("П-привязка: не найдено горизонтальных линий полок П.");
            return;
        }
        legRefs.Sort((a, b) => a.Coord.CompareTo(b.Coord));
        RefWithCoord lowLeg = legRefs.First();   // нижняя полка
        RefWithCoord highLeg = legRefs.Last();   // верхняя полка

        // Грани стены по вертикали (верх/низ)
        var wallRefs = GetWallFaceReferencesAlong(wall, view, dimDir, log, "стена(верх/низ)");
        if (wallRefs.Count < 2)
        {
            log.AppendLine($"П-привязка: граней стены по вертикали найдено {wallRefs.Count} (нужно ≥2).");
            return;
        }
        wallRefs.Sort((a, b) => a.Coord.CompareTo(b.Coord));
        RefWithCoord wallLow = wallRefs.First();   // низ
        RefWithCoord wallHigh = wallRefs.Last();   // верх

        // Линию размеров ставим в 800 мм от края стены на ВЫБРАННОЙ стороне.
        XYZ outDir = sideDir.Normalize();
        double offsetFt = UnitUtils.ConvertToInternalUnits(800.0, UnitTypeId.Millimeters);
        double wallEdge = WallExtentAlong(wall, view, outDir, log);
        XYZ pMid = GetCurvesMidPoint(pRebar.GetCenterlineCurves(
            false, false, false, MultiplanarOption.IncludeOnlyPlanarCurves, 0));
        double targetOut = wallEdge + offsetFt;
        XYZ basePt = ProjectOntoViewPlane(
            pMid + outDir * (targetOut - pMid.DotProduct(outDir)), view);

        // Низ стены → нижняя полка; верхняя полка → верх стены.
        CreateOneCoverDim(doc, view, dimDir, basePt,
            wallLow.Reference, wallLow.Coord, lowLeg.Reference, lowLeg.Coord,
            dimType, log, "П-низ", null);

        CreateOneCoverDim(doc, view, dimDir, basePt,
            highLeg.Reference, highLeg.Coord, wallHigh.Reference, wallHigh.Coord,
            dimType, log, "П-верх", null);
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
        XYZ annotationPoint, DimensionType dimType, XYZ sideDir,
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
        XYZ right = sideDir.Normalize();           // горизонталь — на выбранную сторону
        XYZ diagUR = up * upFt + right * rightFt;  // вверх-в сторону (для нижнего)
        XYZ diagDR = -up * upFt + right * rightFt; // вниз-в сторону (для верхнего)

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

        int before = result.Count;
        CollectParallelLineRefs(geom, rebarDir, dimDir, result);
        log.AppendLine($"Защ. слой [{tag}]: собрано ссылок {result.Count - before}.");
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

            // Стержень показан «линией» — осевая приходит верхнеуровневым Line.
            if (obj is Line ln)
            {
                TryAddParallelLineRef(ln, ln.Reference, rebarDir, dimDir, acc);
                continue;
            }

            // Стержень показан «телом» — берём рёбра солида (поверхностные линии).
            if (obj is Solid solid && solid.Edges.Size > 0)
            {
                foreach (Edge e in solid.Edges)
                {
                    if (e.AsCurve() is Line el)
                        TryAddParallelLineRef(el, e.Reference, rebarDir, dimDir, acc);
                }
            }
        }
    }

    // Добавляет ссылку, если линия идёт вдоль rebarDir (⟂ dimDir) и ссылка не null.
    private static void TryAddParallelLineRef(
        Line ln, Reference rf, XYZ rebarDir, XYZ dimDir, List<RefWithCoord> acc)
    {
        if (rf == null) return;
        XYZ dir = ln.Direction.Normalize();
        if (Math.Abs(dir.DotProduct(dimDir)) > 1e-3) return;    // не ⟂ dimDir
        if (Math.Abs(dir.DotProduct(rebarDir)) < 0.99) return;  // не вдоль стержня
        double coord = ln.GetEndPoint(0).DotProduct(dimDir);
        acc.Add(new RefWithCoord { Reference = rf, Coord = coord });
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

    // Горизонтальная ли П-шка: у равносторонней П две полки параллельны, основание
    // перпендикулярно. У горизонтальной П полки идут вдоль RightDirection (их 2 из 3),
    // у вертикальной — вдоль UpDirection. Считаем, каких прямых сегментов больше.
    private static bool IsHorizontalPShape(Rebar rb, View view)
    {
        XYZ R = view.RightDirection, U = view.UpDirection;
        IList<Curve> cs = rb.GetCenterlineCurves(
            false, false, false, MultiplanarOption.IncludeOnlyPlanarCurves, 0);
        if (cs == null || cs.Count == 0) return false;

        int rCount = 0, uCount = 0;
        foreach (Curve c in cs)
        {
            if (!(c is Line)) continue; // только прямые сегменты (полки/основание)
            XYZ d = (c.GetEndPoint(1) - c.GetEndPoint(0)).Normalize();
            if (Math.Abs(d.DotProduct(R)) >= Math.Abs(d.DotProduct(U))) rCount++;
            else uCount++;
        }
        return rCount > uCount; // горизонтальная П: сегментов вдоль горизонтали больше
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

    // Стержень считается «в обрезке», если его габарит целиком попадает в рамку
    // подрезки вида (по осям X/Y вида). Если обрезка не активна — true (берём всё).
    // Если хоть один угол габарита вне рамки — false (часть за обрезкой).
    private static bool IsInsideCrop(View view, Rebar rebar)
    {
        if (!view.CropBoxActive) return true;

        BoundingBoxXYZ crop = view.CropBox;
        if (crop == null) return true;

        BoundingBoxXYZ bb = rebar.get_BoundingBox(view);
        if (bb == null) return true; // не смогли определить — не исключаем

        Transform toCrop = crop.Transform.Inverse;
        double tol = 1e-4; // ~0.03 мм, чтобы не дёргаться на границе

        foreach (XYZ corner in BoxCornersModel(bb))
        {
            XYZ l = toCrop.OfPoint(corner);
            if (l.X < crop.Min.X - tol || l.X > crop.Max.X + tol ||
                l.Y < crop.Min.Y - tol || l.Y > crop.Max.Y + tol)
                return false; // угол за рамкой → часть стержня за обрезкой
        }
        return true;
    }

    // 8 углов BoundingBoxXYZ в модельных координатах.
    private static IEnumerable<XYZ> BoxCornersModel(BoundingBoxXYZ bb)
    {
        Transform tf = bb.Transform;
        XYZ mn = bb.Min, mx = bb.Max;
        for (int i = 0; i < 8; i++)
        {
            XYZ p = new XYZ(
                (i & 1) == 0 ? mn.X : mx.X,
                (i & 2) == 0 ? mn.Y : mx.Y,
                (i & 4) == 0 ? mn.Z : mx.Z);
            yield return tf.OfPoint(p);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Авто-точка размещения аннотаций: offsetMm от грани стены на стороне sideDir,
    // на уровне центра набора.
    // ─────────────────────────────────────────────────────────────────────────
    private static XYZ ComputeAutoAnnotationPoint(
        Document doc, View view, Rebar rebar, double offsetMm, XYZ sideDir,
        System.Text.StringBuilder log)
    {
        IList<Curve> curves = rebar.GetCenterlineCurves(
            false, false, false, MultiplanarOption.IncludeOnlyPlanarCurves, 0);
        if (curves == null || curves.Count == 0)
        {
            log.AppendLine("Авто-точка: нет кривых оси у стержня.");
            return null;
        }

        // Сторона размещения — выбранная пользователем.
        XYZ outDir = sideDir.Normalize();

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