using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Пробник
{
    // ─────────────────────────────────────────────────────────────────
    //  ВСПОМОГАТЕЛЬНЫЙ КЛАСС — логика извлечения и форматирования марки
    //  Взята из SetViewMark и адаптирована для имён листов
    // ─────────────────────────────────────────────────────────────────
    internal static class SheetMarkHelper
    {
        // Ищем паттерн: буквы + необязательный разделитель + цифры
        // Примеры имён листов: "СНм-1 Название", "ЛММ_5 Название", "Пм 2 Название"
        // Результат:           "СНм-1",           "ЛМм-5",           "Пм-2"
        private static readonly Regex MarkRegex = new Regex(
            @"([А-Яа-яA-Za-z]+)[\s\-_]?(\d+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // Исключаем листы типа "Узел 3" — если вдруг такие встречаются
        private static readonly Regex ExcludeRegex = new Regex(
            @"^Узел\s+\d+$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// Извлекает и нормализует марку из имени листа.
        /// Возвращает null если имя не соответствует паттерну.
        ///
        /// Форматирование букв (идентично SetViewMark):
        ///   2 буквы  → Пм, Км       (первая заглавная, вторая строчная)
        ///   3+ букв  → ЛМм, СНм     (все кроме последней заглавные, последняя строчная)
        ///
        /// Примеры:
        ///   "СНм-1 Перекрытие" → "СНм-1"
        ///   "ЛММ_5 Стена"      → "ЛМм-5"
        ///   "Пм 2 Колонна"     → "Пм-2"
        /// </summary>
        public static string ExtractMark(string sheetName)
        {
            if (string.IsNullOrWhiteSpace(sheetName))
                return null;

            if (ExcludeRegex.IsMatch(sheetName))
                return null;

            Match match = MarkRegex.Match(sheetName);
            if (!match.Success)
                return null;

            string letters = match.Groups[1].Value;
            string number = match.Groups[2].Value;

            string formatted;
            if (letters.Length == 2)
            {
                formatted = char.ToUpper(letters[0]) +
                            letters.Substring(1).ToLower();
            }
            else
            {
                formatted = letters.Substring(0, letters.Length - 1).ToUpper() +
                            letters.Substring(letters.Length - 1).ToLower();
            }

            return $"{formatted}-{number}";
        }
    }

    // ─────────────────────────────────────────────────────────────────
    //  ОСНОВНАЯ КОМАНДА
    // ─────────────────────────────────────────────────────────────────
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class ScheduleMarking : IExternalCommand
    {
        private const string TARGET_PARAM_NAME = "BI_ссылка_на_лист";

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uiDoc = commandData.Application.ActiveUIDocument;
            Document doc = uiDoc.Document;

            try
            {
                // 1. Собираем все листы и группируем по марке
                Dictionary<string, List<string>> markToSheetNumbers = CollectSheetsByMark(doc);

                if (markToSheetNumbers.Count == 0)
                {
                    TaskDialog.Show("Результат", "Не найдено ни одного листа с распознаваемой маркой.");
                    return Result.Cancelled;
                }

                // 2. Собираем все сборки
                List<AssemblyInstance> assemblies = new FilteredElementCollector(doc)
                    .OfClass(typeof(AssemblyInstance))
                    .Cast<AssemblyInstance>()
                    .ToList();

                if (assemblies.Count == 0)
                {
                    TaskDialog.Show("Результат", "В проекте не найдено ни одной сборки.");
                    return Result.Cancelled;
                }

                // 3. Записываем в транзакции
                int updatedCount = 0;
                int skippedCount = 0;
                var warnings = new List<string>();

                using (Transaction tx = new Transaction(doc, "Записать листы в сборки"))
                {
                    tx.Start();

                    foreach (AssemblyInstance assembly in assemblies)
                    {
                        // Комментарий сборки должен совпадать с маркой листа — например "СНм-1"
                        string comment = assembly
                            .get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)
                            ?.AsString()
                            ?.Trim();

                        if (string.IsNullOrEmpty(comment))
                        {
                            skippedCount++;
                            continue;
                        }

                        // Сравниваем без учёта регистра
                        string matchedKey = markToSheetNumbers.Keys
                            .FirstOrDefault(k => string.Equals(k, comment, StringComparison.OrdinalIgnoreCase));

                        if (matchedKey == null)
                        {
                            skippedCount++;
                            continue;
                        }

                        Parameter targetParam = assembly.LookupParameter(TARGET_PARAM_NAME);

                        if (targetParam == null)
                        {
                            warnings.Add($"{comment} — нет параметра \"{TARGET_PARAM_NAME}\"");
                            skippedCount++;
                            continue;
                        }

                        if (targetParam.IsReadOnly)
                        {
                            warnings.Add($"{comment} — параметр только для чтения");
                            skippedCount++;
                            continue;
                        }

                        string sheetList = FormatSheetList(markToSheetNumbers[matchedKey]);
                        targetParam.Set(sheetList);
                        updatedCount++;
                    }

                    tx.Commit();
                }

                // 4. Отчёт
                ShowResultDialog(markToSheetNumbers, updatedCount, skippedCount, warnings);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("Ошибка", $"Произошла ошибка:\n\n{ex.Message}");
                return Result.Failed;
            }
        }

        /// <summary>
        /// Собирает все листы и группирует номера по марке через SheetMarkHelper.
        /// </summary>
        private Dictionary<string, List<string>> CollectSheetsByMark(Document doc)
        {
            var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            var sheets = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSheet))
                .Cast<ViewSheet>()
                .ToList();

            foreach (ViewSheet sheet in sheets)
            {
                string sheetName = sheet.Name?.Trim() ?? string.Empty;
                string sheetNumber = sheet.SheetNumber?.Trim() ?? string.Empty;

                if (string.IsNullOrEmpty(sheetName) || string.IsNullOrEmpty(sheetNumber))
                    continue;

                string mark = SheetMarkHelper.ExtractMark(sheetName);
                if (mark == null)
                    continue;

                if (!result.ContainsKey(mark))
                    result[mark] = new List<string>();

                result[mark].Add(sheetNumber);
            }

            return result;
        }

        /// <summary>
        /// Форматирует список номеров в строку "на листах КЖ - 10...15".
        /// Два и более подряд идущих числа схлопываются в диапазон через "...".
        /// Примеры:
        ///   [10,11,12,13,14,15]  → "на листах КЖ - 10...15"
        ///   [10,11,13,15]        → "на листах КЖ - 10...11, 13, 15"
        ///   [10]                 → "на листах КЖ - 10"
        /// </summary>
        private string FormatSheetList(List<string> sheetNumbers)
        {
            var numeric = new List<int>();
            var nonNumeric = new List<string>();

            foreach (string s in sheetNumbers)
            {
                if (int.TryParse(s.Trim(), out int n))
                    numeric.Add(n);
                else
                    nonNumeric.Add(s.Trim());
            }

            numeric.Sort();

            var parts = new List<string>();
            int i = 0;

            while (i < numeric.Count)
            {
                int start = numeric[i];
                int end = start;

                while (i + 1 < numeric.Count && numeric[i + 1] == numeric[i] + 1)
                {
                    i++;
                    end = numeric[i];
                }

                parts.Add(end == start ? start.ToString() : $"{start}...{end}");
                i++;
            }

            parts.AddRange(nonNumeric);

            return $"на листах КЖ - {string.Join(", ", parts)}";
        }

        private void ShowResultDialog(
            Dictionary<string, List<string>> markMap,
            int updatedCount,
            int skippedCount,
            List<string> warnings)
        {
            var sb = new StringBuilder();

            sb.AppendLine($"✅ Обновлено сборок: {updatedCount}");
            sb.AppendLine($"⏭ Пропущено:        {skippedCount}");
            sb.AppendLine();

            sb.AppendLine("─── Найденные марки и листы ───");
            foreach (var kvp in markMap.OrderBy(k => k.Key))
            {
                string sheets = string.Join(", ", kvp.Value.OrderBy(s => s));
                sb.AppendLine($"  {kvp.Key}: {sheets}");
            }

            if (warnings.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("─── Предупреждения ───");
                foreach (string w in warnings)
                    sb.AppendLine($"  ⚠ {w}");
            }

            TaskDialog dialog = new TaskDialog("Листы → Сборки");
            dialog.MainInstruction = $"Готово: {updatedCount} сборок обновлено";
            dialog.MainContent = sb.ToString();
            dialog.Show();
        }
    }
}