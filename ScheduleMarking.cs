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
    //  ВСПОМОГАТЕЛЬНЫЙ КЛАСС — извлечение марок по известному списку
    // ─────────────────────────────────────────────────────────────────
    internal static class SheetMarkHelper
    {
        // Исчерпывающий список известных марок
        private static readonly HashSet<string> KnownMarks = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "РТм", "Фм", "СЦм", "СНм", "СЖм", "СШм",
            "Км", "ЛМм", "ЛПм", "Пм", "ПРПм", "Бм"
        };

        // Ищем ВСЕ вхождения: буквы + необязательный разделитель + цифры
        private static readonly Regex MarkRegex = new Regex(
            @"([А-Яа-яA-Za-z]+)[\s\-_]?(\d+)",
            RegexOptions.Compiled);

        /// <summary>
        /// Извлекает ВСЕ марки из имени листа, которые входят в KnownMarks.
        /// Возвращает пустой список если ни одной марки не найдено.
        /// Примеры:
        ///   "СНм-1 Перекрытие"      → ["СНм-1"]
        ///   "СНм-1 СЖм-2 Секция А" → ["СНм-1", "СЖм-2"]
        ///   "ЛМм_3 Пм-5 Стена"     → ["ЛМм-3", "Пм-5"]
        ///   "Узел 5 ..."            → []
        /// </summary>
        public static List<string> ExtractAllMarks(string sheetName)
        {
            var result = new List<string>();

            if (string.IsNullOrWhiteSpace(sheetName))
                return result;

            foreach (Match match in MarkRegex.Matches(sheetName.Trim()))
            {
                string letters = match.Groups[1].Value;
                string number = match.Groups[2].Value;

                if (!KnownMarks.Contains(letters))
                    continue;

                string canonical = KnownMarks.First(m => string.Equals(m, letters, StringComparison.OrdinalIgnoreCase));
                result.Add($"{canonical}-{number}");
            }

            return result;
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
        private const string SHEET_PREFIX = "КЖ";

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
                        string comment = assembly
                            .get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)
                            ?.AsString()
                            ?.Trim();

                        if (string.IsNullOrEmpty(comment))
                        {
                            skippedCount++;
                            continue;
                        }

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
        /// Собирает все листы и группирует номера по ВСЕМ найденным маркам.
        /// Один лист может попасть в несколько марок одновременно.
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

                // Нормализуем номер листа:
                //   "045.1" → "45"  (убираем дробную часть и ведущие нули)
                //   "042"   → "42"  (убираем ведущие нули)
                //   "125,5" → "125"
                int dotIndex = sheetNumber.IndexOfAny(new[] { '.', ',' });
                if (dotIndex > 0)
                    sheetNumber = sheetNumber.Substring(0, dotIndex);
                sheetNumber = sheetNumber.TrimStart('0');
                if (string.IsNullOrEmpty(sheetNumber))
                    continue;

                List<string> marks = SheetMarkHelper.ExtractAllMarks(sheetName);

                foreach (string mark in marks)
                {
                    if (!result.ContainsKey(mark))
                        result[mark] = new List<string>();

                    if (!result[mark].Contains(sheetNumber))
                        result[mark].Add(sheetNumber);
                }
            }

            return result;
        }

        /// <summary>
        /// Форматирует список номеров листов в строку с префиксом КЖ.
        ///
        /// Алгоритм:
        ///   1. Сортируем числа
        ///   2. Схлопываем последовательности в диапазоны
        ///   3. Каждую группу (диапазон или одиночное) предваряем "КЖ "
        ///   4. Группы соединяем через "; "
        ///   5. Добавляем префикс "на листах "
        ///
        /// Примеры:
        ///   [1..10]          → "на листах КЖ 1...10"
        ///   [1..10, 12]      → "на листах КЖ 1...10; КЖ 12"
        ///   [1..10, 12..15]  → "на листах КЖ 1...10; КЖ 12...15"
        ///   [5]              → "на листах КЖ 5"
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

            var groups = new List<string>();
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

                string group = end == start
                    ? $"{SHEET_PREFIX} - {start}"
                    : $"{SHEET_PREFIX} - {start}...{end}";

                groups.Add(group);
                i++;
            }

            // Нечисловые номера добавляем в конец как отдельные группы
            foreach (string s in nonNumeric)
                groups.Add($"{SHEET_PREFIX} - {s}");

            return $"на листах {string.Join("; ", groups)}";
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