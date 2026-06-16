using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

namespace DAN_Plugin
{
    public class AssemblySelectionFilter : ISelectionFilter
    {
        public bool AllowElement(Element elem) => elem is AssemblyInstance;
        public bool AllowReference(Reference reference, XYZ position) => false;
    }


    [Transaction(TransactionMode.Manual)]
    public class CreatElevationTags : IExternalCommand
    {
        private class FaceData
        {
            public Element Elem { get; set; }
            public Reference FaceRef { get; set; }
            public XYZ FacePoint { get; set; }
            public double LeftProj { get; set; }
            public double RightProj { get; set; }
            public bool IsWall { get; set; }
            public bool IsTop { get; set; }
            public double Z => FacePoint.Z;
        }

        private const double ZTolerance = 1.0 / 304.8;
        private double hiddenZoneBottom = double.MinValue;
        private double hiddenZoneTop = double.MaxValue;
        private double globalMinRight = double.MaxValue;
        private double globalMaxRight = double.MinValue;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uiDoc = commandData.Application.ActiveUIDocument;
            Document doc = uiDoc.Document;
            ViewSection section = doc.ActiveView as ViewSection;

            if (section == null)
            {
                TaskDialog.Show("Ошибка", "Активный вид должен быть разрезом.");
                return Result.Failed;
            }

            // Шаг 1: выбор сборки стен
            AssemblyInstance assembly = null;
            try
            {
                Reference pickedRef = uiDoc.Selection.PickObject(
                    ObjectType.Element,
                    new AssemblySelectionFilter(),
                    "Выберите сборку стен");
                assembly = doc.GetElement(pickedRef) as AssemblyInstance;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled;
            }

            if (assembly == null)
            {
                TaskDialog.Show("Ошибка", "Не удалось получить сборку.");
                return Result.Failed;
            }

            // Шаг 2: окно настроек
            var settingsWindow = new SettingsWindow();
            if (settingsWindow.ShowDialog() != true)
                return Result.Cancelled;

            bool createBreak = settingsWindow.CreateBreak;
            bool recreate = settingsWindow.Recreate;
            bool createSections = settingsWindow.CreateSections;
            List<BreakRange> breakRanges = settingsWindow.BreakRanges;


            // Шаг 3: пересоздание — удаляем существующие разрывы, линии разрыва, отметки и размеры
            if (recreate)
            {
                using (Transaction txRemove = new Transaction(doc, "Удалить разрывы и аннотации"))
                {
                    txRemove.Start();
                    try
                    {
                        // Удаляем разрывы вида
                        var mgrRemove = section.GetCropRegionShapeManager();
                        if (mgrRemove.Split)
                            mgrRemove.RemoveSplit();

                        // Удаляем линии разрыва
                        var breakLineIds = new FilteredElementCollector(doc, section.Id)
                            .OfClass(typeof(FamilyInstance))
                            .Cast<FamilyInstance>()
                            .Where(fi => fi.Symbol.Family.Name.Equals(
                                "(Оформление) Линия разрыва", StringComparison.OrdinalIgnoreCase))
                            .Select(fi => fi.Id)
                            .ToList();
                        foreach (var id in breakLineIds)
                            doc.Delete(id);

                        // Удаляем высотные отметки типов BI_стрелка_проектная_вверх/вниз
                        var spotIds = new FilteredElementCollector(doc, section.Id)
                            .OfClass(typeof(SpotDimension))
                            .Cast<SpotDimension>()
                            .Where(sd =>
                                sd.SpotDimensionType.Name.Equals("BI_стрелка_проектная_вверх", StringComparison.OrdinalIgnoreCase) ||
                                sd.SpotDimensionType.Name.Equals("BI_стрелка_проектная_вниз", StringComparison.OrdinalIgnoreCase))
                            .Select(sd => sd.Id)
                            .ToList();
                        foreach (var id in spotIds)
                            doc.Delete(id);

                        // Удаляем размеры типа BI_основной_2,5мм
                        var dimIds = new FilteredElementCollector(doc, section.Id)
                            .OfClass(typeof(Dimension))
                            .Cast<Dimension>()
                            .Where(d => d.DimensionType?.Name.Equals(
                                "BI_основной_2,5мм", StringComparison.OrdinalIgnoreCase) == true)
                            .Select(d => d.Id)
                            .ToList();
                        foreach (var id in dimIds)
                            doc.Delete(id);

                        // Удаляем ранее созданные виды узлов этой сборки
                        // (тип вида Detail и имя с префиксом "{комментарий}_Разрез_"),
                        // чтобы нумерация имён начиналась заново с 1-1.
                        string asmComment = assembly
                            .get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString()
                            ?? assembly.Name;
                        string viewPrefix = $"{asmComment}_Разрез_";
                        var oldViewIds = new FilteredElementCollector(doc)
                            .OfClass(typeof(ViewSection))
                            .Cast<ViewSection>()
                            .Where(v => !v.IsTemplate
                                && v.ViewType == ViewType.Detail
                                && v.Name.StartsWith(viewPrefix, StringComparison.OrdinalIgnoreCase))
                            .Select(v => v.Id)
                            .ToList();

                        foreach (var id in oldViewIds)
                        {
                            try { doc.Delete(id); } catch { }
                        }

                        txRemove.Commit();
                    }
                    catch { txRemove.RollBack(); }
                }
            }

            // Шаг 3: разрывы вида
            double elevationOffset = 0;
            Level anyLevel = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .FirstOrDefault();
            if (anyLevel != null)
                elevationOffset = anyLevel.ProjectElevation - anyLevel.Elevation;

            var mgr = section.GetCropRegionShapeManager();
            if (createBreak && mgr.CanBeSplit && breakRanges.Any())
            {
                BoundingBoxXYZ cropBox = section.CropBox;
                Transform t = cropBox.Transform;
                double worldBottomZ = Math.Min(t.OfPoint(cropBox.Min).Z, t.OfPoint(cropBox.Max).Z);
                double worldTopZ = Math.Max(t.OfPoint(cropBox.Min).Z, t.OfPoint(cropBox.Max).Z);
                double worldHeight = worldTopZ - worldBottomZ;

                // Переводим ВСЕ диапазоны в доли полного бокса [0..1].
                // Сортируем сверху вниз, чтобы при разбиении регионов индексы
                // нижних регионов не смещались до их обработки.
                var fractions = breakRanges
                    .Select(r =>
                    {
                        double wb = UnitUtils.ConvertToInternalUnits(r.BottomMm, UnitTypeId.Millimeters);
                        double wt = UnitUtils.ConvertToInternalUnits(r.TopMm, UnitTypeId.Millimeters);
                        double a = (wb + elevationOffset - worldBottomZ) / worldHeight;
                        double b = (wt + elevationOffset - worldBottomZ) / worldHeight;
                        return (fLow: Math.Min(a, b), fHigh: Math.Max(a, b));
                    })
                    .Where(f => f.fHigh > 0.001 && f.fLow < 0.999 && (f.fHigh - f.fLow) > 0.001)
                    .OrderByDescending(f => f.fLow)
                    .ToList();

                if (fractions.Any())
                {
                    using (Transaction txBreak = new Transaction(doc, "Создать разрывы вида"))
                    {
                        txBreak.Start();
                        try
                        {
                            foreach (var (fLow, fHigh) in fractions)
                            {
                                double mid = (fLow + fHigh) / 2.0;

                                // Регион, в который попадает разрыв (доли — относительно ПОЛНОГО бокса)
                                int idx = 0;
                                double rMin = 0.0, rMax = 1.0;

                                if (mgr.Split)
                                {
                                    idx = -1;
                                    int n = mgr.NumberOfSplitRegions;
                                    for (int i = 0; i < n; i++)
                                    {
                                        double a = mgr.GetSplitRegionMinimum(i);
                                        double b = mgr.GetSplitRegionMaximum(i);
                                        if (mid > a && mid < b) { idx = i; rMin = a; rMax = b; break; }
                                    }
                                    if (idx < 0) continue; // разрыв попал в зазор существующего — пропускаем
                                }

                                double span = rMax - rMin;
                                if (span <= 1e-6) continue;

                                // Параметры SplitRegionVertically — доли ВНУТРИ разбиваемого региона
                                double relLow = Math.Max(0.001, Math.Min(0.999, (fLow - rMin) / span));
                                double relHigh = Math.Max(0.001, Math.Min(0.999, (fHigh - rMin) / span));
                                if (relLow >= relHigh) continue;

                                mgr.SplitRegionVertically(idx, relLow, relHigh);
                            }

                            if (!section.CropBoxActive) section.CropBoxActive = true;
                            if (!section.CropBoxVisible) section.CropBoxVisible = true;
                            txBreak.Commit();
                        }
                        catch
                        {
                            if (txBreak.GetStatus() == TransactionStatus.Started)
                                txBreak.RollBack();
                        }
                    }
                }
            }

            // Шаг 3: типы высотных отметок
            SpotDimensionType spotTypeUp = new FilteredElementCollector(doc)
                .OfClass(typeof(SpotDimensionType)).Cast<SpotDimensionType>()
                .FirstOrDefault(t => t.StyleType == DimensionStyleType.SpotElevation &&
                    t.Name.Equals("BI_стрелка_проектная_вверх", StringComparison.OrdinalIgnoreCase));

            SpotDimensionType spotTypeDown = new FilteredElementCollector(doc)
                .OfClass(typeof(SpotDimensionType)).Cast<SpotDimensionType>()
                .FirstOrDefault(t => t.StyleType == DimensionStyleType.SpotElevation &&
                    t.Name.Equals("BI_стрелка_проектная_вниз", StringComparison.OrdinalIgnoreCase));

            if (spotTypeUp == null) TaskDialog.Show("Предупреждение", "Тип \"BI_стрелка_проектная_вверх\" не найден.");
            if (spotTypeDown == null) TaskDialog.Show("Предупреждение", "Тип \"BI_стрелка_проектная_вниз\" не найден.");

            // Шаг 4: сбор элементов
            var visibleIds = new FilteredElementCollector(doc, section.Id)
                .WherePasses(new LogicalOrFilter(
                    new ElementClassFilter(typeof(Floor)),
                    new ElementClassFilter(typeof(Wall))))
                .WhereElementIsNotElementType()
                .ToElementIds()
                .ToHashSet();

            List<Element> walls = assembly.GetMemberIds()
                .Select(id => doc.GetElement(id))
                .Where(e => e is Wall && visibleIds.Contains(e.Id))
                .ToList();

            if (!walls.Any())
            {
                TaskDialog.Show("Информация", $"В сборке \"{assembly.Name}\" не найдено стен на разрезе.");
                return Result.Succeeded;
            }

            List<Element> floors = new FilteredElementCollector(doc, section.Id)
                .OfClass(typeof(Floor))
                .WhereElementIsNotElementType()
                .Cast<Floor>()
                .Where(f =>
                {
                    ElementId asmId = f.AssemblyInstanceId;
                    if (asmId == null || asmId == ElementId.InvalidElementId) return false;
                    AssemblyInstance asm = doc.GetElement(asmId) as AssemblyInstance;
                    if (asm == null) return false;
                    string comment = asm.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() ?? string.Empty;
                    return comment.StartsWith("Пм");
                })
                .Cast<Element>()
                .ToList();

            XYZ rightVec = section.RightDirection.Normalize();
            XYZ viewDir = section.ViewDirection.Normalize();

            double ProjDepth(XYZ pt) => pt.DotProduct(viewDir);

            // Вычисляем все скрытые зоны из mgr
            hiddenZoneBottom = double.MinValue;
            hiddenZoneTop = double.MaxValue;
            var hiddenZones = new List<(double bottom, double top)>();

            var mgrCheck = section.GetCropRegionShapeManager();
            if (mgrCheck.Split && mgrCheck.NumberOfSplitRegions >= 2)
            {
                BoundingBoxXYZ cb = section.CropBox;
                Transform ct = cb.Transform;
                double wBottomZ = Math.Min(ct.OfPoint(cb.Min).Z, ct.OfPoint(cb.Max).Z);
                double wTopZ = Math.Max(ct.OfPoint(cb.Min).Z, ct.OfPoint(cb.Max).Z);
                double wHeight = wTopZ - wBottomZ;

                int nRegions = mgrCheck.NumberOfSplitRegions;
                for (int i = 0; i < nRegions - 1; i++)
                {
                    double localBottom = mgrCheck.GetSplitRegionMaximum(i);
                    double localTop = mgrCheck.GetSplitRegionMinimum(i + 1);
                    hiddenZones.Add((wBottomZ + localBottom * wHeight, wBottomZ + localTop * wHeight));
                }

                if (hiddenZones.Any())
                {
                    hiddenZoneBottom = hiddenZones.Min(z => z.bottom);
                    hiddenZoneTop = hiddenZones.Max(z => z.top);
                }
            }

