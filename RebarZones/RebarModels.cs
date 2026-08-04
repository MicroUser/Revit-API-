using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace LiraToRevit.Rebar
{
    // ────────────────────────────────────────────────────────────
    //  Модель данных: то, что приходит с экрана подтверждения
    // ────────────────────────────────────────────────────────────

    public enum Face { Top, Bottom }
    public enum Dir { X, Y }

    /// <summary>Подтверждённая зона допармирования (одна на один слой).</summary>
    public class ZoneDef
    {
        public string Id;                 // "d12-M17"
        public Face Face;                 // верх / низ — из легенды DXF
        public Dir Dir;                   // вдоль X / вдоль Y — из легенды DXF
        public int Diameter;              // 10,12,14,16,18,20
        public int Step;                  // 100 или 200, мм
        public List<XYZ> Polygon;         // ортогональный контур в координатах модели, мм (Z игнорируется)
        public bool ByMean;               // решение принято по среднему
        public double AsCalc;             // расчётное As (пик), см²/м
        public string Source;             // "расчёт" / "пересечение" / "вложение" / "вручную"
        public bool Accepted;             // размещаем только подтверждённые
        // Зона проиграла проверку на пересечение по длине с другой зоной того же направления
        // и грани (см. ZoneConflictResolver.ResolveLengthOverlaps) — созданный по ней Rebar физически
        // сдвигается на 20мм поперёк направления стержней ПОСЛЕ создания (см. RebarPlacer.Place): любая
        // попытка сдвинуть исходные координаты ДО создания перебивается привязкой к сетке
        // изолиний (NearestIsoline) и другими пересчётами внутри BarLengthCalculator.
        public bool NeedsAcrossShift;
    }

    public class PlacementSettings
    {
        /// <summary>От грани плиты до наружной грани стержня, мм. Защитный слой в типе плиты = 0.</summary>
        public double FaceOffset = 35.0;

        /// <summary>Отступ от торца плиты / края отверстия до центра (оси) конца стержня, мм
        /// (вдоль стержня). Кривая стержня в Rebar API — осевая линия, это расстояние до центра.</summary>
        public double EndOffset = 20.0;

        /// <summary>Длина анкеровки за границы зоны: 55d, мм.</summary>
        public Dictionary<int, double> Anchorage = new Dictionary<int, double>
        {
            {10, 550}, {12, 660}, {14, 770}, {16, 880}, {18, 990}, {20, 1100},
            {22, 1210}, {25, 1380}, {28, 1540}
        };

        /// <summary>Ряд длин прямых стержней (заготовки из 11700), мм.</summary>
        public double[] StandardLengths =
        {
            1670, 1950, 2340, 2920, 3900, 4880, 5850, 6850, 7800, 8770, 9750, 11700
        };

        /// <summary>Ближайшая бо́льшая длина из ряда; null — длиннее максимальной.</summary>
        public double? SnapLength(double needMm)
        {
            foreach (var L in StandardLengths) if (L >= needMm - 1e-6) return L;
            return null;
        }

        /// <summary>Какое направление лежит ближе к грани плиты (первый слой).</summary>
        public Dir FirstLayer = Dir.X;

        /// <summary>Диаметр фоновой (основной) арматуры, мм — задаёт отступ доп. арматуры от
        /// защитного слоя (см. RebarPlacer.BarElevation). Приходит из HTML (выбор "Фоновая арматура" в
        /// настройках редактора зон), 10 мм — запасное значение по умолчанию.</summary>
        public double FirstLayerThickness = 10.0;


        /// <summary>Шаблон имени типа: {F}=В/Н, {D}=x/y, {d}=диаметр.</summary>
        public string TypeNameTemplate = "плита_доп_{F}{D}_d={d}_А500";

        /// <summary>Рабочий набор для создаваемых стержней; null/"" — не менять (оставить текущий).
        /// Молча игнорируется, если модель несовместная или набора с таким именем нет.</summary>
        public string WorksetName = ".#06_Арм_Плиты";

        /// <summary>Фундаменты: стержни всегда прямые, независимо от грани/обрезки краем —
        /// Г/П-образный загиб (см. ниже) не применяется вообще.</summary>
        public bool AlwaysStraight = false;

        // ── Г/П-образные стержни у края плиты (только верхняя допка, Face.Top) ──────────
        // Стержень, обрезаемый краем плиты (ClipAlong → clipped1/clipped2), вместо укорачивания
        // с пометкой "требуется загиб" получает реальный загиб: по умолчанию Г-образный, а если
        // у этого края обнаружен пилон/колонна, параллельные грани плиты — П-образный.

        /// <summary>Имя формы для Г-образного стержня (носик BI_B).</summary>
        public string LShapeName = "(форма)11";
        /// <summary>Длина носика Г-образного стержня, мм (BI_B).</summary>
        public double LShapeNoseMm = 120.0;

        /// <summary>Имя формы для П-образного стержня (BI_B — уход вглубь плиты, BI_C — короткая нога).</summary>
        public string UShapeName = "(форма)21";
        /// <summary>Длина части, уходящей вглубь плиты, мм (BI_B).</summary>
        public double UShapeDepthMm = 140.0;
        /// <summary>Длина короткой ноги на конце, мм (BI_C).</summary>
        public double UShapeFootMm = 500.0;

        /// <summary>Параметр сборки со списком категорий её состава (автозаполняемый Revit'ом
        /// при именовании сборки) — ловит любую сборку из стен/несущих колонн.</summary>
        public string NamingCategoryParam = "Категория именования";
        /// <summary>Подстрока значения NamingCategoryParam, определяющая стены.</summary>
        public string NamingCategoryWalls = "Стены";
        /// <summary>Подстрока значения NamingCategoryParam, определяющая несущие колонны.</summary>
        public string NamingCategoryColumns = "Несущие колонны";

        /// <summary>Допуск вдоль края плиты при поиске пилона/колонны у места загиба, мм.</summary>
        public double SupportSearchToleranceMm = 50.0;
    }

    // ────────────────────────────────────────────────────────────
    //  Вспомогательные типы
    // ────────────────────────────────────────────────────────────

    public class Band
    {
        public double Along1, Along2;    // вдоль стержней (внутренние единицы)
        public double Across1, Across2;  // поперёк стержней
    }

    public class PlacedBars
    {
        public string ZoneId, TypeName;
        public int Count;
        public double LengthMm;
        public bool NeedsHook;
        public ElementId RebarId;   // созданный Rebar — см. RebarPlacer.Place (сдвиг при конфликте по длине)
    }

    public class PlacementResult
    {
        public List<PlacedBars> Bars = new List<PlacedBars>();
        public List<string> Errors = new List<string>();

        /// <summary>Унификация: пары длин одного диаметра и грани, различающиеся менее чем на порог.</summary>
        public List<string> LengthWarnings(double thresholdMm = 100)
        {
            var res = new List<string>();
            foreach (var g in Bars.GroupBy(b => b.TypeName))
            {
                var lens = g.Select(b => b.LengthMm).Distinct().OrderBy(v => v).ToList();
                for (int i = 0; i + 1 < lens.Count; i++)
                    if (lens[i + 1] - lens[i] < thresholdMm)
                        res.Add($"{g.Key}: длины {lens[i]} и {lens[i + 1]} различаются менее чем на {thresholdMm} мм — унифицировать до {lens[i + 1]}");
            }
            return res;
        }
    }
}
