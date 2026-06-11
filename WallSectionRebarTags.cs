using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;

namespace DAN_Plugin
{
    /// <summary>
    /// Создаёт марки несущей арматуры (OST_Rebar) на горизонтальном разрезе стены.
    /// Для каждой пары стержней одного диаметра с разными BI_позиция:
    ///   меньший pos → тип "Позиция_(_)_без полки";
    ///   больший pos → тип "Позиция".
    /// Марки размещаются парами с выноской вниз (в сторону maxU) под углом ~20°.
    /// </summary>
    internal static class WallSectionRebarTagger
    {
        public static void Run(Document doc, ViewSection sectionView, Wall wall, XYZ viewRight)
        {
            if (sectionView == null || wall == null) return;

            LocationCurve lc = wall.Location as LocationCurve;
            Line wallLine = lc?.Curve as Line;
            if (wallLine == null) return;

            // Ориентация вида (та же логика, что в CreateWallSection)
            XYZ wallDir = wallLine.Direction.Normalize();
            XYZ viewRightH = new XYZ(viewRight.X, viewRight.Y, 0);
            if (viewRightH.GetLength() > 1e-9 && wallDir.DotProduct(viewRightH.Normalize()) < 0)
                wallDir = wallDir.Negate();
            XYZ upDir = XYZ.BasisZ.Negate().CrossProduct(wallDir).Normalize();
            double cutZ = sectionView.Origin.Z;

            // Собираем всю несущую арматуру, видимую в разрезе
            var allRebar = new FilteredElementCollector(doc, sectionView.Id)
                .OfCategory(BuiltInCategory.OST_Rebar)
                .WhereElementIsNotElementType()
                .ToList();

            if (!allRebar.Any()) return;

            // Данные каждого стержня: позиция, диаметр, координаты в плоскости вида
            var items = new List<(Element elem, int pos, double diam, double wProj, double uProj)>();
            foreach (Element rb in allRebar)
            {
                Parameter posP = rb.LookupParameter("BI_позиция");
                if (posP == null) continue;

                int pos;
                if (posP.StorageType == StorageType.Integer)
                    pos = posP.AsInteger();
                else if (!int.TryParse(posP.AsString(), out pos))
                    continue;

                double diam = 0;
                foreach (string pn in new[] { "Диаметр стержня", "Диаметр", "Bar Diameter" })
                {
                    Parameter dp = rb.LookupParameter(pn)
                        ?? doc.GetElement(rb.GetTypeId())?.LookupParameter(pn);
                    if (dp != null && dp.StorageType == StorageType.Double)
                    { diam = dp.AsDouble(); break; }
                }

                BoundingBoxXYZ bb = rb.get_BoundingBox(sectionView) ?? rb.get_BoundingBox(null);
                if (bb == null) continue;
                XYZ c = (bb.Min + bb.Max) / 2.0;
                items.Add((rb, pos, diam, c.DotProduct(wallDir), c.DotProduct(upDir)));
            }

            if (!items.Any()) return;

            // Типы марок
            FamilySymbol tagNoShelf = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .OfCategory(BuiltInCategory.OST_RebarTags)
                .Cast<FamilySymbol>()
                .FirstOrDefault(fs => fs.Name.Equals("Позиция_(_)_без полки", StringComparison.OrdinalIgnoreCase));

            FamilySymbol tagStd = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .OfCategory(BuiltInCategory.OST_RebarTags)
                .Cast<FamilySymbol>()
                .FirstOrDefault(fs => fs.Name.Equals("Позиция", StringComparison.OrdinalIgnoreCase));

            if (tagNoShelf == null || tagStd == null)
            {
                TaskDialog.Show("Предупреждение (марки арматуры)",
                    tagNoShelf == null ? "Тип марки \"Позиция_(_)_без полки\" не найден."
                                       : "Тип марки \"Позиция\" не найден.");
                return;
            }
            if (!tagNoShelf.IsActive) tagNoShelf.Activate();
            if (!tagStd.IsActive)     tagStd.Activate();

            double wallMaxU = items.Max(r => r.uProj);

            double angle  = 20.0 * Math.PI / 180.0;
            double leader = UnitUtils.ConvertToInternalUnits(500, UnitTypeId.Millimeters);
            double gap    = UnitUtils.ConvertToInternalUnits(80,  UnitTypeId.Millimeters);
            double posTol = UnitUtils.ConvertToInternalUnits(80,  UnitTypeId.Millimeters);

            XYZ MakePt(double w, double u) =>
                wallDir.Multiply(w) + upDir.Multiply(u) + XYZ.BasisZ.Multiply(cutZ);

            foreach (var diamGroup in items.GroupBy(r => Math.Round(r.diam, 4)))
            {
                var byPos = diamGroup.GroupBy(r => r.pos).OrderBy(g => g.Key).ToList();
                if (byPos.Count < 2) continue;

                var grpMin = byPos[0].OrderBy(r => r.wProj).ToList(); // меньший pos → "без полки"
                var grpMax = byPos[1].OrderBy(r => r.wProj).ToList(); // больший pos → "Позиция"

                var usedMax = new HashSet<int>();
                foreach (var rb1 in grpMin)
                {
                    int bestIdx = -1;
                    double bestDist = double.MaxValue;
                    for (int i = 0; i < grpMax.Count; i++)
                    {
                        if (usedMax.Contains(i)) continue;
                        double d = Math.Abs(grpMax[i].wProj - rb1.wProj);
                        if (d < bestDist) { bestDist = d; bestIdx = i; }
                    }
                    if (bestIdx < 0 || bestDist > posTol * 4) continue;
                    usedMax.Add(bestIdx);
                    var rb2 = grpMax[bestIdx];

                    double midW  = (rb1.wProj + rb2.wProj) / 2.0;
                    double tagU1 = wallMaxU + leader * Math.Cos(angle);
                    double tagU2 = tagU1 + gap;
                    double tagW  = midW + leader * Math.Sin(angle);

                    XYZ pt1 = MakePt(tagW, tagU1); // "без полки" — ближе к стене
                    XYZ pt2 = MakePt(tagW, tagU2); // "Позиция"   — дальше

                    try
                    {
                        IndependentTag.Create(
                            doc, sectionView.Id, tagNoShelf.Id,
                            new Reference(rb1.elem), true, TagOrientation.Horizontal, pt1);

                        IndependentTag.Create(
                            doc, sectionView.Id, tagStd.Id,
                            new Reference(rb2.elem), true, TagOrientation.Horizontal, pt2);
                    }
                    catch { }
                }
            }
        }
    }
}