            // Шаг 5: сбор граней с фильтрацией по скрытой зоне
            var allFaces = new List<FaceData>();
            globalMinRight = double.MaxValue;
            globalMaxRight = double.MinValue;
            double refDepth = 0;
            int depthCnt = 0;

            foreach (Element elem in walls.Concat(floors))
            {
                bool isWall = elem is Wall;

                // Кривая стена (дуга, сплайн) — пропускаем: горизонтальные грани могут быть
                // получены некорректно, а нужные Z-уровни уже покрыты смежными плитами.
                if (isWall)
                {
                    LocationCurve lc = (elem as Wall).Location as LocationCurve;
                    if (!(lc?.Curve is Line))
                        continue;
                }

                GetTopAndBottomFaces(elem, section, rightVec, out FaceData topFace, out FaceData botFace);

                foreach (FaceData fd in new[] { topFace, botFace })
                {
                    if (fd == null) continue;
                    // Пропускаем грани попадающие в любую из скрытых зон
                    if (hiddenZones.Any(z => fd.Z > z.bottom && fd.Z < z.top)) continue;

                    fd.IsWall = isWall;
                    TryAddFace(allFaces, fd);
                    refDepth += ProjDepth(fd.FacePoint); depthCnt++;
                    if (isWall && fd.LeftProj < globalMinRight) globalMinRight = fd.LeftProj;
                    if (isWall && fd.RightProj > globalMaxRight) globalMaxRight = fd.RightProj;
                }
            }

            if (!allFaces.Any())
            {
                TaskDialog.Show("Информация", "Не удалось получить геометрию элементов.");
                return Result.Succeeded;
            }

            if (globalMinRight == double.MaxValue) globalMinRight = allFaces.Min(f => f.LeftProj);
            if (depthCnt > 0) refDepth /= depthCnt;

            allFaces = allFaces.OrderBy(f => f.Z).ToList();
            var oddFaces = allFaces.Where((_, idx) => idx % 2 == 0).ToList();

            HashSet<double> existingZs = GetExistingSpotElevationZs(doc, section);

            double bendGap = UnitUtils.ConvertToInternalUnits(200, UnitTypeId.Millimeters);
            double dimProj = globalMinRight - UnitUtils.ConvertToInternalUnits(1000, UnitTypeId.Millimeters);
            double dimOddProj = globalMinRight - UnitUtils.ConvertToInternalUnits(1350, UnitTypeId.Millimeters);
            double dimTotalProj = globalMinRight - UnitUtils.ConvertToInternalUnits(1700, UnitTypeId.Millimeters);
            double tagProj = globalMinRight - UnitUtils.ConvertToInternalUnits(1700, UnitTypeId.Millimeters);

            XYZ MakePoint(double rightProj, double depth, double z) =>
                rightVec.Multiply(rightProj) + viewDir.Multiply(depth) + XYZ.BasisZ.Multiply(z);

            int createdCount = 0;
            int skippedCount = 0;
            int openingsFound = 0;

            using (Transaction tx = new Transaction(doc, "Создать высотные отметки и размеры"))
            {
                tx.Start();

                // Удаляем высотные отметки, попавшие в зоны разрыва после его создания
                if (hiddenZones.Count > 0)
                {
                    var inHiddenZoneIds = new FilteredElementCollector(doc, section.Id)
                        .OfClass(typeof(SpotDimension))
                        .Cast<SpotDimension>()
                        .Where(sd =>
                            (sd.SpotDimensionType.Name.Equals("BI_стрелка_проектная_вверх", StringComparison.OrdinalIgnoreCase) ||
                             sd.SpotDimensionType.Name.Equals("BI_стрелка_проектная_вниз", StringComparison.OrdinalIgnoreCase)) &&
                            hiddenZones.Any(hz => sd.Origin.Z > hz.bottom && sd.Origin.Z < hz.top))
                        .Select(sd => sd.Id)
                        .ToList();
                    foreach (var id in inHiddenZoneIds)
                        try { doc.Delete(id); } catch { }

                    // Синхронизируем existingZs — убираем удалённые отметки
                    existingZs.RemoveWhere(z => hiddenZones.Any(hz => z > hz.bottom && z < hz.top));
                }

                for (int i = 0; i < allFaces.Count; i++)
                {
                    FaceData fd = allFaces[i];
                    if (ExistsAtZ(existingZs, fd.Z)) { skippedCount++; continue; }

                    SpotDimensionType typeToUse;
                    if (fd.IsWall)
                        // Стены: верх → стрелка вниз, низ → стрелка вверх
                        typeToUse = fd.IsTop
                            ? (spotTypeDown ?? spotTypeUp)
                            : (spotTypeUp ?? spotTypeDown);
                    else
                        // Плиты: всегда стрелка вверх
                        typeToUse = spotTypeUp ?? spotTypeDown;

                    try
                    {
                        double z = fd.Z;
                        double depth = ProjDepth(fd.FacePoint);

                        double originProj = fd.IsWall
                            ? fd.FacePoint.DotProduct(rightVec)
                            : Math.Max(globalMinRight, fd.LeftProj);

                        XYZ origin = MakePoint(originProj, depth, z);
                        XYZ bend = MakePoint(tagProj - bendGap, depth, z);
                        XYZ end = MakePoint(tagProj - bendGap * 2, depth, z);

                        SpotDimension spotDim = doc.Create.NewSpotElevation(
                            section, fd.FaceRef, origin, bend, end, origin, true);

                        if (spotDim != null)
                        {
                            if (typeToUse != null) spotDim.ChangeTypeId(typeToUse.Id);
                            createdCount++;
                            existingZs.Add(Math.Round(z, 6));
                        }
                    }
                    catch (Exception ex)
                    {
                        TaskDialog.Show("Предупреждение", $"Элемент Id={fd.Elem.Id}: {ex.Message}");
                    }
                }

                // Удаляем существующие размеры перед пересозданием (предотвращает дублирование)
                foreach (var dId in new FilteredElementCollector(doc, section.Id)
                    .OfClass(typeof(Dimension)).Cast<Dimension>()
                    .Where(d => d.DimensionType?.Name.Equals("BI_основной_2,5мм", StringComparison.OrdinalIgnoreCase) == true)
                    .Select(d => d.Id).ToList())
                    try { doc.Delete(dId); } catch { }

                if (allFaces.Count >= 2)
                    try { CreateDimensionChain(doc, section, allFaces, dimProj, refDepth, rightVec, viewDir); }
                    catch (Exception ex) { TaskDialog.Show("Предупреждение (размеры)", ex.Message); }

                if (oddFaces.Count >= 2)
                    try { CreateDimensionChain(doc, section, oddFaces, dimOddProj, refDepth, rightVec, viewDir); }
                    catch (Exception ex) { TaskDialog.Show("Предупреждение (нечётные)", ex.Message); }

                // Общий размер от самой нижней до самой верхней грани
                if (allFaces.Count >= 2)
                {
                    var totalFaces = new List<FaceData> { allFaces.First(), allFaces.Last() };
                    try { CreateDimensionChain(doc, section, totalFaces, dimTotalProj, refDepth, rightVec, viewDir); }
                    catch (Exception ex) { TaskDialog.Show("Предупреждение (общий размер)", ex.Message); }
                }

                // Размещаем линии разрыва для всех скрытых зон
                if (mgrCheck.Split && mgrCheck.NumberOfSplitRegions >= 2)
                    try { CreateBreakLineAnnotations(doc, section, rightVec, viewDir, hiddenZones); }
                    catch (Exception ex) { TaskDialog.Show("Предупреждение (линии разрыва)", ex.Message); }

                // Размещаем линии разрыва на плитах только если плита не в скрытой зоне
                try { CreateFloorBreakLineAnnotations(doc, section, rightVec, viewDir, floors, hiddenZones); }
                catch (Exception ex) { TaskDialog.Show("Предупреждение (линии плит)", ex.Message); }

                // Два горизонтальных размера внизу разреза:
                //   размер 1 (нижний) — длина стены (торец↔торец);
                //   размер 2 (над ним) — привязка к осям (торец→оси→торец).
                Wall dimWall = walls.OfType<Wall>()
                    .OrderBy(w =>
                    {
                        BoundingBoxXYZ bb = w.get_BoundingBox(null);
                        return bb != null ? bb.Min.Z : 0.0;
                    })
                    .FirstOrDefault();
                if (dimWall != null && allFaces.Any())
                {
                    double baseZ = allFaces.First().Z;   // самая нижняя грань
                    try { CreateWallPlanDimensions(doc, section, dimWall, rightVec, viewDir, baseZ); }
                    catch (Exception ex) { TaskDialog.Show("Предупреждение (размеры низ)", ex.Message); }
                }

                // Размеры проёмов: для каждой видимой стены с проёмом — две цепочки
                // (высота + привязка к низу стены; ширина + привязка к оси/торцу)
                foreach (Wall w in walls.OfType<Wall>())
                {
                    try { openingsFound += CreateOpeningDimensions(doc, section, w, rightVec, viewDir, hiddenZones); }
                    catch (Exception ex) { TaskDialog.Show("Предупреждение (проёмы)", ex.Message); }
                }

                tx.Commit();
            }

            string assemblyComment = assembly
                .get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() ?? assembly.Name;

            string resultMsg = "";

            // Несколько видов узлов — по одному на каждую смену толщины стены.
            // Стены сборки сортируются снизу вверх; вид создаётся на самой нижней
            // стене (обязательно) и далее на каждой стене, чья толщина отличается
            // от нижележащей. Толщина берётся из типа стены (параметр "Толщина").
            double thkTol = UnitUtils.ConvertToInternalUnits(1, UnitTypeId.Millimeters);

            double WallBottomZ(Wall w)
            {
                BoundingBoxXYZ bb = w.get_BoundingBox(null);
                return bb != null ? bb.Min.Z : 0.0;
            }
            double WallThickness(Wall w)
            {
                try { return w.WallType.Width; }            // параметр "Толщина" типа стены
                catch { try { return w.Width; } catch { return 0.0; } }
            }

            // ВАЖНО: берём ВСЕ стены сборки, а не отфильтрованный по видимости
            // список walls (тот обрезан подрезкой/разрывами активного вида и может
            // содержать только нижние этажи). Иначе смена толщины не обнаружится.
            List<Wall> assemblyWalls = assembly.GetMemberIds()
                .Select(id => doc.GetElement(id))
                .OfType<Wall>()
                .ToList();

