using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using KzhNotes;

namespace LiraToRevit.Rebar
{
    /// <summary>
    /// Точка входа: выбрать плиту → выбрать DXF-экспорт мозаики армирования ЛИРА (грань и
    /// направление стержней читаются из текстовой легенды в самом файле, например «...по оси X
    /// у верхней грани»; если легенда не найдена/не разобралась — спрашиваем явно) → открыть
    /// окно подтверждения зон. Сетка ячеек центрируется по габариту плиты автоматически;
    /// контур плиты и оси проекта передаются в редактор только для наглядности (фон), точная
    /// привязка стержней к ближайшей оси (кратно 10 мм) делается позже, при создании — см.
    /// RebarPlacer. Как и другие команды модуля — без кнопки на ленте, запуск через
    /// Add-In Manager (см. Debug.cs / ImportDxfArmoringTest).
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class RebarZonesCommand : IExternalCommand
    {
        // Ключ для AppDomain.SetData/GetData (не static-поле!) — эта команда грузится HotLoader'ом
        // (см. Loader.cs) заново из НОВОЙ временной копии DAN_Plugin.dll при КАЖДОМ вызове, так что
        // static-поле не переживёт следующий вызов (это будет уже другой Type из другой загрузки
        // сборки). AppDomain-слот — единственное хранилище, переживающее такие перезагрузки (по той
        // же причине HotLoader сам передаёт DAN_PluginDir через AppDomain.SetData, не static). Храним
        // как базовый System.Windows.Window (тип из PresentationFramework.dll, грузится один раз и
        // не меняется между вызовами — в отличие от RebarZonesWindow из перезагружаемого DAN_Plugin.dll,
        // его нельзя было бы стабильно скастовать обратно).
        private const string OpenWindowKey = "DAN_RebarZonesWindow";

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (TryActivateExistingWindow()) return Result.Succeeded;

            var uiApp = commandData.Application;
            var uiDoc = uiApp.ActiveUIDocument;
            var doc = uiDoc.Document;

            Floor floor;
            try
            {
                var pickedRef = uiDoc.Selection.PickObject(ObjectType.Element, new FloorFilter(), "Выберите плиту");
                floor = doc.GetElement(pickedRef) as Floor;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }

            if (floor == null)
            {
                TaskDialog.Show("Ошибка", "Выбранный элемент не является плитой.");
                return Result.Failed;
            }

            string assetFolder = AssetFolder();
            if (assetFolder == null)
            {
                TaskDialog.Show("Ошибка", "Не найден RebarZones\\rebar_zones.html рядом с плагином.");
                return Result.Failed;
            }

            // DXF больше не выбираются здесь блокирующим диалогом — окно открывается сразу с тем,
            // что уже сохранено для этой плиты (может быть от 0 до 4 вкладок), а файлы
            // подгружаются/заменяются изнутри редактора кнопкой "📁 DXF" — см.
            // RebarZonesWindow.OnWebMessage ("loaddxf:") / RebarZonesDxfPicker. Исключение — если
            // для плиты не сохранено вообще ничего: тогда диалог выбора всё же показываем сразу,
            // а не оставляем пользователя перед пустым окном (см. LoadOrPromptInitialDatasets).
            var notes = new List<string>();
            var datasets = LoadCachedDatasets(doc, floor, notes);
            if (datasets.Count == 0) PromptInitialDxf(doc, floor, datasets, notes);

            var contourMismatch = ComputeContourMismatch(
                datasets.Select(d => (d.Dxf, d.Face, d.Dir)), floor, notes);

            if (notes.Count > 0)
                TaskDialog.Show("Допармирование", string.Join("\n", notes));

            string json = BuildInitJson(doc, floor, datasets, contourMismatch);
            OpenWindow(uiApp, assetFolder, json, floor.Id);

            return Result.Succeeded;
        }

