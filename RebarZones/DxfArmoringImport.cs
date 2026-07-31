using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;

namespace LiraToRevit.Rebar
{
    // ────────────────────────────────────────────────────────────
    //  Импорт мозаики армирования из DXF (экспорт ЛИРА-САПР).
    //  Формат подтверждён разбором реального файла:
    //    layer_elements       — 3DFACE, по одному на конечный элемент (3 или 4 точки;
    //                           если p13/p23/p33 совпадает с p12/p22/p32 — треугольник).
    //                           Порядок точек 3DFACE последовательный (в отличие от SOLID).
    //    layer_result_values  — TEXT рядом с каждым элементом: значение As (см²/м),
    //                           текст — простое число, разделитель точка.
    //    layer_result_palette — SOLID для цветовой заливки (не используется — класс диаметра
    //                           считается из As на экране подтверждения) + TEXT-легенда, например:
    //                           «Режим основной. Площадь полной арматуры на 1пм по оси X у верхней грани» —
    //                           отсюда берутся Face/Dir, без ручного выбора пользователем.
    //  Единицы DXF — метры, координаты совпадают с координатами модели Revit
    //  (экспорт из связанной с Revit модели ЛИРА), поэтому пересчёт — просто в футы.
    // ────────────────────────────────────────────────────────────

    /// <summary>Одна ячейка КЭ-сетки: контур элемента + требуемое As в его центре.</summary>
    public class DxfCell
    {
        public List<XYZ> Polygon;   // 3 или 4 точки, футы (внутренние единицы Revit)
        public double X, Y;         // среднее вершин полигона, футы
        public double As;           // см²/м, 0 — если рядом не нашёлся текст со значением
        public bool HasValue;       // false — ячейка без сопоставленного As (нужна ручная проверка)
    }

    public class DxfImportResult
    {
        public List<DxfCell> Cells = new List<DxfCell>();
        public int FacesRead, TextsRead, Matched, Unmatched;
        public double MinAs, MaxAs;

        /// <summary>Грань/направление, распознанные из текста легенды. Null — легенда не найдена
        /// или не разобрана — вызывающий код должен спросить пользователя явно.</summary>
        public Face? Face;
        public Dir? Dir;
        public string LegendText;
    }

    public static class DxfArmoringReader
    {
        private const string LayerCells = "layer_elements";
        private const string LayerValues = "layer_result_values";
        private const double CoordEpsFt = 1e-6; // сравнение точек 3DFACE (p3 == p4 → треугольник)

        public static DxfImportResult Read(string path)
        {
            var faces = new List<List<XYZ>>();
            var texts = new List<(double X, double Y, double Val)>();
            Face? legendFace = null;
            Dir? legendDir = null;
            string legendText = null;

            foreach (var ent in ReadEntities(path))
            {
                if (ent.Type == "3DFACE" && ent.Layer == LayerCells)
                {
                    var poly = FacePoints(ent);
                    if (poly != null) faces.Add(poly);
                }
                else if (ent.Type == "TEXT" && ent.Layer == LayerValues)
                {
                    string raw = ent.S(1);
                    if (raw != null && double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var val))
                    {
                        // позиция подписи: alignment point (11/21), если есть — иначе insertion (10/20)
                        double tx = ent.Has(11) ? ent.D(11) : ent.D(10);
                        double ty = ent.Has(21) ? ent.D(21) : ent.D(20);
                        texts.Add((FeetX(tx), FeetX(ty), val));
                    }
                }
                else if (ent.Type == "TEXT" && legendFace == null)
                {
                    // заголовок вида «...по оси X у верхней грани» — обычно на layer_result_palette,
                    // но слой не проверяем: формат экспорта может отличаться между версиями ЛИРА.
                    var fd = TryParseLegend(ent.S(1));
                    if (fd != null) { legendFace = fd.Value.Face; legendDir = fd.Value.Dir; legendText = ent.S(1); }
                }
            }

            var res = new DxfImportResult
            {
                FacesRead = faces.Count,
                TextsRead = texts.Count,
                Face = legendFace,
                Dir = legendDir,
                LegendText = legendText
            };
            if (faces.Count == 0) return res;

            // — пространственная сетка текстов для быстрого сопоставления (клетка ~0.5 м) —
            double bucketFt = FeetX(0.5);
            var buckets = new Dictionary<(long, long), List<int>>();
            (long, long) KeyOf(double x, double y) => ((long)Math.Floor(x / bucketFt), (long)Math.Floor(y / bucketFt));
            for (int i = 0; i < texts.Count; i++)
            {
                var k = KeyOf(texts[i].X, texts[i].Y);
                if (!buckets.TryGetValue(k, out var list)) buckets[k] = list = new List<int>();
                list.Add(i);
            }

