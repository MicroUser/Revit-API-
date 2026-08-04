using System.Collections.Generic;
using System.Linq;

namespace LiraToRevit.Rebar
{
    /// <summary>
    /// Разрешение конфликтов между РАЗНЫМИ зонами одного направления и грани, чьи стержни рискуют
    /// физически совпасть по длине — чистая математика над List&lt;ZoneDef&gt;, без обращения к Revit API.
    /// </summary>
    public class ZoneConflictResolver
    {
        /// <summary>Сдвиг поперёк направления стержней при конфликте по длине, мм — см.
        /// ResolveLengthOverlaps/RebarPlacer.Place (сдвигаем уже СОЗДАННЫЙ Rebar, а не координаты до
        /// создания: любой пересчёт координат "в лоб" перебивается привязкой к сетке изолиний и т.п.
        /// внутри BarLengthCalculator, а MoveElement трогает готовую геометрию элемента напрямую).
        /// Тем же приёмом и той же величиной пользуется деление длинной зоны на два стыкуемых внахлёст
        /// массива (см. BarLengthCalculator/RebarBuilder) — там это конфликт МЕЖДУ ПОЛОВИНАМИ одной
        /// зоны, а не между разными зонами, поэтому применяется отдельно, не через этот класс.</summary>
        public const double AcrossShiftMm = 20.0;

        private readonly PlacementSettings _s;

        public ZoneConflictResolver(PlacementSettings settings)
        {
            _s = settings;
        }

        /// <summary>
        /// Две зоны одного направления и грани, чьи стержни (уже С УЧЁТОМ анкеровки — иначе зоны с
        /// небольшим зазором между собой кажутся не пересекающимися, а их реальные стержни всё равно
        /// заходят друг в друга анкеровкой) пересекаются по длине (along-диапазон, без проверки
        /// точного совпадения поперечных позиций — сам факт наложения по длине уже означает риск
        /// наложения стержней), физически рискуют дать стержень на стержень. Зону с МЕНЬШИМ
        /// диаметром помечаем на сдвиг (см. AcrossShiftMm, применяется в RebarPlacer.Place к уже
        /// созданному Rebar); при равных диаметрах помечаем любую (вторую по порядку). Каждая зона
        /// помечается не больше одного раза — это практическая подстраховка, а не точный алгоритм
        /// полностью бесконфликтной раскладки.
        /// </summary>
        public void ResolveLengthOverlaps(List<ZoneDef> zones)
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
        /// BarLengthCalculator (p1 = Along1-la, p2 = Along2+la). На этом шаге ещё не известно, какой
        /// конец обрежется краем плиты (ClipAlong отрабатывает позже) — консервативно считаем
        /// анкеровку с обеих сторон, чтобы не пропустить реальное наложение.
        /// </summary>
        private (double Min, double Max) ZoneAlongRangeWithAnchorage(ZoneDef z)
        {
            var bands = BandSplitter.SplitToBands(z);
            double min = bands.Min(bd => System.Math.Min(bd.Along1, bd.Along2));
            double max = bands.Max(bd => System.Math.Max(bd.Along1, bd.Along2));
            double la = RebarUnits.Mm(_s.Anchorage.TryGetValue(z.Diameter, out var v) ? v : 400);
            return (min - la, max + la);
        }
    }
}
