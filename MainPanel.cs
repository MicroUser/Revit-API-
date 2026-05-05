using Autodesk.Revit.UI;
using Пробник.Properties;
using VCRevitRibbonUtil;   


namespace Пробник
{


    internal class MainPanel : IExternalApplication
    {
        public Result OnStartup(UIControlledApplication a)
        {

            Ribbon.GetApplicationRibbon(a)
              .Tab("DAN").Panel("КЖ_Спецификации")

              .CreateButton<ScheduleMarking>("Спецификации", "Заполнение спецификации каркасов",
              b => b
              .SetLargeImage(Resources.cells)
              .SetSmallImage(Resources.cells
)
              .SetLongDescription("Создание спецификаций ")
              );



            return Result.Succeeded;
        }
        public Result OnShutdown(UIControlledApplication a)
        {
            return Result.Succeeded;
        }
    }
}
