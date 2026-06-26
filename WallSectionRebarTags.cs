using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

namespace DAN_Plugin
{
    [Transaction(TransactionMode.Manual)]
    public class WallSectionRebarTagger : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uiDoc = commandData.Application.ActiveUIDocument;
            Document doc = uiDoc.Document;

            ViewSection sectionView = doc.ActiveView as ViewSection;
            if (sectionView == null)
            {
                TaskDialog.Show("Ошибка", "Активный вид должен быть разрезом стены.");
                return Result.Failed;
            }

            Wall wall;
            try
            {
                Reference pickedRef = uiDoc.Selection.PickObject(
                    ObjectType.Element,
                    new WallFilter(),
                    "Выберите стену для расстановки марок арматуры");
                wall = doc.GetElement(pickedRef) as Wall;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled;
            }

            if (wall == null) return Result.Failed;

            // Диалог выбора стороны расстановки марок
            TaskDialog placeDlg = new TaskDialog("Марки арматуры");
            placeDlg.MainInstruction = "Расположение марок";
            placeDlg.MainContent = "С какой стороны стены создать марки арматуры?";
            placeDlg.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Сверху стены");
            placeDlg.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Снизу стены");
            placeDlg.CommonButtons = TaskDialogCommonButtons.Cancel;

            TaskDialogResult dlgResult = placeDlg.Show();
            bool placeAbove;
            if      (dlgResult == TaskDialogResult.CommandLink1) placeAbove = true;
            else if (dlgResult == TaskDialogResult.CommandLink2) placeAbove = false;
            else return Result.Cancelled;

            using (Transaction tx = new Transaction(doc, "Марки арматуры разреза"))
            {
                tx.Start();
                try
                {
                    PlaceRebarTags(doc, sectionView, wall, sectionView.RightDirection, placeAbove);
                    tx.Commit();
                }
                catch (Exception ex)
                {
                    tx.RollBack();
                    message = ex.Message;
                    return Result.Failed;
                }
            }

