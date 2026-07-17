using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

namespace DAN_Plugin
{
    [Transaction(TransactionMode.Manual)]
    public class StairLandingExitsCommand : IExternalCommand
    {
        const string ShapeName   = "(форма)11";
        const string ParamMark   = "BI_марка_конструкции";
        const string ParamFilter = "BI_фильтр_арматуры";
        const string FilterValue = "Армирование основное";
        const double MaxWallGapMm = 50; // допустимый зазор до ближней грани стены, дальше — считаем, что стены с этой стороны нет

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var uiDoc = commandData.Application.ActiveUIDocument;
            var doc   = uiDoc.Document;

            // 1. Форма арматуры "(форма)11"
            var rebarShape = new FilteredElementCollector(doc)
                .OfClass(typeof(RebarShape))
                .Cast<RebarShape>()
                .FirstOrDefault(s => s.Name == ShapeName);
            if (rebarShape == null)
            {
                TaskDialog.Show("Ошибка", $"Форма '{ShapeName}' не найдена в проекте.");
                return Result.Failed;
            }

            // 2. Типоразмеры "Детали" — для диалога
            // Имя формата "(арматура)детали_d=16_А500"
            var namePattern = new System.Text.RegularExpressions.Regex(
                @"^\(арматура\)детали_d=\d+_А500", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            var detailBarTypes = new FilteredElementCollector(doc)
                .OfClass(typeof(RebarBarType))
                .Cast<RebarBarType>()
                .Where(bt => namePattern.IsMatch(bt.Name))
                .GroupBy(bt => Math.Round(
                    bt.get_Parameter(BuiltInParameter.REBAR_BAR_DIAMETER)?.AsDouble() ?? 0, 6))
                .Select(g => g.First())
                .ToList();

            if (!detailBarTypes.Any())
            {
                TaskDialog.Show("Ошибка", $"Не найдены типоразмеры арматуры с {ParamFilter}='Детали'.");
                return Result.Failed;
            }

            // 3. Выбор сборки
            AssemblyInstance assembly;
            try
            {
                var pickedRef = uiDoc.Selection.PickObject(
                    ObjectType.Element,
                    new AssemblyFilter(),
                    "Выберите сборку лестничной площадки");
                assembly = doc.GetElement(pickedRef) as AssemblyInstance;
            }
            catch (OperationCanceledException) { return Result.Cancelled; }

            // 4. Диалог: выбор диаметра, BI_A, BI_B
            var dlg = new StairLandingExitsWindow(detailBarTypes);
            if (dlg.ShowDialog() != true) return Result.Cancelled;

            var    barType  = dlg.SelectedBarType;
            double aLen     = dlg.ALength;
            double bLen     = dlg.BLength;
            double exitDiam = barType.get_Parameter(BuiltInParameter.REBAR_BAR_DIAMETER)?.AsDouble() ?? 0;
            double cover    = UnitUtils.ConvertToInternalUnits(25, UnitTypeId.Millimeters) + exitDiam / 2.0;

            // 5. Комментарий сборки = марка конструкции
            string mark = assembly
                .get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)
                ?.AsString();
            if (string.IsNullOrWhiteSpace(mark))
            {
                TaskDialog.Show("Ошибка", "У выбранной сборки не заполнен комментарий.");
                return Result.Failed;
            }

            // 6. Основные стержни: BI_марка_конструкции = mark, BI_фильтр_арматуры = "Армирование основное"
            var mainBars = new FilteredElementCollector(doc)
                .OfClass(typeof(Rebar))
                .Cast<Rebar>()
                .Where(r =>
                {
                    var pMark = r.LookupParameter(ParamMark);
                    if (pMark == null || pMark.AsString() != mark) return false;
                    var rType   = doc.GetElement(r.GetTypeId()) as RebarBarType;
                    var pFilter = rType?.LookupParameter(ParamFilter);
                    return pFilter != null && pFilter.AsString() == FilterValue;
                })
                .ToList();

