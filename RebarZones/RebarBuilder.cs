using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;

namespace LiraToRevit.Rebar
{
    /// <summary>
    /// Строит и размещает Rebar-элементы в Revit по уже готовым планам от BarLengthCalculator —
    /// координаты/длины он не считает, только превращает их в реальную геометрию: создаёт прямой
    /// или Г/П-образный стержень, задаёт раскладку массивом, пишет параметры "Зона_*", назначает
    /// рабочий набор, применяет сдвиг поперёк направления стержней (конфликт по длине между
    /// зонами — см. ZoneConflictResolver — либо между двумя половинами одной делённой зоны).
    /// </summary>
    public class RebarBuilder
    {
        private readonly Document _doc;
        private readonly PlacementSettings _s;
        private readonly WorksetId _worksetId;

        public RebarBuilder(Document doc, PlacementSettings settings, WorksetId worksetId)
        {
            _doc = doc;
            _s = settings;
            _worksetId = worksetId;
        }

        public void AssignWorkset(Element e)
        {
            if (_worksetId == null) return;
            var p = e.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);
            if (p != null && !p.IsReadOnly) p.Set(_worksetId.IntValue());
        }

        /// <summary>Сдвиг уже СОЗДАННОГО элемента поперёк направления стержней на
        /// ZoneConflictResolver.AcrossShiftMm — двигаем готовую геометрию напрямую
        /// (ElementTransformUtils.MoveElement), а не координаты до создания: любой пересчёт
        /// координат "в лоб" перебивается привязкой к сетке изолиний внутри BarLengthCalculator.</summary>
        public void ApplyAcrossShift(ElementId rebarId, Dir dir)
        {
            if (rebarId == null || rebarId == ElementId.InvalidElementId) return;
            XYZ move = dir == Dir.X ? new XYZ(0, RebarUnits.Mm(ZoneConflictResolver.AcrossShiftMm), 0) : new XYZ(RebarUnits.Mm(ZoneConflictResolver.AcrossShiftMm), 0, 0);
            ElementTransformUtils.MoveElement(_doc, rebarId, move);
        }

        /// <summary>Создаёт прямой стержень (Rebar, массив по band) — общая часть для обычной
        /// раскладки и обеих половин при делении длинной зоны (см. BarLengthCalculator.ComputeStraightSplit).</summary>
        public Autodesk.Revit.DB.Structure.Rebar CreateStraightRebarElement(
            Floor host, ZoneDef z, RebarBarType type, string mark, StraightBarPlan plan,
            double first, double zBar, int count, double step)
        {
            var curves = new List<Curve>
            {
                Line.CreateBound(PointOf(z.Dir, plan.P1, first, zBar), PointOf(z.Dir, plan.P2, first, zBar))
            };
            var normal = z.Dir == Dir.X ? XYZ.BasisY : XYZ.BasisX;  // направление раскладки массива
            var rebar = Autodesk.Revit.DB.Structure.Rebar.CreateFromCurves(
                _doc, RebarStyle.Standard, type, null, null, host,
                normal, curves, RebarHookOrientation.Right, RebarHookOrientation.Right,
                true, true);
            rebar.GetShapeDrivenAccessor()
                 .SetLayoutAsNumberWithSpacing(count, RebarUnits.Mm(step), true, true, true);

            // plan.Note != null — только у половин деления зоны: там нужен готовый текст (стык
            // внахлёст + опционально "требуется загиб"), поэтому WriteParams вызывается с hook=false
            // (не даём ему самому решить текст примечания) и текст пишется явно следом.
            bool hasNoteOverride = plan.Note != null;
            WriteParams(rebar, z, hasNoteOverride ? false : plan.NeedsHook, mark);
            if (hasNoteOverride) SetIfExists(rebar, "Зона_примечание", plan.Note);

            return rebar;
        }

