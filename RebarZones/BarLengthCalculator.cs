using System;
using System.Collections.Generic;
using System.Linq;

namespace LiraToRevit.Rebar
{
    /// <summary>Один готовый к постройке прямой участок стержня — координаты вдоль стержня уже
    /// после привязки к сетке осей (см. BarLengthCalculator.SnapAlongShift). Либо единственный
    /// (если зона уложилась в ряд стандартных заготовок), либо одна из двух половин при делении
    /// длинной зоны на два стыкуемых внахлёст массива.</summary>
    public class StraightBarPlan
    {
        public double P1, P2;
        public double LengthMm;
        public bool NeedsHook;
        /// <summary>Готовый текст "Зона_примечание" — только для половин деления (см.
        /// BarLengthCalculator.ComputeStraightSplit); null — обычный случай, примечание решает
        /// сам NeedsHook (см. RebarBuilder.WriteParams).</summary>
        public string Note;
    }

    /// <summary>Готовый к постройке Г/П-образный участок — координаты вдоль стержня (после
    /// привязки к сетке осей) и длина прямой части до загиба (для FixShapeParams).</summary>
    public class BentBarPlan
    {
        public double FarAlong, NearAlong;
        public double MainLenMm;
        public double TotalLenMm;
        /// <summary>Готовый префикс "Зона_примечание" — только для ближнего массива при делении
        /// длинной зоны; null — обычный (неразделённый) случай.</summary>
        public string Note;
    }

    /// <summary>Пара планов при делении длинной бендовой зоны на два стыкуемых внахлёст массива:
    /// дальний — обычный прямой стержень (целиком максимальная заготовка), ближний (у кромки) —
    /// Г/П-образный, отсчитанный от новой точки стыка.</summary>
    public class SplitBentPlan
    {
        public StraightBarPlan Far;
        public BentBarPlan Near;
    }

    /// <summary>
    /// Чистый расчёт длин/координат стержней допармирования: подбор длины из ряда стандартных
    /// заготовок, распределение добавки при округлении, деление длинной зоны (>11700мм) на два
    /// стыкуемых внахлёст (50d) массива — и для прямого, и для Г/П (бендового) случая, привязка
    /// торцов к сетке осей проекта. Не создаёт никаких Revit-элементов и не читает Document —
    /// только числа, ZoneDef/Dir и уже резолвленные координаты осей, поэтому проверяется напрямую,
    /// без модели Revit (см. RebarBuilder — он берёт готовые планы отсюда и строит по ним Rebar).
    /// </summary>
    public class BarLengthCalculator
    {
        private readonly PlacementSettings _s;
        private readonly List<double> _gridXs;   // координаты X «вертикальных» осей (тянутся вдоль Y), футы
        private readonly List<double> _gridYs;   // координаты Y «горизонтальных» осей (тянутся вдоль X), футы

        public BarLengthCalculator(PlacementSettings settings, List<double> gridXs, List<double> gridYs)
        {
            _s = settings;
            _gridXs = gridXs;
            _gridYs = gridYs;
        }

        /// <summary>
        /// Сдвигает оба конца стержня вдоль его направления на одинаковую величину так, чтобы
        /// расстояние от начала (p1) до ближайшей оси, параллельной направлению стержня, было
        /// кратно 10 мм. Длина стержня (p2−p1) уже взята из ряда стандартных длин — все они кратны
        /// 10 мм, поэтому такой сдвиг делает «чистым» одновременно и второй конец. Без осей — 0.
        /// </summary>
        public double SnapAlongShift(double p1Mm, double p2Mm, Dir dir)
        {
            var axes = dir == Dir.X ? _gridXs : _gridYs;     // ось того же типа координаты, что p1/p2
            if (axes.Count == 0) return 0;

            double mid = (p1Mm + p2Mm) / 2.0;
            double axisMm = RebarUnits.ToMm(axes.OrderBy(a => Math.Abs(RebarUnits.ToMm(a) - mid)).First());
            double dist = p1Mm - axisMm;
            double roundedDist = Math.Round(dist / 10.0) * 10.0;
            return roundedDist - dist;
        }

