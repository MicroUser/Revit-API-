/* На основе VCRevitRibbonUtil (Victor Chekalin, https://github.com/chekalin-v/VCRevitRibbonUtil) — без изменений. */

using System.Collections.Generic;
using Autodesk.Revit.UI;

namespace VCRevitRibbonUtil
{
    public class Tab
    {
        private readonly Ribbon _ribbon;
        private readonly Autodesk.Revit.UI.Tab? _systemTab;
        private readonly string _name;

        public Tab(Ribbon ribbon, string name)
        {
            _ribbon = ribbon;
            _name = name;
        }

        public Tab(Ribbon ribbon, Autodesk.Revit.UI.Tab systemTab)
        {
            _ribbon = ribbon;
            _systemTab = systemTab;
        }

        internal Ribbon Ribbon
        {
            get { return _ribbon; }
        }

        public Panel Panel(string panelTitle)
        {
            List<RibbonPanel> panels;
            if (_systemTab == null)
            {
                panels = _ribbon.Application.GetRibbonPanels(_name);
            }
            else
            {
                panels = _ribbon.Application.GetRibbonPanels(_systemTab.Value);
            }
            foreach (var panel in panels)
            {
                if (panel.Name.Equals(panelTitle))
                {
                    panel.AddSeparator();
                    return new Panel(this, panel);
                }
            }

            RibbonPanel ribbonPanel;
            if (_systemTab == null)
                ribbonPanel = _ribbon.Application.CreateRibbonPanel(_name, panelTitle);
            else
                ribbonPanel = _ribbon.Application.CreateRibbonPanel(_systemTab.Value, panelTitle);

            return new Panel(this, ribbonPanel);
        }
    }
}
