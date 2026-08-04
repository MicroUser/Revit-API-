using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace LiraToRevit.Rebar
{
    /// <summary>
    /// Разбивка Г/П-образной зоны на прямоугольные полосы. Резы — поперёк стержней (по уровням
    /// вершин контура). Чистая геометрия — только List&lt;XYZ&gt;/double, без обращения к Revit API.
    /// </summary>
    public static class BandSplitter
    {
        public static List<Band> SplitToBands(ZoneDef z)
        {
            bool alongX = z.Dir == Dir.X;
            // координата поперёк стержней: для стержней вдоль X режем по Y, и наоборот
            var cuts = z.Polygon.Select(p => alongX ? p.Y : p.X)
                                .Select(v => Math.Round(v, 4))
                                .Distinct().OrderBy(v => v).ToList();

            var bands = new List<Band>();
            for (int i = 0; i < cuts.Count - 1; i++)
            {
                double mid = (cuts[i] + cuts[i + 1]) / 2.0;
                foreach (var (a, b) in Crossings(z.Polygon, mid, alongX))
                    bands.Add(new Band
                    {
                        Along1 = a,                 // начало вдоль стержня
                        Along2 = b,                 // конец вдоль стержня
                        Across1 = cuts[i],          // низ полосы поперёк
                        Across2 = cuts[i + 1]
                    });
            }
            return bands;
        }

        /// <summary>Пересечения контура горизонталью (для along X) или вертикалью (для along Y) — парами.</summary>
        private static IEnumerable<(double, double)> Crossings(List<XYZ> poly, double mid, bool alongX)
        {
            var xs = new List<double>();
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            {
                double v1 = alongX ? poly[j].Y : poly[j].X;
                double v2 = alongX ? poly[i].Y : poly[i].X;
                if ((v1 > mid) == (v2 > mid)) continue;
                double u1 = alongX ? poly[j].X : poly[j].Y;
                double u2 = alongX ? poly[i].X : poly[i].Y;
                xs.Add(u1 + (mid - v1) / (v2 - v1) * (u2 - u1));
            }
            xs.Sort();
            for (int k = 0; k + 1 < xs.Count; k += 2) yield return (xs[k], xs[k + 1]);
        }
    }
}
