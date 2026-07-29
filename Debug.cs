using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace DAN_Plugin
{
    // ─────────────────────────────────────────────────────────────────────────
    // Массовое заполнение: всем ТИПАМ арматуры (BuiltInCategory.OST_Rebar) — параметр
    // "#DAN_ГОСТ_Тип" = "ГОСТ 34028-2016". Без выбора элементов — сразу по всему проекту.
    // ─────────────────────────────────────────────────────────────────────────
    [Transaction(TransactionMode.Manual)]
    public class FillRebarGostType : IExternalCommand
    {
        private const string ParamName = "#DAN_ГОСТ_Тип";
        private const string GostValue = "ГОСТ 34028-2016";

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uiDoc = commandData.Application.ActiveUIDocument;
            Document doc = uiDoc.Document;

            List<Element> rebarTypes = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Rebar)
                .WhereElementIsElementType()
                .ToList();

            if (!rebarTypes.Any())
            {
                TaskDialog.Show("Ошибка", "Типы арматуры (OST_Rebar) в проекте не найдены.");
                return Result.Failed;
            }

            int updated = 0;
            var skippedNames = new List<string>();

            using (Transaction tx = new Transaction(doc, $"Заполнить \"{ParamName}\" у типов арматуры"))
            {
                tx.Start();
                foreach (Element rt in rebarTypes)
                {
                    Parameter p = rt.LookupParameter(ParamName);
                    if (p == null || p.IsReadOnly || p.StorageType != StorageType.String)
                    {
                        skippedNames.Add(rt.Name);
                        continue;
                    }
                    p.Set(GostValue);
                    updated++;
                }
                tx.Commit();
            }

            string msg = $"Обновлено типов арматуры: {updated} из {rebarTypes.Count}";
            if (skippedNames.Any())
                msg += $"\n\nПропущено (нет параметра \"{ParamName}\" или он только для чтения):\n"
                    + string.Join(", ", skippedNames);
            TaskDialog.Show("Готово", msg);

            return Result.Succeeded;
        }
    }
}