        /// <summary>Уже открытое окно — вместо второго поверх разворачиваем (если свёрнуто) и
        /// выводим его на передний план. См. OpenWindowKey. Одного Activate() недостаточно: если
        /// окно свёрнуто, оно останется в панели задач, а если фокус сейчас у другого приложения,
        /// Windows может проигнорировать Activate() (foreground lock) — переключение Topmost
        /// туда-обратно надёжно принудительно выводит окно наверх в обоих случаях.</summary>
        private bool TryActivateExistingWindow()
        {
            if (!(AppDomain.CurrentDomain.GetData(OpenWindowKey) is System.Windows.Window existing) || !existing.IsLoaded)
                return false;

            if (existing.WindowState == System.Windows.WindowState.Minimized)
                existing.WindowState = System.Windows.WindowState.Normal;

            existing.Show();
            existing.Activate();
            existing.Topmost = true;
            existing.Topmost = false;
            existing.Focus();
            return true;
        }

        private void OpenWindow(UIApplication uiApp, string assetFolder, string json, ElementId floorId)
        {
            var bridge = new RevitEventBridge();
            bridge.Init();

            var win = new RebarZonesWindow(uiApp.MainWindowHandle, assetFolder, json, bridge, floorId);
            AppDomain.CurrentDomain.SetData(OpenWindowKey, win);
            win.Closed += (s, e) => AppDomain.CurrentDomain.SetData(OpenWindowKey, null);
            win.Show();
        }

        /// <summary>Читает сохранённые для плиты пути DXF (см. RebarZonesDataStore) и загружает те,
        /// что ещё существуют на диске и не пустые. Проблемные — в notes, не в исключение: одна
        /// битая вкладка не должна мешать открыть остальные.</summary>
        private static List<(DxfImportResult Dxf, Face Face, Dir Dir, string Path)> LoadCachedDatasets(
            Document doc, Floor floor, List<string> notes)
        {
            var datasets = new List<(DxfImportResult Dxf, Face Face, Dir Dir, string Path)>();
            foreach (var kv in RebarZonesDataStore.LoadPaths(doc, floor))
            {
                string label = RebarZonesDataStore.ComboLabel(kv.Key);
                if (!File.Exists(kv.Value))
                {
                    notes.Add(label + ": сохранённый файл \"" + kv.Value + "\" больше не найден на диске — выберите заново через «📁 DXF».");
                    continue;
                }
                try
                {
                    var dxf = DxfArmoringReader.Read(kv.Value);
                    if (dxf.Cells.Count == 0)
                    {
                        notes.Add(label + ": в сохранённом файле нет ячеек — выберите заново через «📁 DXF».");
                        continue;
                    }
                    string[] parts = kv.Key.Split('-');
                    Face face = parts[0] == "bottom" ? Face.Bottom : Face.Top;
                    Dir dir = parts[1] == "y" ? Dir.Y : Dir.X;
                    datasets.Add((dxf, face, dir, kv.Value));
                }
                catch (Exception ex)
                {
                    notes.Add(label + ": ошибка чтения сохранённого файла — " + ex.Message + " — выберите заново через «📁 DXF».");
                }
            }
            return datasets;
        }

        /// <summary>Для плиты не сохранено вообще ничего (не "не хватает пары вкладок", а именно
        /// ни одной) — в этом единственном случае диалог выбора файла показываем сразу, а не
        /// оставляем пользователя перед пустым окном. Отмена — не ошибка, просто откроется пустое
        /// окно, кнопка "📁 DXF" никуда не денется. Выбранное дописывается в datasets и в кэш.</summary>
        private static void PromptInitialDxf(Document doc, Floor floor,
            List<(DxfImportResult Dxf, Face Face, Dir Dir, string Path)> datasets, List<string> notes)
        {
            var picked = RebarZonesDxfPicker.PickFiles(
                "DXF мозаики армирования (ЛИРА) — можно выбрать сразу несколько файлов на одну плиту (Верх/Низ × X/Y)",
                Array.Empty<string>(), notes);
            if (picked.Count == 0) return;

            datasets.AddRange(picked);
            RebarZonesDataStore.SavePaths(doc, floor,
                picked.ToDictionary(d => RebarZonesDataStore.ComboKey(d.Face, d.Dir), d => d.Path));
        }

        private static double M(double ft) => UnitUtils.ConvertFromInternalUnits(ft, UnitTypeId.Meters);

