using System.Reflection;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace DAN_Plugin
{
    [Transaction(TransactionMode.Manual)]
    public class DiagnoseSplitOffset : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            ViewSection section = commandData.Application.ActiveUIDocument.Document.ActiveView as ViewSection;
            if (section == null) { TaskDialog.Show("Ошибка", "Нужен разрез."); return Result.Failed; }

            var mgr = section.GetCropRegionShapeManager();

            string report = $"Split: {mgr.Split}\n";
            report += $"NumberOfSplitRegions: {mgr.NumberOfSplitRegions}\n\n";

            for (int i = 0; i < mgr.NumberOfSplitRegions; i++)
            {
                var min = mgr.GetSplitRegionMinimum(i);
                var max = mgr.GetSplitRegionMaximum(i);
                var offset = mgr.GetSplitRegionOffset(i);

                report += $"Регион {i}:\n";
                report += $"  Min:    {min}\n";
                report += $"  Max:    {max}\n";
                report += $"  Offset: {offset}\n\n";
            }

            // Ищем set-методы для offset
            var setMethods = mgr.GetType()
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.Name.ToLower().Contains("offset") || m.Name.ToLower().Contains("split"))
                .Select(m => $"{m.ReturnType.Name} {m.Name}({string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"))})");

            report += "Методы с offset/split:\n";
            foreach (var m in setMethods)
                report += $"  {m}\n";

            if (report.Length > 4000) report = report.Substring(0, 4000) + "\n...(обрезано)";
            TaskDialog.Show("Split Offset диагностика", report);
            return Result.Succeeded;
        }
    }
}