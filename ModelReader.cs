using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace RevitKJChecklist
{
    public static class ModelReader
    {
        private static readonly Dictionary<string, string> PrefixToSection = BuildPrefixMap();

        private static Dictionary<string, string> BuildPrefixMap()
        {
            var src = new Dictionary<string, string>
            {
                { "Фм",   "found" }, { "РТФм", "found" }, { "ФНм",  "found" }, { "РТм",  "found" },
                { "БФм",  "found" }, { "РТЛм", "found" }, { "ФЛм",  "found" },
                { "СНм", "walls" }, { "СЖм",  "walls" }, { "СЦм",  "walls" }, { "СШм",  "walls" }, { "ПРПм", "walls" },
                { "Пм",   "slabs" }, { "ПРНМ", "slabs" }, { "ПМТм", "slabs" },
                { "КПТм", "slabs" }, { "РМм",  "slabs" },
                { "Км",   "cols"  },
                { "Бм",   "beams" },
                { "Л",    "stairs"}, { "ЛМм",  "stairs"}, { "ЛПм", "stairs" },
            };
            return src.ToDictionary(kv => kv.Key.ToUpperInvariant(), kv => kv.Value);
        }

        public static Dictionary<string, string> ReadProjectMeta(Document doc)
        {
            var pi = doc.ProjectInformation;

            string P(BuiltInParameter bip)
            {
                var p = pi.get_Parameter(bip);
                return p != null ? (p.AsString() ?? "") : "";
            }

            return new Dictionary<string, string>
            {
                ["pname"]   = P(BuiltInParameter.PROJECT_NAME),
                ["pcode"]   = P(BuiltInParameter.PROJECT_NUMBER),
                ["pblock"]  = P(BuiltInParameter.PROJECT_BUILDING_NAME),
                ["pstage"]  = P(BuiltInParameter.PROJECT_STATUS),
                ["pdate"]   = DateTime.Today.ToString("yyyy-MM-dd"),
                ["psw"]     = doc.Application.VersionName,
                ["psheets"] = CountKjSheets(doc).ToString(),
                ["porg"]    = "",
                ["plead"]   = "",
            };
        }

        private static int CountKjSheets(Document doc)
        {
            int n = 0;
            var sheets = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSheet)).Cast<ViewSheet>();
            foreach (var sh in sheets)
            {
                var p = sh.LookupParameter("BI_штамп_раздел_проекта");
                if (p != null && p.StorageType == StorageType.String)
                {
                    var v = p.AsString();
                    if (!string.IsNullOrEmpty(v) &&
                        v.Trim().Equals("КЖ", StringComparison.OrdinalIgnoreCase))
                        n++;
                }
            }
            return n;
        }

        public static Dictionary<string, List<string>> ReadAssemblyMarks(Document doc)
        {
            var marks = new FilteredElementCollector(doc)
                .OfClass(typeof(AssemblyInstance))
                .Cast<AssemblyInstance>()
                .Select(a => a.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim())
                .Distinct()
                .ToList();

            var bySection = new Dictionary<string, List<string>>();
            foreach (var m in marks)
            {
                if (!PrefixToSection.TryGetValue(Prefix(m), out var sec)) continue;
                if (!bySection.TryGetValue(sec, out var list))
                    bySection[sec] = list = new List<string>();
                list.Add(m);
            }
            foreach (var k in bySection.Keys.ToList())
                bySection[k] = bySection[k].OrderBy(Prefix).ThenBy(Num).ToList();

            return bySection;
        }

        private static string Prefix(string m)
        {
            int i = m.IndexOf('-');
            return (i > 0 ? m.Substring(0, i) : m).Trim().ToUpperInvariant();
        }

        private static int Num(string m)
        {
            int i = m.IndexOf('-');
            return (i > 0 && int.TryParse(m.Substring(i + 1), out var n)) ? n : 0;
        }
    }
}