            if (!mainBars.Any())
            {
                TaskDialog.Show("Ошибка",
                    $"Не найдена арматура с {ParamMark}='{mark}' и {ParamFilter}='{FilterValue}'.");
                return Result.Failed;
            }

            // 7. Плита из состава сборки — хост для выпусков
            Element slabHost = assembly.GetMemberIds()
                .Select(id => doc.GetElement(id))
                .FirstOrDefault(e => e is Floor);

            if (slabHost == null)
            {
                TaskDialog.Show("Ошибка", "В составе сборки не найдена плита (Floor).");
                return Result.Failed;
            }

            var allWalls = new FilteredElementCollector(doc)
                .OfClass(typeof(Wall)).Cast<Wall>().ToList();

            int created    = 0;
            var errors     = new List<string>();
            var createdIds = new List<ElementId>();

            using (var tx = new Transaction(doc, "Выпуски из лестничной площадки"))
            {
                tx.Start();

                foreach (var bar in mainBars)
                {
                    try
                    {
                        int layoutRuleInt = bar.get_Parameter(BuiltInParameter.REBAR_ELEM_LAYOUT_RULE)
                                              ?.AsInteger() ?? (int)RebarLayoutRule.FixedNumber;
                        var layoutRule = (RebarLayoutRule)layoutRuleInt;

                        int quantity = bar.get_Parameter(BuiltInParameter.REBAR_ELEM_QUANTITY_OF_BARS)
                                          ?.AsInteger() ?? 1;
                        if (quantity <= 1) continue;

                        double spacing     = bar.get_Parameter(BuiltInParameter.REBAR_ELEM_BAR_SPACING)?.AsDouble() ?? 0;
                        double arrayLength = (quantity - 1) * spacing;

                        var curves0 = bar.GetCenterlineCurves(false, true, false,
                                          MultiplanarOption.IncludeAllMultiplanarCurves, 0);
                        if (!curves0.Any()) continue;
                        var bodyLine0 = curves0.First() as Line;
                        if (bodyLine0 == null) continue;
                        XYZ barDir  = bodyLine0.Direction.Normalize();
                        XYZ ptEnd   = curves0.Last().GetEndPoint(1);
                        XYZ ptStart = curves0.First().GetEndPoint(0);

                        XYZ distDir = XYZ.BasisZ;
                        var curves1 = bar.GetCenterlineCurves(false, true, false,
                                          MultiplanarOption.IncludeAllMultiplanarCurves, 1);
                        if (curves1.Any())
                        {
                            var diff = curves1.First().GetEndPoint(0) - curves0.First().GetEndPoint(0);
                            if (diff.GetLength() > 1e-6) distDir = diff.Normalize();
                        }

                        foreach (var (origin, xVec, mirrorAfter) in new (XYZ, XYZ, bool)[]
                        {
                            (ptEnd,   barDir,          true),
                            (ptStart, barDir.Negate(), false),
                        })
                        {
                            var wr = FindWallInDirection(origin, xVec, allWalls);
                            if (wr.Wall == null) continue; // стены с этой стороны плиты нет — выпуск не создаём

                            double distToAxis = wr.Dist;
                            double wallThick  = wr.Wall.Width;
                            double gapToNearFace = distToAxis - wallThick / 2.0;
                            double maxGap = UnitUtils.ConvertToInternalUnits(MaxWallGapMm, UnitTypeId.Millimeters);
                            if (gapToNearFace > maxGap) continue; // ближайшая стена слишком далеко — не примыкает к плите

                            double insertionOffset = aLen - distToAxis - wallThick / 2.0 + cover;
                            XYZ adjustedOrigin = origin.Subtract(xVec.Multiply(insertionOffset));

                            XYZ yVec = ComputeYVec(xVec);

                            var newBar = Rebar.CreateFromRebarShape(
                                doc, rebarShape, barType, slabHost,
                                adjustedOrigin, xVec, yVec);
                            if (newBar == null) continue;

                            newBar.LookupParameter("BI_A")?.Set(aLen);
                            newBar.LookupParameter("BI_B")?.Set(bLen);

                            var newAcc = newBar.GetShapeDrivenAccessor();
                            if (newAcc != null)
                            {
                                bool bOn = newAcc.Normal.DotProduct(distDir) > 0;
                                ApplyLayout(newAcc, layoutRule, quantity, spacing, arrayLength, bOn);
                                doc.Regenerate();

                                if (mirrorAfter)
                                {
                                    XYZ planeNormal = barDir.CrossProduct(XYZ.BasisZ);
                                    if (planeNormal.GetLength() < 1e-6)
                                        planeNormal = barDir.CrossProduct(XYZ.BasisX);
                                    planeNormal = planeNormal.Normalize();

                                    var cA0 = newBar.GetCenterlineCurves(false, true, false,
                                                  MultiplanarOption.IncludeAllMultiplanarCurves, 0);
                                    XYZ mirrorPt = cA0.Any()
                                        ? cA0.First().Evaluate(0.5, true)
                                        : adjustedOrigin;
                                    var plane = Plane.CreateByNormalAndOrigin(planeNormal, mirrorPt);
                                    ElementTransformUtils.MirrorElements(
                                        doc, new List<ElementId> { newBar.Id }, plane, false);
                                    doc.Regenerate();
                                }

                                var cA = newBar.GetCenterlineCurves(false, true, false,
                                             MultiplanarOption.IncludeAllMultiplanarCurves, 0);
                                if (cA.Any())
                                {
                                    // Коррекция по distDir (совмещение с плоскостью основного стержня)
                                    XYZ mainPt0 = curves0.First().GetEndPoint(0);
                                    double needZ = mainPt0.DotProduct(distDir);
                                    double haveZ = cA.First().GetEndPoint(0).DotProduct(distDir);

                                    // Коррекция по xVec: измеряем фактический "носик" выпуска после
                                    // применения BI_A/BI_B (формула insertionOffset верна только когда
                                    // BI_A == BI_B, т.к. геометрия формы "(форма)11" зависит от обоих
                                    // параметров), и довыставляем зазор cover от дальней грани стены точно.
                                    double haveTip = cA.SelectMany(c => new[] { c.GetEndPoint(0), c.GetEndPoint(1) })
                                                        .Max(p => p.DotProduct(xVec));
                                    double wallAxisProj = origin.DotProduct(xVec) + distToAxis;
                                    double needTip = wallAxisProj + wallThick / 2.0 - cover;

                                    XYZ delta = distDir.Multiply(needZ - haveZ)
                                                        .Add(xVec.Multiply(needTip - haveTip));
                                    if (delta.GetLength() > 1e-6)
                                        ElementTransformUtils.MoveElement(
                                            doc, newBar.Id, delta);
                                }
                            }

                            createdIds.Add(newBar.Id);
                            created++;
                        }
                    }
                    catch (Exception ex) { errors.Add($"Rebar {bar.Id}: {ex.Message}"); }
                }

                // Рабочий набор ".#09_Арм_Лестницы"
                int? wsInt = null;
                if (doc.IsWorkshared)
                {
                    var ws = new FilteredWorksetCollector(doc)
                        .OfKind(WorksetKind.UserWorkset)
                        .Cast<Workset>()
                        .FirstOrDefault(w => w.Name == ".#09_Арм_Лестницы");
                    wsInt = ws?.Id.IntegerValue;
                }
                if (wsInt.HasValue)
                {
                    foreach (var id in createdIds)
                        doc.GetElement(id)
                           ?.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM)
                           ?.Set(wsInt.Value);
                }

