using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Collections.Generic;
using System.Linq;

namespace DAN_Plugin
{
    [Transaction(TransactionMode.Manual)]
    public class ClearParameterInSchedule : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            // Получаем активный вид — должен быть спецификацией
            ViewSchedule schedule = doc.ActiveView as ViewSchedule;
            if (schedule == null)
            {
                TaskDialog.Show("Ошибка", "Активный вид не является спецификацией.");
                return Result.Failed;
            }

            // Получаем выбранные элементы
            ICollection<ElementId> selectedIds = uidoc.Selection.GetElementIds();
            if (!selectedIds.Any())
            {
                TaskDialog.Show("Ошибка", "Не выбраны элементы в спецификации.");
                return Result.Failed;
            }


            // Имя параметра, который нужно очистить
            string parameterName = "BI_позиция"; // <-- замените на нужное имя

            using (Transaction trans = new Transaction(doc, "Очистить параметр"))
            {
                trans.Start();

                int clearedCount = 0;



                foreach (ElementId id in selectedIds)
                {
                    Element elem = doc.GetElement(id);
                    if (elem == null) continue;

                    Parameter param = elem.LookupParameter(parameterName);
                    if (param == null || param.IsReadOnly) 
                    param.Set(string.Empty);

                    clearedCount++;
                }

                trans.Commit();

                TaskDialog.Show("Готово", $"Параметр очищен у {clearedCount} элементов.");
            }

            return Result.Succeeded;
        }
    }
}