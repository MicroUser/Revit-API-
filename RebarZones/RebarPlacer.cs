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
        // Зона проиграла проверку на пересечение по длине с другой зоной того же направления
        // и грани (см. RebarPlacer.ResolveLengthOverlaps) — созданный по ней Rebar физически
        // сдвигается на 20мм поперёк направления стержней ПОСЛЕ создания (см. Place): любая
        // попытка сдвинуть исходные координаты ДО создания перебивается привязкой к сетке
        // изолиний (NearestIsoline) и другими пересчётами внутри PlaceBand.
        public bool NeedsAcrossShift;
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

        /// <summary>Диаметр фоновой (основной) арматуры, мм — задаёт отступ доп. арматуры от
        /// защитного слоя (см. BarElevation). Приходит из HTML (выбор "Фоновая арматура" в
        /// настройках редактора зон), 10 мм — запасное значение по умолчанию.</summary>
        public double FirstLayerThickness = 10.0;


        /// <summary>Шаблон имени типа: {F}=В/Н, {D}=x/y, {d}=диаметр.</summary>
        public string TypeNameTemplate = "плита_доп_{F}{D}_d={d}_А500";

        /// <summary>Рабочий набор для создаваемых стержней; null/"" — не менять (оставить текущий).
        /// Молча игнорируется, если модель несовместная или набора с таким именем нет.</summary>
        public string WorksetName = ".#06_Арм_Плиты";

        /// <summary>Фундаменты: стержни всегда прямые, независимо от грани/обрезки краем —
        /// Г/П-образный загиб (см. ниже) не применяется вообще.</summary>
        public bool AlwaysStraight = false;

        // ── Г/П-образные стержни у края плиты (только верхняя допка, Face.Top) ──────────
        // Стержень, обрезаемый краем плиты (ClipAlong → clipped1/clipped2), вместо укорачивания
        // с пометкой "требуется загиб" получает реальный загиб: по умолчанию Г-образный, а если
        // у этого края обнаружен пилон/колонна, параллельные грани плиты — П-образный.

        /// <summary>Имя формы для Г-образного стержня (носик BI_B).</summary>
        public string LShapeName = "(форма)11";
        /// <summary>Длина носика Г-образного стержня, мм (BI_B).</summary>
        public double LShapeNoseMm = 120.0;

        /// <summary>Имя формы для П-образного стержня (BI_B — уход вглубь плиты, BI_C — короткая нога).</summary>
        public string UShapeName = "(форма)21";
        /// <summary>Длина части, уходящей вглубь плиты, мм (BI_B).</summary>
        public double UShapeDepthMm = 140.0;
        /// <summary>Длина короткой ноги на конце, мм (BI_C).</summary>
        public double UShapeFootMm = 500.0;

        /// <summary>Параметр сборки со списком категорий её состава (автозаполняемый Revit'ом
        /// при именовании сборки) — ловит любую сборку из стен/несущих колонн.</summary>
        public string NamingCategoryParam = "Категория именования";
        /// <summary>Подстрока значения NamingCategoryParam, определяющая стены.</summary>
        public string NamingCategoryWalls = "Стены";
        /// <summary>Подстрока значения NamingCategoryParam, определяющая несущие колонны.</summary>
        public string NamingCategoryColumns = "Несущие колонны";

        /// <summary>Допуск вдоль края плиты при поиске пилона/колонны у места загиба, мм.</summary>
        public double SupportSearchToleranceMm = 50.0;
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
        private readonly RebarShape _lShape;     // "(форма)11" — Г-образный, null если не найдена
        private readonly RebarShape _uShape;     // "(форма)21" — П-образный, null если не найдена
        private readonly List<SupportInfo> _supports;  // пилоны/колонны по "Категория именования"

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

            var shapes = new FilteredElementCollector(doc).OfClass(typeof(RebarShape)).Cast<RebarShape>().ToList();
            _lShape = shapes.FirstOrDefault(sh => sh.Name == settings.LShapeName);
            _uShape = shapes.FirstOrDefault(sh => sh.Name == settings.UShapeName);

            _supports = ResolveSupports(doc, settings);
        }

        /// <summary>Пилон/колонна — сборка (AssemblyInstance) с "Марка по стандарту", содержащей
        /// "Пилон" или "Колонна". Габарит в плане + направление длинной стороны (по большей
        /// стороне габарита — сами пилоны/колонны в этом проекте ортогональны осям).</summary>
        // internal (не private) — доступ нужен диагностической команде в Debug.cs, см.
        // DiagnosePylonOrientation. Сама структура/логика тут не меняется.
        internal class SupportInfo
        {
            public XYZ BBoxMin, BBoxMax;  // футы, план
            public bool LongAxisIsX;      // true — длинная сторона вдоль X, false — вдоль Y
        }

        /// <summary>
        /// Текст значения параметра "Идентификации" сборки (Марка по стандарту / Категория
        /// именования) — на практике StorageType этих параметров оказался ElementId, а не текст
        /// напрямую (AsString() у них всегда пустой). "Марка по стандарту" ссылается на строку
        /// таблицы-ключа Assembly Naming (обычный Element, берём Name). А "Категория именования"
        /// ссылается на КАТЕГОРИЮ (напр. OST_Walls) — doc.GetElement(id) для id категории всегда
        /// возвращает null, поэтому сначала пробуем Category.GetCategory.
        /// </summary>
        // internal (не private) — доступ нужен диагностической команде в Debug.cs, см.
        // DiagnosePylonOrientation.
        internal static string SupportParamText(Document doc, Parameter p)
        {
            if (p == null) return "";
            if (p.StorageType == StorageType.ElementId)
            {
                ElementId id = p.AsElementId();
                if (id == null || id == ElementId.InvalidElementId) return "";
                Category cat = Category.GetCategory(doc, id);
                if (cat != null) return cat.Name;
                return doc.GetElement(id)?.Name ?? "";
            }
            return p.AsString() ?? p.AsValueString() ?? "";
        }

        internal static List<SupportInfo> ResolveSupports(Document doc, PlacementSettings s)
        {
            var res = new List<SupportInfo>();

            foreach (AssemblyInstance a in new FilteredElementCollector(doc).OfClass(typeof(AssemblyInstance)).Cast<AssemblyInstance>())
            {
                // Категории состава сборки (автозаполняется Revit'ом при именовании) — ловит
                // любую сборку из стен/несущих колонн.
                string namingCats = SupportParamText(doc, a.LookupParameter(s.NamingCategoryParam));
                bool isWallCat = namingCats.IndexOf(s.NamingCategoryWalls, StringComparison.OrdinalIgnoreCase) >= 0;
                bool isColumnCat = namingCats.IndexOf(s.NamingCategoryColumns, StringComparison.OrdinalIgnoreCase) >= 0;

                if (!isWallCat && !isColumnCat) continue;

                // Сборка может объединять НЕСКОЛЬКО стен/колонн разной ориентации (например, все
                // стены шахты лифта вокруг проёма) — общий bbox сборки тогда не отражает
                // ориентацию ни одной из них. Поэтому берём габарит и длинную сторону у КАЖДОГО
                // элемента состава отдельно (стена / несущая колонна), а не у сборки целиком.
                int memberSupports = 0;
                foreach (ElementId memberId in a.GetMemberIds())
                {
                    Element member = doc.GetElement(memberId);
                    if (member?.Category == null) continue;
                    long catId = member.Category.Id.IntValue();
                    bool memberIsWall = catId == (int)BuiltInCategory.OST_Walls;
                    bool memberIsColumn = catId == (int)BuiltInCategory.OST_StructuralColumns;
                    if (!memberIsWall && !memberIsColumn) continue;

                    BoundingBoxXYZ mbb = member.get_BoundingBox(null);
                    if (mbb == null) continue;
                    double mdx = mbb.Max.X - mbb.Min.X, mdy = mbb.Max.Y - mbb.Min.Y;
                    res.Add(new SupportInfo { BBoxMin = mbb.Min, BBoxMax = mbb.Max, LongAxisIsX = mdx >= mdy });
                    memberSupports++;
                }
                if (memberSupports > 0) continue;

                // Запасной вариант (в составе не нашлось отдельных стен/колонн с bbox) — общий
                // bbox сборки; AssemblyInstance.get_BoundingBox(null) на практике нередко
                // возвращает null, тогда объединяем габариты всех элементов состава.
                BoundingBoxXYZ bb = a.get_BoundingBox(null);
                if (bb == null)
                {
                    foreach (ElementId memberId in a.GetMemberIds())
                    {
                        BoundingBoxXYZ mbb = doc.GetElement(memberId)?.get_BoundingBox(null);
                        if (mbb == null) continue;
                        bb = bb == null
                            ? new BoundingBoxXYZ { Min = mbb.Min, Max = mbb.Max }
                            : new BoundingBoxXYZ
                            {
                                Min = new XYZ(Math.Min(bb.Min.X, mbb.Min.X), Math.Min(bb.Min.Y, mbb.Min.Y), Math.Min(bb.Min.Z, mbb.Min.Z)),
                                Max = new XYZ(Math.Max(bb.Max.X, mbb.Max.X), Math.Max(bb.Max.Y, mbb.Max.Y), Math.Max(bb.Max.Z, mbb.Max.Z))
                            };
                    }
                }
                if (bb == null) continue;
                double dx = bb.Max.X - bb.Min.X, dy = bb.Max.Y - bb.Min.Y;
                res.Add(new SupportInfo { BBoxMin = bb.Min, BBoxMax = bb.Max, LongAxisIsX = dx >= dy });
            }

            return res;
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

        /// <summary>Сдвиг поперёк направления стержней при конфликте по длине, мм — см.
        /// ResolveLengthOverlaps/Place (сдвигаем уже СОЗДАННЫЙ Rebar, а не координаты до создания:
        /// любой пересчёт координат "в лоб" перебивается привязкой к сетке изолиний и т.п. внутри
        /// PlaceBand, а MoveElement трогает готовую геометрию элемента напрямую).</summary>
        private const double AcrossShiftMm = 20.0;

        /// <summary>
        /// Две зоны одного направления и грани, чьи стержни (уже С УЧЁТОМ анкеровки — иначе зоны с
        /// небольшим зазором между собой кажутся не пересекающимися, а их реальные стержни всё равно
        /// заходят друг в друга анкеровкой) пересекаются по длине (along-диапазон, без проверки
        /// точного совпадения поперечных позиций — сам факт наложения по длине уже означает риск
        /// наложения стержней), физически рискуют дать стержень на стержень. Зону с МЕНЬШИМ
        /// диаметром помечаем на сдвиг (см. AcrossShiftMm, применяется в Place к уже созданному
        /// Rebar); при равных диаметрах помечаем любую (вторую по порядку). Каждая зона помечается
        /// не больше одного раза — это практическая подстраховка, а не точный алгоритм полностью
        /// бесконфликтной раскладки.
        /// </summary>
        private void ResolveLengthOverlaps(List<ZoneDef> zones)
        {
            var flagged = new HashSet<ZoneDef>();

            for (int i = 0; i < zones.Count; i++)
            {
                for (int j = i + 1; j < zones.Count; j++)
                {
                    var a = zones[i]; var b = zones[j];
                    if (a.Face != b.Face || a.Dir != b.Dir) continue;
                    if (flagged.Contains(a) || flagged.Contains(b)) continue;

                    var (aMin, aMax) = ZoneAlongRangeWithAnchorage(a);
                    var (bMin, bMax) = ZoneAlongRangeWithAnchorage(b);
                    if (aMax < bMin || bMax < aMin) continue;   // along-диапазоны не пересекаются

                    var loser = a.Diameter < b.Diameter ? a : b;
                    loser.NeedsAcrossShift = true;
                    flagged.Add(loser);
                }
            }
        }

        /// <summary>
        /// Огибающая along-координат зоны (по всем её полосам — актуально для Г/П-формы) РАСШИРЕННАЯ
        /// на анкеровку с обеих сторон — так же, как реальный стержень выходит за габарит зоны в
        /// PlaceBand (p1 = Along1-la, p2 = Along2+la). На этом шаге ещё не известно, какой конец
        /// обрежется краем плиты (ClipAlong отрабатывает позже, в PlaceBand) — консервативно
        /// считаем анкеровку с обеих сторон, чтобы не пропустить реальное наложение.
        /// </summary>
        private (double Min, double Max) ZoneAlongRangeWithAnchorage(ZoneDef z)
        {
            var bands = SplitToBands(z);
            double min = bands.Min(bd => Math.Min(bd.Along1, bd.Along2));
            double max = bands.Max(bd => Math.Max(bd.Along1, bd.Along2));
            double la = Mm(_s.Anchorage.TryGetValue(z.Diameter, out var v) ? v : 400);
            return (min - la, max + la);
        }

        // ────────────────────────────────────────────────────────
        //  2. Основной проход
        // ────────────────────────────────────────────────────────
        public PlacementResult Place(Floor host, IEnumerable<ZoneDef> zones)
        {
            var res = new PlacementResult();
            var accepted = zones.Where(z => z.Accepted).ToList();   // — только подтверждённые

            ResolveLengthOverlaps(accepted);

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
                        if (bars == null) continue;
                        res.Bars.Add(bars);

                        // Сдвиг при конфликте по длине (см. ResolveLengthOverlaps) — двигаем уже
                        // СОЗДАННЫЙ элемент напрямую (ElementTransformUtils.MoveElement), а не
                        // координаты до создания: сдвиг координат "в лоб" перебивался привязкой
                        // к сетке изолиний и другими пересчётами внутри PlaceBand.
                        if (z.NeedsAcrossShift && bars.RebarId != null && bars.RebarId != ElementId.InvalidElementId)
                        {
                            XYZ move = z.Dir == Dir.X ? new XYZ(0, Mm(AcrossShiftMm), 0) : new XYZ(Mm(AcrossShiftMm), 0, 0);
                            ElementTransformUtils.MoveElement(_doc, bars.RebarId, move);
                        }
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

            // — отметка стержня по толщине —
            double zBar = BarElevation(slab, z);

            // Ровно один торец обрезан краем плиты/отверстия (оба сразу — уже отсеяно ниже,
            // при обоих clipped распределить добавку по стандартной длине некуда, см. throw
            // в прямой ветке) и это верхняя допка — вместо прямого стержня с обрезкой строим
            // Г- или П-образный (реальный загиб, а не текстовая пометка "требуется загиб").
            // Для фундаментов (AlwaysStraight) загиб не делаем вообще — там стержни всегда прямые.
            bool bendable = !_s.AlwaysStraight && z.Face == Face.Top && (clipped1 ^ clipped2) && _lShape != null;

            Autodesk.Revit.DB.Structure.Rebar rebar;
            double reportLenMm;
            bool needsHookNote;

            if (bendable)
            {
                // Анкеровка — только с дальней стороны (torec у кромки уже отдаёт анкеровку через
                // сам загиб, вторая анкеровка внахлёст не нужна): дальний торец сохраняет полный
                // отступ la (как в прямой ветке), ближний — там, где ClipAlong уже остановил его
                // у истинного края плиты (p1/p2 после ClipAlong выше).
                bool bendAtP2 = clipped2;
                double origFarAlong = bendAtP2 ? p1 : p2;     // анкеровка вглубь зоны (для расчёта общего бюджета)
                double edgeNearAlong = bendAtP2 ? p2 : p1;    // положение у истинного края (после ClipAlong)

                // Общая длина стержня (прямой участок + загиб) берётся из ТОГО ЖЕ ряда заготовок
                // 11700, что и обычный прямой стержень — загиб не добавляет металл поверх, а
                // "вырезается" из уже посчитанной длины: если прямой стержень с анкеровкой был бы
                // 1920мм, а торец гнётся, то прямой участок сокращается на длину загиба, чтобы в
                // сумме (прямой + загиб) снова получилось 1920 (округлённые до ряда).
                double totalBudgetMm = Math.Abs(edgeNearAlong - origFarAlong);
                var snappedTotal = _s.SnapLength(totalBudgetMm);
                if (snappedTotal == null)
                    throw new InvalidOperationException(
                        $"Зона {z.Id}: требуемая длина {totalBudgetMm:0} мм превышает максимальную {_s.StandardLengths.Last():0} мм — нужен стык или деление зоны");

                bool useU = _uShape != null && HasParallelSupport(slab, z.Dir, first, step, count, edgeNearAlong, zBar);
                RebarShape shape = useU ? _uShape : _lShape;
                double noseTotalMm = useU ? (_s.UShapeDepthMm + _s.UShapeFootMm) : _s.LShapeNoseMm;

                double mainLenMm = snappedTotal.Value - noseTotalMm;
                if (mainLenMm <= 0)
                    throw new InvalidOperationException(
                        $"Зона {z.Id}: длина загиба ({noseTotalMm:0} мм) больше подобранной длины из ряда ({snappedTotal.Value:0} мм)");

                // Место загиба ФИКСИРУЕМ ровно там, где обрезался бы прямой стержень (edgeNearAlong,
                // т.е. с отступом EndOffset от истинного края плиты) — так же, как выглядел бы
                // обычный прямой стержень. Раньше загиб откладывался от анкеровки на mainLenMm и
                // при округлении общей длины до бо́льшего ряда (скачок, напр. с 2921 сразу на 3900)
                // выталкивал загиб ЗА пределы плиты. Растягивается вместо этого дальний
                // (анкеруемый) торец — как и у прямого стержня при подгонке под ряд заготовок.
                double sign = bendAtP2 ? 1.0 : -1.0;
                double nearAlong = edgeNearAlong;
                double farAlong = nearAlong - sign * mainLenMm;

                // — сдвигаем весь стержень (длину не меняя) так, чтобы дальний (анкеруемый) торец
                // был кратен 10 мм от оси —
                double alongShift = SnapAlongShift(farAlong, nearAlong, z.Dir);
                farAlong += alongShift; nearAlong += alongShift;

                XYZ farPt = PointOf(z.Dir, farAlong, first, zBar);
                XYZ nearPt = PointOf(z.Dir, nearAlong, first, zBar);
                XYZ xVec = (nearPt - farPt).Normalize();

                // Строим ломаную ЯВНО (а не через origin/xVec/yVec формы) — Rebar.CreateFromRebarShape
                // на практике укладывал Г/П-стержни не так, как нужно (неверная ориентация загиба).
                // CreateFromCurvesAndShape сам считает параметры формы (BI_A/BI_B/…) из геометрии
                // кривых, поэтому не нужно ни гадать систему координат формы, ни вручную выставлять
                // параметры (а при смене формы после создания их местами меняет сам Revit — этого
                // тоже избегаем).
                var curves = new List<Curve> { Line.CreateBound(farPt, nearPt) };
                XYZ depthPt = nearPt - XYZ.BasisZ.Multiply(Mm(useU ? _s.UShapeDepthMm : _s.LShapeNoseMm));
                curves.Add(Line.CreateBound(nearPt, depthPt));
                if (useU)
                {
                    // П-шка: третий сегмент — горизонтально назад вдоль основного стержня (к дальнему торцу)
                    XYZ footPt = depthPt - xVec.Multiply(Mm(_s.UShapeFootMm));
                    curves.Add(Line.CreateBound(depthPt, footPt));
                }

                var arrayNormal = z.Dir == Dir.X ? XYZ.BasisY : XYZ.BasisX;  // направление раскладки массива
                rebar = Autodesk.Revit.DB.Structure.Rebar.CreateFromCurvesAndShape(
                    _doc, shape, type, null, null, host, arrayNormal, curves,
                    RebarHookOrientation.Right, RebarHookOrientation.Right);
                if (rebar == null)
                    throw new InvalidOperationException(
                        $"Зона {z.Id}: не удалось создать {(useU ? "П" : "Г")}-образный стержень по форме «{shape.Name}» — кривые не подошли под форму");

                rebar.GetShapeDrivenAccessor()
                     .SetLayoutAsNumberWithSpacing(count, Mm(step), true, true, true);

                // Параметры формы (BI_A/BI_B/…), которые Revit вывел из кривых, на практике не
                // всегда совпадают с нужными значениями (напр. 125/126 мм вместо ровно 120 —
                // поправка на радиус загиба в самой форме) и роль параметра (какой отвечает за
                // основную длину, какой за носик) не гарантированно совпадает с именем BI_A/BI_B
                // по порядку. Сопоставляем по БЛИЗОСТИ величин (сортировка), затем выставляем
                // точные целевые значения принудительно.
                if (useU)
                    FixShapeParams(rebar, new[] { "BI_A", "BI_B", "BI_C" },
                        new[] { mainLenMm, _s.UShapeDepthMm, _s.UShapeFootMm });
                else
                    FixShapeParams(rebar, new[] { "BI_A", "BI_B" },
                        new[] { mainLenMm, _s.LShapeNoseMm });

                reportLenMm = snappedTotal.Value;   // суммарная длина металла — для унификации/отчёта
                needsHookNote = false;   // загиб уже выполнен геометрией, а не остался как TODO

                SetIfExists(rebar, "Зона_ID", z.Id);
                SetIfExists(rebar, "Зона_As", z.AsCalc.ToString("0.00"));
                SetIfExists(rebar, "Зона_режим", z.ByMean ? "среднее" : "пик");
                SetIfExists(rebar, "Зона_источник", z.Source);
                SetIfExists(rebar, "Зона_примечание", useU
                    ? "П-образный (форма 21) — пилон/колонна вдоль края плиты"
                    : "Г-образный (форма 11) — край плиты");
                if (!string.IsNullOrEmpty(mark)) SetIfExists(rebar, "BI_марка_конструкции", mark);
            }
            else
            {
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

                var curves = new List<Curve>
                {
                    Line.CreateBound(PointOf(z.Dir, p1, first, zBar), PointOf(z.Dir, p2, first, zBar))
                };

                var normal = z.Dir == Dir.X ? XYZ.BasisY : XYZ.BasisX;  // направление раскладки массива
                rebar = Autodesk.Revit.DB.Structure.Rebar.CreateFromCurves(
                    _doc, RebarStyle.Standard, type, null, null, host,
                    normal, curves, RebarHookOrientation.Right, RebarHookOrientation.Right,
                    true, true);

                rebar.GetShapeDrivenAccessor()
                     .SetLayoutAsNumberWithSpacing(count, Mm(step), true, true, true);

                WriteParams(rebar, z, clipped1 || clipped2, mark);

                reportLenMm = std.Value;
                needsHookNote = clipped1 || clipped2;
            }

            AssignWorkset(rebar);

            return new PlacedBars
            {
                ZoneId = z.Id,
                TypeName = type.Name,
                Count = count,
                LengthMm = reportLenMm,
                NeedsHook = needsHookNote,
                RebarId = rebar.Id
            };
        }

        /// <summary>
        /// Принудительно выставляет параметры формы (BI_A/BI_B/…) в ожидаемые значения.
        /// Сопоставление "какой параметр — какая роль" делаем по близости величин (сортировка
        /// текущих авто-вычисленных значений и целевых по возрастанию, затем попарно), а не по
        /// имени параметра — на практике имя не гарантирует роль (см. диагностику: BI_A в форме
        /// 11 может быть носиком, а не основной длиной, и Revit сам их меняет местами при смене
        /// формы существующего стержня).
        /// </summary>
        private static void FixShapeParams(Autodesk.Revit.DB.Structure.Rebar rebar, string[] paramNames, double[] targetMm)
        {
            var pars = paramNames.Select(n => rebar.LookupParameter(n)).ToArray();
            var order = Enumerable.Range(0, pars.Length)
                .Where(i => pars[i] != null)
                .OrderBy(i => pars[i].AsDouble())
                .ToList();
            var targetOrder = targetMm.OrderBy(v => v).ToList();
            for (int k = 0; k < order.Count && k < targetOrder.Count; k++)
                pars[order[k]].Set(Mm(targetOrder[k]));
        }

        /// <summary>
        /// Есть ли рядом с местом загиба (у кромки плиты) пилон/колонна, ориентированные
        /// ПАРАЛЛЕЛЬНО грани плиты, и попадает ли хотя бы один стержень массива в его габарит
        /// поперёк раскладки. При совпадении — весь массив (band) переключается на П-образную
        /// форму вместо Г-образной.
        /// </summary>
        /// <summary>Допуск по высоте при поиске опоры для конкретной плиты, мм. Одна сборка может
        /// содержать стены/колонны сразу нескольких этажей (стоящие друг над другом элементы
        /// одной сборки) — без фильтра по Z в качестве "опоры" находилась бы стена ЛЮБОГО этажа
        /// с подходящим планом, а не именно та, что стоит у края ЭТОЙ плиты.</summary>
        // internal (не private) — используется и диагностической командой в Debug.cs.
        internal const double SupportZToleranceMm = 300.0;

        private bool HasParallelSupport(SlabGeometry slab, Dir dir, double acrossStartMm, double stepMm, int count, double bendAlongMm, double zBarMm)
        {
            double tol = _s.SupportSearchToleranceMm;
            double zTol = Mm(SupportZToleranceMm);
            double zBarFt = Mm(zBarMm); // zBarMm — в мм (см. BarElevation), BBoxMin/Max.Z сборок — в футах

            foreach (var sup in _supports)
            {
                if (zBarFt < sup.BBoxMin.Z - zTol || zBarFt > sup.BBoxMax.Z + zTol) continue;

                double supAlongMin = ToMm(dir == Dir.X ? sup.BBoxMin.X : sup.BBoxMin.Y);
                double supAlongMax = ToMm(dir == Dir.X ? sup.BBoxMax.X : sup.BBoxMax.Y);
                if (bendAlongMm < supAlongMin - tol || bendAlongMm > supAlongMax + tol) continue;

                double supAcrossMin = ToMm(dir == Dir.X ? sup.BBoxMin.Y : sup.BBoxMin.X);
                double supAcrossMax = ToMm(dir == Dir.X ? sup.BBoxMax.Y : sup.BBoxMax.X);

                bool anyBarOver = false;
                for (int i = 0; i < count; i++)
                {
                    double acrossMm = acrossStartMm + i * stepMm;
                    if (acrossMm >= supAcrossMin - 1e-6 && acrossMm <= supAcrossMax + 1e-6) { anyBarOver = true; break; }
                }
                if (!anyBarOver) continue;

                XYZ bendPt = dir == Dir.X
                    ? new XYZ(Mm(bendAlongMm), Mm((supAcrossMin + supAcrossMax) / 2.0), 0)
                    : new XYZ(Mm((supAcrossMin + supAcrossMax) / 2.0), Mm(bendAlongMm), 0);
                XYZ edgeTangent = slab.NearestBoundaryTangent(bendPt);
                if (edgeTangent == null) continue;

                bool edgeAlongX = Math.Abs(edgeTangent.X) >= Math.Abs(edgeTangent.Y);
                if (edgeAlongX == sup.LongAxisIsX) return true;   // ориентации совпали — параллельны
            }
            return false;
        }

        /// <summary>
        /// Отметка оси стержня. Защитный слой в типе плиты = 0, поэтому FaceOffset (35 мм)
        /// отсчитывается до наружной грани. Доп. арматура считается лежащей рядом с фоновой
        /// (основной) сеткой, а не на голой поверхности плиты — у фона тоже двухслойная сетка
        /// X+Y толщиной FirstLayerThickness (диаметр фоновой арматуры, приходит из HTML):
        /// направление FirstLayer (первый слой фона) — offset = FaceOffset + FirstLayerThickness/2 + d/2;
        /// второе направление (лежит поверх ПЕРВОГО слоя фона целиком) — offset = FaceOffset + FirstLayerThickness + d/2.
        /// </summary>
        private double BarElevation(SlabGeometry slab, ZoneDef z)
        {
            double layer = (z.Dir == _s.FirstLayer) ? _s.FirstLayerThickness / 2.0 : _s.FirstLayerThickness;
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
        public ElementId RebarId;   // созданный Rebar — см. RebarPlacer.Place (сдвиг при конфликте по длине)
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
            g._loops = FilterLoopsForClipping(GetHorizontalLoops(floor, out g._planeZ));
            return g;
        }

        /// <summary>
        /// Обрезка анкеровки (ClipAlong) нужна только у истинного края плиты и у крупных проёмов
        /// (≥1000×1000мм) — мелкие отверстия (под инженерку и т.п.) стержень просто перекрывает,
        /// без загиба/подрезки. Внешний контур определяем как петлю с наибольшим габаритом bbox
        /// (тот же приём, что и в RebarZonesCommand.GetFloorOutline) — её оставляем всегда,
        /// остальные петли (проёмы) мельче порога хотя бы по одной стороне — убираем.
        /// </summary>
        private const double MinOpeningForClipMm = 1000.0;
        private static List<CurveLoop> FilterLoopsForClipping(List<CurveLoop> loops)
        {
            if (loops == null || loops.Count < 2) return loops;   // 0-1 петель — нечего фильтровать

            int outerIdx = 0;
            double bestArea = -1;
            var bboxes = new (double minX, double maxX, double minY, double maxY)[loops.Count];
            for (int i = 0; i < loops.Count; i++)
            {
                double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
                foreach (Curve c in loops[i])
                {
                    XYZ p0 = c.GetEndPoint(0), p1 = c.GetEndPoint(1);
                    minX = Math.Min(minX, Math.Min(p0.X, p1.X)); maxX = Math.Max(maxX, Math.Max(p0.X, p1.X));
                    minY = Math.Min(minY, Math.Min(p0.Y, p1.Y)); maxY = Math.Max(maxY, Math.Max(p0.Y, p1.Y));
                }
                bboxes[i] = (minX, maxX, minY, maxY);
                double area = (maxX - minX) * (maxY - minY);
                if (area > bestArea) { bestArea = area; outerIdx = i; }
            }

            double minOpeningFt = UnitUtils.ConvertToInternalUnits(MinOpeningForClipMm, UnitTypeId.Millimeters);
            var res = new List<CurveLoop>();
            for (int i = 0; i < loops.Count; i++)
            {
                if (i == outerIdx) { res.Add(loops[i]); continue; }
                var b = bboxes[i];
                if (b.maxX - b.minX >= minOpeningFt && b.maxY - b.minY >= minOpeningFt) res.Add(loops[i]);
            }
            return res;
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

        /// <summary>
        /// Направление ближайшего к точке отрезка контура плиты (внешний контур + отверстия),
        /// нормализованное. Используется для сравнения ориентации пилона/колонны с гранью плиты
        /// (см. RebarPlacer.IsSupportParallelToEdge). Null, если контур не построен или ближайший
        /// сегмент — дуга (плиты в этом проекте ортогональны, дуговые кромки не ожидаются).
        /// </summary>
        public XYZ NearestBoundaryTangent(XYZ pointFt)
        {
            if (_loops == null || _loops.Count == 0) return null;

            Curve best = null;
            double bestDist = double.MaxValue;
            foreach (var loop in _loops)
                foreach (var curve in loop)
                {
                    IntersectionResult r = curve.Project(pointFt);
                    if (r == null) continue;
                    if (r.Distance < bestDist) { bestDist = r.Distance; best = curve; }
                }

            if (!(best is Line line)) return null;
            XYZ dir = line.GetEndPoint(1) - line.GetEndPoint(0);
            return dir.GetLength() < 1e-9 ? null : dir.Normalize();
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