            double minAs = double.MaxValue, maxAs = double.MinValue;
            foreach (var poly in faces)
            {
                double cx = poly.Average(p => p.X), cy = poly.Average(p => p.Y);
                var cell = new DxfCell { Polygon = poly, X = cx, Y = cy };

                int? hit = FindContaining(poly, texts, buckets, bucketFt, KeyOf);
                if (hit == null) hit = FindNearest(cx, cy, texts, buckets, bucketFt, KeyOf);

                if (hit != null)
                {
                    cell.As = texts[hit.Value].Val;
                    cell.HasValue = true;
                    if (cell.As < minAs) minAs = cell.As;
                    if (cell.As > maxAs) maxAs = cell.As;
                    res.Matched++;
                }
                else
                {
                    res.Unmatched++;
                }
                res.Cells.Add(cell);
            }
            res.MinAs = res.Matched > 0 ? minAs : 0;
            res.MaxAs = res.Matched > 0 ? maxAs : 0;
            return res;
        }

        private static int? FindContaining(List<XYZ> poly, List<(double X, double Y, double Val)> texts,
            Dictionary<(long, long), List<int>> buckets, double bucketFt, Func<double, double, (long, long)> keyOf)
        {
            double minX = poly.Min(p => p.X), maxX = poly.Max(p => p.X);
            double minY = poly.Min(p => p.Y), maxY = poly.Max(p => p.Y);
            long gx0 = (long)Math.Floor(minX / bucketFt), gx1 = (long)Math.Floor(maxX / bucketFt);
            long gy0 = (long)Math.Floor(minY / bucketFt), gy1 = (long)Math.Floor(maxY / bucketFt);
            for (long gx = gx0; gx <= gx1; gx++)
                for (long gy = gy0; gy <= gy1; gy++)
                    if (buckets.TryGetValue((gx, gy), out var list))
                        foreach (var i in list)
                            if (PointInPolygon(poly, texts[i].X, texts[i].Y))
                                return i;
            return null;
        }

        private static int? FindNearest(double cx, double cy, List<(double X, double Y, double Val)> texts,
            Dictionary<(long, long), List<int>> buckets, double bucketFt, Func<double, double, (long, long)> keyOf)
        {
            var (gx0, gy0) = keyOf(cx, cy);
            int? best = null; double bestD = double.MaxValue;
            for (long gx = gx0 - 1; gx <= gx0 + 1; gx++)
                for (long gy = gy0 - 1; gy <= gy0 + 1; gy++)
                    if (buckets.TryGetValue((gx, gy), out var list))
                        foreach (var i in list)
                        {
                            double dx = texts[i].X - cx, dy = texts[i].Y - cy, d = dx * dx + dy * dy;
                            if (d < bestD) { bestD = d; best = i; }
                        }
            return best;
        }

        private static bool PointInPolygon(List<XYZ> p, double x, double y)
        {
            bool c = false;
            for (int i = 0, j = p.Count - 1; i < p.Count; j = i++)
            {
                double xi = p[i].X, yi = p[i].Y, xj = p[j].X, yj = p[j].Y;
                if (((yi > y) != (yj > y)) && (x < (xj - xi) * (y - yi) / (yj - yi) + xi)) c = !c;
            }
            return c;
        }

        private static List<XYZ> FacePoints(DxfEntity ent)
        {
            var p1 = new XYZ(FeetX(ent.D(10)), FeetX(ent.D(20)), FeetX(ent.D(30)));
            var p2 = new XYZ(FeetX(ent.D(11)), FeetX(ent.D(21)), FeetX(ent.D(31)));
            var p3 = new XYZ(FeetX(ent.D(12)), FeetX(ent.D(22)), FeetX(ent.D(32)));
            var p4 = new XYZ(FeetX(ent.D(13)), FeetX(ent.D(23)), FeetX(ent.D(33)));
            bool triangle = p4.IsAlmostEqualTo(p3, CoordEpsFt);
            return triangle
                ? new List<XYZ> { p1, p2, p3 }
                : new List<XYZ> { p1, p2, p3, p4 };
        }

        private static double FeetX(double meters) => UnitUtils.ConvertToInternalUnits(meters, UnitTypeId.Meters);

        /// <summary>
        /// Разбирает заголовок вида «Режим основной. Площадь полной арматуры на 1пм
        /// по оси X у верхней грани» (или «...по оси Y у нижней грани» и т.п.) в Face/Dir.
        /// Направление и грань — независимые подстроки, порядок слов не важен.
        /// </summary>
        private static (Face Face, Dir Dir)? TryParseLegend(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;

            Dir? dir = null;
            if (text.IndexOf("оси X", StringComparison.OrdinalIgnoreCase) >= 0) dir = Dir.X;
            else if (text.IndexOf("оси Y", StringComparison.OrdinalIgnoreCase) >= 0) dir = Dir.Y;
            if (dir == null) return null;

            Face? face = null;
            if (text.IndexOf("верхней", StringComparison.OrdinalIgnoreCase) >= 0) face = Face.Top;
            else if (text.IndexOf("нижней", StringComparison.OrdinalIgnoreCase) >= 0) face = Face.Bottom;
            if (face == null) return null;

            return (face.Value, dir.Value);
        }

