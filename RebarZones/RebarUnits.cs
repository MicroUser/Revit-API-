using Autodesk.Revit.DB;

namespace LiraToRevit.Rebar
{
    /// <summary>Единицы: модель Revit во внутренних футах, весь расчёт в проекте — в мм.</summary>
    internal static class RebarUnits
    {
        public static double Mm(double mm) => UnitUtils.ConvertToInternalUnits(mm, UnitTypeId.Millimeters);
        public static double ToMm(double ft) => UnitUtils.ConvertFromInternalUnits(ft, UnitTypeId.Millimeters);
    }
}
