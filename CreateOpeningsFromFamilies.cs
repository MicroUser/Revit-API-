using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace DAN_Plugin
{
    /// <summary>
    /// Проходит по ВСЕМ дверям и окнам, размещённым в прямых стенах,
    /// считывает их габарит и позицию, удаляет семейства и создаёт на их месте
    /// постоянные прямоугольные проёмы через Document.Create.NewOpening(wall, pt1, pt2).
    /// В отличие от правки Sketch-профиля стены, этот метод не требует, чтобы у стены
    /// уже был профиль (обычные, никогда не редактировавшиеся через "Изменить профиль"
    /// стены имеют Wall.SketchId == InvalidElementId и для них правка Sketch невозможна).
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class CreateOpeningsFromFamilies : IExternalCommand
    {
        // ---- Настройки ----
        private const bool DELETE_FAMILIES = true;      // удалять исходные двери/окна
        private const bool USE_ROUGH_DIMENSIONS = true; // сначала пытаться взять «черновой» размер проёма
        private const double EPS = 1e-6;

        public Result Execute(ExternalCommandData cmdData, ref string message, ElementSet elements)
        {
            Document doc = cmdData.Application.ActiveUIDocument.Document;

            // 1. Все двери и окна в стенах (без выбора — по всей модели)
            var cats = new List<BuiltInCategory>
            {
                BuiltInCategory.OST_Doors,
                BuiltInCategory.OST_Windows
            };
            var filter = new ElementMulticategoryFilter(cats);

            var instances = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilyInstance))
                .WherePasses(filter)
                .Cast<FamilyInstance>()
                .Where(fi => fi.Host is Wall)
                .ToList();

            if (instances.Count == 0)
            {
                message = "В модели не найдено дверей или окон, размещённых в стенах.";
                return Result.Cancelled;
            }

            // 2. Считываем геометрию ДО любых изменений
            var data = new List<OpeningData>();
            var skipped = new List<string>();

            foreach (var fi in instances)
            {
                try
                {
                    var od = ExtractOpening(doc, fi);
                    if (od != null) data.Add(od);
                    else skipped.Add($"{fi.Id.IntValue()} — не удалось получить геометрию (кривая стена / нет размеров)");
                }
                catch (Exception ex)
                {
                    skipped.Add($"{fi.Id.IntValue()} — {ex.Message}");
                }
            }

            if (data.Count == 0)
            {
                message = "Не удалось получить геометрию ни одного проёма.";
                return Result.Failed;
            }

            int done = 0;

            using (var t = new Transaction(doc, "Проёмы из семейств"))
            {
                t.Start();

                // 3. Удаляем семейства
                if (DELETE_FAMILIES)
                {
                    foreach (var od in data)
                        if (doc.GetElement(od.InstanceId) != null)
                            doc.Delete(od.InstanceId);
                }

                // 4. Создаём прямоугольные проёмы. Каждый — в своей SubTransaction,
                //    чтобы ошибка на одном проёме не откатывала остальные.
                foreach (var od in data)
                {
                    Wall wall = doc.GetElement(od.WallId) as Wall;
                    if (wall == null)
                    {
                        skipped.Add($"Стена {od.WallId.IntValue()} не найдена (проём {od.InstanceId.IntValue()})");
                        continue;
                    }

                    using (var st = new SubTransaction(doc))
                    {
                        st.Start();
                        try
                        {
                            // Corners: bl, br, tr, tl — bl и tr являются противоположными
                            // углами прямоугольника, ровно что требует NewOpening.
                            doc.Create.NewOpening(wall, od.Corners[0], od.Corners[2]);
                            st.Commit();
                            done++;
                        }
                        catch (Exception ex)
                        {
                            st.RollBack();
                            skipped.Add($"Стена {wall.Id.IntValue()} (проём {od.InstanceId.IntValue()}) — не удалось создать проём: {ex.Message}");
                        }
                    }
                }

                t.Commit();
            }

            // 5. Отчёт
            string report = $"Создано проёмов: {done} из {data.Count}.";
            if (skipped.Count > 0)
                report += $"\n\nПропущено ({skipped.Count}):\n- " +
                          string.Join("\n- ", skipped.Take(25)) +
                          (skipped.Count > 25 ? "\n- …" : "");

            TaskDialog.Show("Проёмы из семейств", report);
            return Result.Succeeded;
        }

        /// <summary>Считывает габарит и позицию проёма из семейства (только прямые стены).</summary>
        private OpeningData ExtractOpening(Document doc, FamilyInstance fi)
        {
            if (!(fi.Host is Wall wall)) return null;

            // расположение стены должно быть прямой линией
            if (!((wall.Location as LocationCurve)?.Curve is Line wallLine)) return null;

            if (!(fi.Location is LocationPoint lp)) return null;
            XYZ center = lp.Point;

            double width = GetDimension(fi, true);
            double height = GetDimension(fi, false);
            if (width <= EPS || height <= EPS) return null;

            // отметка низа проёма
            double sill = 0.0;
            var sillParam = fi.get_Parameter(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM);
            if (sillParam != null && sillParam.HasValue) sill = sillParam.AsDouble();

            Level level = doc.GetElement(fi.LevelId) as Level;
            double baseZ = (level?.Elevation ?? center.Z) + sill;
            double topZ = baseZ + height;

            // прямоугольник в мировых координатах: центр ± половина ширины вдоль оси стены
            XYZ half = wallLine.Direction.Normalize() * (width / 2.0);

            XYZ bl = new XYZ(center.X - half.X, center.Y - half.Y, baseZ);
            XYZ br = new XYZ(center.X + half.X, center.Y + half.Y, baseZ);
            XYZ tr = new XYZ(center.X + half.X, center.Y + half.Y, topZ);
            XYZ tl = new XYZ(center.X - half.X, center.Y - half.Y, topZ);

            return new OpeningData
            {
                InstanceId = fi.Id,
                WallId = wall.Id,
                Corners = new[] { bl, br, tr, tl }
            };
        }

        /// <summary>
        /// Возвращает ширину или высоту проёма во внутренних единицах (футы).
        /// Порядок приоритета: 1) «черновой» (структурный) размер проёма — то, что реально
        /// нужно вырезать в стене; 2) встроенный параметр Width/Height (FAMILY_*_PARAM);
        /// 3) параметр с обычным именем «Ширина»/«Высота» (свой shared-параметр семейства,
        /// не обязательно привязанный к builtin-у). На каждом шаге сперва проверяется
        /// параметр ЭКЗЕМПЛЯРА, затем — параметр ТИПА (у части семейств размеры сделаны
        /// параметрами экземпляра, а не типа).
        /// </summary>
        private double GetDimension(FamilyInstance fi, bool isWidth)
        {
            if (USE_ROUGH_DIMENSIONS)
            {
                var roughNames = isWidth
                    ? new[] { "Rough Width", "Примерная ширина", "Ширина проёма", "Ширина проема" }
                    : new[] { "Rough Height", "Примерная высота", "Высота проёма", "Высота проема" };

                double rv = GetByNames(fi, roughNames);
                if (rv > EPS) return rv;
            }

            var bip = isWidth ? BuiltInParameter.FAMILY_WIDTH_PARAM
                              : BuiltInParameter.FAMILY_HEIGHT_PARAM;
            var np = fi.get_Parameter(bip) ?? fi.Symbol.get_Parameter(bip);
            if (np != null && np.HasValue)
            {
                double v = np.AsDouble();
                if (v > EPS) return v;
            }

            var plainNames = isWidth ? new[] { "Ширина", "Width" } : new[] { "Высота", "Height" };
            return GetByNames(fi, plainNames);
        }

        /// <summary>Ищет первый ненулевой double-параметр из списка имён: сначала у экземпляра, потом у типа.</summary>
        private double GetByNames(FamilyInstance fi, string[] names)
        {
            foreach (var n in names)
            {
                var p = fi.LookupParameter(n) ?? fi.Symbol.LookupParameter(n);
                if (p != null && p.HasValue && p.StorageType == StorageType.Double)
                {
                    double v = p.AsDouble();
                    if (v > EPS) return v;
                }
            }
            return 0.0;
        }

        // ---- Вспомогательные типы ----

        private class OpeningData
        {
            public ElementId InstanceId;
            public ElementId WallId;
            public XYZ[] Corners; // bl, br, tr, tl
        }
    }
}
