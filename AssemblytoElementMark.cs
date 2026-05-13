using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DAN_Plugin
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class AssemblytoElementMark : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiApp = commandData.Application;
            UIDocument uiDoc = uiApp.ActiveUIDocument;
            Document doc = uiDoc.Document;

            try
            {
                // Собираем все сборки в документе
                List<AssemblyInstance> assemblies = new FilteredElementCollector(doc)
                    .OfClass(typeof(AssemblyInstance))
                    .Cast<AssemblyInstance>()
                    .ToList();

                if (assemblies.Count == 0)
                {
                    TaskDialog.Show("Информация", "В документе не найдено ни одной сборки.");
                    return Result.Cancelled;
                }

                int updatedElements = 0;
                int skippedAssemblies = 0;
                List<string> errors = new List<string>();

                using (Transaction trans = new Transaction(doc, "Копировать Марку из сборок в элементы"))
                {
                    trans.Start();

                    foreach (AssemblyInstance assembly in assemblies)
                    {
                        // Получаем параметр "Комментарии" у сборки
                        Parameter markParam = assembly.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);

                        if (markParam == null || string.IsNullOrWhiteSpace(markParam.AsString()))
                        {
                            skippedAssemblies++;
                            continue;
                        }

                        string markValue = markParam.AsString();

                        if (string.IsNullOrWhiteSpace(markValue))
                        {
                            skippedAssemblies++;
                            continue;
                        }

                        // Получаем все элементы, входящие в сборку
                        ICollection<ElementId> memberIds = assembly.GetMemberIds();

                        foreach (ElementId memberId in memberIds)
                        {
                            Element member = doc.GetElement(memberId);
                            if (member == null) continue;

                            // Пробуем записать в параметр "Марка" элемента
                            bool written = TrySetMark(member, markValue);

                            if (written)
                                updatedElements++;
                            else
                                errors.Add($"Элемент {member.Id} ({member.Category?.Name ?? "?"}): параметр не найден");
                        }
                    }

                    trans.Commit();
                }

                // Итоговое сообщение
                string report = $"Готово!\n" +
                                $"Обработано сборок: {assemblies.Count - skippedAssemblies} из {assemblies.Count}\n" +
                                $"Обновлено элементов: {updatedElements}";

                if (skippedAssemblies > 0)
                    report += $"\nПропущено сборок (нет Марки): {skippedAssemblies}";

                if (errors.Count > 0)
                    report += $"\nНе удалось записать в {errors.Count} элемент(ов):\n" +
                              string.Join("\n", errors.Take(10)); // показываем первые 10

                TaskDialog.Show("Результат", report);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }

        /// <summary>
        /// Пытается записать значение марки в элемент.
        /// Проверяет встроенный параметр ALL_MODEL_MARK, затем ищет по имени.
        /// </summary>
        private bool TrySetMark(Element element, string value)
        {
            Parameter p = element.get_Parameter(BuiltInParameter.DOOR_NUMBER);

            if (p == null || p.IsReadOnly)
                return false;

            p.Set(value);
            return true;
        }

        
       
        
    }
}