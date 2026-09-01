using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;

namespace LiraToRevit.Rebar
{
    // ────────────────────────────────────────────────────────────
    //  Размещение допармирования — тонкий оркестратор.
    //
    //  Модели данных (ZoneDef/PlacementSettings/Band/PlacedBars/PlacementResult) — RebarModels.cs.
    //  Геометрия плиты (верх/низ, контур, изолинии, обрезка по кромке) — SlabGeometry.cs.
    //  Разбивка Г/П-контура зоны на прямоугольные полосы — BandSplitter.cs.
    //  Конфликты между РАЗНЫМИ зонами по длине (сдвиг 20мм) — ZoneConflictResolver.cs.
    //  Подбор типа арматуры по зоне / инвентаризация недостающих типов — RebarTypeResolver.cs.
    //  Поиск пилонов/колонн у края плиты (выбор Г/П формы) — SupportDetector.cs.
    //  Чистый расчёт длин/координат/деления длинных зон — BarLengthCalculator.cs.
    //  Постройка самих Rebar-элементов в Revit по готовым планам — RebarBuilder.cs.
    // ────────────────────────────────────────────────────────────

    public class RebarPlacer
    {
        private readonly Document _doc;
        private readonly PlacementSettings _s;
        private readonly WorksetId _worksetId;
        private readonly RebarShape _lShape;     // "(форма)11" — Г-образный, null если не найдена
        private readonly RebarShape _uShape;     // "(форма)21" — П-образный, null если не найдена

        private readonly RebarTypeResolver _typeResolver;
        private readonly SupportDetector _supportDetector;
        private readonly ZoneConflictResolver _conflictResolver;
        private readonly BarLengthCalculator _calculator;
        private readonly RebarBuilder _builder;

        public RebarPlacer(Document doc, PlacementSettings settings)
        {
            _doc = doc;
            _s = settings;

            _typeResolver = new RebarTypeResolver(doc, settings);
            _worksetId = ResolveWorkset(doc, settings.WorksetName);

            var (gridXs, gridYs) = ResolveGrids(doc);
            _calculator = new BarLengthCalculator(settings, gridXs, gridYs);

            var shapes = new FilteredElementCollector(doc).OfClass(typeof(RebarShape)).Cast<RebarShape>().ToList();
            _lShape = shapes.FirstOrDefault(sh => sh.Name == settings.LShapeName);
            _uShape = shapes.FirstOrDefault(sh => sh.Name == settings.UShapeName);

            _supportDetector = new SupportDetector(doc, settings);
            _conflictResolver = new ZoneConflictResolver(settings);
            _builder = new RebarBuilder(doc, settings, _worksetId);
        }

        private static WorksetId ResolveWorkset(Document doc, string name)
        {
            if (string.IsNullOrEmpty(name) || !doc.IsWorkshared) return null;
            var ws = new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset)
                .FirstOrDefault(w => w.Name == name);
            return ws?.Id;
        }

        private static (List<double> Xs, List<double> Ys) ResolveGrids(Document doc)
        {
            var xs = new List<double>();
            var ys = new List<double>();
            foreach (var g in new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>())
            {
                if (!(g.Curve is Line line)) continue;      // дуговые оси для привязки не используем
                var p0 = line.GetEndPoint(0);
                var p1 = line.GetEndPoint(1);
                if (Math.Abs(p1.Y - p0.Y) >= Math.Abs(p1.X - p0.X))
                    xs.Add((p0.X + p1.X) / 2.0);             // ось тянется вдоль Y → «вертикальная», координата X
                else
                    ys.Add((p0.Y + p1.Y) / 2.0);             // ось тянется вдоль X → «горизонтальная», координата Y
            }
            return (xs, ys);
        }

