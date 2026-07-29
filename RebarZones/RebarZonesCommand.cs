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
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
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

            var ofd = new Microsoft.Win32.OpenFileDialog
            {
                Title = "DXF мозаики армирования (ЛИРА)",
                Filter = "DXF файлы (*.dxf)|*.dxf|Все файлы (*.*)|*.*"
            };
            if (ofd.ShowDialog() != true) return Result.Cancelled;

            DxfImportResult dxf;
            try { dxf = DxfArmoringReader.Read(ofd.FileName); }
            catch (IOException)
            {
                TaskDialog.Show("Файл занят",
                    "Файл \"" + ofd.FileName + "\" сейчас открыт другой программой (AutoCAD, "
                    + "текстовый редактор, просмотрщик и т.п.) с монопольным доступом.\n\n"
                    + "Закройте программу, которая держит файл, и повторите попытку.");
                return Result.Failed;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Ошибка импорта DXF", ex.ToString());
                return Result.Failed;
            }
            if (dxf.Cells.Count == 0)
            {
                TaskDialog.Show("Импорт DXF", "В файле не найдено ячеек (3DFACE на слое layer_elements).");
                return Result.Failed;
            }

            Face face; Dir dir;
            if (dxf.Face.HasValue && dxf.Dir.HasValue)
            {
                // легенда найдена и разобрана — доверяем ей, без лишнего диалога
                face = dxf.Face.Value;
                dir = dxf.Dir.Value;
            }
            else
            {
                // легенды нет или формат непривычный (другая версия ЛИРА) — спрашиваем явно
                TaskDialog.Show("Легенда не найдена",
                    "В DXF не нашлось текста легенды («...по оси X у верхней грани» и т.п.) — "
                    + "укажите грань и направление вручную.");
                if (!AskFaceDir(out face, out dir)) return Result.Cancelled;
            }

            string assetFolder = AssetFolder();
            if (assetFolder == null)
            {
                TaskDialog.Show("Ошибка", "Не найден RebarZones\\rebar_zones.html рядом с плагином.");
                return Result.Failed;
            }

            var offset = ComputeOffset(floor, dxf);
            var outline = GetFloorOutline(floor);
            var grids = GetGrids(doc);
            var slab = SlabGeometry.From(doc, floor);
            var isolines = slab.BuildIsolines();
            string plateLabel = PlateLabel(dxf);
            string json = BuildInitJson(dxf, face, dir, plateLabel, offset, outline, grids, isolines);

            var bridge = new RevitEventBridge();
            bridge.Init();

            var win = new RebarZonesWindow(uiApp.MainWindowHandle, assetFolder, json, bridge, floor.Id);
            win.Show();

            return Result.Succeeded;
        }

        private static double M(double ft) => UnitUtils.ConvertFromInternalUnits(ft, UnitTypeId.Meters);

        private static string PlateLabel(DxfImportResult dxf)
        {
            double m = M(dxf.Cells[0].Polygon[0].Z);
            return "Плита " + (m >= 0 ? "+" : "") + m.ToString("0.000", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// DXF из ЛИРА не обязательно в тех же координатах, что модель Revit (разные базовые
        /// точки/начала координат при экспорте). Выравниваем по центру габарита: сдвигаем всю
        /// сетку ячеек так, чтобы её центр совпал с центром габарита выбранной плиты. Поворот
        /// не учитывается — предполагается, что оси X/Y ЛИРА и Revit сонаправлены.
        /// </summary>
        private static (double Dx, double Dy) ComputeOffset(Floor floor, DxfImportResult dxf)
        {
            var bb = floor.get_BoundingBox(null);
            double floorCx = (bb.Min.X + bb.Max.X) / 2.0;
            double floorCy = (bb.Min.Y + bb.Max.Y) / 2.0;

            double minX = dxf.Cells.Min(c => c.Polygon.Min(p => p.X));
            double maxX = dxf.Cells.Max(c => c.Polygon.Max(p => p.X));
            double minY = dxf.Cells.Min(c => c.Polygon.Min(p => p.Y));
            double maxY = dxf.Cells.Max(c => c.Polygon.Max(p => p.Y));

            return (floorCx - (minX + maxX) / 2.0, floorCy - (minY + maxY) / 2.0);
        }

        /// <summary>Внешний контур верхней грани плиты — визуальный ориентир на фоне редактора.</summary>
        private static List<XYZ> GetFloorOutline(Floor floor)
        {
            var opt = new Options { ComputeReferences = false, DetailLevel = ViewDetailLevel.Coarse };
            List<XYZ> best = null;
            double bestArea = -1;

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
                        if (pts.Count < 3) continue;
                        double area = (pts.Max(p => p.X) - pts.Min(p => p.X)) * (pts.Max(p => p.Y) - pts.Min(p => p.Y));
                        if (area > bestArea) { bestArea = area; best = pts; }
                    }
                }
            }
            return best ?? new List<XYZ>();
        }

        /// <summary>Оси (Grid) проекта — визуальный фон в редакторе, для наглядности расположения зон.</summary>
        private static List<(string Name, XYZ P0, XYZ P1)> GetGrids(Document doc)
        {
            return new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>()
                .Where(g => g.Curve is Line)
                .Select(g => (g.Name, ((Line)g.Curve).GetEndPoint(0), ((Line)g.Curve).GetEndPoint(1)))
                .ToList();
        }

        private static string BuildInitJson(DxfImportResult dxf, Face face, Dir dir, string plate,
            (double Dx, double Dy) offset, List<XYZ> floorOutline, List<(string Name, XYZ P0, XYZ P1)> grids,
            (List<double> Xs, List<double> Ys) isolines)
        {
            var payload = new
            {
                cells = dxf.Cells.Select(c => new
                {
                    p = c.Polygon.Select(pt => new[] { Math.Round(M(pt.X + offset.Dx), 4), Math.Round(M(pt.Y + offset.Dy), 4) }).ToArray(),
                    x = Math.Round(M(c.X + offset.Dx), 4),
                    y = Math.Round(M(c.Y + offset.Dy), 4),
                    a = c.HasValue ? c.As : 0
                }).ToArray(),
                floorOutline = floorOutline.Select(p => new[] { Math.Round(M(p.X), 4), Math.Round(M(p.Y), 4) }).ToArray(),
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
                face = face == Face.Bottom ? "bottom" : "top",
                dir = dir == Dir.Y ? "y" : "x",
                plate
            };
            return JsonSerializer.Serialize(payload);
        }

        private static bool AskFaceDir(out Face face, out Dir dir)
        {
            var td = new TaskDialog("Направление и грань")
            {
                MainInstruction = "Какому DXF-файлу соответствует эта мозаика?",
                MainContent = "Экспорт ЛИРА содержит As только для ОДНОЙ комбинации грани плиты и направления стержней.",
                CommonButtons = TaskDialogCommonButtons.Cancel
            };
            td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Верх · X");
            td.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Верх · Y");
            td.AddCommandLink(TaskDialogCommandLinkId.CommandLink3, "Низ · X");
            td.AddCommandLink(TaskDialogCommandLinkId.CommandLink4, "Низ · Y");

            switch (td.Show())
            {
                case TaskDialogResult.CommandLink1: face = Face.Top; dir = Dir.X; return true;
                case TaskDialogResult.CommandLink2: face = Face.Top; dir = Dir.Y; return true;
                case TaskDialogResult.CommandLink3: face = Face.Bottom; dir = Dir.X; return true;
                case TaskDialogResult.CommandLink4: face = Face.Bottom; dir = Dir.Y; return true;
                default: face = Face.Top; dir = Dir.X; return false;
            }
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
