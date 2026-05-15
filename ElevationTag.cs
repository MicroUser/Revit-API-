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
            public bool IsWall { get; set; }
            public double Z => FacePoint.Z;
        }

        private const double ZTolerance = 1.0 / 304.8;

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

            // Шаг 2: разрыв вида
            double breakWorldBottom = UnitUtils.ConvertToInternalUnits(28500, UnitTypeId.Millimeters);
            double breakWorldTop = UnitUtils.ConvertToInternalUnits(50400, UnitTypeId.Millimeters);

            var mgr = section.GetCropRegionShapeManager();
            if (mgr.CanBeSplit)
            {
                double regionMin = mgr.GetSplitRegionMinimum(0);
                double regionMax = mgr.GetSplitRegionMaximum(0);
                double regionHeight = regionMax - regionMin;

                BoundingBoxXYZ cropBox = section.CropBox;
                Transform t = cropBox.Transform;
                double worldBottomZ = Math.Min(t.OfPoint(cropBox.Min).Z, t.OfPoint(cropBox.Max).Z);
                double worldHeight = Math.Max(t.OfPoint(cropBox.Min).Z, t.OfPoint(cropBox.Max).Z) - worldBottomZ;

                double breakLocalBottom = regionMin + (breakWorldBottom - worldBottomZ) / worldHeight * regionHeight;
                double breakLocalTop = regionMin + (breakWorldTop - worldBottomZ) / worldHeight * regionHeight;

                if (breakLocalBottom > regionMin && breakLocalTop < regionMax && breakLocalBottom < breakLocalTop)
                {
                    using (Transaction txBreak = new Transaction(doc, "Создать разрыв вида"))
                    {
                        txBreak.Start();
                        try
                        {
                            mgr.SplitRegionVertically(0, breakLocalBottom, breakLocalTop);

                            // Включаем отображение границ обрезки если отключены
                            if (!section.CropBoxActive) section.CropBoxActive = true;
                            if (!section.CropBoxVisible) section.CropBoxVisible = true;

                            txBreak.Commit();
                        }
                        catch { txBreak.RollBack(); }
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

            // Шаг 5: сбор граней с фильтрацией по скрытой зоне
            var allFaces = new List<FaceData>();
            double globalMinRight = double.MaxValue;
            double refDepth = 0;
            int depthCnt = 0;

            foreach (Element elem in walls.Concat(floors))
            {
                bool isWall = elem is Wall;
                GetTopAndBottomFaces(elem, section, rightVec, out FaceData topFace, out FaceData botFace);

                foreach (FaceData fd in new[] { topFace, botFace })
                {
                    if (fd == null) continue;
                    if (fd.Z > breakWorldBottom && fd.Z < breakWorldTop) continue;

                    fd.IsWall = isWall;
                    TryAddFace(allFaces, fd);
                    refDepth += ProjDepth(fd.FacePoint); depthCnt++;
                    if (isWall && fd.LeftProj < globalMinRight) globalMinRight = fd.LeftProj;
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
            double dimProj = globalMinRight - UnitUtils.ConvertToInternalUnits(700, UnitTypeId.Millimeters);
            double dimOddProj = globalMinRight - UnitUtils.ConvertToInternalUnits(1000, UnitTypeId.Millimeters);

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

                    SpotDimensionType typeToUse = (i % 2 == 0)
                        ? (spotTypeUp ?? spotTypeDown)
                        : (spotTypeDown ?? spotTypeUp);

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

                tx.Commit();
            }

            string resultMsg = $"Сборка: {assembly.Name}\n" +
                               $"Стен: {walls.Count}, плит: {floors.Count}\n" +
                               $"Создано отметок: {createdCount} из {allFaces.Count}";
            if (skippedCount > 0) resultMsg += $"\nПропущено: {skippedCount}";
            TaskDialog.Show("Готово", resultMsg);
            return Result.Succeeded;
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
                    LeftProj = minProj
                };
            if (botFace != null)
                botFaceData = new FaceData
                {
                    Elem = elem,
                    FaceRef = botFace.Reference,
                    FacePoint = Center(botFace),
                    LeftProj = minProj
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
    }
}