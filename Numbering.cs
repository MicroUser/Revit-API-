using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Collections.Generic;
using System.Linq;

namespace DAN_Plugin
{
    [Transaction(TransactionMode.Manual)]
    public class NumberScheduleElements : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            ViewSchedule schedule = doc.ActiveView as ViewSchedule;
            if (schedule == null)
            {
                TaskDialog.Show("Ошибка", "Активный вид не является спецификацией.");
                return Result.Failed;
            }

            ICollection<ElementId> selectedIds = uidoc.Selection.GetElementIds();
            if (!selectedIds.Any())
            {
                TaskDialog.Show("Ошибка", "Не выбраны элементы в спецификации.");
                return Result.Failed;
            }

            using (Transaction trans = new Transaction(doc, "Нумерация элементов"))
            {
                trans.Start();
                new ScheduleElementNumberer(doc, schedule).Number(selectedIds);
                trans.Commit();
            }

            TaskDialog.Show("Готово", $"Пронумеровано элементов: {selectedIds.Count}");
            return Result.Succeeded;
        }
    }

    // ─────────────────────────────────────────────
    // Класс нумерации
    // ─────────────────────────────────────────────
    public class ScheduleElementNumberer
    {
        private const string PositionParamName = "BI_позиция";


        private readonly Document _doc;
        private readonly ViewSchedule _schedule;

        public ScheduleElementNumberer(Document doc, ViewSchedule schedule)
        {
            _doc = doc;
            _schedule = schedule;
        }

        public void Number(ICollection<ElementId> selectedIds)
        {
            var groups = selectedIds
                .Select(id => _doc.GetElement(id))
                .Where(elem => elem != null)
                .GroupBy(elem => GetGroupingKey(elem))
                .ToList();

            int counter = 1;

            foreach (var group in groups)
            {
                string positionValue = (counter++).ToString();

                foreach (Element elem in group)
                {
                    SetParameter(elem, PositionParamName, positionValue);
                }
            }
        }

        // ДОБАВИТЬ (новый метод в класс)
        private string GetGroupingKey(Element elem)
        {
            Element elemType = _doc.GetElement(elem.get_Parameter(BuiltInParameter.ELEM_TYPE_PARAM).AsElementId());

            string p1 = elemType?.LookupParameter("BI_фильтр_арматуры")?.AsString() ?? "";
            string p2 = elem.get_Parameter(BuiltInParameter.REBAR_ELEM_LENGTH)?.AsDouble().ToString() ?? "";
            string p3 = elemType?.LookupParameter("BI_диаметр_арматуры")?.AsDouble().ToString() ?? "";

            return $"{p1}|{p2}|{p3}";
        }


        private void SetParameter(Element elem, string paramName, string value)
        {
            Parameter param = elem.LookupParameter(paramName);
            if (param == null || param.IsReadOnly) return;
            param.Set(value);
        }




    }
}