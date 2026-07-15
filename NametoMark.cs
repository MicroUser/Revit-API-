using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace DAN_Plugin
{
    [Transaction(TransactionMode.Manual)]
    public class SetViewMark : IExternalCommand
    {
        // Канонические сокращения марок конструкций. Ключ — любой регистр (поиск
        // case-insensitive), значение — то самое написание, которое нужно записать
        // в параметр. Длинные сокращения идут первыми, чтобы при альтернации регулярки
        // не сработал более короткий префикс на месте более длинного.
        private static readonly Dictionary<string, string> MarkPrefixMap =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["РТЛм"] = "РТЛм",
                ["ППГм"] = "ППГм",
                ["ПРПм"] = "ПРПм",
                ["ПРМм"] = "ПРМм",
                ["РТм"] = "РТм",
                ["ЛМм"] = "ЛМм",
                ["ЛПм"] = "ЛПм",
                ["СНм"] = "СНм",
                ["СЖм"] = "СЖм",
                ["СЦм"] = "СЦм",
                ["СШм"] = "СШм",
                ["ФЛм"] = "ФЛм",
                ["КРм"] = "КРм",
                ["Км"] = "Км",
                ["Пм"] = "Пм",
                ["Фм"] = "Фм",
                ["Бм"] = "Бм",
                ["Бл"] = "Бл",
                ["Л"] = "Л",
                ["КПТм"] = "КПТм",
                ["РМм"] = "РМм",
                ["БФм"] = "БФм",
            };

        private static readonly Regex MarkRegex = new Regex(
            // (?<![...]) — перед сокращением не должно быть буквы/цифры, иначе оно
            // может совпасть с концом случайного слова (например, "л" в "Узел").
            $@"(?<![A-Za-zА-Яа-яЁё0-9])({string.Join("|", MarkPrefixMap.Keys.OrderByDescending(k => k.Length).Select(Regex.Escape))})[\s\-_]?(\d+)",
            RegexOptions.IgnoreCase);

        public Result Execute(
            ExternalCommandData commandData,
            ref string message,
            ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            ICollection<ElementId> selectedIds = uidoc.Selection.GetElementIds();

            if (!selectedIds.Any())
            {
                TaskDialog.Show("Ошибка", "Не выбраны виды/спецификации.");
                return Result.Cancelled;
            }

            using (Transaction t = new Transaction(doc, "Запись BI_марка_конструкции"))
            {
                t.Start();

                foreach (ElementId id in selectedIds)
                {
                    View view = doc.GetElement(id) as View;
                    if (view == null)
                        continue;

                    string viewName = view.Name;

                    Match match = MarkRegex.Match(viewName);

                    if (!match.Success)
                        continue;

                    // Приводим к каноническому написанию из словаря независимо от
                    // регистра, в котором сокращение встретилось в имени вида.
                    string shortName = MarkPrefixMap[match.Groups[1].Value];
                    string number = match.Groups[2].Value;

                    string resultValue = $"{shortName}-{number}";

                    Parameter param = view.LookupParameter("BI_марка_конструкции");

                    if (param == null || param.IsReadOnly)
                        continue;

                    string currentValue = param.AsString();

                    // Если параметр уже заполнен правильно — пропускаем
                    if (!string.IsNullOrEmpty(currentValue) &&
                        currentValue.Equals(resultValue, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    param.Set(resultValue);
                }

                t.Commit();
            }

            TaskDialog.Show("Готово", "Параметр BI_марка_конструкции заполнен.");

            return Result.Succeeded;
        }
    }
}