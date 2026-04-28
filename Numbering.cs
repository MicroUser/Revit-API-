using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Collections.Generic;
using System.Linq;

// ─────────────────────────────────────────────
// Команда — точка входа
// ─────────────────────────────────────────────
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
    private const string RebarShapeParamName = "Форма арматурного стержня";

    private readonly Document _doc;
    private readonly ViewSchedule _schedule;

    public ScheduleElementNumberer(Document doc, ViewSchedule schedule)
    {
        _doc = doc;
        _schedule = schedule;
    }

    public void Number(ICollection<ElementId> selectedIds)
    {
        int normalCounter = 1;
        int skaboCounter = 1;

        foreach (ElementId id in selectedIds)
        {
            Element elem = _doc.GetElement(id);
            if (elem == null) continue;

            string positionValue = IsRebarShape21(elem)
                ? $"Ск-{skaboCounter++}"
                : (normalCounter++).ToString();

            SetParameter(elem, PositionParamName, positionValue);
        }
    }

    private bool IsRebarShape21(Element elem)
    {
        Parameter shapeParam = elem.LookupParameter(RebarShapeParamName);
        if (shapeParam == null) return false;

        Element shapeElem = _doc.GetElement(shapeParam.AsElementId());
        return shapeElem?.Name.Trim() == "21";
    }

    private void SetParameter(Element elem, string paramName, string value)
    {
        Parameter param = elem.LookupParameter(paramName);
        if (param == null || param.IsReadOnly) return;
        param.Set(value);
    }
}