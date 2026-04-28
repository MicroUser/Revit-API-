using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Xml.Linq;

namespace Пробник
{
    [Transaction(TransactionMode.Manual)]
    public class ScheduleCreate : IExternalCommand
    {

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {

            // ---------------------  0  Задание переменных     -------------------

            var uiapp = commandData.Application;
            var uidoc = uiapp.ActiveUIDocument;
            var doc = uidoc.Document;
                    
            bool Executer = false;


            // ---------------------  1  Получение категории      -------------------
            ElementId categoryId = new ElementId(BuiltInCategory.OST_Rebar);

            // ---------------------  2  Инициализация формы      -------------------
            Form form = new Form(Executer);
            form.ShowDialog();

            Executer = form.formExecute;

            if (Executer == false)
            {
                TaskDialog.Show("Пробник", "Отменено");
                return Result.Failed;
            }

            //  ---------------------  3  Получение данных с формы и подготовка списка суффиксов     -------------------
            string fieldName = "BI_марка_конструкции"; // поле для фильтра

            // Получаем строку ввода, разделяем на элементы по запятой или пробелу (учитываем множественные разделители)
            string rawInput = form.ConstructionMark.Text ?? string.Empty;
            var entries = rawInput
                .Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => !string.IsNullOrEmpty(s))
                .ToList();

            if (entries.Count == 0)
            {
                TaskDialog.Show("Пробник", "Не указаны марки конструкций.");
                return Result.Failed;
            }

            // Словарь соответствия вводимого кода -> базовое имя спецификации
            var nameMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                // Стены
                { "СЖМ", "Стены" },
                { "СНМ", "Стены" },
                { "СШМ", "Стены" },

                // Плиты
                { "ПМ", "Плиты" },
                { "ПРПМ", "Плиты" },

                // Фундаменты
                { "ФМ", "Фундаменты" }

                // добавьте другие соответствия по необходимости
            };

            // Сопоставление суффикса -> имя шаблона (измените имена шаблонов под ваш документ)
            var templateMap = new Dictionary<string, string>
            {
                { "СА", "*(спецификация)СА" },
                { "ВД", "*(спецификация)ВД" },
                { "ВРС", "*(спецификация)ВРС" }
            };

            // Собираем выбранные булевы в список суффиксов
            var selectedSuffixes = new List<string>();
            if (form.Schedule_SA != null && form.Schedule_SA.IsChecked == true) selectedSuffixes.Add("СА");
            if (form.Schedule_VD != null && form.Schedule_VD.IsChecked == true) selectedSuffixes.Add("ВД");
            if (form.Schedule_VRS != null && form.Schedule_VRS.IsChecked == true) selectedSuffixes.Add("ВРС");

            if (selectedSuffixes.Count == 0)
            {
                TaskDialog.Show("Пробник", "Не выбран ни один тип спецификации.");
                return Result.Failed;
            }

            int createdCount = 0;

            // ---------------------  4  Создание спецификаций в цикле и применение соответствующих шаблонов     -------------------
            using (Transaction myTr = new Transaction(doc, "Создать спецификации с шаблонами"))
            {
                myTr.Start();

                foreach (var entry in entries)
                {
                    // entry — например "СЖм-11" или "Пм-12"
                    string filterValue = entry;

                    // Извлекаем кодPrefix (часть до дефиса) для маппинга: "СЖм-11" -> "СЖм"
                    string codePrefix = filterValue.Split(new[] { '-' }, 2)[0].Trim();

                    // Определяем базовое имя спецификации по извлечённой первой части
                    string baseSpecName = "##Пробник";
                    if (!string.IsNullOrEmpty(codePrefix) && nameMap.TryGetValue(codePrefix.ToUpperInvariant(), out var mappedName))
                    {
                        baseSpecName = mappedName;
                    }

                    foreach (var suffix in selectedSuffixes)
                    {
                        try
                        {
                            // Создаём спецификацию
                            ViewSchedule schedule = ViewSchedule.CreateSchedule(doc, categoryId);

                            // Формируем имя: базовое имя по первой части + полный введённый код + суффикс
                            schedule.Name = $"##_{baseSpecName}_{filterValue}_{suffix}";

                            // Получаем имя шаблона для данного суффикса (если есть)
                            string templateName = null;
                            if (templateMap.TryGetValue(suffix, out var tn))
                            {
                                templateName = tn;
                            }

                            // Ищем и применяем шаблон для данной спецификации
                            if (!string.IsNullOrEmpty(templateName))
                            {
                                View template = new FilteredElementCollector(doc)
                                    .OfClass(typeof(View))
                                    .Cast<View>()
                                    .FirstOrDefault(v => v.IsTemplate && v.Name == templateName);

                                if (template != null)
                                {
                                    schedule.ViewTemplateId = template.Id;
                                }
                            }

                            // Обновляем документ чтобы поля шаблона появились в спецификации
                            doc.Regenerate();

                            // Попытка установить параметр спецификации (если параметр доступен)
                            try
                            {
                                var param = schedule.LookupParameter(fieldName);
                                if (param != null && !string.IsNullOrEmpty(filterValue))
                                {
                                    param.Set(filterValue);
                                }
                            }
                            catch
                            {
                                // игнорируем ошибки установки параметра
                            }

                            // Поиск поля в определении и добавление фильтра
                            ScheduleDefinition definition = schedule.Definition;
                            ScheduleField targetField = null;

                            for (int i = 0; i < definition.GetFieldCount(); i++)
                            {
                                ScheduleField field = definition.GetField(i);
                                if (field.GetName() == fieldName)
                                {
                                    targetField = field;
                                    break;
                                }
                            }

                            if (targetField != null && !string.IsNullOrEmpty(filterValue))
                            {
                                ScheduleFilter filter = new ScheduleFilter(
                                    targetField.FieldId,
                                    ScheduleFilterType.Equal,
                                    filterValue
                                );

                                definition.AddFilter(filter);
                            }

                            createdCount++;
                        }
                        catch (Exception exInner)
                        {
                            // Продолжаем создавать остальные спецификации, логируем минимально
                            TaskDialog.Show("Ошибка создания спецификации", $"Вход: '{entry}', Суффикс: '{suffix}'. Ошибка: {exInner.Message}");
                        }
                    }
                }

                myTr.Commit();
            }

            TaskDialog.Show("Результат", $"Создано спецификаций: {createdCount}");
            return Result.Succeeded;
        }



    }


}