                if (createdIds.Count > 0)
                {
                    try
                    {
                        Group group = doc.Create.NewGroup(createdIds);
                        string groupName = $"{mark}_Выпуски";
                        var existingType = new FilteredElementCollector(doc)
                            .OfClass(typeof(GroupType))
                            .Cast<GroupType>()
                            .FirstOrDefault(gt => gt.Name == groupName);
                        if (existingType == null)
                            group.GroupType.Name = groupName;
                        if (wsInt.HasValue)
                            group.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM)
                                 ?.Set(wsInt.Value);
                    }
                    catch (Exception ex) { errors.Add($"Группа: {ex.Message}"); }
                }

                tx.Commit();
            }

            string result = $"Создано выпусков: {created}";
            if (errors.Any())
                result += $"\nОшибок: {errors.Count}\n{string.Join("\n", errors.Take(5))}";
            TaskDialog.Show("Выпуски лестничной площадки", result);
            return Result.Succeeded;
        }

        static (Wall Wall, double Dist) FindWallInDirection(XYZ origin, XYZ direction, IList<Wall> walls)
        {
            Wall best = null;
            double minT = double.MaxValue;
            foreach (var w in walls)
            {
                if (!(w.Location is LocationCurve lc)) continue;
                XYZ p0 = lc.Curve.GetEndPoint(0);
                XYZ p1 = lc.Curve.GetEndPoint(1);
                XYZ wallDir = p1 - p0;
                double wallLen = wallDir.GetLength();
                if (wallLen < 1e-6) continue;
                wallDir = wallDir.Multiply(1.0 / wallLen);

                XYZ wallNormal = new XYZ(-wallDir.Y, wallDir.X, 0);
                double wnLen = wallNormal.GetLength();
                if (wnLen < 1e-6) continue;
                wallNormal = wallNormal.Multiply(1.0 / wnLen);

                double denom = direction.DotProduct(wallNormal);
                if (Math.Abs(denom) < 1e-3) continue;

                double t = (p0 - origin).DotProduct(wallNormal) / denom;
                if (t < -(w.Width / 2.0) - 0.01) continue;

                // Стена — отрезок, а не бесконечная линия: точка пересечения должна лежать
                // в пределах самого сегмента стены (с небольшим допуском на стык стен в углах).
                XYZ hitPoint = origin.Add(direction.Multiply(t));
                double s = hitPoint.Subtract(p0).DotProduct(wallDir);
                double edgeTol = w.Width; // допуск ~толщина стены на угловые примыкания
                if (s < -edgeTol || s > wallLen + edgeTol) continue;

                if (t < minT) { minT = t; best = w; }
            }
            return (best, minT == double.MaxValue ? 0.0 : minT);
        }

        static void ApplyLayout(RebarShapeDrivenAccessor acc, RebarLayoutRule rule,
                                int quantity, double spacing, double arrayLength, bool bOn)
        {
            switch (rule)
            {
                case RebarLayoutRule.FixedNumber:
                    acc.SetLayoutAsFixedNumber(quantity, arrayLength, bOn, true, true); break;
                case RebarLayoutRule.MaximumSpacing:
                    acc.SetLayoutAsMaximumSpacing(spacing, arrayLength, bOn, true, true); break;
                case RebarLayoutRule.MinimumClearSpacing:
                    acc.SetLayoutAsMinimumClearSpacing(spacing, arrayLength, bOn, true, true); break;
                case RebarLayoutRule.NumberWithSpacing:
                    acc.SetLayoutAsNumberWithSpacing(quantity, spacing, bOn, true, true); break;
                default:
                    acc.SetLayoutAsFixedNumber(quantity, arrayLength, bOn, true, true); break;
            }
        }

        static XYZ ComputeYVec(XYZ xVec)
        {
            XYZ down = XYZ.BasisZ.Negate();
            double dot = down.DotProduct(xVec);
            XYZ y = down - xVec.Multiply(dot);
            if (y.GetLength() < 1e-6)
                y = xVec.CrossProduct(XYZ.BasisY);
            return y.Normalize();
        }

        private class AssemblyFilter : ISelectionFilter
        {
            public bool AllowElement(Element elem) => elem is AssemblyInstance;
            public bool AllowReference(Reference reference, XYZ position) => false;
        }
    }
}
