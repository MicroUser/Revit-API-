/*
 * На основе VCRevitRibbonUtil (Victor Chekalin, https://github.com/chekalin-v/VCRevitRibbonUtil)
 *
 * Адаптировано под net8.0-windows / Revit 2026: оригинальный конструктор и метод Tab(string)
 * проверяли существование вкладки через ВНУТРЕННИЕ классы Revit (Autodesk.Windows.RibbonControl,
 * UIFramework.RevitRibbonControl) — недокументированные, часто меняются между версиями, и это
 * был единственный файл библиотеки, который на них опирался. Заменено на публичный API:
 * CreateRibbonTab бросает исключение, если вкладка с таким именем уже есть — этого достаточно.
 */

using System;
using Autodesk.Revit.UI;

namespace VCRevitRibbonUtil
{
    public class Ribbon
    {
        private readonly UIControlledApplication _application;

        public Ribbon(UIControlledApplication application)
        {
            _application = application;
        }

        public static Ribbon GetApplicationRibbon(UIControlledApplication application)
        {
            return new Ribbon(application);
        }

        internal UIControlledApplication Application
        {
            get { return _application; }
        }

        public Tab Tab(string tabTitle)
        {
            try
            {
                _application.CreateRibbonTab(tabTitle);
            }
            catch
            {
                // Вкладка с таким именем уже существует (повторный OnStartup/hot-reload) — ок.
            }

            return new Tab(this, tabTitle);
        }

        public Tab Tab(Autodesk.Revit.UI.Tab systemTab)
        {
            return new Tab(this, systemTab);
        }
    }
}
