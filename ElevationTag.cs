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
                .Where(f => f.Category?.Id.IntegerValue != (int)BuiltInCategory.OST_StructuralFoundation)
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

            double tagProj = globalMinRight - UnitUtils.ConvertToInternalUnits(1000, UnitTypeId.Millimeters);
            double bendGap = UnitUtils.ConvertToInternalUnits(200, UnitTypeId.Millimeters);
            double dimProj = globalMinRight - UnitUtils.ConvertToInternalUnits(600, UnitTypeId.Millimeters);
            double dimOddProj = globalMinRight - UnitUtils.ConvertToInternalUnits(900, UnitTypeId.Millimeters);
            double dimTotalProj = globalMinRight - UnitUtils.ConvertToInternalUnits(1200, UnitTypeId.Millimeters);

            XYZ MakePoint(double rightProj, double depth, double z) =>
                rightVec.Multiply(rightProj) + viewDir.Multiply(depth) + XYZ.BasisZ.Multiply(z);

            int createdCount = 0;
            int skippedCount = 0;

            using (Transaction tx = new Transaction(doc, "Создать высотные отметки и размеры"))
            {
                tx.Start();

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

                tx.Commit();
            }

            string assemblyComment = assembly
                .get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() ?? assembly.Name;

            string resultMsg = $"Сборка: {assemblyComment}\n" +
                               $"Создано отметок: {createdCount} из {allFaces.Count}";
            if (skippedCount > 0) resultMsg += $"\nПропущено: {skippedCount}";
            if (createBreak) resultMsg += "\n\n⚠ Подвиньте части разрыва вручную через синие ручки на виде.";

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

            var sortedWalls = assemblyWalls
                .Select(w => new { Wall = w, Bottom = WallBottomZ(w), Thickness = WallThickness(w) })
                .OrderBy(x => x.Bottom)
                .ToList();

            var changeWalls = new List<Wall>();
            double prevThk = double.NaN;
            foreach (var x in sortedWalls)
            {
                if (double.IsNaN(prevThk) || Math.Abs(x.Thickness - prevThk) > thkTol)
                    changeWalls.Add(x.Wall);   // самая нижняя или смена толщины
                prevThk = x.Thickness;
            }

            // Диагностика: какие имена с этим префиксом уже есть в проекте
            string namePrefix = $"{assemblyComment}_Разрез_";
            var preExisting = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSection)).Cast<ViewSection>()
                .Where(v => v.Name.StartsWith(namePrefix, StringComparison.OrdinalIgnoreCase))
                .Select(v => $"{v.Name} [{v.ViewType}]")
                .ToList();

            int sectionsCreated = 0;
            using (Transaction txSection = new Transaction(doc, "Создать виды узлов"))
            {
                txSection.Start();
                for (int i = 0; i < changeWalls.Count; i++)
                {
                    Wall cw = changeWalls[i];
                    try
                    {
                        BoundingBoxXYZ bb = cw.get_BoundingBox(null);
                        double cz = (bb.Min.Z + bb.Max.Z) / 2.0;   // середина по высоте этой стены
                        CreateWallSection(doc, assembly, cw, assemblyComment, cz, rightVec, i + 1);
                        sectionsCreated++;
                    }
                    catch (Exception ex)
                    {
                        TaskDialog.Show("Предупреждение (вид узла)",
                            $"Стена Id={cw.Id}: {ex.GetType().Name}: {ex.Message}");
                    }
                }
                txSection.Commit();
            }

            resultMsg += $"\nСтен в сборке: {assemblyWalls.Count}, видов узлов: {sectionsCreated} (по сменам толщины)";
            if (preExisting.Any())
                resultMsg += $"\nУже были имена с префиксом \"{namePrefix}\":\n  " + string.Join("\n  ", preExisting);

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

                // Края плиты
                double floorLeftProj = topFace.LeftProj;
                double floorRightProj = topFace.RightProj;

                // Позиции линий разрыва — 100 мм от края вида
                BoundingBoxXYZ cropBox = section.CropBox;
                Transform ct = cropBox.Transform;
                double offset100 = UnitUtils.ConvertToInternalUnits(100, UnitTypeId.Millimeters);
                double leftProj = Math.Min(ct.OfPoint(cropBox.Min).DotProduct(rightVec),
                                            ct.OfPoint(cropBox.Max).DotProduct(rightVec)) + offset100;
                double rightProj = Math.Max(ct.OfPoint(cropBox.Min).DotProduct(rightVec),
                                            ct.OfPoint(cropBox.Max).DotProduct(rightVec)) - offset100;

                XYZ MakePt(double proj, double z) =>
                    rightVec.Multiply(proj) + viewDir.Multiply(depth) + XYZ.BasisZ.Multiply(z);

                // Левая линия — только если плита выступает левее стены (с допуском 1 мм)
                double edgeTolerance = UnitUtils.ConvertToInternalUnits(1, UnitTypeId.Millimeters);
                if (floorLeftProj < globalMinRight - edgeTolerance)
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

                // Правая линия — только если плита выступает правее стены (с допуском 1 мм)
                if (floorRightProj > globalMaxRight + edgeTolerance)
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
                    Plane mirrorPlane = Plane.CreateByNormalAndOrigin(XYZ.BasisZ, center);
                    ElementTransformUtils.MirrorElement(doc, btmInst.Id, mirrorPlane);
                    doc.Delete(btmInst.Id);
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
            double minProj = double.MaxValue;
            double maxProj = double.MinValue;

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

                    foreach (EdgeArray edgeLoop in pFace.EdgeLoops)
                        foreach (Edge edge in edgeLoop)
                            foreach (XYZ pt in edge.Tessellate())
                            {
                                double proj = pt.DotProduct(rightVec);
                                if (proj < minProj) minProj = proj;
                                if (proj > maxProj) maxProj = proj;
                            }
                }
            }

            XYZ Center(PlanarFace f) => f.Evaluate((f.GetBoundingBox().Min + f.GetBoundingBox().Max) / 2.0);

            if (topFace != null)
                topFaceData = new FaceData
                {
                    Elem = elem,
                    FaceRef = topFace.Reference,
                    FacePoint = Center(topFace),
                    LeftProj = minProj,
                    RightProj = maxProj,
                    IsTop = true
                };
            if (botFace != null)
                botFaceData = new FaceData
                {
                    Elem = elem,
                    FaceRef = botFace.Reference,
                    FacePoint = Center(botFace),
                    LeftProj = minProj,
                    RightProj = maxProj,
                    IsTop = false
                };
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
        }

        // -----------------------------------------------------------------------
        /// <summary>
        /// Создаёт вид узла (Detail) вдоль всей длины стены из сборки.
        /// Секущая плоскость на мировой отметке cutZ (середина по высоте стены),
        /// взгляд направлен ВНИЗ. Тип вида: "Вид узла" → "*04_Стены_сечение".
        /// Применяется шаблон вида "*01_КЖ_(04_Стены)_Сечение".
        /// Имя вида: "{комментарий сборки}_Разрез_{n}-{n}".
        /// </summary>
        private void CreateWallSection(Document doc, AssemblyInstance assembly,
            Wall wall, string assemblyComment, double cutZ, XYZ viewRight, int seqIndex)
        {
            if (wall == null) return;

            LocationCurve locationCurve = wall.Location as LocationCurve;
            if (locationCurve == null) return;

            Line wallLine = locationCurve.Curve as Line;
            if (wallLine == null) return;

            // Геометрия стены
            XYZ wallStart = wallLine.GetEndPoint(0);
            XYZ wallEnd = wallLine.GetEndPoint(1);
            XYZ wallMid = (wallStart + wallEnd) / 2.0;
            double wallLength = wallLine.Length;
            double wallThickness = wall.Width;

            // Параметры разреза
            // ВНИМАНИЕ: размах по X задаёт и рамку подрезки, и положение головок
            // марки одновременно — в API их развязать нельзя. Держим узкие границы.
            double offsetLen = UnitUtils.ConvertToInternalUnits(500, UnitTypeId.Millimeters);   // запас по длине (подрезка + марки)
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

            // Origin — середина стены на отметке cutZ (между отметками 1 и 2)
            XYZ origin = new XYZ(wallMid.X, wallMid.Y, cutZ);

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
                -wallLength / 2.0 - offsetLen,      // X — левая граница (подрезка и левая марка)
                -wallThickness / 2.0 - offsetThk,   // Y — толщина стены
                -depthUp);                          // Z — небольшой запас выше секущей
            sectionBox.Max = new XYZ(
                 wallLength / 2.0 + offsetLen,      // X — правая граница (подрезка и правая марка)
                 wallThickness / 2.0 + offsetThk,
                 depthDown);                        // Z — глубина взгляда вниз

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

            if (detailType == null) return;

            string sectionName = GenerateSectionName(doc, assemblyComment, seqIndex);

            // CreateDetail создаёт вид семейства "Вид узла"
            ViewSection newSection = ViewSection.CreateDetail(doc, detailType.Id, sectionBox);

            newSection.Name = sectionName;

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