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
              .Tab("RAS DVA").Panel("Плагины")

              .CreateButton<ScheduleCreate>("Спецификации", "Создать спецификации для конструкций",
              b => b
              .SetLargeImage(Resources.colli32)
              .SetSmallImage(Resources.colli16)
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
