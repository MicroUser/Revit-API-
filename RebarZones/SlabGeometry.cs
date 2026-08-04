using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace LiraToRevit.Rebar
{
    /// <summary>Геометрия плиты: верх/низ и контур для подрезки анкеровки.</summary>
    public class SlabGeometry
    {
        public double TopMm, BottomMm;
        private List<CurveLoop> _loops;      // внешний контур + отверстия (все вместе, для правила чётности)
        private double _planeZ;              // отметка плоскости контуров, футы (Z верхней грани)
        private XYZ _bboxMin, _bboxMax;      // габарит плиты в плане, футы — для построения сканирующей линии

        public static SlabGeometry From(Document doc, Floor floor)
        {
            var g = new SlabGeometry();
            var bb = floor.get_BoundingBox(null);
            g.BottomMm = UnitUtils.ConvertFromInternalUnits(bb.Min.Z, UnitTypeId.Millimeters);
            g.TopMm = UnitUtils.ConvertFromInternalUnits(bb.Max.Z, UnitTypeId.Millimeters);
            g._bboxMin = bb.Min;
            g._bboxMax = bb.Max;
            g._loops = FilterLoopsForClipping(GetHorizontalLoops(floor, out g._planeZ));
            return g;
        }

        /// <summary>
        /// Обрезка анкеровки (ClipAlong) нужна только у истинного края плиты и у крупных проёмов
        /// (≥1000×1000мм) — мелкие отверстия (под инженерку и т.п.) стержень просто перекрывает,
        /// без загиба/подрезки. Внешний контур определяем как петлю с наибольшим габаритом bbox
        /// (тот же приём, что и в RebarZonesCommand.GetFloorOutline) — её оставляем всегда,
        /// остальные петли (проёмы) мельче порога хотя бы по одной стороне — убираем.
        /// </summary>
        private const double MinOpeningForClipMm = 1000.0;
        private static List<CurveLoop> FilterLoopsForClipping(List<CurveLoop> loops)
        {
            if (loops == null || loops.Count < 2) return loops;   // 0-1 петель — нечего фильтровать

            int outerIdx = 0;
            double bestArea = -1;
            var bboxes = new (double minX, double maxX, double minY, double maxY)[loops.Count];
            for (int i = 0; i < loops.Count; i++)
            {
                double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
                foreach (Curve c in loops[i])
                {
                    XYZ p0 = c.GetEndPoint(0), p1 = c.GetEndPoint(1);
                    minX = System.Math.Min(minX, System.Math.Min(p0.X, p1.X)); maxX = System.Math.Max(maxX, System.Math.Max(p0.X, p1.X));
                    minY = System.Math.Min(minY, System.Math.Min(p0.Y, p1.Y)); maxY = System.Math.Max(maxY, System.Math.Max(p0.Y, p1.Y));
                }
                bboxes[i] = (minX, maxX, minY, maxY);
                double area = (maxX - minX) * (maxY - minY);
                if (area > bestArea) { bestArea = area; outerIdx = i; }
            }

            double minOpeningFt = UnitUtils.ConvertToInternalUnits(MinOpeningForClipMm, UnitTypeId.Millimeters);
            var res = new List<CurveLoop>();
            for (int i = 0; i < loops.Count; i++)
            {
                if (i == outerIdx) { res.Add(loops[i]); continue; }
                var b = bboxes[i];
                if (b.maxX - b.minX >= minOpeningFt && b.maxY - b.minY >= minOpeningFt) res.Add(loops[i]);
            }
            return res;
        }

        /// <summary>
        /// "Изолинии" — сетка потенциальных положений доп. стержней, привязанная к сетке
        /// основной арматуры. Та обычно идёт шагом 200мм с защитным слоем 50мм, то есть её
        /// стержни стоят на 50, 250, 450… мм от края плиты — а середины пролётов между ними
        /// (удобное место под доп. стержень, не сталкивается с основной сеткой) на 150, 350,
        /// 550… мм. Строится от нижнего/левого края габарита плиты (снизу вверх, слева направо),
        /// шагом stepMm, пока не выйдет за противоположный край. Координаты — в мм.
        /// </summary>
        public (List<double> Xs, List<double> Ys) BuildIsolines(double stepMm = 200.0, double firstOffsetMm = 150.0)
        {
            double stepFt = UnitUtils.ConvertToInternalUnits(stepMm, UnitTypeId.Millimeters);
            double firstFt = UnitUtils.ConvertToInternalUnits(firstOffsetMm, UnitTypeId.Millimeters);
            const double tolFt = 1e-6;

            var xs = new List<double>();
            for (double x = _bboxMin.X + firstFt; x < _bboxMax.X - tolFt; x += stepFt)
                xs.Add(System.Math.Round(UnitUtils.ConvertFromInternalUnits(x, UnitTypeId.Millimeters), 3));

            var ys = new List<double>();
            for (double y = _bboxMin.Y + firstFt; y < _bboxMax.Y - tolFt; y += stepFt)
                ys.Add(System.Math.Round(UnitUtils.ConvertFromInternalUnits(y, UnitTypeId.Millimeters), 3));

            return (xs, ys);
        }

        /// <summary>
        /// Ближайшая изолиния к заданной поперечной (across) координате, мм. barDir — направление
        /// СТЕРЖНЕЙ (не поперёк): при стержнях вдоль X поперёк идёт Y — берём Ys, и наоборот.
        ///
        /// Границы зон (band.Across1/2) приходят из JS-редактора уже привязанными к изолиниям,
        /// но это только КЛИЕНТСКАЯ копия того же расчёта — при объединении зон, зонах у краёв
        /// отверстий и т.п. они могут разойтись с исходным списком на несколько/десятки мм.
        /// Пересчитываем здесь, из ТОГО ЖЕ SlabGeometry, которым эти изолинии и были посчитаны —
        /// так соседние зоны с близкими (но не идеально совпадающими) границами гарантированно
        /// притягиваются к ОДНОЙ и той же изолинии, а не к двум разным, и стык между ними не рвёт
        /// сетку 200 мм.
        /// </summary>
        public double NearestIsoline(double valueMm, Dir barDir)
        {
            var (xs, ys) = BuildIsolines();
            var list = barDir == Dir.X ? ys : xs;
            if (list.Count == 0) return valueMm;

            double best = list[0], bestDist = System.Math.Abs(best - valueMm);
            foreach (var v in list)
            {
                double d = System.Math.Abs(v - valueMm);
                if (d < bestDist) { bestDist = d; best = v; }
            }
            return best;
        }

        /// <summary>
        /// Направление ближайшего к точке отрезка контура плиты (внешний контур + отверстия),
        /// нормализованное. Используется для сравнения ориентации пилона/колонны с гранью плиты
        /// (см. SupportDetector.HasParallelSupport). Null, если контур не построен или ближайший
        /// сегмент — дуга (плиты в этом проекте ортогональны, дуговые кромки не ожидаются).
        /// </summary>
        public XYZ NearestBoundaryTangent(XYZ pointFt)
        {
            if (_loops == null || _loops.Count == 0) return null;

            Curve best = null;
            double bestDist = double.MaxValue;
            foreach (var loop in _loops)
                foreach (var curve in loop)
                {
                    IntersectionResult r = curve.Project(pointFt);
                    if (r == null) continue;
                    if (r.Distance < bestDist) { bestDist = r.Distance; best = curve; }
                }

            if (!(best is Line line)) return null;
            XYZ dir = line.GetEndPoint(1) - line.GetEndPoint(0);
            return dir.GetLength() < 1e-9 ? null : dir.Normalize();
        }

        private static List<CurveLoop> GetHorizontalLoops(Floor floor, out double planeZ)
        {
            var loops = new List<CurveLoop>();
            planeZ = 0;
            bool have = false;
            var opt = new Options { ComputeReferences = false, DetailLevel = ViewDetailLevel.Coarse };
            foreach (var go in floor.get_Geometry(opt))
                if (go is Solid s && s.Volume > 0)
                    foreach (Autodesk.Revit.DB.Face f in s.Faces)
                        if (f is PlanarFace pf && pf.FaceNormal.IsAlmostEqualTo(XYZ.BasisZ))
                        {
                            if (!have) { planeZ = pf.Origin.Z; have = true; }
                            loops.AddRange(pf.GetEdgesAsCurveLoops());
                        }
            return loops;
        }

        /// <summary>
        /// Подрезает конец стержня по кромке плиты и отверстиям:
        /// конец не ближе EndOffset к границе. Возвращает флаги подрезки.
        /// </summary>
        public void ClipAlong(Dir dir, ref double p1, ref double p2, double acrossMm,
                              double endOffsetMm, out bool clipped1, out bool clipped2)
        {
            clipped1 = clipped2 = false;
            if (_loops == null || _loops.Count == 0) return;

            double acrossFt = UnitUtils.ConvertToInternalUnits(acrossMm, UnitTypeId.Millimeters);
            const double margin = 1.0;   // фут — заведомо за пределами габарита плиты
            Line scan = dir == Dir.X
                ? Line.CreateBound(new XYZ(_bboxMin.X - margin, acrossFt, _planeZ), new XYZ(_bboxMax.X + margin, acrossFt, _planeZ))
                : Line.CreateBound(new XYZ(acrossFt, _bboxMin.Y - margin, _planeZ), new XYZ(acrossFt, _bboxMax.Y + margin, _planeZ));

            // координаты пересечений сканирующей линии с рёбрами контуров (внешний + отверстия), мм вдоль стержня
            var xs = new List<double>();
            foreach (var loop in _loops)
                foreach (var curve in loop)
                {
                    IntersectionResultArray results;
                    curve.Intersect(scan, out results);
                    if (results == null) continue;
                    foreach (IntersectionResult r in results)
                    {
                        double along = dir == Dir.X ? r.XYZPoint.X : r.XYZPoint.Y;
                        xs.Add(System.Math.Round(UnitUtils.ConvertFromInternalUnits(along, UnitTypeId.Millimeters), 3));
                    }
                }
            if (xs.Count < 2) return;                 // контур не пересечён — подрезать нечем
            xs.Sort();

            // правило чётности: интервалы (xs[0],xs[1]), (xs[2],xs[3])… — внутри плиты
            double midMm = (p1 + p2) / 2.0;
            double? spanLo = null, spanHi = null;
            for (int k = 0; k + 1 < xs.Count; k += 2)
                if (midMm >= xs[k] - 1e-6 && midMm <= xs[k + 1] + 1e-6) { spanLo = xs[k]; spanHi = xs[k + 1]; break; }
            if (spanLo == null) return;                // середина полосы вне плиты — подрезка не определена

            double lo = spanLo.Value + endOffsetMm, hi = spanHi.Value - endOffsetMm;
            if (lo > hi) { lo = hi = (spanLo.Value + spanHi.Value) / 2.0; }   // зона у́же двойного отступа — вырождаем, не переворачиваем стержень

            double newP1 = System.Math.Max(p1, lo), newP2 = System.Math.Min(p2, hi);
            clipped1 = newP1 > p1 + 1e-6;
            clipped2 = newP2 < p2 - 1e-6;
            p1 = newP1;
            p2 = newP2;
        }
    }
}