        /// <summary>Раньше бралось из Z первой ячейки DXF — теперь из отметки уровня самой плиты
        /// в Revit, т.к. окно может открываться и без единого загруженного DXF (см. Execute).</summary>
        private static string PlateLabel(Floor floor, Document doc)
        {
            var level = doc.GetElement(floor.LevelId) as Level;
            double m = level != null ? M(level.Elevation) : 0;
            return "Плита " + (m >= 0 ? "+" : "") + m.ToString("0.000", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// DXF из ЛИРА не обязательно в тех же координатах, что модель Revit (разные базовые
        /// точки/начала координат при экспорте). Выравниваем по углу габарита (min X, min Y) — не
        /// по центру: контур плиты и габарит DXF-мозаики уже проверены на совпадение по площади
        /// (см. ContourMismatch), а для неправильной (ступенчатой) формы центр габарита не
        /// совпадает с "видимым" центром фигуры и выравнивание по нему съезжает. Совмещение по
        /// одному углу однозначно задаёт сдвиг при простом параллельном переносе — тот же принцип,
        /// что и у сетки изолиний (см. SlabGeometry.BuildIsolines — тоже от bboxMin). Поворот не
        /// учитывается — предполагается, что оси X/Y ЛИРА и Revit сонаправлены.
        /// </summary>
        private static (double Dx, double Dy) ComputeOffset(Floor floor, DxfImportResult dxf)
        {
            var bb = floor.get_BoundingBox(null);

            double minX = dxf.Cells.Min(c => c.Polygon.Min(p => p.X));
            double minY = dxf.Cells.Min(c => c.Polygon.Min(p => p.Y));

            return (bb.Min.X - minX, bb.Min.Y - minY);
        }

        /// <summary>
        /// Грубая сверка "тот ли DXF выбран для этой плиты" — площадь габаритного прямоугольника
        /// контура плиты в Revit против площади габарита мозаики КЭ из DXF. Не точная геометрия
        /// (не вычитает проёмы, не учитывает форму контура) — специально просто и быстро, чтобы
        /// поймать явную ошибку (не тот файл/не та плита), а не подменить реальную проверку контура.
        /// </summary>
        private static bool ContourMismatch(Floor floor, DxfImportResult dxf, out double diffPercent)
        {
            var bb = floor.get_BoundingBox(null);
            double revitArea = M(bb.Max.X - bb.Min.X) * M(bb.Max.Y - bb.Min.Y);

            double minX = dxf.Cells.Min(c => c.Polygon.Min(p => p.X));
            double maxX = dxf.Cells.Max(c => c.Polygon.Max(p => p.X));
            double minY = dxf.Cells.Min(c => c.Polygon.Min(p => p.Y));
            double maxY = dxf.Cells.Max(c => c.Polygon.Max(p => p.Y));
            double dxfArea = M(maxX - minX) * M(maxY - minY);

            diffPercent = revitArea > 1e-6 ? Math.Abs(revitArea - dxfArea) / revitArea * 100.0 : 0;
            return diffPercent > 20.0;
        }

        /// <summary>
        /// Внешний контур верхней грани плиты (визуальный ориентир на фоне редактора) + крупные
        /// проёмы (≥1000×1000мм — та же логика и порог, что и в RebarPlacer.FilterLoopsForClipping
        /// на стороне размещения): мелкие отверстия арматура просто перекрывает, без огибания, а
        /// у крупных стержень в реальности обрывается — редактору тоже нужно об этом знать, иначе
        /// анкеровка на превью рисуется поверх проёма, хотя фактически там будет обрезка/загиб.
        /// </summary>
        private const double MinOpeningMm = 1000.0;
        private static (List<XYZ> Outer, List<List<XYZ>> Openings) GetFloorOutlineAndOpenings(Floor floor)
        {
            var opt = new Options { ComputeReferences = false, DetailLevel = ViewDetailLevel.Coarse };
            var allLoops = new List<List<XYZ>>();

            foreach (var go in floor.get_Geometry(opt))
            {
                if (!(go is Solid s) || s.Volume <= 0) continue;
                foreach (Autodesk.Revit.DB.Face f in s.Faces)
                {
                    if (!(f is PlanarFace pf) || !pf.FaceNormal.IsAlmostEqualTo(XYZ.BasisZ)) continue;
                    foreach (var loop in pf.GetEdgesAsCurveLoops())
                    {
                        var pts = new List<XYZ>();
                        foreach (Curve c in loop) pts.AddRange(c.Tessellate());
                        if (pts.Count >= 3) allLoops.Add(pts);
                    }
                }
            }
            if (allLoops.Count == 0) return (new List<XYZ>(), new List<List<XYZ>>());

            // внешний контур — петля с наибольшим габаритом bbox
            int outerIdx = 0;
            double bestArea = -1;
            for (int i = 0; i < allLoops.Count; i++)
            {
                var pts = allLoops[i];
                double area = (pts.Max(p => p.X) - pts.Min(p => p.X)) * (pts.Max(p => p.Y) - pts.Min(p => p.Y));
                if (area > bestArea) { bestArea = area; outerIdx = i; }
            }

            double minOpeningFt = UnitUtils.ConvertToInternalUnits(MinOpeningMm, UnitTypeId.Millimeters);
            var openings = new List<List<XYZ>>();
            for (int i = 0; i < allLoops.Count; i++)
            {
                if (i == outerIdx) continue;
                var pts = allLoops[i];
                double w = pts.Max(p => p.X) - pts.Min(p => p.X);
                double h = pts.Max(p => p.Y) - pts.Min(p => p.Y);
                if (w >= minOpeningFt && h >= minOpeningFt) openings.Add(pts);
            }

            return (allLoops[outerIdx], openings);
        }

        /// <summary>Оси (Grid) проекта — визуальный фон в редакторе, для наглядности расположения
        /// зон. Только те, что реально пересекают габарит ВЫБРАННОЙ плиты — в проекте с большим
        /// количеством осей (весь корпус на десятках этажей) без фильтра в редактор попадали ВСЕ
        /// оси проекта разом, забивая фон осями других частей здания, к этой плите не относящимися.</summary>
        private static List<(string Name, XYZ P0, XYZ P1)> GetGrids(Document doc, Floor floor)
        {
            var bb = floor.get_BoundingBox(null);
            return new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>()
                .Where(g => g.Curve is Line)
                .Select(g => (g.Name, P0: ((Line)g.Curve).GetEndPoint(0), P1: ((Line)g.Curve).GetEndPoint(1)))
                .Where(g => LineIntersectsBoxXY(g.P0, g.P1, bb))
                .ToList();
        }

        /// <summary>Пересекает ли отрезок p0-p1 прямоугольник габарита bb в плане (X/Y, Z не
        /// учитывается — оси вертикальны по всей высоте здания). Liang-Barsky — работает и для
        /// наклонных осей, не только для ортогональных.</summary>
        private static bool LineIntersectsBoxXY(XYZ p0, XYZ p1, BoundingBoxXYZ bb)
        {
            double dx = p1.X - p0.X, dy = p1.Y - p0.Y;
            double t0 = 0.0, t1 = 1.0;
            double[] p = { -dx, dx, -dy, dy };
            double[] q = { p0.X - bb.Min.X, bb.Max.X - p0.X, p0.Y - bb.Min.Y, bb.Max.Y - p0.Y };
            for (int i = 0; i < 4; i++)
            {
                if (Math.Abs(p[i]) < 1e-9)
                {
                    if (q[i] < 0) return false; // параллельно соответствующей стороне и снаружи неё
                }
                else
                {
                    double r = q[i] / p[i];
                    if (p[i] < 0) { if (r > t1) return false; if (r > t0) t0 = r; }
                    else { if (r < t0) return false; if (r < t1) t1 = r; }
                }
            }
            return true;
        }

        /// <summary>Допуск в плане при отборе опор для конкретной плиты, мм — пилон/колонна у
        /// кромки физически соприкасается с габаритом плиты почти всегда, небольшой запас — на
        /// случай, если плита не доходит вплотную до грани опоры (обрамление, зазор и т.п.).</summary>
        private const double SupportPlanToleranceMm = 500.0;

        /// <summary>Пилоны/колонны рядом с этой плитой (та же выборка "Категория именования"
        /// сборки, что и SupportDetector.HasParallelSupport, — она же решает П vs Г при загибе) —
        /// визуальный фон в редакторе, чтобы было видно, у какой кромки встанет П-образная форма.
        /// Отфильтрованы и по высоте (тот же допуск SupportZToleranceMm, что и в реальном решении
        /// формы), и по плану (габарит опоры должен пересекаться с габаритом ИМЕННО этой плиты) —
        /// без второго фильтра показывались бы вообще все опоры того же этажа сборки по всему
        /// проекту, а не только те, что реально стоят под этой плитой.</summary>
        private static List<List<XYZ>> GetSupports(Document doc, Floor floor)
        {
            var res = new List<List<XYZ>>();
            var floorBb = floor.get_BoundingBox(null);
            if (floorBb == null) return res;

            double zTolFt = UnitUtils.ConvertToInternalUnits(SupportDetector.SupportZToleranceMm, UnitTypeId.Millimeters);
            double xyTolFt = UnitUtils.ConvertToInternalUnits(SupportPlanToleranceMm, UnitTypeId.Millimeters);
            foreach (var s in SupportDetector.ResolveSupports(doc, new PlacementSettings()))
            {
                if (s.BBoxMax.Z < floorBb.Min.Z - zTolFt || s.BBoxMin.Z > floorBb.Max.Z + zTolFt) continue;
                if (s.BBoxMax.X < floorBb.Min.X - xyTolFt || s.BBoxMin.X > floorBb.Max.X + xyTolFt) continue;
                if (s.BBoxMax.Y < floorBb.Min.Y - xyTolFt || s.BBoxMin.Y > floorBb.Max.Y + xyTolFt) continue;

                // Настоящий контур стены (по оси + толщине), если получилось — осевой bbox
                // подходит для ортогональной прямой стены, но у диагональной/дуговой стены сильно
                // искажает форму и разваливает цепочку соседних сегментов "лесенкой" из
                // непохожих прямоугольников (см. скриншот пользователя: стена вдоль дуговой
                // кромки плиты, смоделированная короткими прямыми сегментами). Bbox — только
                // запасной вариант (не стена/нет LocationCurve/не удалось построить).
                List<XYZ> poly = s.MemberId != null && doc.GetElement(s.MemberId) is Wall wall
                    ? WallFootprint(wall) : null;
                if (poly == null)
                    poly = new List<XYZ>
                    {
                        new XYZ(s.BBoxMin.X, s.BBoxMin.Y, 0),
                        new XYZ(s.BBoxMax.X, s.BBoxMin.Y, 0),
                        new XYZ(s.BBoxMax.X, s.BBoxMax.Y, 0),
                        new XYZ(s.BBoxMin.X, s.BBoxMax.Y, 0)
                    };
                res.Add(poly);
            }
            return res;
        }

        /// <summary>Настоящий плановый контур стены по её оси (LocationCurve) и толщине (Width) —
        /// прямая стена даёт повёрнутый (вдоль стены, а не вдоль осей модели) прямоугольник, дуговая
        /// — кольцевой сектор (тоже по оси, со смещением ±Width/2 по радиусу, тесселированный —
        /// точная кривизна тут не нужна, это только визуальный фон). Null — нет LocationCurve или
        /// её кривая не Line/Arc (например, стена по сплайну — не ожидается в этом проекте).</summary>
        private static List<XYZ> WallFootprint(Wall wall)
        {
            if (!(wall.Location is LocationCurve lc)) return null;
            double halfW = wall.Width / 2.0;

            if (lc.Curve is Line line)
            {
                XYZ p0 = line.GetEndPoint(0), p1 = line.GetEndPoint(1);
                XYZ dir = (p1 - p0);
                if (dir.GetLength() < 1e-9) return null;
                dir = dir.Normalize();
                XYZ perp = new XYZ(-dir.Y, dir.X, 0).Multiply(halfW);
                return new List<XYZ>
                {
                    new XYZ(p0.X + perp.X, p0.Y + perp.Y, 0),
                    new XYZ(p1.X + perp.X, p1.Y + perp.Y, 0),
                    new XYZ(p1.X - perp.X, p1.Y - perp.Y, 0),
                    new XYZ(p0.X - perp.X, p0.Y - perp.Y, 0)
                };
            }

            if (lc.Curve is Arc arc)
            {
                var axis = arc.Tessellate();
                var outer = new List<XYZ>();
                var inner = new List<XYZ>();
                foreach (var pt in axis)
                {
                    XYZ radial = new XYZ(pt.X - arc.Center.X, pt.Y - arc.Center.Y, 0);
                    if (radial.GetLength() < 1e-9) continue;
                    radial = radial.Normalize().Multiply(halfW);
                    outer.Add(new XYZ(pt.X + radial.X, pt.Y + radial.Y, 0));
                    inner.Add(new XYZ(pt.X - radial.X, pt.Y - radial.Y, 0));
                }
                if (outer.Count < 2) return null;
                inner.Reverse();
                outer.AddRange(inner);
                return outer;
            }

            return null;
        }

        /// <summary>
        /// Грубая сверка "тот ли DXF выбран для этой плиты" для набора датасетов сразу — площадь
        /// габарита контура плиты в Revit против площади габарита мозаики КЭ из DXF (см.
        /// ContourMismatch). Не точная геометрия, специально просто и быстро — ловит явную ошибку
        /// (не тот файл/не та плита). Расхождения >20% попадают и в возвращаемый словарь (для
        /// красного "!" на вкладке в редакторе, см. rebar_zones.html renderTabs), и текстом в notes
        /// (для диалога/тоста). Общая логика для первого открытия (Execute → BuildInitJson) и для
        /// подгрузки новых вкладок кнопкой "📁 DXF" (BuildAddJson).
        /// </summary>
        private static Dictionary<string, double> ComputeContourMismatch(
            IEnumerable<(DxfImportResult Dxf, Face Face, Dir Dir)> datasets, Floor floor, List<string> notes)
        {
            var contourMismatch = new Dictionary<string, double>();
            foreach (var d in datasets)
            {
                if (!ContourMismatch(floor, d.Dxf, out double diffPercent)) continue;
                string comboKey = RebarZonesDataStore.ComboKey(d.Face, d.Dir);
                contourMismatch[comboKey] = diffPercent;
                notes.Add(RebarZonesDataStore.ComboLabel(comboKey) + ": контур плиты в Revit и габарит мозаики "
                    + "из DXF отличаются на " + diffPercent.ToString("0") + "% — проверьте, тот ли файл выбран для этой плиты.");
            }
            return contourMismatch;
        }

        /// <summary>Датасеты (ячейки мозаики, приведённые к координатам Revit — см. ComputeOffset)
        /// в форме, готовой для встраивания в JSON редактора. Общая логика для BuildInitJson и
        /// BuildAddJson.</summary>
        private static object[] BuildDatasetsPayload(IEnumerable<(DxfImportResult Dxf, Face Face, Dir Dir)> datasets, Floor floor)
        {
            return datasets.Select(d =>
            {
                var offset = ComputeOffset(floor, d.Dxf);
                return (object)new
                {
                    face = d.Face == Face.Bottom ? "bottom" : "top",
                    dir = d.Dir == Dir.Y ? "y" : "x",
                    cells = d.Dxf.Cells.Select(c => new
                    {
                        p = c.Polygon.Select(pt => new[] { Math.Round(M(pt.X + offset.Dx), 4), Math.Round(M(pt.Y + offset.Dy), 4) }).ToArray(),
                        x = Math.Round(M(c.X + offset.Dx), 4),
                        y = Math.Round(M(c.Y + offset.Dy), 4),
                        a = c.HasValue ? c.As : 0
                    }).ToArray()
                };
            }).ToArray();
        }

        /// <summary>
        /// Полный JSON для первого открытия окна — один "датасет" на комбинацию грань+направление
        /// (до 4 на плиту), каждый со своим смещением приводки к модели (см. ComputeOffset, у разных
        /// DXF-экспортов может отличаться база координат), плюс общий на всю плиту фон (контур,
        /// проёмы, оси, изолинии) и история/снимок зон из прошлых сеансов.
        /// </summary>
        private static string BuildInitJson(Document doc, Floor floor,
            List<(DxfImportResult Dxf, Face Face, Dir Dir, string Path)> datasets, Dictionary<string, double> contourMismatch)
        {
            var (outline, openings) = GetFloorOutlineAndOpenings(floor);
            var grids = GetGrids(doc, floor);
            var supports = GetSupports(doc, floor);
            var isolines = SlabGeometry.From(doc, floor).BuildIsolines();
            var placedHistory = RebarZonesDataStore.LoadPlaced(doc, floor);
            string savedZonesJson = RebarZonesDataStore.LoadZonesJson(doc, floor);
            string plate = PlateLabel(floor, doc);
            // Фундаменты — тоже класс Floor (см. RebarZonesWindow), но у них своя логика фоновой
            // арматуры (раздельно верх/низ, по умолчанию ⌀20 вместо ⌀10) — редактору нужно знать
            // об этом сразу при открытии, а не только в момент размещения.
            bool isFoundation = floor.Category != null
                && floor.Category.Id.IntValue() == (int)BuiltInCategory.OST_StructuralFoundation;

            // Полный снимок зон из прошлого сеанса (см. RebarZonesDataStore.SavedZonesJson,
            // window.getStateJSON в rebar_zones.html) — сырой JSON, C# его не разбирает, только
            // встраивает как есть (JsonElement — пройдёт через сериализацию как объект, не строка).
            object savedZones = null;
            if (!string.IsNullOrEmpty(savedZonesJson))
            {
                try { savedZones = JsonSerializer.Deserialize<JsonElement>(savedZonesJson); }
                catch { savedZones = null; }
            }

            // Настраиваемая анкеровка (коэффициент + построчные длины по диаметрам) — общая на
            // весь проект, не на плиту (см. RebarZonesDataStore.LoadAnchorSettingsJson), тоже
            // сырой JSON без разбора на C#.
            object anchor = null;
            string anchorJson = RebarZonesDataStore.LoadAnchorSettingsJson(doc);
            if (!string.IsNullOrEmpty(anchorJson))
            {
                try { anchor = JsonSerializer.Deserialize<JsonElement>(anchorJson); }
                catch { anchor = null; }
            }

            // Глобальный переключатель "Загнутые/Прямые верхние стержни" — тоже общий на весь
            // проект, не на плиту (см. RebarZonesDataStore.LoadTopBendJson), сырой JSON без
            // разбора. Форма конкретной зоны (П/Г/Авто/Прямая) хранится в самой зоне (savedZones),
            // не здесь.
            object bendTop = null;
            string bendTopJson = RebarZonesDataStore.LoadTopBendJson(doc);
            if (!string.IsNullOrEmpty(bendTopJson))
            {
                try { bendTop = JsonSerializer.Deserialize<JsonElement>(bendTopJson); }
                catch { bendTop = null; }
            }

            // Диаметр/шаг фоновой (основной) арматуры — per-floor, у разных плит может отличаться
            // (см. RebarZonesDataStore.LoadBgSettingsJson), сырой JSON без разбора. Без него фон
            // молча сбрасывался на дефолтные 10мм при каждом переоткрытии окна.
            object bg = null;
            string bgJson = RebarZonesDataStore.LoadBgSettingsJson(doc, floor);
            if (!string.IsNullOrEmpty(bgJson))
            {
                try { bg = JsonSerializer.Deserialize<JsonElement>(bgJson); }
                catch { bg = null; }
            }

            var payload = new
            {
                datasets = BuildDatasetsPayload(datasets.Select(d => (d.Dxf, d.Face, d.Dir)), floor),
                floorOutline = outline.Select(p => new[] { Math.Round(M(p.X), 4), Math.Round(M(p.Y), 4) }).ToArray(),
                // Крупные проёмы (≥1000×1000мм) — см. GetFloorOutlineAndOpenings. Нужны редактору,
                // чтобы не рисовать анкеровку "сквозь" проём, где стержень в реальности обрывается.
                openings = openings.Select(op => op.Select(p => new[] { Math.Round(M(p.X), 4), Math.Round(M(p.Y), 4) }).ToArray()).ToArray(),
                // Пилоны/колонны у этой плиты (см. GetSupports) — серым пунктиром на фоне: та же
                // геометрия, что решает П vs Г при загибе (SupportDetector.HasParallelSupport).
                supports = supports.Select(op => op.Select(p => new[] { Math.Round(M(p.X), 4), Math.Round(M(p.Y), 4) }).ToArray()).ToArray(),
                grids = grids.Select(g => new
                {
                    name = g.Name,
                    x1 = Math.Round(M(g.P0.X), 4), y1 = Math.Round(M(g.P0.Y), 4),
                    x2 = Math.Round(M(g.P1.X), 4), y2 = Math.Round(M(g.P1.Y), 4)
                }).ToArray(),
                // Сетка «изолиний» — потенциальные положения доп. стержней (см. SlabGeometry.BuildIsolines),
                // мм → м. Используется в редакторе и для привязки границ зон, и как визуальный фон.
                isolines = new
                {
                    xs = isolines.Xs.Select(v => Math.Round(v / 1000.0, 4)).ToArray(),
                    ys = isolines.Ys.Select(v => Math.Round(v / 1000.0, 4)).ToArray()
                },
                // История уже размещённой (в прошлых сеансах) арматуры по вкладкам — редактор
                // сверяет по габариту+диаметру+шагу (мм → м) и помечает совпавшие зоны как
                // "размещена", см. rebar_zones.html applyPlacedHistory.
                placed = placedHistory.ToDictionary(
                    kv => kv.Key,
                    kv => kv.Value.Select(r => new
                    {
                        x1 = Math.Round(r.X1 / 1000.0, 4), y1 = Math.Round(r.Y1 / 1000.0, 4),
                        x2 = Math.Round(r.X2 / 1000.0, 4), y2 = Math.Round(r.Y2 / 1000.0, 4),
                        d = r.D, step = r.Step
                    }).ToArray()),
                // Полный снимок зон из прошлого сеанса (см. выше) — если есть, редактор
                // восстанавливает его целиком вместо пересчёта с нуля, см. loadState.
                savedZones,
                // Настраиваемая анкеровка из прошлого сеанса (см. выше) — если есть, редактор
                // применяет коэффициент/построчные длины поверх дефолтных 55d, см. loadState.
                anchor,
                // Глобальный переключатель "Загнутые/Прямые верхние стержни" из прошлого сеанса
                // (см. выше) — если есть, редактор восстанавливает его в "⚙ Настройки", см. loadState.
                bendTop,
                // Диаметр/шаг фоновой арматуры из прошлого сеанса (см. выше) — если есть, редактор
                // восстанавливает его поверх дефолтных 10мм/⌀20 для фундамента, см. loadState.
                bg,
                // Фундамент — своя логика фоновой арматуры (верх/низ раздельно, дефолт ⌀20).
                isFoundation,
                // Вкладки, где контур плиты и габарит DXF разошлись более чем на 20% (см.
                // ContourMismatch) — редактор помечает их красным "!" в панели вкладок.
                contourMismatch = contourMismatch.ToDictionary(kv => kv.Key, kv => Math.Round(kv.Value, 1)),
                plate
            };
            return JsonSerializer.Serialize(payload);
        }

        /// <summary>
        /// Компактный JSON для добавления/замены части вкладок В УЖЕ ОТКРЫТОМ окне — см.
        /// window.addDataset в rebar_zones.html и RebarZonesWindow ("📁 DXF"). Не дублирует
        /// floorOutline/grids/isolines/plate (не меняются, уже загружены в JS) и не тащит
        /// savedZones/placed для этих вкладок — только что выбранный DXF считаем свежими данными,
        /// восстанавливать под него старый снимок зон (под другую геометрию) не нужно.
        /// </summary>
        internal static string BuildAddJson(List<(DxfImportResult Dxf, Face Face, Dir Dir)> datasets, Floor floor, out List<string> notes)
        {
            notes = new List<string>();
            var contourMismatch = ComputeContourMismatch(datasets, floor, notes);

            var payload = new
            {
                datasets = BuildDatasetsPayload(datasets, floor),
                contourMismatch = contourMismatch.ToDictionary(kv => kv.Key, kv => Math.Round(kv.Value, 1))
            };
            return JsonSerializer.Serialize(payload);
        }

        private static string AssetFolder()
        {
            string dir = RevitKJChecklist.DependencyResolver.OriginalFolder;
            if (!string.IsNullOrEmpty(dir))
            {
                var candidate = Path.Combine(dir, "RebarZones");
                if (File.Exists(Path.Combine(candidate, "rebar_zones.html"))) return candidate;
            }
            try
            {
                string loc = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                var candidate = Path.Combine(loc ?? "", "RebarZones");
                if (File.Exists(Path.Combine(candidate, "rebar_zones.html"))) return candidate;
            }
            catch { }
            return null;
        }

        private sealed class FloorFilter : ISelectionFilter
        {
            public bool AllowElement(Element elem) => elem is Floor;
            public bool AllowReference(Reference reference, XYZ position) => false;
        }
    }
}
