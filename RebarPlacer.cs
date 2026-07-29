using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;

namespace LiraToRevit.Rebar
{
    // ────────────────────────────────────────────────────────────
    //  Модель данных: то, что приходит с экрана подтверждения
    // ────────────────────────────────────────────────────────────

    public enum Face { Top, Bottom }
    public enum Dir { X, Y }

    /// <summary>Подтверждённая зона допармирования (одна на один слой).</summary>
    public class ZoneDef
    {
        public string Id;                 // "d12-M17"
        public Face Face;                 // верх / низ — из легенды DXF
        public Dir Dir;                   // вдоль X / вдоль Y — из легенды DXF
        public int Diameter;              // 10,12,14,16,18,20
        public int Step;                  // 100 или 200, мм
        public List<XYZ> Polygon;         // ортогональный контур в координатах модели, мм (Z игнорируется)
        public bool ByMean;               // решение принято по среднему
        public double AsCalc;             // расчётное As (пик), см²/м
        public string Source;             // "расчёт" / "пересечение" / "вложение" / "вручную"
        public bool Accepted;             // размещаем только подтверждённые
    }

    public class PlacementSettings
    {
        /// <summary>От грани плиты до наружной грани стержня, мм. Защитный слой в типе плиты = 0.</summary>
        public double FaceOffset = 35.0;

        /// <summary>Отступ от торца плиты / края отверстия до центра (оси) конца стержня, мм
        /// (вдоль стержня). Кривая стержня в Rebar API — осевая линия, это расстояние до центра.</summary>
        public double EndOffset = 20.0;

        /// <summary>Длина анкеровки за границы зоны: 55d, мм.</summary>
        public Dictionary<int, double> Anchorage = new Dictionary<int, double>
        {
            {10, 550}, {12, 660}, {14, 770}, {16, 880}, {18, 990}, {20, 1100},
            {22, 1210}, {25, 1380}, {28, 1540}
        };

        /// <summary>Ряд длин прямых стержней (заготовки из 11700), мм.</summary>
        public double[] StandardLengths =
        {
            1670, 1950, 2340, 2920, 3900, 4880, 5850, 6850, 7800, 8770, 9750, 11700
        };

        /// <summary>Ближайшая бо́льшая длина из ряда; null — длиннее максимальной.</summary>
        public double? SnapLength(double needMm)
        {
            foreach (var L in StandardLengths) if (L >= needMm - 1e-6) return L;
            return null;
        }

        /// <summary>Какое направление лежит ближе к грани плиты (первый слой).</summary>
        public Dir FirstLayer = Dir.X;

        /// <summary>Диаметр стержней первого слоя для отступа второго слоя, мм (обычно диаметр зоны).</summary>
        public double FirstLayerThickness = 10.0;


        /// <summary>Шаблон имени типа: {F}=В/Н, {D}=x/y, {d}=диаметр.</summary>
        public string TypeNameTemplate = "плита_доп_{F}{D}_d={d}_А500";

        /// <summary>Рабочий набор для создаваемых стержней; null/"" — не менять (оставить текущий).
        /// Молча игнорируется, если модель несовместная или набора с таким именем нет.</summary>
        public string WorksetName = ".#06_Арм_Плиты";
    }

    // ────────────────────────────────────────────────────────────
    //  Размещение
    // ────────────────────────────────────────────────────────────

    public class RebarPlacer
    {
        private readonly Document _doc;
        private readonly PlacementSettings _s;
        private readonly List<(string Norm, RebarBarType Type)> _types;
        private readonly WorksetId _worksetId;
        private readonly List<double> _gridXs;   // координаты X «вертикальных» осей (тянутся вдоль Y)
        private readonly List<double> _gridYs;   // координаты Y «горизонтальных» осей (тянутся вдоль X)

        public RebarPlacer(Document doc, PlacementSettings settings)
        {
            _doc = doc;
            _s = settings;
            _types = new FilteredElementCollector(doc)
                .OfClass(typeof(RebarBarType)).Cast<RebarBarType>()
                .Select(t => (Normalize(t.Name), t))
                .ToList();
            _worksetId = ResolveWorkset(doc, settings.WorksetName);
            (_gridXs, _gridYs) = ResolveGrids(doc);
        }

