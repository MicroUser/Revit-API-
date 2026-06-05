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

                    // Ищем только конкретные сокращения: РТм, Фм, СЦм, СНм, СЖм, СШм, Км, ЛМм, ЛПм, Пм, ПРПм, Бм
                    Match match = Regex.Match(
                        viewName,
                        @"(РТм|Фм|СЦм|СНм|СЖм|СШм|Км|ЛМм|ЛПм|Пм|ПРПм|Бм|СТм|Л|КРм|ППГм|РТЛм|ФЛм|ПРМм)[\s\-_]?(\d+)",
                        RegexOptions.IgnoreCase);

                    if (!match.Success)
                        continue;

                    string shortName = match.Groups[1].Value;
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