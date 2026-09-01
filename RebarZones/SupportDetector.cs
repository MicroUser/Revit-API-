using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace LiraToRevit.Rebar
{
    /// <summary>Поиск пилонов/колонн у края плиты (по "Категория именования" сборки) — решает,
    /// нужна ли П-образная (а не Г-образная) форма загиба у конкретного места.</summary>
    public class SupportDetector
    {
        /// <summary>Пилон/колонна — сборка (AssemblyInstance) с "Марка по стандарту", содержащей
        /// "Пилон" или "Колонна". Габарит в плане + направление длинной стороны (по большей
        /// стороне габарита — сами пилоны/колонны в этом проекте ортогональны осям).</summary>
        // internal (не private) — доступ нужен диагностической команде в Debug.cs, см.
        // DiagnosePylonOrientation. Сама структура/логика тут не меняется.
        internal class SupportInfo
        {
            public XYZ BBoxMin, BBoxMax;  // футы, план
            public bool LongAxisIsX;      // true — длинная сторона вдоль X, false — вдоль Y
            // Элемент состава (стена/колонна), из которого взят этот габарит — null у запасного
            // варианта (общий bbox сборки целиком, без единого элемента). Сама логика решения
            // Г/П (HasParallelSupport) от него не зависит, использует только BBoxMin/Max/
            // LongAxisIsX — нужен только визуальному фону в редакторе (RebarZonesCommand.GetSupports),
            // чтобы вместо осевого прямоугольника габарита показать НАСТОЯЩИЙ контур стены
            // (в т.ч. дуговой — см. WallFootprint), а не его bbox.
            public ElementId MemberId;
        }

        /// <summary>Допуск по высоте при поиске опоры для конкретной плиты, мм. Одна сборка может
        /// содержать стены/колонны сразу нескольких этажей (стоящие друг над другом элементы
        /// одной сборки) — без фильтра по Z в качестве "опоры" находилась бы стена ЛЮБОГО этажа
        /// с подходящим планом, а не именно та, что стоит у края ЭТОЙ плиты.</summary>
        // internal (не private) — используется и диагностической командой в Debug.cs.
        internal const double SupportZToleranceMm = 300.0;

        private readonly PlacementSettings _s;
        private readonly List<SupportInfo> _supports;

        public SupportDetector(Document doc, PlacementSettings settings)
        {
            _s = settings;
            _supports = ResolveSupports(doc, settings);
        }

        /// <summary>
        /// Текст значения параметра "Идентификации" сборки (Марка по стандарту / Категория
        /// именования) — на практике StorageType этих параметров оказался ElementId, а не текст
        /// напрямую (AsString() у них всегда пустой). "Марка по стандарту" ссылается на строку
        /// таблицы-ключа Assembly Naming (обычный Element, берём Name). А "Категория именования"
        /// ссылается на КАТЕГОРИЮ (напр. OST_Walls) — doc.GetElement(id) для id категории всегда
        /// возвращает null, поэтому сначала пробуем Category.GetCategory.
        /// </summary>
        // internal (не private) — доступ нужен диагностической команде в Debug.cs, см.
        // DiagnosePylonOrientation.
        internal static string SupportParamText(Document doc, Parameter p)
        {
            if (p == null) return "";
            if (p.StorageType == StorageType.ElementId)
            {
                ElementId id = p.AsElementId();
                if (id == null || id == ElementId.InvalidElementId) return "";
                Category cat = Category.GetCategory(doc, id);
                if (cat != null) return cat.Name;
                return doc.GetElement(id)?.Name ?? "";
            }
            return p.AsString() ?? p.AsValueString() ?? "";
        }

        internal static List<SupportInfo> ResolveSupports(Document doc, PlacementSettings s)
        {
            var res = new List<SupportInfo>();

            foreach (AssemblyInstance a in new FilteredElementCollector(doc).OfClass(typeof(AssemblyInstance)).Cast<AssemblyInstance>())
            {
                // Категории состава сборки (автозаполняется Revit'ом при именовании) — ловит
                // любую сборку из стен/несущих колонн.
                string namingCats = SupportParamText(doc, a.LookupParameter(s.NamingCategoryParam));
                bool isWallCat = namingCats.IndexOf(s.NamingCategoryWalls, StringComparison.OrdinalIgnoreCase) >= 0;
                bool isColumnCat = namingCats.IndexOf(s.NamingCategoryColumns, StringComparison.OrdinalIgnoreCase) >= 0;

                if (!isWallCat && !isColumnCat) continue;

                // Сборка может объединять НЕСКОЛЬКО стен/колонн разной ориентации (например, все
                // стены шахты лифта вокруг проёма) — общий bbox сборки тогда не отражает
                // ориентацию ни одной из них. Поэтому берём габарит и длинную сторону у КАЖДОГО
                // элемента состава отдельно (стена / несущая колонна), а не у сборки целиком.
                int memberSupports = 0;
                foreach (ElementId memberId in a.GetMemberIds())
                {
                    Element member = doc.GetElement(memberId);
                    if (member?.Category == null) continue;
                    long catId = member.Category.Id.IntValue();
                    bool memberIsWall = catId == (int)BuiltInCategory.OST_Walls;
                    bool memberIsColumn = catId == (int)BuiltInCategory.OST_StructuralColumns;
                    if (!memberIsWall && !memberIsColumn) continue;

                    BoundingBoxXYZ mbb = member.get_BoundingBox(null);
                    if (mbb == null) continue;
                    double mdx = mbb.Max.X - mbb.Min.X, mdy = mbb.Max.Y - mbb.Min.Y;
                    res.Add(new SupportInfo { BBoxMin = mbb.Min, BBoxMax = mbb.Max, LongAxisIsX = mdx >= mdy, MemberId = memberId });
                    memberSupports++;
                }
                if (memberSupports > 0) continue;

                // Запасной вариант (в составе не нашлось отдельных стен/колонн с bbox) — общий
                // bbox сборки; AssemblyInstance.get_BoundingBox(null) на практике нередко
                // возвращает null, тогда объединяем габариты всех элементов состава.
                BoundingBoxXYZ bb = a.get_BoundingBox(null);
                if (bb == null)
                {
                    foreach (ElementId memberId in a.GetMemberIds())
                    {
                        BoundingBoxXYZ mbb = doc.GetElement(memberId)?.get_BoundingBox(null);
                        if (mbb == null) continue;
                        bb = bb == null
                            ? new BoundingBoxXYZ { Min = mbb.Min, Max = mbb.Max }
                            : new BoundingBoxXYZ
                            {
                                Min = new XYZ(Math.Min(bb.Min.X, mbb.Min.X), Math.Min(bb.Min.Y, mbb.Min.Y), Math.Min(bb.Min.Z, mbb.Min.Z)),
                                Max = new XYZ(Math.Max(bb.Max.X, mbb.Max.X), Math.Max(bb.Max.Y, mbb.Max.Y), Math.Max(bb.Max.Z, mbb.Max.Z))
                            };
                    }
                }
                if (bb == null) continue;
                double dx = bb.Max.X - bb.Min.X, dy = bb.Max.Y - bb.Min.Y;
                res.Add(new SupportInfo { BBoxMin = bb.Min, BBoxMax = bb.Max, LongAxisIsX = dx >= dy });
            }

            return res;
        }

        /// <summary>
        /// Есть ли рядом с местом загиба (у кромки плиты) пилон/колонна, ориентированные
        /// ПАРАЛЛЕЛЬНО грани плиты, и попадает ли хотя бы один стержень массива в его габарит
        /// поперёк раскладки. При совпадении — весь массив (band) переключается на П-образную
        /// форму вместо Г-образной.
        /// </summary>
        public bool HasParallelSupport(SlabGeometry slab, Dir dir, double acrossStartMm, double stepMm, int count, double bendAlongMm, double zBarMm)
        {
            double tol = _s.SupportSearchToleranceMm;
            double zTol = RebarUnits.Mm(SupportZToleranceMm);
            double zBarFt = RebarUnits.Mm(zBarMm); // zBarMm — в мм, локальная отметка в точке загиба (см. RebarBuilder.ZAt), BBoxMin/Max.Z сборок — в футах

            foreach (var sup in _supports)
            {
                if (zBarFt < sup.BBoxMin.Z - zTol || zBarFt > sup.BBoxMax.Z + zTol) continue;

                double supAlongMin = RebarUnits.ToMm(dir == Dir.X ? sup.BBoxMin.X : sup.BBoxMin.Y);
                double supAlongMax = RebarUnits.ToMm(dir == Dir.X ? sup.BBoxMax.X : sup.BBoxMax.Y);
                if (bendAlongMm < supAlongMin - tol || bendAlongMm > supAlongMax + tol) continue;

                double supAcrossMin = RebarUnits.ToMm(dir == Dir.X ? sup.BBoxMin.Y : sup.BBoxMin.X);
                double supAcrossMax = RebarUnits.ToMm(dir == Dir.X ? sup.BBoxMax.Y : sup.BBoxMax.X);

                bool anyBarOver = false;
                for (int i = 0; i < count; i++)
                {
                    double acrossMm = acrossStartMm + i * stepMm;
                    if (acrossMm >= supAcrossMin - 1e-6 && acrossMm <= supAcrossMax + 1e-6) { anyBarOver = true; break; }
                }
                if (!anyBarOver) continue;

                XYZ bendPt = dir == Dir.X
                    ? new XYZ(RebarUnits.Mm(bendAlongMm), RebarUnits.Mm((supAcrossMin + supAcrossMax) / 2.0), 0)
                    : new XYZ(RebarUnits.Mm((supAcrossMin + supAcrossMax) / 2.0), RebarUnits.Mm(bendAlongMm), 0);
                XYZ edgeTangent = slab.NearestBoundaryTangent(bendPt);
                if (edgeTangent == null) continue;

                bool edgeAlongX = Math.Abs(edgeTangent.X) >= Math.Abs(edgeTangent.Y);
                if (edgeAlongX == sup.LongAxisIsX) return true;   // ориентации совпали — параллельны
            }
            return false;
        }
    }
}