        /// <summary>Прямой (не бендовый) случай: один стержень, если зона укладывается в ряд
        /// заготовок, иначе — деление на два стыкуемых внахлёст массива (см. ComputeStraightSplit).</summary>
        public List<StraightBarPlan> ComputeStraight(ZoneDef z, double p1, double p2, bool clipped1, bool clipped2)
        {
            // — длина берётся из ряда заготовок, а не округлением —
            double len = p2 - p1;
            var std = _s.SnapLength(len);
            if (std == null)
                // Зона длиннее максимальной заготовки (11700мм) — делим на два стыкуемых
                // внахлёст массива вместо ошибки.
                return ComputeStraightSplit(z, p1, p2, clipped1, clipped2);

            double extra = std.Value - len;                  // добавка распределяется симметрично,
            if (!clipped1 && !clipped2) { p1 -= extra / 2; p2 += extra / 2; }
            else if (!clipped2) p2 += extra;                 // а с подрезанной кромки — в свободную сторону
            else if (!clipped1) p1 -= extra;
            else throw new InvalidOperationException(
                $"Зона {z.Id}: стержень зажат кромками с обеих сторон, длина из ряда не подбирается");

            // — сдвигаем весь стержень (длину не меняя) так, чтобы оба конца были кратны 10 мм от оси —
            double alongShift = SnapAlongShift(p1, p2, z.Dir);
            p1 += alongShift; p2 += alongShift;

            return new List<StraightBarPlan>
            {
                new StraightBarPlan { P1 = p1, P2 = p2, LengthMm = std.Value, NeedsHook = clipped1 || clipped2 }
            };
        }

        /// <summary>
        /// Зона длиннее максимальной заготовки (11700мм, см. PlacementSettings.StandardLengths) — один
        /// прямой стержень из ряда не подобрать. Делим на два массива, стыкуемых внахлёст: первый
        /// массив — целиком максимальная заготовка (11700мм, экономичнее всего по расходу стыков),
        /// второй — покрывает остаток зоны плюс нахлёст 50d (d — диаметр стержня, мм), округлённый
        /// вверх до ближайшей длины из ряда заготовок. Например, зона 13700мм → первый массив 11700,
        /// второй — от (11700-нахлёст) до конца зоны, т.е. номинально ~2000+нахлёст, округлённые до
        /// ближайшей стандартной длины. Добавка от этого округления уходит целиком В нахлёст
        /// (увеличивает его сверх 50d — это не вредит), а не наружу — внешние торцы (p1 у первого
        /// массива, p2 у второго) уже зафиксированы кромкой плиты/анкеровкой (ClipAlong), как и у
        /// обычного нерасщеплённого стержня, и не должны сдвигаться.
        /// </summary>
        private List<StraightBarPlan> ComputeStraightSplit(ZoneDef z, double p1, double p2, bool clipped1, bool clipped2)
        {
            double lap = 50.0 * z.Diameter;               // нахлест, мм
            double totalLen = p2 - p1;
            double maxStd = _s.StandardLengths.Last();     // максимальная заготовка (11700) — целиком в первый массив

            double aOuter = p1;                            // внешний торец 1-го массива — как у обычного стержня
            double aInner = p1 + maxStd;                    // внутренний (стыковой) торец — свободный, фикс. длина заготовки

            double rawB = totalLen - maxStd + lap;          // сырая длина второго массива: остаток зоны + нахлёст
            var stdB = _s.SnapLength(rawB);
            if (stdB == null)
                throw new InvalidOperationException(
                    $"Зона {z.Id}: длина {totalLen:0} мм слишком велика даже для деления на два стыкуемых " +
                    $"массива (второму нужно {rawB:0} мм, максимум {_s.StandardLengths.Last():0} мм) — разделите зону вручную");

            double bOuter = p2;                                          // внешний торец 2-го массива — как у обычного стержня
            double bInner = bOuter - stdB.Value;                         // внутренний (стыковой) торец — свободный

            // — сдвигаем каждый массив (длину не меняя) так, чтобы оба конца были кратны 10 мм от оси —
            double shiftA = SnapAlongShift(aOuter, aInner, z.Dir);
            aOuter += shiftA; aInner += shiftA;
            double shiftB = SnapAlongShift(bInner, bOuter, z.Dir);
            bInner += shiftB; bOuter += shiftB;

            string noteA = $"стык внахлёст 50d — массив 1/2 ({maxStd:0}мм)" + (clipped1 ? "; анкеровка подрезана — требуется загиб" : "");
            string noteB = "стык внахлёст 50d — массив 2/2" + (clipped2 ? "; анкеровка подрезана — требуется загиб" : "");

            return new List<StraightBarPlan>
            {
                new StraightBarPlan { P1 = aOuter, P2 = aInner, LengthMm = maxStd, NeedsHook = clipped1, Note = noteA },
                new StraightBarPlan { P1 = bInner, P2 = bOuter, LengthMm = stdB.Value, NeedsHook = clipped2, Note = noteB }
            };
        }

