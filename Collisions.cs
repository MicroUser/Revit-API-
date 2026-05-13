using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;

namespace DAN_Plugin
{
    [Transaction(TransactionMode.Manual)]
    public class CreateCollisionFamily : IExternalCommand
    {
        public Result Execute(
            ExternalCommandData commandData,
            ref string message,
            ElementSet elements)
        {
            Document doc =
                commandData.Application
                .ActiveUIDocument
                .Document;

            Options options = new Options
            {
                DetailLevel = ViewDetailLevel.Fine,
                ComputeReferences = true,
                IncludeNonVisibleObjects = true
            };

            try
            {
                // =====================================================
                // 1. Все стены
                // =====================================================

                List<Wall> walls =
                    new FilteredElementCollector(doc)
                    .OfClass(typeof(Wall))
                    .Cast<Wall>()
                    .ToList();

                if (!walls.Any())
                {
                    TaskDialog.Show(
                        "Ошибка",
                        "Стены не найдены");

                    return Result.Cancelled;
                }

                // =====================================================
                // 2. Все трубы из связей
                // =====================================================

                List<(Element Pipe, Transform Transform)> linkedPipes =
                    new List<(Element, Transform)>();

                foreach (RevitLinkInstance link in
                    new FilteredElementCollector(doc)
                    .OfClass(typeof(RevitLinkInstance))
                    .Cast<RevitLinkInstance>())
                {
                    Document linkDoc =
                        link.GetLinkDocument();

                    if (linkDoc == null)
                        continue;

                    Transform linkTransform =
                        link.GetTransform();

                    List<Element> pipes =
                        new FilteredElementCollector(linkDoc)
                        .OfCategory(BuiltInCategory.OST_PipeCurves)
                        .WhereElementIsNotElementType()
                        .ToElements()
                        .ToList();

                    foreach (Element pipe in pipes)
                    {
                        linkedPipes.Add(
                            (pipe, linkTransform));
                    }
                }

                if (!linkedPipes.Any())
                {
                    TaskDialog.Show(
                        "Ошибка",
                        "Трубы в связях не найдены");

                    return Result.Cancelled;
                }

                // =====================================================
                // 3. Семейство
                // =====================================================

                FamilySymbol symbol =
                    new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilySymbol))
                    .Cast<FamilySymbol>()
                    .FirstOrDefault(x =>
                        x.Family.Name.Equals(
                            "Коллизия_R22",
                            StringComparison.OrdinalIgnoreCase));

                if (symbol == null)
                {
                    TaskDialog.Show(
                        "Ошибка",
                        "Семейство Коллизии не найдено");

                    return Result.Failed;
                }

                int createdCount = 0;

                // =====================================================
                // 4. Основная транзакция
                // =====================================================

                using (Transaction t =
                    new Transaction(doc, "Создание коллизий"))
                {
                    t.Start();

                    if (!symbol.IsActive)
                    {
                        symbol.Activate();
                        doc.Regenerate();
                    }

                    // =====================================================
                    // УПРОЩЕННАЯ ЛОГИКА:
                    // только BoundingBox пересечение
                    // =====================================================

                    foreach (Wall wall in walls)
                    {
                        BoundingBoxXYZ wallBox =
                            wall.get_BoundingBox(null);

                        if (wallBox == null)
                            continue;

                        foreach (var pipeData in linkedPipes)
                        {
                            Element pipe =
                                pipeData.Pipe;

                            Transform transform =
                                pipeData.Transform;

                            BoundingBoxXYZ pipeBox =
                                pipe.get_BoundingBox(null);

                            if (pipeBox == null)
                                continue;

                            // Перевод bbox трубы
                            XYZ pipeMin =
                                transform.OfPoint(pipeBox.Min);

                            XYZ pipeMax =
                                transform.OfPoint(pipeBox.Max);

                            BoundingBoxXYZ hostPipeBox =
                                new BoundingBoxXYZ();

                            hostPipeBox.Min = pipeMin;
                            hostPipeBox.Max = pipeMax;

                            // =================================================
                            // Только bbox intersect
                            // =================================================

                            bool intersects =
                                CollisionFunctions
                                .BoundingBoxesIntersect(
                                    wallBox,
                                    hostPipeBox);

                            if (!intersects)
                                continue;

                            // =================================================
                            // Центр пересечения (пока центр стены)
                            // =================================================

                            double minX = Math.Max(wallBox.Min.X, hostPipeBox.Min.X);
                            double minY = Math.Max(wallBox.Min.Y, hostPipeBox.Min.Y);
                            double minZ = Math.Max(wallBox.Min.Z, hostPipeBox.Min.Z);

                            double maxX = Math.Min(wallBox.Max.X, hostPipeBox.Max.X);
                            double maxY = Math.Min(wallBox.Max.Y, hostPipeBox.Max.Y);
                            double maxZ = Math.Min(wallBox.Max.Z, hostPipeBox.Max.Z);

                            XYZ center = new XYZ(
                                (minX + maxX) * 0.5,
                                (minY + maxY) * 0.5,
                                (minZ + maxZ) * 0.5);

                            // =================================================
                            // Временно БЕЗ duplicate check
                            // =================================================

                            doc.Create.NewFamilyInstance(
                                center,
                                symbol,
                                StructuralType.NonStructural);

                            createdCount++;
                        }
                    }

                    t.Commit();
                }

                TaskDialog.Show(
                    "Готово",
                    $"Создано семейств: {createdCount}");

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show(
                    "Ошибка",
                    ex.ToString());

                return Result.Failed;
            }
        }
    }
}