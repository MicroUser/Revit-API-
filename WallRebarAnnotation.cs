using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using System;
using System.Collections.Generic;
using System.Linq;

public class RebarSelectionFilter : ISelectionFilter
{
    public bool AllowElement(Element elem) => elem is Rebar;
    public bool AllowReference(Reference reference, XYZ position) => false;
}

[Transaction(TransactionMode.Manual)]
public class WallRebarAnnotation : IExternalCommand
{
    public Result Execute(
        ExternalCommandData commandData,
        ref string message,
        ElementSet elements)
    {
        UIDocument uidoc = commandData.Application.ActiveUIDocument;
        Document doc = uidoc.Document;
        View view = doc.ActiveView;

        var filter = new RebarSelectionFilter();

        // ── ШАГ 1: стержень для аннотации нескольких стержней ────────────────
        Reference ref1;
        try
        {
            ref1 = uidoc.Selection.PickObject(
                ObjectType.Element,
                filter,
                "Шаг 1 из 3: выберите стержень для «Аннотации нескольких стержней»");
        }
        catch (Autodesk.Revit.Exceptions.OperationCanceledException)
        {
            return Result.Cancelled;
        }

        Rebar rebar1 = doc.GetElement(ref1.ElementId) as Rebar;

        // ── ШАГ 2: точка размещения аннотации ────────────────────────────────
        // На разрезах рабочая плоскость не установлена по умолчанию —
        // задаём её вручную по плоскости вида перед вызовом PickPoint.
        XYZ annotationPoint;
        try
        {
            using (Transaction tPlane = new Transaction(doc, "Установка рабочей плоскости"))
            {
                tPlane.Start();
                Plane viewPlane = Plane.CreateByNormalAndOrigin(view.ViewDirection, view.Origin);
                SketchPlane sketchPlane = SketchPlane.Create(doc, viewPlane);
                view.SketchPlane = sketchPlane;
                tPlane.Commit();
            }

            annotationPoint = uidoc.Selection.PickPoint(
                "Шаг 2 из 3: укажите место размещения аннотации");
        }
        catch (Autodesk.Revit.Exceptions.OperationCanceledException)
        {
            return Result.Cancelled;
        }

        // ── ШАГ 3: стержень для марки по категории ───────────────────────────
        // Revit 2023+: тегировать можно только Subelement (отдельный стержень
        // внутри набора), а не весь Rebar-элемент целиком.
        Reference ref2;
        try
        {
            ref2 = uidoc.Selection.PickObject(
                ObjectType.Subelement,
                "Шаг 3 из 5: выберите стержень для «Марки по категории без выноски»");
        }
        catch (Autodesk.Revit.Exceptions.OperationCanceledException)
        {
            return Result.Cancelled;
        }

        Rebar rebar2 = doc.GetElement(ref2.ElementId) as Rebar;

        // ── КЭШ ТИПОВ АННОТАЦИЙ ───────────────────────────────────────────────
        string typeBigName = "шаг_количество_длина/поз.(_)";
        string typeSmallName = "шаг_количество_длина/поз.(_)";

        Dictionary<string, MultiReferenceAnnotationType> typeCache =
            new FilteredElementCollector(doc)
                .OfClass(typeof(MultiReferenceAnnotationType))
                .Cast<MultiReferenceAnnotationType>()
                .Where(x => x.Name == typeBigName || x.Name == typeSmallName)
                .ToDictionary(x => x.Name, x => x);

        var log = new System.Text.StringBuilder();

        // ── ТИП РАЗМЕРА ───────────────────────────────────────────────────────
        DimensionType rebarDimType = new FilteredElementCollector(doc)
            .OfClass(typeof(DimensionType))
            .Cast<DimensionType>()
            .FirstOrDefault(x => x.Name == "BI_основной_2,5мм_округление_до_5мм");

        using (Transaction t = new Transaction(doc, "Аннотации арматуры"))
        {
            t.Start();

            // REBAR 1 → MultiReferenceAnnotation
            XYZ dimDir1 = null;
            XYZ tagHead1 = null;
            CreateMultiReferenceAnnotation(doc, view, rebar1,
                typeCache, typeBigName, typeSmallName, annotationPoint, log,
                out dimDir1, out tagHead1);

            // REBAR 2 → IndependentTag без выноски
            CreateCategoryTag(doc, view, rebar2, ref2, tagHead1, log);

            t.Commit();
        }

        string diagInfo = log.Length > 0 ? $"\n\nДиагностика:\n{log}" : string.Empty;

        TaskDialog.Show("Готово",
            $"Аннотация нескольких стержней: стержень {rebar1.Id.IntegerValue}\n" +
            $"Марка по категории (без выноски): стержень {rebar2.Id.IntegerValue}" +
            diagInfo);

        return Result.Succeeded;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // MultiReferenceAnnotation
    // ─────────────────────────────────────────────────────────────────────────
    private static void CreateMultiReferenceAnnotation(
        Document doc, View view, Rebar rebar,
        Dictionary<string, MultiReferenceAnnotationType> typeCache,
        string typeBigName, string typeSmallName,
        XYZ annotationPoint,
        System.Text.StringBuilder log,
        out XYZ outDimDir,
        out XYZ outTagHeadPosition)
    {
        outDimDir = null;
        outTagHeadPosition = null;
        // Геометрия первого стержня в наборе (позиция 0)
        IList<Curve> curves = rebar.GetCenterlineCurves(
            false, false, false, MultiplanarOption.IncludeOnlyPlanarCurves, 0);

        if (curves == null || curves.Count == 0)
        {
            log.AppendLine($"Rebar {rebar.Id}: нет кривых центральной оси.");
            return;
        }

        GetRebarDirAndMid(curves, out XYZ rebarDir, out XYZ firstBarMid);

        XYZ dimDir = SafeCrossProduct(rebarDir, view.ViewDirection);
        if (dimDir == null)
        {
            log.AppendLine($"Rebar {rebar.Id}: стержень перпендикулярен плоскости вида.");
            return;
        }
        outDimDir = dimDir;

        // ── Шаг и количество ─────────────────────────────────────────────────
        int count = rebar.NumberOfBarPositions;
        double spacing = 0;
        Parameter sp = rebar.get_Parameter(BuiltInParameter.REBAR_ELEM_BAR_SPACING);
        if (sp != null && sp.HasValue) spacing = sp.AsDouble(); // в футах

        double spacingMm = UnitUtils.ConvertFromInternalUnits(spacing, UnitTypeId.Millimeters);
        double totalSpanMm = (count > 1 && spacing > 0) ? (count - 1) * spacingMm : 0;
        double halfSpanMm = totalSpanMm / 2.0;
        double halfSpanFt = UnitUtils.ConvertToInternalUnits(halfSpanMm, UnitTypeId.Millimeters);

        // ── Центр массива: первый стержень + dimDir * halfSpan ────────────────
        // dimDir направлен в сторону распределения стержней в массиве,
        // поэтому сдвигаем от первого стержня на половину суммарного шага.
        XYZ arrayCenter = firstBarMid + dimDir * halfSpanFt;

        // Единственный тип аннотации
        MultiReferenceAnnotationType typeToUse = null;
        typeCache.TryGetValue(typeBigName, out typeToUse);

        if (typeToUse == null)
        {
            log.AppendLine($"Rebar {rebar.Id}: тип MultiReferenceAnnotation не найден.");
            return;
        }

        var options = new MultiReferenceAnnotationOptions(typeToUse);
        options.SetElementsToDimension(new List<ElementId> { rebar.Id });
        options.DimensionPlaneNormal = view.ViewDirection;
        options.DimensionLineDirection = dimDir;

        // DimensionLineOrigin — точка, указанная пользователем (где лежит линия размера).
        XYZ origin = ProjectOntoViewPlane(annotationPoint, view);
        options.DimensionLineOrigin = origin;

        // TagHeadPosition — геометрический центр массива, вычисленный через шаг и количество.
        // Проецируем arrayCenter на линию размера (сохраняем компонент вдоль rebarDir от origin,
        // но берём компонент вдоль dimDir от arrayCenter).
        double arrayCenterOnDimDir = arrayCenter.DotProduct(dimDir);
        double originOnDimDir = origin.DotProduct(dimDir);
        XYZ tagHead = origin + dimDir * (arrayCenterOnDimDir - originOnDimDir);
        options.TagHeadPosition = tagHead;
        outTagHeadPosition = tagHead;

        try
        {
            MultiReferenceAnnotation.Create(doc, view.Id, options);
        }
        catch (Exception ex)
        {
            log.AppendLine($"Rebar {rebar.Id}: ошибка создания аннотации — {ex.Message}");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // IndependentTag без выноски (марка по категории)
    // ─────────────────────────────────────────────────────────────────────────
    private static void CreateCategoryTag(
        Document doc, View view, Rebar rebar, Reference subelemRef,
        XYZ tagHead1,
        System.Text.StringBuilder log)
    {
        FamilySymbol tagSymbol = new FilteredElementCollector(doc)
            .OfClass(typeof(FamilySymbol))
            .OfCategory(BuiltInCategory.OST_RebarTags)
            .Cast<FamilySymbol>()
            .FirstOrDefault(fs =>
                fs.Name.Equals("Позиция_(_)_без полки", StringComparison.OrdinalIgnoreCase));

        if (tagSymbol == null)
        {
            log.AppendLine($"Rebar {rebar.Id}: не найден тип марки «Позиция_(_)_без полки» " +
                           "в категории OST_RebarTags.");
            return;
        }

        if (!tagSymbol.IsActive)
            tagSymbol.Activate();

        double offsetUpFt = UnitUtils.ConvertToInternalUnits(350, UnitTypeId.Millimeters);
        double offsetRightFt = UnitUtils.ConvertToInternalUnits(225, UnitTypeId.Millimeters);
        XYZ basePos = tagHead1 ?? (rebar.get_BoundingBox(view) is BoundingBoxXYZ bb2
            ? (bb2.Min + bb2.Max) / 2.0 : XYZ.Zero);
        XYZ tagPos = basePos + view.UpDirection * offsetUpFt + view.RightDirection * offsetRightFt;

        try
        {
            // Revit 2023+: передаём Subelement Reference от PickObject(ObjectType.Subelement).
            IndependentTag tag = IndependentTag.Create(
                doc,
                tagSymbol.Id,
                view.Id,
                subelemRef,
                false,                  // hasLeader = false → БЕЗ ВЫНОСКИ
                TagOrientation.Vertical, // вертикальное расположение текста
                tagPos);

            tag.HasLeader = false;
        }
        catch (Exception ex)
        {
            log.AppendLine($"Rebar {rebar.Id}: ошибка создания марки — {ex.Message}");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Размер от крайних стержней rebar1 до границ стены-хоста
    // ─────────────────────────────────────────────────────────────────────────
    private static void CreateWallBoundaryDimension(
        Document doc, View view, Rebar rebar,
        XYZ annotationPoint, XYZ dimDir,
        Reference refWallBottom, Reference refWallTop,
        Reference refRebarFirst, Reference refRebarLast,
        DimensionType dimType,
        System.Text.StringBuilder log)
    {
        // Геометрия первого стержня для определения направления и точки линии размера
        IList<Curve> firstCurves = rebar.GetCenterlineCurves(
            false, false, false, MultiplanarOption.IncludeOnlyPlanarCurves, 0);

        if (firstCurves == null || firstCurves.Count == 0)
        {
            log.AppendLine($"Rebar {rebar.Id}: нет кривых центральной оси.");
            return;
        }

        GetRebarDirAndMid(firstCurves, out XYZ rebarDir, out XYZ firstMid);

        // Линия размера — на той же горизонтальной позиции что и аннотация
        XYZ ap = ProjectOntoViewPlane(annotationPoint, view);
        double pos = ap.DotProduct(dimDir);
        XYZ dimMid = firstMid + dimDir * (pos - firstMid.DotProduct(dimDir));

        Line dimLine = TryCreateDimLine(dimMid, rebarDir, view.Origin.Z);
        if (dimLine == null)
        {
            log.AppendLine($"Rebar {rebar.Id}: не удалось создать линию размера.");
            return;
        }

        // Цепочка: низ стены → первый стержень → последний стержень → верх стены
        var ra = new ReferenceArray();
        ra.Append(refWallBottom);
        ra.Append(refRebarFirst);
        ra.Append(refRebarLast);
        ra.Append(refWallTop);

        try
        {
            Dimension dim = dimType != null
                ? doc.Create.NewDimension(view, dimLine, ra, dimType)
                : doc.Create.NewDimension(view, dimLine, ra);

            if (dim == null)
                log.AppendLine($"Rebar {rebar.Id}: NewDimension вернул null.");
        }
        catch (Exception ex)
        {
            log.AppendLine($"Rebar {rebar.Id}: ОШИБКА NewDimension — {ex.Message}");
        }
    }

    // ─── Вспомогательные методы ───────────────────────────────────────────────

    private static void GetRebarDirAndMid(IList<Curve> curves, out XYZ dir, out XYZ mid)
    {
        if (curves.Count == 1 && curves[0] is Line sl)
        {
            dir = (sl.GetEndPoint(1) - sl.GetEndPoint(0)).Normalize();
            mid = (sl.GetEndPoint(0) + sl.GetEndPoint(1)) / 2.0;
            return;
        }

        XYZ chord = curves.Last().GetEndPoint(1) - curves.First().GetEndPoint(0);
        dir = chord.GetLength() > 1e-6
            ? chord.Normalize()
            : curves[0].ComputeDerivatives(0.5, true).BasisX.Normalize();

        var pts = new List<XYZ>();
        foreach (Curve c in curves) pts.AddRange(c.Tessellate());
        mid = new XYZ(
            (pts.Min(p => p.X) + pts.Max(p => p.X)) / 2.0,
            (pts.Min(p => p.Y) + pts.Max(p => p.Y)) / 2.0,
            (pts.Min(p => p.Z) + pts.Max(p => p.Z)) / 2.0);
    }

    private static XYZ GetCurvesMidPoint(IList<Curve> curves)
    {
        if (curves.Count == 1 && curves[0] is Line line)
            return (line.GetEndPoint(0) + line.GetEndPoint(1)) / 2.0;

        var pts = new List<XYZ>();
        foreach (Curve c in curves) pts.AddRange(c.Tessellate());
        return new XYZ(
            (pts.Min(p => p.X) + pts.Max(p => p.X)) / 2.0,
            (pts.Min(p => p.Y) + pts.Max(p => p.Y)) / 2.0,
            (pts.Min(p => p.Z) + pts.Max(p => p.Z)) / 2.0);
    }

    private static Line TryCreateDimLine(XYZ mid, XYZ dir, double z)
    {
        if (dir.GetLength() < 1e-9) return null;
        XYZ d = dir.Normalize();
        double half = 2.0; // 2 фута — заведомо достаточно
        XYZ p1 = new XYZ((mid - d * half).X, (mid - d * half).Y, z);
        XYZ p2 = new XYZ((mid + d * half).X, (mid + d * half).Y, z);
        if ((p2 - p1).GetLength() < 0.1) return null;
        try { return Line.CreateBound(p1, p2); }
        catch { return null; }
    }

    private static XYZ SafeCrossProduct(XYZ a, XYZ b)
    {
        XYZ cross = a.CrossProduct(b);
        return cross.GetLength() > 1e-6 ? cross.Normalize() : null;
    }

    /// <summary>
    /// Проецирует точку на плоскость вида, убирая компонент вдоль ViewDirection.
    /// Это нужно чтобы точка, подобранная PickPoint, лежала строго в плоскости вида.
    /// </summary>
    private static XYZ ProjectOntoViewPlane(XYZ point, View view)
    {
        XYZ origin = view.Origin;
        XYZ normal = view.ViewDirection;
        double dist = (point - origin).DotProduct(normal);
        return point - normal * dist;
    }
}