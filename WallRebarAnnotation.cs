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
    // Имена форм (RebarShape) П-шек в торцах стены.
    private static readonly HashSet<string> P_SHAPE_NAMES = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "(форма)21",
        "(форма)П-шка 4",
        "(форма)П-шка А=С",
        "(форма)П-шка А=С 3",
        "(форма)П-шка равносторонний",
    };

    private static readonly HashSet<string> SKIP_SHAPE_NAMES = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "(форма)хомут",
        "(форма)хомут 2",
        "(форма)хомут 4",
        "(форма)хомут 5",
        "(форма)хомут 6",
        "(форма)хомут 7",
        "(форма)хомут 8",
        "(форма)хомут 9",
        "(форма)хомут_обычный 2",
        "(форма)шпилька 2",
        "(форма)шпилька 5",
        "(форма)шпилька_S",
        "(форма)шпилька_S 2",
    };

    // Линия аннотаций — на этом расстоянии от края стены (мм).
    private const double ANNOTATION_OFFSET_MM = 800.0;
    // Отступ размеров у вертикальной арматуры (проекция над плитой, стержни с «В») — общий.
    private const double PROJECTION_DIM_OFFSET_MM = 500.0;

    private const string TYPE_BIG_NAME     = "шаг_количество_длина/поз.(_)";
    private const string TYPE_SMALL_NAME   = "шаг_количество/поз.(_)"; // n*шаг < 1000 мм
    private const string TYPE_STIRRUP_NAME       = "шаг_количество/поз.(_)";
    private const string TYPE_STIRRUP_MULTI_NAME = "шаг_количество/поз.(_)";
    private const string TYPE_VERT_NAME    = "шаг_количество"; // вертикальная арматура стены 
    private const string TYPE_POS_DIAM_NAME = "поз._диаметр"; // MRA на середине высоты каждой стены
    private const string TAG_TYPE_NAME = "Позиция_(_)_без полки";
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
                .Where(x => x.Name == TYPE_BIG_NAME || x.Name == TYPE_SMALL_NAME
                         || x.Name == TYPE_STIRRUP_NAME || x.Name == TYPE_STIRRUP_MULTI_NAME
                         || x.Name == TYPE_VERT_NAME || x.Name == TYPE_POS_DIAM_NAME)
                .ToDictionary(x => x.Name, x => x);

        DimensionType rebarDimType = new FilteredElementCollector(doc)
            .OfClass(typeof(DimensionType))
            .Cast<DimensionType>()
            .FirstOrDefault(x => x.Name == DIM_TYPE_NAME);

        // ── Проверка конфигурации ─────────────────────────────────────────────
        var missingTypes = new List<string>();
        foreach (string n in new[] { TYPE_BIG_NAME, TYPE_SMALL_NAME, TYPE_STIRRUP_NAME, TYPE_STIRRUP_MULTI_NAME, TYPE_VERT_NAME, TYPE_POS_DIAM_NAME }.Distinct())
            if (!typeCache.ContainsKey(n))
                missingTypes.Add($"Тип MRA: «{n}»");

        bool tagTypeFound = new FilteredElementCollector(doc)
            .OfClass(typeof(FamilySymbol))
            .Cast<FamilySymbol>()
            .Any(fs => fs.Name.Equals(TAG_TYPE_NAME, StringComparison.OrdinalIgnoreCase));
        if (!tagTypeFound)
            missingTypes.Add($"Тип марки: «{TAG_TYPE_NAME}»");

        if (missingTypes.Count > 0)
        {
            var errDlg = new TaskDialog("Ошибка конфигурации")
            {
                MainInstruction = "В проекте не найдены следующие типы аннотаций:",
                MainContent     = string.Join("\n", missingTypes),
                CommonButtons   = TaskDialogCommonButtons.Ok
            };
            errDlg.Show();
            return Result.Failed;
        }

        // Комментарии сборки — фильтр для стержней по параметру BI_марка_конструкции.
        string assemblyMark = assembly
            .get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString();

        // Только стержни видимые на текущем виде — Revit сам определяет видимость
        // (секущая плоскость, диапазон вида). Это исключает арматуру параллельных стен
        // сборки (напр. лифтовая шахта: 4 стены, на разрезе видна только одна).
        List<Rebar> allRebars =
            new FilteredElementCollector(doc, view.Id)
                .OfClass(typeof(Rebar))
                .Cast<Rebar>()
                .Where(r => string.IsNullOrEmpty(assemblyMark) ||
                            r.LookupParameter("BI_марка_конструкции")?.AsString() == assemblyMark)
                .ToList();

        // На каждом уровне аннотируем только крайнюю стену со стороны аннотации.
        var targetWalls = walls
            .GroupBy(w => w.LevelId)
            .Select(g => g.OrderByDescending(w => WallCenterAlong(w, view, sideDir)).First())
            .ToList();

        // Стена на самом нижнем уровне — для неё дополнительно создаём MRA по
        // вертикальным стержням (см. AnnotateVerticalRebar ниже).
        Wall lowestWall = targetWalls
            .OrderBy(w => w.get_BoundingBox(null)?.Min.Z ?? double.MaxValue)
            .FirstOrDefault();

        // Диагностика — отдельный список для каждой стены.
        var warningsByWall = new List<(Wall wall, List<string> msgs)>();

        // Скрытые зоны разрывов вида (в мировых Z-координатах).
        var hiddenZones = new List<(double bottom, double top)>();
        var mgrCheck = view.GetCropRegionShapeManager();
        if (mgrCheck.Split && mgrCheck.NumberOfSplitRegions >= 2)
        {
            BoundingBoxXYZ cb = view.CropBox;
            Transform ct = cb.Transform;
            double wBottomZ = Math.Min(ct.OfPoint(cb.Min).Z, ct.OfPoint(cb.Max).Z);
            double wTopZ    = Math.Max(ct.OfPoint(cb.Min).Z, ct.OfPoint(cb.Max).Z);
            double wHeight  = wTopZ - wBottomZ;
            int nRegions = mgrCheck.NumberOfSplitRegions;
            for (int i = 0; i < nRegions - 1; i++)
            {
                double localBottom = mgrCheck.GetSplitRegionMaximum(i);
                double localTop    = mgrCheck.GetSplitRegionMinimum(i + 1);
                hiddenZones.Add((wBottomZ + localBottom * wHeight, wBottomZ + localTop * wHeight));
            }
        }



        using (Transaction t = new Transaction(doc, "Аннотации арматуры (сборка)"))
        {
            t.Start();

            // Удаляем MRA и марки арматуры созданные предыдущими запусками плагина.
            // Без этого Revit создаёт дубли на том же стержне → один из них (или оба) невидим.
            var toDelete = new List<ElementId>();
            toDelete.AddRange(new FilteredElementCollector(doc, view.Id)
                .OfClass(typeof(MultiReferenceAnnotation))
                .ToElementIds());
            toDelete.AddRange(new FilteredElementCollector(doc, view.Id)
                .OfClass(typeof(IndependentTag))
                .WherePasses(new ElementCategoryFilter(BuiltInCategory.OST_Rebar))
                .ToElementIds());
            if (toDelete.Count > 0)
            {
                doc.Delete(toDelete);
                doc.Regenerate();
            }

            var wallMsgsById = new Dictionary<ElementId, List<string>>();

            foreach (Wall wall in targetWalls)
            {
                // Стержни для этой стены: только те, у которых хост == эта стена.
                // Fallback по BI_марка_конструкции намеренно убран — в сборке из нескольких стен
                // (напр. лифтовая шахта) все стены имеют одинаковую марку, что приводило к тому,
                // что стержни всех стен попадали в одну стену.
                string wallMark = wall.LookupParameter("BI_марка_конструкции")?.AsString();
                List<Rebar> hosted = allRebars.Where(r => r.GetHostId() == wall.Id).ToList();

                // botZ/topZ нужны только для BothInWall — берём из BBox стены.
                BoundingBoxXYZ wallBb = wall.get_BoundingBox(null);
                double botZ = wallBb?.Min.Z ?? 0.0;
                double topZ = wallBb?.Max.Z ?? double.PositiveInfinity;

                var wallMsgs = new List<string>();
                warningsByWall.Add((wall, wallMsgs));
                wallMsgsById[wall.Id] = wallMsgs;

                AnnotateWall(doc, view, wall, hosted, typeCache, rebarDimType, sideDir, hiddenZones, wallMsgs,
                    wallBotZ: botZ, wallTopZ: topZ);

                if (lowestWall != null && wall.Id == lowestWall.Id)
                    AnnotateVerticalRebar(doc, view, wall, hosted, typeCache, rebarDimType, hiddenZones, wallMsgs);

                AnnotateWallMidPosition(doc, view, wall, hosted, typeCache, sideDir, hiddenZones, wallMsgs);

                AnnotateShortRebarToWallTop(doc, view, wall, hosted, rebarDimType, sideDir, hiddenZones, wallMsgs);
            }

            // Переходы между стенами сборки (этаж → этаж): размер верх плиты → верх
            // выступающего вертикального стержня нижней стены (стык/выпуск арматуры).
            var wallsByZ = targetWalls
                .Select(w => new { Wall = w, Bb = w.get_BoundingBox(null) })
                .Where(x => x.Bb != null)
                .OrderBy(x => x.Bb.Min.Z)
                .Select(x => x.Wall)
                .ToList();
            for (int wi = 0; wi + 1 < wallsByZ.Count; wi++)
            {
                Wall wallBelow = wallsByZ[wi];
                Wall wallAbove = wallsByZ[wi + 1];
                List<string> msgs = wallMsgsById.TryGetValue(wallBelow.Id, out var m) ? m : new List<string>();
                AnnotateRebarProjectionAboveFloor(doc, view, wallBelow, wallAbove, allRebars, rebarDimType, sideDir, hiddenZones, msgs);
            }

            t.Commit();
        }

        int mraCount = new FilteredElementCollector(doc, view.Id)
            .OfClass(typeof(MultiReferenceAnnotation))
            .GetElementCount();

        TaskDialog.Show("Готово",
            $"Сборка \"{assemblyMark}\"\nСоздано аннотаций: {mraCount}\nСтен обработано: {warningsByWall.Count}");

        //foreach (var (wall, msgs) in warningsByWall)
        //{
        //    if (msgs.Count == 0) continue;
        //    string wallMark2 = wall.LookupParameter("BI_марка_конструкции")?.AsString() ?? "-";
        //    TaskDialog.Show(
        //        $"Стена [{wall.Id.IntValue()}] {wallMark2}",
        //        string.Join("\n", msgs));
        //}

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
        List<(double bottom, double top)> hiddenZones,
        List<string> warnings,
        double wallBotZ = double.NegativeInfinity,
        double wallTopZ = double.PositiveInfinity)
    {
        // Знак стороны: +1 если sideDir совпадает с «вправо» вида, иначе −1.
        double sideSign = sideDir.DotProduct(view.RightDirection) >= 0 ? 1.0 : -1.0;

        // ── Диагностика ───────────────────────────────────────────────────────
        string wallMarkDbg = wall.LookupParameter("BI_марка_конструкции")?.AsString() ?? "-";
        string FmtMm(double ft) => $"{UnitUtils.ConvertFromInternalUnits(ft, UnitTypeId.Millimeters):F0}";
        string FmtPtDbg(XYZ p) => p == null ? "null" : $"({FmtMm(p.X)};{FmtMm(p.Y)};{FmtMm(p.Z)})";

        warnings.Add($"=== Стена [{wall.Id.IntValue()}] марка={wallMarkDbg} ===");
        if (hosted != null)
        {
            var pShapes = hosted.Where(r => IsPShape(ShapeName(doc, r))).ToList();
            warnings.Add($"  П-шек в стене: {pShapes.Count}");

            var groups = pShapes
                .GroupBy(r => r.LookupParameter("BI_позиция")?.AsString() is string p && p.Length > 0
                    ? p : $"id:{r.Id.IntValue()}")
                .OrderBy(g => g.Key);

            foreach (var g in groups)
            {
                Rebar rep = g.First();
                string sn = ShapeName(doc, rep);
                warnings.Add($"  поз={g.Key} ({sn}) [{string.Join(",", g.Select(r => r.Id.IntValue()))}]");

                // Шаг 1: кривые позиции 0
                IList<Curve> cs = rep.GetCenterlineCurves(false, false, false, MultiplanarOption.IncludeOnlyPlanarCurves, 0);
                string csSource = "IncludeOnlyPlanarCurves";
                if (cs == null || cs.Count == 0)
                {
                    cs = rep.GetCenterlineCurves(true, true, false, MultiplanarOption.IncludeOnlyPlanarCurves, 0);
                    csSource = "suppressHooks+BendRadius";
                }

                if (cs == null || cs.Count == 0)
                {
                    // Шаг 2 фолбэк: габарит
                    BoundingBoxXYZ bb2 = rep.get_BoundingBox(null);
                    if (bb2 != null)
                    {
                        XYZ sz = bb2.Max - bb2.Min;
                        double dNf = Math.Abs(sz.DotProduct(view.ViewDirection));
                        double dUf = Math.Abs(sz.DotProduct(view.UpDirection));
                        bool hf = dNf > 0.01 && dNf >= dUf;
                        warnings.Add($"    шаг1: кривых нет → фолбэк по ББ: dN={FmtMm(dNf)} dU={FmtMm(dUf)} → {(hf ? "ГОРИЗОНТАЛЬНАЯ" : "ВЕРТИКАЛЬНАЯ")}");
                    }
                    else
                    {
                        warnings.Add("    шаг1: кривых нет, ББ недоступен → ВЕРТИКАЛЬНАЯ");
                    }
                    continue;
                }

                warnings.Add($"    шаг1: кривых={cs.Count} источник={csSource}");

                // Шаг 2: разбор сегментов
                XYZ R = view.RightDirection, U = view.UpDirection, Nv = view.ViewDirection;
                int rCnt = 0, uCnt = 0, nCnt = 0;
                foreach (Curve c in cs)
                {
                    if (!(c is Line ln)) { warnings.Add($"    сегм: не Line → пропуск"); continue; }
                    XYZ d = (ln.GetEndPoint(1) - ln.GetEndPoint(0)).Normalize();
                    double dotN = Math.Abs(d.DotProduct(Nv));
                    double dotR = Math.Abs(d.DotProduct(R));
                    double dotU = Math.Abs(d.DotProduct(U));
                    if (dotN > 0.9)
                    {
                        nCnt++;
                        warnings.Add($"    сегм: dotN={dotN:F2} dotR={dotR:F2} dotU={dotU:F2} → ВГЛУБЬ (пропуск)");
                    }
                    else if (dotR >= dotU)
                    {
                        rCnt++;
                        warnings.Add($"    сегм: dotN={dotN:F2} dotR={dotR:F2} dotU={dotU:F2} → R (гориз)");
                    }
                    else
                    {
                        uCnt++;
                        warnings.Add($"    сегм: dotN={dotN:F2} dotR={dotR:F2} dotU={dotU:F2} → U (верт)");
                    }
                }

                bool isHoriz = rCnt >= uCnt;
                warnings.Add($"    шаг3: rCount={rCnt} uCount={uCnt} nCount={nCnt} → {(isHoriz ? "ГОРИЗОНТАЛЬНАЯ" : "ВЕРТИКАЛЬНАЯ")}");
            }
        }
        // ─────────────────────────────────────────────────────────────────────

        if (hosted == null || hosted.Count == 0)
        {
            return false;
        }

        // Стержни, попадающие в разрыв вида хотя бы одним краем — пропускаем.
        if (hiddenZones.Count > 0)
        {
            hosted = hosted.Where(r =>
            {
                BoundingBoxXYZ bb = r.get_BoundingBox(null);
                if (bb == null) return true;
                double minZ = bb.Min.Z, maxZ = bb.Max.Z;
                return !hiddenZones.Any(hz =>
                    (minZ > hz.bottom && minZ < hz.top) ||
                    (maxZ > hz.bottom && maxZ < hz.top));
            }).ToList();
        }

        // Горизонтальный рабочий набор: ориентация «Гориз», незамкнутый, не П-шка/хомут/шпилька.
        // Среди них выбираем позицию с наибольшим n (она будет «главной»).
        // Затем берём ВСЕ элементы с той же позицией — это могут быть параллельные слои стены.
        var horizCandidates = hosted
            .Where(r => IsInsideCrop(view, r))
            .Where(r => { string sn = ShapeName(doc, r); return !IsPShape(sn) && !SKIP_SHAPE_NAMES.Contains(sn); })
            .Where(r =>
            {
                string o = ClassifyOrient(r, view, out bool closed, out double _);
                return o == "Гориз" && !closed;
            })
            .ToList();

        Rebar rebar1 = horizCandidates
            .OrderByDescending(r => r.GetHostId() == wall.Id ? 1 : 0)
            .ThenByDescending(r => r.NumberOfBarPositions)
            .FirstOrDefault();

        double wallCenterR = WallCenterAlong(wall, view, view.RightDirection);
        string rebar1Pos = rebar1?.LookupParameter("BI_позиция")?.AsString();

        // Глубина элемента (ViewDirection) — для выбора ближней грани.
        double RebarDepth(Rebar r)
        {
            BoundingBoxXYZ bb = r.get_BoundingBox(null);
            return bb != null
                ? ((bb.Min + bb.Max) / 2.0).DotProduct(view.ViewDirection)
                : double.MaxValue;
        }

        // Z-центр элемента — для группировки слоёв.
        double RebarZCenter(Rebar r)
        {
            BoundingBoxXYZ bb = r.get_BoundingBox(null);
            return bb != null ? (bb.Min.Z + bb.Max.Z) / 2.0 : 0;
        }

        var samePosCandidates = horizCandidates
            .Where(r => r.LookupParameter("BI_позиция")?.AsString() == rebar1Pos)
            .ToList();

        // Группируем по Z-центру (±100 мм = один и тот же слой).
        // Из каждой группы берём только БЛИЖНИЙ элемент (минимальная глубина по ViewDirection).
        // Результат: один представитель на каждый уникальный Z-слой.
        double zTolFt = UnitUtils.ConvertToInternalUnits(100, UnitTypeId.Millimeters);
        var zRepresentatives = new List<Rebar>();
        var usedIds = new HashSet<ElementId>();
        foreach (Rebar r in samePosCandidates.OrderBy(RebarDepth)) // ближние первыми
        {
            if (usedIds.Contains(r.Id)) continue;
            zRepresentatives.Add(r);
            double zC = RebarZCenter(r);
            foreach (Rebar r2 in samePosCandidates)
                if (Math.Abs(RebarZCenter(r2) - zC) <= zTolFt)
                    usedIds.Add(r2.Id);
        }

        // Основной элемент — нативные хост-стержни в приоритете, затем по числу стержней.
        rebar1 = zRepresentatives
            .OrderByDescending(r => r.GetHostId() == wall.Id ? 1 : 0)
            .ThenByDescending(r => r.NumberOfBarPositions)
            .FirstOrDefault();

        // П-шка: горизонтальные по ориентации. Предпочитаем сторону аннотации,
        // но если там нет — берём с другой стороны (жёсткий фильтр по стороне убран).
        Rebar rebar2 = hosted
            .Where(r => IsInsideCrop(view, r))
            .Where(r => IsPShape(ShapeName(doc, r)))
            .Where(r => IsHorizontalPShape(r, view))
            .Select(r =>
            {
                ClassifyOrient(r, view, out bool _, out double cR);
                return new { Rebar = r, CenterR = cR };
            })
            .OrderByDescending(x => (x.CenterR - wallCenterR) * sideSign)  // нужная сторона — первой
            .ThenByDescending(x => x.Rebar.NumberOfBarPositions)            // предпочитаем большие массивы
            .Select(x => x.Rebar)
            .FirstOrDefault();

        if (rebar1 == null && rebar2 == null)
        {
            return false;
        }

        XYZ tagHead1 = null;
        var rebar1MraTagHeads = new List<XYZ>();
        bool rebar1IsPShape = rebar1 != null && IsPShape(ShapeName(doc, rebar1));
        List<Rebar> rebar1SortedReps = new List<Rebar>();

        // П-шки любого вида (горизонтальные И вертикальные в разрезе).
        // Короткие П-шки на концах прямых стержней видны в разрезе как вертикальные стержни
        // (основной стержень уходит в толщу стены), IsHorizontalPShape = false для них.
        bool hasAnyPShapes = hosted.Any(r =>
            IsInsideCrop(view, r) && IsPShape(ShapeName(doc, r)));

        warnings.Add($"[DBG {wall.Id.IntValue()}] rebar1={rebar1?.Id.IntValue().ToString() ?? "null"}"
            + $" rebar2={rebar2?.Id.IntValue().ToString() ?? "null"}"
            + $" hasAnyPShapes={hasAnyPShapes}");

        XYZ rebar1AnnotationPoint = null;

        if (rebar1 != null)
        {
            // Для покрытий и доп. стержня используем основной элемент (наибольший n).
            XYZ annotationPoint = ComputeAutoAnnotationPoint(
                view, rebar1, ANNOTATION_OFFSET_MM, sideDir, wall);
            rebar1AnnotationPoint = annotationPoint;

            if (annotationPoint != null)
            {

                // Сортируем представителей по Z — нужно для размеров между массивами
                var sortedReps = zRepresentatives
                    .OrderBy(r => { BoundingBoxXYZ bb = r.get_BoundingBox(null); return bb != null ? (bb.Min.Z + bb.Max.Z) / 2.0 : 0.0; })
                    .ToList();
                rebar1SortedReps = sortedReps;

                // Для MRA пригодны только n>1; n=1 стержни не дают корректных ссылок и Revit отвергает их.
                // Если все представители n=1 — используем их как fallback.
                var mraReps1 = sortedReps.Where(r => r.NumberOfBarPositions > 1).ToList();
                if (mraReps1.Count == 0) mraReps1 = sortedReps;

                // Один MRA на каждый Z-представитель; запоминаем у кого MRA успешна
                XYZ dimDir1 = null;
                var mraOk1 = new HashSet<ElementId>();
                foreach (Rebar rep in mraReps1)
                {
                    XYZ dimDirN = null, tagHeadN = null;
                    CreateMultiReferenceAnnotation(doc, view, rep, new List<Rebar> { rep },
                        typeCache, TYPE_BIG_NAME, TYPE_SMALL_NAME, annotationPoint, warnings,
                        out dimDirN, out tagHeadN, shiftDown: !rebar1IsPShape);
                    warnings.Add(dimDirN != null
                        ? $"  [MRA1 OK]   [{rep.Id.IntValue()}] tagHead={FmtPtDbg(tagHeadN)}"
                        : $"  [MRA1 FAIL] [{rep.Id.IntValue()}]");
                    if (dimDirN != null) { mraOk1.Add(rep.Id); if (dimDir1 == null) dimDir1 = dimDirN; if (tagHeadN != null) rebar1MraTagHeads.Add(tagHeadN); }
                    if (rep.Id == rebar1.Id) tagHead1 = tagHeadN;
                }
                // rebar1 может не совпасть по ID с Z-представителем (ближний к виду другой стержень).
                // Берём первый доступный tagHead из набора MRA.
                if (tagHead1 == null && rebar1MraTagHeads.Count > 0)
                    tagHead1 = rebar1MraTagHeads[0];

                // Для П-шек dimDir из геометрии стержня горизонтален (боковая сторона П).
                // Межмассивный размер между стопками П-шек — вертикальный, берём UpDirection.
                XYZ interArrayDimDir = (rebar1IsPShape && dimDir1 != null)
                    ? view.UpDirection
                    : dimDir1;

                // Размеры между соседними массивами одной позиции (крайние стержни групп).
                if (sortedReps.Count > 1 && interArrayDimDir != null)
                {
                    double zTolInterArr = UnitUtils.ConvertToInternalUnits(50, UnitTypeId.Millimeters);
                    for (int i = 0; i < sortedReps.Count - 1; i++)
                    {
                        // Оба репа должны иметь MRA (n=1 без MRA не включаем в цепочку).
                        if (!mraOk1.Contains(sortedReps[i].Id) || !mraOk1.Contains(sortedReps[i + 1].Id)) continue;
                        if (!BothInWall(sortedReps[i], sortedReps[i + 1], wallBotZ, wallTopZ)) continue;
                        // Пропускаем пары с совпадающим Z-центром (иначе размер = 0).
                        var bb0 = sortedReps[i].get_BoundingBox(null);
                        var bb1 = sortedReps[i + 1].get_BoundingBox(null);
                        if (bb0 == null || bb1 == null) continue;
                        double zCtr0 = (bb0.Min.Z + bb0.Max.Z) / 2.0;
                        double zCtr1 = (bb1.Min.Z + bb1.Max.Z) / 2.0;
                        if (Math.Abs(zCtr1 - zCtr0) < zTolInterArr) continue;
                        CreateInterArrayDimension(doc, view,
                            sortedReps[i], sortedReps[i + 1],
                            interArrayDimDir, annotationPoint, rebarDimType, sideDir);
                    }
                }

                if (rebar1IsPShape)
                {
                    CreatePShapeAnchorDimensions(doc, view, sortedReps, wall, annotationPoint, rebarDimType, sideDir,
                        allHosted: hosted, mraOk: mraOk1);
                }
                else
                {
                    // Доп стержни (не получили MRA) выше/ниже основного массива —
                    // включаем их в цепочку защитного слоя
                    XYZ upDir1 = view.UpDirection;
                    double Upc(Rebar r) {
                        BoundingBoxXYZ bb = r.get_BoundingBox(null);
                        return bb != null ? ((bb.Min + bb.Max) / 2.0).DotProduct(upDir1) : 0.0;
                    }
                    double mainUpc = Upc(rebar1);
                    // П-шки не должны использоваться как промежуточные стержни в цепочке,
                    // иначе короткая П-шка (та же поз.) вызовет паразитный размер до верха стены.
                    Rebar extraUpper = sortedReps
                        .Where(r => !mraOk1.Contains(r.Id) && Upc(r) > mainUpc && !IsPShape(ShapeName(doc, r)))
                        .OrderBy(r => Upc(r))
                        .FirstOrDefault();
                    Rebar extraLower = sortedReps
                        .Where(r => !mraOk1.Contains(r.Id) && Upc(r) < mainUpc && !IsPShape(ShapeName(doc, r)))
                        .OrderByDescending(r => Upc(r))
                        .FirstOrDefault();
                    var nativePool = hosted
                        .Where(r => r.GetHostId() == wall.Id && !IsPShape(ShapeName(doc, r)))
                        .ToList();
                    // Нижний размер (стена → первый прямой стержень) нужен всегда, даже когда
                    // в стене есть П-шки — иначе нижняя привязка к грани стены отсутствует.
                    CreateCoverDimensions(doc, view, rebar1, wall, annotationPoint, rebarDimType, sideDir,
                        createLower: true,
                        extraUpperRebar: extraUpper, extraLowerRebar: extraLower,
                        nativePool: nativePool, allZReps: sortedReps);
                }
            }
        }
        // Элементы П-шек, аннотированные блоком A (else-if rebar2) — для дедупликации в блоке B.
        var annotatedInBlockA = new HashSet<ElementId>();

        // Короткие П-шки на концах прямых стержней: в разрезе видны как вертикальные сегменты
        // (IsHorizontalPShape=false). Создаём простую марку, без MRA и размеров.
        if (rebar1 != null && rebar2 == null && hasAnyPShapes)
        {
            var nonHorizPShapes = hosted
                .Where(r => IsInsideCrop(view, r))
                .Where(r => IsPShape(ShapeName(doc, r)))
                .Where(r => !IsHorizontalPShape(r, view))
                .ToList();
            if (nonHorizPShapes.Count > 0)
            {
                double nhZTol = UnitUtils.ConvertToInternalUnits(100, UnitTypeId.Millimeters);
                var nhReps = new List<Rebar>();
                var nhUsed = new HashSet<ElementId>();
                foreach (Rebar r in nonHorizPShapes.OrderBy(r2 => {
                    BoundingBoxXYZ bb = r2.get_BoundingBox(null);
                    return bb != null ? ((bb.Min + bb.Max) / 2.0).DotProduct(view.ViewDirection) : double.MaxValue;
                }))
                {
                    if (nhUsed.Contains(r.Id)) continue;
                    nhReps.Add(r);
                    BoundingBoxXYZ bb0 = r.get_BoundingBox(null);
                    double zC = bb0 != null ? (bb0.Min.Z + bb0.Max.Z) / 2.0 : 0;
                    foreach (Rebar r2 in nonHorizPShapes)
                    {
                        BoundingBoxXYZ bb2 = r2.get_BoundingBox(null);
                        double zC2 = bb2 != null ? (bb2.Min.Z + bb2.Max.Z) / 2.0 : 0;
                        if (Math.Abs(zC2 - zC) <= nhZTol) nhUsed.Add(r2.Id);
                    }
                }
                // Тот же паттерн, что и для вторичных позиций хомутов (см. ниже): якорь —
                // голова тега MRA прямых стержней (tagHead1), боковой сдвиг вправо добавляем
                // один раз как константу, шаг вверх — ДО размещения каждого тега, позиция
                // передаётся через overrideTagPos (без внутреннего сдвига CreateCategoryTag,
                // который иначе прибавлялся бы ещё раз поверх и уводил марку далеко от MRA).
                const double NH_STEP_MM = 250;
                const double NH_SIDE_MM = 250;
                double nhStepFt = UnitUtils.ConvertToInternalUnits(NH_STEP_MM, UnitTypeId.Millimeters);
                double nhSideFt = UnitUtils.ConvertToInternalUnits(NH_SIDE_MM, UnitTypeId.Millimeters);
                XYZ nhTagBase = (tagHead1 ?? rebar1AnnotationPoint) + view.RightDirection * nhSideFt;

                string Fmt(XYZ p) => p == null ? "null"
                    : $"({UnitUtils.ConvertFromInternalUnits(p.X, UnitTypeId.Millimeters):F0};"
                    + $"{UnitUtils.ConvertFromInternalUnits(p.Y, UnitTypeId.Millimeters):F0};"
                    + $"{UnitUtils.ConvertFromInternalUnits(p.Z, UnitTypeId.Millimeters):F0})";

                warnings.Add($"[DBG стена {wall.Id.IntValue()}]"
                    + $"\n  tagHead1        = {Fmt(tagHead1)} мм"
                    + $"\n  rebar1AnnotPt   = {Fmt(rebar1AnnotationPoint)} мм"
                    + $"\n  nhTagBase       = {Fmt(nhTagBase)} мм"
                    + $"\n  view.UpDir      = {Fmt(view.UpDirection)}"
                    + $"\n  view.RightDir   = {Fmt(view.RightDirection)}"
                    + $"\n  reps            = {nhReps.Count}");

                foreach (Rebar rep in nhReps)
                {
                    nhTagBase += view.UpDirection * nhStepFt;
                    CreateCategoryTag(doc, view, rep, null, warnings, overrideTagPos: nhTagBase);
                }
            }
        }

        else if (rebar2 != null && (rebar1 == null || rebar1IsPShape))
        {
            // Только П-шки (без прямых стержней) или П-шки рядом с другими П-шками:
            // Z-дедупликация всех одноимённых П-шек, MRA + меж-массив + привязки.
            // Группируем СНАЧАЛА по BI_позиция (как хомуты и «длинные» П-шки в другой ветке) —
            // раньше обрабатывалась только позиция, которой принадлежит rebar2 (единственный
            // глобально выбранный представитель), а остальные типы П-шек в этой же стене
            // вообще не доходили до MRA.
            double zTolFt2 = UnitUtils.ConvertToInternalUnits(100, UnitTypeId.Millimeters);

            var allHorizPShapes2 = hosted
                .Where(r => IsInsideCrop(view, r))
                .Where(r => IsPShape(ShapeName(doc, r)))
                .Where(r => IsHorizontalPShape(r, view))
                .ToList();

            var pShapesByPos2 = allHorizPShapes2
                .GroupBy(r => r.LookupParameter("BI_позиция")?.AsString() ?? "")
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Привязку к граням стены делаем ОДИН раз на все позиции вместе (см. ниже, после
            // цикла) — иначе у каждой позиции появлялась своя привязка к низу/верху стены,
            // хотя по факту к стене должны привязываться только САМЫЙ нижний и САМЫЙ верхний
            // стержень среди ВСЕХ П-шек, как это уже делается для разных массивов одной позиции.
            var allRebar2RepsAcrossPos = new List<Rebar>();
            var allMraOk2AcrossPos = new HashSet<ElementId>();
            XYZ anyDimDir2 = null;

            foreach (var posGroup2 in pShapesByPos2)
            {
                var rebar2Candidates = posGroup2.ToList();

                // Запоминаем все элементы этой позиции — блок B не будет их повторно аннотировать.
                foreach (var r in rebar2Candidates) annotatedInBlockA.Add(r.Id);

                var rebar2Reps = new List<Rebar>();
                var usedIds2 = new HashSet<ElementId>();
                foreach (Rebar r in rebar2Candidates.OrderBy(r2 =>
                {
                    BoundingBoxXYZ bb = r2.get_BoundingBox(null);
                    return bb != null ? ((bb.Min + bb.Max) / 2.0).DotProduct(view.ViewDirection) : double.MaxValue;
                }))
                {
                    if (usedIds2.Contains(r.Id)) continue;
                    rebar2Reps.Add(r);
                    BoundingBoxXYZ bb0 = r.get_BoundingBox(null);
                    double zC = bb0 != null ? (bb0.Min.Z + bb0.Max.Z) / 2.0 : 0;
                    foreach (Rebar r2 in rebar2Candidates)
                    {
                        BoundingBoxXYZ bb2 = r2.get_BoundingBox(null);
                        double zC2 = bb2 != null ? (bb2.Min.Z + bb2.Max.Z) / 2.0 : 0;
                        if (Math.Abs(zC2 - zC) <= zTolFt2) usedIds2.Add(r2.Id);
                    }
                }

                var sortedRebar2Reps = rebar2Reps
                    .OrderBy(r => { BoundingBoxXYZ bb = r.get_BoundingBox(null); return bb != null ? (bb.Min.Z + bb.Max.Z) / 2.0 : 0.0; })
                    .ToList();
                allRebar2RepsAcrossPos.AddRange(sortedRebar2Reps);

                // Одиночные П-шки (n=1) не пригодны для MRA — исключаем из MRA-цикла.
                // Но для них создаём размер от их центра до крайнего стержня ближайшего массива.
                var mraRebar2Reps = sortedRebar2Reps.Where(r => r.NumberOfBarPositions > 1).ToList();
                if (mraRebar2Reps.Count == 0) mraRebar2Reps = sortedRebar2Reps; // fallback: если все n=1

                // Представитель ЭТОЙ позиции для точки аннотации — ближе к стороне аннотации,
                // затем наибольший n (глобальный rebar2 относится только к своей позиции).
                Rebar posRep2 = rebar2Candidates
                    .OrderByDescending(r => { ClassifyOrient(r, view, out bool _, out double cR); return (cR - wallCenterR) * sideSign; })
                    .ThenByDescending(r => r.NumberOfBarPositions)
                    .FirstOrDefault() ?? rebar2Candidates[0];

                XYZ annotationPoint2 = ComputeAutoAnnotationPoint(
                    view, posRep2, ANNOTATION_OFFSET_MM, sideDir, wall);
                if (annotationPoint2 != null)
                {
                    XYZ dimDir2 = null;
                    var mraOk2 = new HashSet<ElementId>();
                    foreach (Rebar rep in mraRebar2Reps)
                    {
                        XYZ dimDirN = null, tagHeadN = null;
                        CreateMultiReferenceAnnotation(doc, view, rep, new List<Rebar> { rep },
                            typeCache, TYPE_BIG_NAME, TYPE_SMALL_NAME, annotationPoint2, warnings,
                            out dimDirN, out tagHeadN, shiftDown: false);
                        warnings.Add(dimDirN != null
                            ? $"  [MRA2 OK]   [{rep.Id.IntValue()}] tagHead={FmtPtDbg(tagHeadN)}"
                            : $"  [MRA2 FAIL] [{rep.Id.IntValue()}]");
                        if (dimDirN != null) { mraOk2.Add(rep.Id); if (dimDir2 == null) dimDir2 = dimDirN; }
                        if (rep.Id == rebar2.Id) tagHead1 = tagHeadN;
                    }

                    if (mraRebar2Reps.Count > 1 && dimDir2 != null)
                    {
                        for (int i = 0; i < mraRebar2Reps.Count - 1; i++)
                        {
                            if (!mraOk2.Contains(mraRebar2Reps[i].Id) || !mraOk2.Contains(mraRebar2Reps[i + 1].Id)) continue;
                            if (!BothInWall(mraRebar2Reps[i], mraRebar2Reps[i + 1], wallBotZ, wallTopZ)) continue;
                            CreateInterArrayDimension(doc, view,
                                mraRebar2Reps[i], mraRebar2Reps[i + 1],
                                dimDir2, annotationPoint2, rebarDimType, sideDir);
                        }
                    }

                    foreach (var okId in mraOk2) allMraOk2AcrossPos.Add(okId);
                    if (dimDir2 != null && anyDimDir2 == null) anyDimDir2 = dimDir2;
                }
            }

            // Привязка к граням стены — один раз на объединённом списке всех позиций: сама
            // найдёт самый нижний и самый верхний стержень среди ВСЕХ П-шек этой стены.
            if (anyDimDir2 != null && allRebar2RepsAcrossPos.Count > 0)
            {
                XYZ annotationPointAll = ComputeAutoAnnotationPoint(
                    view, rebar2, ANNOTATION_OFFSET_MM, sideDir, wall);
                if (annotationPointAll != null)
                    CreatePShapeAnchorDimensions(doc, view, allRebar2RepsAcrossPos, wall,
                        annotationPointAll, rebarDimType, sideDir,
                        allHosted: hosted, mraOk: allMraOk2AcrossPos);
            }
        }

        // ── П-шки рядом с прямыми стержнями → MRA по X-группам + размер до прямых ──
        if (rebar2 != null && rebar1 != null && !rebar1IsPShape)
        {
            var allPShapes = hosted
                .Where(r => IsInsideCrop(view, r))
                .Where(r => IsPShape(ShapeName(doc, r)))
                .Where(r => IsHorizontalPShape(r, view))
                .ToList();

            if (allPShapes.Count > 0)
            {
                double PShapeXCtr(Rebar r) {
                    BoundingBoxXYZ bb = r.get_BoundingBox(null);
                    return bb != null ? ((bb.Min + bb.Max) / 2.0).DotProduct(view.RightDirection) : 0.0;
                }
                double PShapeZCtr(Rebar r) {
                    BoundingBoxXYZ bb = r.get_BoundingBox(null);
                    return bb != null ? (bb.Min.Z + bb.Max.Z) / 2.0 : 0.0;
                }

                // Длинные П-шки замещают горизонтальную арматуру в зоне ПРОЁМА —
                // их Z-диапазон НЕ пересекается с Z-диапазоном прямых стержней.
                // Короткие П-шки — на концах прямых стержней, их Z совпадает с прямыми.
                double zTolSep = UnitUtils.ConvertToInternalUnits(50, UnitTypeId.Millimeters);
                var straightZRanges = rebar1SortedReps.Select(r => {
                    BoundingBoxXYZ bb = r.get_BoundingBox(null);
                    return bb != null ? (Min: bb.Min.Z, Max: bb.Max.Z)
                                     : (Min: double.MaxValue, Max: double.MinValue);
                }).ToList();
                bool OverlapsStrBars(Rebar r) {
                    BoundingBoxXYZ bb = r.get_BoundingBox(null);
                    if (bb == null) return false;
                    double zLo = bb.Min.Z, zHi = bb.Max.Z;
                    return straightZRanges.Any(rng => zHi > rng.Min - zTolSep && zLo < rng.Max + zTolSep);
                }
                var longPShapes  = allPShapes.Where(r => !OverlapsStrBars(r)).ToList();
                var shortPShapes = allPShapes.Where(r =>  OverlapsStrBars(r)).ToList();

                // Позиции П-шек, уже аннотированных в блоке B (short ИЛИ long секция).
                // Ключ: BI_позиция если непустая, иначе TypeId как строка.
                var pShapeAnnotatedKeys = new HashSet<string>();
                string PShapeKey(Rebar r)
                {
                    string pos = r.LookupParameter("BI_позиция")?.AsString();
                    return string.IsNullOrEmpty(pos) ? r.GetTypeId().ToString() : pos;
                }

                // Короткие П-шки: Z-дедупликация + простая марка
                if (shortPShapes.Count > 0)
                {
                    var shortReps = new List<Rebar>();
                    var shortUsed = new HashSet<ElementId>();
                    double shortZTol = UnitUtils.ConvertToInternalUnits(100, UnitTypeId.Millimeters);
                    foreach (Rebar r in shortPShapes.OrderBy(r2 => {
                        BoundingBoxXYZ bb = r2.get_BoundingBox(null);
                        return bb != null ? ((bb.Min + bb.Max) / 2.0).DotProduct(view.ViewDirection) : double.MaxValue;
                    }))
                    {
                        if (shortUsed.Contains(r.Id)) continue;
                        shortReps.Add(r);
                        double zC = PShapeZCtr(r);
                        foreach (Rebar r2 in shortPShapes)
                            if (Math.Abs(PShapeZCtr(r2) - zC) <= shortZTol) shortUsed.Add(r2.Id);
                    }
                    // Та же логика, что и у хомутов (см. "Вторичные позиции хомутов" ниже):
                    // марка ВСЕГДА выше MRA (якорь tagHead1), боковой сдвиг вправо — константой
                    // один раз, шаг вверх — на P_STEP_MM перед КАЖДЫМ размещением (в т.ч. первым),
                    // чтобы марка не садилась поверх текста MRA. При нескольких репрезентантах
                    // группы (несколько П-шек на разных Z) каждая следующая встаёт ещё выше.
                    const double P_STEP_MM = 350;
                    const double P_SIDE_MM = 250;
                    double pStepFt = UnitUtils.ConvertToInternalUnits(P_STEP_MM, UnitTypeId.Millimeters);
                    double pSideFt = UnitUtils.ConvertToInternalUnits(P_SIDE_MM, UnitTypeId.Millimeters);
                    XYZ stbBase = tagHead1 ?? rebar1AnnotationPoint;
                    XYZ curPTagBase = stbBase != null ? stbBase + view.RightDirection * pSideFt : null;

                    foreach (Rebar rep in shortReps)
                    {
                        // Пропускаем позиции, уже аннотированные (блок A или предыдущий раздел).
                        if (annotatedInBlockA.Contains(rep.Id)) continue;
                        string repKey = PShapeKey(rep);
                        if (pShapeAnnotatedKeys.Contains(repKey)) continue;
                        pShapeAnnotatedKeys.Add(repKey);

                        if (curPTagBase != null) curPTagBase += view.UpDirection * pStepFt;
                        CreateCategoryTag(doc, view, rep, null, warnings, overrideTagPos: curPTagBase);
                    }
                }

                // Длинные П-шки: СНАЧАЛА группируем по типу/позиции (BI_позиция) — как хомуты
                // (stirrupByPos) и как для прямых стержней (samePosCandidates по rebar1Pos) —
                // чтобы у каждого типа П-шки была своя отдельная MRA, а не общая на все типы
                // сразу. Внутри каждой позиции — прежняя X-группировка (разрыв > 400 мм между
                // соседними X-центрами = новая пространственная группа той же позиции, напр.
                // одинаковые П-шки по разные стороны проёма).
                double pGapFt = UnitUtils.ConvertToInternalUnits(400, UnitTypeId.Millimeters);
                var longPShapesByPos = longPShapes
                    .GroupBy(r => r.LookupParameter("BI_позиция")?.AsString() ?? "")
                    .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                Rebar topmostPShape = null;

                // Нижнюю привязку к грани стены делаем ОДИН раз на все позиции вместе (см.
                // после обоих циклов) — иначе у каждого типа П-шки появлялась своя привязка
                // к низу стены, хотя к стене должен привязываться только самый нижний стержень
                // среди ВСЕХ длинных П-шек.
                var allPRepsAcrossPos = new List<Rebar>();
                var allPMraOkAcrossPos = new HashSet<ElementId>();
                bool anyPDimDir = false;

                foreach (var posGroup in longPShapesByPos)
                {
                    var pShapeGroups = new List<List<Rebar>>();
                    var pCur = new List<Rebar>();
                    double prevPX = double.MinValue;
                    foreach (Rebar r in posGroup.OrderBy(PShapeXCtr))
                    {
                        double x = PShapeXCtr(r);
                        if (pCur.Count > 0 && x - prevPX > pGapFt) { pShapeGroups.Add(pCur); pCur = new List<Rebar>(); }
                        pCur.Add(r); prevPX = x;
                    }
                    if (pCur.Count > 0) pShapeGroups.Add(pCur);

                    // Если групп несколько — оставляем только группы на стороне аннотации (sideDir).
                    // Противоположная сторона (другая узкая часть у проёма) не аннотируется с этой стороны.
                    if (pShapeGroups.Count > 1)
                        pShapeGroups = pShapeGroups
                            .Where(g => (g.Average(PShapeXCtr) - wallCenterR) * sideSign >= 0)
                            .ToList();

                    foreach (var pGroup in pShapeGroups)
                    {
                        // Z-дедупликация: один представитель на Z-уровень (ближний к виду)
                        var pReps = new List<Rebar>();
                        var pUsed = new HashSet<ElementId>();
                        double pZTol = UnitUtils.ConvertToInternalUnits(100, UnitTypeId.Millimeters);
                        foreach (Rebar r in pGroup.OrderBy(r2 => {
                            BoundingBoxXYZ bb = r2.get_BoundingBox(null);
                            return bb != null ? ((bb.Min + bb.Max) / 2.0).DotProduct(view.ViewDirection) : double.MaxValue;
                        }))
                        {
                            if (pUsed.Contains(r.Id)) continue;
                            pReps.Add(r);
                            double zC = PShapeZCtr(r);
                            foreach (Rebar r2 in pGroup)
                                if (Math.Abs(PShapeZCtr(r2) - zC) <= pZTol) pUsed.Add(r2.Id);
                        }
                        if (pReps.Count == 0) continue;

                        // Главный представитель: ближе к стороне аннотации, затем наибольший n
                        Rebar pMain = pGroup
                            .OrderByDescending(r => { ClassifyOrient(r, view, out bool _, out double cR); return (cR - wallCenterR) * sideSign; })
                            .ThenByDescending(r => r.NumberOfBarPositions)
                            .FirstOrDefault() ?? pReps[0];

                        // Пропускаем группы с позицией, уже аннотированной в short-разделе или предыдущей X-группе той же позиции.
                        string groupKey = PShapeKey(pMain);
                        if (annotatedInBlockA.Contains(pMain.Id) || pShapeAnnotatedKeys.Contains(groupKey))
                        {
                            // Обновляем кандидата на topmostPShape даже для пропущенных групп.
                            Rebar groupTopSkip = pGroup.OrderByDescending(PShapeZCtr).FirstOrDefault();
                            if (groupTopSkip != null && (topmostPShape == null || PShapeZCtr(groupTopSkip) > PShapeZCtr(topmostPShape)))
                                topmostPShape = groupTopSkip;
                            continue;
                        }
                        pShapeAnnotatedKeys.Add(groupKey);

                        XYZ pAnnotPt = ComputeAutoAnnotationPoint(view, pMain, ANNOTATION_OFFSET_MM, sideDir, wall);
                        if (pAnnotPt == null) continue;

                        var pMraReps = pReps.Where(r => r.NumberOfBarPositions > 1).ToList();
                        if (pMraReps.Count == 0) pMraReps = pReps;
                        var pSorted = pMraReps.OrderBy(PShapeZCtr).ToList();

                        XYZ pDimDir = null;
                        var pMraOk = new HashSet<ElementId>();
                        foreach (Rebar rep in pSorted)
                        {
                            XYZ dd = null, th = null;
                            CreateMultiReferenceAnnotation(doc, view, rep, new List<Rebar> { rep },
                                typeCache, TYPE_BIG_NAME, TYPE_SMALL_NAME, pAnnotPt, warnings,
                                out dd, out th, shiftDown: false);
                            warnings.Add(dd != null
                                ? $"  [MRAp OK]   [{rep.Id.IntValue()}] tagHead={FmtPtDbg(th)}"
                                : $"  [MRAp FAIL] [{rep.Id.IntValue()}]");
                            if (dd != null) { pMraOk.Add(rep.Id); if (pDimDir == null) pDimDir = dd; }
                        }

                        // Копим для единой нижней привязки после циклов (см. allPRepsAcrossPos
                        // выше) — верхняя в любом случае заменяется размером П-шки → первый
                        // прямой стержень, создаваемым ниже.
                        if (pDimDir != null)
                        {
                            allPRepsAcrossPos.AddRange(pReps);
                            foreach (var okId in pMraOk) allPMraOkAcrossPos.Add(okId);
                            anyPDimDir = true;
                        }

                        // Верхний П-образный стержень группы → кандидат для размера до прямых
                        Rebar groupTop = pGroup.OrderByDescending(PShapeZCtr).FirstOrDefault();
                        if (groupTop != null && (topmostPShape == null || PShapeZCtr(groupTop) > PShapeZCtr(topmostPShape)))
                            topmostPShape = groupTop;
                    }
                }

                // Нижняя привязка к грани стены — один раз на объединённом списке всех позиций.
                if (anyPDimDir && allPRepsAcrossPos.Count > 0)
                {
                    XYZ pAnnotPtAll = ComputeAutoAnnotationPoint(view, rebar2, ANNOTATION_OFFSET_MM, sideDir, wall);
                    if (pAnnotPtAll != null)
                        CreatePShapeAnchorDimensions(doc, view, allPRepsAcrossPos, wall, pAnnotPtAll, rebarDimType, sideDir,
                            allHosted: hosted, mraOk: allPMraOkAcrossPos, skipUpperAnchor: true);
                }

                // Размер от центра крайней П-шки до первого прямого стержня
                Rebar bottomStraight = rebar1SortedReps.Count > 0 ? rebar1SortedReps[0] : null;
                if (topmostPShape != null && bottomStraight != null)
                {
                    IList<Curve> cs = rebar1.GetCenterlineCurves(false, false, false, MultiplanarOption.IncludeOnlyPlanarCurves, 0);
                    if (cs != null && cs.Count > 0)
                    {
                        GetRebarDirAndMid(cs, out XYZ rd, out XYZ _);
                        XYZ dimDirPS = SafeCrossProduct(rd, view.ViewDirection);
                        if (dimDirPS != null)
                        {
                            XYZ psDimAnnotPt = ComputeAutoAnnotationPoint(view, rebar1, ANNOTATION_OFFSET_MM, sideDir, wall);
                            if (psDimAnnotPt != null)
                                CreateInterArrayDimension(doc, view, topmostPShape, bottomStraight, dimDirPS, psDimAnnotPt, rebarDimType, sideDir);
                        }
                    }
                }
            }
        }

        // ── Хомуты: MRA с противоположной стороны ────────────────────────────
        {
            XYZ oppDir = sideDir.Negate(); // сторона хомутов — напротив основных стержней

            // Отбираем хомуты видимые в виде, принадлежащие текущей стене по Z (шпильки исключены).
            double stTol = UnitUtils.ConvertToInternalUnits(10, UnitTypeId.Millimeters);
            var stirrupCandidates = hosted
                .Where(r => IsInsideCrop(view, r))
                .Where(r => IsStirrupShape(ShapeName(doc, r)))
                .Where(r =>
                {
                    BoundingBoxXYZ bb = r.get_BoundingBox(null);
                    if (bb == null) return false;
                    double zC = (bb.Min.Z + bb.Max.Z) / 2.0;
                    return zC >= wallBotZ - stTol && zC <= wallTopZ + stTol;
                })
                .ToList();

            if (stirrupCandidates.Count > 0)
            {
                // Группируем по позиции; сортировка по BI_позиции (Х1 раньше Х2).
                var stirrupByPos = stirrupCandidates
                    .GroupBy(r => r.LookupParameter("BI_позиция")?.AsString() ?? "")
                    .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                // ── Главная позиция хомутов → MRA ──────────────────────────
                var mainStirGroup = stirrupByPos[0].ToList();

                // Направление вдоль стены (горизонталь вида) — перпендикулярно sideDir и Z.
                XYZ wallLenDir;
                if (wall.Location is LocationCurve _lc && _lc.Curve is Line _line)
                    wallLenDir = _line.Direction;
                else
                    wallLenDir = XYZ.BasisZ.CrossProduct(sideDir).Normalize();

                double planTolFt = UnitUtils.ConvertToInternalUnits(100, UnitTypeId.Millimeters);

                XYZ StirCenter(Rebar r)
                {
                    BoundingBoxXYZ bb = r.get_BoundingBox(null);
                    return bb != null ? (bb.Min + bb.Max) / 2.0 : XYZ.Zero;
                }
                double StirProjLen(Rebar r)  => StirCenter(r).DotProduct(wallLenDir);
                double StirProjOpp(Rebar r)  => StirCenter(r).DotProduct(oppDir);

                // Группируем главную позицию по столбцу (X вдоль стены), берём столбец ближайший к oppDir.
                var columnGroups = mainStirGroup
                    .GroupBy(r => (int)Math.Round(StirProjLen(r) / planTolFt))
                    .ToList();

                var bestColumn = columnGroups
                    .OrderByDescending(g => g.Average(r => StirProjOpp(r)))
                    .First();

                // Главный представитель — с наибольшим n, ближайший к грани аннотации.
                Rebar mainStirRep = bestColumn
                    .OrderByDescending(r => r.NumberOfBarPositions)
                    .ThenByDescending(StirProjOpp)
                    .FirstOrDefault();

                if (mainStirRep != null)
                {
                    XYZ stirAnnotPt = ComputeAutoAnnotationPoint(
                        view, mainStirRep, ANNOTATION_OFFSET_MM, oppDir, wall);

                    if (stirAnnotPt != null)
                    {
                        // MRA из элементов лучшего столбца с n>1; если таких нет — все элементы столбца.
                        var mraStirReps = bestColumn.Where(r => r.NumberOfBarPositions > 1).ToList();
                        if (mraStirReps.Count == 0) mraStirReps = bestColumn.ToList();

                        XYZ stirDimDir = null, stirTagHead = null;
                        bool multiPos = stirrupByPos.Count > 1;
                        string stirTypeName = multiPos ? TYPE_STIRRUP_MULTI_NAME : TYPE_STIRRUP_NAME;
                        CreateMultiReferenceAnnotation(doc, view, mainStirRep, mraStirReps,
                            typeCache, stirTypeName, stirTypeName, stirAnnotPt, warnings,
                            out stirDimDir, out stirTagHead, shiftDown: multiPos);
                        warnings.Add(stirDimDir != null
                            ? $"  [MRAx OK]   [{mainStirRep.Id.IntValue()}] tagHead={FmtPtDbg(stirTagHead)}"
                            : $"  [MRAx FAIL] [{mainStirRep.Id.IntValue()}]");

                        // ── Вторичные позиции хомутов → простая марка, стек вверх ──
                        // Каждая следующая позиция (Х2, Х3…) сдвигается выше предыдущей на свой
                        // шаг (см. TagStepMm ниже: Х1→Х2 350мм, Х2→Х3 и далее 250мм). Сдвиг
                        // применяется ДО размещения тега (а не после) — иначе первая вторичная
                        // позиция ставится ровно в stirTagHead, то есть поверх размерного текста
                        // главной MRA.
                        //
                        // Позиция передаётся через overrideTagPos, а не как tagHead1: без этого
                        // CreateCategoryTag сама добавляет свой фиксированный сдвиг (+250 вверх,
                        // +250 вбок), одинаковый для КАЖДОГО вызова — он «съедался» в разнице
                        // между соседними вторичными тегами и они слипались. С overrideTagPos
                        // шаг цикла — единственный источник вертикального разнесения. Боковой
                        // сдвиг вправо (тот же, что раньше давала CreateCategoryTag) добавляем
                        // здесь один раз как константу — он нужен, чтобы марка не сидела прямо
                        // на линии выноски MRA, но сам по себе на шаг между тегами не влияет.
                        double TagStepMm(int gi) => gi == 1 ? 350.0 : 250.0;   // Х1→Х2 / Х2→Х3…
                        const double TAG_SIDE_MM = 250;
                        double tagSideFt = UnitUtils.ConvertToInternalUnits(TAG_SIDE_MM, UnitTypeId.Millimeters);
                        XYZ curTagBase = (stirTagHead ?? stirAnnotPt) + view.RightDirection * tagSideFt;
                        for (int gi = 1; gi < stirrupByPos.Count; gi++)
                        {
                            double tagStepFt = UnitUtils.ConvertToInternalUnits(TagStepMm(gi), UnitTypeId.Millimeters);
                            curTagBase += view.UpDirection * tagStepFt;
                            Rebar secRep = stirrupByPos[gi]
                                .OrderByDescending(r => r.NumberOfBarPositions)
                                .ThenByDescending(StirProjOpp)
                                .FirstOrDefault();
                            if (secRep != null)
                                CreateCategoryTag(doc, view, secRep, null, warnings, overrideTagPos: curTagBase);
                        }
                    }
                }
            }
        }

        return true;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Вертикальная арматура стены на нижнем уровне: MRA (тип "шаг_количество",
    // без длины — как и для малых массивов) под стеной, а ниже — размер между
    // торцевыми гранями стены (с осью в цепочке, если рядом видна ось).
    // ─────────────────────────────────────────────────────────────────────────
    private static void AnnotateVerticalRebar(
        Document doc, View view, Wall wall,
        List<Rebar> hosted,
        Dictionary<string, MultiReferenceAnnotationType> typeCache,
        DimensionType dimType,
        List<(double bottom, double top)> hiddenZones,
        List<string> warnings)
    {
        if (hosted == null || hosted.Count == 0) return;

        if (hiddenZones.Count > 0)
        {
            hosted = hosted.Where(r =>
            {
                BoundingBoxXYZ bb = r.get_BoundingBox(null);
                if (bb == null) return true;
                double minZ = bb.Min.Z, maxZ = bb.Max.Z;
                return !hiddenZones.Any(hz =>
                    (minZ > hz.bottom && minZ < hz.top) ||
                    (maxZ > hz.bottom && maxZ < hz.top));
            }).ToList();
        }

        // Вертикальный рабочий набор: ориентация «Верт», незамкнутый, не П-шка/хомут/шпилька.
        // Позиции с буквой «В» в BI_позиции исключаются — как и в AnnotateWallMidPosition,
        // это отдельные (стартовые/угловые) стержни с другим шагом, для них MRA не нужна:
        // без исключения они попадали в общий набор и ломали ровный «200x16» на сегменты
        // вида «400x2 + 200x12» (у Revit шаг/количество берутся из своих элементов).
        var vertCandidates = hosted
            .Where(r => IsInsideCrop(view, r))
            .Where(r => { string sn = ShapeName(doc, r); return !IsPShape(sn) && !SKIP_SHAPE_NAMES.Contains(sn); })
            .Where(r =>
            {
                string o = ClassifyOrient(r, view, out bool closed, out double _);
                return o == "Верт" && !closed;
            })
            .Where(r => (r.LookupParameter("BI_позиция")?.AsString() ?? "").IndexOf("В", StringComparison.OrdinalIgnoreCase) < 0)
            .ToList();

        // Глубина элемента (ViewDirection) — стена обычно имеет два слоя вертикальной
        // арматуры (у передней и у задней грани), в разрезе оба видны в одном месте
        // по X/Z и отличаются только глубиной. Берём ОДИН слой — ближний к виду
        // («передний»), как и для Z-слоёв горизонтальной арматуры (см. AnnotateWall).
        double RebarDepth(Rebar r)
        {
            BoundingBoxXYZ bb = r.get_BoundingBox(null);
            return bb != null ? ((bb.Min + bb.Max) / 2.0).DotProduct(view.ViewDirection) : double.MaxValue;
        }

        Rebar rebarV = vertCandidates
            .OrderBy(RebarDepth)                                       // ближний слой первым
            .ThenByDescending(r => r.GetHostId() == wall.Id ? 1 : 0)
            .ThenByDescending(r => r.NumberOfBarPositions)
            .FirstOrDefault();

        if (rebarV == null)
        {
            warnings.Add($"[Верт] Стена [{wall.Id.IntValue()}]: вертикальных стержней не найдено.");
            return;
        }

        // Группа для MRA: ВСЕ вертикальные стержни этого слоя по глубине (±100мм),
        // вне зависимости от BI_позиции — эта MRA («шаг_количество» под стеной) должна
        // охватывать все позиции сразу, а не только позицию представителя rebarV.
        // Фильтр по глубине остаётся — иначе в набор попадут стержни обеих граней
        // (переднего и заднего слоя).
        double rebarVDepth = RebarDepth(rebarV);
        double depthTolFt = UnitUtils.ConvertToInternalUnits(100, UnitTypeId.Millimeters);
        List<Rebar> rebarVGroup = vertCandidates
            .Where(r => Math.Abs(RebarDepth(r) - rebarVDepth) <= depthTolFt)
            .ToList();
        if (rebarVGroup.Count == 0) rebarVGroup.Add(rebarV);

        // MRA — под стеной (offset вниз вместо вбок), тип «шаг_количество» без
        // длины принудительно — стена вертикальной арматуры проходит через
        // этажи, полная длина массива тут не показывается.
        XYZ downDir = -view.UpDirection;
        XYZ mraPoint = ComputeAutoAnnotationPoint(view, rebarV, ANNOTATION_OFFSET_MM, downDir, wall);
        if (mraPoint == null)
        {
            warnings.Add($"[Верт] Стена [{wall.Id.IntValue()}]: не удалось вычислить точку MRA.");
            return;
        }

        CreateMultiReferenceAnnotation(
            doc, view, rebarV, rebarVGroup, typeCache,
            TYPE_VERT_NAME, TYPE_VERT_NAME,   // всегда «шаг_количество» (без «/поз.(_)»)
            mraPoint, warnings,
            out XYZ dimDirH, out XYZ _,
            shiftDown: true);

        if (dimDirH == null)
        {
            warnings.Add($"[Верт] Стена [{wall.Id.IntValue()}]: MRA не создана, размер по торцам пропущен.");
            return;
        }

        // Размер по торцевым граням стены — ниже MRA.
        const double SPAN_DIM_GAP_MM = 400.0;
        XYZ spanPoint = ComputeAutoAnnotationPoint(view, rebarV, ANNOTATION_OFFSET_MM + SPAN_DIM_GAP_MM, downDir, wall);
        if (spanPoint == null) return;

        CreateWallSpanDimension(doc, view, wall, dimDirH, spanPoint, dimType, warnings);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // MRA «поз._диаметр» на середине высоты КАЖДОЙ стены (не только нижней):
    // группирует вертикальные стержни по BI_позиции; если позиций (диаметров)
    // несколько — создаёт ОТДЕЛЬНУЮ MRA на каждую, разнесённые по высоте, чтобы
    // не сливались в одну точку. Размещаются с противоположной от горизонтальных
    // марок стороны, чтобы не накладывались.
    // ─────────────────────────────────────────────────────────────────────────
    private static void AnnotateWallMidPosition(
        Document doc, View view, Wall wall,
        List<Rebar> hosted,
        Dictionary<string, MultiReferenceAnnotationType> typeCache,
        XYZ sideDir,
        List<(double bottom, double top)> hiddenZones,
        List<string> warnings)
    {
        if (hosted == null || hosted.Count == 0) return;

        if (hiddenZones.Count > 0)
        {
            hosted = hosted.Where(r =>
            {
                BoundingBoxXYZ bb = r.get_BoundingBox(null);
                if (bb == null) return true;
                double minZ = bb.Min.Z, maxZ = bb.Max.Z;
                return !hiddenZones.Any(hz =>
                    (minZ > hz.bottom && minZ < hz.top) ||
                    (maxZ > hz.bottom && maxZ < hz.top));
            }).ToList();
        }

        var vertCandidates = hosted
            .Where(r => IsInsideCrop(view, r))
            .Where(r => { string sn = ShapeName(doc, r); return !IsPShape(sn) && !SKIP_SHAPE_NAMES.Contains(sn); })
            .Where(r =>
            {
                string o = ClassifyOrient(r, view, out bool closed, out double _);
                return o == "Верт" && !closed;
            })
            // Позиции с буквой «В» в BI_позиции исключаются — для них MRA не нужна.
            .Where(r => (r.LookupParameter("BI_позиция")?.AsString() ?? "").IndexOf("В", StringComparison.OrdinalIgnoreCase) < 0)
            .ToList();

        if (vertCandidates.Count == 0)
        {
            warnings.Add($"[Поз.MRA] Стена [{wall.Id.IntValue()}]: вертикальных стержней не найдено.");
            return;
        }

        double Diameter(Rebar r) =>
            doc.GetElement(r.GetTypeId()) is RebarBarType bt ? bt.BarModelDiameter : double.MaxValue;
        Rebar PickRep(IEnumerable<Rebar> rs) => rs
            .OrderByDescending(r => r.GetHostId() == wall.Id ? 1 : 0)
            .ThenByDescending(r => r.NumberOfBarPositions)
            .FirstOrDefault();

        // Одна группа на каждую BI_позицию — по возрастанию диаметра (стабильный порядок).
        var groups = vertCandidates
            .GroupBy(r => r.LookupParameter("BI_позиция")?.AsString() is string p && p.Length > 0
                ? p : $"id:{r.Id.IntValue()}")
            .Select(g => new { Rebars = g.ToList(), Diameter = g.Min(Diameter) })
            .OrderBy(g => g.Diameter)
            .ToList();

        // В направлении, противоположном горизонтальным маркам (sideDir), — чтобы не пересекались.
        const double POS_MRA_OFFSET_MM = 350.0;
        XYZ oppositeSideDir = sideDir.Negate();

        // Общая базовая точка (середина высоты стены) — одна на все группы, чтобы
        // разнесение по высоте отсчитывалось от единого центра, а не «плавало»
        // из-за разной геометрии представителей разных позиций.
        Rebar baselineRep = PickRep(groups[0].Rebars);
        XYZ baseline = baselineRep != null
            ? ComputeAutoAnnotationPoint(view, baselineRep, POS_MRA_OFFSET_MM, oppositeSideDir, wall)
            : null;
        if (baseline == null)
        {
            warnings.Add($"[Поз.MRA] Стена [{wall.Id.IntValue()}]: не удалось вычислить точку MRA.");
            return;
        }

        // Несколько диаметров — разносим MRA по высоте от общего центра, шагом POS_MRA_STEP_MM.
        const double POS_MRA_STEP_MM = 500.0;
        double stepFt = UnitUtils.ConvertToInternalUnits(POS_MRA_STEP_MM, UnitTypeId.Millimeters);
        double startOffsetFt = -(groups.Count - 1) / 2.0 * stepFt;

        for (int gi = 0; gi < groups.Count; gi++)
        {
            Rebar rep = PickRep(groups[gi].Rebars);
            if (rep == null) continue;

            XYZ point = baseline + view.UpDirection * (startOffsetFt + gi * stepFt);

            CreateMultiReferenceAnnotation(
                doc, view, rep, groups[gi].Rebars, typeCache,
                TYPE_POS_DIAM_NAME, TYPE_POS_DIAM_NAME,
                point, warnings,
                out XYZ _, out XYZ _,
                shiftDown: false,
                tagAtOrigin: true);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Стержни с буквой «В» в BI_позиции (исключённые из MRA поз./диаметр и из
    // размера выступа над плитой) — для них отдельный размер: нижний торец
    // стержня → верх СВОЕЙ стены (не соседней сверху, как для проекции).
    // ─────────────────────────────────────────────────────────────────────────
    private static void AnnotateShortRebarToWallTop(
        Document doc, View view, Wall wall,
        List<Rebar> hosted,
        DimensionType dimType,
        XYZ sideDir,
        List<(double bottom, double top)> hiddenZones,
        List<string> warnings)
    {
        if (hosted == null || hosted.Count == 0) return;

        if (hiddenZones.Count > 0)
        {
            hosted = hosted.Where(r =>
            {
                BoundingBoxXYZ bb = r.get_BoundingBox(null);
                if (bb == null) return true;
                double minZ = bb.Min.Z, maxZ = bb.Max.Z;
                return !hiddenZones.Any(hz =>
                    (minZ > hz.bottom && minZ < hz.top) ||
                    (maxZ > hz.bottom && maxZ < hz.top));
            }).ToList();
        }

        // Только стержни С буквой «В» в BI_позиции — обратный фильтр к остальным MRA.
        var vCandidates = hosted
            .Where(r => IsInsideCrop(view, r))
            .Where(r => { string sn = ShapeName(doc, r); return !IsPShape(sn) && !SKIP_SHAPE_NAMES.Contains(sn); })
            .Where(r =>
            {
                string o = ClassifyOrient(r, view, out bool closed, out double _);
                return o == "Верт" && !closed;
            })
            .Where(r => (r.LookupParameter("BI_позиция")?.AsString() ?? "").IndexOf("В", StringComparison.OrdinalIgnoreCase) >= 0)
            .ToList();

        if (vCandidates.Count == 0) return;

        var wallTopFaces = GetWallFaceReferencesAlong(wall, view, view.UpDirection);
        if (wallTopFaces.Count < 1)
        {
            warnings.Add($"[В] Стена [{wall.Id.IntValue()}]: верхняя грань стены не найдена.");
            return;
        }
        RefWithCoord wallTop = wallTopFaces.OrderByDescending(f => f.Coord).First();

        // Стержень-основа — с той стороны стены, где будет стоять размер (см. sideDir.Negate()
        // ниже): та же логика, что и для выпусков над плитой.
        double sideRightDot = sideDir.Negate().DotProduct(view.RightDirection);
        bool pickRightmost = sideRightDot >= 0;
        double HorizCoord(Rebar r)
        {
            BoundingBoxXYZ bb = r.get_BoundingBox(view);
            return bb != null ? ((bb.Min + bb.Max) / 2.0).DotProduct(view.RightDirection) : 0.0;
        }

        Rebar PickRep(IEnumerable<Rebar> rs) => rs
            .OrderByDescending(r => r.GetHostId() == wall.Id ? 1 : 0)
            .ThenByDescending(r => pickRightmost ? HorizCoord(r) : -HorizCoord(r))
            .ThenByDescending(r => r.NumberOfBarPositions)
            .FirstOrDefault();

        // Одна группа (и один размер) на каждую отдельную BI_позицию с «В».
        var groups = vCandidates
            .GroupBy(r => r.LookupParameter("BI_позиция")?.AsString() is string p && p.Length > 0
                ? p : $"id:{r.Id.IntValue()}")
            .ToList();

        foreach (var group in groups)
        {
            Rebar rep = PickRep(group);
            if (rep == null) continue;

            RefWithCoord? bottomRef = GetRebarEndRefPerp(rep, view, view.UpDirection, wantMax: false);
            if (bottomRef == null)
            {
                warnings.Add($"[В] Стена [{wall.Id.IntValue()}]: нижний торец стержня [{rep.Id.IntValue()}] не найден.");
                continue;
            }

            // С противоположной от горизонтальных марок стороны — как и остальные MRA/размеры у вертикальной арматуры.
            XYZ point = ComputeAutoAnnotationPoint(view, rep, PROJECTION_DIM_OFFSET_MM, sideDir.Negate(), wall);
            if (point == null)
            {
                warnings.Add($"[В] Стена [{wall.Id.IntValue()}]: не удалось вычислить точку размера.");
                continue;
            }

            CreateOneCoverDim(doc, view, view.UpDirection, point,
                bottomRef.Value.Reference, bottomRef.Value.Coord,
                wallTop.Reference, wallTop.Coord, dimType, null, warnings);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Переход между стенами сборки (этаж → этаж): размер от верха плиты, лежащей
    // на границе, до верха самого выступающего вертикального стержня нижней
    // стены (стык/выпуск арматуры на след. этаж — см. эскиз).
    // ─────────────────────────────────────────────────────────────────────────
    private static void AnnotateRebarProjectionAboveFloor(
        Document doc, View view, Wall wallBelow, Wall wallAbove,
        List<Rebar> allRebars, DimensionType dimType, XYZ sideDir,
        List<(double bottom, double top)> hiddenZones,
        List<string> warnings)
    {
        BoundingBoxXYZ bbBelow = wallBelow.get_BoundingBox(null);
        BoundingBoxXYZ bbAbove = wallAbove.get_BoundingBox(null);
        if (bbBelow == null || bbAbove == null) return;
        double boundaryZ = (bbBelow.Max.Z + bbAbove.Min.Z) / 2.0;

        // Переход внутри скрытой зоны разрыва вида — плиты/стержней там всё равно не видно.
        if (hiddenZones.Any(hz => boundaryZ > hz.bottom && boundaryZ < hz.top)) return;

        // Нижняя грань верхней стены (вместо грани плиты — та же категория Wall, что и
        // везде в этом файле, надёжнее сочетается со ссылкой на стержень в Dimension).
        var aboveFaces = GetWallFaceReferencesAlong(wallAbove, view, view.UpDirection);
        if (aboveFaces.Count < 1)
        {
            warnings.Add($"[Проекция] Стена [{wallAbove.Id.IntValue()}]: нижняя грань не найдена.");
            return;
        }
        RefWithCoord floorTop = aboveFaces.OrderBy(f => f.Coord).First();

        string FmtMm(double ft) => $"{UnitUtils.ConvertFromInternalUnits(ft, UnitTypeId.Millimeters):F0}";

        // Диагностика: всё, что хостится в нижней стене вообще.
        List<Rebar> hostedBelowAll = allRebars.Where(r => r.GetHostId() == wallBelow.Id).ToList();
        warnings.Add($"[Проекция] Стена [{wallBelow.Id.IntValue()}]→[{wallAbove.Id.IntValue()}]:"
            + $" boundaryZ={FmtMm(boundaryZ)} стержней в нижней стене всего={hostedBelowAll.Count}");
        foreach (Rebar r in hostedBelowAll)
        {
            string sn = ShapeName(doc, r);
            string o = ClassifyOrient(r, view, out bool closed, out double _);
            string pos = r.LookupParameter("BI_позиция")?.AsString() ?? "?";
            BoundingBoxXYZ bb = r.get_BoundingBox(view);
            warnings.Add($"    [{r.Id.IntValue()}] поз={pos} форма='{sn}' ориент={o} closed={closed}"
                + $" inCrop={IsInsideCrop(view, r)} topZ={(bb != null ? FmtMm(bb.Max.Z) : "?")}");
        }

        // Вертикальные стержни нижней стены — ищем самый выступающий верх.
        var vertCandidates = hostedBelowAll
            .Where(r => IsInsideCrop(view, r))
            .Where(r => { string sn = ShapeName(doc, r); return !IsPShape(sn) && !SKIP_SHAPE_NAMES.Contains(sn); })
            .Where(r =>
            {
                string o = ClassifyOrient(r, view, out bool closed, out double _);
                return o == "Верт" && !closed;
            })
            // Позиции с буквой «В» в BI_позиции исключаются — как и для MRA поз./диаметр.
            .Where(r => (r.LookupParameter("BI_позиция")?.AsString() ?? "").IndexOf("В", StringComparison.OrdinalIgnoreCase) < 0)
            .ToList();
        warnings.Add($"[Проекция] после фильтров (Верт, не закрыт, не П-шка/skip, без «В»): {vertCandidates.Count}"
            + $" [{string.Join(",", vertCandidates.Select(r => r.Id.IntValue()))}]");

        // Стержень-основа — с той стороны стены, где будет стоять размер (см. sideDir.Negate()
        // ниже): если размер уходит вправо — берём крайний правый стержень, если влево —
        // крайний левый. Высота (верх bbox) — только как второй критерий при равенстве.
        double sideRightDot = sideDir.Negate().DotProduct(view.RightDirection);
        bool pickRightmost = sideRightDot >= 0;
        double HorizCoord(Rebar r)
        {
            BoundingBoxXYZ bb = r.get_BoundingBox(view);
            return bb != null ? ((bb.Min + bb.Max) / 2.0).DotProduct(view.RightDirection) : 0.0;
        }

        Rebar rep = vertCandidates
            .OrderByDescending(r => pickRightmost ? HorizCoord(r) : -HorizCoord(r))
            .ThenByDescending(r => r.get_BoundingBox(view)?.Max.Z ?? double.MinValue)
            .FirstOrDefault();
        if (rep == null)
        {
            warnings.Add($"[Проекция] Стена [{wallBelow.Id.IntValue()}]: выступающий вертикальный стержень не найден (после фильтров пусто).");
            return;
        }

        // Торец стержня — линия, ПЕРПЕНДИКУЛЯРНАЯ его оси (как в RebarAnnotation.cs,
        // рабочий приём для продольных размеров): верхнеуровневые Line (Solid пропускаем),
        // без рекурсии в GeometryInstance, без явного DetailLevel — вида/этой геометрии хватает.
        RefWithCoord? topEndRef = GetRebarEndRefPerp(rep, view, view.UpDirection);
        warnings.Add($"[Проекция] стержень-представитель [{rep.Id.IntValue()}]:"
            + (topEndRef.HasValue ? $" торец найден, Z={FmtMm(topEndRef.Value.Coord)}" : " торец (перпенд. линия) не найден"));

        Reference topRefElem;
        double topCoordEst;
        if (topEndRef.HasValue)
        {
            topRefElem = topEndRef.Value.Reference;
            topCoordEst = topEndRef.Value.Coord;
        }
        else
        {
            // Fallback — грань всё же не нашлась: засечка (DetailCurve) в точке верха стержня.
            BoundingBoxXYZ repBb = rep.get_BoundingBox(view);
            if (repBb == null)
            {
                warnings.Add($"[Проекция] Стена [{wallBelow.Id.IntValue()}]: не удалось получить габарит стержня [{rep.Id.IntValue()}].");
                return;
            }
            topCoordEst = repBb.Max.Z;
            XYZ barCenter = (repBb.Min + repBb.Max) / 2.0;
            XYZ barTopProj = ProjectOntoViewPlane(new XYZ(barCenter.X, barCenter.Y, topCoordEst), view);
            double tickHalfFt = UnitUtils.ConvertToInternalUnits(50, UnitTypeId.Millimeters);
            try
            {
                Line tickLine = Line.CreateBound(
                    barTopProj - view.RightDirection * tickHalfFt,
                    barTopProj + view.RightDirection * tickHalfFt);
                DetailCurve tick = doc.Create.NewDetailCurve(view, tickLine);
                topRefElem = tick.GeometryCurve.Reference;
                warnings.Add($"[Проекция] стержень-представитель [{rep.Id.IntValue()}]: грань не найдена, засечка верха создана, topZ={FmtMm(topCoordEst)}");
            }
            catch (Exception ex)
            {
                warnings.Add($"[Проекция] Стена [{wallBelow.Id.IntValue()}]: не удалось создать засечку верха стержня: {ex.Message}");
                return;
            }
        }

        // Как и MRA поз./диаметр — с противоположной от горизонтальных марок стороны.
        XYZ point = ComputeAutoAnnotationPoint(view, rep, PROJECTION_DIM_OFFSET_MM, sideDir.Negate(), wallBelow);
        if (point == null)
        {
            warnings.Add($"[Проекция] Стена [{wallBelow.Id.IntValue()}]: не удалось вычислить точку размера.");
            return;
        }

        warnings.Add($"[Проекция] floorTop.Coord={FmtMm(floorTop.Coord)} topCoordEst={FmtMm(topCoordEst)} → создаю размер");
        CreateOneCoverDim(doc, view, view.UpDirection, point,
            floorTop.Reference, floorTop.Coord, topRefElem, topCoordEst, dimType, null, warnings);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Размер между торцевыми гранями стены. Если рядом видна ось — цепочка из
    // трёх ссылок (грань, ось, грань), иначе — простой размер по двум граням.
    // Порядок ссылок определяется сортировкой по координате вдоль dimDir, а не
    // жёстко «ось всегда посередине» — если ось снаружи пролёта стены, она
    // естественно встанет первой или последней в цепочке.
    // ─────────────────────────────────────────────────────────────────────────
    private static void CreateWallSpanDimension(
        Document doc, View view, Wall wall, XYZ dimDir, XYZ basePt,
        DimensionType dimType, List<string> warnings)
    {
        var wallRefs = GetWallFaceReferencesAlong(wall, view, dimDir);
        if (wallRefs.Count < 2)
        {
            warnings.Add($"[Верт] Стена [{wall.Id.IntValue()}]: торцевые грани не найдены — размер по торцам пропущен.");
            return;
        }
        wallRefs.Sort((a, b) => a.Coord.CompareTo(b.Coord));
        RefWithCoord faceLow = wallRefs.First();
        RefWithCoord faceHigh = wallRefs.Last();

        RefWithCoord? axisRef = FindNearbyGridAxis(doc, view, dimDir, faceLow.Coord, faceHigh.Coord);

        var pts = new List<(double Coord, Reference Ref)>
        {
            (faceLow.Coord, faceLow.Reference),
            (faceHigh.Coord, faceHigh.Reference)
        };
        if (axisRef.HasValue) pts.Add((axisRef.Value.Coord, axisRef.Value.Reference));
        pts.Sort((a, b) => a.Coord.CompareTo(b.Coord));

        double margin = UnitUtils.ConvertToInternalUnits(20, UnitTypeId.Millimeters);
        double baseOnDim = basePt.DotProduct(dimDir);
        XYZ p1 = basePt + dimDir * (pts.First().Coord - baseOnDim - margin);
        XYZ p2 = basePt + dimDir * (pts.Last().Coord - baseOnDim + margin);
        if ((p2 - p1).GetLength() < 1e-6) return;

        Line dimLine;
        try { dimLine = Line.CreateBound(p1, p2); }
        catch { return; }

        var ra = new ReferenceArray();
        foreach (var p in pts) ra.Append(p.Ref);

        try
        {
            Dimension dim = dimType != null
                ? doc.Create.NewDimension(view, dimLine, ra, dimType)
                : doc.Create.NewDimension(view, dimLine, ra);
            warnings.Add($"[Верт] Стена [{wall.Id.IntValue()}]: размер по торцам создан"
                + (axisRef.HasValue ? " (с осью в цепочке)" : " (без оси)")
                + $", сегментов={pts.Count - 1}");
        }
        catch (Exception ex)
        {
            warnings.Add($"[Верт] Стена [{wall.Id.IntValue()}]: размер по торцам — ошибка: {ex.Message}");
        }
    }

    // Ищет видимую на виде ось (Grid), пересекающую стену в этом виде (линия оси
    // идёт поперёк dimDir) и попадающую в диапазон торцов стены ± запас. Из
    // нескольких подходящих берёт ближайшую к центру пролёта.
    private static RefWithCoord? FindNearbyGridAxis(
        Document doc, View view, XYZ dimDir, double loCoord, double hiCoord)
    {
        double marginFt = UnitUtils.ConvertToInternalUnits(3000, UnitTypeId.Millimeters);
        double midCoord = (loCoord + hiCoord) / 2.0;

        RefWithCoord? best = null;
        double bestDist = double.MaxValue;

        foreach (Grid grid in new FilteredElementCollector(doc, view.Id).OfClass(typeof(Grid)).Cast<Grid>())
        {
            if (!(grid.Curve is Line line)) continue;
            XYZ d = (line.GetEndPoint(1) - line.GetEndPoint(0)).Normalize();
            // Ось должна идти поперёк dimDir (пересекать стену в этом виде), не вдоль него.
            if (Math.Abs(d.DotProduct(dimDir)) > 0.1) continue;

            XYZ mid = (line.GetEndPoint(0) + line.GetEndPoint(1)) / 2.0;
            double coord = mid.DotProduct(dimDir);
            if (coord < loCoord - marginFt || coord > hiCoord + marginFt) continue;

            double dist = Math.Abs(coord - midCoord);
            if (dist < bestDist)
            {
                try
                {
                    best = new RefWithCoord { Reference = new Reference(grid), Coord = coord };
                    bestDist = dist;
                }
                catch { }
            }
        }
        return best;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // MultiReferenceAnnotation
    // ─────────────────────────────────────────────────────────────────────────
    private static void CreateMultiReferenceAnnotation(
        Document doc, View view, Rebar rebar, List<Rebar> rebarGroup,
        Dictionary<string, MultiReferenceAnnotationType> typeCache,
        string typeBigName, string typeSmallName,
        XYZ annotationPoint,
        List<string> warnings,
        out XYZ outDimDir,
        out XYZ outTagHeadPosition,
        bool shiftDown = true,
        bool tagAtOrigin = false)
    {
        outDimDir = null;
        outTagHeadPosition = null;
        string mraDbgId = $"[{rebar.Id.IntValue()}]";

        // Геометрия первого стержня в наборе (позиция 0)
        IList<Curve> curves = rebar.GetCenterlineCurves(
            false, false, false, MultiplanarOption.IncludeOnlyPlanarCurves, 0);

        if (curves == null || curves.Count == 0)
        {
            warnings.Add($"    MRA {mraDbgId}: нет кривых центровой линии");
            return;
        }

        // Для П-шек ноги могут идти вглубь стены (≈viewDir) или вдоль неё.
        // Chord по всем кривым может оказаться = viewDir (нога A начинается в Z₀, нога C заканчивается в Z₁).
        // Надёжнее: берём направление самого длинного Line-сегмента, не параллельного viewDir.
        XYZ vn = view.ViewDirection;
        Line longestVisible = curves
            .OfType<Line>()
            .Where(ln => Math.Abs((ln.GetEndPoint(1) - ln.GetEndPoint(0)).Normalize().DotProduct(vn)) < 0.95)
            .OrderByDescending(ln => ln.Length)
            .FirstOrDefault();

        XYZ rebarDir, firstBarMid;
        if (longestVisible != null)
        {
            rebarDir    = (longestVisible.GetEndPoint(1) - longestVisible.GetEndPoint(0)).Normalize();
            firstBarMid = (longestVisible.GetEndPoint(0) + longestVisible.GetEndPoint(1)) / 2.0;
        }
        else
        {
            GetRebarDirAndMid(curves, out rebarDir, out firstBarMid);
        }

        XYZ dimDir = SafeCrossProduct(rebarDir, view.ViewDirection);
        if (dimDir == null)
        {
            warnings.Add($"    MRA {mraDbgId}: dimDir=null (стержень параллелен нормали вида)");
            return;
        }

        // ── Шаг и количество ─────────────────────────────────────────────────
        int count = rebar.NumberOfBarPositions;
        double spacing = 0;
        Parameter sp = rebar.get_Parameter(BuiltInParameter.REBAR_ELEM_BAR_SPACING);
        if (sp != null && sp.HasValue) spacing = sp.AsDouble(); // в футах

        double spacingMm = UnitUtils.ConvertFromInternalUnits(spacing, UnitTypeId.Millimeters);
        double totalSpanMm = (count > 1 && spacing > 0) ? (count - 1) * spacingMm : 0;
        double halfSpanMm = totalSpanMm / 2.0;
        double halfSpanFt = UnitUtils.ConvertToInternalUnits(halfSpanMm, UnitTypeId.Millimeters);

        warnings.Add($"    MRA {mraDbgId}: n={count} шаг={spacingMm:F0}мм dimDir=({dimDir.X:F2};{dimDir.Y:F2};{dimDir.Z:F2})");

        // ── Центр массива — по габариту набора в виде (надёжная середина,
        // не зависит от знака dimDir). При необходимости опускаем марку ниже.
        XYZ arrayCenter;
        BoundingBoxXYZ rbb = rebar.get_BoundingBox(view);
        arrayCenter = (rbb != null)
            ? rbb.Transform.OfPoint((rbb.Min + rbb.Max) / 2.0)
            : firstBarMid + dimDir * halfSpanFt;

        // Малый массив: n * шаг < 1000 мм → тип без длины, марка по центру (без shiftDown).
        bool isSmallArray = count > 1 && spacing > 0 && (count * spacingMm) < 1000.0;
        bool effectiveShiftDown = shiftDown && !isSmallArray;

        // Опускание марки MRA ниже центра набора (вдоль вертикали вида), мм.
        // Для П-шек и малых массивов не применяем.
        if (effectiveShiftDown)
        {
            const double MRA_MARK_DOWN_MM = 150;
            double mraDownFt = UnitUtils.ConvertToInternalUnits(MRA_MARK_DOWN_MM, UnitTypeId.Millimeters);
            arrayCenter = arrayCenter - view.UpDirection * mraDownFt;
        }

        // Выбор типа MRA: малый массив → typeSmallName, иначе typeBigName.
        MultiReferenceAnnotationType typeToUse = null;
        string preferredName = isSmallArray ? typeSmallName : typeBigName;
        if (!typeCache.TryGetValue(preferredName, out typeToUse))
            typeCache.TryGetValue(typeBigName, out typeToUse); // fallback

        if (typeToUse == null)
        {
            warnings.Add($"    MRA {mraDbgId}: тип аннотации не найден ('{preferredName}')");
            return;
        }

        var options = new MultiReferenceAnnotationOptions(typeToUse);
        var ids = (rebarGroup != null && rebarGroup.Count > 0)
            ? rebarGroup.Select(r => r.Id).ToList()
            : new List<ElementId> { rebar.Id };
        options.SetElementsToDimension(ids);
        options.DimensionPlaneNormal = view.ViewDirection;
        options.DimensionLineDirection = dimDir;

        // DimensionLineOrigin — точка, указанная пользователем (где лежит линия размера).
        XYZ origin = ProjectOntoViewPlane(annotationPoint, view);
        options.DimensionLineOrigin = origin;

        // TagHeadPosition — геометрический центр массива, вычисленный через шаг и количество.
        // Проецируем arrayCenter на линию размера (сохраняем компонент вдоль rebarDir от origin,
        // но берём компонент вдоль dimDir от arrayCenter). Если tagAtOrigin=true — марка ставится
        // ровно в annotationPoint без пересчёта (нужно, когда вызывающий код сам сознательно
        // сдвинул точку вдоль dimDir, напр. наружу за грань стены — иначе сдвиг обнулился бы).
        XYZ tagHead;
        if (tagAtOrigin)
        {
            tagHead = origin;
        }
        else
        {
            double arrayCenterOnDimDir = arrayCenter.DotProduct(dimDir);
            double originOnDimDir = origin.DotProduct(dimDir);
            tagHead = origin + dimDir * (arrayCenterOnDimDir - originOnDimDir);
        }
        options.TagHeadPosition = tagHead;
        outTagHeadPosition = tagHead;

        try
        {
            MultiReferenceAnnotation.Create(doc, view.Id, options);
            outDimDir = dimDir;
        }
        catch (Exception ex)
        {
            warnings.Add($"Не удалось создать MRA для арматуры [{rebar.Id.IntValue()}]: {ex.Message}");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // IndependentTag без выноски (марка по категории)
    // ─────────────────────────────────────────────────────────────────────────
    private static void CreateCategoryTag(
        Document doc, View view, Rebar rebar,
        XYZ tagHead1,
        List<string> warnings,
        XYZ overrideTagPos = null)
    {
        FamilySymbol tagSymbol = new FilteredElementCollector(doc)
            .OfClass(typeof(FamilySymbol))
            .OfCategory(BuiltInCategory.OST_RebarTags)
            .Cast<FamilySymbol>()
            .FirstOrDefault(fs =>
                fs.Name.Equals(TAG_TYPE_NAME, StringComparison.OrdinalIgnoreCase));

        if (tagSymbol == null)
            return;

        if (!tagSymbol.IsActive)
            tagSymbol.Activate();

        // Ссылка для марки — из Subelement стержня (без ручного выбора).
        Reference tagRef = GetTagReference(rebar);
        if (tagRef == null)
        {
            warnings.Add($"Арматура [{rebar.Id.IntValue()}]: не удалось получить ссылку для марки.");
            return;
        }

        // Вертикальный сдвиг марки относительно центра (мм): + вверх / − вниз.
        // 0 = ровно по центру набора, рядом с маркой MRA.
        const double TAG_VERTICAL_MM = 250;
        double offsetUpFt = UnitUtils.ConvertToInternalUnits(TAG_VERTICAL_MM, UnitTypeId.Millimeters);
        double offsetSideFt = UnitUtils.ConvertToInternalUnits(250, UnitTypeId.Millimeters);
        XYZ tagPos;
        if (overrideTagPos != null)
        {
            tagPos = overrideTagPos;
        }
        else
        {
            XYZ basePos = tagHead1 ?? (rebar.get_BoundingBox(view) is BoundingBoxXYZ bb2
                ? (bb2.Min + bb2.Max) / 2.0 : XYZ.Zero);
            tagPos = basePos + view.UpDirection * offsetUpFt + view.RightDirection * offsetSideFt;
        }

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
            string fmtFt(double ft) => $"{UnitUtils.ConvertFromInternalUnits(ft, UnitTypeId.Millimeters):F0}";
            warnings.Add($"  [TAG OK]  [{rebar.Id.IntValue()}] pos=({fmtFt(tagPos.X)};{fmtFt(tagPos.Y)};{fmtFt(tagPos.Z)})");
        }
        catch (Exception ex)
        {
            warnings.Add($"  [TAG FAIL] [{rebar.Id.IntValue()}]: {ex.Message}");
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
    // Размер между верхним стержнем нижнего массива и нижним стержнем верхнего
    // ─────────────────────────────────────────────────────────────────────────
    private static void CreateInterArrayDimension(
        Document doc, View view, Rebar lowerRebar, Rebar upperRebar,
        XYZ dimDir, XYZ annotationPoint, DimensionType dimType, XYZ sideDir)
    {
        // legDir ⟂ dimDir в плоскости вида — надёжнее чем выводить из геометрии П-шки
        XYZ legDirRaw = dimDir.CrossProduct(view.ViewDirection);
        if (legDirRaw.GetLength() < 1e-6) return;
        XYZ legDir = legDirRaw.Normalize();

        double barRadius = 0;
        if (doc.GetElement(lowerRebar.GetTypeId()) is RebarBarType bt)
            barRadius = bt.BarModelDiameter / 2.0;
        const double eps = 0.0001;

        // Верхний стержень нижнего массива (ось)
        var lowerRefs = GetLineReferencesAlong(lowerRebar, view, legDir, dimDir);
        if (lowerRefs.Count < 1) return;
        lowerRefs.Sort((a, b) => a.Coord.CompareTo(b.Coord));

        // Нижний стержень верхнего массива (ось)
        var upperRefs = GetLineReferencesAlong(upperRebar, view, legDir, dimDir);
        if (upperRefs.Count < 1) return;
        upperRefs.Sort((a, b) => a.Coord.CompareTo(b.Coord));

        // Ссылка «верх нижнего» = та, у которой coord ближе к upperRebar.
        // Если dimDir направлен вверх (Z > 0): upper имеет б́ольший coord → берём lowerRefs.Last().
        // Если dimDir направлен вниз (Z < 0): coord = -Z, upper имеет меньший coord → берём lowerRefs.First().
        RefWithCoord topLower, bottomUpper;
        if (dimDir.DotProduct(XYZ.BasisZ) >= 0)
        {
            topLower    = ClosestRef(lowerRefs, lowerRefs.Last().Coord  - barRadius - eps);
            bottomUpper = ClosestRef(upperRefs, upperRefs.First().Coord + barRadius + eps);
        }
        else
        {
            topLower    = ClosestRef(lowerRefs, lowerRefs.First().Coord + barRadius + eps);
            bottomUpper = ClosestRef(upperRefs, upperRefs.Last().Coord  - barRadius - eps);
        }

        double topLowerMm   = UnitUtils.ConvertFromInternalUnits(topLower.Coord,   UnitTypeId.Millimeters);
        double bottomUpperMm = UnitUtils.ConvertFromInternalUnits(bottomUpper.Coord, UnitTypeId.Millimeters);
        XYZ basePt = ProjectOntoViewPlane(annotationPoint, view);
        double basePtOnDim = basePt.DotProduct(dimDir);

        // Текст в середине зазора, смещён в сторону марки
        double midCoord = (topLower.Coord + bottomUpper.Coord) / 2.0;
        XYZ anchorMid = basePt + dimDir * (midCoord - basePtOnDim);
        double rightFt = UnitUtils.ConvertToInternalUnits(350.0, UnitTypeId.Millimeters);
        XYZ textPos = anchorMid + sideDir.Normalize() * rightFt;

        CreateOneCoverDim(doc, view, dimDir, basePt,
            topLower.Reference, topLower.Coord,
            bottomUpper.Reference, bottomUpper.Coord,
            dimType, textPos);
    }

    // ─────────────────────────────────────────────────────────────────────────
    private static void CreatePShapeAnchorDimensions(
        Document doc, View view, IList<Rebar> pRebars, Wall wall,
        XYZ annotationPoint, DimensionType dimType, XYZ sideDir,
        List<Rebar> allHosted = null, HashSet<ElementId> mraOk = null,
        bool skipUpperAnchor = false)
    {
        XYZ dimDir = view.UpDirection;     // меряем по вертикали
        XYZ legDir = view.RightDirection;  // полки П идут горизонтально

        // Радиус стержня для поиска ссылок ближе к оси, а не к поверхности
        double barRadius = 0;
        if (pRebars.Count > 0 && doc.GetElement(pRebars[0].GetTypeId()) is RebarBarType pBarType)
            barRadius = pBarType.BarModelDiameter / 2.0;
        const double eps = 0.0001;

        // Грани стены — получаем ПЕРВЫМИ, чтобы отфильтровать legRefs в пределах стены.
        var wallRefs = GetWallFaceReferencesAlong(wall, view, dimDir);
        if (wallRefs.Count < 2) return;
        wallRefs.Sort((a, b) => a.Coord.CompareTo(b.Coord));
        RefWithCoord wallLow = wallRefs.First();   // низ
        RefWithCoord wallHigh = wallRefs.Last();   // верх

        // Собираем ссылки из ВСЕХ представителей, фильтруем в пределах стены.
        var legRefs = new List<RefWithCoord>();
        foreach (Rebar pRebar in pRebars)
            legRefs.AddRange(GetLineReferencesAlong(pRebar, view, legDir, dimDir));

        if (legRefs.Count < 1) return;
        legRefs.Sort((a, b) => a.Coord.CompareTo(b.Coord));

        // Оставляем только ссылки внутри вертикального диапазона стены.
        // Это исключает стержни соседних (выше/ниже) стен сборки.
        legRefs = legRefs.Where(r => r.Coord >= wallLow.Coord - barRadius && r.Coord <= wallHigh.Coord + barRadius).ToList();
        if (legRefs.Count < 1) return;
        legRefs.Sort((a, b) => a.Coord.CompareTo(b.Coord));

        // ClosestRef чуть выше нижнего ребра → ось нижнего стержня (нижнего массива)
        RefWithCoord lowLeg  = ClosestRef(legRefs, legRefs.First().Coord + barRadius + eps);
        // ClosestRef чуть ниже верхнего ребра → ось верхнего стержня (верхнего массива)
        RefWithCoord highLeg = ClosestRef(legRefs, legRefs.Last().Coord  - barRadius - eps);

        // Дополнительная проверка: полки должны быть внутри стены.
        //if (lowLeg.Coord < wallLow.Coord || lowLeg.Coord > wallHigh.Coord) lowLeg = default;
        //if (highLeg.Coord < wallLow.Coord || highLeg.Coord > wallHigh.Coord) highLeg = default;

        // Поиск доп стержней (той же BI_позиции, без MRA) между гранью стены и полкой П-шки
        Rebar extraLower = null, extraUpper = null;
        if (allHosted != null && mraOk != null)
        {
            double MidCoord(Rebar r) {
                BoundingBoxXYZ bb = r.get_BoundingBox(null);
                return bb != null ? ((bb.Min + bb.Max) / 2.0).DotProduct(dimDir) : 0.0;
            }
            double tol = barRadius * 2 + UnitUtils.ConvertToInternalUnits(5, UnitTypeId.Millimeters);
            // Кандидаты: та же BI_позиция что у основного MRA-набора, но сам не вошедший в mraOk.
            // Явно исключаем сами П-шки: при Z-дедупликации (см. основной цикл) на одной высоте
            // может остаться «двойник» той же П-шки, не получивший MRA и не попавший в mraOk —
            // без этого исключения он ошибочно принимался за «доп. прямой стержень между полкой
            // и гранью стены», что давало нулевой (по факту паразитный) сегмент цепочки.
            string pPos = pRebars.Count > 0 ? pRebars[0].LookupParameter("BI_позиция")?.AsString() : null;
            var extraCandidates = allHosted
                .Where(r => !mraOk.Contains(r.Id))
                .Where(r => !IsPShape(ShapeName(doc, r)))
                .Where(r => string.IsNullOrEmpty(pPos) || r.LookupParameter("BI_позиция")?.AsString() == pPos)
                .ToList();

            // Доп стержень ниже П-шки: между низом стены и нижней полкой
            extraLower = extraCandidates
                .Where(r => { double c = MidCoord(r); return c > wallLow.Coord && c < lowLeg.Coord + tol; })
                .OrderByDescending(r => MidCoord(r))
                .FirstOrDefault();

            // Доп стержень выше П-шки: между верхней полкой и верхом стены
            extraUpper = extraCandidates
                .Where(r => { double c = MidCoord(r); return c < wallHigh.Coord && c > highLeg.Coord - tol; })
                .OrderBy(r => MidCoord(r))
                .FirstOrDefault();

        }

        // Линию размеров ставим на той же позиции, что и MRA (как для прямых стержней).
        XYZ basePt = ProjectOntoViewPlane(annotationPoint, view);
        double basePtOnDim = basePt.DotProduct(dimDir);

        // Текст смещаем диагонально в сторону марки — как в CreateCoverDimensions.
        const double TEXT_RIGHT_MM = 150.0;
        const double TEXT_UP_MM    = 150.0;
        double rightFt = UnitUtils.ConvertToInternalUnits(TEXT_RIGHT_MM, UnitTypeId.Millimeters);
        double upFt    = UnitUtils.ConvertToInternalUnits(TEXT_UP_MM,    UnitTypeId.Millimeters);

        XYZ up    = view.UpDirection;
        XYZ right = sideDir.Normalize();
        XYZ diagUR = up * upFt + right * rightFt;   // вверх + в сторону марки
        XYZ diagDR = -up * upFt + right * rightFt;  // вниз  + в сторону марки

        XYZ anchorLow  = basePt + dimDir * (wallLow.Coord  - basePtOnDim);
        XYZ anchorHigh = basePt + dimDir * (wallHigh.Coord - basePtOnDim);

        XYZ textPosLow, textPosHigh;
        if (anchorLow.DotProduct(up) <= anchorHigh.DotProduct(up))
        {
            textPosLow  = anchorLow  + diagUR;
            textPosHigh = anchorHigh + diagDR;
        }
        else
        {
            textPosLow  = anchorLow  + diagDR;
            textPosHigh = anchorHigh + diagUR;
        }

        // Направление стержня для цепочки: для П-шки — legDir, для прямого — его фактическая ось
        XYZ ExtraDir(Rebar r)
        {
            if (r == null || IsPShape(ShapeName(doc, r))) return legDir;
            IList<Curve> cc = r.GetCenterlineCurves(false, false, false, MultiplanarOption.IncludeOnlyPlanarCurves, 0);
            if (cc == null || cc.Count == 0) return legDir;
            GetRebarDirAndMid(cc, out XYZ d, out XYZ _);
            return d;
        }

        // Низ стены → нижняя полка (с цепочкой через доп стержень если есть)
        if (lowLeg.Reference != null)
        {
            if (extraLower != null)
                CreateChainCoverDim(doc, view, dimDir, ExtraDir(extraLower), basePt, sideDir,
                    wallLow, lowLeg, extraLower, dimType,
                    fallbackTextPos: textPosLow);
            else
                CreateOneCoverDim(doc, view, dimDir, basePt,
                    wallLow.Reference, wallLow.Coord, lowLeg.Reference, lowLeg.Coord,
                    dimType, textPosLow);
        }

        // Верхняя полка → верх стены (с цепочкой через доп стержень если есть)
        if (highLeg.Reference != null && !skipUpperAnchor)
        {
            if (extraUpper != null)
                CreateChainCoverDim(doc, view, dimDir, ExtraDir(extraUpper), basePt, sideDir,
                    wallHigh, highLeg, extraUpper, dimType,
                    fallbackTextPos: textPosHigh);
            else
                CreateOneCoverDim(doc, view, dimDir, basePt,
                    highLeg.Reference, highLeg.Coord, wallHigh.Reference, wallHigh.Coord,
                    dimType, textPosHigh);
        }
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
        Document doc, View view, Rebar rebar, Wall wall,
        XYZ annotationPoint, DimensionType dimType, XYZ sideDir,
        bool createLower = true, bool createUpper = true,
        Rebar extraUpperRebar = null, Rebar extraLowerRebar = null,
        List<Rebar> nativePool = null,
        List<Rebar> allZReps = null)
    {
        IList<Curve> curves = rebar.GetCenterlineCurves(
            false, false, false, MultiplanarOption.IncludeOnlyPlanarCurves, 0);
        if (curves == null || curves.Count == 0) return;

        GetRebarDirAndMid(curves, out XYZ rebarDir, out XYZ _);

        XYZ dimDir = SafeCrossProduct(rebarDir, view.ViewDirection);
        if (dimDir == null) return;

        var barRefs = GetLineReferencesAlong(rebar, view, rebarDir, dimDir);

        // Если основной стержень не имеет пригодной геометрии (диагональная схема),
        // ищем замену среди нативных хост-стержней стены той же ориентации.
        Rebar geomRebar = rebar;
        if (barRefs.Count < 1 && nativePool != null)
        {
            foreach (Rebar candidate in nativePool)
            {
                if (candidate.Id == rebar.Id) continue;
                IList<Curve> cc = candidate.GetCenterlineCurves(false, false, false, MultiplanarOption.IncludeOnlyPlanarCurves, 0);
                if (cc == null || cc.Count == 0) continue;
                GetRebarDirAndMid(cc, out XYZ cDir, out XYZ _);
                if (Math.Abs(cDir.DotProduct(rebarDir)) < 0.99) continue;
                var cRefs = GetLineReferencesAlong(candidate, view, rebarDir, dimDir);
                if (cRefs.Count > 0)
                {
                    barRefs = cRefs;
                    geomRebar = candidate;
                    break;
                }
            }
        }

        if (barRefs.Count < 1) return;

        double firstAxis = BarPositionCoord(geomRebar, 0, dimDir);
        double barRadius = 0;
        if (doc.GetElement(geomRebar.GetTypeId()) is RebarBarType barType)
            barRadius = barType.BarModelDiameter / 2.0;

        RefWithCoord farthest = barRefs
            .OrderByDescending(r => Math.Abs(r.Coord - firstAxis))
            .First();
        double sideSign = Math.Sign(farthest.Coord - firstAxis);
        if (sideSign == 0) sideSign = 1;
        double lastAxis = farthest.Coord - sideSign * barRadius;

        RefWithCoord firstBar = ClosestRef(barRefs, firstAxis);
        RefWithCoord lastBar  = ClosestRef(barRefs, lastAxis);
        var wallRefs = GetWallFaceReferencesAlong(wall, view, dimDir);
        if (wallRefs.Count < 2) return;
        wallRefs.Sort((a, b) => a.Coord.CompareTo(b.Coord));

        bool dimDirIsUp = dimDir.DotProduct(view.UpDirection) >= 0;
        RefWithCoord wallBottom = dimDirIsUp ? wallRefs.First() : wallRefs.Last();
        RefWithCoord wallTop    = dimDirIsUp ? wallRefs.Last()  : wallRefs.First();

        RefWithCoord barBottom, barTop;
        if (dimDirIsUp)
        {
            barBottom = firstBar.Coord <= lastBar.Coord ? firstBar : lastBar;
            barTop    = firstBar.Coord <= lastBar.Coord ? lastBar  : firstBar;
        }
        else
        {
            barBottom = firstBar.Coord >= lastBar.Coord ? firstBar : lastBar;
            barTop    = firstBar.Coord >= lastBar.Coord ? lastBar  : firstBar;
        }
        // Если есть несколько Z-слоёв — уточняем barBottom из нижнего, barTop из верхнего,
        // чтобы размер охватывал всю высоту армирования, а не только один массив.
        if (allZReps != null && allZReps.Count > 1)
        {
            Rebar lowerRep = allZReps.First();
            Rebar upperRep = allZReps.Last();

            if (lowerRep.Id != geomRebar.Id && (extraLowerRebar == null || lowerRep.Id != extraLowerRebar.Id))
            {
                Rebar lowerGeom = lowerRep;
                var lowerRefs = GetLineReferencesAlong(lowerRep, view, rebarDir, dimDir);
                if (lowerRefs.Count == 0 && nativePool != null)
                {
                    foreach (Rebar c in nativePool)
                    {
                        if (c.Id == lowerRep.Id) continue;
                        IList<Curve> cc = c.GetCenterlineCurves(false, false, false, MultiplanarOption.IncludeOnlyPlanarCurves, 0);
                        if (cc == null || cc.Count == 0) continue;
                        GetRebarDirAndMid(cc, out XYZ cD, out XYZ _);
                        if (Math.Abs(cD.DotProduct(rebarDir)) < 0.99) continue;
                        lowerRefs = GetLineReferencesAlong(c, view, rebarDir, dimDir);
                        if (lowerRefs.Count > 0) { lowerGeom = c; break; }
                    }
                }
                if (lowerRefs.Count > 0)
                {
                    // Ось крайнего стержня нижнего z-слоя (аналогично firstAxis в основном блоке)
                    double lowerExtremeAxis = dimDirIsUp ? double.MaxValue : double.MinValue;
                    for (int ii = 0; ii < lowerGeom.NumberOfBarPositions; ii++)
                    {
                        double c = BarPositionCoord(lowerGeom, ii, dimDir);
                        lowerExtremeAxis = dimDirIsUp ? Math.Min(lowerExtremeAxis, c) : Math.Max(lowerExtremeAxis, c);
                    }
                    if (double.IsInfinity(lowerExtremeAxis))
                        lowerExtremeAxis = lowerRefs.OrderBy(r => dimDirIsUp ? r.Coord : -r.Coord).First().Coord;
                    barBottom = ClosestRef(lowerRefs, lowerExtremeAxis);
                }
            }

            if (upperRep.Id != geomRebar.Id && (extraUpperRebar == null || upperRep.Id != extraUpperRebar.Id))
            {
                Rebar upperGeom = upperRep;
                var upperRefs = GetLineReferencesAlong(upperRep, view, rebarDir, dimDir);
                if (upperRefs.Count == 0 && nativePool != null)
                {
                    foreach (Rebar c in nativePool)
                    {
                        if (c.Id == upperRep.Id) continue;
                        IList<Curve> cc = c.GetCenterlineCurves(false, false, false, MultiplanarOption.IncludeOnlyPlanarCurves, 0);
                        if (cc == null || cc.Count == 0) continue;
                        GetRebarDirAndMid(cc, out XYZ cD, out XYZ _);
                        if (Math.Abs(cD.DotProduct(rebarDir)) < 0.99) continue;
                        upperRefs = GetLineReferencesAlong(c, view, rebarDir, dimDir);
                        if (upperRefs.Count > 0) { upperGeom = c; break; }
                    }
                }
                if (upperRefs.Count > 0)
                {
                    double upperExtremeAxis = dimDirIsUp ? double.MinValue : double.MaxValue;
                    for (int ii = 0; ii < upperGeom.NumberOfBarPositions; ii++)
                    {
                        double c = BarPositionCoord(upperGeom, ii, dimDir);
                        upperExtremeAxis = dimDirIsUp ? Math.Max(upperExtremeAxis, c) : Math.Min(upperExtremeAxis, c);
                    }
                    if (double.IsInfinity(upperExtremeAxis))
                        upperExtremeAxis = upperRefs.OrderByDescending(r => dimDirIsUp ? r.Coord : -r.Coord).First().Coord;
                    barTop = ClosestRef(upperRefs, upperExtremeAxis);
                }
            }
        }

        // 5. Базовая точка линии размера — на оффсете аннотации.
        //    Линия «50» остаётся НА ТОЙ ЖЕ ОСИ, что и основной размер «3000».
        XYZ basePt = ProjectOntoViewPlane(annotationPoint, view);
        double basePtOnDim = basePt.DotProduct(dimDir);

        // Надпись «50» смещаем по диагонали в координатах ВИДА (однозначные «вверх/вправо»):
        // нижний размер → вверх-вправо, верхний → вниз-вправо.
        const double TEXT_RIGHT_MM = 150.0; // по горизонтали вида (RightDirection)
        const double TEXT_UP_MM = 150.0;    // по вертикали вида (UpDirection)

        double rightFt = UnitUtils.ConvertToInternalUnits(TEXT_RIGHT_MM, UnitTypeId.Millimeters);
        double upFt = UnitUtils.ConvertToInternalUnits(TEXT_UP_MM, UnitTypeId.Millimeters);

        XYZ up = view.UpDirection;
        XYZ right = sideDir.Normalize();           // горизонталь — на выбранную сторону
        XYZ diagUR = up * upFt + right * rightFt;  // вверх-в сторону (для нижнего)
        XYZ diagDR = -up * upFt + right * rightFt; // вниз-в сторону (для верхнего)

        // Точки на линии размера у торцов стены (по физическому низу/верху)
        XYZ anchorLow  = basePt + dimDir * (wallBottom.Coord - basePtOnDim);
        XYZ anchorHigh = basePt + dimDir * (wallTop.Coord    - basePtOnDim);

        // Нижний на экране → вверх-вправо, верхний на экране → вниз-вправо.
        XYZ textPosLow, textPosHigh;
        if (anchorLow.DotProduct(up) <= anchorHigh.DotProduct(up))
        {
            textPosLow  = anchorLow  + diagUR;
            textPosHigh = anchorHigh + diagDR;
        }
        else
        {
            textPosLow  = anchorLow  + diagDR;
            textPosHigh = anchorHigh + diagUR;
        }

        // 6. Размеры защитного слоя (с цепочкой через доп стержень, если задан)
        if (createLower)
        {
            if (extraLowerRebar != null)
                CreateChainCoverDim(doc, view, dimDir, rebarDir, basePt, sideDir,
                    wallBottom, barBottom, extraLowerRebar, dimType,
                    fallbackTextPos: textPosLow);
            else
                CreateOneCoverDim(doc, view, dimDir, basePt,
                    wallBottom.Reference, wallBottom.Coord, barBottom.Reference, barBottom.Coord,
                    dimType, textPosLow);
        }

        if (createUpper)
        {
            if (extraUpperRebar != null)
                CreateChainCoverDim(doc, view, dimDir, rebarDir, basePt, sideDir,
                    wallTop, barTop, extraUpperRebar, dimType,
                    fallbackTextPos: textPosHigh);
            else
                CreateOneCoverDim(doc, view, dimDir, basePt,
                    barTop.Reference, barTop.Coord, wallTop.Reference, wallTop.Coord,
                    dimType, textPosHigh);
        }
    }

    // Сбор линейных ссылок, идущих вдоль rebarDir (⟂ dimDir).
    // Ключ: ComputeReferences + IncludeNonVisibleObjects одновременно — иначе Reference == null.
    private static List<RefWithCoord> GetLineReferencesAlong(
        Element elem, View view, XYZ rebarDir, XYZ dimDir)
    {
        var result = new List<RefWithCoord>();

        Options opt = new Options
        {
            View = view,
            ComputeReferences = true,
            IncludeNonVisibleObjects = true
        };

        GeometryElement geom = elem.get_Geometry(opt);
        if (geom == null) return result;

        int before = result.Count;
        CollectParallelLineRefs(geom, rebarDir, dimDir, result);

        if (result.Count == before)
        {
            Options optNoView = new Options { ComputeReferences = true, IncludeNonVisibleObjects = true };
            GeometryElement geomNoView = elem.get_Geometry(optNoView);
            if (geomNoView != null)
                CollectParallelLineRefs(geomNoView, rebarDir, dimDir, result);
        }

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
        Element elem, View view, XYZ dimDir)
    {
        var result = new List<RefWithCoord>();

        Options opt = new Options
        {
            View = view,
            ComputeReferences = true,
            IncludeNonVisibleObjects = false
        };

        GeometryElement geom = elem.get_Geometry(opt);
        if (geom == null) return result;

        CollectFacesAlong(geom, dimDir, result);

        return result;
    }

    // Торец стержня — верхнеуровневая линия его геометрии, ПЕРПЕНДИКУЛЯРНАЯ оси
    // (rebarDir), а не параллельная ей. Solid пропускается, рекурсия в
    // GeometryInstance не нужна — тот же приём, что в RebarAnnotation.cs для
    // продольных размеров (там он подтверждённо работает). wantMax=true — верхний
    // торец (наибольшая координата вдоль rebarDir), false — нижний.
    private static RefWithCoord? GetRebarEndRefPerp(Rebar rebar, View view, XYZ rebarDir, bool wantMax = true)
    {
        Options opt = new Options { View = view, ComputeReferences = true, IncludeNonVisibleObjects = true };
        GeometryElement geom = rebar.get_Geometry(opt);
        if (geom == null) return null;

        RefWithCoord? best = null;
        foreach (GeometryObject go in geom)
        {
            if (go is Solid) continue;
            if (!(go is Line ln) || ln.Reference == null) continue;

            XYZ lnDir = (ln.GetEndPoint(1) - ln.GetEndPoint(0)).Normalize();
            if (Math.Abs(lnDir.DotProduct(rebarDir)) >= 0.3) continue; // нужна линия ПОПЕРЁК стержня

            XYZ mid = (ln.GetEndPoint(0) + ln.GetEndPoint(1)) / 2.0;
            double coord = mid.DotProduct(rebarDir);
            if (best == null || (wantMax ? coord > best.Value.Coord : coord < best.Value.Coord))
                best = new RefWithCoord { Reference = ln.Reference, Coord = coord };
        }
        return best;
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

    // Ищет ссылку на центральную ось стержня через геометрию (Line-объект с Reference).
    // Рекурсивно заходит в GeometryInstance — именно там обычно хранится осевая линия.
    // Если не найдена — возвращает null.
    // Цепочка: грань стены → центр доп стержня → грань крайнего стержня массива.
    // Текст сегмента 0 (стена→доп): вниз + sideDir.
    // Текст сегмента 1 (доп→массив): вниз - sideDir.
    private static void CreateChainCoverDim(
        Document doc, View view, XYZ dimDir, XYZ rebarDir, XYZ basePt, XYZ sideDir,
        RefWithCoord wallFace, RefWithCoord mainBar,
        Rebar extraRebar,
        DimensionType dimType,
        XYZ fallbackTextPos = null)
    {
        // Середина диапазона [wallFace, mainBar] — запасная координата для выбора ГРАНИ
        // П-шки (см. fallback ниже, там обе полки equidistant от центра стержня).
        double midRangeFt = (wallFace.Coord + mainBar.Coord) * 0.5;

        // Ссылка на ось доп стержня: если extraRebar — массив с несколькими позициями
        // (напр. 2 стержня), нужна КРАЙНЯЯ позиция, ближайшая к грани стены. GetRebarAxisRef
        // сканирует сырую геометрию (CollectAxisCandidates) и не всегда отдаёт отдельную
        // ссылку на КАЖДУЮ позицию массива — тогда «ближайшая к цели» на деле выбирает то,
        // что вообще нашлось, а не то, что реально ближе к стене. Вместо этого считаем
        // крайнюю координату точно через официальный API позиций (BarPositionCoord, как для
        // barTop/barBottom в CreateCoverDimensions), затем ищем ближайшую к ней ссылку среди
        // GetLineReferencesAlong — так же надёжно, как уже работает для главного массива.
        RefWithCoord? axisRefNullable = null;
        {
            var extraLineRefs = GetLineReferencesAlong(extraRebar, view, rebarDir, dimDir);
            if (extraLineRefs.Count > 0)
            {
                bool towardHigher = wallFace.Coord > mainBar.Coord;
                double extremeAxis = towardHigher ? double.MinValue : double.MaxValue;
                for (int ii = 0; ii < extraRebar.NumberOfBarPositions; ii++)
                {
                    double c = BarPositionCoord(extraRebar, ii, dimDir);
                    extremeAxis = towardHigher ? Math.Max(extremeAxis, c) : Math.Min(extremeAxis, c);
                }
                if (double.IsInfinity(extremeAxis))
                    extremeAxis = extraLineRefs.OrderByDescending(r => towardHigher ? r.Coord : -r.Coord).First().Coord;
                axisRefNullable = ClosestRef(extraLineRefs, extremeAxis);
            }
        }
        RefWithCoord extraRef;
        if (axisRefNullable.HasValue)
        {
            extraRef = axisRefNullable.Value;
        }
        else
        {
            // Fallback: берём грань ближайшую к оси стержня
            var faceRefs = GetLineReferencesAlong(extraRebar, view, rebarDir, dimDir);
            double axisCoord = BarPositionCoord(extraRebar, 0, dimDir);
            double tol200 = UnitUtils.ConvertToInternalUnits(200, UnitTypeId.Millimeters);
            // Отбрасываем хуки: оставляем только ссылки в пределах 200мм от оси.
            var nearFaceRefs = faceRefs.Where(r => Math.Abs(r.Coord - axisCoord) <= tol200).ToList();
            if (nearFaceRefs.Count == 0)
            {
                CreateOneCoverDim(doc, view, dimDir, basePt,
                    wallFace.Reference, wallFace.Coord, mainBar.Reference, mainBar.Coord,
                    dimType, fallbackTextPos);
                return;
            }
            // Выбираем полку ближайшую к СЕРЕДИНЕ диапазона [wallFace, mainBar].
            // Для П-шки обе полки equidistant от геометрического центра стержня,
            // поэтому (minC+maxC)/2 даёт случайный результат. Середина диапазона
            // устойчиво выбирает полку, не совпадающую с wallFace (устраняет 0-размер).
            extraRef = nearFaceRefs.OrderBy(r => Math.Abs(r.Coord - midRangeFt)).First();
        }

        // Три ссылки в порядке возрастания Coord
        var pts = new (double Coord, Reference Ref)[]
        {
            (wallFace.Coord, wallFace.Reference),
            (extraRef.Coord,  extraRef.Reference),
            (mainBar.Coord,   mainBar.Reference)
        };
        var ordered = pts.OrderBy(p => p.Coord).ToArray();

        double margin = UnitUtils.ConvertToInternalUnits(20, UnitTypeId.Millimeters);
        double baseOnDim = basePt.DotProduct(dimDir);
        XYZ p1 = basePt + dimDir * (ordered[0].Coord - baseOnDim - margin);
        XYZ p2 = basePt + dimDir * (ordered[2].Coord - baseOnDim + margin);
        if ((p2 - p1).GetLength() < 1e-6) return;

        Line dimLine;
        try { dimLine = Line.CreateBound(p1, p2); }
        catch { return; }

        var ra = new ReferenceArray();
        foreach (var pt in ordered) ra.Append(pt.Ref);

        try
        {
            Dimension dim = dimType != null
                ? doc.Create.NewDimension(view, dimLine, ra, dimType)
                : doc.Create.NewDimension(view, dimLine, ra);

            // Текст каждого сегмента — тот же оффсет выноски, что и у одиночной привязки
            // (CreateOneCoverDim / textPosLow,textPosHigh), чтобы верхняя и нижняя привязка
            // выглядели одинаково независимо от того, есть ли доп. стержень (цепочка) или нет.
            const double TEXT_MM = 150.0;
            double textFt = UnitUtils.ConvertToInternalUnits(TEXT_MM, UnitTypeId.Millimeters);
            XYZ up    = view.UpDirection;
            XYZ right = sideDir.Normalize();

            // Вертикальная сторона смещения — как в CreatePShapeAnchorDimensions (textPosLow/
            // textPosHigh): текст уходит К СЕРЕДИНЕ стены, а не всегда вниз. Для нижней привязки
            // (mainBar/полка выше wallFace/грани) это «вверх», для верхней — «вниз»; раньше здесь
            // было жёстко «вниз» для обеих привязок, из-за чего у верхней и нижней привязки текст
            // и выноска смещались по-разному.
            double vSign = Math.Sign(mainBar.Coord - wallFace.Coord);
            if (vSign == 0) vSign = 1;
            XYZ vert = up * vSign;

            // Сег 0 (mainBar → доп стержень, «дальний» от грани стены сегмент, обычно
            // бОльшее число — «100») — дополнительно смещаем на 50мм к середине стены и
            // в сторону, чтобы текст не наезжал на перекрестье у грани стены/сегмента «50».
            const double SEG0_EXTRA_MM = 50.0;
            double seg0ExtraFt = UnitUtils.ConvertToInternalUnits(SEG0_EXTRA_MM, UnitTypeId.Millimeters);
            double mid0 = (ordered[0].Coord + ordered[1].Coord) / 2.0;
            XYZ pt0 = basePt + dimDir * (mid0 - baseOnDim)
                + vert * (textFt + seg0ExtraFt) - right * (textFt + seg0ExtraFt);

            // Сег 1 (доп стержень → крайний массив): к середине стены + sideDir (вправо)
            double mid1 = (ordered[1].Coord + ordered[2].Coord) / 2.0;
            XYZ pt1 = basePt + dimDir * (mid1 - baseOnDim) + vert * textFt + right * textFt;

            if (dim.Segments.Size >= 2)
            {
                doc.Regenerate();
                try { dim.Segments.get_Item(0).TextPosition = pt0; } catch { }
                try { dim.Segments.get_Item(1).TextPosition = pt1; } catch { }
            }

        }
        catch (Exception)
        {
            CreateOneCoverDim(doc, view, dimDir, basePt,
                wallFace.Reference, wallFace.Coord, mainBar.Reference, mainBar.Coord,
                dimType, fallbackTextPos);
        }
    }

    private static void CreateOneCoverDim(
        Document doc, View view, XYZ dimDir, XYZ basePt,
        Reference rLow, double coordLow,
        Reference rHigh, double coordHigh,
        DimensionType dimType, XYZ textPos,
        List<string> warnings = null)
    {
        double baseOnDim = basePt.DotProduct(dimDir);
        double margin = UnitUtils.ConvertToInternalUnits(20, UnitTypeId.Millimeters);

        // Линия размера вдоль dimDir на оффсете basePt, с запасом по краям.
        // Reference'ы Revit спроецирует на неё сам.
        XYZ p1 = basePt + dimDir * (coordLow - baseOnDim - margin);
        XYZ p2 = basePt + dimDir * (coordHigh - baseOnDim + margin);

        if ((p2 - p1).GetLength() < 1e-6)
        {
            warnings?.Add("    CreateOneCoverDim: длина линии размера ~0 — пропущено.");
            return;
        }

        Line dimLine;
        try { dimLine = Line.CreateBound(p1, p2); }
        catch (Exception ex) { warnings?.Add($"    CreateOneCoverDim: Line.CreateBound: {ex.Message}"); return; }

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
                warnings?.Add("    CreateOneCoverDim: NewDimension вернул null.");
                return;
            }
            warnings?.Add("    CreateOneCoverDim: размер создан.");

            // Смещаем надпись — линия размера остаётся на оси,
            // Revit рисует полку-выноску к смещённому тексту.
            if (textPos != null)
            {
                try
                {
                    doc.Regenerate();
                    dim.HasLeader = true;
                    dim.TextPosition = textPos;
                }
                catch { }
            }
        }
        catch (Exception ex) { warnings?.Add($"    CreateOneCoverDim: NewDimension: {ex.Message}"); }
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
        XYZ R = view.RightDirection, U = view.UpDirection, N = view.ViewDirection;
        IList<Curve> cs = rb.GetCenterlineCurves(
            false, false, false, MultiplanarOption.IncludeOnlyPlanarCurves, 0);
        // Для равносторонних П-шек (А=С) Revit может вернуть пустой список — пробуем без подавления
        if (cs == null || cs.Count == 0)
            cs = rb.GetCenterlineCurves(true, true, false, MultiplanarOption.IncludeOnlyPlanarCurves, 0);
        if (cs == null || cs.Count == 0)
        {
            // Последний фолбэк: по габариту стержня — горизонтальная П, если вытянута вглубь стены
            BoundingBoxXYZ bb = rb.get_BoundingBox(null);
            if (bb == null) return false;
            XYZ sz = bb.Max - bb.Min;
            double dN2 = Math.Abs(sz.DotProduct(N));
            double dU2 = Math.Abs(sz.DotProduct(U));
            return dN2 > 0.01 && dN2 >= dU2;
        }

        int rCount = 0, uCount = 0;
        foreach (Curve c in cs)
        {
            if (!(c is Line)) continue;
            XYZ d = (c.GetEndPoint(1) - c.GetEndPoint(0)).Normalize();
            double dotN = Math.Abs(d.DotProduct(N));
            // Сегменты, идущие вглубь стены, не влияют на классификацию горизонтальности
            if (dotN > 0.9) continue;
            if (Math.Abs(d.DotProduct(R)) >= Math.Abs(d.DotProduct(U))) rCount++;
            else uCount++;
        }
        // >= вместо > : равносторонние П-шки могут давать rCount == uCount
        return rCount >= uCount;
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

    private static bool IsPShape(string shapeName) =>
        P_SHAPE_NAMES.Contains(shapeName)
        || shapeName.IndexOf("П-шка", StringComparison.OrdinalIgnoreCase) >= 0;

    private static bool IsStirrupShape(string shapeName) =>
        shapeName != null
        && shapeName.IndexOf("хомут", StringComparison.OrdinalIgnoreCase) >= 0;

    // Стержень считается «в обрезке», если его габарит целиком попадает в рамку
    // Проверяет, что оба представителя находятся в Z-диапазоне стены [botZ, topZ].
    // Используется перед созданием размера между массивами.
    private static bool BothInWall(Rebar a, Rebar b, double botZ, double topZ, double tol = 0.033)
    {
        double MidZ(Rebar r) {
            BoundingBoxXYZ bb = r.get_BoundingBox(null);
            return bb != null ? (bb.Min.Z + bb.Max.Z) / 2.0 : double.NaN;
        }
        double zA = MidZ(a), zB = MidZ(b);
        return zA >= botZ - tol && zA <= topZ + tol
            && zB >= botZ - tol && zB <= topZ + tol;
    }

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
        View view, Rebar rebar, double offsetMm, XYZ sideDir,
        Wall targetWall)
    {
        IList<Curve> curves = rebar.GetCenterlineCurves(
            false, false, false, MultiplanarOption.IncludeOnlyPlanarCurves, 0);
        if (curves == null || curves.Count == 0) return null;

        XYZ outDir = sideDir.Normalize();

        double wallEdge = WallExtentAlong(targetWall, view, outDir);
        if (double.IsNegativeInfinity(wallEdge)) return null;

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
        Wall wall, View view, XYZ dir)
    {
        double maxProj = double.NegativeInfinity;

        Options opt = new Options
        {
            View = view,
            ComputeReferences = false,
            IncludeNonVisibleObjects = false
        };

        GeometryElement geom = wall.get_Geometry(opt);
        if (geom == null) return maxProj;

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

public class LoggingPreprocessor : IFailuresPreprocessor
{
    public FailureProcessingResult PreprocessFailures(FailuresAccessor a)
        => FailureProcessingResult.Continue;
}