        /// <summary>Нужно ли делить бендовую (Г/П, у края плиты) зону на два стыкуемых массива —
        /// зона длиннее максимальной заготовки. Если true — вызывающий код должен вызвать
        /// ComputeBentSplit вместо ComputeBent.</summary>
        public bool NeedsBentSplit(double origFarAlong, double edgeNearAlong)
            => _s.SnapLength(Math.Abs(edgeNearAlong - origFarAlong)) == null;

        /// <summary>Единственный бендовый участок — зона целиком укладывается в ряд заготовок.
        /// noseTotalMm уже решён снаружи (выбор Г vs П — по пилонам/колоннам у кромки плиты, это
        /// Revit-геометрия, см. SupportDetector.HasParallelSupport — данный класс её не касается).</summary>
        public BentBarPlan ComputeBent(ZoneDef z, double origFarAlong, double edgeNearAlong, bool bendAtP2, double noseTotalMm)
        {
            // Общая длина стержня (прямой участок + загиб) берётся из ТОГО ЖЕ ряда заготовок
            // 11700, что и обычный прямой стержень — загиб не добавляет металл поверх, а
            // "вырезается" из уже посчитанной длины: если прямой стержень с анкеровкой был бы
            // 1920мм, а торец гнётся, то прямой участок сокращается на длину загиба, чтобы в
            // сумме (прямой + загиб) снова получилось 1920 (округлённые до ряда).
            double totalBudgetMm = Math.Abs(edgeNearAlong - origFarAlong);
            var snappedTotal = _s.SnapLength(totalBudgetMm);
            if (snappedTotal == null)
                throw new InvalidOperationException(
                    $"Зона {z.Id}: требуемая длина {totalBudgetMm:0} мм превышает максимальную " +
                    $"{_s.StandardLengths.Last():0} мм — вызывающий код должен был проверить NeedsBentSplit");

            return BuildBentPlan(z, edgeNearAlong, bendAtP2, snappedTotal.Value, noseTotalMm, null);
        }