        // ────────────────────────────────────────────────────────
        //  Основной проход
        // ────────────────────────────────────────────────────────
        public PlacementResult Place(Floor host, IEnumerable<ZoneDef> zones)
        {
            var res = new PlacementResult();
            var accepted = zones.Where(z => z.Accepted).ToList();   // — только подтверждённые

            _conflictResolver.ResolveLengthOverlaps(accepted);

            var missing = _typeResolver.MissingTypes(accepted);
            if (missing.Any())
            {
                res.Errors.AddRange(missing.Select(m => $"В проекте нет типа {m}"));
                return res;                                          // размещение не начинаем
            }

            if (!string.IsNullOrEmpty(_s.WorksetName) && _worksetId == null)
                res.Errors.Add(_doc.IsWorkshared
                    ? $"Рабочий набор «{_s.WorksetName}» не найден — стержни остались в текущем рабочем наборе"
                    : "Модель несовместная — рабочий набор не назначен");

            var slab = SlabGeometry.From(_doc, host);
            string mark = host.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString();

            using (var t = new Transaction(_doc, "Допармирование по расчёту КЖ"))
            {
                t.Start();
                foreach (var z in accepted)
                {
                    var barType = _typeResolver.FindType(z);
                    foreach (var band in BandSplitter.SplitToBands(z))
                    {
                        var barsList = PlaceBand(host, slab, z, band, barType, mark);
                        if (barsList == null) continue;
                        foreach (var bars in barsList)
                        {
                            res.Bars.Add(bars);

                            // Сдвиг при конфликте по длине между РАЗНЫМИ зонами (см.
                            // ZoneConflictResolver.ResolveLengthOverlaps).
                            if (z.NeedsAcrossShift) _builder.ApplyAcrossShift(bars.RebarId, z.Dir);
                        }
                    }
                }
                t.Commit();
            }
            return res;
        }

        // ────────────────────────────────────────────────────────
        //  Раскладка одной полосы: считаем план (BarLengthCalculator), строим по нему (RebarBuilder)
        // ────────────────────────────────────────────────────────
        private List<PlacedBars> PlaceBand(Floor host, SlabGeometry slab, ZoneDef z, Band band, RebarBarType type, string mark)
        {
            double step = z.Step;                                   // мм
            double across1Mm = RebarUnits.ToMm(band.Across1);
            double across2Mm = RebarUnits.ToMm(band.Across2);

            // — граница зоны (band.Across1/2) приходит из редактора УЖЕ выровненной по
            // «изолиниям» — но это лишь клиентская (JS) копия того же расчёта, и в краевых
            // случаях (объединение зон, зоны у отверстий и т.п.) может разойтись с исходным
            // списком на десяток-другой мм. Пересчитываем обе границы через NearestIsoline —
            // притягиваем к КАНОНИЧЕСКОЙ сетке изолиний из этого же SlabGeometry, чтобы соседние
            // зоны с почти совпадающими границами гарантированно сходились на одну и ту же
            // изолинию, а не расходились на её долю.
            double first = slab.NearestIsoline(across1Mm, z.Dir);
            double last = slab.NearestIsoline(across2Mm, z.Dir);

            double usable = last - first;                           // доступно под раскладку — границы уже отступ
            int count = usable < 0 ? 0 : (int)Math.Floor(usable / step + 1e-6) + 1;
            if (count < 1) return null;                             // полоса слишком узкая — стержень не встаёт

            // — длина: габарит + анкеровка, с подрезкой по кромке —
            double la = _s.Anchorage.TryGetValue(z.Diameter, out var v) ? v : 400;
            double p1 = RebarUnits.ToMm(band.Along1) - la;
            double p2 = RebarUnits.ToMm(band.Along2) + la;

            bool clipped1, clipped2;
            double mid = RebarUnits.ToMm((band.Across1 + band.Across2) / 2.0);
            slab.ClipAlong(z.Dir, ref p1, ref p2, mid, _s.EndOffset, out clipped1, out clipped2);

            // Ровно один торец обрезан краем плиты/отверстия (оба сразу — уже отсеяно ниже,
            // при обоих clipped распределить добавку по стандартной длине некуда) и это верхняя
            // допка — вместо прямого стержня с обрезкой строим Г- или П-образный (реальный загиб,
            // а не текстовая пометка "требуется загиб"). Для фундаментов (AlwaysStraight) загиб не
            // делаем вообще — там стержни всегда прямые. Глобальный BendTopBars=false — то же самое,
            // но по явному выбору пользователя в "⚙ Настройки", а не по категории элемента, и главнее
            // формы отдельной зоны (см. PlacementSettings.BendTopBars). Явный выбор ЭТОЙ зоны
            // (z.ShapeMode=Straight — карточка зоны) тоже отключает гибку только для неё. Нужная
            // форма (или хотя бы Г — запасной вариант для Auto) должна существовать в проекте,
            // иначе, как и раньше, стержень остаётся прямым с обрезкой.
            bool shapeAvailable = z.ShapeMode == TopBarShapeMode.UShape ? _uShape != null : _lShape != null;
            bool bendable = !_s.AlwaysStraight && _s.BendTopBars && z.ShapeMode != TopBarShapeMode.Straight
                && z.Face == Face.Top && (clipped1 ^ clipped2) && shapeAvailable;

            if (bendable)
                return PlaceBendableBand(host, slab, z, type, mark, p1, p2, clipped1, clipped2, first, count, step);

            return PlaceStraightBand(host, slab, z, type, mark, p1, p2, clipped1, clipped2, first, count, step);
        }

