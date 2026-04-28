using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace NametoMark
{
    [Transaction(TransactionMode.Manual)]
    public class SetViewMark : IExternalCommand
    {
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

                    // Исключаем виды типа "Узел 3", "Узел 58"
                    if (Regex.IsMatch(viewName, @"^Узел\s+\d+$", RegexOptions.IgnoreCase))
                        continue;

                    // Ищем шаблон типа: Пм_3, ЛММ_5, БФМ-7
                    Match match = Regex.Match(
                        viewName,
                        @"([А-Яа-яA-Za-z]+)[\s\-_]?(\d+)",
                        RegexOptions.IgnoreCase);

                    if (!match.Success)
                        continue;

                    string shortName = match.Groups[1].Value;
                    string number = match.Groups[2].Value;

                    // Форматирование сокращения:
                    // 2 буквы → Пм, Км
                    // 3+ буквы → ЛМм, БФм
                    if (shortName.Length == 2)
                    {
                        shortName = char.ToUpper(shortName[0]) +
                                    shortName.Substring(1).ToLower();
                    }
                    else if (shortName.Length >= 3)
                    {
                        shortName = shortName.Substring(0, shortName.Length - 1).ToUpper() +
                                    shortName.Substring(shortName.Length - 1).ToLower();
                    }

                    string resultValue = $"{shortName}-{number}";

                    Parameter param = view.LookupParameter("BI_марка_конструкции");

                    if (param == null || param.IsReadOnly)
                        continue;

                    string currentValue = param.AsString();

                    // Если параметр уже заполнен правильно — пропускаем
                    if (!string.IsNullOrEmpty(currentValue) &&
                        currentValue.Equals(resultValue, StringComparison.OrdinalIgnoreCase))
                    {
                        
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