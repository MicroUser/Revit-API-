using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace DxfCleaner
{
    /// <summary>Итог очистки — для показа пользователю, без тихого отбрасывания данных.</summary>
    public class CleanSummary
    {
        public string OutputPath;
        public int CellsFound;                 // 3DFACE на layer_elements
        public int CellsMatched;                // из них получили подпись As и центрированы
        public int CellsUnmatched;               // без подписи рядом — нужна ручная проверка
        public int OrphanValueTexts;             // подписи As, не попавшие ни в одну ячейку — не тронуты
        public Dictionary<string, int> DroppedByLayer = new Dictionary<string, int>();

        /// <summary>Высотная отметка плиты, метры — медиана Z всех вершин 3DFACE на layer_elements
        /// (тот же приём, что и в RebarZones/SlabMarkCommand.cs при определении этажа по мозаике).
        /// 0, если граней не найдено.</summary>
        public double ElevationM;
    }

    /// <summary>
    /// Очистка DXF-мозаики армирования из ЛИРА: убирает всё, что не на layer_elements/
    /// layer_result_values/layer_result_palette, и переставляет подписи As (TEXT на
    /// layer_result_values) точно в центр их конечного элемента + выставляет высоту текста 0.1.
    ///
    /// Сопоставление "подпись → грань" — НЕ через поиск ближайшей/содержащей грани "вслепую"
    /// (так делает более снисходительный RebarZones/DxfArmoringImport.cs — DxfArmoringReader, и
    /// первая версия этого инструмента копировала тот же приём) — у ЛИРА подпись смещена от
    /// центра своего КЭ вниз и влево РОВНО на высоту самой подписи (код 40 у TEXT, до того как
    /// эта высота ниже перезаписывается на фиксированные 0.1). Поэтому для каждой подписи сперва
    /// вычитаем эту высоту из X и Y — почти всегда попадаем прямо в свою грань — и только затем на
    /// месте проверяем: попали ли внутрь контура (с небольшим запасом на неточность) и не занята
    /// ли уже эта грань другой подписью. Если занята — ищем свободную соседнюю в небольшом радиусе.
    ///
    /// История (важно не наступить повторно): изначально сдвиг был захардкожен константой (вниз
    /// 0.22м, влево 0.25м) — подобранной на одном конкретном экспорте. Проверено вживую (два
    /// экспорта одной и той же мозаики при разном масштабе вида ЛИРА-САПР перед экспортом), что
    /// эта константа НЕ универсальна: смещение подписи (и её высота) плывёт с масштабом вида.
    /// Первая попытка исправить — самокалибровка по каждому файлу (медиана сдвига по "надёжным"
    /// совпадениям, второй проход с уточнённым сдвигом) — подняла % сопоставления, но проверка по
    /// физическому положению ячеек показала: ~19% "новых" совпадений были ТИХОЙ ПОДМЕНОЙ значения
    /// соседней ячейки, а не честным исправлением. Для инженерных данных по армированию это
    /// на порядок опаснее видимых пропусков — самокалибровку откатили. Настоящее решение нашлось
    /// эмпирически (пользователь вручную подобрал сдвиг на двух свежих экспортах и заметил, что
    /// требуемое смещение ТОЧНО равно исходной высоте самой подписи, не какой-то доле от неё) —
    /// подтверждено тем же способом перекрёстной проверки по физическому положению: 100%
    /// сопоставления на ОБОИХ экспортах (обычный масштаб и сильно отдалённый), и ВСЕ 4468 ячеек,
    /// присутствующих в обоих файлах, получили ОДИНАКОВОЕ значение — 0 расхождений. Сдвиг теперь
    /// вычисляется на лету из данных каждой отдельной подписи, а не берётся статической константой.
    /// </summary>
    public static class DxfCleanerCore
    {
        private const string LayerCells = "layer_elements";
        private const string LayerValues = "layer_result_values";
        private const string LayerPalette = "layer_result_palette";

        private const double TextHeightM = 0.1;    // фиксированная высота подписи As, метры
        private const double BucketM = 0.5;        // сторона ячейки пространственной сетки, метры
        private const double CoordEpsM = 1e-6;     // сравнение точек 3DFACE (p3==p4 → треугольник)

        private const double SnapRadiusM = 0.2;           // если сдвинутая точка не попала точно в грань — насколько ещё искать рядом
        private const double FreeCellSearchRadiusM = 0.5; // если "своя" грань уже занята — радиус поиска свободной соседней

        public static CleanSummary Clean(string sourcePath)
        {
            var groups = DxfDocument.Load(sourcePath);
            var summary = new CleanSummary();

            // ── проход 1: границы ENTITIES + сбор граней/подписей ──────────────────────
            var faces = new List<(int GroupIndex, List<(double X, double Y)> Poly, double Cx, double Cy)>();
            var texts = new List<(int GroupIndex, double X, double Y, double Val, double Height)>();
            var allZs = new List<double>();

            bool inEntities = false;
            for (int i = 0; i < groups.Count; i++)
            {
                var g = groups[i];
                if (g.Type == "SECTION" && g.S(2) == "ENTITIES") { inEntities = true; continue; }
                if (g.Type == "ENDSEC" && inEntities) { inEntities = false; continue; }
                if (!inEntities) continue;

                if (g.Type == "3DFACE" && g.Layer == LayerCells)
                {
                    var poly = FacePoints(g);
                    double cx = poly.Average(p => p.X), cy = poly.Average(p => p.Y);
                    faces.Add((i, poly, cx, cy));
                    allZs.AddRange(FaceZs(g));
                }
                else if (g.Type == "TEXT" && g.Layer == LayerValues)
                {
                    string raw = g.S(1);
                    if (raw != null && double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var val))
                    {
                        // позиция подписи: alignment point (11/21), если есть — иначе insertion (10/20)
                        double tx = g.Has(11) ? g.D(11) : g.D(10);
                        double ty = g.Has(21) ? g.D(21) : g.D(20);
                        texts.Add((i, tx, ty, val, g.D(40)));
                    }
                }
            }
            summary.CellsFound = faces.Count;
            summary.ElevationM = Median(allZs);

            // ── пространственная сетка ГРАНЕЙ (по центроиду) — ищем "какая грань здесь"
            // по сдвинутой точке подписи, а не наоборот (как раньше) ──
            (long, long) KeyOf(double x, double y) => ((long)Math.Floor(x / BucketM), (long)Math.Floor(y / BucketM));
            var faceBuckets = new Dictionary<(long, long), List<int>>();
            for (int fi = 0; fi < faces.Count; fi++)
            {
                var k = KeyOf(faces[fi].Cx, faces[fi].Cy);
                if (!faceBuckets.TryGetValue(k, out var list)) faceBuckets[k] = list = new List<int>();
                list.Add(fi);
            }

            var recenterByGroupIndex = new Dictionary<int, (double Cx, double Cy)>();
            var assignedFace = new bool[faces.Count];
            int matchedCells = 0, usedTexts = 0;

            // Порядок обхода подписей — как в файле; конфликты (см. ниже) на практике редки,
            // поскольку сдвиг почти всегда указывает прямо на "свою" грань, а не на случайную
            // соседнюю — в отличие от чистого поиска по близости, здесь очередь почти не влияет.
            foreach (var t in texts)
            {
                // Сдвиг = собственная высота ЭТОЙ подписи (см. класс-док) — не общая константа и
                // не статистика по файлу, поэтому корректно работает независимо от масштаба вида
                // при экспорте и не зависит от других подписей в файле.
                double px = t.X - t.Height, py = t.Y - t.Height;

                int? faceIdx = FindContainingFace(px, py, faces, faceBuckets, KeyOf)
                               ?? FindNearestFace(px, py, faces, faceBuckets, KeyOf, SnapRadiusM);
                if (faceIdx == null) continue;   // сдвинутая точка никуда не попала — не трогаем эту подпись

                if (assignedFace[faceIdx.Value])
                {
                    // "Своя" грань по сдвигу уже занята другой подписью — ищем свободную рядом
                    // (небольшой радиус: реальный сосед, а не случайная дальняя ячейка).
                    var alt = FindNearestFreeFace(faces[faceIdx.Value].Cx, faces[faceIdx.Value].Cy,
                        faces, faceBuckets, KeyOf, assignedFace, FreeCellSearchRadiusM);
                    if (alt == null) continue;
                    faceIdx = alt;
                }

                assignedFace[faceIdx.Value] = true;
                usedTexts++;
                matchedCells++;
                recenterByGroupIndex[t.GroupIndex] = (faces[faceIdx.Value].Cx, faces[faceIdx.Value].Cy);
            }
            summary.CellsMatched = matchedCells;
            summary.CellsUnmatched = faces.Count - matchedCells;
            summary.OrphanValueTexts = texts.Count - usedTexts;

            // ── проход 2: решаем keep/drop/recenter, собираем итоговый список блоков ──────
            var result = new List<DxfEntity>(groups.Count);
            inEntities = false;
            for (int i = 0; i < groups.Count; i++)
            {
                var g = groups[i];

                if (g.Type == "SECTION" && g.S(2) == "ENTITIES") { inEntities = true; result.Add(g); continue; }
                if (g.Type == "ENDSEC" && inEntities) { inEntities = false; result.Add(g); continue; }
                if (!inEntities) { result.Add(g); continue; }

                string layer = g.Layer;
                if (layer != LayerCells && layer != LayerValues && layer != LayerPalette)
                {
                    summary.DroppedByLayer.TryGetValue(layer, out var c);
                    summary.DroppedByLayer[layer] = c + 1;
                    continue;   // "лишний" слой — не попадает в итоговый файл
                }

                if (g.Type == "TEXT" && layer == LayerValues)
                {
                    // Высота подписи As — всегда 0.1, независимо от исходного экспорта ЛИРА,
                    // чтобы подпись гарантированно помещалась внутри грани КЭ.
                    g.Set(40, TextHeightM.ToString("0.0##", CultureInfo.InvariantCulture));

                    if (recenterByGroupIndex.TryGetValue(i, out var c))
                    {
                        string cxs = c.Cx.ToString("0.000000", CultureInfo.InvariantCulture);
                        string cys = c.Cy.ToString("0.000000", CultureInfo.InvariantCulture);
                        g.Set(10, cxs); g.Set(20, cys);
                        if (g.Has(11)) g.Set(11, cxs);
                        if (g.Has(21)) g.Set(21, cys);
                    }
                }

                result.Add(g);
            }

            // Перезаписываем исходный файл на месте — отдельный "_clean.dxf" больше не создаём.
            // Пишем во временный файл рядом и заменяем им исходный ТОЛЬКО после успешной записи —
            // если запись оборвётся (диск/права/ещё что-то), оригинал останется целым, а не
            // наполовину переписанным.
            string tempPath = sourcePath + ".tmp";
            DxfDocument.Save(tempPath, result);
            File.Move(tempPath, sourcePath, true);
            summary.OutputPath = sourcePath;
            return summary;
        }

        private static List<(double X, double Y)> FacePoints(DxfEntity g)
        {
            double x1 = g.D(10), y1 = g.D(20);
            double x2 = g.D(11), y2 = g.D(21);
            double x3 = g.D(12), y3 = g.D(22);
            double x4 = g.D(13), y4 = g.D(23);
            bool triangle = Math.Abs(x4 - x3) < CoordEpsM && Math.Abs(y4 - y3) < CoordEpsM;
            var pts = new List<(double X, double Y)> { (x1, y1), (x2, y2), (x3, y3) };
            if (!triangle) pts.Add((x4, y4));
            return pts;
        }

        /// <summary>Z всех вершин грани (коды 30/31/32/33), с той же поправкой на треугольник
        /// (4-я точка совпадает с 3-й), что и FacePoints — используется только для отметки, не
        /// для сопоставления/геометрии.</summary>
        private static IEnumerable<double> FaceZs(DxfEntity g)
        {
            bool triangle = Math.Abs(g.D(13) - g.D(12)) < CoordEpsM && Math.Abs(g.D(23) - g.D(22)) < CoordEpsM;
            yield return g.D(30); yield return g.D(31); yield return g.D(32);
            if (!triangle) yield return g.D(33);
        }

        private static double Median(List<double> values)
        {
            if (values.Count == 0) return 0;
            var sorted = values.OrderBy(v => v).ToList();
            int mid = sorted.Count / 2;
            return sorted.Count % 2 == 0 ? (sorted[mid - 1] + sorted[mid]) / 2.0 : sorted[mid];
        }

        private static bool PointInPolygon(List<(double X, double Y)> poly, double x, double y)
        {
            bool c = false;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            {
                double xi = poly[i].X, yi = poly[i].Y, xj = poly[j].X, yj = poly[j].Y;
                if (((yi > y) != (yj > y)) && (x < (xj - xi) * (y - yi) / (yj - yi) + xi)) c = !c;
            }
            return c;
        }

        /// <summary>Грань, геометрически содержащая точку (среди соседних по сетке 0.5м) — при
        /// нескольких кандидатах (не должно случаться для неперекрывающейся сетки КЭ, но на всякий
        /// случай) берём с ближайшим центром.</summary>
        private static int? FindContainingFace(double px, double py,
            List<(int GroupIndex, List<(double X, double Y)> Poly, double Cx, double Cy)> faces,
            Dictionary<(long, long), List<int>> faceBuckets, Func<double, double, (long, long)> keyOf)
        {
            var (gx0, gy0) = keyOf(px, py);
            int? best = null; double bestD = double.MaxValue;
            for (long gx = gx0 - 1; gx <= gx0 + 1; gx++)
                for (long gy = gy0 - 1; gy <= gy0 + 1; gy++)
                    if (faceBuckets.TryGetValue((gx, gy), out var list))
                        foreach (var fi in list)
                            if (PointInPolygon(faces[fi].Poly, px, py))
                            {
                                double dx = faces[fi].Cx - px, dy = faces[fi].Cy - py, d = dx * dx + dy * dy;
                                if (d < bestD) { bestD = d; best = fi; }
                            }
            return best;
        }

        /// <summary>Ближайшая по центру грань в пределах maxDistM — запасной вариант, когда
        /// сдвинутая точка чуть-чуть не попала внутрь контура (неточность калибровки сдвига).</summary>
        private static int? FindNearestFace(double px, double py,
            List<(int GroupIndex, List<(double X, double Y)> Poly, double Cx, double Cy)> faces,
            Dictionary<(long, long), List<int>> faceBuckets, Func<double, double, (long, long)> keyOf, double maxDistM)
        {
            var (gx0, gy0) = keyOf(px, py);
            int? best = null; double bestD = double.MaxValue;
            for (long gx = gx0 - 1; gx <= gx0 + 1; gx++)
                for (long gy = gy0 - 1; gy <= gy0 + 1; gy++)
                    if (faceBuckets.TryGetValue((gx, gy), out var list))
                        foreach (var fi in list)
                        {
                            double dx = faces[fi].Cx - px, dy = faces[fi].Cy - py, d = dx * dx + dy * dy;
                            if (d < bestD) { bestD = d; best = fi; }
                        }
            return best != null && bestD <= maxDistM * maxDistM ? best : null;
        }

        /// <summary>Ближайшая ЕЩЁ НЕ занятая грань в пределах maxDistM — для разрешения конфликта,
        /// когда "своя" по сдвигу грань уже получила другую подпись.</summary>
        private static int? FindNearestFreeFace(double px, double py,
            List<(int GroupIndex, List<(double X, double Y)> Poly, double Cx, double Cy)> faces,
            Dictionary<(long, long), List<int>> faceBuckets, Func<double, double, (long, long)> keyOf,
            bool[] assigned, double maxDistM)
        {
            long r = (long)Math.Ceiling(maxDistM / BucketM) + 1;
            var (gx0, gy0) = keyOf(px, py);
            int? best = null; double bestD = double.MaxValue;
            for (long gx = gx0 - r; gx <= gx0 + r; gx++)
                for (long gy = gy0 - r; gy <= gy0 + r; gy++)
                    if (faceBuckets.TryGetValue((gx, gy), out var list))
                        foreach (var fi in list)
                        {
                            if (assigned[fi]) continue;
                            double dx = faces[fi].Cx - px, dy = faces[fi].Cy - py, d = dx * dx + dy * dy;
                            if (d < bestD) { bestD = d; best = fi; }
                        }
            return best != null && bestD <= maxDistM * maxDistM ? best : null;
        }
    }
}