            // Геометрический ключ стены: "толщинаМм|Ш1xВ1,Ш2xВ2,..." (проёмы отсортированы).
            // Новый разрез нужен при любом отличии ключа — изменилась толщина, количество
            // проёмов или размеры хотя бы одного из них.
            string WallGeomKey(Wall w)
            {
                int thkMm = (int)Math.Round(
                    UnitUtils.ConvertFromInternalUnits(WallThickness(w), UnitTypeId.Millimeters));

                // Опорная точка стены: левый нижний угол (для относительных координат проёмов)
                BoundingBoxXYZ wallBb = w.get_BoundingBox(null);
                double wallMinR = (wallBb != null)
                    ? new[] { wallBb.Min.X, wallBb.Max.X }
                        .SelectMany(bx => new[] { wallBb.Min.Y, wallBb.Max.Y }
                            .Select(by => new XYZ(bx, by, 0).DotProduct(rightVec)))
                        .Min()
                    : 0.0;
                double wallBotZ  = wallBb?.Min.Z ?? 0.0;
                double wallHeight = wallBb != null ? wallBb.Max.Z - wallBb.Min.Z : double.MaxValue;

                var openings = new List<(double minR, double maxR, double botZ, double topZ)>();

                // Проверяем оба режима геометрии:
                // — без вида: SolidSolidCutUtils-резы и hosted-семейства всегда видны
                // — с видом: профильные проёмы (OpeningBySketch) видны только view-dependent
                // Результаты объединяем; дубликаты уберёт деdup по позиции ниже.
                var geomSources = new List<GeometryElement>();
                GeometryElement geomNoView = w.get_Geometry(new Options { ComputeReferences = false });
                if (geomNoView != null) geomSources.Add(geomNoView);
                GeometryElement geomWithView = w.get_Geometry(new Options { ComputeReferences = false, View = section });
                if (geomWithView != null) geomSources.Add(geomWithView);
                foreach (GeometryElement geomSrc in geomSources)
                {
                    foreach (GeometryObject go in geomSrc)
                    {
                        Solid s = go as Solid;
                        if (s == null || s.Faces.IsEmpty) continue;
                        foreach (Face f in s.Faces)
                        {
                            PlanarFace pf = f as PlanarFace;
                            if (pf == null) continue;
                            if (Math.Abs(pf.FaceNormal.Z) > 1e-3) continue;
                            // Пропускаем только торцы стены (норм. параллельна длине стены = rightVec);
                            // проверяем и переднюю, и заднюю грань (дубликаты уберёт deduplicate ниже).
                            if (Math.Abs(pf.FaceNormal.DotProduct(rightVec)) > 0.7) continue;

                            IList<CurveLoop> loops;
                            try { loops = pf.GetEdgesAsCurveLoops(); } catch { continue; }
                            if (loops == null || loops.Count < 2) continue;

                            // Внешний контур — с наибольшей площадью (ширина × высота)
                            var boxes = loops.Select(loop =>
                            {
                                double mn = double.MaxValue, mx = double.MinValue;
                                double bz = double.MaxValue, tz = double.MinValue;
                                foreach (Curve c in loop)
                                    foreach (XYZ p in c.Tessellate())
                                    {
                                        double r = p.DotProduct(rightVec);
                                        if (r < mn) mn = r; if (r > mx) mx = r;
                                        if (p.Z < bz) bz = p.Z; if (p.Z > tz) tz = p.Z;
                                    }
                                return (mn, mx, bz, tz);
                            }).ToList();

                            int outerIdx = -1; double maxArea = double.MinValue;
                            for (int li = 0; li < boxes.Count; li++)
                            {
                                double a = (boxes[li].mx - boxes[li].mn) * (boxes[li].tz - boxes[li].bz);
                                if (a > maxArea) { maxArea = a; outerIdx = li; }
                            }

                            double outerMinR = boxes[outerIdx].mn;
                            double outerMaxR = boxes[outerIdx].mx;
                            // Допуск: 30 мм — колонны/стены примыкают вплотную к торцу
                            double edgeTol = UnitUtils.ConvertToInternalUnits(30, UnitTypeId.Millimeters);

                            for (int li = 0; li < boxes.Count; li++)
                            {
                                if (li == outerIdx) continue;
                                // Артефакт примыкания: inner loop прижат к левому или правому
                                // торцу внешнего контура (колонна/стена срезает грань по краю).
                                // Реальные проёмы (окна, двери, профильные вырезы) расположены
                                // внутри грани и не касаются горизонтальных краёв.
                                bool touchesLeft  = Math.Abs(boxes[li].mn - outerMinR) < edgeTol;
                                bool touchesRight = Math.Abs(boxes[li].mx - outerMaxR) < edgeTol;
                                if (touchesLeft || touchesRight) continue;
                                openings.Add((boxes[li].mn, boxes[li].mx, boxes[li].bz, boxes[li].tz));
                            }
                        }
                    }
                }

                // Детектирование через зависимые элементы (wall-hosted Generic Models)
                try
                {
                    var gmFilter = new ElementCategoryFilter(BuiltInCategory.OST_GenericModel);
                    foreach (ElementId depId in w.GetDependentElements(gmFilter))
                    {
                        FamilyInstance fi = doc.GetElement(depId) as FamilyInstance;
                        if (fi == null) continue;
                        BoundingBoxXYZ fiBb = fi.get_BoundingBox(null);
                        if (fiBb == null) continue;
                        double minR2 = double.MaxValue, maxR2 = double.MinValue;
                        foreach (XYZ corner in new[] {
                            new XYZ(fiBb.Min.X, fiBb.Min.Y, 0), new XYZ(fiBb.Max.X, fiBb.Min.Y, 0),
                            new XYZ(fiBb.Min.X, fiBb.Max.Y, 0), new XYZ(fiBb.Max.X, fiBb.Max.Y, 0) })
                        {
                            double r = corner.DotProduct(rightVec);
                            if (r < minR2) minR2 = r;
                            if (r > maxR2) maxR2 = r;
                        }
                        openings.Add((minR2, maxR2, fiBb.Min.Z, fiBb.Max.Z));
                    }
                }
                catch { }

                // Дедупликация по горизонтальной позиции (допуск 100 мм):
                // один и тот же проём детектируется из нескольких источников / граней.
                var dedupedOpenings = openings
                    .GroupBy(o => (
                        (int)Math.Round(UnitUtils.ConvertFromInternalUnits(o.minR - wallMinR, UnitTypeId.Millimeters) / 100.0),
                        (int)Math.Round(UnitUtils.ConvertFromInternalUnits(o.maxR - wallMinR, UnitTypeId.Millimeters) / 100.0)
                    ))
                    .Select(g => g.OrderByDescending(o => o.topZ - o.botZ).First())
                    .ToList();

                if (!dedupedOpenings.Any())
                    return $"{thkMm}";

                var sigs = dedupedOpenings.Select(o =>
                {
                    int posL = (int)Math.Round(UnitUtils.ConvertFromInternalUnits(o.minR - wallMinR, UnitTypeId.Millimeters));
                    int wid  = (int)Math.Round(UnitUtils.ConvertFromInternalUnits(o.maxR - o.minR, UnitTypeId.Millimeters));
                    return $"{posL},{wid}";
                }).OrderBy(s => s).ToList();

                return $"{thkMm}|{string.Join(";", sigs)}";
            }

            // Группируем стены по этажу (одинаковая нижняя отметка ±50мм) и сортируем по Z.
            // Разрез создаётся на группу (этаж), а не на отдельную стену:
            // если суммарный ключ группы изменился — новый разрез, иначе — ссылочный.
            // isNew=true: создаём новый вид с индексом sectionIdx (репрезентативная стена этажа)
            // isNew=false: создаём ссылочный разрез на вид с индексом refToIdx
            double levelTol = UnitUtils.ConvertToInternalUnits(50, UnitTypeId.Millimeters);

            var sortedWallsAll = assemblyWalls
                .Select(w => new { Wall = w, Bottom = WallBottomZ(w) })
                .OrderBy(x => x.Bottom)
                .ToList();

            // Разбиваем на группы-этажи: стены с близким bottom Z попадают в одну группу
            var floorGroups = new List<List<Wall>>();
            foreach (var x in sortedWallsAll)
            {
                if (floorGroups.Count == 0 ||
                    Math.Abs(x.Bottom - WallBottomZ(floorGroups.Last().First())) > levelTol)
                    floorGroups.Add(new List<Wall>());
                floorGroups.Last().Add(x.Wall);
            }

            // Ключ этажа = объединение ключей всех стен на нём (сортированных, порядок не важен)
            string FloorKey(List<Wall> fw) =>
                string.Join("+", fw.Select(w => WallGeomKey(w)).OrderBy(k => k));

            var wallPlan = new List<(Wall wall, bool isNew, int sectionIdx, int refToIdx)>();
            var sectionFloorWalls = new Dictionary<int, List<Wall>>();  // sectionIdx → все стены этажа
            int sectionCounter = 0;
            int lastNewSectionIdx = -1;
            string lastKey = null;

            foreach (var floorWalls in floorGroups)
            {
                string key = FloorKey(floorWalls);
                bool isNew = key != lastKey;

                if (isNew)
                {
                    sectionCounter++;
                    lastNewSectionIdx = sectionCounter;
                    lastKey = key;
                }

                // Репрезентативная стена для создания разреза — самая длинная прямая стена этажа
                Wall repWall = floorWalls
                    .Where(w => (w.Location as LocationCurve)?.Curve is Line)
                    .OrderByDescending(w => ((w.Location as LocationCurve).Curve as Line).Length)
                    .FirstOrDefault() ?? floorWalls.First();

                if (isNew)
                {
                    sectionFloorWalls[sectionCounter] = floorWalls;
                    wallPlan.Add((repWall, true, sectionCounter, -1));
                    foreach (Wall w in floorWalls.Where(w => w.Id != repWall.Id))
                        wallPlan.Add((w, false, -1, sectionCounter));
                }
                else
                {
                    foreach (Wall w in floorWalls)
                        wallPlan.Add((w, false, -1, lastNewSectionIdx));
                }
            }

