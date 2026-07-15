using Autodesk.Revit.UI;
using VCRevitRibbonUtil;   
using DAN_Plugin;
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

            /*.CreateButton<ScheduleCreate>("Создание спецификации", "Создание " +
            "спецификации",
              btn => btn
              .SetLargeImage(Resources.new_table_32)
              .SetSmallImage(Resources.new_table_16)
              .SetLongDescription("Создает спецификацию по вписанной марке конструкции. Можно выбрать какой тип спецификации требуется с помощью кнопок выбора. " +
              "У спецификации автоматически будет создан фильтр по выбранной марке. ")
              )*/

             /*.CreateSeparator()

             .CreateButton<ClearParameterInSchedule>("Очистка параметров", "Очистка параметра",
              btn => btn
              .SetLargeImage(Resources.eraser_32)
              .SetSmallImage(Resources.eraser_16)
              .SetLongDescription("У выделенных элементов очищает параметр BI_позиция")
              )*/


            .CreateSeparator()

              .CreateButton<CreateRebarAnnotation>("Аннотация доп. арматуры плит", "Аннотация доп.арм",
              btn => btn
              .SetLargeImage(Resources.slab_32)
              .SetSmallImage(Resources.slab_16)
              .SetLongDescription("Создает аннотацию для дополнительной арматуры плит по центру")
              )


            /*.CreateSeparator()

              .CreateButton<AssemblytoElementMark>("Марка из сборки", "Марка из сборки",
              btn => btn
              .SetLargeImage(Resources.right_arrow_32)
              .SetSmallImage(Resources.right_arrow_16)
              .SetLongDescription($"Передает из сборки параметр <Комментарии> в параметр <Марка> конструкций внутри сборки")
              )*/

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

              /*.CreateSeparator()

              .CreateButton<StairLandingExitsCommand>("Выпуски из площадки", "Выпуски\nплощадки",
              btn => btn
              .SetLargeImage(Resources.pencil_32)
              .SetSmallImage(Resources.pencil_16)
              .SetLongDescription("Создаёт выпуски (форма)11 из основной арматуры лестничной площадки в стены. " +
              "Выберите сборку — команда найдёт арматуру по BI_марка_конструкции = комментарий сборки, " +
              "фильтр BI_фильтр_арматуры = Армирование основное, и создаст стержни BI_A=1000мм, BI_B=1000мм.")
              )

              .CreateSeparator();*/




            return Result.Succeeded;
        }
        public Result OnShutdown(UIControlledApplication a)
        {
            return Result.Succeeded;
        }
    }
}
