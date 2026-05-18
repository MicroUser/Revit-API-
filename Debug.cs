using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Linq;

namespace DAN_Plugin
{
    [Transaction(TransactionMode.Manual)]
    public class DiagnoseBreakLineFamily : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            Document doc = commandData.Application.ActiveUIDocument.Document;

            FamilySymbol sym = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .FirstOrDefault(fs =>
                    fs.Family.Name.Equals("(Оформление) Линия разрыва", StringComparison.OrdinalIgnoreCase) &&
                    fs.Name.Equals("М 1/20", StringComparison.OrdinalIgnoreCase));

            if (sym == null)
            {
                TaskDialog.Show("Ошибка", "Семейство не найдено.");
                return Result.Failed;
            }

            string report =
                $"Family.Name: {sym.Family.Name}\n" +
                $"Symbol.Name: {sym.Name}\n" +
                $"FamilyPlacementType: {sym.Family.FamilyPlacementType}\n" +
                $"Category: {sym.Category?.Name}\n" +
                $"IsAnnotation: {sym.Category?.CategoryType == CategoryType.Annotation}\n";

            TaskDialog.Show("Диагностика семейства", report);
            return Result.Succeeded;
        }
    }
}