        private List<PlacedBars> PlaceStraightBand(Floor host, SlabGeometry slab, ZoneDef z, RebarBarType type, string mark,
            double p1, double p2, bool clipped1, bool clipped2, double first, int count, double step)
        {
            var plans = _calculator.ComputeStraight(z, p1, p2, clipped1, clipped2);

            var result = new List<PlacedBars>();
            for (int i = 0; i < plans.Count; i++)
            {
                var plan = plans[i];
                // "Лесенка" — на наклонной плите длинный массив поперёк уклона разбивается на
                // несколько горизонтальных ступеней вместо одной общей отметки на весь массив,
                // см. RebarBuilder.ComputeRungs.
                var rungs = _builder.ComputeRungs(slab, z, plan.P1, plan.P2, first, step, count);
                foreach (var rung in rungs)
                {
                    var rebar = _builder.CreateStraightRebarElement(host, z, type, mark, plan, rung.AcrossStartMm, rung.ZBar, rung.Count, step);
                    _builder.AssignWorkset(rebar);
                    // При делении зоны на два стыкуемых внахлёст массива (plans.Count==2) второй
                    // (ближний к p2) массив сдвигается на 20мм поперёк — см. класс-док BarLengthCalculator.
                    // Применяется к КАЖДОЙ ступени этой половины.
                    if (plans.Count == 2 && i == 1) _builder.ApplyAcrossShift(rebar.Id, z.Dir);

                    result.Add(new PlacedBars
                    {
                        ZoneId = z.Id,
                        TypeName = type.Name,
                        Count = rung.Count,
                        LengthMm = plan.LengthMm,
                        NeedsHook = plan.NeedsHook,
                        RebarId = rebar.Id
                    });
                }
            }
            return result;
        }