        private static WorksetId ResolveWorkset(Document doc, string name)
        {
            if (string.IsNullOrEmpty(name) || !doc.IsWorkshared) return null;
            var ws = new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset)
                .FirstOrDefault(w => w.Name == name);
            return ws?.Id;
        }

        private void AssignWorkset(Element e)
        {
            if (_worksetId == null) return;
            var p = e.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);
            if (p != null && !p.IsReadOnly) p.Set(_worksetId.IntValue());
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

        /// <summary>
        /// Сдвигает оба конца стержня вдоль его направления на одинаковую величину так, чтобы
        /// расстояние от начала (p1) до ближайшей оси, параллельной направлению стержня, было
        /// кратно 10 мм. Длина стержня (p2−p1) уже взята из ряда стандартных длин — все они кратны
        /// 10 мм, поэтому такой сдвиг делает «чистым» одновременно и второй конец. Без осей — 0.
        /// </summary>
        private double SnapAlongShift(double p1Mm, double p2Mm, Dir dir)
        {
            var axes = dir == Dir.X ? _gridXs : _gridYs;     // ось того же типа координаты, что p1/p2
            if (axes.Count == 0) return 0;

            double mid = (p1Mm + p2Mm) / 2.0;
            double axisMm = ToMm(axes.OrderBy(a => Math.Abs(ToMm(a) - mid)).First());
            double dist = p1Mm - axisMm;
            double roundedDist = Math.Round(dist / 10.0) * 10.0;
            return roundedDist - dist;
        }

        // — единицы: модель Revit во внутренних футах —
        private static double Mm(double mm) => UnitUtils.ConvertToInternalUnits(mm, UnitTypeId.Millimeters);
        private static double ToMm(double ft) => UnitUtils.ConvertFromInternalUnits(ft, UnitTypeId.Millimeters);

        /// <summary>Нормализация имени типа: регистр, пробелы, кириллические/латинские х-у-в-н.</summary>
        private static string Normalize(string s)
        {
            if (s == null) return "";
            s = s.ToLowerInvariant().Replace(" ", "");
            return s.Replace('х', 'x').Replace('у', 'y').Replace('в', 'b').Replace('н', 'h');
        }

        private string TypeNameFor(ZoneDef z) => _s.TypeNameTemplate
            .Replace("{F}", z.Face == Face.Top ? "В" : "Н")
            .Replace("{D}", z.Dir == Dir.X ? "x" : "y")
            .Replace("{d}", z.Diameter.ToString());

        /// <summary>
        /// Отличительная часть имени типа (грань+направление+диаметр) без префиксов/суффиксов —
        /// реальные имена в проекте часто содержат что-то вроде "(арматура)плита_доп_Вх_d=10_А500",
        /// поэтому ищем ПОДСТРОКОЙ, а не точным совпадением всего имени с шаблоном.
        /// </summary>
        private static string CoreSignature(ZoneDef z) =>
            (z.Face == Face.Top ? "b" : "h") + (z.Dir == Dir.X ? "x" : "y") + "_d=" + z.Diameter + "_";

        private RebarBarType FindType(ZoneDef z)
        {
            string core = CoreSignature(z);
            return _types.FirstOrDefault(t => t.Norm.Contains(core)).Type;
        }

        // ────────────────────────────────────────────────────────
        //  1. Инвентаризация типов — до начала размещения
        // ────────────────────────────────────────────────────────
        public List<string> MissingTypes(IEnumerable<ZoneDef> zones)
        {
            return zones.Where(z => z.Accepted)
                        .GroupBy(z => CoreSignature(z))
                        .Where(g => !_types.Any(t => t.Norm.Contains(g.Key)))
                        .Select(g => TypeNameFor(g.First()))
                        .ToList();
        }