        /// <summary>Создаёт Г- или П-образный стержень (Rebar, массив по band) от дальнего торца
        /// (плана BentBarPlan) до ближнего — общая часть для обычной раскладки бендового случая и
        /// ближнего массива при делении длинной зоны (см. BarLengthCalculator.ComputeBentSplit).
        /// useU/shape — уже решено снаружи (см. RebarPlacer — выбор формы зависит от пилонов/колонн,
        /// это не задача построителя).</summary>
        public Autodesk.Revit.DB.Structure.Rebar CreateBentRebarElement(
            Floor host, ZoneDef z, RebarBarType type, string mark, bool useU, RebarShape shape, BentBarPlan plan,
            double first, double zBar, int count, double step)
        {
            XYZ farPt = PointOf(z.Dir, plan.FarAlong, first, zBar);
            XYZ nearPt = PointOf(z.Dir, plan.NearAlong, first, zBar);
            XYZ xVec = (nearPt - farPt).Normalize();

            // Строим ломаную ЯВНО (а не через origin/xVec/yVec формы) — Rebar.CreateFromRebarShape
            // на практике укладывал Г/П-стержни не так, как нужно (неверная ориентация загиба).
            // CreateFromCurvesAndShape сам считает параметры формы (BI_A/BI_B/…) из геометрии
            // кривых, поэтому не нужно ни гадать систему координат формы, ни вручную выставлять
            // параметры (а при смене формы после создания их местами меняет сам Revit — этого
            // тоже избегаем).
            var curves = new List<Curve> { Line.CreateBound(farPt, nearPt) };
            XYZ depthPt = nearPt - XYZ.BasisZ.Multiply(RebarUnits.Mm(useU ? _s.UShapeDepthMm : _s.LShapeNoseMm));
            curves.Add(Line.CreateBound(nearPt, depthPt));
            if (useU)
            {
                // П-шка: третий сегмент — горизонтально назад вдоль основного стержня (к дальнему торцу)
                XYZ footPt = depthPt - xVec.Multiply(RebarUnits.Mm(_s.UShapeFootMm));
                curves.Add(Line.CreateBound(depthPt, footPt));
            }

            var arrayNormal = z.Dir == Dir.X ? XYZ.BasisY : XYZ.BasisX;  // направление раскладки массива
            var rebar = Autodesk.Revit.DB.Structure.Rebar.CreateFromCurvesAndShape(
                _doc, shape, type, null, null, host, arrayNormal, curves,
                RebarHookOrientation.Right, RebarHookOrientation.Right);
            if (rebar == null)
                throw new InvalidOperationException(
                    $"Зона {z.Id}: не удалось создать {(useU ? "П" : "Г")}-образный стержень по форме «{shape.Name}» — кривые не подошли под форму");

            rebar.GetShapeDrivenAccessor()
                 .SetLayoutAsNumberWithSpacing(count, RebarUnits.Mm(step), true, true, true);

            // Параметры формы (BI_A/BI_B/…), которые Revit вывел из кривых, на практике не
            // всегда совпадают с нужными значениями (напр. 125/126 мм вместо ровно 120 —
            // поправка на радиус загиба в самой форме) и роль параметра (какой отвечает за
            // основную длину, какой за носик) не гарантированно совпадает с именем BI_A/BI_B
            // по порядку. Сопоставляем по БЛИЗОСТИ величин (сортировка), затем выставляем
            // точные целевые значения принудительно.
            if (useU)
                FixShapeParams(rebar, new[] { "BI_A", "BI_B", "BI_C" },
                    new[] { plan.MainLenMm, _s.UShapeDepthMm, _s.UShapeFootMm });
            else
                FixShapeParams(rebar, new[] { "BI_A", "BI_B" },
                    new[] { plan.MainLenMm, _s.LShapeNoseMm });

            WriteParams(rebar, z, false, mark);
            string shapeNote = useU
                ? "П-образный (форма 21) — пилон/колонна вдоль края плиты"
                : "Г-образный (форма 11) — край плиты";
            SetIfExists(rebar, "Зона_примечание", (plan.Note != null ? plan.Note + "; " : "") + shapeNote);

            return rebar;
        }

        /// <summary>
        /// Принудительно выставляет параметры формы (BI_A/BI_B/…) в ожидаемые значения.
        /// Сопоставление "какой параметр — какая роль" делаем по близости величин (сортировка
        /// текущих авто-вычисленных значений и целевых по возрастанию, затем попарно), а не по
        /// имени параметра — на практике имя не гарантирует роль (см. диагностику: BI_A в форме
        /// 11 может быть носиком, а не основной длиной, и Revit сам их меняет местами при смене
        /// формы существующего стержня).
        /// </summary>
        private static void FixShapeParams(Autodesk.Revit.DB.Structure.Rebar rebar, string[] paramNames, double[] targetMm)
        {
            var pars = paramNames.Select(n => rebar.LookupParameter(n)).ToArray();
            var order = Enumerable.Range(0, pars.Length)
                .Where(i => pars[i] != null)
                .OrderBy(i => pars[i].AsDouble())
                .ToList();
            var targetOrder = targetMm.OrderBy(v => v).ToList();
            for (int k = 0; k < order.Count && k < targetOrder.Count; k++)
                pars[order[k]].Set(RebarUnits.Mm(targetOrder[k]));
        }

        private static XYZ PointOf(Dir dir, double along, double across, double zMm) =>
            dir == Dir.X ? new XYZ(RebarUnits.Mm(along), RebarUnits.Mm(across), RebarUnits.Mm(zMm))
                         : new XYZ(RebarUnits.Mm(across), RebarUnits.Mm(along), RebarUnits.Mm(zMm));

        /// <summary>
        /// Отметка оси стержня. Защитный слой в типе плиты = 0, поэтому FaceOffset (35 мм)
        /// отсчитывается до наружной грани. Доп. арматура считается лежащей рядом с фоновой
        /// (основной) сеткой, а не на голой поверхности плиты — у фона тоже двухслойная сетка
        /// X+Y толщиной FirstLayerThickness (диаметр фоновой арматуры, приходит из HTML):
        /// направление FirstLayer (первый слой фона) — offset = FaceOffset + FirstLayerThickness/2 + d/2;
        /// второе направление (лежит поверх ПЕРВОГО слоя фона целиком) — offset = FaceOffset + FirstLayerThickness + d/2.
        /// </summary>
        public double BarElevation(SlabGeometry slab, ZoneDef z)
        {
            double layer = (z.Dir == _s.FirstLayer) ? _s.FirstLayerThickness / 2.0 : _s.FirstLayerThickness;
            double d = z.Diameter;
            double off = _s.FaceOffset + layer + d / 2.0;
            return z.Face == Face.Top ? slab.TopMm - off : slab.BottomMm + off;
        }

        private void WriteParams(Autodesk.Revit.DB.Structure.Rebar r, ZoneDef z, bool hook, string mark)
        {
            SetIfExists(r, "Зона_ID", z.Id);
            SetIfExists(r, "Зона_As", z.AsCalc.ToString("0.00"));
            SetIfExists(r, "Зона_режим", z.ByMean ? "среднее" : "пик");
            SetIfExists(r, "Зона_источник", z.Source);
            if (hook) SetIfExists(r, "Зона_примечание", "анкеровка подрезана — требуется загиб");
            if (!string.IsNullOrEmpty(mark)) SetIfExists(r, "BI_марка_конструкции", mark);
        }

        private static void SetIfExists(Element e, string name, string value)
        {
            var p = e.LookupParameter(name);
            if (p != null && !p.IsReadOnly && p.StorageType == StorageType.String) p.Set(value);
        }
    }
}