        private List<PlacedBars> PlaceBendableBand(Floor host, SlabGeometry slab, ZoneDef z, RebarBarType type, string mark,
            double p1, double p2, bool clipped1, bool clipped2, double first, int count, double step)
        {
            // Анкеровка — только с дальней стороны (torec у кромки уже отдаёт анкеровку через
            // сам загиб, вторая анкеровка внахлёст не нужна): дальний торец сохраняет полный
            // отступ la (как в прямой ветке), ближний — там, где ClipAlong уже остановил его
            // у истинного края плиты (p1/p2 после ClipAlong выше).
            bool bendAtP2 = clipped2;
            double origFarAlong = bendAtP2 ? p1 : p2;     // анкеровка вглубь зоны (для расчёта общего бюджета)
            double edgeNearAlong = bendAtP2 ? p2 : p1;    // положение у истинного края (после ClipAlong)

            // Выбор формы (Г vs П) зависит только от места загиба (edgeNearAlong) — не от того,
            // делим ли зону на два массива или нет (при делении место загиба не меняется), поэтому
            // решаем один раз здесь и переиспользуем в обеих ветках ниже. При явном выборе формы
            // ЭТОЙ зоны (z.ShapeMode ≠ Auto — карточка зоны в редакторе) форма фиксирована и
            // автодетект пилона/колонны (SupportDetector) не запускается вообще; UShape гарантированно
            // доступна здесь — bendable выше уже отсеял случай отсутствующей формы.
            // Z нужен только для сверки высоты опоры (SupportDetector) — берём худшую отметку по
            // всему диапазону загиба (та же логика, что и для самого элемента), а не одну точку.
            double acrossEnd = first + (count - 1) * step;
            double zAtBend = _builder.BandZAt(slab, z, Math.Min(origFarAlong, edgeNearAlong), Math.Max(origFarAlong, edgeNearAlong), first, acrossEnd);
            bool useU = z.ShapeMode == TopBarShapeMode.UShape ? true
                : z.ShapeMode == TopBarShapeMode.LShape ? false
                : _uShape != null && _supportDetector.HasParallelSupport(slab, z.Dir, first, step, count, edgeNearAlong, zAtBend);
            RebarShape shape = useU ? _uShape : _lShape;
            double noseTotalMm = useU ? (_s.UShapeDepthMm + _s.UShapeFootMm) : _s.LShapeNoseMm;

            if (_calculator.NeedsBentSplit(origFarAlong, edgeNearAlong))
            {
                var split = _calculator.ComputeBentSplit(z, origFarAlong, edgeNearAlong, bendAtP2, noseTotalMm);
                var result = new List<PlacedBars>();

                var farRungs = _builder.ComputeRungs(slab, z, split.Far.P1, split.Far.P2, first, step, count);
                foreach (var rung in farRungs)
                {
                    var rebarA = _builder.CreateStraightRebarElement(host, z, type, mark, split.Far, rung.AcrossStartMm, rung.ZBar, rung.Count, step);
                    _builder.AssignWorkset(rebarA);
                    result.Add(new PlacedBars { ZoneId = z.Id, TypeName = type.Name, Count = rung.Count, LengthMm = split.Far.LengthMm, NeedsHook = split.Far.NeedsHook, RebarId = rebarA.Id });
                }

                double nearAlong1 = Math.Min(split.Near.FarAlong, split.Near.NearAlong);
                double nearAlong2 = Math.Max(split.Near.FarAlong, split.Near.NearAlong);
                var nearRungs = _builder.ComputeRungs(slab, z, nearAlong1, nearAlong2, first, step, count);
                foreach (var rung in nearRungs)
                {
                    var rebarB = _builder.CreateBentRebarElement(host, z, type, mark, useU, shape, split.Near, rung.AcrossStartMm, rung.ZBar, rung.Count, step);
                    _builder.AssignWorkset(rebarB);
                    // Ближний (бендовый) массив сдвигается на 20мм поперёк — см. класс-док
                    // BarLengthCalculator. Применяется к КАЖДОЙ его ступени.
                    _builder.ApplyAcrossShift(rebarB.Id, z.Dir);
                    result.Add(new PlacedBars { ZoneId = z.Id, TypeName = type.Name, Count = rung.Count, LengthMm = split.Near.TotalLenMm, NeedsHook = false, RebarId = rebarB.Id });
                }

                return result;
            }

            var plan = _calculator.ComputeBent(z, origFarAlong, edgeNearAlong, bendAtP2, noseTotalMm);
            double bentAlong1 = Math.Min(origFarAlong, edgeNearAlong);
            double bentAlong2 = Math.Max(origFarAlong, edgeNearAlong);
            var bentRungs = _builder.ComputeRungs(slab, z, bentAlong1, bentAlong2, first, step, count);
            var bentResult = new List<PlacedBars>();
            foreach (var rung in bentRungs)
            {
                var rebar = _builder.CreateBentRebarElement(host, z, type, mark, useU, shape, plan, rung.AcrossStartMm, rung.ZBar, rung.Count, step);
                _builder.AssignWorkset(rebar);
                bentResult.Add(new PlacedBars { ZoneId = z.Id, TypeName = type.Name, Count = rung.Count, LengthMm = plan.TotalLenMm, NeedsHook = false, RebarId = rebar.Id });
            }
            return bentResult;
        }
    }
}