        /// <summary>
        /// Деление длинной бендовой зоны на два стыкуемых внахлёст массива (тот же принцип, что и
        /// ComputeStraightSplit): дальний (анкеруемый) массив — обычный прямой стержень, целиком
        /// максимальная заготовка 11700мм; ближний (у кромки) — Г/П-образный, отсчитанный не от
        /// исходной анкеровки, а от НОВОЙ точки стыка с дальним массивом (сдвинутой на нахлёст 50d).
        /// noseTotalMm — то же самое решение по форме (Г/П), что было бы для НЕразделённой зоны
        /// (зависит только от edgeNearAlong — места загиба у кромки, — который при делении не
        /// меняется, см. ComputeBent).
        /// </summary>
        public SplitBentPlan ComputeBentSplit(ZoneDef z, double origFarAlong, double edgeNearAlong, bool bendAtP2, double noseTotalMm)
        {
            double lap = 50.0 * z.Diameter;
            double totalBudgetMm = Math.Abs(edgeNearAlong - origFarAlong);
            double maxStd = _s.StandardLengths.Last();
            double sign = bendAtP2 ? 1.0 : -1.0;              // направление от дальнего (анкер) торца к кромке

            double aOuter = origFarAlong;                     // внешний (анкеруемый) торец 1-го массива — фиксирован
            double aInner = origFarAlong + sign * maxStd;      // внутренний (стыковой) торец — свободный

            double jointAlong = aInner - sign * lap;           // точка стыка со 2-м массивом, сдвинутая на нахлёст
            double budget2 = Math.Abs(edgeNearAlong - jointAlong);
            var snapped2 = _s.SnapLength(budget2);
            if (snapped2 == null)
                throw new InvalidOperationException(
                    $"Зона {z.Id}: длина {totalBudgetMm:0} мм слишком велика даже для деления на два стыкуемых " +
                    $"массива (второму, с загибом, нужно {budget2:0} мм, максимум {_s.StandardLengths.Last():0} мм) — разделите зону вручную");

            // — сдвигаем 1-й (прямой) массив так, чтобы стыковой торец был кратен 10 мм от оси —
            double shiftA = SnapAlongShift(aOuter, aInner, z.Dir);
            aOuter += shiftA; aInner += shiftA;

            var far = new StraightBarPlan
            {
                P1 = aOuter,
                P2 = aInner,
                LengthMm = maxStd,
                NeedsHook = false,
                Note = $"стык внахлёст 50d — массив 1/2 ({maxStd:0}мм)"
            };
            var near = BuildBentPlan(z, edgeNearAlong, bendAtP2, snapped2.Value, noseTotalMm, "стык внахлёст 50d — массив 2/2");

            return new SplitBentPlan { Far = far, Near = near };
        }

        private BentBarPlan BuildBentPlan(ZoneDef z, double edgeNearAlong, bool bendAtP2, double snappedTotalMm, double noseTotalMm, string note)
        {
            double mainLenMm = snappedTotalMm - noseTotalMm;
            if (mainLenMm <= 0)
                throw new InvalidOperationException(
                    $"Зона {z.Id}: длина загиба ({noseTotalMm:0} мм) больше подобранной длины из ряда ({snappedTotalMm:0} мм)");

            // Место загиба ФИКСИРУЕМ ровно там, где обрезался бы прямой стержень (edgeNearAlong,
            // т.е. с отступом EndOffset от истинного края плиты) — так же, как выглядел бы
            // обычный прямой стержень. Раньше загиб откладывался от анкеровки на mainLenMm и
            // при округлении общей длины до бо́льшего ряда (скачок, напр. с 2921 сразу на 3900)
            // выталкивал загиб ЗА пределы плиты. Растягивается вместо этого дальний торец —
            // как и у прямого стержня при подгонке под ряд заготовок.
            double sign = bendAtP2 ? 1.0 : -1.0;
            double nearAlong = edgeNearAlong;
            double farAlong = nearAlong - sign * mainLenMm;

            // — сдвигаем весь стержень (длину не меняя) так, чтобы дальний торец был кратен 10 мм от оси —
            double alongShift = SnapAlongShift(farAlong, nearAlong, z.Dir);
            farAlong += alongShift; nearAlong += alongShift;

            return new BentBarPlan { FarAlong = farAlong, NearAlong = nearAlong, MainLenMm = mainLenMm, TotalLenMm = snappedTotalMm, Note = note };
        }
    }
}