        // ────────────────────────────────────────────────────────
        //  Низкоуровневый разбор DXF: пары код/значение → сущности секции ENTITIES.
        // ────────────────────────────────────────────────────────
        private class DxfEntity
        {
            public string Type;
            public readonly Dictionary<int, string> Codes = new Dictionary<int, string>();
            public string Layer => Codes.TryGetValue(8, out var v) ? v : "";
            public bool Has(int code) => Codes.ContainsKey(code);
            public double D(int code, double def = 0) =>
                Codes.TryGetValue(code, out var v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : def;
            public string S(int code, string def = null) => Codes.TryGetValue(code, out var v) ? v : def;
        }

        private static IEnumerable<DxfEntity> ReadEntities(string path)
        {
            // FileShare.ReadWrite — читаем, даже если файл открыт другой программой на чтение/запись
            // (например, просмотрщик DXF или сам ЛИРА); если та программа держит его монопольно,
            // это всё равно не поможет — тогда сработает понятное сообщение в RebarZonesCommand.
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var sr = new StreamReader(fs, Encoding.UTF8))
            {
                bool inEntities = false;
                DxfEntity cur = null;
                string codeLine, valLine;
                while ((codeLine = sr.ReadLine()) != null)
                {
                    valLine = sr.ReadLine();
                    if (valLine == null) break;
                    if (!int.TryParse(codeLine.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int code))
                        continue;
                    string val = valLine.TrimEnd('\r');

                    if (code == 0)
                    {
                        if (cur != null && inEntities) yield return cur;
                        cur = null;

                        if (val == "SECTION")
                        {
                            // следующая пара обычно "2 <ИмяСекции>" — прочитаем её здесь же
                            string c2 = sr.ReadLine(), v2 = sr.ReadLine();
                            inEntities = c2 != null && c2.Trim() == "2" && v2 != null && v2.TrimEnd('\r') == "ENTITIES";
                            continue;
                        }
                        if (val == "ENDSEC") { inEntities = false; continue; }
                        if (val == "EOF") break;

                        if (inEntities) cur = new DxfEntity { Type = val };
                        continue;
                    }

                    if (cur != null) cur.Codes[code] = val;
                }
                if (cur != null && inEntities) yield return cur;
            }
        }
    }
}

namespace DAN_Plugin
{
    // ─────────────────────────────────────────────────────────────────────────
    // Диагностика: выбрать DXF → показать статистику разбора (без записи в документ).
    // Как и остальные команды в Debug.cs — не подключена к ленте, запуск через Add-In Manager.
    // ─────────────────────────────────────────────────────────────────────────
    [Autodesk.Revit.Attributes.Transaction(Autodesk.Revit.Attributes.TransactionMode.ReadOnly)]
    public class ImportDxfArmoringTest : Autodesk.Revit.UI.IExternalCommand
    {
        public Autodesk.Revit.UI.Result Execute(
            Autodesk.Revit.UI.ExternalCommandData commandData, ref string message, Autodesk.Revit.DB.ElementSet elements)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Импорт мозаики армирования (DXF)",
                Filter = "DXF файлы (*.dxf)|*.dxf|Все файлы (*.*)|*.*"
            };
            if (dlg.ShowDialog() != true) return Autodesk.Revit.UI.Result.Cancelled;

            LiraToRevit.Rebar.DxfImportResult res;
            try { res = LiraToRevit.Rebar.DxfArmoringReader.Read(dlg.FileName); }
            catch (System.Exception ex)
            {
                Autodesk.Revit.UI.TaskDialog.Show("Ошибка импорта DXF", ex.ToString());
                return Autodesk.Revit.UI.Result.Failed;
            }

            double M(double ft) => Autodesk.Revit.DB.UnitUtils.ConvertFromInternalUnits(ft, Autodesk.Revit.DB.UnitTypeId.Meters);

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"3DFACE (layer_elements): {res.FacesRead}");
            sb.AppendLine($"TEXT (layer_result_values): {res.TextsRead}");
            sb.AppendLine($"Сопоставлено с As: {res.Matched}");
            sb.AppendLine($"Без значения As: {res.Unmatched}");
            if (res.Matched > 0)
                sb.AppendLine($"As диапазон: {res.MinAs:0.00} … {res.MaxAs:0.00} см²/м");
            sb.AppendLine();
            sb.AppendLine("Первые ячейки:");
            foreach (var c in System.Linq.Enumerable.Take(res.Cells, 8))
                sb.AppendLine($"  x={M(c.X):0.000}  y={M(c.Y):0.000}  As={(c.HasValue ? c.As.ToString("0.00") : "—")}  верш.={c.Polygon.Count}");

            Autodesk.Revit.UI.TaskDialog.Show("Импорт DXF — результат", sb.ToString());
            return Autodesk.Revit.UI.Result.Succeeded;
        }
    }
}
