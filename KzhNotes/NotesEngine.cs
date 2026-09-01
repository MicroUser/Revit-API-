// NotesEngine.cs
// Ядро плагина примечаний: разрешение ссылок и сборка текста примечания.
// ЧИСТЫЙ C# без Revit API — работает со "снимком" листов (SheetInfo),
// поэтому тестируется отдельно и вызывается из UI мгновенно.
//
// Токены в теле пункта:
//   {{МАРКА}}                                         -> полная марка листа (СТ-4)
//   {{ПОЛЕ n="класс"}}                                -> значение поля, введённое пользователем
//   {{КЖ role="Спецификация" excl="Выпуски"}}         -> ссылка по марке текущего листа
//   {{КЖ role="Общие указания..." scope="global"}}    -> глобальная ссылка (может дать диапазон)
//   {{КЖ role="..." scope="mark:СТ-1"}}              -> ссылка на конкретную чужую марку
//   {{КЖ role="..." scope="marks:{поле}"}}            -> ссылка на список чужих марок (значение
//                                                         поля Multi=true, марки через запятую)
//   {{СОВМЕСТНО}}                                      -> "листом/листами КЖ-.." (та же марка, без себя)
//   {{СЕЧЕНИЯ}}                                        -> авто-сборка сечений (только стены)

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace KzhNotes
{
    /// <summary>Снимок одного листа (заполняется из Revit при открытии/обновлении окна).</summary>
    public sealed class SheetInfo
    {
        public string Number { get; set; }   // "017"
        public string Name { get; set; }     // "Стена СТ-4. Опалубка"
        public string Mark { get; private set; }   // "СТ-4"  (первая марка после слова-элемента)
        public string Family { get; private set; } // "СТ"

        public SheetInfo(string number, string name)
        {
            Number = number ?? "";
            Name = name ?? "";
            Mark = MarkParser.FullMark(Name);
            Family = Mark != null ? MarkParser.Family(Mark) : null;
        }

        public int NumberInt
        {
            get { int v; return int.TryParse(new string(Number.TakeWhile(char.IsDigit).ToArray()), out v) ? v : 0; }
        }
    }

    /// <summary>Извлечение марки по белому списку префиксов (приоритет длинных, первая после элемента).</summary>
    public static class MarkParser
    {
        // Полный белый список префиксов проекта.
        public static readonly string[] Prefixes = new[]
        {
            "РТЛм","ППГм","ПРПм","ПРМм","КПТм","БФм","РТм","ЛМм","ЛПм",
            "СНм","СЖм","СЦм","СШм","ФЛм","КРм","РМм","Км","Пм","Фм","Бм","Бл","Л"
        };

        // Семейства-стены (для правила сечений — только они).
        public static readonly HashSet<string> WallFamilies =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "СНм", "СЖм", "СЦм", "СШм", "ПРПм" };

        private static readonly Regex MarkRe = BuildMarkRegex();

        private static Regex BuildMarkRegex()
        {
            var ordered = Prefixes.OrderByDescending(p => p.Length).Select(Regex.Escape);
            // (?<![А-Яа-яёЁ]) — префикс не внутри другого слова; первая марка = первое совпадение
            string pat = @"(?<![А-Яа-яёЁ])(" + string.Join("|", ordered) + @")\s?-?\s?(\d+)";
            return new Regex(pat, RegexOptions.Compiled);
        }

        /// <summary>Полная марка ("СТ-4") или null.</summary>
        public static string FullMark(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            var m = MarkRe.Match(name);
            return m.Success ? m.Groups[1].Value + "-" + m.Groups[2].Value : null;
        }

        /// <summary>Семейство ("СТ") из полной марки.</summary>
        public static string Family(string fullMark)
        {
            if (string.IsNullOrEmpty(fullMark)) return null;
            int dash = fullMark.IndexOf('-');
            string pfx = dash > 0 ? fullMark.Substring(0, dash) : fullMark;
            return pfx.ToUpperInvariant();
        }
    }

    /// <summary>Результат сборки: текст + предупреждения о неразрешённых ссылках.</summary>
    public sealed class RenderResult
    {
        public string Text { get; set; }
        public List<string> Warnings { get; } = new List<string>();
        public bool HasUnresolved { get { return Warnings.Count > 0; } }
    }

    /// <summary>Один пункт-экземпляр в составе листа: ID из библиотеки + значения полей.</summary>
    public sealed class NoteItem
    {
        public string LibraryId { get; set; }                 // трассировка, напр. "СТ-08"
        public string Template { get; set; }                  // тело с токенами
        public Dictionary<string, string> Fields { get; set; } // значения полей пользователя

        public NoteItem(string template, Dictionary<string, string> fields = null, string libId = null)
        {
            Template = template ?? "";
            Fields = fields ?? new Dictionary<string, string>();
            LibraryId = libId;
        }
    }

    /// <summary>Движок: разрешение ссылок и сборка итогового текста примечания.</summary>
    public sealed class NotesEngine
    {
        private readonly List<SheetInfo> _sheets;
        private static readonly Regex TokenRe =
            new Regex(@"\{\{(.*?)\}\}", RegexOptions.Compiled);
        private static readonly Regex ArgRe =
            new Regex("(\\w+)=\"([^\"]*)\"", RegexOptions.Compiled);
        private static readonly Regex SecWord =
            new Regex(@"(?<![а-яё])(сечени|разрез)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex SecPair =
            new Regex(@"(\d+)\s*-\s*(\d+)", RegexOptions.Compiled);
        private static readonly Regex LetterPair =
            new Regex(@"[А-Я]\s*-\s*[А-Я]", RegexOptions.Compiled);

        public NotesEngine(IEnumerable<SheetInfo> snapshot)
        {
            _sheets = snapshot.ToList();
        }

        // ---------- нормализация для сравнения имён ----------
        private static string Norm(string s)
        {
            if (s == null) return "";
            var sb = new StringBuilder();
            bool prevSpace = false;
            foreach (char ch in s.ToLowerInvariant())
            {
                bool keep = (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'z') ||
                            (ch >= 'а' && ch <= 'я') || ch == 'ё';
                if (keep) { sb.Append(ch); prevSpace = false; }
                else if (!prevSpace) { sb.Append(' '); prevSpace = true; }
            }
            return sb.ToString().Trim();
        }

        // "007и" -> numPart=7, suffix="и"; "017" -> numPart=17, suffix=""
        private static void ParseNumber(string n, out int numPart, out string suffix)
        {
            int i = 0;
            while (i < n.Length && char.IsDigit(n[i])) i++;
            suffix = n.Substring(i);
            int.TryParse(n.Substring(0, i), out numPart);
        }

        private static string Kz(string number)  // "017" -> "КЖ-17", "007и" -> "КЖ-7и"
        {
            int v; string suf;
            ParseNumber(number, out v, out suf);
            return "КЖ-" + v.ToString(CultureInfo.InvariantCulture) + suf;
        }

        private IEnumerable<SheetInfo> OfMark(string mark)
        {
            return _sheets.Where(s => s.Mark == mark);
        }

        /// <summary>Форматирование набора номеров: непрерывный ряд ≥3 → "КЖ-a ... b", иначе список через "; ".
        /// Номера с буквенным суффиксом (напр. "007и") не входят в диапазоны и выводятся отдельно.</summary>
        private static string FormatRange(IEnumerable<string> numbers)
        {
            var plainNums = new List<int>();
            var suffixedKz = new List<string>();
            foreach (var n in numbers.Distinct())
            {
                int v; string suf;
                ParseNumber(n, out v, out suf);
                if (suf.Length == 0) plainNums.Add(v);
                else suffixedKz.Add("КЖ-" + v.ToString(CultureInfo.InvariantCulture) + suf);
            }

            plainNums = plainNums.Distinct().OrderBy(v => v).ToList();
            suffixedKz = suffixedKz.OrderBy(x => x).ToList();

            var pieces = new List<string>();
            int i = 0;
            while (i < plainNums.Count)
            {
                int j = i;
                while (j + 1 < plainNums.Count && plainNums[j + 1] == plainNums[j] + 1) j++;
                int runLen = j - i + 1;
                if (runLen >= 3)
                    pieces.Add("КЖ-" + plainNums[i] + " ... " + plainNums[j]);
                else
                    for (int k = i; k <= j; k++) pieces.Add("КЖ-" + plainNums[k]);
                i = j + 1;
            }
            pieces.AddRange(suffixedKz);
            if (pieces.Count == 0) return "";
            return string.Join("; ", pieces);
        }

        // ---------- резолверы ссылок ----------

        /// <summary>{{КЖ ...}} — по марке / глобально / на чужую марку. Возвращает null, если не разрешилось.</summary>
        public string ResolveKz(SheetInfo cur, string role, string scope, string excl)
        {
            string roleN = Norm(role), exclN = Norm(excl);
            if (string.IsNullOrEmpty(roleN)) return null;

            IEnumerable<SheetInfo> pool;
            if (scope == "global") pool = _sheets;
            else if (scope != null && scope.StartsWith("marks:"))
            {
                var markList = scope.Substring(6)
                    .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
                pool = _sheets.Where(s => s.Mark != null &&
                    markList.Any(mk => string.Equals(mk, s.Mark, StringComparison.OrdinalIgnoreCase)));
            }
            else if (scope != null && scope.StartsWith("mark:")) pool = OfMark(scope.Substring(5));
            else pool = cur.Mark != null ? OfMark(cur.Mark) : Enumerable.Empty<SheetInfo>();

            var cand = pool.Where(s => Norm(s.Name).Contains(roleN)).ToList();
            if (exclN.Length > 0) cand = cand.Where(s => !Norm(s.Name).Contains(exclN)).ToList();
            cand = cand.OrderBy(s => s.NumberInt).ToList();

            if (cand.Count == 0) return null;
            if (cand.Count == 1) return Kz(cand[0].Number);
            return FormatRange(cand.Select(s => s.Number)); // глобальный набор → диапазон/список
        }

        /// <summary>Имённые группы для {{СОВМЕСТНО}} на листах БЕЗ марки
        /// (каркасные схемы и т.п.): если имя листа содержит фразу — группируем по ней.</summary>
        public static readonly string[] SovmestnoGroupPhrases = new[]
        {
            "Схема расположения элементов каркаса"

        };

        /// <summary>{{СОВМЕСТНО}} — листы той же марки (или той же имённой группы для листов без марки),
        /// кроме текущего; "листом/листами КЖ-..".</summary>
        public string ResolveSovmestno(SheetInfo cur)
        {
            List<SheetInfo> others;
            if (cur.Mark != null)
            {
                others = OfMark(cur.Mark).Where(s => s.Number != cur.Number)
                                         .OrderBy(s => s.NumberInt).ToList();
            }
            else
            {
                // лист без марки — пробуем имённую группу (напр. «Схема расположения элементов каркаса»)
                string curN = Norm(cur.Name);
                string phrase = SovmestnoGroupPhrases.FirstOrDefault(p => curN.Contains(Norm(p)));
                if (phrase == null) return null;
                string phN = Norm(phrase);
                others = _sheets.Where(s => s.Number != cur.Number && Norm(s.Name).Contains(phN))
                                .OrderBy(s => s.NumberInt).ToList();
            }
            if (others.Count == 0) return null;
            string prep = others.Count == 1 ? "листом" : "листами";
            return prep + " " + FormatRange(others.Select(s => s.Number));
        }

        /// <summary>{{СЕЧЕНИЯ}} — авто-сборка: диапазон сечений + список листов для текущей марки.</summary>
        public string ResolveSechenia(SheetInfo cur)
        {
            if (cur.Mark == null) return null;

            var secSheets = OfMark(cur.Mark).Where(s => SecWord.IsMatch(s.Name)).ToList();
            var use = new List<string>();
            var pairs = new List<Tuple<int, int>>();
            bool hasRazrez = false;
            foreach (var s in secSheets)
            {
                var wordMatch = SecWord.Match(s.Name);
                if (wordMatch.Success &&
                    wordMatch.Groups[1].Value.StartsWith("р", StringComparison.OrdinalIgnoreCase))
                    hasRazrez = true;

                var nums = SecPair.Matches(s.Name).Cast<Match>()
                    .Select(m => Tuple.Create(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)))
                    .Where(t => t.Item1 <= 30 && t.Item2 <= 30).ToList();
                if (nums.Count > 0) { use.Add(s.Number); pairs.AddRange(nums); }
                else if (LetterPair.IsMatch(s.Name)) continue;
                else use.Add(s.Number);
            }
            if (use.Count == 0) return null;
            string noun = hasRazrez ? "разрезы" : "сечения";
            string prep = use.Distinct().Count() == 1 ? "листе" : "листах";
            string body = FormatRange(use);
            if (pairs.Count > 0)
            {
                var lo = pairs.OrderBy(t => t.Item1).ThenBy(t => t.Item2).First();
                var hi = pairs.OrderBy(t => t.Item1).ThenBy(t => t.Item2).Last();
                string st = lo.Equals(hi)
                    ? string.Format("{0}-{1}", lo.Item1, lo.Item2)
                    : string.Format("{0}-{1} ... {2}-{3}", lo.Item1, lo.Item2, hi.Item1, hi.Item2);
                return noun + " " + st + " разработаны на " + prep + " " + body;
            }
            return noun + " разработаны на " + prep + " " + body;
        }

        // ---------- рендер одного пункта ----------
        private string RenderItem(NoteItem item, SheetInfo cur, List<string> warnings)
        {
            return TokenRe.Replace(item.Template, m =>
            {
                string bodyText = m.Groups[1].Value.Trim();
                int sp = bodyText.IndexOf(' ');
                string kind = sp < 0 ? bodyText : bodyText.Substring(0, sp);
                var args = ArgRe.Matches(bodyText).Cast<Match>()
                                .ToDictionary(x => x.Groups[1].Value, x => x.Groups[2].Value);

                string val;
                switch (kind)
                {
                    case "МАРКА":
                        return cur.Mark ?? "??";
                    case "ПОЛЕ":
                        string fn = args.ContainsKey("n") ? args["n"] : "";
                        return item.Fields.TryGetValue(fn, out val) ? val : "??";
                    case "СЕЧЕНИЯ":
                        val = ResolveSechenia(cur);
                        if (val == null) warnings.Add("Сечения не разрешены для " + (cur.Mark ?? "?"));
                        return val ?? "СЕЧЕНИЯ-??";
                    case "СОВМЕСТНО":
                        val = ResolveSovmestno(cur);
                        if (val == null) warnings.Add("Нет других листов марки " + (cur.Mark ?? "?"));
                        return val ?? "листами КЖ-??";
                    case "КЖ":
                    {
                        // {fieldName} внутри аргументов КЖ заменяется значением поля пользователя
                        var flds = item.Fields;
                        System.Func<string, string> sf = a =>
                            Regex.Replace(a, @"\{(\w+)\}", mm =>
                            {
                                string fv; return flds.TryGetValue(mm.Groups[1].Value, out fv) ? fv : mm.Value;
                            });
                        string kzRole  = sf(args.ContainsKey("role")  ? args["role"]  : "");
                        string kzScope = sf(args.ContainsKey("scope") ? args["scope"] : "mark");
                        if (kzScope.Length == 0) kzScope = "mark";
                        string kzExcl  = sf(args.ContainsKey("excl")  ? args["excl"]  : "");
                        val = ResolveKz(cur, kzRole, kzScope, kzExcl);
                        if (val == null)
                        {
                            warnings.Add("Ссылка не разрешена: role=\"" + kzRole + "\"");
                            return "КЖ-??";
                        }
                        // prep="лист" -> добавить "листе"/"листах" по числу листов (одно vs диапазон/список)
                        if (args.ContainsKey("prep") && args["prep"].Length > 0)
                        {
                            bool many = val.Contains("...") || val.Contains(";");
                            string stem = args["prep"];               // "лист"
                            val = (many ? stem + "ах" : stem + "е") + " " + val;
                        }
                        return val;
                    }
                    default:
                        return "??";
                }
            });
        }

        /// <summary>Собрать весь текст примечания (нумерованный список) для листа.</summary>
        public RenderResult Build(SheetInfo cur, IList<NoteItem> items)
        {
            var res = new RenderResult();
            var sb = new StringBuilder();
            for (int i = 0; i < items.Count; i++)
            {
                if (i > 0) sb.Append("\r\n");
                string rendered = RenderItem(items[i], cur, res.Warnings);
                if (rendered.Length > 0)
                    rendered = char.ToUpperInvariant(rendered[0]) + rendered.Substring(1);
                sb.Append(i + 1).Append(". ").Append(rendered);
            }
            res.Text = sb.ToString();
            return res;
        }
    }
}