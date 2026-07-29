using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.UI;
using VCRevitRibbonUtil;
using DAN_Plugin;
using RevitKJChecklist;
using Пробник.Properties;


namespace Пробник
{


    internal class MainPanel : IExternalApplication
    {
        public Result OnStartup(UIControlledApplication a)
        {

            Ribbon.GetApplicationRibbon(a)
              .Tab("DAN").Panel("КЖ")

              .CreateButton<ScheduleMarking>("Спецификация каркасов", "Спецификация " +
              "каркасов",
              btn => btn
              .SetLargeImage(Resources.cells)
              .SetSmallImage(Resources.cells)
              .SetLongDescription("Код ищет в имени листов марки консутрукций и вписывает их сборкам в параметр BI_ссылка_на_лист. " +
              "Если в проекте нет листа с нужным именем, то в сборку ничего не попадёт")
              )

            .CreateSeparator()

              .CreateButton<CreateRebarAnnotation>("Аннотация доп. арматуры плит", "Аннотация доп.арм",
              btn => btn
              .SetLargeImage(Resources.slab_32)
              .SetSmallImage(Resources.slab_16)
              .SetLongDescription("Создает аннотацию для дополнительной арматуры плит по центру")
              )

              .CreateSeparator()

              .CreateButton<WallRebarAnnotation>("Аннотация арматуры стен", "Аннотация\nарм. стен",
              btn => btn
              .SetLargeImage(Resources.Annotation_32)
              .SetSmallImage(Resources.Annotation_16)
              .SetLongDescription("Создаёт аннотацию горизонтальной арматуры и П-шек для крайней стены сборки на каждом уровне")
              )

              .CreateSeparator()

              .CreateButton<CreatElevationTags>("Опалубка стен", "Опалубка стен",
              btn => btn
              .SetLargeImage(Resources.dimension_32)
              .SetSmallImage(Resources.dimension_16)
              .SetLongDescription("Создает высотные отметки, размеры ")
              )

              .CreateSeparator()

              .CreateButton<KzhNotes.NotesCommand>("Примечания на листах", "Примечания",
              btn => btn
              .SetLargeImage(Resources.pencil_32)
              .SetSmallImage(Resources.pencil_16)
              .SetLongDescription("Генератор примечаний на листах КЖ: библиотека пунктов с токенами, " +
              "автоматическое разрешение ссылок на листы, сохранение состава в ExtensibleStorage.")
              )

              .CreateSeparator()

              .CreateButton<ChecklistCommand>("Чек-лист КЖ", "Чек-лист",
              btn => btn
              .SetLargeImage(Resources.checklist_32)
              .SetSmallImage(Resources.checklist_16)
              .SetLongDescription("Открывает чек-лист проверки конструкций.")
              )

              .CreateSeparator()

              .CreateButton<ExportViewTemplatesCommand>("Экспорт шаблонов видов", "Шаблоны\nвидов",
              btn => btn
              .SetLargeImage(Resources.pencil_32)
              .SetSmallImage(Resources.pencil_16)
              .SetLongDescription("Выгружает все имена шаблонов видов из проекта на рабочий стол в формате для вставки в словарь переименования.")
              )

              .CreateSeparator()

              .CreateButton<RenameViewsCommand>("Переименование видов", "Переим.\nвиды",
              btn => btn
              .SetLargeImage(Resources.pencil_32)
              .SetSmallImage(Resources.pencil_16)
              .SetLongDescription("Переименовывает выбранные виды по шаблону: {BI_марка_конструкции}_{аббр}_{суффикс}. " +
              "Шаблон вида должен содержать # и код в конце имени (например: *01_КЖ_(01_Тип_#суффикс)_КОД).")
              );

            // Видео-инструкции по F1: обёртка VCRevitRibbonUtil.Button не даёт доступа к нативному
            // Autodesk.Revit.UI.ContextualHelp (это то, что Revit открывает по F1, когда кнопка в
            // фокусе), поэтому назначаем его напрямую через Revit API — ПОСЛЕ того как кнопки уже
            // созданы, по их внутреннему имени (первый аргумент CreateButton, см. выше).
            var videoLinks = new Dictionary<string, string>
            {
                ["Спецификация каркасов"] = "https://youtu.be/EMZZ8qeVNzg",
                ["Аннотация доп. арматуры плит"] = "https://youtu.be/Dywl_Y47nVY",
                ["Аннотация арматуры стен"] = "https://youtu.be/CH6c1r1kSNc",
                ["Опалубка стен"] = "https://youtu.be/1MxJJG7TpuI",
                ["Примечания на листах"] = "https://youtu.be/7AG21HpX1FY",
                ["Чек-лист КЖ"] = "https://youtu.be/tN2wmnG9PfM",
            };

            RibbonPanel kzhPanel = a.GetRibbonPanels("DAN").FirstOrDefault(p => p.Name == "КЖ");
            if (kzhPanel != null)
            {
                foreach (RibbonItem item in kzhPanel.GetItems())
                {
                    if (item is PushButton pb && videoLinks.TryGetValue(pb.Name, out string url))
                        pb.SetContextualHelp(new ContextualHelp(ContextualHelpType.Url, url));
                }
            }

            return Result.Succeeded;
        }
        public Result OnShutdown(UIControlledApplication a)
        {
            return Result.Succeeded;
        }
    }
}