            return Result.Succeeded;
        }

        private static void PlaceRebarTags(Document doc, ViewSection sectionView, Wall wall, XYZ viewRight, bool placeAbove)
        {
            if (sectionView == null || wall == null) return;

            LocationCurve lc = wall.Location as LocationCurve;
            Line wallLine = lc?.Curve as Line;
            if (wallLine == null)
                throw new InvalidOperationException("Стена не является прямой.");

            XYZ wallDir = wallLine.Direction.Normalize();
            XYZ viewRightH = new XYZ(viewRight.X, viewRight.Y, 0);
            if (viewRightH.GetLength() > 1e-9 && wallDir.DotProduct(viewRightH.Normalize()) < 0)
                wallDir = wallDir.Negate();
            XYZ upDir = XYZ.BasisZ.Negate().CrossProduct(wallDir).Normalize();
            double cutZ = sectionView.Origin.Z;

            // ── Сбор арматуры ──────────────────────────────────────────────
            var allRebar = new FilteredElementCollector(doc, sectionView.Id)
                .OfCategory(BuiltInCategory.OST_Rebar)
                .WhereElementIsNotElementType()
                .ToList();

            // Фильтр по марке конструкции: берём только арматуру, у которой
            // BI_марка_конструкции совпадает с «Комментарием» сборки стены.
            string targetMark = null;
            if (wall.AssemblyInstanceId != ElementId.InvalidElementId)
            {
                AssemblyInstance asm = doc.GetElement(wall.AssemblyInstanceId) as AssemblyInstance;
                targetMark = asm?.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)
                                 ?.AsString();
            }

            if (!string.IsNullOrEmpty(targetMark))
            {
                allRebar = allRebar
                    .Where(rb => rb.LookupParameter("BI_марка_конструкции")
                                   ?.AsString() == targetMark)
                    .ToList();
            }

            if (!allRebar.Any())
                throw new InvalidOperationException(
                    $"В виде \"{sectionView.Name}\" не найдено арматуры" +
                    (targetMark != null ? $" с BI_марка_конструкции = \"{targetMark}\"" : "") + ".");

            // Каждая запись — один стержень массива (первый или последний).
            // Reference хранит конкретный subelement для правильного тега.
            var items  = new List<(Element elem, Reference tagRef, int pos, double diam, double wProj, double uProj)>();
            var hItems = new List<(Element elem, Reference tagRef, int pos, double diam, double wProj, double uProj)>();
            int skippedNoPos = 0;

            foreach (Element rb in allRebar)
            {
                Parameter posP = rb.LookupParameter("BI_позиция");
                if (posP == null) { skippedNoPos++; continue; }

                int pos;
                if (posP.StorageType == StorageType.Integer)
                    pos = posP.AsInteger();
                else if (!int.TryParse(posP.AsString(), out pos))
                    continue;

                double diam = 0;
                foreach (string pn in new[] { "Диаметр стержня", "Диаметр", "Bar Diameter" })
                {
                    Parameter dp = rb.LookupParameter(pn)
                        ?? doc.GetElement(rb.GetTypeId())?.LookupParameter(pn);
                    if (dp != null && dp.StorageType == StorageType.Double)
                    { diam = dp.AsDouble(); break; }
                }

                Rebar rebar = rb as Rebar;
                if (rebar == null) continue;

                IList<Subelement> subs = rebar.GetSubelements();
                int nBars    = Math.Max(1, rebar.NumberOfBarPositions);
                int subsCount = subs?.Count ?? 0;

                // ── Позиция первого стержня ────────────────────────────────
                XYZ firstBarPt = null;
                bool isVertical = false;
                bool isStraightHorizontal = false; // прямой стержень вдоль стены (форма 1)
                try
                {
                    IList<Curve> c0 = rebar.GetCenterlineCurves(
                        false, false, false, MultiplanarOption.IncludeOnlyPlanarCurves, 0);
                    if (c0?.Count > 0)
                    {
                        firstBarPt = c0[0].Evaluate(0.5, true);
                        if (c0[0] is Line bl)
                        {
                            isVertical = Math.Abs(bl.Direction.Z) > 0.9;
                            // Форма 1: прямой стержень, направление вдоль стены (не вертикальный)
                            isStraightHorizontal = !isVertical
                                && Math.Abs(bl.Direction.DotProduct(wallDir)) > 0.7;
                        }
                    }
                }
                catch { }

                if (!isVertical)
                {
                    // Горизонтальные прямые стержни (форма 1): собираем для отдельного тегирования
                    if (isStraightHorizontal && firstBarPt != null)
                    {
                        Reference refH = subsCount > 0 ? subs[0].GetReference() : null;
                        if (refH == null) { try { refH = new Reference(rebar); } catch { } }
                        if (refH != null)
                            hItems.Add((rb, refH, pos, diam,
                                        firstBarPt.DotProduct(wallDir),
                                        firstBarPt.DotProduct(upDir)));
                    }
                    continue; // только вертикальные стержни идут в items
                }

                if (firstBarPt == null)
                {
                    BoundingBoxXYZ bb0 = rb.get_BoundingBox(sectionView) ?? rb.get_BoundingBox(null);
                    if (bb0 != null) firstBarPt = (bb0.Min + bb0.Max) / 2.0;
                }
                if (firstBarPt == null) continue;

                Reference ref0 = subsCount > 0 ? subs[0].GetReference() : null;
                if (ref0 == null) { try { ref0 = new Reference(rebar); } catch { } }
                if (ref0 == null) continue;

                items.Add((rb, ref0, pos, diam,
                           firstBarPt.DotProduct(wallDir),
                           firstBarPt.DotProduct(upDir)));

                if (nBars <= 1) continue; // одиночный стержень — последнего нет

                // ── Позиция последнего стержня ─────────────────────────────
                XYZ lastBarPt = null;

                // 1. GetCenterlineCurves для конкретного barIdx
                try
                {
                    IList<Curve> cN = rebar.GetCenterlineCurves(
                        false, false, false, MultiplanarOption.IncludeOnlyPlanarCurves, nBars - 1);
                    if (cN?.Count > 0)
                    {
                        XYZ pt = cN[0].Evaluate(0.5, true);
                        if (pt.DistanceTo(firstBarPt) > 1e-4) lastBarPt = pt;
                    }
                }
                catch { }

                // 2. GetBarPositionTransform — смещение через transform
                if (lastBarPt == null)
                {
                    try
                    {
                        RebarShapeDrivenAccessor acc = rebar.GetShapeDrivenAccessor();
                        if (acc != null)
                        {
                            Transform t0 = acc.GetBarPositionTransform(0);
                            Transform tN = acc.GetBarPositionTransform(nBars - 1);
                            XYZ offset   = tN.Origin - t0.Origin;
                            if (offset.GetLength() > 1e-4) lastBarPt = firstBarPt + offset;
                        }
                    }
                    catch { }
                }

                // 3. Bbox зеркалирование
                if (lastBarPt == null)
                {
                    BoundingBoxXYZ bbN = rb.get_BoundingBox(sectionView) ?? rb.get_BoundingBox(null);
                    if (bbN != null)
                    {
                        double wCenter = ((bbN.Min + bbN.Max) / 2.0).DotProduct(wallDir);
                        double wFirst  = firstBarPt.DotProduct(wallDir);
                        double wLast   = 2.0 * wCenter - wFirst;
                        double uFirst  = firstBarPt.DotProduct(upDir);
                        if (Math.Abs(wLast - wFirst) > 1e-4)
                            lastBarPt = wallDir.Multiply(wLast) + upDir.Multiply(uFirst)
                                        + XYZ.BasisZ.Multiply(cutZ);
                    }
                }

                if (lastBarPt == null) continue;

                // Reference для последнего стержня
                Reference refN = subsCount > 1 ? subs[subsCount - 1].GetReference()
                               : subsCount > 0 ? subs[0].GetReference()
                               : ref0;

                items.Add((rb, refN, pos, diam,
                           lastBarPt.DotProduct(wallDir),
                           lastBarPt.DotProduct(upDir)));
            }

            if (!items.Any())
                throw new InvalidOperationException(
                    skippedNoPos > 0
                        ? $"Найдено {allRebar.Count} стержней, но у всех отсутствует параметр BI_позиция."
                        : $"Найдено {allRebar.Count} стержней, но ни один не прошёл фильтрацию.");


            // ── Типы марок ─────────────────────────────────────────────────
            FamilySymbol tagNoShelf = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .OfCategory(BuiltInCategory.OST_RebarTags)
                .Cast<FamilySymbol>()
                .FirstOrDefault(fs => fs.Name.Equals("Позиция_(_)_без полки", StringComparison.OrdinalIgnoreCase));

            FamilySymbol tagStd = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .OfCategory(BuiltInCategory.OST_RebarTags)
                .Cast<FamilySymbol>()
                .FirstOrDefault(fs =>
                    fs.Family.Name.Equals("(марка)арматурный_стержень", StringComparison.OrdinalIgnoreCase) &&
                    fs.Name.Equals("Позиция", StringComparison.OrdinalIgnoreCase));

            if (tagNoShelf == null)
                throw new InvalidOperationException("Тип марки \"Позиция_(_)_без полки\" не найден.");
            if (tagStd == null)
                throw new InvalidOperationException("Тип марки \"Позиция\" (семейство \"(марка)арматурный_стержень\") не найден.");

            if (!tagNoShelf.IsActive) tagNoShelf.Activate();
            if (!tagStd.IsActive)     tagStd.Activate();

            // ── Диагностика ────────────────────────────────────────────────
            var diagSb = new System.Text.StringBuilder();
            double ToMm(double v) => Math.Round(UnitUtils.ConvertFromInternalUnits(v, UnitTypeId.Millimeters));

            diagSb.AppendLine($"=== items: {items.Count} ===");
            foreach (var ig in items.GroupBy(r => (r.pos, Math.Round(r.diam, 4))).OrderBy(g => g.Key.pos))
            {
                var ws = ig.OrderBy(r => r.wProj).ToList();
                diagSb.AppendLine($"  pos={ig.Key.pos} d={ToMm(ig.Key.Item2)}мм: {ws.Count}шт "
                    + $"W=[{ToMm(ws.First().wProj)}..{ToMm(ws.Last().wProj)}]мм");
            }

            // ── 3-уровневая пространственная кластеризация стержней ───────
            // Уровень 1: диаметр — разный диаметр → разные кластеры
            // Уровень 2: позиция — пары позиций внутри одного диаметра
            // Уровень 3: пространство — та же пара прерывается стержнями другого типа
            var allPairs = new List<(Reference ref1, Reference ref2,
                                     double w1, double u1, double w2, double u2,
                                     int pos1, int pos2,
                                     string uid1, string uid2)>();

            // colTol: стержни на одной W-позиции (лицо/тыл стены)
            // gapTol: максимальный разрыв между W-столбцами одного кластера (не для проёмов)
            double colTol = UnitUtils.ConvertToInternalUnits(10,  UnitTypeId.Millimeters);
            double gapTol = UnitUtils.ConvertToInternalUnits(400, UnitTypeId.Millimeters);

            // Сортируем все элементы по W
            var sortedAll = items.OrderBy(r => r.wProj).ToList();

            // Группируем в W-столбцы: соседние стержни ближе colTol → один столбец
            var wCols = new List<List<(Element elem, Reference tagRef, int pos, double diam, double wProj, double uProj)>>();
            {
                var curCol = new List<(Element elem, Reference tagRef, int pos, double diam, double wProj, double uProj)>
                    { sortedAll[0] };
                for (int si = 1; si < sortedAll.Count; si++)
                {
                    if (sortedAll[si].wProj - curCol[curCol.Count - 1].wProj <= colTol)
                        curCol.Add(sortedAll[si]);
                    else
                    {
                        wCols.Add(curCol);
                        curCol = new List<(Element elem, Reference tagRef, int pos, double diam, double wProj, double uProj)> { sortedAll[si] };
                    }
                }
                wCols.Add(curCol);
            }

            // Pre-compute position groups: (diam, pos) pairs that share at least one W-column
            // are merged into the same group. This handles 2-row layouts where pos=2/pos=3 bars
            // are at the same W positions (same row spacing) — they belong to one cluster.
            // Example: "2 2 2 / 3 3 3" → group {pos=2, pos=3}, "2 2 2 | 3 4 3 4" → separate groups.
            var dpBarsW = items
                .GroupBy(i => (diam: Math.Round(i.diam, 4), pos: i.pos))
                .ToDictionary(g => g.Key, g => g.Select(i => i.wProj).ToList());
            var dpKeys = dpBarsW.Keys.ToList();
            var dpParent = Enumerable.Range(0, dpKeys.Count).ToArray();
            int DpFind(int x) { while (dpParent[x] != x) { dpParent[x] = dpParent[dpParent[x]]; x = dpParent[x]; } return x; }
            void DpUnion(int a, int b) { dpParent[DpFind(a)] = DpFind(b); }
            for (int pia = 0; pia < dpKeys.Count; pia++)
                for (int pib = pia + 1; pib < dpKeys.Count; pib++)
                {
                    if (Math.Abs(dpKeys[pia].diam - dpKeys[pib].diam) > 0.001) continue;
                    var wA = dpBarsW[dpKeys[pia]]; var wB = dpBarsW[dpKeys[pib]];
                    if (wA.Any(wa => wB.Any(wb => Math.Abs(wa - wb) <= colTol)))
                        DpUnion(pia, pib);
                }
            var dpGroupId = new Dictionary<(double diam, int pos), string>();
            {
                var byRoot = new Dictionary<int, List<(double diam, int pos)>>();
                for (int i = 0; i < dpKeys.Count; i++)
                {
                    int r = DpFind(i);
                    if (!byRoot.ContainsKey(r)) byRoot[r] = new List<(double, int)>();
                    byRoot[r].Add(dpKeys[i]);
                }
                foreach (var kv in byRoot)
                {
                    string gid = string.Join("|", kv.Value.Select(m => $"{m.diam:F4}:{m.pos}").OrderBy(s => s));
                    foreach (var m in kv.Value) dpGroupId[m] = gid;
                }
            }

            // ColSig uses the pre-computed group ID so that items sharing W-columns
            // produce the same signature even when a column contains only one of the positions.
            string ColSig(List<(Element elem, Reference tagRef, int pos, double diam, double wProj, double uProj)> col) =>
                string.Join("|", col
                    .Select(i => dpGroupId.TryGetValue((Math.Round(i.diam, 4), i.pos), out var gid)
                        ? gid : $"{Math.Round(i.diam, 4):F4}:{i.pos}")
                    .Distinct()
                    .OrderBy(s => s));


            // Объединяем соседние столбцы с одинаковой сигнатурой в пространственные кластеры.
            // Кластер НЕ прерывается, если соседние столбцы содержат общий элемент —
            // это первый и последний стержень одного длинного массива (bridging gap).
            // Кластер прерывается только при смене сигнатуры ИЛИ при большом разрыве без общих элементов.
            var spatialClusters = new List<List<List<(Element elem, Reference tagRef, int pos, double diam, double wProj, double uProj)>>>();
            {
                var curCluster = new List<List<(Element elem, Reference tagRef, int pos, double diam, double wProj, double uProj)>> { wCols[0] };
                string curSig = ColSig(wCols[0]);
                for (int ci = 1; ci < wCols.Count; ci++)
                {
                    string sig = ColSig(wCols[ci]);
                    double gap = wCols[ci][0].wProj - wCols[ci - 1][wCols[ci - 1].Count - 1].wProj;
                    // Элементы, общие для двух соседних столбцов — это первый и последний стержень одного массива.
                    var prevUids = new HashSet<string>(wCols[ci - 1].Select(i => i.elem.UniqueId));
                    bool hasSharedElems = wCols[ci].Any(i => prevUids.Contains(i.elem.UniqueId));
                    // Продолжаем кластер если: сигнатура та же И (есть общие элементы ИЛИ разрыв небольшой)
                    if (sig == curSig && (hasSharedElems || gap <= gapTol))
                    {
                        curCluster.Add(wCols[ci]);
                    }
                    else
                    {
                        spatialClusters.Add(curCluster);
                        curCluster = new List<List<(Element elem, Reference tagRef, int pos, double diam, double wProj, double uProj)>> { wCols[ci] };
                        curSig = sig;
                    }
                }
                spatialClusters.Add(curCluster);
            }

            diagSb.AppendLine($"\n=== Кластеры до слияния: {spatialClusters.Count} ===");
            foreach (var sc in spatialClusters)
            {
                var ai = sc.SelectMany(c => c).ToList();
                diagSb.AppendLine($"  [{ToMm(ai.Min(i => i.wProj))}..{ToMm(ai.Max(i => i.wProj))}] "
                    + $"sig={ColSig(ai)}");
            }

            // Дополнительный проход: объединяем соседние кластеры с одинаковой сигнатурой.
            // Это обеспечивает "один большой кластер" для марок одного типа, разделённых
            // пустыми разрывами (без стержней другого типа между ними).
            {
                var merged2 = new List<List<List<(Element elem, Reference tagRef, int pos, double diam, double wProj, double uProj)>>>();
                string prevSig2 = null;
                foreach (var sc in spatialClusters)
                {
                    string sig2 = ColSig(sc.SelectMany(c => c).ToList());
                    if (prevSig2 != null && sig2 == prevSig2)
                        merged2[merged2.Count - 1].AddRange(sc);
                    else
                    {
                        merged2.Add(new List<List<(Element, Reference, int, double, double, double)>>(sc));
                        prevSig2 = sig2;
                    }
                }
                spatialClusters = merged2;
            }

            diagSb.AppendLine($"\n=== Кластеры после слияния: {spatialClusters.Count} ===");
            foreach (var sc in spatialClusters)
            {
                var ai = sc.SelectMany(c => c).ToList();
                diagSb.AppendLine($"  [{ToMm(ai.Min(i => i.wProj))}..{ToMm(ai.Max(i => i.wProj))}] "
                    + $"sig={ColSig(ai)}");
            }

            // Для каждого пространственного кластера строим пары стержней
            foreach (var spCluster in spatialClusters)
            {
                var clusterItems = spCluster.SelectMany(c => c).ToList();

                // Самопара позиции: для каждого U-уровня (верх/низ стены) берём
                // глобально первый и последний стержень → 4 лидера (верх-лево, верх-право,
                // низ-лево, низ-право) при наличии двух рядов арматуры.
                void AddSelfPairs(IGrouping<int, (Element elem, Reference tagRef, int pos, double diam, double wProj, double uProj)> grp)
                {
                    int pos = grp.Key;
                    var sorted = grp.OrderBy(r => r.wProj).ToList();
                    if (sorted.Count == 0) return;

                    double minW  = sorted[0].wProj;
                    double maxW  = sorted[sorted.Count - 1].wProj;
                    double wTolSP = UnitUtils.ConvertToInternalUnits(10, UnitTypeId.Millimeters);

                    // Стержни на левом и правом крае кластера
                    var leftItems  = sorted.Where(r => r.wProj - minW <= wTolSP)
                                           .OrderBy(r => r.uProj).ToList();
                    var rightItems = sorted.Where(r => maxW - r.wProj <= wTolSP)
                                           .OrderBy(r => r.uProj).ToList();

                    // Пары по U-уровням: верх↔верх, низ↔низ
                    int n = Math.Min(leftItems.Count, rightItems.Count);
                    for (int li = 0; li < n; li++)
                    {
                        var l = leftItems [li];
                        var r = rightItems[li];
                        allPairs.Add((l.tagRef, r.tagRef,
                                      l.wProj, l.uProj,
                                      r.wProj, r.uProj,
                                      pos, pos,
                                      l.elem.UniqueId, r.elem.UniqueId));
                    }
                }

                foreach (var diamGrp in clusterItems.GroupBy(r => Math.Round(r.diam, 4)))
                {
                    var byPos = diamGrp.GroupBy(r => r.pos).OrderBy(g => g.Key).ToList();

                    if (byPos.Count < 2) { AddSelfPairs(byPos[0]); continue; }

                    for (int pi = 0; pi + 1 < byPos.Count; pi += 2)
                    {
                        var gMin = byPos[pi    ].OrderBy(r => r.wProj).ToList();
                        var gMax = byPos[pi + 1].OrderBy(r => r.wProj).ToList();
                        int p1 = byPos[pi].Key, p2 = byPos[pi + 1].Key;

                        if (gMin.Count == 0 || gMax.Count == 0) continue;

                        // Только крайние стержни (как в AddSelfPairs).
                        // Единый uid гарантирует, что оба попадут в ОДИН arrayCluster → один тег.
                        double wTolCP = UnitUtils.ConvertToInternalUnits(10, UnitTypeId.Millimeters);
                        var leftR1  = gMin.First();
                        var leftR2  = gMax.First();
                        var rightR1 = gMin.Last();
                        var rightR2 = gMax.Last();
                        string shUid1 = leftR1.elem.UniqueId;
                        string shUid2 = leftR2.elem.UniqueId;

                        allPairs.Add((leftR1.tagRef, leftR2.tagRef,
                                      leftR1.wProj, leftR1.uProj,
                                      leftR2.wProj, leftR2.uProj,
                                      p1, p2, shUid1, shUid2));

                        if (Math.Abs(rightR1.wProj - leftR1.wProj) > wTolCP)
                            allPairs.Add((rightR1.tagRef, rightR2.tagRef,
                                          rightR1.wProj, rightR1.uProj,
                                          rightR2.wProj, rightR2.uProj,
                                          p1, p2, shUid1, shUid2));
                    }

                    if (byPos.Count % 2 != 0)
                        AddSelfPairs(byPos[byPos.Count - 1]);
                }
            }

            diagSb.AppendLine($"\n=== allPairs: {allPairs.Count} ===");
            foreach (var pg in allPairs.GroupBy(p => (p.pos1, p.pos2)).OrderBy(g => g.Key.pos1))
            {
                diagSb.AppendLine($"  pos({pg.Key.pos1},{pg.Key.pos2}): {pg.Count()} пар "
                    + $"W=[{ToMm(pg.Min(p => Math.Min(p.w1, p.w2)))}..{ToMm(pg.Max(p => Math.Max(p.w1, p.w2)))}]мм");
            }

            if (!allPairs.Any())
                throw new InvalidOperationException("Не найдено позиций арматуры для расстановки марок.");

            XYZ MakePt(double w, double u) =>
                wallDir.Multiply(w) + upDir.Multiply(u) + XYZ.BasisZ.Multiply(cutZ);

            // ── Параметры раскладки ────────────────────────────────────────
            double wallMinU = items.Min(r => r.uProj);
            double wallMaxU = items.Max(r => r.uProj);
            double tagOffset = UnitUtils.ConvertToInternalUnits(250, UnitTypeId.Millimeters);
            double tagU = placeAbove
                ? wallMinU - tagOffset
                : wallMaxU + tagOffset;
            double halfGap  = UnitUtils.ConvertToInternalUnits(50, UnitTypeId.Millimeters);

            // ── Создание тегов ─────────────────────────────────────────────
            // Логика: внутри каждой (pos1,pos2)-группы сначала разбиваем на
            // per-array кластеры (по uid1,uid2), потом сливаем соседние кластеры
            // если разрыв между концами ≤ 250 мм (шаг стержней).
            double mergeGap = UnitUtils.ConvertToInternalUnits(250, UnitTypeId.Millimeters);

            int tagsPlaced = 0;
            var errors = new List<string>();
            int k = 0;

            foreach (var posGroup in allPairs
                .GroupBy(p => (p.pos1, p.pos2))
                .OrderBy(g => g.Average(p => (p.w1 + p.w2) / 2.0)))
            {
                // Per-array кластеры (один element-pair → один массив)
                var arrayClusters = posGroup
                    .GroupBy(p => (p.uid1, p.uid2))
                    .Select(g => g.ToList())
                    .OrderBy(c => c.Average(p => (p.w1 + p.w2) / 2.0))
                    .ToList();

                // Слияние соседних кластеров с малым разрывом
                var merged = new List<List<(Reference ref1, Reference ref2,
                    double w1, double u1, double w2, double u2,
                    int pos1, int pos2, string uid1, string uid2)>>();
                var cur = arrayClusters[0];

                for (int ci = 1; ci < arrayClusters.Count; ci++)
                {
                    double prevEnd  = cur.Max(p => Math.Max(p.w1, p.w2));
                    double nextStart = arrayClusters[ci].Min(p => Math.Min(p.w1, p.w2));

                    if (nextStart - prevEnd <= mergeGap)
                        cur = cur.Concat(arrayClusters[ci]).ToList();
                    else
                    { merged.Add(cur); cur = arrayClusters[ci]; }
                }
                merged.Add(cur);

                bool isSelfPair = posGroup.Key.pos1 == posGroup.Key.pos2;

                foreach (var cluster in merged)
                {
                    double canonW = cluster.Average(p => (p.w1 + p.w2) / 2.0);
                    double shift  = UnitUtils.ConvertToInternalUnits(50, UnitTypeId.Millimeters);

                    if (isSelfPair)
                    {
                        // Одинаковые позиции: один тип "Позиция", все марки в одной точке.
                        // Крайние W-группы кластера определяют первый/последний стержни массивов.
                        XYZ    tagPt     = MakePt(canonW, tagU);
                        double tol       = UnitUtils.ConvertToInternalUnits(1, UnitTypeId.Millimeters);
                        double minAvgW   = cluster.Min(p => (p.w1 + p.w2) / 2.0);
                        double maxAvgW   = cluster.Max(p => (p.w1 + p.w2) / 2.0);
                        bool   twoGroups = maxAvgW - minAvgW > tol;

                        // Первые стержни первых массивов (левая граница → ref1)
                        var leftPairs = cluster.Where(p => Math.Abs((p.w1 + p.w2) / 2.0 - minAvgW) < tol).ToList();
                        foreach (var p in leftPairs)
                        {
                            XYZ anchor = MakePt(p.w1, p.u1);
                            XYZ elbow  = MakePt(p.w1 + Math.Sign(canonW - p.w1) * shift, tagU);
                            try
                            {
                                var t = IndependentTag.Create(doc, tagStd.Id, sectionView.Id,
                                    p.ref1, true, TagOrientation.Horizontal, tagPt);
                                t.LeaderEndCondition = LeaderEndCondition.Free;
                                t.SetLeaderEnd(p.ref1, anchor);
                                t.SetLeaderElbow(p.ref1, elbow);
                                t.TagHeadPosition = tagPt;
                                tagsPlaced++;
                            }
                            catch (Exception ex) { errors.Add($"SelfStart[{k}]: {ex.Message}"); }
                        }

                        // Последние стержни последних массивов (правая граница → ref2)
                        var rightPairs = twoGroups
                            ? cluster.Where(p => Math.Abs((p.w1 + p.w2) / 2.0 - maxAvgW) < tol).ToList()
                            : leftPairs; // один W-уровень: те же пары, но ref2
                        foreach (var p in rightPairs)
                        {
                            XYZ anchor = MakePt(p.w2, p.u2);
                            XYZ elbow  = MakePt(p.w2 + Math.Sign(canonW - p.w2) * shift, tagU);
                            try
                            {
                                var t = IndependentTag.Create(doc, tagStd.Id, sectionView.Id,
                                    p.ref2, true, TagOrientation.Horizontal, tagPt);
                                t.LeaderEndCondition = LeaderEndCondition.Free;
                                t.SetLeaderEnd(p.ref2, anchor);
                                t.SetLeaderElbow(p.ref2, elbow);
                                t.TagHeadPosition = tagPt;
                                tagsPlaced++;
                            }
                            catch (Exception ex) { errors.Add($"SelfEnd[{k}]: {ex.Message}"); }
                        }
                    }
                    else
                    {
                        // Разные позиции: tagNoShelf (pos1) + tagStd (pos2) на двух точках
                        XYZ ptNoShelf = MakePt(canonW - halfGap, tagU);
                        XYZ ptStd     = MakePt(canonW + halfGap, tagU);

                        var startP   = cluster[0];
                        double sMid  = (startP.w1 + startP.w2) / 2.0;
                        XYZ sElbow   = MakePt(sMid + Math.Sign(canonW - sMid) * shift, tagU);

                        try
                        {
                            var t1 = IndependentTag.Create(doc, tagNoShelf.Id, sectionView.Id,
                                startP.ref1, true, TagOrientation.Horizontal, ptNoShelf);
                            t1.LeaderEndCondition = LeaderEndCondition.Free;
                            t1.SetLeaderEnd(startP.ref1, MakePt(startP.w1, startP.u1));
                            t1.SetLeaderElbow(startP.ref1, sElbow);
                            t1.TagHeadPosition = ptNoShelf;
                            tagsPlaced++;
                        }
                        catch (Exception ex) { errors.Add($"NoShelf start[{k}]: {ex.Message}"); }

                        try
                        {
                            var t2 = IndependentTag.Create(doc, tagStd.Id, sectionView.Id,
                                startP.ref2, true, TagOrientation.Horizontal, ptStd);
                            t2.LeaderEndCondition = LeaderEndCondition.Free;
                            t2.SetLeaderEnd(startP.ref2, MakePt(startP.w2, startP.u2));
                            t2.SetLeaderElbow(startP.ref2, sElbow);
                            t2.TagHeadPosition = ptStd;
                            tagsPlaced++;
                        }
                        catch (Exception ex) { errors.Add($"Std start[{k}]: {ex.Message}"); }

                        if (cluster.Count >= 2)
                        {
                            var endP    = cluster[cluster.Count - 1];
                            double eMid = (endP.w1 + endP.w2) / 2.0;
                            XYZ eElbow  = MakePt(eMid + Math.Sign(canonW - eMid) * shift, tagU);

                            try
                            {
                                var t3 = IndependentTag.Create(doc, tagNoShelf.Id, sectionView.Id,
                                    endP.ref1, true, TagOrientation.Horizontal, ptNoShelf);
                                t3.LeaderEndCondition = LeaderEndCondition.Free;
                                t3.SetLeaderEnd(endP.ref1, MakePt(endP.w1, endP.u1));
                                t3.SetLeaderElbow(endP.ref1, eElbow);
                                t3.TagHeadPosition = ptNoShelf;
                                tagsPlaced++;
                            }
                            catch (Exception ex) { errors.Add($"NoShelf end[{k}]: {ex.Message}"); }

                            try
                            {
                                var t4 = IndependentTag.Create(doc, tagStd.Id, sectionView.Id,
                                    endP.ref2, true, TagOrientation.Horizontal, ptStd);
                                t4.LeaderEndCondition = LeaderEndCondition.Free;
                                t4.SetLeaderEnd(endP.ref2, MakePt(endP.w2, endP.u2));
                                t4.SetLeaderElbow(endP.ref2, eElbow);
                                t4.TagHeadPosition = ptStd;
                                tagsPlaced++;
                            }
                            catch (Exception ex) { errors.Add($"Std end[{k}]: {ex.Message}"); }
                        }
                    }

                    k++;
                }
            }

            // ── Размерная цепь вдоль стены ───────────────────────────────────
            PlaceWallDimensions(doc, sectionView, wall, wallDir, upDir, cutZ, items, diagSb);

            // ── Размеры поперёк стены (толщина / привязки) ───────────────────
            PlaceCrossDimensions(doc, sectionView, wall, wallDir, upDir, cutZ, items, wallLine, diagSb);

            // ── Марки горизонтальных стержней формы 1 ───────────────────────
            PlaceHorizontalBarTags(doc, sectionView, wallDir, upDir, cutZ, wallLine, hItems);

            // ── Диагностика П-шек ─────────────────────────────────────────────
            {
                var pDiag = new System.Text.StringBuilder();
                int pTotal = 0;
                foreach (Element rb in allRebar)
                {
                    Rebar pr = rb as Rebar;
                    if (pr == null) continue;
                    RebarShape ps = doc.GetElement(pr.GetShapeId()) as RebarShape;
                    if (ps == null) continue;
                    bool isP = ps.Name.StartsWith("(форма)П-шка", StringComparison.OrdinalIgnoreCase)
                            || ps.Name.Equals("(форма)21", StringComparison.OrdinalIgnoreCase);
                    if (!isP) continue;

                    pTotal++;
                    string posStr = pr.LookupParameter("BI_позиция")?.AsString()
                                 ?? pr.LookupParameter("BI_позиция")?.AsInteger().ToString()
                                 ?? "—";
                    pDiag.AppendLine($"  id={pr.Id.IntegerValue} форма={ps.Name} " +
                                     $"поз={posStr} кол={pr.NumberOfBarPositions}");
                }
                diagSb.AppendLine($"\n=== П-шки на виде: {pTotal} ===");
                if (pTotal > 0) diagSb.Append(pDiag);
            }

            // ── Марки для П-шек ────────────────────────────────────────────────
            const string pShapePrefix = "(форма)П-шка";

            FamilySymbol tagPosShag = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .OfCategory(BuiltInCategory.OST_RebarTags)
                .Cast<FamilySymbol>()
                .FirstOrDefault(fs => fs.Name.Equals("Позиция/Шаг", StringComparison.OrdinalIgnoreCase));

            // Fallback to standard tag if "Позиция/Шаг" type is not loaded
            FamilySymbol pBarTagSymbol = tagPosShag ?? tagNoShelf ?? tagStd;

            if (pBarTagSymbol != null)
            {
                if (!pBarTagSymbol.IsActive) pBarTagSymbol.Activate();

                double wallWCenter = (wallLine.GetEndPoint(0).DotProduct(wallDir) +
                                      wallLine.GetEndPoint(1).DotProduct(wallDir)) / 2.0;

                double pSideOffset = UnitUtils.ConvertToInternalUnits(500, UnitTypeId.Millimeters);
                double pUpOffset   = UnitUtils.ConvertToInternalUnits(400, UnitTypeId.Millimeters);

                foreach (Element rb in allRebar)
                {
                    Rebar pRebar = rb as Rebar;
                    if (pRebar == null) continue;

                    RebarShape pShape = doc.GetElement(pRebar.GetShapeId()) as RebarShape;
                    if (pShape == null) continue;
                    bool isPShape = pShape.Name.StartsWith(pShapePrefix, StringComparison.OrdinalIgnoreCase)
                                 || pShape.Name.Equals("(форма)21", StringComparison.OrdinalIgnoreCase);
                    if (!isPShape) continue;

                    // Compute W/U range from the first bar's centerline curves.
                    // Using bar 0 keeps the tag anchored to the visible bar in the section
                    // rather than the overall array bounding box center.
                    double barWMin = double.MaxValue, barWMax = double.MinValue;
                    double barUMin = double.MaxValue, barUMax = double.MinValue;
                    bool gotCurves = false;
                    try
                    {
                        IList<Curve> curves = pRebar.GetCenterlineCurves(
                            false, false, false,
                            MultiplanarOption.IncludeOnlyPlanarCurves, 0);
                        if (curves != null && curves.Count > 0)
                        {
                            foreach (Curve c in curves)
                            {
                                for (int ks = 0; ks <= 4; ks++)
                                {
                                    XYZ ptC = c.Evaluate((double)ks / 4.0, true);
                                    double cw = ptC.DotProduct(wallDir);
                                    double cu = ptC.DotProduct(upDir);
                                    if (cw < barWMin) barWMin = cw; if (cw > barWMax) barWMax = cw;
                                    if (cu < barUMin) barUMin = cu; if (cu > barUMax) barUMax = cu;
                                }
                            }
                            gotCurves = barWMin < double.MaxValue;
                        }
                    }
                    catch { }

                    if (!gotCurves)
                    {
                        // Fallback: overall bounding box
                        BoundingBoxXYZ bb = pRebar.get_BoundingBox(sectionView) ?? pRebar.get_BoundingBox(null);
                        if (bb == null) continue;
                        XYZ mn = bb.Min, mx = bb.Max;
                        for (int ci = 0; ci < 8; ci++)
                        {
                            XYZ corner = bb.Transform.OfPoint(new XYZ(
                                (ci & 1) == 0 ? mn.X : mx.X,
                                (ci & 2) == 0 ? mn.Y : mx.Y,
                                (ci & 4) == 0 ? mn.Z : mx.Z));
                            double cw = corner.DotProduct(wallDir);
                            double cu = corner.DotProduct(upDir);
                            if (cw < barWMin) barWMin = cw; if (cw > barWMax) barWMax = cw;
                            if (cu < barUMin) barUMin = cu; if (cu > barUMax) barUMax = cu;
                        }
                        if (barWMin == double.MaxValue) continue;
                    }

                    double barWCenter = (barWMin + barWMax) / 2.0;
                    double barUCenter = (barUMin + barUMax) / 2.0;
                    double sideSign   = barWCenter >= wallWCenter ? 1.0 : -1.0;

                    double anchorW    = sideSign > 0 ? barWMax : barWMin;
                    XYZ leaderAnchor  = MakePt(anchorW, barUCenter);
                    XYZ tagHead       = MakePt(anchorW + sideSign * pSideOffset, barUCenter - pUpOffset);

                    Reference pTagRef = GetTagRef(pRebar);
                    if (pTagRef == null) continue;

                    try
                    {
                        var pt = IndependentTag.Create(doc, pBarTagSymbol.Id, sectionView.Id,
                            pTagRef, true, TagOrientation.Horizontal, tagHead);
                        pt.LeaderEndCondition = LeaderEndCondition.Free;
                        pt.SetLeaderEnd(pTagRef, leaderAnchor);
                        pt.TagHeadPosition = tagHead;
                        tagsPlaced++;
                    }
                    catch (Exception ex) { errors.Add($"П-шка [{pRebar.Id.IntegerValue}]: {ex.Message}"); }
                }
            }

            {
                string errPart = errors.Any() ? "\n\nОшибки:\n" + string.Join("\n", errors) : "";
                TaskDialog.Show("Диагностика",
                    $"Создано тегов: {tagsPlaced}{errPart}\n\n{diagSb}");
            }
        }

        // ── Поперечные размеры: толщина стены + привязки к арматуре и оси ──
        private static void PlaceCrossDimensions(
            Document doc, ViewSection sectionView, Wall wall,
            XYZ wallDir, XYZ upDir, double cutZ,
            List<(Element elem, Reference tagRef, int pos, double diam, double wProj, double uProj)> items,
            Line wallLine,
            System.Text.StringBuilder diagSb = null)
        {
            if (!items.Any()) return;

            double ToMmU(double v) => Math.Round(UnitUtils.ConvertFromInternalUnits(v, UnitTypeId.Millimeters));

            XYZ MakePt(double w, double u) =>
                wallDir.Multiply(w) + upDir.Multiply(u) + XYZ.BasisZ.Multiply(cutZ);

            double marginU    = UnitUtils.ConvertToInternalUnits(200, UnitTypeId.Millimeters);
            double dimSpacing = UnitUtils.ConvertToInternalUnits(250, UnitTypeId.Millimeters);

            // W-диапазон стены
            double wA = wallLine.GetEndPoint(0).DotProduct(wallDir);
            double wB = wallLine.GetEndPoint(1).DotProduct(wallDir);
            double wallWLeft  = Math.Min(wA, wB);
            double wallWRight = Math.Max(wA, wB);

            // Грани стены с нормалью вдоль upDir (передняя/задняя)
            var facesU = new List<(Reference rf, double u)>();
            {
                Options opt = new Options { View = sectionView, ComputeReferences = true };
                GeometryElement geom = wall.get_Geometry(opt);
                if (geom != null)
                {
                    foreach (GeometryObject obj in geom)
                    {
                        Solid s = obj as Solid;
                        if (s == null || s.Faces.Size == 0) continue;
                        foreach (Face f in s.Faces)
                        {
                            PlanarFace pf = f as PlanarFace;
                            if (pf?.Reference == null) continue;
                            if (Math.Abs(pf.FaceNormal.Normalize().DotProduct(upDir)) < 0.99) continue;
                            if (!FaceOverlapsCrop(sectionView, pf)) continue; // грань невидимого проёма за CropBox
                            double u = pf.Origin.DotProduct(upDir);
                            if (!facesU.Any(r => Math.Abs(r.u - u) < 1e-4))
                                facesU.Add((pf.Reference, u));
                        }
                    }
                }
            }
            if (facesU.Count < 2) return;
            facesU.Sort((a, b) => a.u.CompareTo(b.u));
            var faceMin = facesU.First();
            var faceMax = facesU.Last();

            // Оси (Grid) параллельные wallDir, попадающие в диапазон толщины стены
            var gridRefs = new List<(Reference rf, double u)>();
            {
                XYZ wallDirH = new XYZ(wallDir.X, wallDir.Y, 0).Normalize();
                foreach (Grid g in new FilteredElementCollector(doc)
                    .OfClass(typeof(Grid)).Cast<Grid>())
                {
                    if (!(g.Curve is Line gl)) continue;
                    XYZ gDirH = new XYZ(gl.Direction.X, gl.Direction.Y, 0);
                    if (gDirH.GetLength() < 1e-6) continue;
                    if (Math.Abs(gDirH.Normalize().DotProduct(wallDirH)) < 0.99) continue;
                    double gu = gl.GetEndPoint(0).DotProduct(upDir);
                    if (gu < faceMin.u - 1e-3 || gu > faceMax.u + 1e-3) continue;
                    gridRefs.Add((new Reference(g), gu));
                }
            }
            gridRefs.Sort((a, b) => a.u.CompareTo(b.u));

            // Ссылки на арматуру вдоль upDir: передний ряд (min U) и задний ряд (max U)
            double uMinRebar = items.Min(r => r.uProj);
            double uMaxRebar = items.Max(r => r.uProj);
            double rowTol    = UnitUtils.ConvertToInternalUnits(20, UnitTypeId.Millimeters);

            (Reference rf, int elemId, double uFound) FindRebarRefAtU(double targetU)
            {
                foreach (var item in items.OrderBy(r => Math.Abs(r.uProj - targetU)))
                {
                    Rebar rb = item.elem as Rebar;
                    if (rb == null) continue;
                    var lrs = GetVerticalBarLineRefs(rb, sectionView, wallDir, upDir);
                    var c = lrs.Where(lr => Math.Abs(lr.uCoord - targetU) < rowTol)
                               .OrderBy(lr => Math.Abs(lr.uCoord - targetU))
                               .FirstOrDefault();
                    if (c.rf != null) return (c.rf, item.elem.Id.IntegerValue, c.uCoord);
                }
                return (null, -1, 0);
            }

            var frResult = FindRebarRefAtU(uMinRebar);
            Reference frontRef = frResult.rf;
            var brResult = Math.Abs(uMaxRebar - uMinRebar) > rowTol
                           ? FindRebarRefAtU(uMaxRebar) : (null, -1, 0.0);
            Reference backRef = brResult.rf;

            diagSb?.AppendLine("\n=== PlaceCrossDimensions ===");
            diagSb?.AppendLine($"  facesU ({facesU.Count}): {string.Join(", ", facesU.Select(f => $"{ToMmU(f.u)}мм"))}");
            diagSb?.AppendLine($"  gridRefs: {gridRefs.Count}");
            diagSb?.AppendLine($"  items uProj: {string.Join(", ", items.Select(r => $"{ToMmU(r.uProj)}мм"))}");
            diagSb?.AppendLine($"  uMinRebar={ToMmU(uMinRebar)}мм  uMaxRebar={ToMmU(uMaxRebar)}мм  diff={ToMmU(Math.Abs(uMaxRebar - uMinRebar))}мм  rowTol={ToMmU(rowTol)}мм");
            diagSb?.AppendLine($"  frontRef: {(frontRef != null ? $"найден  elemId={frResult.elemId}  uFound={ToMmU(frResult.uFound)}мм" : "НЕ найден")}");
            diagSb?.AppendLine($"  backRef:  {(backRef  != null ? $"найден  elemId={brResult.elemId}  uFound={ToMmU(brResult.uFound)}мм" : (Math.Abs(uMaxRebar - uMinRebar) <= rowTol ? "NULL — один ряд (uMin==uMax)" : "НЕ найден"))}");
            if (frontRef != null && backRef != null)
                diagSb?.AppendLine($"  sameElement: {frResult.elemId == brResult.elemId}");

            // Создание размера вдоль upDir
            Dimension CreateDim(double lineW, ReferenceArray ra)
            {
                if (ra.Size < 2) return null;
                XYZ p1 = MakePt(lineW, faceMin.u - marginU);
                XYZ p2 = MakePt(lineW, faceMax.u + marginU);
                if ((p2 - p1).GetLength() < 1e-6) return null;
                Line dl;
                try { dl = Line.CreateBound(p1, p2); } catch { return null; }
                try { return doc.Create.NewDimension(sectionView, dl, ra); } catch { return null; }
            }

            // 1. Толщина стены (крайний левый)
            {
                var ra = new ReferenceArray();
                ra.Append(faceMin.rf);
                ra.Append(faceMax.rf);
                CreateDim(wallWLeft - 2 * dimSpacing, ra);
            }

            // 2. Цепочка: грань → перед. арматура → [оси] → задн. арматура → грань
            //    Если осей нет — просто грань → арматура → грань
            {
                var ra = new ReferenceArray();
                ra.Append(faceMin.rf);
                if (frontRef != null) ra.Append(frontRef);
                foreach (var gr in gridRefs) ra.Append(gr.rf);
                if (backRef  != null) ra.Append(backRef);
                ra.Append(faceMax.rf);
                diagSb?.AppendLine($"  dim2 ra.Size={ra.Size}");
                Dimension dim2 = CreateDim(wallWLeft - dimSpacing, ra);
                if (diagSb != null && dim2 != null)
                {
                    doc.Regenerate();
                    var segs2 = dim2.Segments;
                    if (segs2 != null && segs2.Size > 0)
                    {
                        var sb2 = new System.Text.StringBuilder("  dim2 сегменты: ");
                        for (int i = 0; i < segs2.Size; i++)
                            sb2.Append($"[{i}]={ToMmU(segs2.get_Item(i).Value ?? 0)}мм ");
                        diagSb.AppendLine(sb2.ToString());
                    }
                    else
                        diagSb?.AppendLine($"  dim2 Value={ToMmU(dim2.Value ?? 0)}мм (нет сегментов)");
                }
                else if (diagSb != null)
                    diagSb.AppendLine("  dim2: НЕ создан");
            }

            // 3. Привязка к оси: грань → [оси] → грань (справа)
            if (gridRefs.Any())
            {
                var ra = new ReferenceArray();
                ra.Append(faceMin.rf);
                foreach (var gr in gridRefs) ra.Append(gr.rf);
                ra.Append(faceMax.rf);
                CreateDim(wallWRight + dimSpacing, ra);
            }
        }

        // ── Размерная цепь: торец → первый/последний стержень каждого массива → торец ──
        private static void PlaceWallDimensions(
            Document doc, ViewSection sectionView, Wall wall,
            XYZ wallDir, XYZ upDir, double cutZ,
            List<(Element elem, Reference tagRef, int pos, double diam, double wProj, double uProj)> items,
            System.Text.StringBuilder diagSb = null)
        {
            if (!items.Any()) return;

            double ToMmD(double v) => Math.Round(UnitUtils.ConvertFromInternalUnits(v, UnitTypeId.Millimeters));

            double wallMinU  = items.Min(r => r.uProj);
            double wallMaxU  = items.Max(r => r.uProj);
            double dimOffset = UnitUtils.ConvertToInternalUnits(400, UnitTypeId.Millimeters);
            double dimU      = wallMaxU + dimOffset;

            // Нижние стержни (uProj ближайший к wallMaxU = ближняя грань, снизу в плане)
            double rowTol    = Math.Max((wallMaxU - wallMinU) * 0.25,
                                        UnitUtils.ConvertToInternalUnits(30, UnitTypeId.Millimeters));
            var bottomItems  = items.Where(r => r.uProj >= wallMaxU - rowTol).ToList();
            if (!bottomItems.Any()) bottomItems = items;

            XYZ MakePt(double w, double u) =>
                wallDir.Multiply(w) + upDir.Multiply(u) + XYZ.BasisZ.Multiply(cutZ);

            // Торцевые грани стены — нормаль вдоль wallDir
            var wallFaceRefs = new List<(Reference rf, double coord)>();
            {
                Options opt = new Options { View = sectionView, ComputeReferences = true };
                GeometryElement geom = wall.get_Geometry(opt);
                if (geom != null)
                {
                    foreach (GeometryObject obj in geom)
                    {
                        Solid s = obj as Solid;
                        if (s == null || s.Faces.Size == 0) continue;
                        foreach (Face f in s.Faces)
                        {
                            PlanarFace pf = f as PlanarFace;
                            if (pf?.Reference == null) continue;
                            if (Math.Abs(pf.FaceNormal.Normalize().DotProduct(wallDir)) < 0.99) continue;
                            if (!FaceOverlapsCrop(sectionView, pf)) continue; // грань невидимого проёма за CropBox
                            double coord = pf.Origin.DotProduct(wallDir);
                            if (!wallFaceRefs.Any(r => Math.Abs(r.coord - coord) < 1e-4))
                                wallFaceRefs.Add((pf.Reference, coord));
                        }
                    }
                }
            }
            if (wallFaceRefs.Count < 2) return;
            wallFaceRefs.Sort((a, b) => a.coord.CompareTo(b.coord));

            diagSb?.AppendLine($"\n=== PlaceWallDimensions ===");
            diagSb?.AppendLine($"  Граней стены: {wallFaceRefs.Count}");
            foreach (var wf in wallFaceRefs)
                diagSb?.AppendLine($"    W={ToMmD(wf.coord)}мм");
            diagSb?.AppendLine($"  bottomItems: {bottomItems.Count}шт");

            double matchTol = UnitUtils.ConvertToInternalUnits(5, UnitTypeId.Millimeters);
            double margin   = UnitUtils.ConvertToInternalUnits(100, UnitTypeId.Millimeters);

            // Iterate over pairs of consecutive wall faces.
            // A solid wall has 2 faces; a wall with one opening has 4 faces (left pair + right pair).
            for (int si = 0; si + 1 < wallFaceRefs.Count; si += 2)
            {
                var segLeft  = wallFaceRefs[si];
                var segRight = wallFaceRefs[si + 1];

                // Все стержни в диапазоне W (не только bottomItems): изолированные стержни
                // на краях проёмов могут иметь другую U-позицию и выпасть из bottomItems.
                var segItems = items
                    .Where(r => r.wProj >= segLeft.coord - matchTol && r.wProj <= segRight.coord + matchTol)
                    .ToList();

                diagSb?.AppendLine($"  Сегмент [{ToMmD(segLeft.coord)}..{ToMmD(segRight.coord)}]: "
                    + $"segItems={segItems.Count}");

                if (!segItems.Any()) continue;

                // Первый проход: собираем (wFirst, wLast, rebar, ref) для каждого элемента
                var allDataRaw = new List<(double wFirst, double wLast, Rebar rebar,
                                           Reference refFirst, Reference refLast)>();

                foreach (var grp in segItems.GroupBy(r => r.elem.UniqueId))
                {
                    Rebar rebar = grp.First().elem as Rebar;
                    if (rebar == null) continue;

                    var sorted = grp.OrderBy(r => r.wProj).ToList();
                    double wFirst = sorted.First().wProj;
                    double wLast  = sorted.Last().wProj;

                    // ВАЖНО: используем найденную геометрическую edge-ссылку (lineRef) всегда,
                    // если она есть — без проверки допуска. Subelement-ссылка (tagRef) годится
                    // для IndependentTag, но NewDimension может тихо отбросить её как witness,
                    // из-за чего соседние отрезки размерной цепи сливаются в один (без ошибки).
                    var lineRefs = GetVerticalBarLineRefs(rebar, sectionView, wallDir, upDir);

                    // Среди рёбер в одном W-столбце (в пределах matchTol от ближайшего)
                    // всегда берём нижний ряд (наибольший uCoord = wallMaxU), а не
                    // первый по сортировке — иначе привязка случайно прыгает между
                    // верхним и нижним рядом стержней.
                    Reference refFirst = sorted.First().tagRef;
                    if (lineRefs.Any())
                    {
                        double bestFirstDiff = lineRefs.Min(r => Math.Abs(r.wCoord - wFirst));
                        var bestFirst = lineRefs
                            .Where(r => Math.Abs(r.wCoord - wFirst) <= bestFirstDiff + matchTol)
                            .OrderByDescending(r => r.uCoord)
                            .First();
                        refFirst = bestFirst.rf;
                    }

                    Reference refLast = sorted.Last().tagRef;
                    if (Math.Abs(wLast - wFirst) > matchTol && lineRefs.Any())
                    {
                        var others = lineRefs.Where(r => Math.Abs(r.wCoord - wFirst) > matchTol).ToList();
                        if (others.Any())
                        {
                            double bestLastDiff = others.Min(r => Math.Abs(r.wCoord - wLast));
                            var bestLast = others
                                .Where(r => Math.Abs(r.wCoord - wLast) <= bestLastDiff + matchTol)
                                .OrderByDescending(r => r.uCoord)
                                .First();
                            refLast = bestLast.rf;
                        }
                    }

                    allDataRaw.Add((wFirst, wLast, rebar, refFirst, refLast));
                }

                var allArrayData = allDataRaw;
                var barPositions = new List<(double wCoord, Reference rf)>();
                var arraySpans   = new List<(double wFirst, double wLast, Rebar rebar)>();

                // Сортируем по длине span (короткие первыми).
                allArrayData.Sort((a, b) => (a.wLast - a.wFirst).CompareTo(b.wLast - b.wFirst));

                // ── Обнаружение шахматного расположения ──────────────────────────────
                // Два массива с одинаковым шагом s, смещённые ровно на s/2,
                // создают эффективный шаг s/2.  Объединяем их в один «сводный» span.
                var mergedPairs = new HashSet<int>();
                var mergedAnnotations = new List<(double wFirst, double wLast,
                                                  double effSpacing, int totalBars,
                                                  Reference refFirst, Reference refLast)>();

                for (int ia = 0; ia < allArrayData.Count; ia++)
                {
                    if (mergedPairs.Contains(ia)) continue;
                    var a = allArrayData[ia];
                    int  n_a = a.rebar.NumberOfBarPositions;
                    if (n_a < 2) continue;
                    double span_a = a.wLast - a.wFirst;
                    if (span_a < matchTol) continue;
                    double spacing_a = span_a / (n_a - 1);

                    for (int ib = ia + 1; ib < allArrayData.Count; ib++)
                    {
                        if (mergedPairs.Contains(ib)) continue;
                        var b = allArrayData[ib];
                        int  n_b = b.rebar.NumberOfBarPositions;
                        if (n_b < 2) continue;
                        double span_b = b.wLast - b.wFirst;
                        if (span_b < matchTol) continue;
                        double spacing_b = span_b / (n_b - 1);

                        // Шаги должны быть похожи
                        if (Math.Abs(spacing_a - spacing_b) > matchTol * 3) continue;
                        double spacing = (spacing_a + spacing_b) / 2.0;

                        // Размеры массивов не должны сильно различаться (не допускаем слияние
                        // маленького фрагмента n=2 с большим массивом n=10)
                        if (Math.Min(n_a, n_b) * 2 < Math.Max(n_a, n_b)) continue;

                        // Смещение первых стержней ≈ spacing/2
                        double offsetAB = Math.Abs(b.wFirst - a.wFirst);
                        if (Math.Abs(offsetAB - spacing / 2.0) > matchTol * 3) continue;

                        // Достаточное перекрытие span-ов (≥ 0.4×spacing, чтобы включить пары n=2)
                        double overlapLen = Math.Min(a.wLast, b.wLast) - Math.Max(a.wFirst, b.wFirst);
                        if (overlapLen < spacing * 0.4) continue;

                        double mFirst = Math.Min(a.wFirst, b.wFirst);
                        double mLast  = Math.Max(a.wLast,  b.wLast);
                        Reference mRefFirst = a.wFirst <= b.wFirst ? a.refFirst : b.refFirst;
                        Reference mRefLast  = a.wLast  >= b.wLast  ? a.refLast  : b.refLast;

                        mergedAnnotations.Add((mFirst, mLast, spacing / 2.0, n_a + n_b,
                                               mRefFirst, mRefLast));
                        mergedPairs.Add(ia);
                        mergedPairs.Add(ib);
                        break;
                    }
                }

                // ── Обнаружение последовательных цепочек n=2 ───────────────────────
                // Если A.wLast ≈ B.wFirst и у них одинаковый шаг, это линейная
                // цепочка (не шахматная).  Промежуточные точки не добавляются.
                var chainedIndices   = new HashSet<int>();
                var sequentialChains = new List<(double wFirst, double wLast,
                                                 double spacing, int nSpaces,
                                                 Reference refFirst, Reference refLast)>();

                var sortedByFirst5 = Enumerable.Range(0, allArrayData.Count)
                    .Where(i => !mergedPairs.Contains(i))
                    .OrderBy(i => allArrayData[i].wFirst)
                    .ToList();

                for (int si5 = 0; si5 < sortedByFirst5.Count; si5++)
                {
                    int ia5 = sortedByFirst5[si5];
                    if (chainedIndices.Contains(ia5)) continue;
                    var a5 = allArrayData[ia5];
                    if (a5.rebar.NumberOfBarPositions != 2) continue;
                    double spacingA5 = Math.Abs(a5.wLast - a5.wFirst);
                    if (spacingA5 < matchTol) continue;
                    if (mergedAnnotations.Any(ma =>
                            a5.wFirst >= ma.wFirst - matchTol &&
                            a5.wLast  <= ma.wLast  + matchTol)) continue;

                    var chain5      = new List<int> { ia5 };
                    double chainEnd5 = a5.wLast;
                    Reference chainRefLast5 = a5.refLast;

                    for (int si6 = si5 + 1; si6 < sortedByFirst5.Count; si6++)
                    {
                        int ib5 = sortedByFirst5[si6];
                        if (chainedIndices.Contains(ib5)) break;
                        var b5 = allArrayData[ib5];
                        if (b5.rebar.NumberOfBarPositions != 2) break;
                        if (Math.Abs(b5.wFirst - chainEnd5) > matchTol * 2) break;
                        double spacingB5 = Math.Abs(b5.wLast - b5.wFirst);
                        if (Math.Abs(spacingA5 - spacingB5) > matchTol * 2) break;
                        if (mergedAnnotations.Any(ma =>
                                b5.wFirst >= ma.wFirst - matchTol &&
                                b5.wLast  <= ma.wLast  + matchTol)) break;

                        chain5.Add(ib5);
                        chainEnd5     = b5.wLast;
                        chainRefLast5 = b5.refLast;
                    }

                    if (chain5.Count >= 2)
                    {
                        sequentialChains.Add((a5.wFirst, chainEnd5, spacingA5, chain5.Count,
                                              a5.refFirst, chainRefLast5));
                        foreach (int ci in chain5) chainedIndices.Add(ci);
                    }
                }

                // ── Строим barPositions ─────────────────────────────────────────────
                // Слитые шахматные пары → только объединённые концы
                foreach (var ma in mergedAnnotations)
                {
                    if (!barPositions.Any(p => Math.Abs(p.wCoord - ma.wFirst) < matchTol))
                        barPositions.Add((ma.wFirst, ma.refFirst));
                    if (!barPositions.Any(p => Math.Abs(p.wCoord - ma.wLast) < matchTol))
                        barPositions.Add((ma.wLast, ma.refLast));
                }

                // Последовательные цепочки → только первый и последний конец
                foreach (var sc in sequentialChains)
                {
                    if (!barPositions.Any(p => Math.Abs(p.wCoord - sc.wFirst) < matchTol))
                        barPositions.Add((sc.wFirst, sc.refFirst));
                    if (!barPositions.Any(p => Math.Abs(p.wCoord - sc.wLast) < matchTol))
                        barPositions.Add((sc.wLast, sc.refLast));
                }

                // Обычные (не слитые, не цепочечные) span-ы
                for (int idx = 0; idx < allArrayData.Count; idx++)
                {
                    if (mergedPairs.Contains(idx) || chainedIndices.Contains(idx)) continue;
                    var (wFirst, wLast, rebar, refFirst, refLast) = allArrayData[idx];

                    bool insideMerged = mergedAnnotations.Any(ma =>
                        wFirst >= ma.wFirst - matchTol &&
                        wLast  <= ma.wLast  + matchTol);
                    bool insideChain = sequentialChains.Any(sc =>
                        wFirst >= sc.wFirst - matchTol &&
                        wLast  <= sc.wLast  + matchTol);

                    if (!insideMerged && !insideChain)
                    {
                        if (!barPositions.Any(p => Math.Abs(p.wCoord - wFirst) < matchTol))
                            barPositions.Add((wFirst, refFirst));

                        if (Math.Abs(wLast - wFirst) > matchTol)
                        {
                            bool shorterSpanExists = arraySpans.Any(sp =>
                                Math.Abs(sp.wFirst - wFirst) < matchTol &&
                                sp.wLast < wLast - matchTol);

                            if (!shorterSpanExists)
                            {
                                if (!barPositions.Any(p => Math.Abs(p.wCoord - wLast) < matchTol))
                                    barPositions.Add((wLast, refLast));
                            }
                        }
                    }

                    if (Math.Abs(wLast - wFirst) > matchTol)
                        arraySpans.Add((wFirst, wLast, rebar));
                }

                if (!barPositions.Any()) continue;
                barPositions.Sort((a, b) => a.wCoord.CompareTo(b.wCoord));

                if (!barPositions.Any()) continue;

                diagSb?.Append($"    barPositions: ");
                diagSb?.AppendLine(string.Join(", ", barPositions.Select(p => $"{ToMmD(p.wCoord)}мм")));
                diagSb?.Append($"    arraySpans: ");
                diagSb?.AppendLine(string.Join(", ", arraySpans.Select(s =>
                    $"[{ToMmD(s.wFirst)}..{ToMmD(s.wLast)}] n={s.rebar.NumberOfBarPositions}")));
                diagSb?.Append($"    mergedAnnotations: ");
                diagSb?.AppendLine(string.Join(", ", mergedAnnotations.Select(ma =>
                    $"[{ToMmD(ma.wFirst)}..{ToMmD(ma.wLast)}] eff={ToMmD(ma.effSpacing)}мм n={ma.totalBars}")));
                diagSb?.Append($"    sequentialChains: ");
                diagSb?.AppendLine(string.Join(", ", sequentialChains.Select(sc =>
                    $"[{ToMmD(sc.wFirst)}..{ToMmD(sc.wLast)}] sp={ToMmD(sc.spacing)}мм n={sc.nSpaces}")));

                // Segment seg[j] is between barPositions[j-1] and barPositions[j]
                var segmentRebarMap = new Dictionary<int, Rebar>();
                for (int j = 1; j < barPositions.Count; j++)
                {
                    double w0 = barPositions[j - 1].wCoord;
                    double w1 = barPositions[j].wCoord;
                    foreach (var span in arraySpans)
                    {
                        if (Math.Abs(span.wFirst - w0) < matchTol && Math.Abs(span.wLast - w1) < matchTol)
                        {
                            segmentRebarMap[j] = span.rebar;
                            break;
                        }
                    }
                }

                diagSb?.Append($"    segmentRebarMap: ");
                diagSb?.AppendLine(string.Join(", ", segmentRebarMap.Select(kvp =>
                    $"seg{kvp.Key}→pos{kvp.Value.get_Parameter(BuiltInParameter.REBAR_ELEM_QUANTITY_OF_BARS)?.AsInteger()}шт")));

                if (!barPositions.Any()) continue;
                XYZ p1 = MakePt(segLeft.coord - margin, dimU);
                XYZ p2 = MakePt(segRight.coord + margin, dimU);
                if ((p2 - p1).GetLength() < 1e-6) continue;
                Line dimLine;
                try { dimLine = Line.CreateBound(p1, p2); }
                catch { continue; }

                var ra = new ReferenceArray();
                ra.Append(segLeft.rf);
                foreach (var bp in barPositions)
                    ra.Append(bp.rf);
                ra.Append(segRight.rf);

                if (ra.Size < 2) continue;

                try
                {
                    Dimension dim = doc.Create.NewDimension(sectionView, dimLine, ra);

                    if (dim != null && (arraySpans.Any() || mergedAnnotations.Any() || sequentialChains.Any()))
                    {
                        doc.Regenerate();
                        double threshold = UnitUtils.ConvertToInternalUnits(600, UnitTypeId.Millimeters);
                        DimensionSegmentArray segs = dim.Segments;

                        if (segs != null && diagSb != null)
                        {
                            var sbAll = new System.Text.StringBuilder("    Все сегменты: ");
                            for (int si2 = 0; si2 < segs.Size; si2++)
                            {
                                double? v2 = segs.get_Item(si2).Value;
                                sbAll.Append($"[{si2}]={ToMmD(v2 ?? 0)}мм ");
                            }
                            diagSb.AppendLine(sbAll.ToString());
                        }

                        if (segs != null)
                        {
                            // Индексный обход: сегмент si2 соответствует паре
                            // barPositions[si2-1]..barPositions[si2] (si2=0 и si2=last — покрытие).
                            // ra = [segLeft, bp[0], bp[1], ..., bp[n-1], segRight]
                            // segs.Size = barPositions.Count + 1
                            for (int si2 = 1; si2 < segs.Size - 1; si2++)
                            {
                                DimensionSegment seg = segs.get_Item(si2);
                                if (seg == null || !seg.Value.HasValue || seg.Value.Value < 1e-6) continue;
                                double segLen = seg.Value.Value;

                                int bpLeft  = si2 - 1;
                                int bpRight = si2;
                                if (bpRight >= barPositions.Count)
                                {
                                    diagSb?.AppendLine($"    seg[{si2}] ПРОПУСК: bpRight={bpRight} >= barPositions.Count={barPositions.Count}");
                                    continue;
                                }

                                double wLeft  = barPositions[bpLeft].wCoord;
                                double wRight = barPositions[bpRight].wCoord;
                                diagSb?.AppendLine($"    seg[{si2}] wLeft={ToMmD(wLeft)}мм wRight={ToMmD(wRight)}мм segLen={ToMmD(segLen)}мм");

                                // Слитые шахматные span-ы
                                int mIdx = mergedAnnotations.FindIndex(ma =>
                                    Math.Abs(ma.wFirst - wLeft)  < matchTol * 4 &&
                                    Math.Abs(ma.wLast  - wRight) < matchTol * 4);
                                if (mIdx >= 0)
                                {
                                    var ma = mergedAnnotations[mIdx];
                                    int mSpMm   = (int)Math.Round(UnitUtils.ConvertFromInternalUnits(ma.effSpacing, UnitTypeId.Millimeters));
                                    string mAnn = $"{mSpMm}х{ma.totalBars - 1}";
                                    diagSb?.AppendLine($"    seg[{si2}] bp[{bpLeft}..{bpRight}]: merged eff={mSpMm}мм n={ma.totalBars} → \"{mAnn}\"");
                                    if (segLen < threshold) seg.ValueOverride = mAnn;
                                    else                    seg.Prefix = mAnn + "=";
                                    continue;
                                }

                                // Последовательные цепочки n=2
                                int scIdx = sequentialChains.FindIndex(sc =>
                                    Math.Abs(sc.wFirst - wLeft)  < matchTol * 4 &&
                                    Math.Abs(sc.wLast  - wRight) < matchTol * 4);
                                if (scIdx >= 0)
                                {
                                    var sc = sequentialChains[scIdx];
                                    int scSpMm   = (int)Math.Round(UnitUtils.ConvertFromInternalUnits(sc.spacing, UnitTypeId.Millimeters));
                                    string scAnn = $"{scSpMm}х{sc.nSpaces}";
                                    diagSb?.AppendLine($"    seg[{si2}] bp[{bpLeft}..{bpRight}]: seqChain sp={scSpMm}мм n={sc.nSpaces} → \"{scAnn}\"");
                                    if (segLen < threshold) seg.ValueOverride = scAnn;
                                    else                    seg.Prefix = scAnn + "=";
                                    continue;
                                }

                                // Обычный массив
                                var matchSpan = arraySpans.FirstOrDefault(sp =>
                                    Math.Abs(sp.wFirst - wLeft)  < matchTol * 4 &&
                                    Math.Abs(sp.wLast  - wRight) < matchTol * 4);
                                if (matchSpan.rebar == null)
                                {
                                    diagSb?.AppendLine($"    seg[{si2}] → НЕТ СОВПАДЕНИЯ (нет массива для wLeft={ToMmD(wLeft)} wRight={ToMmD(wRight)})");
                                    diagSb?.AppendLine($"      доступные spans: {string.Join("; ", arraySpans.Select(sp => $"[{ToMmD(sp.wFirst)}..{ToMmD(sp.wLast)}]"))}");
                                    diagSb?.AppendLine($"      merged: {string.Join("; ", mergedAnnotations.Select(ma => $"[{ToMmD(ma.wFirst)}..{ToMmD(ma.wLast)}]"))}");
                                    continue;
                                }

                                int cnt = matchSpan.rebar.NumberOfBarPositions;
                                if (cnt <= 2)
                                {
                                    diagSb?.AppendLine($"    seg[{si2}] → ПРОПУСК: span найден но cnt={cnt} <= 2");
                                    continue;
                                }

                                double sp3 = 0;
                                Parameter sp2p = matchSpan.rebar.get_Parameter(BuiltInParameter.REBAR_ELEM_BAR_SPACING);
                                if (sp2p != null && sp2p.HasValue) sp3 = sp2p.AsDouble();
                                if (sp3 < 1e-6) sp3 = segLen / (cnt - 1);

                                int spMm  = (int)Math.Round(UnitUtils.ConvertFromInternalUnits(sp3, UnitTypeId.Millimeters));
                                string ann = $"{spMm}х{cnt - 1}";
                                diagSb?.AppendLine($"    seg[{si2}] bp[{bpLeft}..{bpRight}]: span sp={spMm}мм n={cnt} → \"{ann}\"");
                                if (segLen < threshold) seg.ValueOverride = ann;
                                else                    seg.Prefix = ann + "=";
                            }
                        }

                    }
                }
                catch (Exception ex)
                {
                    TaskDialog.Show("Размеры", $"Ошибка создания размерной цепи: {ex.Message}");
                }
            }
        }

        // Проверяет, попадает ли грань (хотя бы частично) в CropBox вида.
        // wall.get_Geometry возвращает полную 3D-геометрию стены, включая грани
        // проёмов, расположенных за пределами видимой (обрезанной) области вида —
        // без этой проверки такие грани ложно считаются границами сегмента стены.
        private static bool FaceOverlapsCrop(View view, Face face)
        {
            if (!view.CropBoxActive) return true;
            BoundingBoxXYZ crop = view.CropBox;
            if (crop == null) return true;

            Transform toCrop = crop.Transform.Inverse;
            double tol = 1e-4;

            double minX = double.MaxValue, maxX = double.MinValue;
            double minY = double.MaxValue, maxY = double.MinValue;
            double minZ = double.MaxValue, maxZ = double.MinValue;

            foreach (EdgeArray loop in face.EdgeLoops)
            {
                foreach (Edge e in loop)
                {
                    foreach (XYZ wp in e.Tessellate())
                    {
                        XYZ l = toCrop.OfPoint(wp);
                        if (l.X < minX) minX = l.X; if (l.X > maxX) maxX = l.X;
                        if (l.Y < minY) minY = l.Y; if (l.Y > maxY) maxY = l.Y;
                        if (l.Z < minZ) minZ = l.Z; if (l.Z > maxZ) maxZ = l.Z;
                    }
                }
            }
            if (minX > maxX) return true; // не смогли определить — не исключаем

            bool overlapX = maxX >= crop.Min.X - tol && minX <= crop.Max.X + tol;
            bool overlapY = maxY >= crop.Min.Y - tol && minY <= crop.Max.Y + tol;
            bool overlapZ = maxZ >= crop.Min.Z - tol && minZ <= crop.Max.Z + tol;
            return overlapX && overlapY && overlapZ;
        }

        private static List<(Reference rf, double wCoord, double uCoord)> GetVerticalBarLineRefs(
            Rebar rebar, View view, XYZ wallDir, XYZ upDir)
        {
            var result = new List<(Reference rf, double wCoord, double uCoord)>();
            Options opt = new Options
            {
                View = view,
                ComputeReferences = true,
                IncludeNonVisibleObjects = true
            };
            GeometryElement geom = rebar.get_Geometry(opt);
            if (geom == null) return result;
            CollectVerticalLineRefs(geom, wallDir, upDir, result);
            return result;
        }

        private static void CollectVerticalLineRefs(
            GeometryElement geom, XYZ wallDir, XYZ upDir,
            List<(Reference rf, double wCoord, double uCoord)> acc)
        {
            foreach (GeometryObject obj in geom)
            {
                if (obj is GeometryInstance gi)
                {
                    CollectVerticalLineRefs(gi.GetInstanceGeometry(), wallDir, upDir, acc);
                    continue;
                }
                if (obj is Line ln && ln.Reference != null)
                {
                    AddIfVertical(ln, ln.Reference, wallDir, upDir, acc);
                    continue;
                }
                if (obj is Solid solid && solid.Edges.Size > 0)
                {
                    foreach (Edge e in solid.Edges)
                    {
                        if (e.AsCurve() is Line el && e.Reference != null)
                            AddIfVertical(el, e.Reference, wallDir, upDir, acc);
                    }
                }
            }
        }

        private static void AddIfVertical(
            Line ln, Reference rf, XYZ wallDir, XYZ upDir,
            List<(Reference rf, double wCoord, double uCoord)> acc)
        {
            if (rf == null) return;
            if (Math.Abs(ln.Direction.Normalize().Z) < 0.99) return;
            XYZ p0 = ln.GetEndPoint(0);
            XYZ p1 = ln.GetEndPoint(1);
            double wCoord = p0.DotProduct(wallDir);
            // uCoord = самый нижний конец ребра (largest uProj = нижний ряд).
            // GetEndPoint(0) — произвольный конец (направление ребра у разных
            // "ножек" одной шпильки/хомута может быть развёрнуто по-разному),
            // поэтому без Max() сравнение верх/низ между рёбрами не надёжно.
            double uCoord = Math.Max(p0.DotProduct(upDir), p1.DotProduct(upDir));
            acc.Add((rf, wCoord, uCoord));
        }

        private static Reference GetTagRef(Rebar rebar)
        {
            if (rebar == null) return null;
            IList<Subelement> subs = rebar.GetSubelements();
            if (subs != null && subs.Count > 0)
                return subs[0].GetReference();
            try { return new Reference(rebar); }
            catch { return null; }
        }

        // ── Марки горизонтальных стержней формы 1 (верхний / нижний) ────────
        // Выноска: вертикально вверх (100 мм) → горизонтальная полка вправо (200 мм).
        // Оба тега над дальней гранью стены, по W в одном месте.
        private static void PlaceHorizontalBarTags(
            Document doc, ViewSection sectionView,
            XYZ wallDir, XYZ upDir, double cutZ,
            Line wallLine,
            List<(Element elem, Reference tagRef, int pos, double diam, double wProj, double uProj)> hItems)
        {
            if (!hItems.Any()) return;

            FamilySymbol tagTick = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .OfCategory(BuiltInCategory.OST_RebarTags)
                .Cast<FamilySymbol>()
                .FirstOrDefault(fs => fs.Name.Equals("Позиция_с засечкой",
                                                      StringComparison.OrdinalIgnoreCase));
            if (tagTick == null) return;
            if (!tagTick.IsActive) tagTick.Activate();

            // wallDir × upDir — оба горизонтальны, их крест = вертикаль (Z).
            // Полка идёт вправо в плане = в направлении wallDir.
            XYZ MakePt(double w, double u) =>
                wallDir.Multiply(w) + upDir.Multiply(u) + XYZ.BasisZ.Multiply(cutZ);

            double tagW = hItems.Average(r => r.wProj);

            // "Выше" = уменьшение u (upDir = -Y, уменьшение u → бо́льший Y → дальняя грань и за ней).
            double wallMinU  = hItems.Min(r => r.uProj);
            double vertLen   = UnitUtils.ConvertToInternalUnits(100, UnitTypeId.Millimeters); // вертикальная часть
            double shelfLen  = UnitUtils.ConvertToInternalUnits(200, UnitTypeId.Millimeters); // горизонтальная полка
            double tagGap    = UnitUtils.ConvertToInternalUnits(200, UnitTypeId.Millimeters); // зазор между двумя марками

            // farBar  = дальняя грань (выше в плане, меньший u)
            // nearBar = ближняя грань (ниже в плане, больший u)
            var farBar  = hItems.OrderBy(r => r.uProj).First();
            var nearBar = hItems.OrderByDescending(r => r.uProj).First();

            // Локальная функция: размещает один тег с полкой вправо.
            // elbowU — U-координата излома (конец вертикали / начало полки).
            void PlaceTagWithShelf(
                (Element elem, Reference tagRef, int pos, double diam, double wProj, double uProj) bar,
                double elbowU)
            {
                XYZ anchor  = MakePt(tagW,             bar.uProj); // засечка на стержне
                XYZ elbow   = MakePt(tagW,             elbowU);    // излом: вертикаль → полка
                XYZ tagHead = MakePt(tagW + shelfLen,  elbowU);    // конец полки = голова тега
                try
                {
                    var tag = IndependentTag.Create(doc, tagTick.Id, sectionView.Id,
                        bar.tagRef, true, TagOrientation.Horizontal, tagHead);
                    tag.LeaderEndCondition = LeaderEndCondition.Free;
                    tag.SetLeaderEnd(bar.tagRef, anchor);
                    tag.SetLeaderElbow(bar.tagRef, elbow);
                    tag.TagHeadPosition = tagHead;
                }
                catch { }
            }

            // Оба тега на одной высоте — излом на wallMinU - vertLen
            double sharedElbowU = wallMinU - vertLen;

            PlaceTagWithShelf(farBar, sharedElbowU);
            if (nearBar.elem.UniqueId != farBar.elem.UniqueId)
                PlaceTagWithShelf(nearBar, sharedElbowU);
        }

        private class WallFilter : ISelectionFilter
        {
            public bool AllowElement(Element elem) => elem is Wall;
            public bool AllowReference(Reference reference, XYZ position) => false;
        }
    }
}