            string namePrefix = $"{assemblyComment}_Разрез_";
            var existingSectionsByName = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSection)).Cast<ViewSection>()
                .Where(v => v.Name.StartsWith(namePrefix, StringComparison.OrdinalIgnoreCase))
                .ToDictionary(v => v.Name, v => v, StringComparer.OrdinalIgnoreCase);

            int sectionsCreated = 0, sectionsSkipped = 0, refSectionsCreated = 0;
            var createdSections = new Dictionary<int, ViewSection>(); // sectionIdx → ViewSection

            if (createSections)
            {
                using (Transaction txSection = new Transaction(doc, "Создать виды узлов"))
                {
                    txSection.Start();

                    // Шаг 1: новые виды узлов (при каждой смене толщины)
                    foreach (var entry in wallPlan.Where(p => p.isNew))
                    {
                        string expectedName = $"{assemblyComment}_Разрез_{entry.sectionIdx}-{entry.sectionIdx}";
                        if (existingSectionsByName.TryGetValue(expectedName, out ViewSection existing))
                        {
                            createdSections[entry.sectionIdx] = existing;
                            sectionsSkipped++;
                            continue;
                        }
                        try
                        {
                            BoundingBoxXYZ bb = entry.wall.get_BoundingBox(null);
                            double cz = (bb.Min.Z + bb.Max.Z) / 2.0;
                            ViewSection vs = CreateWallSection(doc, assembly, entry.wall, assemblyComment, cz, rightVec, entry.sectionIdx);
                            if (vs != null)
                            {
                                createdSections[entry.sectionIdx] = vs;
                                sectionsCreated++;
                                try { CreateWallSectionDimensions(doc, vs, entry.wall, sectionFloorWalls[entry.sectionIdx], rightVec); }
                                catch (Exception ex) { TaskDialog.Show("Предупреждение (размеры разреза)", ex.Message); }
                            }
                        }
                        catch (Exception ex)
                        {
                            TaskDialog.Show("Предупреждение (вид узла)",
                                $"Стена Id={entry.wall.Id}: {ex.GetType().Name}: {ex.Message}");
                        }
                    }

                    // Шаг 2: один ссылочный разрез на каждый этаж без нового разреза
                    foreach (var floorWalls in floorGroups)
                    {
                        // Этаж с новым разрезом — уже обработан в Шаге 1, пропускаем
                        if (wallPlan.Any(p => floorWalls.Any(fw => fw.Id == p.wall.Id) && p.isNew))
                            continue;

                        var firstEntry = wallPlan.FirstOrDefault(p => floorWalls.Any(fw => fw.Id == p.wall.Id));
                        if (firstEntry.wall == null) continue;
                        if (!createdSections.TryGetValue(firstEntry.refToIdx, out ViewSection target)) continue;

                        // Репрезентативная стена этажа — самая длинная прямая
                        Wall repWall = floorWalls
                            .Where(w => (w.Location as LocationCurve)?.Curve is Line)
                            .OrderByDescending(w => ((w.Location as LocationCurve).Curve as Line).Length)
                            .FirstOrDefault() ?? floorWalls.First();

                        try
                        {
                            BoundingBoxXYZ bb = repWall.get_BoundingBox(null);
                            double cz = (bb.Min.Z + bb.Max.Z) / 2.0;
                            if (hiddenZones.Any(hz => cz > hz.bottom && cz < hz.top)) continue;
                            CreateReferenceWallSection(doc, cz, rightVec, section, target);
                            refSectionsCreated++;
                        }
                        catch (Exception ex)
                        {
                            TaskDialog.Show("Предупреждение (ссылочный разрез)",
                                $"Стена Id={repWall.Id}: {ex.GetType().Name}: {ex.Message}");
                        }
                    }

                    txSection.Commit();
                }
            }

            string sectionsStr = createSections
                ? $"{sectionsCreated}" + (refSectionsCreated > 0 ? $" + {refSectionsCreated} ссылочных" : "")
                : "не создавались";

            resultMsg = $"Сборка: {assemblyComment}\n" +
                        $"Создано отметок: {createdCount} из {allFaces.Count}\n" +
                        $"Создано разрезов: {sectionsStr}\n" +
                        $"Образмерено проёмов: {openingsFound}";
            if (createBreak) resultMsg += "\n\n⚠ Подвиньте части разрыва вручную через синие ручки на виде.";

            TaskDialog.Show("Готово", resultMsg);
            return Result.Succeeded;
        }

        private void CreateFloorBreakLineAnnotations(Document doc, ViewSection section,
            XYZ rightVec, XYZ viewDir, List<Element> floors,
            List<(double bottom, double top)> hiddenZones)
        {
            if (!floors.Any()) return;

            FamilySymbol breakLineSymbol = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .FirstOrDefault(fs =>
                    fs.Family.Name.Equals("(Оформление) Линия разрыва", StringComparison.OrdinalIgnoreCase) &&
                    fs.Name.Equals("М 1/20", StringComparison.OrdinalIgnoreCase));

            if (breakLineSymbol == null) return;
            if (!breakLineSymbol.IsActive) breakLineSymbol.Activate();

            double depth = section.Origin.DotProduct(viewDir);

            // Границы crop box вида (без отступа — реальная линия обрезки)
            BoundingBoxXYZ cropBox = section.CropBox;
            Transform ct = cropBox.Transform;
            double cropLeftEdge = Math.Min(ct.OfPoint(cropBox.Min).DotProduct(rightVec),
                                            ct.OfPoint(cropBox.Max).DotProduct(rightVec));
            double cropRightEdge = Math.Max(ct.OfPoint(cropBox.Min).DotProduct(rightVec),
                                             ct.OfPoint(cropBox.Max).DotProduct(rightVec));

            Solid masterCropSolid = BuildCropBoxSolid(cropBox);

            foreach (Element floor in floors)
            {
                GetTopAndBottomFaces(floor, section, rightVec,
                    out FaceData topFace, out FaceData botFace);

                if (topFace == null || botFace == null) continue;

                double topZ = topFace.Z;
                double botZ = botFace.Z;

                // Пропускаем плиты у которых верх или низ попадает в скрытую зону
                if (hiddenZones.Any(z => topZ > z.bottom && topZ < z.top)) continue;
                if (hiddenZones.Any(z => botZ > z.bottom && botZ < z.top)) continue;

                if (Math.Abs(topZ - botZ) < 1e-6) continue;

                // Реальный видимый диапазон плиты, обрезанный по объёму crop box вида.
                // Если булева операция не удалась — используем нескорректированные края грани.
                bool clipped = TryGetClippedHorizontalRange(floor, masterCropSolid, rightVec,
                    out double clippedLeft, out double clippedRight);
                double floorLeftProj = clipped ? clippedLeft : topFace.LeftProj;
                double floorRightProj = clipped ? clippedRight : topFace.RightProj;

                // Позиции линий разрыва — 100 мм от края вида
                double offset100 = UnitUtils.ConvertToInternalUnits(100, UnitTypeId.Millimeters);
                double leftProj = cropLeftEdge + offset100;
                double rightProj = cropRightEdge - offset100;

                XYZ MakePt(double proj, double z) =>
                    rightVec.Multiply(proj) + viewDir.Multiply(depth) + XYZ.BasisZ.Multiply(z);

                double edgeTolerance = UnitUtils.ConvertToInternalUnits(1, UnitTypeId.Millimeters);

                // Левая линия — только если плита пересекает левую границу crop box.
                // Для обрезанной геометрии граница "касания" — это сама граница crop box (изнутри),
                // для запасного варианта (геометрия не обрезана) — реальное пересечение границы.
                bool leftPastCrop = clipped
                    ? floorLeftProj < cropLeftEdge + edgeTolerance
                    : floorLeftProj < cropLeftEdge - edgeTolerance;
                if (leftPastCrop)
                {
                    Line lineLeft = Line.CreateBound(MakePt(leftProj, botZ), MakePt(leftProj, topZ));
                    FamilyInstance leftInst = doc.Create.NewFamilyInstance(lineLeft, breakLineSymbol, section);
                    if (leftInst != null)
                    {
                        XYZ center = MakePt(leftProj, (botZ + topZ) / 2.0);
                        Plane mirrorPlane = Plane.CreateByNormalAndOrigin(rightVec, center);
                        ElementTransformUtils.MirrorElement(doc, leftInst.Id, mirrorPlane);
                        doc.Delete(leftInst.Id);
                    }
                }

                // Правая линия — аналогично, с учётом того, обрезана ли геометрия
                bool rightPastCrop = clipped
                    ? floorRightProj > cropRightEdge - edgeTolerance
                    : floorRightProj > cropRightEdge + edgeTolerance;
                if (rightPastCrop)
                {
                    Line lineRight = Line.CreateBound(MakePt(rightProj, botZ), MakePt(rightProj, topZ));
                    doc.Create.NewFamilyInstance(lineRight, breakLineSymbol, section);
                }
            }
        }

        private void CreateBreakLineAnnotations(Document doc, ViewSection section,
            XYZ rightVec, XYZ viewDir, List<(double bottom, double top)> hiddenZones)
        {
            FamilySymbol breakLineSymbol = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .FirstOrDefault(fs =>
                    fs.Family.Name.Equals("(Оформление) Линия разрыва", StringComparison.OrdinalIgnoreCase) &&
                    fs.Name.Equals("М 1/20", StringComparison.OrdinalIgnoreCase));

            if (breakLineSymbol == null)
            {
                TaskDialog.Show("Предупреждение", "Семейство не найдено.");
                return;
            }

            if (!breakLineSymbol.IsActive) breakLineSymbol.Activate();

            double offset = UnitUtils.ConvertToInternalUnits(200, UnitTypeId.Millimeters);
            double depth = section.Origin.DotProduct(viewDir);
            double leftProj = globalMinRight;
            double rightProj = globalMaxRight;

            XYZ MakePtLeft(double z) => rightVec.Multiply(leftProj) + viewDir.Multiply(depth) + XYZ.BasisZ.Multiply(z);
            XYZ MakePtRight(double z) => rightVec.Multiply(rightProj) + viewDir.Multiply(depth) + XYZ.BasisZ.Multiply(z);

            foreach (var (bottom, top) in hiddenZones)
            {
                // Нижний компонент — 200 мм ниже нижней границы зоны (зеркальный)
                double zBottom = bottom - offset;
                Line lineBtm = Line.CreateBound(MakePtLeft(zBottom), MakePtRight(zBottom));
                FamilyInstance btmInst = doc.Create.NewFamilyInstance(lineBtm, breakLineSymbol, section);
                if (btmInst != null)
                {
                    XYZ center = MakePtLeft(zBottom).Add(MakePtRight(zBottom)).Divide(2);

                    // Зеркало 1: горизонтальное (нормаль Z) — переворачивает форму волны вертикально
                    Plane mirrorH = Plane.CreateByNormalAndOrigin(XYZ.BasisZ, center);
                    var hIds = ElementTransformUtils.MirrorElements(doc, new List<ElementId> { btmInst.Id }, mirrorH, true);
                    doc.Delete(btmInst.Id);

                    // Зеркало 2: по вертикальной оси центра (нормаль rightVec) — зеркалит волну по длине
                    ElementId hMirroredId = hIds?.FirstOrDefault();
                    if (hMirroredId != null && hMirroredId != ElementId.InvalidElementId)
                    {
                        Plane mirrorV = Plane.CreateByNormalAndOrigin(rightVec, center);
                        ElementTransformUtils.MirrorElements(doc, new List<ElementId> { hMirroredId }, mirrorV, true);
                        doc.Delete(hMirroredId);
                    }
                }

                // Верхний компонент — 200 мм выше верхней границы зоны
                double zTop = top + offset;
                Line lineTop = Line.CreateBound(MakePtLeft(zTop), MakePtRight(zTop));
                doc.Create.NewFamilyInstance(lineTop, breakLineSymbol, section);
            }
        }

        /// <summary>
        /// Находит индекс региона в который попадает мировая Z-координата.
        /// </summary>
        private int FindRegionForZ(dynamic mgr, double worldZ, double worldBottomZ, double worldHeight)
        {
            int n = mgr.NumberOfSplitRegions;
            for (int i = 0; i < n; i++)
            {
                double localMin = mgr.GetSplitRegionMinimum(i);
                double localMax = mgr.GetSplitRegionMaximum(i);
                double zMin = worldBottomZ + localMin * worldHeight;
                double zMax = worldBottomZ + localMax * worldHeight;
                if (worldZ >= zMin && worldZ <= zMax) return i;
            }
            return -1;
        }

        private void TryAddFace(List<FaceData> list, FaceData fd)
        {
            if (fd == null) return;
            if (list.Any(f => Math.Abs(f.Z - fd.Z) < ZTolerance)) return;
            list.Add(fd);
        }

        private HashSet<double> GetExistingSpotElevationZs(Document doc, View view)
        {
            var result = new HashSet<double>();
            new FilteredElementCollector(doc, view.Id)
                .OfClass(typeof(SpotDimension))
                .Cast<SpotDimension>()
                .Where(sd => sd.SpotDimensionType.StyleType == DimensionStyleType.SpotElevation)
                .ToList()
                .ForEach(sd => result.Add(Math.Round(sd.Origin.Z, 6)));
            return result;
        }

        private bool ExistsAtZ(HashSet<double> existingZs, double z) =>
            existingZs.Any(ez => Math.Abs(ez - z) < ZTolerance);

        private void GetTopAndBottomFaces(Element elem, View view, XYZ rightVec,
            out FaceData topFaceData, out FaceData botFaceData)
        {
            topFaceData = null;
            botFaceData = null;

            GeometryElement geomElem = elem.get_Geometry(new Options
            {
                ComputeReferences = true,
                IncludeNonVisibleObjects = false,
                View = view
            });
            if (geomElem == null) return;

            PlanarFace topFace = null, botFace = null;
            double maxZ = double.MinValue;
            double minZ = double.MaxValue;

            foreach (GeometryObject geomObj in geomElem)
            {
                Solid solid = geomObj as Solid;
                if (solid == null || solid.Faces.IsEmpty) continue;

                foreach (Face face in solid.Faces)
                {
                    PlanarFace pFace = face as PlanarFace;
                    if (pFace == null) continue;

                    if (Math.Abs(pFace.FaceNormal.Z - 1.0) < 1e-6 && pFace.Origin.Z > maxZ)
                    { maxZ = pFace.Origin.Z; topFace = pFace; }

                    if (Math.Abs(pFace.FaceNormal.Z + 1.0) < 1e-6 && pFace.Origin.Z < minZ)
                    { minZ = pFace.Origin.Z; botFace = pFace; }
                }
            }

            XYZ Center(PlanarFace f) => f.Evaluate((f.GetBoundingBox().Min + f.GetBoundingBox().Max) / 2.0);

            (double min, double max) ProjRange(PlanarFace f)
            {
                double mn = double.MaxValue, mx = double.MinValue;
                foreach (EdgeArray edgeLoop in f.EdgeLoops)
                    foreach (Edge edge in edgeLoop)
                        foreach (XYZ pt in edge.Tessellate())
                        {
                            double proj = pt.DotProduct(rightVec);
                            if (proj < mn) mn = proj;
                            if (proj > mx) mx = proj;
                        }
                return (mn, mx);
            }

            if (topFace != null)
            {
                var (mn, mx) = ProjRange(topFace);
                topFaceData = new FaceData
                {
                    Elem = elem,
                    FaceRef = topFace.Reference,
                    FacePoint = Center(topFace),
                    LeftProj = mn,
                    RightProj = mx,
                    IsTop = true
                };
            }
            if (botFace != null)
            {
                var (mn, mx) = ProjRange(botFace);
                botFaceData = new FaceData
                {
                    Elem = elem,
                    FaceRef = botFace.Reference,
                    FacePoint = Center(botFace),
                    LeftProj = mn,
                    RightProj = mx,
                    IsTop = false
                };
            }
        }

        private Solid BuildCropBoxSolid(BoundingBoxXYZ box)
        {
            Transform t = box.Transform;
            XYZ mn = box.Min, mx = box.Max;
            if (mx.X - mn.X < 1e-6 || mx.Y - mn.Y < 1e-6 || mx.Z - mn.Z < 1e-6) return null;

            XYZ p0 = t.OfPoint(new XYZ(mn.X, mn.Y, mn.Z));
            XYZ p1 = t.OfPoint(new XYZ(mx.X, mn.Y, mn.Z));
            XYZ p2 = t.OfPoint(new XYZ(mx.X, mx.Y, mn.Z));
            XYZ p3 = t.OfPoint(new XYZ(mn.X, mx.Y, mn.Z));
            XYZ extrudeDir = t.OfVector(XYZ.BasisZ).Normalize();
            double height = mx.Z - mn.Z;

            CurveLoop BuildLoop(bool reverse)
            {
                XYZ[] pts = reverse ? new[] { p0, p3, p2, p1 } : new[] { p0, p1, p2, p3 };
                CurveLoop cl = new CurveLoop();
                for (int i = 0; i < 4; i++)
                    cl.Append(Line.CreateBound(pts[i], pts[(i + 1) % 4]));
                return cl;
            }

            try
            {
                return GeometryCreationUtilities.CreateExtrusionGeometry(
                    new List<CurveLoop> { BuildLoop(false) }, extrudeDir, height);
            }
            catch
            {
                try
                {
                    return GeometryCreationUtilities.CreateExtrusionGeometry(
                        new List<CurveLoop> { BuildLoop(true) }, extrudeDir, height);
                }
                catch { return null; }
            }
        }

        // Реальный видимый (обрезанный crop box'ом) горизонтальный диапазон верхней грани элемента.
        // Возвращает false, если не удалось вычислить (тогда нужно использовать запасной вариант).
        private bool TryGetClippedHorizontalRange(Element elem, Solid masterCropSolid, XYZ rightVec,
            out double clippedLeft, out double clippedRight)
        {
            clippedLeft = double.MaxValue;
            clippedRight = double.MinValue;
            if (masterCropSolid == null) return false;

            GeometryElement geom = elem.get_Geometry(new Options { ComputeReferences = false, DetailLevel = ViewDetailLevel.Fine });
            if (geom == null) return false;

            bool found = false;
            foreach (GeometryObject go in geom)
            {
                Solid solid = go as Solid;
                if (solid == null || solid.Faces.IsEmpty) continue;

                Solid cropCopy = SolidUtils.Clone(masterCropSolid);
                Solid clipped;
                try
                {
                    clipped = BooleanOperationsUtils.ExecuteBooleanOperation(
                        SolidUtils.Clone(solid), cropCopy, BooleanOperationsType.Intersect);
                }
                catch { continue; }
                if (clipped == null || clipped.Faces.IsEmpty) continue;

                foreach (Face f in clipped.Faces)
                {
                    PlanarFace pf = f as PlanarFace;
                    if (pf == null || Math.Abs(pf.FaceNormal.Z - 1.0) > 1e-3) continue;

                    foreach (EdgeArray loop in pf.EdgeLoops)
                        foreach (Edge edge in loop)
                            foreach (XYZ pt in edge.Tessellate())
                            {
                                double proj = pt.DotProduct(rightVec);
                                if (proj < clippedLeft) clippedLeft = proj;
                                if (proj > clippedRight) clippedRight = proj;
                                found = true;
                            }
                }
            }
            return found;
        }

        private void CreateDimensionChain(Document doc, View view,
            List<FaceData> sortedFaces, double rightProj, double depth,
            XYZ rightVec, XYZ viewDir)
        {
            if (sortedFaces.Count < 2) return;

            DimensionType dimType = new FilteredElementCollector(doc)
                .OfClass(typeof(DimensionType)).Cast<DimensionType>()
                .FirstOrDefault(dt => dt.Name.Equals("BI_основной_2,5мм", StringComparison.OrdinalIgnoreCase));

            if (dimType == null)
                TaskDialog.Show("Предупреждение", "Тип \"BI_основной_2,5мм\" не найден.");

            double pad = UnitUtils.ConvertToInternalUnits(0.1, UnitTypeId.Meters);

            XYZ MakePt(double z) =>
                rightVec.Multiply(rightProj) + viewDir.Multiply(depth) + XYZ.BasisZ.Multiply(z);

            Line dimLine = Line.CreateBound(
                MakePt(sortedFaces.First().Z - pad),
                MakePt(sortedFaces.Last().Z + pad));

            ReferenceArray refArray = new ReferenceArray();
            foreach (FaceData fd in sortedFaces) refArray.Append(fd.FaceRef);

            Dimension dim = dimType != null
                ? doc.Create.NewDimension(view, dimLine, refArray, dimType)
                : doc.Create.NewDimension(view, dimLine, refArray);

            if (dim == null)
            {
                for (int i = 0; i < sortedFaces.Count - 1; i++)
                {
                    var refs = new ReferenceArray();
                    refs.Append(sortedFaces[i].FaceRef);
                    refs.Append(sortedFaces[i + 1].FaceRef);

                    Line l = Line.CreateBound(MakePt(sortedFaces[i].Z), MakePt(sortedFaces[i + 1].Z));

                    if (dimType != null) doc.Create.NewDimension(view, l, refs, dimType);
                    else doc.Create.NewDimension(view, l, refs);
                }
            }
            else
            {
                // Сдвигаем текст вниз и вправо для сегментов «верх стены → низ следующей стены»
                double gapOffsetDown  = UnitUtils.ConvertToInternalUnits(400, UnitTypeId.Millimeters);
                double gapOffsetRight = UnitUtils.ConvertToInternalUnits(300, UnitTypeId.Millimeters);

                try
                {
                    int si = 0;
                    foreach (DimensionSegment seg in dim.Segments)
                    {
                        if (si < sortedFaces.Count - 1)
                        {
                            FaceData fA = sortedFaces[si];
                            FaceData fB = sortedFaces[si + 1];
                            if (fA.IsWall && fA.IsTop && fB.IsWall && !fB.IsTop)
                            {
                                double midZ = (fA.Z + fB.Z) / 2.0;
                                seg.TextPosition = rightVec.Multiply(rightProj + gapOffsetRight)
                                                 + viewDir.Multiply(depth)
                                                 + XYZ.BasisZ.Multiply(midZ - gapOffsetDown);
                            }
                        }
                        si++;
                    }
                }
                catch { }
            }
        }

        /// <summary>
        /// Создаёт внизу разреза два горизонтальных размера, привязанных к видимым
        /// торцам стены: размер 1 (нижний) — длина стены; размер 2 (над ним) —
        /// привязка к осям (торец→оси→торец). Если на разрезе нет ни одной
        /// пересекающей стену оси, размер 2 не создаётся.
        /// </summary>
        private void CreateWallPlanDimensions(Document doc, ViewSection section,
            Wall wall, XYZ rightVec, XYZ viewDir, double baseZ)
        {
            // Торцы стены, видимые на разрезе (вертикальные грани с нормалью вдоль rightVec)
            GetWallEndFaces(wall, section, rightVec,
                out Reference leftRef, out Reference rightRef,
                out double leftProj, out double rightProj);

            if (leftRef == null || rightRef == null || (rightProj - leftProj) < 1e-6)
                return;

            DimensionType dimType = new FilteredElementCollector(doc)
                .OfClass(typeof(DimensionType)).Cast<DimensionType>()
                .FirstOrDefault(dt => dt.Name.Equals("BI_основной_2,5мм", StringComparison.OrdinalIgnoreCase));

            double depth = section.Origin.DotProduct(viewDir);
            double offNear = UnitUtils.ConvertToInternalUnits(800, UnitTypeId.Millimeters);   // оси — ближе к стене (выше)
            double offFar = UnitUtils.ConvertToInternalUnits(1300, UnitTypeId.Millimeters);   // длина — ниже

            XYZ MakePt(double proj, double z) =>
                rightVec.Multiply(proj) + viewDir.Multiply(depth) + XYZ.BasisZ.Multiply(z);

            // Размер 1 — длина стены (нижний)
            try
            {
                Line line1 = Line.CreateBound(MakePt(leftProj, baseZ - offFar), MakePt(rightProj, baseZ - offFar));
                ReferenceArray ra1 = new ReferenceArray();
                ra1.Append(leftRef);
                ra1.Append(rightRef);
                if (dimType != null) doc.Create.NewDimension(section, line1, ra1, dimType);
                else doc.Create.NewDimension(section, line1, ra1);
            }
            catch (Exception ex) { TaskDialog.Show("Предупреждение (длина стены)", ex.Message); }

            // Собираем оси, пересекающие стену (перпендикулярные ей и в пределах пролёта)
            double eps = UnitUtils.ConvertToInternalUnits(10, UnitTypeId.Millimeters);
            var gridRefs = new List<(double proj, Reference gref)>();
            foreach (Grid g in new FilteredElementCollector(doc, section.Id)
                .OfClass(typeof(Grid)).Cast<Grid>())
            {
                Line gl = g.Curve as Line;
                if (gl == null) continue;
                XYZ gd = gl.Direction.Normalize();
                if (Math.Abs(gd.DotProduct(rightVec)) > 1e-3) continue;           // не перпендикулярна стене
                double gp = gl.Evaluate(0.5, true).DotProduct(rightVec);
                if (gp < leftProj - eps || gp > rightProj + eps) continue;        // вне пролёта стены
                gridRefs.Add((gp, new Reference(g)));
            }

            // Если осей нет — размер 2 не создаём
            if (!gridRefs.Any()) return;

            // Размер 2 — привязка к осям (над размером 1): торец → оси → торец
            var chain = new List<(double proj, Reference cref)>
            {
                (leftProj, leftRef),
                (rightProj, rightRef)
            };
            chain.AddRange(gridRefs.Select(x => (x.proj, x.gref)));
            chain = chain.OrderBy(x => x.proj).ToList();

            // Удаляем совпадающие по позиции ссылки (ось на торце и т.п.)
            var deduped = new List<(double proj, Reference cref)>();
            foreach (var item in chain)
                if (!deduped.Any() || (item.proj - deduped[deduped.Count - 1].proj) > eps)
                    deduped.Add(item);

            if (deduped.Count < 2) return;

            try
            {
                Line line2 = Line.CreateBound(MakePt(leftProj, baseZ - offNear), MakePt(rightProj, baseZ - offNear));
                ReferenceArray ra2 = new ReferenceArray();
                foreach (var item in deduped) ra2.Append(item.cref);
                if (dimType != null) doc.Create.NewDimension(section, line2, ra2, dimType);
                else doc.Create.NewDimension(section, line2, ra2);
            }
            catch (Exception ex) { TaskDialog.Show("Предупреждение (привязка к осям)", ex.Message); }
        }

        /// <summary>
        /// Находит ссылки на левый и правый торцы стены, видимые на виде
        /// (вертикальные плоские грани с нормалью вдоль rightVec).
        /// </summary>
        private void GetWallEndFaces(Element wall, View view, XYZ rightVec,
            out Reference leftRef, out Reference rightRef,
            out double leftProj, out double rightProj)
        {
            leftRef = null; rightRef = null;
            leftProj = double.MaxValue; rightProj = double.MinValue;

            GeometryElement geom = wall.get_Geometry(new Options
            {
                ComputeReferences = true,
                IncludeNonVisibleObjects = false,
                View = view
            });
            if (geom == null) return;

            foreach (GeometryObject go in geom)
            {
                Solid s = go as Solid;
                if (s == null || s.Faces.IsEmpty) continue;

                foreach (Face f in s.Faces)
                {
                    PlanarFace pf = f as PlanarFace;
                    if (pf == null) continue;
                    if (Math.Abs(pf.FaceNormal.Z) > 1e-3) continue;                                  // вертикальная грань
                    if (Math.Abs(Math.Abs(pf.FaceNormal.DotProduct(rightVec)) - 1.0) > 1e-3) continue; // нормаль вдоль стены

                    double proj = pf.Origin.DotProduct(rightVec);
                    if (proj < leftProj) { leftProj = proj; leftRef = pf.Reference; }
                    if (proj > rightProj) { rightProj = proj; rightRef = pf.Reference; }
                }
            }
        }

        /// <summary>
        /// Находит габариты проёмов в стене как вырезы профиля: у лицевой/секущей
        /// грани стены (нормаль вдоль viewDir) внутренние контуры EdgeLoops — это
        /// проёмы. Возвращает по каждому диапазон вдоль rightVec и по Z.
        /// </summary>
        /// <summary>
        /// Находит разомкнутые проёмы — прямоугольные выемки снизу во внешнем контуре
        /// лицевой грани (проёмы «от пола»). Низ такой выемки = низ стены.
        /// </summary>
        private void AddBottomNotches(CurveLoop outerLoop, XYZ rightVec,
            List<(double minProj, double maxProj, double botZ, double topZ)> result)
        {
            // Упорядоченные точки контура
            var pts = new List<XYZ>();
            foreach (Curve c in outerLoop)
                foreach (XYZ p in c.Tessellate())
                    if (pts.Count == 0 || pts[pts.Count - 1].DistanceTo(p) > 1e-7)
                        pts.Add(p);
            if (pts.Count < 4) return;

            double tol = UnitUtils.ConvertToInternalUnits(5, UnitTypeId.Millimeters);
            double bottomZ = pts.Min(p => p.Z);

            // Горизонтальные сегменты контура по низу стены
            var bottomSegs = new List<(double x1, double x2)>();
            for (int i = 0; i < pts.Count; i++)
            {
                XYZ a = pts[i];
                XYZ b = pts[(i + 1) % pts.Count];
                if (Math.Abs(a.Z - bottomZ) < tol && Math.Abs(b.Z - bottomZ) < tol)
                {
                    double pa = a.DotProduct(rightVec), pb = b.DotProduct(rightVec);
                    if (Math.Abs(pa - pb) > tol)
                        bottomSegs.Add((Math.Min(pa, pb), Math.Max(pa, pb)));
                }
            }
            if (bottomSegs.Count < 2) return;   // низ не разорван → выемок нет

            bottomSegs = bottomSegs.OrderBy(s => s.x1).ToList();

            // Зазоры между нижними сегментами = выемки
            for (int i = 0; i < bottomSegs.Count - 1; i++)
            {
                double nL = bottomSegs[i].x2;
                double nR = bottomSegs[i + 1].x1;
                if (nR - nL < tol) continue;

                // Верх выемки — максимальная Z точек контура строго внутри зазора
                double notchTop = double.MinValue;
                foreach (XYZ p in pts)
                {
                    double pr = p.DotProduct(rightVec);
                    if (pr >= nL - tol && pr <= nR + tol && p.Z > notchTop)
                        notchTop = p.Z;
                }
                if (notchTop > bottomZ + tol)
                    result.Add((nL, nR, bottomZ, notchTop));
            }
        }

        private List<(double minProj, double maxProj, double botZ, double topZ)> GetWallOpeningBoxes(
            Element wall, View view, XYZ rightVec, XYZ viewDir)
        {
            var result = new List<(double minProj, double maxProj, double botZ, double topZ)>();

            GeometryElement geom = wall.get_Geometry(new Options
            {
                ComputeReferences = false,
                IncludeNonVisibleObjects = false,
                View = view
            });
            if (geom == null) return result;

            foreach (GeometryObject go in geom)
            {
                Solid s = go as Solid;
                if (s == null || s.Faces.IsEmpty) continue;

                foreach (Face f in s.Faces)
                {
                    PlanarFace pf = f as PlanarFace;
                    if (pf == null) continue;
                    // лицевая грань: нормаль горизонтальна и направлена вдоль viewDir
                    if (Math.Abs(pf.FaceNormal.Z) > 1e-3) continue;
                    if (Math.Abs(pf.FaceNormal.DotProduct(viewDir)) < 0.999) continue;

                    IList<CurveLoop> loops;
                    try { loops = pf.GetEdgesAsCurveLoops(); }
                    catch { continue; }
                    if (loops == null || loops.Count == 0) continue;

                    var boxes = new List<(double mn, double mx, double bz, double tz, double range)>();
                    foreach (CurveLoop loop in loops)
                    {
                        double mn = double.MaxValue, mx = double.MinValue, bz = double.MaxValue, tz = double.MinValue;
                        foreach (Curve c in loop)
                            foreach (XYZ p in c.Tessellate())
                            {
                                double pr = p.DotProduct(rightVec);
                                if (pr < mn) mn = pr;
                                if (pr > mx) mx = pr;
                                if (p.Z < bz) bz = p.Z;
                                if (p.Z > tz) tz = p.Z;
                            }
                        boxes.Add((mn, mx, bz, tz, mx - mn));
                    }

                    // внешний контур — с наибольшим размахом
                    double maxRange = boxes.Max(b => b.range);
                    int outerIdx = boxes.FindIndex(b => Math.Abs(b.range - maxRange) < 1e-9);

                    // Замкнутые проёмы — все внутренние контуры
                    for (int li = 0; li < boxes.Count; li++)
                        if (li != outerIdx)
                            result.Add((boxes[li].mn, boxes[li].mx, boxes[li].bz, boxes[li].tz));

                    // Разомкнутые проёмы — прямоугольные выемки снизу во внешнем контуре
                    if (outerIdx >= 0)
                        AddBottomNotches(loops[outerIdx], rightVec, result);
                }
            }

            // Дедуп проёмов, найденных на двух гранях (фронт/тыл) стены
            var deduped = new List<(double minProj, double maxProj, double botZ, double topZ)>();
            double tol = UnitUtils.ConvertToInternalUnits(20, UnitTypeId.Millimeters);
            foreach (var b in result)
                if (!deduped.Any(d => Math.Abs(d.minProj - b.minProj) < tol &&
                                      Math.Abs(d.maxProj - b.maxProj) < tol &&
                                      Math.Abs(d.botZ - b.botZ) < tol &&
                                      Math.Abs(d.topZ - b.topZ) < tol))
                    deduped.Add(b);

            return deduped;
        }

        ///   1) вертикальную справа — низ стены → низ проёма → верх проёма
        ///      (высота проёма и его привязка к низу стены);
        ///   2) горизонтальную сверху — ближайшая ось (или торец стены, если осей
        ///      нет) → левый край проёма → правый край (ширина и привязка).
        /// Привязка идёт к реальным граням стены/проёма, сопоставленным с габаритами
        /// вставки. Работает для прямоугольных проёмов, выровненных по стене.
        /// </summary>
        private int CreateOpeningDimensions(Document doc, ViewSection section, Wall wall,
            XYZ rightVec, XYZ viewDir, List<(double bottom, double top)> hiddenZones)
        {
            // Проёмы ищем по геометрии стены: вырезы в профиле (внутренние контуры
            // лицевой грани) и, дополнительно, вставки (двери/окна/проёмы-вставки).
            var openingBoxes = GetWallOpeningBoxes(wall, section, rightVec, viewDir);

            double mergeTol = UnitUtils.ConvertToInternalUnits(20, UnitTypeId.Millimeters);
            ICollection<ElementId> inserts = wall.FindInserts(true, false, true, true);
            if (inserts != null)
            {
                foreach (ElementId insId in inserts)
                {
                    BoundingBoxXYZ ibb = doc.GetElement(insId)?.get_BoundingBox(null);
                    if (ibb == null) continue;
                    double mn = double.MaxValue, mx = double.MinValue;
                    foreach (double x in new[] { ibb.Min.X, ibb.Max.X })
                        foreach (double y in new[] { ibb.Min.Y, ibb.Max.Y })
                            foreach (double z in new[] { ibb.Min.Z, ibb.Max.Z })
                            {
                                double p = new XYZ(x, y, z).DotProduct(rightVec);
                                if (p < mn) mn = p;
                                if (p > mx) mx = p;
                            }
                    if (!openingBoxes.Any(d => Math.Abs(d.minProj - mn) < mergeTol &&
                                               Math.Abs(d.maxProj - mx) < mergeTol &&
                                               Math.Abs(d.botZ - ibb.Min.Z) < mergeTol &&
                                               Math.Abs(d.topZ - ibb.Max.Z) < mergeTol))
                        openingBoxes.Add((mn, mx, ibb.Min.Z, ibb.Max.Z));
                }
            }

            if (!openingBoxes.Any()) return 0;

            GetWallFaceRefs(wall, section, rightVec, out var vertFaces, out var horizFaces);
            if (vertFaces.Count < 2 || horizFaces.Count < 2) return openingBoxes.Count;

            double wallLeftProj = vertFaces.Min(v => v.proj);
            double wallRightProj = vertFaces.Max(v => v.proj);
            double wallBottomZ = horizFaces.Min(h => h.z);
            double wallTopZ = horizFaces.Max(h => h.z);
            Reference wallBottomRef = horizFaces.OrderBy(h => h.z).First().r;

            DimensionType dimType = new FilteredElementCollector(doc)
                .OfClass(typeof(DimensionType)).Cast<DimensionType>()
                .FirstOrDefault(dt => dt.Name.Equals("BI_основной_2,5мм", StringComparison.OrdinalIgnoreCase));

            double depth = section.Origin.DotProduct(viewDir);
            double eps = UnitUtils.ConvertToInternalUnits(75, UnitTypeId.Millimeters);    // допуск сопоставления граней
            double offRight = UnitUtils.ConvertToInternalUnits(300, UnitTypeId.Millimeters); // вынос вертикальной цепочки правее проёма

            XYZ MakePt(double proj, double z) =>
                rightVec.Multiply(proj) + viewDir.Multiply(depth) + XYZ.BasisZ.Multiply(z);

            // Границы crop box вида — проёмы, выходящие за них, не образмериваем
            BoundingBoxXYZ cropBox = section.CropBox;
            Transform ct = cropBox.Transform;
            double cropLeftEdge = Math.Min(ct.OfPoint(cropBox.Min).DotProduct(rightVec),
                                            ct.OfPoint(cropBox.Max).DotProduct(rightVec));
            double cropRightEdge = Math.Max(ct.OfPoint(cropBox.Min).DotProduct(rightVec),
                                             ct.OfPoint(cropBox.Max).DotProduct(rightVec));
            double cropBottomZ = Math.Min(ct.OfPoint(cropBox.Min).Z, ct.OfPoint(cropBox.Max).Z);
            double cropTopZ = Math.Max(ct.OfPoint(cropBox.Min).Z, ct.OfPoint(cropBox.Max).Z);

            Reference NearestVert(double proj) => vertFaces
                .Where(v => Math.Abs(v.proj - proj) < eps)
                .OrderBy(v => Math.Abs(v.proj - proj))
                .Select(v => v.r).FirstOrDefault();
            Reference NearestHoriz(double z) => horizFaces
                .Where(h => Math.Abs(h.z - z) < eps)
                .OrderBy(h => Math.Abs(h.z - z))
                .Select(h => h.r).FirstOrDefault();

            // Оси, пересекающие стену (перпендикулярные ей)
            var crossingGrids = new List<(double proj, Reference r)>();
            foreach (Grid g in new FilteredElementCollector(doc, section.Id)
                .OfClass(typeof(Grid)).Cast<Grid>())
            {
                Line gl = g.Curve as Line;
                if (gl == null) continue;
                if (Math.Abs(gl.Direction.Normalize().DotProduct(rightVec)) > 1e-3) continue;
                crossingGrids.Add((gl.Evaluate(0.5, true).DotProduct(rightVec), new Reference(g)));
            }

            foreach (var ob in openingBoxes)
            {
                double oMinProj = ob.minProj, oMaxProj = ob.maxProj;
                double oBottomZ = ob.botZ, oTopZ = ob.topZ;

                // Проём, выходящий за crop box вида, не образмериваем
                if (oMinProj < cropLeftEdge || oMaxProj > cropRightEdge ||
                    oBottomZ < cropBottomZ || oTopZ > cropTopZ)
                    continue;

                // Проём, попавший в скрытую зону разрыва вида, не образмериваем —
                // та же проверка, что и для высотных отметок/плит.
                if (hiddenZones.Any(z => oTopZ > z.bottom && oTopZ < z.top)) continue;
                if (hiddenZones.Any(z => oBottomZ > z.bottom && oBottomZ < z.top)) continue;

                Reference openLeft = NearestVert(oMinProj);
                Reference openRight = NearestVert(oMaxProj);
                Reference openBottom = NearestHoriz(oBottomZ);
                Reference openTop = NearestHoriz(oTopZ);

                // --- Цепочка 1: высота проёма + привязка к низу стены (вертикальная, справа) ---
                // Если проём идёт от пола (низ проёма совпал с низом стены), делаем
                // один размер: низ стены → верх проёма, без промежуточной привязки.
                double floorTol = UnitUtils.ConvertToInternalUnits(50, UnitTypeId.Millimeters);
                bool openingFromFloor = Math.Abs(oBottomZ - wallBottomZ) < floorTol;

                if (openTop != null && wallBottomRef != null)
                {
                    var hChain = new List<(double z, Reference r)> { (wallBottomZ, wallBottomRef) };
                    if (!openingFromFloor && openBottom != null)
                        hChain.Add((oBottomZ, openBottom));   // промежуточная привязка (низ проёма)
                    hChain.Add((oTopZ, openTop));
                    hChain = hChain.OrderBy(x => x.z).ToList();

                    var hDed = new List<(double z, Reference r)>();
                    foreach (var it in hChain)
                        if (!hDed.Any() || (it.z - hDed[hDed.Count - 1].z) > eps) hDed.Add(it);

                    if (hDed.Count >= 2)
                    {
                        double vProj = oMaxProj + offRight;
                        try
                        {
                            Line vline = Line.CreateBound(
                                MakePt(vProj, hDed.First().z), MakePt(vProj, hDed.Last().z));
                            ReferenceArray ra = new ReferenceArray();
                            foreach (var it in hDed) ra.Append(it.r);
                            if (dimType != null) doc.Create.NewDimension(section, vline, ra, dimType);
                            else doc.Create.NewDimension(section, vline, ra);
                        }
                        catch (Exception ex) { TaskDialog.Show("Предупреждение (высота проёма)", ex.Message); }
                    }
                }

                // --- Цепочка 2: ширина проёма + привязка к оси/торцу (горизонтальная, сверху) ---
                if (openLeft != null && openRight != null)
                {
                    double oCenter = (oMinProj + oMaxProj) / 2.0;

                    Reference bindRef = null; double bindProj = 0;
                    if (crossingGrids.Any())
                    {
                        var ng = crossingGrids.OrderBy(g => Math.Abs(g.proj - oCenter)).First();
                        bindRef = ng.r; bindProj = ng.proj;
                    }
                    else
                    {
                        // ближайший торец стены
                        if (Math.Abs(wallLeftProj - oCenter) <= Math.Abs(wallRightProj - oCenter))
                        { bindRef = NearestVert(wallLeftProj); bindProj = wallLeftProj; }
                        else
                        { bindRef = NearestVert(wallRightProj); bindProj = wallRightProj; }
                    }

                    var wChain = new List<(double proj, Reference r)>
                    {
                        (oMinProj, openLeft),
                        (oMaxProj, openRight)
                    };
                    if (bindRef != null) wChain.Add((bindProj, bindRef));
                    wChain = wChain.OrderBy(x => x.proj).ToList();

                    var wDed = new List<(double proj, Reference r)>();
                    foreach (var it in wChain)
                        if (!wDed.Any() || (it.proj - wDed[wDed.Count - 1].proj) > eps) wDed.Add(it);

                    if (wDed.Count >= 2)
                    {
                        double hZ = (oBottomZ + oTopZ) / 2.0;
                        try
                        {
                            Line hline = Line.CreateBound(
                                MakePt(wDed.First().proj, hZ), MakePt(wDed.Last().proj, hZ));
                            ReferenceArray ra = new ReferenceArray();
                            foreach (var it in wDed) ra.Append(it.r);
                            if (dimType != null) doc.Create.NewDimension(section, hline, ra, dimType);
                            else doc.Create.NewDimension(section, hline, ra);
                        }
                        catch (Exception ex) { TaskDialog.Show("Предупреждение (ширина проёма)", ex.Message); }
                    }
                }
            }

            return openingBoxes.Count;
        }

        /// <summary>
        /// Собирает ссылки на плоские грани стены (с учётом проёмов), разделяя их на
        /// вертикальные (нормаль вдоль rightVec) и горизонтальные (нормаль вдоль Z).
        /// </summary>
        private void GetWallFaceRefs(Element wall, View view, XYZ rightVec,
            out List<(double proj, Reference r)> vertFaces,
            out List<(double z, Reference r)> horizFaces)
        {
            vertFaces = new List<(double proj, Reference r)>();
            horizFaces = new List<(double z, Reference r)>();

            GeometryElement geom = wall.get_Geometry(new Options
            {
                ComputeReferences = true,
                IncludeNonVisibleObjects = false,
                View = view
            });
            if (geom == null) return;

            foreach (GeometryObject go in geom)
            {
                Solid s = go as Solid;
                if (s == null || s.Faces.IsEmpty) continue;

                foreach (Face f in s.Faces)
                {
                    PlanarFace pf = f as PlanarFace;
                    if (pf == null || pf.Reference == null) continue;

                    if (Math.Abs(Math.Abs(pf.FaceNormal.Z) - 1.0) < 1e-3)
                        horizFaces.Add((pf.Origin.Z, pf.Reference));                       // горизонтальная
                    else if (Math.Abs(pf.FaceNormal.Z) < 1e-3 &&
                             Math.Abs(Math.Abs(pf.FaceNormal.DotProduct(rightVec)) - 1.0) < 1e-3)
                        vertFaces.Add((pf.Origin.DotProduct(rightVec), pf.Reference));     // вертикальная вдоль стены
                }
            }
        }

        // -----------------------------------------------------------------------
        /// <summary>
        /// Создаёт вид узла (Detail) вдоль всей длины стены из сборки.
        /// Секущая плоскость на мировой отметке cutZ (середина по высоте стены),
        /// взгляд направлен ВНИЗ. Тип вида: "Вид узла" → "*04_Стены_сечение".
        /// Применяется шаблон вида "*01_КЖ_(04_Стены)_Сечение".
        /// Имя вида: "{комментарий сборки}_Разрез_{n}-{n}".
        /// </summary>
        private ViewSection CreateWallSection(Document doc, AssemblyInstance assembly,
            Wall wall, string assemblyComment, double cutZ, XYZ viewRight, int seqIndex)
        {
            if (wall == null) return null;

            LocationCurve locationCurve = wall.Location as LocationCurve;
            if (locationCurve == null) return null;

            Line wallLine = locationCurve.Curve as Line;
            if (wallLine == null) return null;

            // Геометрия стены
            XYZ wallStart = wallLine.GetEndPoint(0);
            XYZ wallEnd = wallLine.GetEndPoint(1);
            XYZ wallMid = (wallStart + wallEnd) / 2.0;
            double wallLength = wallLine.Length;
            double wallThickness = wall.Width;

            // Параметры разреза
            // ВНИМАНИЕ: размах по X задаёт и рамку подрезки, и положение головок
            // марки одновременно — в API их развязать нельзя. Держим узкие границы.
            double offsetLen = UnitUtils.ConvertToInternalUnits(800, UnitTypeId.Millimeters);   // запас по длине (подрезка + марки)
            double offsetThk = UnitUtils.ConvertToInternalUnits(500, UnitTypeId.Millimeters);   // запас по толщине
            double depthDown = UnitUtils.ConvertToInternalUnits(500, UnitTypeId.Millimeters);   // глубина взгляда вниз
            double depthUp = UnitUtils.ConvertToInternalUnits(100, UnitTypeId.Millimeters);    // запас вверх до секущей

            // Горизонтальный разрез вдоль всей длины стены, взгляд ВНИЗ.
            // BasisX = вдоль длины стены (вправо во виде)
            // BasisZ = -Z (к наблюдателю снизу → взгляд направлен вниз)
            // BasisY = BasisZ × BasisX (поперёк толщины; правая тройка)
            XYZ wallDir = wallLine.Direction.Normalize();

            // Жёсткая привязка «лево/право»: ориентируем длину стены по правому
            // направлению активного вида (его внутренним координатам), чтобы +X
            // во виде узла всегда совпадал с правой стороной исходного разреза,
            // независимо от того, в какую сторону нарисована линия стены.
            XYZ viewRightHoriz = new XYZ(viewRight.X, viewRight.Y, 0);
            if (viewRightHoriz.GetLength() > 1e-9 &&
                wallDir.DotProduct(viewRightHoriz.Normalize()) < 0)
                wallDir = wallDir.Negate();

            XYZ rightDir = wallDir;
            XYZ viewBasisZ = XYZ.BasisZ.Negate();                          // -Z
            XYZ upDir = viewBasisZ.CrossProduct(rightDir).Normalize();      // поперёк толщины

            // Полный горизонтальный охват сборки вдоль rightDir:
            // если сборка включает несколько стен (боковые + центральные), разрез
            // должен охватывать всю сборку, а не только текущую стену.
            double asmMinR = double.MaxValue, asmMaxR = double.MinValue;
            foreach (var memberId in assembly.GetMemberIds())
            {
                // Только стены — плиты/фундаменты в составе сборки могут быть
                // огромными (вплоть до целого здания), и их bounding box не должен
                // влиять на охват разреза по горизонтали.
                if (!(doc.GetElement(memberId) is Wall)) continue;

                BoundingBoxXYZ ebb = doc.GetElement(memberId)?.get_BoundingBox(null);
                if (ebb == null) continue;
                foreach (XYZ corner in new[] {
                    ebb.Min,
                    new XYZ(ebb.Max.X, ebb.Min.Y, ebb.Min.Z),
                    new XYZ(ebb.Min.X, ebb.Max.Y, ebb.Min.Z),
                    new XYZ(ebb.Max.X, ebb.Max.Y, ebb.Min.Z) })
                {
                    double r = corner.DotProduct(rightDir);
                    if (r < asmMinR) asmMinR = r;
                    if (r > asmMaxR) asmMaxR = r;
                }
            }
            double asmLength   = (asmMaxR > asmMinR) ? (asmMaxR - asmMinR) : wallLength;
            double asmCenterR  = (asmMaxR > asmMinR) ? (asmMinR + asmMaxR) / 2.0 : wallMid.DotProduct(rightDir);

            // Origin — центр сборки по горизонтали, на отметке cutZ
            double wallMidR = wallMid.DotProduct(rightDir);
            double shiftR = asmCenterR - wallMidR;
            XYZ origin = new XYZ(
                wallMid.X + rightDir.X * shiftR,
                wallMid.Y + rightDir.Y * shiftR,
                cutZ);

            Transform t = Transform.Identity;
            t.BasisX = rightDir;     // вдоль длины
            t.BasisY = upDir;        // поперёк толщины
            t.BasisZ = viewBasisZ;   // -Z (BasisX × BasisY = -Z → правая тройка, взгляд вниз)
            t.Origin = origin;

            // Локальный +Z теперь направлен в мировой -Z (вниз),
            // поэтому глубину взгляда (depthDown) кладём в Max.Z.
            BoundingBoxXYZ sectionBox = new BoundingBoxXYZ();
            sectionBox.Transform = t;
            sectionBox.Min = new XYZ(
                -asmLength / 2.0 - offsetLen,       // X — левая граница (вся сборка + марки)
                -wallThickness / 2.0 - offsetThk,   // Y — толщина стены
                -depthUp);                           // Z — небольшой запас выше секущей
            sectionBox.Max = new XYZ(
                 asmLength / 2.0 + offsetLen,        // X — правая граница (вся сборка + марки)
                 wallThickness / 2.0 + offsetThk,
                 depthDown);                         // Z — глубина взгляда вниз

            // Тип вида — "Вид узла" (Detail) с именем "*04_Стены_сечение"
            ViewFamilyType detailType = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewFamilyType))
                .Cast<ViewFamilyType>()
                .FirstOrDefault(vft => vft.ViewFamily == ViewFamily.Detail &&
                    vft.Name.Equals("*04_Стены_сечение", StringComparison.OrdinalIgnoreCase));

            if (detailType == null)
                detailType = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewFamilyType))
                    .Cast<ViewFamilyType>()
                    .FirstOrDefault(vft => vft.ViewFamily == ViewFamily.Detail);

            if (detailType == null) return null;

            string sectionName = GenerateSectionName(doc, assemblyComment, seqIndex);

            ViewSection newSection = ViewSection.CreateDetail(doc, detailType.Id, sectionBox);
            if (newSection == null) return null;

            newSection.Name = sectionName;
            try { newSection.Scale = 25; } catch { }

            // Применяем шаблон вида "*01_КЖ_(04_Стены)_Сечение"
            View viewTemplate = new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .FirstOrDefault(v => v.IsTemplate &&
                    v.Name.Equals("*01_КЖ_(04_Стены)_Сечение", StringComparison.OrdinalIgnoreCase));

            if (viewTemplate != null)
                newSection.ViewTemplateId = viewTemplate.Id;
            else
                TaskDialog.Show("Предупреждение", "Шаблон вида \"*01_КЖ_(04_Стены)_Сечение\" не найден.");

            Parameter markParam = newSection.LookupParameter("BI_марка_конструкции");
            if (markParam != null && !markParam.IsReadOnly)
                markParam.Set(assemblyComment);

            return newSection;
        }

        private void CreateReferenceWallSection(Document doc, double cutZ,
            XYZ viewRight, ViewSection parentSection, ViewSection referencedView)
        {
            if (referencedView == null) return;

            // Левый/правый край марки берём из CropBox обычного разреза, на который
            // ссылаемся — а не из длины одной стены, чтобы координаты X/Y ссылочного
            // разреза в точности совпадали с обычным.
            BoundingBoxXYZ cropBox = referencedView.CropBox;
            Transform t = cropBox.Transform;
            XYZ pMin = t.OfPoint(new XYZ(cropBox.Min.X, 0, 0));
            XYZ pMax = t.OfPoint(new XYZ(cropBox.Max.X, 0, 0));

            double projMin = pMin.DotProduct(viewRight);
            double projMax = pMax.DotProduct(viewRight);
            XYZ leftPt  = projMin <= projMax ? pMin : pMax;
            XYZ rightPt = projMin <= projMax ? pMax : pMin;

            XYZ headPoint = new XYZ(leftPt.X,  leftPt.Y,  cutZ);
            XYZ tailPoint = new XYZ(rightPt.X, rightPt.Y, cutZ);

            // tailPoint передаётся первым (head), headPoint — вторым (tail):
            // вектор tail→head даёт -Z через cross product с ViewDirection родителя → разрез смотрит вниз
            ViewSection.CreateReferenceSection(doc, parentSection.Id, referencedView.Id, tailPoint, headPoint);
        }

        /// <summary>
        /// Создаёт 4 вида размеров на горизонтальном разрезе стены:
        ///   1 — общая длина (торец↔торец);
        ///   2 — цепочка: торец → оси/проёмы → торец;
        ///   3 — толщина стены;
        ///   4 — толщина с привязкой к продольной оси.
        /// </summary>
        private void CreateWallSectionDimensions(Document doc, ViewSection sectionView,
            Wall wall, IList<Wall> floorWalls, XYZ viewRight)
        {
            if (sectionView == null || wall == null) return;

            LocationCurve lc = wall.Location as LocationCurve;
            Line wallLine = lc?.Curve as Line;
            if (wallLine == null) return;

            XYZ wallDir = wallLine.Direction.Normalize();
            XYZ viewRightH = new XYZ(viewRight.X, viewRight.Y, 0);
            if (viewRightH.GetLength() > 1e-9 && wallDir.DotProduct(viewRightH.Normalize()) < 0)
                wallDir = wallDir.Negate();
            XYZ upDir = XYZ.BasisZ.Negate().CrossProduct(wallDir).Normalize();

            double cutZ = sectionView.Origin.Z;

            // allEndFaces — торцы и откосы проёмов со ВСЕХ стен этажа
            var allEndFaces = new List<(double proj, Reference r)>();
            var sideFaces   = new List<(double proj, Reference r)>();

            double eps2 = UnitUtils.ConvertToInternalUnits(5, UnitTypeId.Millimeters);

            var repGeomV = wall.get_Geometry(new Options { ComputeReferences = true, View = sectionView });
            var repGeomN = wall.get_Geometry(new Options { ComputeReferences = true });
            GeometryElement repGeomForSide = repGeomV ?? repGeomN;

            foreach (Wall w in floorWalls)
            {
                GeometryElement geom = w.get_Geometry(new Options { ComputeReferences = true });
                if (geom == null) continue;

                foreach (GeometryObject go in geom)
                {
                    Solid s = go as Solid;
                    if (s == null || s.Faces.IsEmpty) continue;
                    foreach (Face f in s.Faces)
                    {
                        PlanarFace pf = f as PlanarFace;
                        if (pf == null || pf.Reference == null) continue;
                        XYZ n = pf.FaceNormal.Normalize();
                        if (Math.Abs(n.Z) > 1e-3) continue;
                        double dotWall = pf.FaceNormal.DotProduct(wallDir);
                        double proj = pf.Origin.DotProduct(wallDir);
                        if (Math.Abs(Math.Abs(dotWall) - 1.0) < 1e-3)
                        {
                            if (!allEndFaces.Any(e => Math.Abs(e.proj - proj) < eps2))
                                allEndFaces.Add((proj, pf.Reference));
                        }
                    }
                }
            }

            // sideFaces — только от репрезентативной стены
            if (repGeomForSide != null)
            {
                foreach (GeometryObject go in repGeomForSide)
                {
                    Solid s = go as Solid;
                    if (s == null || s.Faces.IsEmpty) continue;
                    foreach (Face f in s.Faces)
                    {
                        PlanarFace pf = f as PlanarFace;
                        if (pf == null || pf.Reference == null) continue;
                        XYZ n = pf.FaceNormal.Normalize();
                        if (Math.Abs(n.Z) > 1e-3) continue;
                        if (Math.Abs(Math.Abs(n.DotProduct(upDir)) - 1.0) < 1e-3)
                        {
                            double up = pf.Origin.DotProduct(upDir);
                            if (!sideFaces.Any(e => Math.Abs(e.proj - up) < eps2))
                                sideFaces.Add((up, pf.Reference));
                        }
                    }
                }
            }

            if (allEndFaces.Count < 2 || sideFaces.Count < 2) return;

            double leftProj  = allEndFaces.Min(f => f.proj);
            double rightProj = allEndFaces.Max(f => f.proj);
            Reference leftRef  = allEndFaces.OrderBy(f => f.proj).First().r;
            Reference rightRef = allEndFaces.OrderByDescending(f => f.proj).First().r;

            double minU = sideFaces.Min(f => f.proj);
            double maxU = sideFaces.Max(f => f.proj);
            Reference minURef = sideFaces.OrderBy(f => f.proj).First().r;
            Reference maxURef = sideFaces.OrderByDescending(f => f.proj).First().r;

            // Расширяем crop box вида так, чтобы все торцевые грани были видимы
            try
            {
                BoundingBoxXYZ cropBox = sectionView.CropBox;
                Transform ct = cropBox.Transform;

                // ВАЖНО: реальный ct.BasisX не гарантированно совпадает по знаку с
                // wallDir (Revit может пересобрать оси при ViewSection.CreateDetail).
                // Поэтому локальную координату торца стены считаем через проекцию на
                // wallDir с учётом знака BasisX относительно wallDir, а не напрямую.
                double sign = ct.BasisX.DotProduct(wallDir) >= 0 ? 1.0 : -1.0;
                double originWallProj = ct.Origin.DotProduct(wallDir);
                double a = (leftProj  - originWallProj) * sign;
                double b = (rightProj - originWallProj) * sign;
                double localLeft  = Math.Min(a, b);
                double localRight = Math.Max(a, b);
                double cbPad = UnitUtils.ConvertToInternalUnits(300, UnitTypeId.Millimeters);
                bool willExpand = (localLeft - cbPad) < cropBox.Min.X || (localRight + cbPad) > cropBox.Max.X;
                if (willExpand)
                {
                    cropBox.Min = new XYZ(Math.Min(cropBox.Min.X, localLeft  - cbPad), cropBox.Min.Y, cropBox.Min.Z);
                    cropBox.Max = new XYZ(Math.Max(cropBox.Max.X, localRight + cbPad), cropBox.Max.Y, cropBox.Max.Z);
                    sectionView.CropBox = cropBox;
                }
            }
            catch { }

            DimensionType dimType = new FilteredElementCollector(doc)
                .OfClass(typeof(DimensionType)).Cast<DimensionType>()
                .FirstOrDefault(dt => dt.Name.Equals("BI_основной_2,5мм(фон)", StringComparison.OrdinalIgnoreCase));

            // off1 — привязочный (ближе к стене), off2 — общий (дальше)
            double off1 = UnitUtils.ConvertToInternalUnits(800,  UnitTypeId.Millimeters);
            double off2 = UnitUtils.ConvertToInternalUnits(1300, UnitTypeId.Millimeters);
            double pad  = UnitUtils.ConvertToInternalUnits(100,  UnitTypeId.Millimeters);
            double eps  = UnitUtils.ConvertToInternalUnits(10,   UnitTypeId.Millimeters);

            XYZ MakePt(double wProj, double uProj) =>
                wallDir.Multiply(wProj) + upDir.Multiply(uProj) + XYZ.BasisZ.Multiply(cutZ);

            void AddDim(Line line, ReferenceArray ra)
            {
                if (dimType != null) doc.Create.NewDimension(sectionView, line, ra, dimType);
                else                 doc.Create.NewDimension(sectionView, line, ra);
            }

            // ── Поперечные оси (перпендикулярны стене, пересекают пролёт) ─────────
            var crossGrids = new List<(double proj, Reference r)>();
            foreach (Grid g in new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>())
            {
                Line gl = g.Curve as Line;
                if (gl == null) continue;
                XYZ gd = gl.Direction.Normalize();
                if (Math.Abs(Math.Abs(gd.DotProduct(upDir)) - 1.0) > 0.01) continue;
                double gp = gl.GetEndPoint(0).DotProduct(wallDir);
                if (gp < leftProj - eps || gp > rightProj + eps) continue;
                crossGrids.Add((gp, new Reference(g)));
            }

            // ── Dim 2 (ближе, at off1): цепочка — торцы стены + откосы проёмов + оси ──
            {
                var chain = allEndFaces
                    .Concat(crossGrids.Select(g => (g.proj, g.r)))
                    .OrderBy(x => x.proj).ToList();

                var deduped = new List<(double proj, Reference r)>();
                foreach (var item in chain)
                    if (!deduped.Any() || item.proj - deduped[deduped.Count - 1].proj > eps)
                        deduped.Add(item);

                if (deduped.Count >= 2)
                {
                    try
                    {
                        var ra = new ReferenceArray();
                        foreach (var item in deduped) ra.Append(item.r);
                        AddDim(Line.CreateBound(
                            MakePt(deduped.First().proj - pad, maxU + off1),
                            MakePt(deduped.Last().proj  + pad, maxU + off1)), ra);
                    }
                    catch { }
                }
            }

            // ── Dim 1 (дальше, at off2): общая длина стены ───────────────────────
            try
            {
                var ra = new ReferenceArray(); ra.Append(leftRef); ra.Append(rightRef);
                AddDim(Line.CreateBound(MakePt(leftProj - pad, maxU + off2),
                                        MakePt(rightProj + pad, maxU + off2)), ra);
            }
            catch { }

            // ── Продольная ось (параллельна стене) ───────────────────────────────
            Reference longiRef  = null;
            double    longiProj = 0;
            foreach (Grid g in new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>())
            {
                Line gl = g.Curve as Line;
                if (gl == null) continue;
                XYZ gd = gl.Direction.Normalize();
                if (Math.Abs(Math.Abs(gd.DotProduct(wallDir)) - 1.0) > 0.01) continue;
                double gp = gl.GetEndPoint(0).DotProduct(upDir);
                if (gp < minU - eps || gp > maxU + eps) continue;
                longiRef  = new Reference(g);
                longiProj = gp;
                break;
            }

            // ── Dim 4 (ближе, at off1): цепочка по толщине — грани + продольная ось ──
            {
                var chain = sideFaces.Select(f => (f.proj, f.r)).ToList();
                if (longiRef != null) chain.Add((longiProj, longiRef));
                chain = chain.OrderBy(x => x.proj).ToList();

                var deduped = new List<(double proj, Reference r)>();
                foreach (var item in chain)
                    if (!deduped.Any() || item.proj - deduped[deduped.Count - 1].proj > eps)
                        deduped.Add(item);

                if (deduped.Count >= 2)
                {
                    try
                    {
                        var ra = new ReferenceArray();
                        foreach (var item in deduped) ra.Append(item.r);
                        AddDim(Line.CreateBound(
                            MakePt(leftProj - off1, deduped.First().proj - pad),
                            MakePt(leftProj - off1, deduped.Last().proj  + pad)), ra);
                    }
                    catch { }
                }
            }

            // ── Dim 3 (дальше, at off2): общая толщина стены ─────────────────────
            try
            {
                var ra = new ReferenceArray(); ra.Append(minURef); ra.Append(maxURef);
                AddDim(Line.CreateBound(MakePt(leftProj - off2, minU - pad),
                                        MakePt(leftProj - off2, maxU + pad)), ra);
            }
            catch { }
        }

        private string GenerateSectionName(Document doc, string assemblyComment, int preferredIndex)
        {
            var existingNames = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSection))
                .Cast<ViewSection>()
                .Select(v => v.Name)
                .ToHashSet();

            // Сначала пробуем предпочтительный номер (порядок в пачке), затем —
            // ближайший свободный вверх. Так нумерация идёт 1-1, 2-2, 3-3…
            for (int i = preferredIndex; i <= preferredIndex + 100; i++)
            {
                string candidate = $"{assemblyComment}_Разрез_{i}-{i}";
                if (!existingNames.Contains(candidate))
                    return candidate;
            }

            return $"{assemblyComment}_Разрез_{Guid.NewGuid():N}";
        }
    }
}