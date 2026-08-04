using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;

namespace LiraToRevit.Rebar
{
    /// <summary>Подбор существующего в проекте RebarBarType под зону допармирования (грань +
    /// направление + диаметр) и инвентаризация недостающих типов до начала размещения.</summary>
    public class RebarTypeResolver
    {
        private readonly PlacementSettings _s;
        private readonly List<(string Norm, RebarBarType Type)> _types;

        public RebarTypeResolver(Document doc, PlacementSettings settings)
        {
            _s = settings;
            _types = new FilteredElementCollector(doc)
                .OfClass(typeof(RebarBarType)).Cast<RebarBarType>()
                .Select(t => (Normalize(t.Name), t))
                .ToList();
        }

        /// <summary>Нормализация имени типа: регистр, пробелы, кириллические/латинские х-у-в-н.</summary>
        private static string Normalize(string s)
        {
            if (s == null) return "";
            s = s.ToLowerInvariant().Replace(" ", "");
            return s.Replace('х', 'x').Replace('у', 'y').Replace('в', 'b').Replace('н', 'h');
        }

        public string TypeNameFor(ZoneDef z) => _s.TypeNameTemplate
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

        public RebarBarType FindType(ZoneDef z)
        {
            string core = CoreSignature(z);
            return _types.FirstOrDefault(t => t.Norm.Contains(core)).Type;
        }

        /// <summary>Инвентаризация типов — до начала размещения.</summary>
        public List<string> MissingTypes(IEnumerable<ZoneDef> zones)
        {
            return zones.Where(z => z.Accepted)
                        .GroupBy(z => CoreSignature(z))
                        .Where(g => !_types.Any(t => t.Norm.Contains(g.Key)))
                        .Select(g => TypeNameFor(g.First()))
                        .ToList();
        }
    }
}
