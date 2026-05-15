using System;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace DAN_Plugin
{
    [Transaction(TransactionMode.Manual)]
    public class CreateViewBreak : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            Document doc = commandData.Application.ActiveUIDocument.Document;
            ViewSection section = doc.ActiveView as ViewSection;

            if (section == null)
            {
                TaskDialog.Show("Ошибка", "Активный вид должен быть разрезом.");
                return Result.Failed;
            }

            var mgr = section.GetCropRegionShapeManager();

            if (!mgr.CanBeSplit)
            {
                TaskDialog.Show("Ошибка", "Данный вид не поддерживает разрыв.");
                return Result.Failed;
            }

            // Границы вида в локальных координатах
            double regionMin = mgr.GetSplitRegionMinimum(0);
            double regionMax = mgr.GetSplitRegionMaximum(0);
            double regionHeight = regionMax - regionMin;

            // Мировые Z-координаты низа и верха вида — берём .Z напрямую
            BoundingBoxXYZ cropBox = section.CropBox;
            Transform t = cropBox.Transform;

            XYZ cropMin = t.OfPoint(cropBox.Min);
            XYZ cropMax = t.OfPoint(cropBox.Max);

            double worldBottomZ = Math.Min(cropMin.Z, cropMax.Z);
            double worldTopZ = Math.Max(cropMin.Z, cropMax.Z);
            double worldHeight = worldTopZ - worldBottomZ;

            // Мировые Z высот разрыва
            // breakWorldBottom — верхняя граница нижнего региона
            // breakWorldTop    — нижняя граница верхнего региона
            double breakWorldBottom = UnitUtils.ConvertToInternalUnits(28500, UnitTypeId.Millimeters);
            double breakWorldTop = UnitUtils.ConvertToInternalUnits(50400, UnitTypeId.Millimeters);

            // Переводим мировые Z в локальные координаты вида
            double breakLocalBottom = regionMin + (breakWorldBottom - worldBottomZ) / worldHeight * regionHeight;
            double breakLocalTop = regionMin + (breakWorldTop - worldBottomZ) / worldHeight * regionHeight;

            if (breakLocalBottom <= regionMin || breakLocalTop >= regionMax ||
                breakLocalBottom >= breakLocalTop)
            {
                TaskDialog.Show("Ошибка", "Высоты разрыва выходят за границы вида.");
                return Result.Failed;
            }

            using (Transaction tx = new Transaction(doc, "Создать разрыв вида"))
            {
                tx.Start();
                try
                {
                    mgr.SplitRegionVertically(0, breakLocalBottom, breakLocalTop);
                    tx.Commit();
                    TaskDialog.Show("Готово", "Разрыв вида создан:\n28500 мм → 50400 мм");
                }
                catch (Exception ex)
                {
                    tx.RollBack();
                    TaskDialog.Show("Ошибка", ex.Message);
                    return Result.Failed;
                }
            }

            return Result.Succeeded;
        }
    }
}