        // ────────────────────────────────────────────────────────
        //  2. Основной проход
        // ────────────────────────────────────────────────────────
        public PlacementResult Place(Floor host, IEnumerable<ZoneDef> zones)
        {
            var res = new PlacementResult();
            var accepted = zones.Where(z => z.Accepted).ToList();   // — только подтверждённые

            var missing = MissingTypes(accepted);
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
                    var barType = FindType(z);
                    foreach (var band in SplitToBands(z))
                    {
                        var bars = PlaceBand(host, slab, z, band, barType, mark);
                        if (bars != null) res.Bars.Add(bars);
                    }
                }
                t.Commit();
            }
            return res;
        }

        // ────────────────────────────────────────────────────────
        //  3. Разбивка Г/П-образной зоны на прямоугольные полосы
        //     Резы — поперёк стержней (по уровням вершин).
        // ────────────────────────────────────────────────────────
        public static List<Band> SplitToBands(ZoneDef z)
        {
            bool alongX = z.Dir == Dir.X;
            // координата поперёк стержней: для стержней вдоль X режем по Y, и наоборот
            var cuts = z.Polygon.Select(p => alongX ? p.Y : p.X)
                                .Select(v => Math.Round(v, 4))
                                .Distinct().OrderBy(v => v).ToList();

            var bands = new List<Band>();
            for (int i = 0; i < cuts.Count - 1; i++)
            {
                double mid = (cuts[i] + cuts[i + 1]) / 2.0;
                foreach (var (a, b) in Crossings(z.Polygon, mid, alongX))
                    bands.Add(new Band
                    {
                        Along1 = a,                 // начало вдоль стержня
                        Along2 = b,                 // конец вдоль стержня
                        Across1 = cuts[i],          // низ полосы поперёк
                        Across2 = cuts[i + 1]
                    });
            }
            return bands;
        }

        /// <summary>Пересечения контура горизонталью (для along X) или вертикалью (для along Y) — парами.</summary>
        private static IEnumerable<(double, double)> Crossings(List<XYZ> poly, double mid, bool alongX)
        {
            var xs = new List<double>();
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            {
                double v1 = alongX ? poly[j].Y : poly[j].X;
                double v2 = alongX ? poly[i].Y : poly[i].X;
                if ((v1 > mid) == (v2 > mid)) continue;
                double u1 = alongX ? poly[j].X : poly[j].Y;
                double u2 = alongX ? poly[i].X : poly[i].Y;
                xs.Add(u1 + (mid - v1) / (v2 - v1) * (u2 - u1));
            }
            xs.Sort();
            for (int k = 0; k + 1 < xs.Count; k += 2) yield return (xs[k], xs[k + 1]);
        }

        // ────────────────────────────────────────────────────────
        //  4. Раскладка одной полосы
        // ────────────────────────────────────────────────────────
        private PlacedBars PlaceBand(Floor host, SlabGeometry slab, ZoneDef z, Band band, RebarBarType type, string mark)
        {
            double step = z.Step;                                   // мм
            double across1Mm = ToMm(band.Across1);
            double across2Mm = ToMm(band.Across2);

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
            double p1 = ToMm(band.Along1) - la;
            double p2 = ToMm(band.Along2) + la;

            bool clipped1 = false, clipped2 = false;
            double mid = ToMm((band.Across1 + band.Across2) / 2.0);
            slab.ClipAlong(z.Dir, ref p1, ref p2, mid, _s.EndOffset, out clipped1, out clipped2);

            // — длина берётся из ряда заготовок, а не округлением —
            double len = p2 - p1;
            var std = _s.SnapLength(len);
            if (std == null)
                throw new InvalidOperationException(
                    $"Зона {z.Id}: требуемая длина {len:0} мм превышает максимальную {_s.StandardLengths.Last():0} мм — нужен стык или деление зоны");

            double extra = std.Value - len;                  // добавка распределяется симметрично,
            if (!clipped1 && !clipped2) { p1 -= extra / 2; p2 += extra / 2; }
            else if (!clipped2) p2 += extra;                 // а с подрезанной кромки — в свободную сторону
            else if (!clipped1) p1 -= extra;
            else throw new InvalidOperationException(
                $"Зона {z.Id}: стержень зажат кромками с обеих сторон, длина из ряда не подбирается");

            // — сдвигаем весь стержень (длину не меняя) так, чтобы оба конца были кратны 10 мм от оси —
            double alongShift = SnapAlongShift(p1, p2, z.Dir);
            p1 += alongShift; p2 += alongShift;

            // — отметка стержня по толщине —
            double zBar = BarElevation(slab, z);

            var curves = new List<Curve>
            {
                Line.CreateBound(PointOf(z.Dir, p1, first, zBar), PointOf(z.Dir, p2, first, zBar))
            };

            var normal = z.Dir == Dir.X ? XYZ.BasisY : XYZ.BasisX;  // направление раскладки массива
            var rebar = Autodesk.Revit.DB.Structure.Rebar.CreateFromCurves(
                _doc, RebarStyle.Standard, type, null, null, host,
                normal, curves, RebarHookOrientation.Right, RebarHookOrientation.Right,
                true, true);

            rebar.GetShapeDrivenAccessor()
                 .SetLayoutAsNumberWithSpacing(count, Mm(step), true, true, true);

            WriteParams(rebar, z, clipped1 || clipped2, mark);
            AssignWorkset(rebar);

            return new PlacedBars
            {
                ZoneId = z.Id,
                TypeName = type.Name,
                Count = count,
                LengthMm = std.Value,
                NeedsHook = clipped1 || clipped2
            };
        }

        /// <summary>
        /// Отметка оси стержня. Защитный слой в типе плиты = 0,
        /// поэтому расстояние 35 мм отсчитывается до наружной грани стержня:
        /// ось = грань − (35 + d/2). Внешние грани стержней разных диаметров лежат заподлицо.
        /// Для второго слоя добавляется толщина первого.
        /// </summary>
        private double BarElevation(SlabGeometry slab, ZoneDef z)
        {
            double layer = (z.Dir == _s.FirstLayer) ? 0.0 : _s.FirstLayerThickness;
            double d = z.Diameter;
            double off = _s.FaceOffset + layer + d / 2.0;
            return z.Face == Face.Top ? slab.TopMm - off : slab.BottomMm + off;
        }

        private static XYZ PointOf(Dir dir, double along, double across, double zMm) =>
            dir == Dir.X ? new XYZ(Mm(along), Mm(across), Mm(zMm))
                         : new XYZ(Mm(across), Mm(along), Mm(zMm));

        private void WriteParams(Autodesk.Revit.DB.Structure.Rebar r, ZoneDef z, bool hook, string mark)
        {
            SetIfExists(r, "Зона_ID", z.Id);
            SetIfExists(r, "Зона_As", z.AsCalc.ToString("0.00"));
            SetIfExists(r, "Зона_режим", z.ByMean ? "среднее" : "пик");
            SetIfExists(r, "Зона_источник", z.Source);
            if (hook) SetIfExists(r, "Зона_примечание", "анкеровка подрезана — требуется загиб");
            if (!string.IsNullOrEmpty(mark)) SetIfExists(r, "BI_марка_конструкции", mark);
        }

        private static void SetIfExists(Element e, string name, string value)
        {
            var p = e.LookupParameter(name);
            if (p != null && !p.IsReadOnly && p.StorageType == StorageType.String) p.Set(value);
        }
    }

    // ────────────────────────────────────────────────────────────
    //  Вспомогательные типы
    // ────────────────────────────────────────────────────────────

    public class Band
    {
        public double Along1, Along2;    // вдоль стержней (внутренние единицы)
        public double Across1, Across2;  // поперёк стержней
    }

    public class PlacedBars
    {
        public string ZoneId, TypeName;
        public int Count;
        public double LengthMm;
        public bool NeedsHook;
    }

    public class PlacementResult
    {
        public List<PlacedBars> Bars = new List<PlacedBars>();
        public List<string> Errors = new List<string>();

        /// <summary>Унификация: пары длин одного диаметра и грани, различающиеся менее чем на порог.</summary>
        public List<string> LengthWarnings(double thresholdMm = 100)
        {
            var res = new List<string>();
            foreach (var g in Bars.GroupBy(b => b.TypeName))
            {
                var lens = g.Select(b => b.LengthMm).Distinct().OrderBy(v => v).ToList();
                for (int i = 0; i + 1 < lens.Count; i++)
                    if (lens[i + 1] - lens[i] < thresholdMm)
                        res.Add($"{g.Key}: длины {lens[i]} и {lens[i + 1]} различаются менее чем на {thresholdMm} мм — унифицировать до {lens[i + 1]}");
            }
            return res;
        }
    }

    /// <summary>Геометрия плиты: верх/низ и контур для подрезки анкеровки.</summary>
    public class SlabGeometry
    {
        public double TopMm, BottomMm;
        private List<CurveLoop> _loops;      // внешний контур + отверстия (все вместе, для правила чётности)
        private double _planeZ;              // отметка плоскости контуров, футы (Z верхней грани)
        private XYZ _bboxMin, _bboxMax;      // габарит плиты в плане, футы — для построения сканирующей линии

        public static SlabGeometry From(Document doc, Floor floor)
        {
            var g = new SlabGeometry();
            var bb = floor.get_BoundingBox(null);
            g.BottomMm = UnitUtils.ConvertFromInternalUnits(bb.Min.Z, UnitTypeId.Millimeters);
            g.TopMm = UnitUtils.ConvertFromInternalUnits(bb.Max.Z, UnitTypeId.Millimeters);
            g._bboxMin = bb.Min;
            g._bboxMax = bb.Max;
            g._loops = GetHorizontalLoops(floor, out g._planeZ);
            return g;
        }

        /// <summary>
        /// "Изолинии" — сетка потенциальных положений доп. стержней, привязанная к сетке
        /// основной арматуры. Та обычно идёт шагом 200мм с защитным слоем 50мм, то есть её
        /// стержни стоят на 50, 250, 450… мм от края плиты — а середины пролётов между ними
        /// (удобное место под доп. стержень, не сталкивается с основной сеткой) на 150, 350,
        /// 550… мм. Строится от нижнего/левого края габарита плиты (снизу вверх, слева направо),
        /// шагом stepMm, пока не выйдет за противоположный край. Координаты — в мм.
        /// </summary>
        public (List<double> Xs, List<double> Ys) BuildIsolines(double stepMm = 200.0, double firstOffsetMm = 150.0)
        {
            double stepFt = UnitUtils.ConvertToInternalUnits(stepMm, UnitTypeId.Millimeters);
            double firstFt = UnitUtils.ConvertToInternalUnits(firstOffsetMm, UnitTypeId.Millimeters);
            const double tolFt = 1e-6;

            var xs = new List<double>();
            for (double x = _bboxMin.X + firstFt; x < _bboxMax.X - tolFt; x += stepFt)
                xs.Add(Math.Round(UnitUtils.ConvertFromInternalUnits(x, UnitTypeId.Millimeters), 3));

            var ys = new List<double>();
            for (double y = _bboxMin.Y + firstFt; y < _bboxMax.Y - tolFt; y += stepFt)
                ys.Add(Math.Round(UnitUtils.ConvertFromInternalUnits(y, UnitTypeId.Millimeters), 3));

            return (xs, ys);
        }

        /// <summary>
        /// Ближайшая изолиния к заданной поперечной (across) координате, мм. barDir — направление
        /// СТЕРЖНЕЙ (не поперёк): при стержнях вдоль X поперёк идёт Y — берём Ys, и наоборот.
        ///
        /// Границы зон (band.Across1/2) приходят из JS-редактора уже привязанными к изолиниям,
        /// но это только КЛИЕНТСКАЯ копия того же расчёта — при объединении зон, зонах у краёв
        /// отверстий и т.п. они могут разойтись с исходным списком на несколько/десятки мм.
        /// Пересчитываем здесь, из ТОГО ЖЕ SlabGeometry, которым эти изолинии и были посчитаны —
        /// так соседние зоны с близкими (но не идеально совпадающими) границами гарантированно
        /// притягиваются к ОДНОЙ и той же изолинии, а не к двум разным, и стык между ними не рвёт
        /// сетку 200 мм.
        /// </summary>
        public double NearestIsoline(double valueMm, Dir barDir)
        {
            var (xs, ys) = BuildIsolines();
            var list = barDir == Dir.X ? ys : xs;
            if (list.Count == 0) return valueMm;

            double best = list[0], bestDist = Math.Abs(best - valueMm);
            foreach (var v in list)
            {
                double d = Math.Abs(v - valueMm);
                if (d < bestDist) { bestDist = d; best = v; }
            }
            return best;
        }

        private static List<CurveLoop> GetHorizontalLoops(Floor floor, out double planeZ)
        {
            var loops = new List<CurveLoop>();
            planeZ = 0;
            bool have = false;
            var opt = new Options { ComputeReferences = false, DetailLevel = ViewDetailLevel.Coarse };
            foreach (var go in floor.get_Geometry(opt))
                if (go is Solid s && s.Volume > 0)
                    foreach (Autodesk.Revit.DB.Face f in s.Faces)
                        if (f is PlanarFace pf && pf.FaceNormal.IsAlmostEqualTo(XYZ.BasisZ))
                        {
                            if (!have) { planeZ = pf.Origin.Z; have = true; }
                            loops.AddRange(pf.GetEdgesAsCurveLoops());
                        }
            return loops;
        }

        /// <summary>
        /// Подрезает конец стержня по кромке плиты и отверстиям:
        /// конец не ближе EndOffset к границе. Возвращает флаги подрезки.
        /// </summary>
        public void ClipAlong(Dir dir, ref double p1, ref double p2, double acrossMm,
                              double endOffsetMm, out bool clipped1, out bool clipped2)
        {
            clipped1 = clipped2 = false;
            if (_loops == null || _loops.Count == 0) return;

            double acrossFt = UnitUtils.ConvertToInternalUnits(acrossMm, UnitTypeId.Millimeters);
            const double margin = 1.0;   // фут — заведомо за пределами габарита плиты
            Line scan = dir == Dir.X
                ? Line.CreateBound(new XYZ(_bboxMin.X - margin, acrossFt, _planeZ), new XYZ(_bboxMax.X + margin, acrossFt, _planeZ))
                : Line.CreateBound(new XYZ(acrossFt, _bboxMin.Y - margin, _planeZ), new XYZ(acrossFt, _bboxMax.Y + margin, _planeZ));

            // координаты пересечений сканирующей линии с рёбрами контуров (внешний + отверстия), мм вдоль стержня
            var xs = new List<double>();
            foreach (var loop in _loops)
                foreach (var curve in loop)
                {
                    IntersectionResultArray results;
                    curve.Intersect(scan, out results);
                    if (results == null) continue;
                    foreach (IntersectionResult r in results)
                    {
                        double along = dir == Dir.X ? r.XYZPoint.X : r.XYZPoint.Y;
                        xs.Add(Math.Round(UnitUtils.ConvertFromInternalUnits(along, UnitTypeId.Millimeters), 3));
                    }
                }
            if (xs.Count < 2) return;                 // контур не пересечён — подрезать нечем
            xs.Sort();

            // правило чётности: интервалы (xs[0],xs[1]), (xs[2],xs[3])… — внутри плиты
            double midMm = (p1 + p2) / 2.0;
            double? spanLo = null, spanHi = null;
            for (int k = 0; k + 1 < xs.Count; k += 2)
                if (midMm >= xs[k] - 1e-6 && midMm <= xs[k + 1] + 1e-6) { spanLo = xs[k]; spanHi = xs[k + 1]; break; }
            if (spanLo == null) return;                // середина полосы вне плиты — подрезка не определена

            double lo = spanLo.Value + endOffsetMm, hi = spanHi.Value - endOffsetMm;
            if (lo > hi) { lo = hi = (spanLo.Value + spanHi.Value) / 2.0; }   // зона у́же двойного отступа — вырождаем, не переворачиваем стержень

            double newP1 = Math.Max(p1, lo), newP2 = Math.Min(p2, hi);
            clipped1 = newP1 > p1 + 1e-6;
            clipped2 = newP2 < p2 - 1e-6;
            p1 = newP1;
            p2 = newP2;
        }
    }
}

