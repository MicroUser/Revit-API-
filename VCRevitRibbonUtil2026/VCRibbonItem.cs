/* На основе VCRevitRibbonUtil (Victor Chekalin, https://github.com/chekalin-v/VCRevitRibbonUtil) — без изменений. */

using Autodesk.Revit.UI;

namespace VCRevitRibbonUtil
{
    public abstract class VCRibbonItem
    {
        internal RibbonItem RibbonItem { get; set; }
        public bool IsVisible { get; set; }
    }
}
