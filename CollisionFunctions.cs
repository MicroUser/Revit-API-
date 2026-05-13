using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace DAN_Plugin
{
    public static class CollisionFunctions
    {
        // =====================================================
        // Solid элемента (упрощённый, стабильный)
        // =====================================================

        public static Solid GetElementSolid(Element element, Options options)
        {
            GeometryElement geo = element.get_Geometry(options);
            if (geo == null) return null;

            foreach (GeometryObject obj in geo)
            {
                if (obj is Solid s && s.Volume > 0)
                    return s;

                if (obj is GeometryInstance gi)
                {
                    GeometryElement instGeo = gi.GetInstanceGeometry();

                    foreach (GeometryObject io in instGeo)
                    {
                        if (io is Solid isolid && isolid.Volume > 0)
                            return isolid;
                    }
                }
            }

            return null;
        }

        // =====================================================
        // BoundingBox overlap (основной фильтр)
        // =====================================================

        public static bool BoundingBoxesIntersect(
            BoundingBoxXYZ a,
            BoundingBoxXYZ b)
        {
            if (a == null || b == null)
                return false;

            return (a.Min.X <= b.Max.X && a.Max.X >= b.Min.X) &&
                   (a.Min.Y <= b.Max.Y && a.Max.Y >= b.Min.Y) &&
                   (a.Min.Z <= b.Max.Z && a.Max.Z >= b.Min.Z);
        }

        // =====================================================
        // Дедупликация по расстоянию между точками
        // =====================================================

        public static bool IsDuplicatePoint(
            XYZ newPoint,
            IEnumerable<XYZ> existingPoints,
            double minDistance = 0.5) // в футах (~15 см)
        {
            return existingPoints.Any(p =>
                p.DistanceTo(newPoint) < minDistance);
        }
    }
}