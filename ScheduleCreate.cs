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

namespace DAN_Plugin
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
                TaskDialog.Show("DAN_Plugin", "Отменено");
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
                TaskDialog.Show("DAN_Plugin", "Не указаны марки конструкций.");
                return Result.Failed;
            }

            // Словарь соответствия вводимого кода -> базовое имя спецификации
            var nameMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
{
                // Стены
                { "СЖм", "Стены" },
                { "СЦм", "Стены" },
                { "СНм", "Стены" },

                // Плиты
                { "Пм",   "Плиты" },
                { "ПРПм", "Плиты" },
                { "КПТм", "Плиты" },

                // Колонны
                { "Км", "Колонны" },

                // Балки
                { "Бм", "Балки" },

                // Фундаменты
                { "Фм",  "Фундаменты" },
                { "РТЛм", "Фундаменты" },
                { "ФЛм", "Фундаменты" },
                { "РТм", "Фундаменты" },

                // Лестницы
                { "Л",   "Лестницы" },
                { "ЛМм", "Лестницы" },
                { "ЛПм", "Лестницы" },
            };

            // Сопоставление суффикса -> имя шаблона (измените имена шаблонов под ваш документ)
            var templateMap = new Dictionary<string, string>
            {
                { "СА", "*(спецификация)СА" },
                { "ВД", "*(спецификация)ВД" },
                { "ВРС", "*(спецификация)ВРС" },
                { "ВМ", "*(спецификация)ВМ(сборка)_АГСК" }
            };

            // Собираем выбранные булевы в список суффиксов
            var selectedSuffixes = new List<string>();
            if (form.Schedule_SA != null && form.Schedule_SA.IsChecked == true) selectedSuffixes.Add("СА");
            if (form.Schedule_VD != null && form.Schedule_VD.IsChecked == true) selectedSuffixes.Add("ВД");
            if (form.Schedule_VRS != null && form.Schedule_VRS.IsChecked == true) selectedSuffixes.Add("ВРС");
            if (form.Schedule_VM != null && form.Schedule_VM.IsChecked == true) selectedSuffixes.Add("ВМ");
            if (selectedSuffixes.Count == 0)
            {
                TaskDialog.Show("DAN_Plugin", "Не выбран ни один тип спецификации.");
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

                    // Ищем совпадение в nameMap без учёта регистра, берём эталонный ключ
                    string normalizedPrefix = nameMap.Keys
                        .FirstOrDefault(k => string.Equals(k, codePrefix, StringComparison.OrdinalIgnoreCase));

                    if (normalizedPrefix != null)
                    {
                        codePrefix = normalizedPrefix;
                        // Заменяем префикс в filterValue на эталонный
                        string suffix2 = filterValue.Substring(codePrefix.Length); // "-111"
                        filterValue = normalizedPrefix + suffix2; // "Пм-111"
                    }

                    // Определяем базовое имя спецификации по извлечённой первой части
                    string baseSpecName = "##DAN_Plugin";
                    if (!string.IsNullOrEmpty(codePrefix) && nameMap.TryGetValue(codePrefix.ToUpperInvariant(), out var mappedName))
                    {
                        baseSpecName = mappedName;
                    }

                    foreach (var suffix in selectedSuffixes)
                    {
                        try
                        {
                            // Создаём спецификацию
                            ViewSchedule schedule = suffix == "ВМ"
    ? ViewSchedule.CreateMaterialTakeoff(doc, new ElementId(BuiltInCategory.OST_Assemblies))
    : ViewSchedule.CreateSchedule(doc, categoryId);

                            // Формируем имя: базовое имя по первой части + полный введённый код + суффикс
                            var suffixNumber = new Dictionary<string, string>
                                {
                                    { "СА",  "2" },
                                    { "ВМ", "3" },
                                    { "ВД",  "4" },
                                    { "ВРС", "5" }
                                };

                            string specNumber = suffixNumber.TryGetValue(suffix, out var num) ? num : "";
                            schedule.Name = $"({baseSpecName})_{filterValue}_{specNumber}{suffix}";

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


                            try
                            {
                                var markParam = schedule.LookupParameter(fieldName); // BI_марка_конструкции
                                if (markParam != null && !markParam.IsReadOnly)
                                    markParam.Set(filterValue);
                            }
                            catch { }

                            try
                            {
                                var groupingParam = schedule.LookupParameter("BI_группирование");
                                if (groupingParam != null && !groupingParam.IsReadOnly && baseSpecName != "##DAN_Plugin")
                                    groupingParam.Set(baseSpecName);
                            }
                            catch { }

                            try
                            {
                                var commentParam = schedule.LookupParameter("BI_комментарии_к_виду");
                                if (commentParam != null && !commentParam.IsReadOnly)
                                    commentParam.Set(suffix);
                            }
                            catch { }

                            // Поиск поля в определении и добавление фильтра
                            ScheduleDefinition definition = schedule.Definition;

                            if (suffix == "ВМ")
                            {
                                ScheduleField commentsField = null;
                                for (int i = 0; i < definition.GetFieldCount(); i++)
                                {
                                    ScheduleField field = definition.GetField(i);
                                    if (field.GetName() == "Комментарии")
                                    {
                                        commentsField = field;
                                        break;
                                    }
                                }
                                if (commentsField != null)
                                {
                                    definition.AddFilter(new ScheduleFilter(
                                        commentsField.FieldId,
                                        ScheduleFilterType.Equal,
                                        filterValue
                                    ));
                                }
                            }
                            else
                            {
                                // Стандартный фильтр для остальных
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
                                    definition.AddFilter(new ScheduleFilter(
                                        targetField.FieldId,
                                        ScheduleFilterType.Equal,
                                        filterValue
                                    ));
                                }
                            }
                        

                            createdCount++;
                        }
                        catch (Exception exInner)
                        {
                            TaskDialog.Show("Ошибка создания спецификации",
                                $"Спецификация {suffix} для марки {filterValue} уже создана.");
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
