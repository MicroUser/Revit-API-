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
            var normal = z.Dir == Dir.X ? XYZ.BasisY : XYZ.BasisX;
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
        /// это не задача построителя). bendMm — носик Г / глубина П (BI_B), уже подобранный
        /// снаружи по толщине плиты (см. PlacementSettings.BendSizesFor) — построитель не решает,
        /// какая толщина у какой плиты, только получает готовое значение.</summary>
        public Autodesk.Revit.DB.Structure.Rebar CreateBentRebarElement(
            Floor host, ZoneDef z, RebarBarType type, string mark, bool useU, RebarShape shape, BentBarPlan plan,
            double first, double zBar, int count, double step, double bendMm)
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
            XYZ depthPt = nearPt - XYZ.BasisZ.Multiply(RebarUnits.Mm(bendMm));
            curves.Add(Line.CreateBound(nearPt, depthPt));
            if (useU)
            {
                // П-шка: третий сегмент — горизонтально назад вдоль основного стержня (к дальнему торцу)
                XYZ footPt = depthPt - xVec.Multiply(RebarUnits.Mm(_s.UShapeFootMm));
                curves.Add(Line.CreateBound(depthPt, footPt));
            }

            var arrayNormal = z.Dir == Dir.X ? XYZ.BasisY : XYZ.BasisX;
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
                    new[] { plan.MainLenMm, bendMm, _s.UShapeFootMm });
            else
                FixShapeParams(rebar, new[] { "BI_A", "BI_B" },
                    new[] { plan.MainLenMm, bendMm });

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
        /// Параметр может оказаться read-only — семейство формы иногда вычисляет один из
        /// размеров формулой по остальным/по геометрии переданных кривых (не по независимому
        /// значению); Revit уже выставил его сам из curves в CreateFromCurvesAndShape, поэтому
        /// такой параметр просто пропускаем, а не пытаемся перезаписать (иначе Parameter.Set
        /// кидает "the parameter is read-only" и рушит всю транзакцию размещения).
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
            {
                var p = pars[order[k]];
                if (p.IsReadOnly) continue;
                p.Set(RebarUnits.Mm(targetOrder[k]));
            }
        }

        /// <summary>Точка стержня в реальных (xMm,yMm) на заданной отметке zMm — один Z на весь
        /// элемент (стержень прямой и горизонтальный), см. BandZAt.</summary>
        private static XYZ PointOf(Dir dir, double along, double across, double zMm)
        {
            double xMm = dir == Dir.X ? along : across;
            double yMm = dir == Dir.X ? across : along;
            return new XYZ(RebarUnits.Mm(xMm), RebarUnits.Mm(yMm), RebarUnits.Mm(zMm));
        }

        /// <summary>4 угловые отметки ЗАДАННОЙ грани (без офсета арматуры) прямоугольника
        /// along1..along2 × acrossFrom..acrossTo — общий геометрический хелпер для BandZAt
        /// (своя грань z.Face) и ComputeRungs (своя грань — для проверки запаса на
        /// ПРОТИВОПОЛОЖНОЙ). Та же along/across → x/y привязка, что и в PointOf.</summary>
        private static double[] CornerZs(SlabGeometry slab, ZoneDef z, Face face,
            double along1Mm, double along2Mm, double acrossFromMm, double acrossToMm)
        {
            double X(double along, double across) => z.Dir == Dir.X ? along : across;
            double Y(double along, double across) => z.Dir == Dir.X ? across : along;
            Func<double, double, double> f = face == Face.Top
                ? (Func<double, double, double>)slab.TopZMmAt
                : slab.BottomZMmAt;
            return new[]
            {
                f(X(along1Mm, acrossFromMm), Y(along1Mm, acrossFromMm)),
                f(X(along1Mm, acrossToMm),   Y(along1Mm, acrossToMm)),
                f(X(along2Mm, acrossFromMm), Y(along2Mm, acrossFromMm)),
                f(X(along2Mm, acrossToMm),   Y(along2Mm, acrossToMm)),
            };
        }

        /// <summary>
        /// Отметка оси ОДНОГО размещаемого элемента (одной "ступени" — band целиком или её
        /// часть, см. ComputeRungs), выбранная так, чтобы защитный слой был ≥ норматива нигде не
        /// проседал ниже — даже на наклонной плите. Плита — одна плоскость (см.
        /// SlabGeometry.TopZMmAt/BottomZMmAt), поэтому экстремум отметки грани по прямоугольной
        /// области, которую занимает элемент (along1..along2 × acrossFrom..acrossTo), всегда
        /// достигается в одном из 4 углов (CornerFaceZs): берём Min по верхней грани (Face.Top —
        /// худший, самый низкий угол верха) или Max по нижней грани (Face.Bottom — самый высокий
        /// угол низа), затем один раз применяем офсет. На плоской плите (уклон=0) все 4 угла дают
        /// одну отметку — вырожденный случай той же формулы, ничего не меняется относительно
        /// плоских плит "как было".
        /// Защитный слой в типе плиты = 0, поэтому FaceOffset (35 мм) отсчитывается до наружной
        /// грани. Доп. арматура считается лежащей рядом с фоновой (основной) сеткой — у фона тоже
        /// двухслойная сетка X+Y толщиной FirstLayerThickness (диаметр фоновой арматуры, приходит
        /// из HTML): направление FirstLayer (первый слой фона) — offset = FaceOffset +
        /// FirstLayerThickness/2 + d/2; второе направление (лежит поверх ПЕРВОГО слоя фона
        /// целиком) — offset = FaceOffset + FirstLayerThickness + d/2.
        /// </summary>
        public double BandZAt(SlabGeometry slab, ZoneDef z,
            double along1Mm, double along2Mm, double acrossFromMm, double acrossToMm)
        {
            double layer = (z.Dir == _s.FirstLayer) ? _s.FirstLayerThickness / 2.0 : _s.FirstLayerThickness;
            double d = z.Diameter;
            double off = _s.FaceOffset + layer + d / 2.0;

            var corners = CornerZs(slab, z, z.Face, along1Mm, along2Mm, acrossFromMm, acrossToMm);
            return z.Face == Face.Top ? corners.Min() - off : corners.Max() + off;
        }

        /// <summary>Одна "ступень лесенки" — подряд идущая группа копий массива на одной общей
        /// горизонтальной отметке, см. ComputeRungs.</summary>
        public class RebarRung
        {
            public double AcrossStartMm;
            public int Count;
            public double ZBar;
        }

        /// <summary>
        /// Разбивает массив копий (acrossStart..acrossStart+(count-1)*step) на "ступени лесенки":
        /// подряд идущие группы копий на одной общей горизонтальной отметке (см. BandZAt), между
        /// которыми — ступенчатый сдвиг Z вниз/вверх по уклону. Нужно потому, что у BandZAt на
        /// весь диапазон сразу есть предел: если направление раскладки массива (across) совпадает
        /// с направлением уклона, а копий много, вся раскладка садится на ОДНУ (самую
        /// консервативную) отметку — а на "высоком" конце защитный слой до СВОЕЙ (ближней) грани
        /// растёт настолько, что до ПРОТИВОПОЛОЖНОЙ грани он, наоборот, тает и в пределе стержень
        /// проваливается сквозь неё, если накопленный по уклону перепад сравним с толщиной плиты.
        /// Ступень растёт жадно, пока для всего накопленного диапазона запас до ПРОТИВОПОЛОЖНОЙ
        /// грани (тоже по худшему из её 4 углов) не станет меньше FaceOffset — это и есть момент,
        /// когда дальнейшее продолжение той же ступени начало бы срезать норматив с обратной
        /// стороны плиты. Как только запас исчерпан — фиксируем ступень (её Z — BandZAt по
        /// накопленному диапазону) и начинаем следующую заново с этой точки, тем же способом
        /// (свой худший угол для нового диапазона), а не продолжаем предыдущую отметку.
        /// На плоской плите или коротком/пологом массиве, где всего диапазона не хватает, чтобы
        /// исчерпать запас у противоположной грани, получается одна ступень на весь band —
        /// поведение идентично "плоскому" случаю (как было до лесенки).
        /// </summary>
        public List<RebarRung> ComputeRungs(SlabGeometry slab, ZoneDef z,
            double along1Mm, double along2Mm, double acrossStartMm, double stepMm, int count)
        {
            Face opposite = z.Face == Face.Top ? Face.Bottom : Face.Top;
            var rungs = new List<RebarRung>();
            int i0 = 0;
            while (i0 < count)
            {
                int i1 = i0;
                while (i1 + 1 < count)
                {
                    double acrossFrom = acrossStartMm + i0 * stepMm;
                    double acrossToCandidate = acrossStartMm + (i1 + 1) * stepMm;
                    double zBarCandidate = BandZAt(slab, z, along1Mm, along2Mm, acrossFrom, acrossToCandidate);
                    var oppCorners = CornerZs(slab, z, opposite, along1Mm, along2Mm, acrossFrom, acrossToCandidate);
                    double oppositeWorst = z.Face == Face.Top ? oppCorners.Max() : oppCorners.Min();
                    double oppositeMargin = z.Face == Face.Top ? zBarCandidate - oppositeWorst : oppositeWorst - zBarCandidate;
                    if (oppositeMargin < _s.FaceOffset) break;
                    i1++;
                }
                double from = acrossStartMm + i0 * stepMm;
                double to = acrossStartMm + i1 * stepMm;
                rungs.Add(new RebarRung
                {
                    AcrossStartMm = from,
                    Count = i1 - i0 + 1,
                    ZBar = BandZAt(slab, z, along1Mm, along2Mm, from, to)
                });
                i0 = i1 + 1;
            }
            return rungs;
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
