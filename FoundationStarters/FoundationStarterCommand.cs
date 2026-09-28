using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

namespace DAN_Plugin
{
    // ======================================================================
    //  Скелет команды: выпуски арматуры в плиты от вертикальной арматуры стены
    //  Revit 2023 (.NET Framework 4.8)
    //
    //  Логика:
    //   1. Пользователь выбирает сборку стены (AssemblyInstance).
    //   2. Из параметра "Комментарии" сборки читаем марку (СБм-1 / СБм-5 ...).
    //   3. Пользователь выбирает плиту фундамента (StructuralFoundation) под стеной.
    //   4. Собираем Rebar, у которых BI_марка_конструкции == марка.
    //   5. Делим наборы на грани (внутренняя/наружная) и по высоте (низкий/высокий).
    //   6. Для каждого набора строим набор Г-образных выпусков по существующей форме.
    //
    //  Помечено // CALIBRATE — то, что нужно доуточнить, когда придут форма,
    //  имена её параметров и таблица вылетов.
    // ======================================================================

    [Transaction(TransactionMode.Manual)]
    public class FoundationStarterCommand : IExternalCommand
    {
        // ---- Константы спеки -------------------------------------------------
        internal const string MarkParamName = "BI_марка_конструкции"; // параметр марки на Rebar

        // В реальных стенах, помимо основной вертикальной арматуры, под той же маркой могут
        // попадаться и другие элементы (свои хомуты, доп. арматура) — отдельно фильтруем по
        // BI_фильтр_арматуры, чтобы для расчёта выпусков брались ТОЛЬКО стержни вертикального
        // армирования, а не всё подряд с совпадающей маркой.
        private const string RebarFilterParamName = "BI_фильтр_арматуры";
        private const string RebarFilterVerticalValue = "Вертикальное армирование";
        private const string StarterMarkValue = "Выпуски"; // значение марки для самих выпусков
        private const string ShapeName     = "(форма)11";             // CALIBRATE: имя RebarShape
        // Типоразмер для самих выпусков — имя зависит от диаметра стыкуемой арматуры стены
        // (напр. "(арматура)выпуски_d=16_А500" для d=16), не тип стены.
        private const string StarterBarTypeNameFormat = "(арматура)выпуски_d={0}_А500";
        internal static string StarterBarTypeName(int diaMm) => string.Format(StarterBarTypeNameFormat, diaMm);
        private const string PolkaParam    = "BI_B";                     // CALIBRATE: имя размера полки в форме
        private const string VertParam     = "BI_A";                     // CALIBRATE: имя размера вертикали в форме

        // Длина полки выпуска, мм: зависит от диаметра стержня. Тот же порог DiaThresholdMm
        // разделяет способ стыковки с основной арматурой: < порога — внахлёст (нахлёст из
        // таблицы), >= порога — сваркой (см. WeldGapMm, крупный диаметр внахлёст не стыкуют).
        internal const int DiaThresholdMm      = 22;   // граница диаметра
        private const double PolkaLenSmallMm  = 200.0; // диаметр < DiaThresholdMm
        private const double PolkaLenLargeMm  = 400.0; // диаметр >= DiaThresholdMm

        // Зазор под сварной шов для стыковки крупного диаметра (>= DiaThresholdMm): выпуск не
        // доходит до низа основной арматуры стены на эту величину.
        internal const double WeldGapMm = 10.0;

        // "Накладки" сварного стыка — у крупного диаметра (> SpliceCoverPlateMinDiaMm, мм) стык
        // арматуры сваркой сопровождается короткими накладками ТОГО ЖЕ типоразмера/диаметра, что
        // и основной стержень стены (тот же BI_фильтр_арматуры), поэтому они попадают в
        // CollectWallRebar как обычный "набор" — но выпуски им не нужны. Отличаем по фактической
        // длине стержня (короткая, 200–250мм, в отличие от полноразмерного вертикального
        // стержня) — см. IsSpliceCoverPlate.
        internal const int SpliceCoverPlateMinDiaMm = 20;
        internal const double SpliceCoverPlateMinLenMm = 200.0;
        internal const double SpliceCoverPlateMaxLenMm = 250.0;

        // Отступ от низа фундамента до ЦЕНТРА (оси) низа выпуска (точка загиба полки) — значение
        // по умолчанию, пользователь может поменять его при вызове команды (см. GroupNumberPrompt,
        // поле "Расстояние от низа фундамента до центра выпуска"). Эта же отметка (foundBotZ +
        // выбранный отступ) служит нижней границей и для хомутов/горизонтальной арматуры/шпилек —
        // ниже низа выпуска обхватывать нечего.
        internal const double DowelBottomOffsetDefaultMm = 75.0;

        // Высота проверяемого отрезка от верха фундамента вверх при проверке "есть ли материал
        // стены у низа" (см. HasSolidAt), мм.
        internal const double OpeningCheckHeightMm = 300.0;

        // Шаг сканирования вдоль стены при поиске проёмов для горизонтальной арматуры/шпилек
        // (см. GetSolidWallSegments) — проём УЖЕ этого шага может быть не замечен, поэтому он
        // должен быть заметно меньше минимальной реальной ширины проёма в проекте.
        internal const double OpeningScanStepMm = 100.0;

        // "Заполняющие" выпуски под окном — проёмом в теле стены, НЕ доходящим до низа
        // фундамента (материал стены есть у самого низа, проём выше — см.
        // BuildWindowFillerStarters/GetWindowOpenings). Не связаны с реальной арматурой стены:
        // фиксированный диаметр, фиксированный шаг, не доходят до низа окна на
        // WindowFillerGapMm.
        internal const int WindowFillerDiaMm = 12;
        internal const double WindowFillerStepMm = 200.0;
        internal const double WindowFillerGapMm = 20.0;

        // Отступ от каждого края проёма зависит от того, делится ли ширина проёма нацело на
        // WindowFillerStepMm (200мм): если делится — отступ WindowFillerEdgeMarginEvenMm (100мм,
        // тогда шаг между стержнями получается ровно 200мм); если нет — отступ
        // WindowFillerEdgeMarginOtherMm (50мм). См. BuildWindowFillerStarters.
        internal const double WindowFillerEdgeMarginEvenMm = 100.0;
        internal const double WindowFillerEdgeMarginOtherMm = 50.0;

        // Класс бетона стены — берём из имени материала параметра "Материал несущих
        // конструкций" (BuiltInParameter.STRUCTURAL_MATERIAL_PARAM), напр. имя материала
        // "BI_бетон_железобетон_C20/25(главный), W8, F150" → класс "C20/25". Латиница/кириллица
        // "C"/"С" не различаются — ключи таблицы ниже всегда с латинской "C".
        private static readonly Regex ConcreteClassPattern =
            new Regex(@"[CС]\s*(\d+)\s*/\s*(\d+)", RegexOptions.IgnoreCase);

        // ЧИСТАЯ длина нахлёста (без отступа основной арматуры от фундамента — тот берётся по
        // факту из модели, см. BuildStarterSet/info.BottomZ), мм: класс бетона стены ->
        // диаметр(мм) -> нахлёст. Один и тот же нахлёст для любого стержня данного диаметра —
        // разная итоговая длина выпуска у разных стержней получается сама собой из разной
        // фактической высоты их низа над фундаментом (info.BottomZ), отдельного деления на
        // "низкий"/"высокий" набор для выбора нахлёста больше не требуется. Только для диаметров
        // < DiaThresholdMm — крупный диаметр стыкуется сваркой, нахлёст ему не нужен (см.
        // WeldGapMm). CALIBRATE: у C30/37 пока значения C20/25 (заглушка) — заполнить реальными.
        private static readonly Dictionary<string, Dictionary<int, double>> NahlestByClassAndDiaMm =
            new Dictionary<string, Dictionary<int, double>>
            {
                ["C20/25"] = new Dictionary<int, double>
                {
                    { 10, 650 },
                    { 12, 780 },
                    { 14, 910 },
                    { 16, 1040 },
                    { 18, 1170 },
                    { 20, 1300 }
                },
                ["C25/30"] = new Dictionary<int, double>
                {
                    { 10, 550 },
                    { 12, 660 },
                    { 14, 770 },
                    { 16, 880 },
                    { 18, 990 },
                    { 20, 1100 }
                },
                ["C30/37"] = new Dictionary<int, double>
                {
                    { 10, 500 },
                    { 12, 600 },
                    { 14, 700 },
                    { 16, 800 },
                    { 18, 900 },
                    { 20, 1000 }
                }
            };

        // ---- Определение основной (в отличие от дополнительной) арматуры стены ----------
        // Основная арматура стены стоит в шахматном порядке из двух ярусов стыков: "нижний" —
        // в пределах LowerTierSearchBandMm от верха фундамента, "верхний" — отстоит от нижнего
        // на величину из таблицы ниже (класс бетона -> диаметр -> сдвиг). Это НЕ то же самое,
        // что NahlestByClassAndDiaMm выше — та таблица про чистую длину нахлёста стыковки
        // ВЫПУСКА с арматурой стены, это другая физическая величина. Диаметр > 20мм — сдвиг
        // фиксированный (StaggerOffsetLargeDiaMm), не зависит от класса бетона (см.
        // ClassifyMainTierZs/SelectStaggerOffset).
        private static readonly Dictionary<string, Dictionary<int, double>> StaggerOffsetByClassAndDiaMm =
            new Dictionary<string, Dictionary<int, double>>
            {
                ["C20/25"] = new Dictionary<int, double>
                {
                    { 10, 850 },
                    { 12, 1020 },
                    { 14, 1190 },
                    { 16, 1360 },
                    { 18, 1530 },
                    { 20, 1700 }
                },
                ["C25/30"] = new Dictionary<int, double>
                {
                    { 10, 720 },
                    { 12, 860 },
                    { 14, 1010 },
                    { 16, 1150 },
                    { 18, 1290 },
                    { 20, 1430 }
                },
                ["C30/37"] = new Dictionary<int, double>
                {
                    { 10, 650 },
                    { 12, 780 },
                    { 14, 910 },
                    { 16, 1040 },
                    { 18, 1170 },
                    { 20, 1300 }
                }
            };
        internal const int StaggerOffsetLargeDiaThresholdMm = 20;   // диаметр > этого значения — фиксированный сдвиг
        internal const double StaggerOffsetLargeDiaMm = 500.0;      // сдвиг яруса для диаметра > StaggerOffsetLargeDiaThresholdMm, не зависит от класса бетона

        // Диапазон отступа "нижнего" яруса основной арматуры от верха фундамента, мм — см.
        // ClassifyMainTierZs.
        internal const double LowerTierSearchBandMm = 700.0;

        // Допуск сравнения фактической отметки "верхнего" яруса с ожидаемой (нижний + сдвиг по
        // таблице выше), мм — см. ClassifyMainTierZs.
        internal const double TierMatchToleranceMm = 100.0;

        // ---- Хомуты вокруг выпусков в теле фундамента -------------------------
        // Один хомут — замкнутая рамка, охватывающая ОБА выпуска на одном уровне (наружный и
        // внутренний), как хомут колонны/пилона. Условный_A — вдоль стены (шаг стержней
        // выпусков + запас на загиб), Условный_B — поперёк стены (толщина стены минус защитный
        // слой хомута с двух сторон).
        internal const string TieShapeName = "(форма)хомут";
        // Диаметр хомута выбирает пользователь (см. ReinforcementModePrompt) — имя типоразмера
        // зависит от диаметра, класс всегда А240 (гладкая арматура).
        private const string TieBarTypeNameFormat = "(арматура)детали_d={0}_А240";
        internal static string TieBarTypeName(int diaMm) => string.Format(TieBarTypeNameFormat, diaMm);
        private const string TieParamA = "Условный_A";
        private const string TieParamB = "Условный_B";

        // Рабочий набор для всей созданной арматуры (выпуски + хомуты)
        private const string WorksetName = ".#01_Арм_Фундаменты";

        internal const double TieCoverMm = 35.0;        // защитный слой хомута (для Условный_B)
        internal const double TieAExtraMm = 25.0;        // запас в Условный_A сверх шага стержней
        internal const double TieTopOffsetMm = 50.0;     // первый хомут — на столько ниже верха фундамента
        internal const double TieVerticalStepMm = 200.0; // шаг хомутов по высоте, вниз от первого

        // Шаг хомутов вдоль стены больше не фиксированная константа — см. BuildTiesForWall
        // (stepHFt = 2×effectiveSpacingFt).

        // Умолчания формы "(форма)хомут" для Условный_A/Условный_B — подтверждены диагностикой
        // (дважды, стабильно вышло 400×300мм). Нужны для компенсации "усыхания к дальнему углу"
        // при CreateFromRebarShape (см. комментарий в BuildTiesForWall). CALIBRATE: если кто-то
        // поменяет умолчания в самой форме, эти константы придётся обновить вручную.
        internal const double TieDefaultAMm = 400.0;
        internal const double TieDefaultBMm = 300.0;

        // Хомуты никогда не "сгущаются" плотнее этого условного общего (шахматного) шага: если
        // в стене реальная арматура гуще (напр. 2 массива шагом 200мм => общий шаг 100мм вместо
        // обычных 400мм => 200мм), хомуты всё равно считаются и ставятся так, будто общий шаг —
        // TieMinEffectiveSpacingMm (см. BuildTiesForWall).
        internal const double TieMinEffectiveSpacingMm = 200.0;

        // ---- Шпильки (режим "Горизонтальная арматура" вместо хомутов) -------------------
        // Простой прямой стержень поперёк стены, стягивающий наружный и внутренний слои
        // горизонтальной арматуры — аналог хомута для этого режима, но без формы-рамки:
        // форма/типоразмер фиксированные (диаметр не выбирает пользователь), один параметр.
        internal const string ShpilkaShapeName = "(форма)шпилька";
        internal const string ShpilkaBarTypeName = "(арматура)выпуски_d=6_А240";
        private const string ShpilkaParamA = "BI_A";

        internal const double ShpilkaOffsetMm = 45.0;       // BI_A = толщина стены - это значение
        internal const double ShpilkaVerticalStepMm = 400.0; // шаг массива шпилек по высоте

        // Умолчание BI_A формы "(форма)шпилька" — подтверждено диагностикой (1020мм). Нужно
        // для компенсации "усыхания к дальнему углу" при CreateFromRebarShape — та же причина,
        // что и у хомута (см. TieDefaultAMm/TieDefaultBMm, комментарий в BuildTiesForWall):
        // форма растёт от origin к умолчанию, а не к целевому BI_A, и потом "усыхает" обратно
        // к дальнему углу (origin + xVec*ShpilkaDefaultAMm), сдвигая весь стержень на разницу
        // (умолчание-целевое). CALIBRATE: если поменяют умолчание в самой форме — обновить тут.
        internal const double ShpilkaDefaultAMm = 1020.0;

        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            UIDocument uidoc = data.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            try
            {
                // 1. Выбор сборки стены
                AssemblyInstance assembly = PickAssembly(uidoc);
                if (assembly == null) return Result.Cancelled;

                // 2. Марка из "Комментариев"
                string mark = assembly.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString();
                if (string.IsNullOrWhiteSpace(mark))
                    return Fail(ref message, "У сборки не заполнен параметр \"Комментарии\" (марка).");

                // 3. Выбор плиты фундамента
                Element foundation = PickFoundation(uidoc);
                if (foundation == null) return Result.Cancelled;

                // Марка фундамента — для имени группы созданных выпусков
                string foundationMark = foundation.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString();
                if (string.IsNullOrWhiteSpace(foundationMark))
                    return Fail(ref message, "У выбранного фундамента не заполнен параметр \"Марка\".");

                // Способ армирования тела фундамента вокруг выпусков — хомуты (с диаметром от
                // пользователя) или горизонтальные стержни (с диаметром/шагом от пользователя),
                // а также отступ от низа фундамента до ЦЕНТРА низа выпуска.
                ReinforcementChoice reinforcement = ReinforcementModePrompt.Ask(doc, DowelBottomOffsetDefaultMm);
                if (reinforcement == null) return Result.Cancelled;

                // Номер группы — вводит пользователь (имя группы "{Марка}_Выпуски_{номер}")
                int? groupNumber = GroupNumberPrompt.Ask(doc, foundationMark);
                if (groupNumber == null) return Result.Cancelled;
                string groupName = $"{foundationMark}_Выпуски_{groupNumber}";

                // Отметки фундамента (плоская плита -> BBox достаточно)
                BoundingBoxXYZ fbb = foundation.get_BoundingBox(null);
                double foundTopZ = fbb.Max.Z;
                double foundBotZ = fbb.Min.Z;
                double coverFt = MmToFt(reinforcement.BottomOffsetMm);

                // 4. Сбор арматуры стены по марке — только среди стен, входящих в выбранную сборку
                //    (марка/Комментарии может повторяться у одинаковых сборок в других местах здания,
                //    поэтому поиск по всему документу захватывал чужую, "далёкую" арматуру).
                List<Rebar> wallRebars = CollectWallRebar(doc, assembly, mark);
                if (wallRebars.Count == 0)
                    return Fail(ref message, $"Не найдена арматура с {MarkParamName} = \"{mark}\" в выбранной сборке.");

                // Формы и типоразмеры арматуры собираем ОДИН РАЗ на весь документ (а не заново
                // FilteredElementCollector'ом на каждое обращение — раньше это делалось ЗАНОВО
                // на КАЖДЫЙ набор стержней в BuildStarterSet, что при десятках наборов заметно
                // замедляло команду) и складываем в словари по имени для быстрого поиска.
                var allRebarShapes = new FilteredElementCollector(doc)
                    .OfClass(typeof(RebarShape)).Cast<RebarShape>().ToList();
                Dictionary<string, RebarShape> rebarShapesByName = allRebarShapes
                    .GroupBy(s => s.Name).ToDictionary(g => g.Key, g => g.First());
                var allRebarBarTypes = new FilteredElementCollector(doc)
                    .OfClass(typeof(RebarBarType)).Cast<RebarBarType>().ToList();
                Dictionary<string, RebarBarType> rebarBarTypesByName = allRebarBarTypes
                    .GroupBy(bt => bt.Name).ToDictionary(g => g.Key, g => g.First());

                // Форма выпуска
                rebarShapesByName.TryGetValue(ShapeName, out RebarShape shape);
                if (shape == null)
                    return Fail(ref message, $"Не найдена форма арматуры \"{ShapeName}\".");

                // Типоразмер "заполняющих" выпусков под окнами — фиксированный диаметр
                // WindowFillerDiaMm, не зависит от диаметра основной арматуры стены (см.
                // BuildWindowFillerStarters).
                string windowFillerTypeName = StarterBarTypeName(WindowFillerDiaMm);
                rebarBarTypesByName.TryGetValue(windowFillerTypeName, out RebarBarType windowFillerBarType);
                if (windowFillerBarType == null)
                    return Fail(ref message, $"Не найден типоразмер арматуры \"{windowFillerTypeName}\" для выпусков под окнами.");

                // Форма и типоразмер хомута — нужны только в режиме "Хомуты"; форма и
                // типоразмер шпильки — только в режиме "Горизонтальная арматура" (шпильки
                // заменяют хомуты в этом режиме, стягивают наружный и внутренний слои).
                RebarShape tieShape = null;
                RebarBarType tieBarType = null;
                RebarShape shpilkaShape = null;
                RebarBarType shpilkaBarType = null;
                if (reinforcement.Mode == ReinforcementMode.Ties)
                {
                    rebarShapesByName.TryGetValue(TieShapeName, out tieShape);
                    if (tieShape == null)
                        return Fail(ref message, $"Не найдена форма хомута \"{TieShapeName}\".");

                    tieBarType = reinforcement.TieBarType;
                }
                else
                {
                    rebarShapesByName.TryGetValue(ShpilkaShapeName, out shpilkaShape);
                    if (shpilkaShape == null)
                        return Fail(ref message, $"Не найдена форма шпильки \"{ShpilkaShapeName}\".");

                    rebarBarTypesByName.TryGetValue(ShpilkaBarTypeName, out shpilkaBarType);
                    if (shpilkaBarType == null)
                        return Fail(ref message, $"Не найден типоразмер арматуры шпильки \"{ShpilkaBarTypeName}\".");
                }


                // Стена-хост и наружная нормаль — отдельно для каждого набора
                // (в сборке может быть несколько стеновых элементов).
                var sets = new List<(Rebar Rebar, Wall Wall, XYZ Exterior, SetInfo Info, int DiaMm)>();
                foreach (Rebar wallSet in wallRebars)
                {
                    Wall wall = GetHostWall(doc, wallSet);
                    if (wall == null) continue; // без стены-хоста набор пропускаем

                    XYZ exterior = wall.Orientation.Normalize(); // наружу от стены
                    SetInfo info = ClassifySet(wallSet, wall, exterior);
                    RebarBarType wallSetBarType = doc.GetElement(wallSet.GetTypeId()) as RebarBarType;
                    int diaMm = (int)Math.Round(FtToMm(wallSetBarType.BarModelDiameter));
                    sets.Add((wallSet, wall, exterior, info, diaMm));
                }

                if (sets.Count == 0)
                    return Fail(ref message, "Не удалось получить стену-хост ни для одного набора арматуры.");

                // Дополнительная арматура (сверх основной, того же диаметра) выпусков не
                // требует. Раньше отличали по "двум самым нижним уникальным отметкам BottomZ",
                // но это ошибочно засчитывало дополнительную арматуру за основную в стенах, где
                // основной арматуры этого диаметра нет вовсе (тогда "две нижние" — это и есть
                // дополнительная). Теперь ищем физически, через ClassifyMainTierZs: "нижний"
                // ярус — отметка в пределах LowerTierSearchBandMm от верха фундамента; "верхний"
                // — ближайшая к (нижний + сдвиг яруса по таблице) в пределах допуска
                // TierMatchToleranceMm. Если нижнего яруса не нашлось — у этого диаметра в этой
                // стене основной арматуры нет, весь набор дополнительный и отбрасывается.
                sets = sets
                    .GroupBy(s => (s.Wall.Id, s.DiaMm))
                    .SelectMany(g =>
                    {
                        var groupList = g.ToList();
                        Wall wall = groupList[0].Wall;
                        int diaMm = groupList[0].DiaMm;

                        double tolFt = MmToFt(5.0);
                        var uniqueZs = new List<double>();
                        foreach (double z in groupList.Select(s => s.Info.BottomZ).OrderBy(z => z))
                            if (uniqueZs.Count == 0 || z - uniqueZs[uniqueZs.Count - 1] > tolFt)
                                uniqueZs.Add(z);

                        List<double> keepZs = ClassifyMainTierZs(doc, wall, diaMm, uniqueZs, foundTopZ);
                        return groupList.Where(s => keepZs.Any(kz => Math.Abs(s.Info.BottomZ - kz) <= tolFt));
                    })
                    .ToList();

                // Набор, у которого в точке его вставки нет материала стены у самого низа
                // (проём, в т.ч. вырез правкой профиля стены — он не ловится ни FindInserts,
                // ни классом Opening, только реальной геометрией), выпуска не требует — такой
                // стержень физически не идёт до низа стены сквозь проём.
                // Кэш геометрии элементов для HasSolidAt: get_Geometry — дорогая операция, а
                // одна и та же стена/фундамент проверяются МНОГО раз подряд (по разу на каждый
                // набор стержней) — без кэша геометрия пересчитывалась бы заново на каждый вызов.
                var geomCache = new Dictionary<ElementId, List<Solid>>();

                var withoutOpenings = new List<(Rebar Rebar, Wall Wall, XYZ Exterior, SetInfo Info, int DiaMm)>();
                double checkHeightFt = MmToFt(OpeningCheckHeightMm);
                foreach (var s in sets)
                {
                    XYZ xy = GetBarBottomPoint(s.Rebar, 0);
                    if (!HasSolidAt(s.Wall, xy, foundTopZ, checkHeightFt, geomCache))
                        continue; // в этой точке у низа стены пусто — проём, выпуск не строим

                    withoutOpenings.Add(s);
                }
                sets = withoutOpenings;
                if (sets.Count == 0)
                    return Fail(ref message, "Вся найденная арматура попадает в проёмы стены.");

                // Рабочий набор для созданной арматуры (выпуски + хомуты) — если ворксеты в
                // проекте вообще включены и такой набор существует; иначе просто пропускаем.
                var wsInt = doc.IsWorkshared
                    ? new FilteredWorksetCollector(doc)
                        .OfKind(WorksetKind.UserWorkset)
                        .Cast<Workset>()
                        .FirstOrDefault(w => w.Name == WorksetName)?.Id.IntValue()
                    : null;

                var tieErrors = new List<string>();
                using (Transaction t = new Transaction(doc, "Выпуски арматуры в фундамент"))
                {
                    t.Start();

                    var createdIds = new List<ElementId>();
                    // Группируем не только по стене, но и по "семейству" арматуры — шагу
                    // массива (округлён до мм). На одной стене может быть несколько независимых
                    // семейств стержней с разным шагом (напр. обычная зона с общим шагом 200мм
                    // и локальная более частая зона с общим шагом 100мм) — решение "один набор
                    // параметров на всю стену по первому попавшемуся Rebar" в таком случае
                    // просто ИГНОРИРОВАЛО второе семейство для хомутов/шпилек (хотя выпуски для
                    // него строились верно, т.к. BuildStarterSet работает независимо для каждого
                    // Rebar). Теперь каждое семейство получает свои хомуты/шпильки в своём
                    // диапазоне вдоль стены (см. цикл по wallGroup ниже). MinProj/MaxProj внутри
                    // одного семейства объединяются (min/max), если у него оказалось несколько
                    // наборов (напр. низкий+высокий).
                    var familiesByWall = new Dictionary<ElementId, Dictionary<int, (double SpacingFt, double MinProjFt, double MaxProjFt, XYZ WallDir, int MaxDiaMm)>>();

                    // Для хомутов нужен не "средний" шаг семейства, а РЕАЛЬНОЕ расстояние до
                    // конкретной соседней пары — на стыке двух зон с разным шагом (напр. 200мм и
                    // 150мм) хомут, объединяющий последнюю пару одной зоны и первую пару другой,
                    // должен считаться по факту между ними (150мм), а не по шагу "своего"
                    // семейства. Поэтому отдельно собираем ПОЛНЫЙ список реальных позиций пар по
                    // всей стене (из всех наборов, не группируя по шагу) — см. BuildTiesForWall.
                    var wallPairProjections = new Dictionary<ElementId, List<double>>();

                    foreach (var s in sets)
                    {
                        var built = BuildStarterSet(doc, shape, rebarBarTypesByName, s.Rebar, foundation, s.Wall,
                                        s.Info, s.Exterior, foundBotZ, coverFt, geomCache);
                        createdIds.Add(built.Starter.Id);

                        if (!familiesByWall.TryGetValue(s.Wall.Id, out var wallFamilies))
                        {
                            wallFamilies = new Dictionary<int, (double, double, double, XYZ, int)>();
                            familiesByWall[s.Wall.Id] = wallFamilies;
                        }

                        int familyKey = (int)Math.Round(FtToMm(built.SpacingFt));
                        if (!wallFamilies.TryGetValue(familyKey, out var existing))
                            wallFamilies[familyKey] = (built.SpacingFt, built.MinProjFt, built.MaxProjFt, built.WallDir, built.DiaMm);
                        else
                            wallFamilies[familyKey] = (
                                existing.SpacingFt,
                                Math.Min(existing.MinProjFt, built.MinProjFt),
                                Math.Max(existing.MaxProjFt, built.MaxProjFt),
                                existing.WallDir,
                                Math.Max(existing.MaxDiaMm, built.DiaMm));

                        if (!wallPairProjections.TryGetValue(s.Wall.Id, out var projList))
                        {
                            projList = new List<double>();
                            wallPairProjections[s.Wall.Id] = projList;
                        }
                        projList.AddRange(GetAllBarProjections(s.Rebar, built.WallDir));
                    }

                    // Сортируем и убираем дубли (наружный и внутренний наборы дают ОДНИ И ТЕ ЖЕ
                    // позиции вдоль стены — иначе каждая пара считалась бы дважды).
                    double pairDedupTolFt = MmToFt(5.0);
                    foreach (ElementId wallId in wallPairProjections.Keys.ToList())
                    {
                        var sortedProj = wallPairProjections[wallId].OrderBy(p => p).ToList();
                        var dedupedProj = new List<double>();
                        foreach (double p in sortedProj)
                            if (dedupedProj.Count == 0 || p - dedupedProj[dedupedProj.Count - 1] > pairDedupTolFt)
                                dedupedProj.Add(p);
                        wallPairProjections[wallId] = dedupedProj;
                    }

                    // Армирование вокруг выпусков — по одному набору на стену (охватывает сразу
                    // и наружную, и внутреннюю грань). Это отдельная, менее обкатанная часть —
                    // оборачиваем каждую стену в свою SubTransaction, чтобы сбой на ОДНОЙ стене
                    // откатывался только сам по себе и не ронял уже построенные выпуски и
                    // армирование других стен.
                    foreach (var wallGroup in sets.GroupBy(s => s.Wall.Id))
                    {
                        Wall wall = wallGroup.First().Wall;
                        var families = familiesByWall[wall.Id].Values.ToList();

                        using (SubTransaction st = new SubTransaction(doc))
                        {
                            st.Start();
                            try
                            {
                                var builtIds = new List<ElementId>();
                                // Один проход по ВСЕЙ стене сразу — по полному списку реальных
                                // позиций пар (не по семействам): нужен и хомутам (длина каждого
                                // считается по факту расстояния до конкретной соседней пары), и
                                // шпилькам ("через одну" = каждая вторая позиция в этом списке).
                                // Переходы между зонами с разным шагом (напр. 200мм → 150мм)
                                // обрабатываются сами собой — не нужно отдельно идти по каждому
                                // "семейству".
                                List<double> pairProjections = wallPairProjections.TryGetValue(wall.Id, out var pp)
                                    ? new List<double>(pp) : new List<double>();
                                XYZ wallDirCommon = families[0].WallDir;
                                int wallMaxDiaMm = families.Max(f => f.MaxDiaMm);

                                // Выпуски под окнами (проёмами, не доходящими до низа
                                // фундамента) — независимо от режима армирования и от реальной
                                // арматуры стены (см. BuildWindowFillerStarters). Их позиции
                                // подмешиваем в pairProjections ДО построения хомутов/шпилек —
                                // иначе между ближайшими РЕАЛЬНЫМИ стержнями по разные стороны
                                // окна (которых под самим окном нет) получался бы один хомут на
                                // весь проём, вместо деления по факту вокруг новых позиций.
                                var windowFiller = BuildWindowFillerStarters(doc, shape, windowFillerBarType,
                                    foundation, wall, wallDirCommon, wall.Orientation.Normalize(),
                                    foundTopZ, foundBotZ, coverFt, geomCache);
                                builtIds.AddRange(windowFiller.Ids);
                                if (windowFiller.Projections.Count > 0)
                                {
                                    pairProjections.AddRange(windowFiller.Projections);
                                    pairProjections = pairProjections.OrderBy(p => p).ToList();
                                    double dedupTolFt = MmToFt(5.0);
                                    var dedupedProjections = new List<double>();
                                    foreach (double p in pairProjections)
                                        if (dedupedProjections.Count == 0 || p - dedupedProjections[dedupedProjections.Count - 1] > dedupTolFt)
                                            dedupedProjections.Add(p);
                                    pairProjections = dedupedProjections;
                                }

                                if (reinforcement.Mode == ReinforcementMode.Ties)
                                {
                                    builtIds.AddRange(BuildTiesForWall(doc, tieShape, tieBarType,
                                        foundation, wall, pairProjections, wallDirCommon, wallMaxDiaMm,
                                        foundTopZ, foundBotZ, coverFt));
                                }
                                else
                                {
                                    // Горизонтальная арматура — одна пара стержней на всю стену
                                    // (использует реальную геометрию оси стены, а не диапазон
                                    // семейства), но отступ от грани — по максимальному диаметру
                                    // выпуска среди ВСЕХ семейств стены.
                                    builtIds.AddRange(BuildHorizontalBarsForWall(doc, reinforcement.HorizontalBarType, reinforcement.HorizontalStepMm,
                                        foundation, wall, wallDirCommon, wallMaxDiaMm, foundTopZ, foundBotZ, coverFt, geomCache));

                                    builtIds.AddRange(BuildShpilkiForWall(doc, shpilkaShape, shpilkaBarType,
                                        foundation, wall, pairProjections, wallDirCommon,
                                        foundTopZ, foundBotZ, coverFt, geomCache));
                                }
                                st.Commit();
                                createdIds.AddRange(builtIds);
                            }
                            catch (Exception ex)
                            {
                                st.RollBack();
                                tieErrors.Add($"Стена Id={wall.Id}: {ex.Message}");
                            }
                        }
                    }

                    if (wsInt.HasValue)
                        foreach (ElementId id in createdIds)
                            doc.GetElement(id)?.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM)?.Set(wsInt.Value);

                    if (createdIds.Count > 0)
                    {
                        Autodesk.Revit.DB.Group group = doc.Create.NewGroup(createdIds);
                        try
                        {
                            group.GroupType.Name = groupName;
                        }
                        catch (Exception)
                        {
                            throw new InvalidOperationException(
                                $"Имя группы \"{groupName}\" уже занято — измените номер и повторите.");
                        }
                        if (wsInt.HasValue)
                            group.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM)?.Set(wsInt.Value);
                    }

                    t.Commit();
                }

                if (tieErrors.Count > 0)
                    TaskDialog.Show("Выпуски арматуры в фундамент",
                        "Выпуски созданы, но хомуты построились не везде:\n\n" + string.Join("\n", tieErrors));

                return Result.Succeeded;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                return Fail(ref message, ex.Message);
            }
        }

        // Message из IExternalCommand через Add-In Manager виден не всегда (в отличие от
        // обычной загрузки через .addin-манифест) — поэтому дублируем ещё и TaskDialog'ом,
        // как это уже сделано в других командах модуля (см. Debug.cs, StairLandingExitsCommand.cs).
        private static Result Fail(ref string message, string text)
        {
            message = text;
            TaskDialog.Show("Выпуски арматуры в фундамент", text);
            return Result.Failed;
        }

        // ---- Выбор элементов -------------------------------------------------

        internal AssemblyInstance PickAssembly(UIDocument uidoc)
        {
            Reference r = uidoc.Selection.PickObject(
                ObjectType.Element, new AssemblyFilter(), "Выберите сборку стены");
            return uidoc.Document.GetElement(r) as AssemblyInstance;
        }

        internal Element PickFoundation(UIDocument uidoc)
        {
            Reference r = uidoc.Selection.PickObject(
                ObjectType.Element, new CategoryFilter(BuiltInCategory.OST_StructuralFoundation),
                "Выберите плиту фундамента под стеной");
            return uidoc.Document.GetElement(r);
        }

        // ---- Сбор и классификация -------------------------------------------

        internal List<Rebar> CollectWallRebar(Document doc, AssemblyInstance assembly, string mark)
        {
            List<ElementId> allWallIds = GetMemberWallIds(doc, assembly).ToList();
            if (allWallIds.Count == 0) return new List<Rebar>();

            // Сборка может включать несколько поэтажных сегментов одной стены (многоэтажная
            // стена = несколько Wall друг над другом) — берём арматуру только у САМОГО НИЖНЕГО
            // яруса, который реально опирается на фундамент, иначе выпуски дублируются на
            // каждом этаже.
            HashSet<ElementId> wallIds = new HashSet<ElementId>(FilterLowestWalls(doc, allWallIds));
            if (wallIds.Count == 0) return new List<Rebar>();

            List<Rebar> byMark = new FilteredElementCollector(doc)
                .OfClass(typeof(Rebar))
                .Cast<Rebar>()
                .Where(rb => wallIds.Contains(rb.GetHostId()) &&
                    string.Equals(
                        rb.LookupParameter(MarkParamName)?.AsString(), mark,
                        StringComparison.OrdinalIgnoreCase))
                .ToList();

            List<Rebar> byMarkAndFilter = byMark
                .Where(rb => string.Equals(
                    GetRebarFilterValue(doc, rb), RebarFilterVerticalValue,
                    StringComparison.OrdinalIgnoreCase))
                .Where(rb => !IsSpliceCoverPlate(doc, rb))
                .ToList();

            // Марка нашлась, но ни один стержень не подошёл под BI_фильтр_арматуры — ошибка
            // "не найдена арматура с маркой" в этом случае вводила бы в заблуждение (марка-то
            // как раз найдена), поэтому показываем отдельное сообщение с РЕАЛЬНЫМИ значениями
            // BI_фильтр_арматуры у найденных по марке стержней — сразу видно, опечатка это,
            // другой регистр или параметр вовсе не заполнен, без нового круга диагностики.
            if (byMarkAndFilter.Count == 0 && byMark.Count > 0)
            {
                var foundValues = byMark
                    .Select(rb => GetRebarFilterValue(doc, rb))
                    .Select(v => string.IsNullOrEmpty(v) ? "<пусто/параметр не заполнен>" : v)
                    .Distinct()
                    .ToList();
                throw new InvalidOperationException(
                    $"У арматуры с {MarkParamName} = \"{mark}\" ({byMark.Count} шт.) не найдено значение " +
                    $"{RebarFilterParamName} = \"{RebarFilterVerticalValue}\".\n" +
                    $"Найденные значения {RebarFilterParamName}: {string.Join(", ", foundValues.Select(v => $"\"{v}\""))}");
            }

            return byMarkAndFilter;
        }

        // BI_фильтр_арматуры — параметр ТИПА арматуры (RebarBarType), а не экземпляра Rebar:
        // LookupParameter на самом Rebar его не находит, только на типе.
        private static string GetRebarFilterValue(Document doc, Rebar rebar)
        {
            RebarBarType barType = doc.GetElement(rebar.GetTypeId()) as RebarBarType;
            return barType?.LookupParameter(RebarFilterParamName)?.AsString();
        }

        // Накладка сварного стыка (см. SpliceCoverPlateMinDiaMm/MinLenMm/MaxLenMm) — тот же
        // диаметр, что и основной стержень стены, но заметно короче: отличаем по фактической
        // длине позиции 0.
        private static bool IsSpliceCoverPlate(Document doc, Rebar rebar)
        {
            RebarBarType barType = doc.GetElement(rebar.GetTypeId()) as RebarBarType;
            if (barType == null) return false;
            int diaMm = (int)Math.Round(FtToMm(barType.BarModelDiameter));
            if (diaMm <= SpliceCoverPlateMinDiaMm) return false;

            double lenMm = FtToMm(GetBarLengthFt(rebar, 0));
            return lenMm >= SpliceCoverPlateMinLenMm - 0.5 && lenMm <= SpliceCoverPlateMaxLenMm + 0.5;
        }

        // Длина стержня в заданной позиции массива (сумма длин всех кривых осевой линии) —
        // нужна только для отличения короткой накладки от полноразмерного стержня стены.
        private static double GetBarLengthFt(Rebar rebar, int barPositionIndex)
        {
            IList<Curve> curves = rebar.GetCenterlineCurves(
                false, false, false, MultiplanarOption.IncludeOnlyPlanarCurves, barPositionIndex);
            double len = 0.0;
            if (curves != null)
                foreach (Curve c in curves) len += c.Length;
            return len;
        }

        // Стены-члены сборки, включая вложенные под-сборки
        internal static IEnumerable<ElementId> GetMemberWallIds(Document doc, AssemblyInstance assembly)
        {
            foreach (ElementId id in assembly.GetMemberIds())
            {
                Element el = doc.GetElement(id);
                if (el is Wall)
                {
                    yield return id;
                }
                else if (el is AssemblyInstance nested)
                {
                    foreach (ElementId nestedId in GetMemberWallIds(doc, nested))
                        yield return nestedId;
                }
            }
        }

        // Из списка стен оставляет только те, что стоят на самом нижнем ярусе (минимальная
        // отметка низа BoundingBox), с допуском — чтобы не потерять соседние сегменты ТОГО ЖЕ
        // этажа (напр. угловые куски одной сборки), но не задеть следующий этаж выше (отметки
        // низа которого отличаются на высоту этажа, на порядок больше допуска).
        internal static List<ElementId> FilterLowestWalls(Document doc, List<ElementId> wallIds)
        {
            var withZ = wallIds
                .Select(id => (Id: id, Wall: doc.GetElement(id) as Wall))
                .Where(w => w.Wall != null)
                .Select(w => (w.Id, BotZ: w.Wall.get_BoundingBox(null)?.Min.Z ?? double.MaxValue))
                .ToList();
            if (withZ.Count == 0) return new List<ElementId>();

            double minZ = withZ.Min(w => w.BotZ);
            double tolFt = MmToFt(500.0);
            return withZ.Where(w => w.BotZ <= minZ + tolFt).Select(w => w.Id).ToList();
        }

        internal Wall GetHostWall(Document doc, Rebar rebar)
        {
            return doc.GetElement(rebar.GetHostId()) as Wall;
        }

        // Есть ли материал (solid) элемента (стены, фундамента) в плане-точке xy на отрезке
        // [baseZ; baseZ+checkHeightFt] по высоте. Для стены надёжнее перебора вставок/класса
        // Opening — так ловится и вырез, сделанный правкой профиля самой стены (не вставка и
        // не отдельный элемент Opening, просто дырка в форме стены). Для фундамента — проверка,
        // что точка вообще попадает в его тело в плане (см. HasSolidAt для полки выпуска).
        //
        // geomCache — необязательный кэш "элемент → его solid'ы": get_Geometry — дорогая
        // операция (пересчёт/тесселляция), а этот метод вызывается МНОГО раз ПОДРЯД для ОДНОГО
        // И ТОГО ЖЕ элемента (напр. фундамент — по разу на каждый выпуск стены). Без кэша
        // геометрия фундамента пересчитывалась бы заново на каждый вызов — с ним считается один
        // раз и переиспользуется (сама проверка пересечения с отрезком по-прежнему считается
        // каждый раз — она дешёвая и зависит от конкретных xy/baseZ).
        internal static bool HasSolidAt(Element elem, XYZ xy, double baseZ, double checkHeightFt,
            Dictionary<ElementId, List<Solid>> geomCache = null)
        {
            List<Solid> solids;
            if (geomCache == null || !geomCache.TryGetValue(elem.Id, out solids))
            {
                GeometryElement geom = elem.get_Geometry(new Options
                {
                    ComputeReferences = false,
                    DetailLevel = ViewDetailLevel.Fine
                });
                solids = geom?.OfType<Solid>().Where(s => !s.Faces.IsEmpty).ToList();
                if (geomCache != null) geomCache[elem.Id] = solids;
            }
            if (solids == null) return true; // геометрию не получили — не блокируем создание

            Line testLine = Line.CreateBound(
                new XYZ(xy.X, xy.Y, baseZ),
                new XYZ(xy.X, xy.Y, baseZ + checkHeightFt));

            foreach (Solid solid in solids)
            {
                SolidCurveIntersection sci = solid.IntersectWithCurve(testLine, new SolidCurveIntersectionOptions());
                if (sci != null && sci.SegmentCount > 0) return true;
            }
            return false;
        }

        // Участки ВДОЛЬ СТЕНЫ (в проекциях на wallDir, от fromProj до toProj), где у стены
        // ЕСТЬ материал на отметке foundTopZ (т.е. НЕ под проёмом) — нужно, чтобы горизонтальная
        // арматура/шпильки в теле фундамента не проходили под проёмом стены (проём — в т.ч.
        // вырез правкой профиля самой стены, тот же приём HasSolidAt, что и для выпусков).
        // Сканируем с шагом sampleStepFt и склеиваем соседние точки "есть материал" в
        // непрерывные участки; проём МЕНЬШЕ шага сканирования может быть не замечен — шаг
        // должен быть заметно меньше минимальной ширины проёма.
        internal static List<(double Start, double End)> GetSolidWallSegments(
            Wall wall, XYZ scanStart, XYZ scanEnd, XYZ wallDir,
            double foundTopZ, double checkHeightFt, double sampleStepFt,
            Dictionary<ElementId, List<Solid>> geomCache)
        {
            var segments = new List<(double Start, double End)>();
            double fromProj = scanStart.DotProduct(wallDir);
            double toProj = scanEnd.DotProduct(wallDir);
            double length = toProj - fromProj;
            if (length <= 0) return segments;

            int sampleCount = Math.Max(2, (int)Math.Ceiling(length / sampleStepFt) + 1);
            double? segStart = null;
            double lastProj = fromProj;
            for (int i = 0; i < sampleCount; i++)
            {
                double offset = Math.Min(i * sampleStepFt, length);
                double proj = fromProj + offset;
                XYZ pt = scanStart + wallDir.Multiply(offset);
                bool hasSolid = HasSolidAt(wall, pt, foundTopZ, checkHeightFt, geomCache);
                if (hasSolid)
                {
                    if (segStart == null) segStart = proj;
                }
                else if (segStart != null)
                {
                    segments.Add((segStart.Value, lastProj));
                    segStart = null;
                }
                lastProj = proj;
            }
            if (segStart != null)
                segments.Add((segStart.Value, toProj));

            return segments;
        }

        // Проверяет одну XY-точку: есть ли у стены окно (проём, НЕ доходящий до низа
        // фундамента) — материал стены есть у самого низа (foundTopZ), но ВЫШЕ, до wallTopZ,
        // есть разрыв. В отличие от HasSolidAt (булев факт "есть/нет материал в диапазоне"),
        // здесь через SolidCurveIntersection читаем РЕАЛЬНЫЕ Z-интервалы материала по всей
        // высоте одним длинным тестовым отрезком — это даёт точную отметку низа окна (конец
        // нижнего сплошного участка). null — окна здесь нет (либо проём доходит до низа —
        // это уже не окно, а обычная дверь/проём, см. HasSolidAt/GetSolidWallSegments; либо
        // материал сплошной до верха стены).
        internal static double? GetWindowBottomZAt(List<Solid> solids, XYZ xy, double foundTopZ, double wallTopZ, double tolFt)
        {
            Line testLine = Line.CreateBound(
                new XYZ(xy.X, xy.Y, foundTopZ),
                new XYZ(xy.X, xy.Y, wallTopZ));

            var ranges = new List<(double Start, double End)>();
            foreach (Solid solid in solids)
            {
                SolidCurveIntersection sci;
                try { sci = solid.IntersectWithCurve(testLine, new SolidCurveIntersectionOptions()); }
                catch { continue; }
                if (sci == null) continue;
                for (int i = 0; i < sci.SegmentCount; i++)
                {
                    Curve seg = sci.GetCurveSegment(i);
                    double z0 = seg.GetEndPoint(0).Z;
                    double z1 = seg.GetEndPoint(1).Z;
                    ranges.Add((Math.Min(z0, z1), Math.Max(z0, z1)));
                }
            }
            if (ranges.Count == 0) return null;

            ranges.Sort((a, b) => a.Start.CompareTo(b.Start));
            var merged = new List<(double Start, double End)>();
            foreach (var r in ranges)
            {
                if (merged.Count > 0 && r.Start <= merged[merged.Count - 1].End + tolFt)
                {
                    var last = merged[merged.Count - 1];
                    merged[merged.Count - 1] = (last.Start, Math.Max(last.End, r.End));
                }
                else
                {
                    merged.Add(r);
                }
            }

            // Нижний участок должен начинаться прямо от foundTopZ (материал есть у самого
            // низа) — иначе это проём, доходящий до низа (не окно).
            if (merged[0].Start > foundTopZ + tolFt) return null;

            // И должен быть разрыв — значит выше есть второй участок. Один участок на всю
            // высоту — материал сплошной, окна здесь нет.
            if (merged.Count < 2) return null;

            return merged[0].End; // низ окна = конец нижнего сплошного участка
        }

        // Окна (проёмы в теле стены, НЕ доходящие до низа фундамента) вдоль стены — участки в
        // проекциях на wallDir, где обнаружен низ окна (см. GetWindowBottomZAt), с усреднённой
        // по участку отметкой низа. Сканируем с тем же шагом/приёмом, что и GetSolidWallSegments.
        internal static List<(double StartProj, double EndProj, double BottomZ)> GetWindowOpenings(
            List<Solid> solids, XYZ scanStart, XYZ scanEnd, XYZ wallDir,
            double foundTopZ, double wallTopZ, double sampleStepFt)
        {
            var result = new List<(double, double, double)>();
            double fromProj = scanStart.DotProduct(wallDir);
            double toProj = scanEnd.DotProduct(wallDir);
            double length = toProj - fromProj;
            if (length <= 0) return result;

            double tolFt = MmToFt(5.0);
            // Точность уточнения границы проёма бисекцией — сильно меньше шага сканирования
            // (sampleStepFt, обычно 100мм): иначе край проёма "плавал" бы в пределах целого шага
            // сканирования (подтверждено диагностикой — отступ от края окна получался на ~100мм
            // больше расчётного, ровно на величину шага сканирования), а отступы под окном
            // (WindowFillerEdgeMarginEvenMm/OtherMm, деление ширины на 200мм) чувствительны к
            // точности в несколько мм.
            double refineTolFt = MmToFt(2.0);

            bool HasWindowAt(double proj)
            {
                XYZ pt = scanStart + wallDir.Multiply(proj - fromProj);
                return GetWindowBottomZAt(solids, pt, foundTopZ, wallTopZ, tolFt).HasValue;
            }

            // Бисекция между точкой БЕЗ окна и точкой С окном, до точности refineTolFt
            // (направление аргументов не важно — работает в обе стороны). Возвращаем СЕРЕДИНУ
            // финальной вилки, а не сторону "окно есть" — иначе граница систематически
            // смещалась бы внутрь окна на величину до refineTolFt, и обнаруженная ширина проёма
            // получалась бы на ~2×refineTolFt уже реальной (подтверждено диагностикой: ровное
            // число вроде 1800мм переставало делиться на 200 нацело из-за этого смещения).
            double RefineBoundary(double noWinProj, double winProj)
            {
                while (Math.Abs(winProj - noWinProj) > refineTolFt)
                {
                    double mid = (noWinProj + winProj) / 2.0;
                    if (HasWindowAt(mid)) winProj = mid;
                    else noWinProj = mid;
                }
                return (noWinProj + winProj) / 2.0;
            }

            int sampleCount = Math.Max(2, (int)Math.Ceiling(length / sampleStepFt) + 1);
            double? segStart = null;
            double segBottomZSum = 0;
            int segBottomZCount = 0;
            double lastProj = fromProj;

            for (int i = 0; i < sampleCount; i++)
            {
                double offset = Math.Min(i * sampleStepFt, length);
                double proj = fromProj + offset;
                double? windowBottomZ = GetWindowBottomZAt(solids, scanStart + wallDir.Multiply(offset), foundTopZ, wallTopZ, tolFt);

                if (windowBottomZ.HasValue)
                {
                    if (segStart == null)
                        segStart = (i == 0) ? proj : RefineBoundary(lastProj, proj);
                    segBottomZSum += windowBottomZ.Value;
                    segBottomZCount++;
                }
                else if (segStart != null)
                {
                    double preciseEnd = RefineBoundary(proj, lastProj);
                    result.Add((segStart.Value, preciseEnd, segBottomZSum / segBottomZCount));
                    segStart = null;
                    segBottomZSum = 0;
                    segBottomZCount = 0;
                }
                lastProj = proj;
            }
            if (segStart != null)
                result.Add((segStart.Value, lastProj, segBottomZSum / segBottomZCount));

            return result;
        }

        // "Заполняющие" выпуски под окнами (см. WindowFillerDiaMm/WindowFillerStepMm/
        // WindowFillerGapMm/WindowFillerEdgeMarginEvenMm/OtherMm) — простые Г-образные стержни,
        // НЕ связанные с реальной арматурой стены: фиксированный диаметр, от фундамента вверх до
        // низа окна минус WindowFillerGapMm. По ширине — от ОБОИХ краёв проёма (а не от центра —
        // иначе на широком проёме стержни "сползаются" к середине, оставляя пустые края) с
        // отступом, который зависит от делимости ширины проёма на 200мм (100мм, если делится
        // нацело — тогда и шаг между стержнями получается ровно 200мм; 50мм — если нет), заполняя
        // промежуток равномерно фактическим шагом ≤ WindowFillerStepMm. На каждое окно — ДВА Rebar
        // (наружная и внутренняя грань), каждый ОДНИМ массивом (SetLayoutAsNumberWithSpacing) на
        // все позиции сразу, а не отдельными стержнями (как и обычные выпуски, см.
        // BuildStarterSet) — так они и редактируются, и считаются в спецификации как один элемент.
        //
        // Возвращает также along-wall проекции всех позиций (для слияния с pairProjections) —
        // чтобы хомуты/шпильки вокруг них считались по факту (см. GroupTiePairPositions), а не
        // одним пролётом через весь проём (между ближайшими РЕАЛЬНЫМИ стержнями стены, которых
        // под окном просто нет).
        private (List<ElementId> Ids, List<double> Projections) BuildWindowFillerStarters(
            Document doc, RebarShape shape, RebarBarType starterBarType, Element host, Wall wall,
            XYZ wallDir, XYZ exterior, double foundTopZ, double foundBotZ, double coverFt,
            Dictionary<ElementId, List<Solid>> geomCache)
        {
            var ids = new List<ElementId>();
            var allProjections = new List<double>();

            Curve wallCurve = (wall.Location as LocationCurve)?.Curve;
            if (wallCurve == null) return (ids, allProjections);

            XYZ wallStart = wallCurve.GetEndPoint(0);
            XYZ wallEnd = wallCurve.GetEndPoint(1);
            double startProj = wallStart.DotProduct(wallDir);
            double wallTopZ = wall.get_BoundingBox(null).Max.Z;
            double sampleStepFt = MmToFt(OpeningScanStepMm);

            List<Solid> solids;
            if (!geomCache.TryGetValue(wall.Id, out solids))
            {
                GeometryElement geom = wall.get_Geometry(new Options
                {
                    ComputeReferences = false,
                    DetailLevel = ViewDetailLevel.Fine
                });
                solids = geom?.OfType<Solid>().Where(s => !s.Faces.IsEmpty).ToList();
                geomCache[wall.Id] = solids;
            }
            if (solids == null || solids.Count == 0) return (ids, allProjections);

            var windows = GetWindowOpenings(solids, wallStart, wallEnd, wallDir, foundTopZ, wallTopZ, sampleStepFt);
            if (windows.Count == 0) return (ids, allProjections);

            double polkaLen = MmToFt(SelectPolkaLen(WindowFillerDiaMm));
            double polkaZ = foundBotZ + coverFt;
            double nominalStepFt = MmToFt(WindowFillerStepMm);
            double gapFt = MmToFt(WindowFillerGapMm);
            double edgeCheckHalfFt = MmToFt(50.0);
            double tolFt = MmToFt(5.0);

            foreach (var win in windows)
            {
                // Отступ от края проёма зависит от делимости его ширины на 200мм: делится —
                // отступ 100мм (тогда шаг между стержнями получается ровно 200мм без остатка),
                // не делится — отступ 50мм (см. WindowFillerEdgeMarginEvenMm/OtherMm). Ширину
                // округляем до 5мм перед проверкой делимости — обнаруженная геометрией граница
                // проёма (см. GetWindowOpenings/RefineBoundary) точна до нескольких мм, а не до
                // долей мм, и "круглая" по проекту ширина (напр. 1800мм) не должна из-за этого
                // шума попадать в "не делится на 200".
                double widthMm = FtToMm(win.EndProj - win.StartProj);
                double roundedWidthMm = Math.Round(widthMm / 5.0) * 5.0;
                double remainderMm = roundedWidthMm % WindowFillerStepMm;
                bool divisibleBy200 = remainderMm < 2.5 || remainderMm > WindowFillerStepMm - 2.5;
                double edgeCoverFt = MmToFt(divisibleBy200 ? WindowFillerEdgeMarginEvenMm : WindowFillerEdgeMarginOtherMm);

                // Расстановку считаем по ОКРУГЛЁННОЙ (до 5мм) ширине проёма, а не по сырой, чуть
                // шумной геометрии, центрируя её на середине обнаруженного проёма (сам центр
                // шуму почти не подвержен, в отличие от ширины) — иначе даже "круглый" по
                // проекту размер (2100мм, 1800мм и т.п.) давал бы не идеально ровный шаг между
                // стержнями что при отступе 100мм, что при 50мм (подтверждено диагностикой:
                // ширина 2098.4мм вместо 2100мм давала шаг 199.8мм вместо 200мм).
                double centerProj = (win.StartProj + win.EndProj) / 2.0;
                double halfWidthFt = MmToFt(roundedWidthMm) / 2.0;
                double effectiveStartProj = centerProj - halfWidthFt;
                double effectiveEndProj = centerProj + halfWidthFt;

                double usableStart = effectiveStartProj + edgeCoverFt;
                double usableEnd = effectiveEndProj - edgeCoverFt;
                double usableWidth = usableEnd - usableStart;

                int count;
                double actualStepFt;
                double firstProj;
                if (usableWidth <= 0)
                {
                    // Проём слишком узкий для отступов с обеих сторон — один стержень по центру.
                    count = 1;
                    actualStepFt = 0;
                    firstProj = (win.StartProj + win.EndProj) / 2.0;
                }
                else
                {
                    // Небольшой эпсилон перед Ceiling — иначе шум плавающей точки при точном
                    // делении (напр. usableWidth ровно 8×200мм) мог бы округлить вверх лишний
                    // стержень.
                    count = (int)Math.Ceiling(usableWidth / nominalStepFt - 1e-6) + 1;
                    actualStepFt = usableWidth / (count - 1);
                    firstProj = usableStart;
                }

                // Минимум отметки низа окна по ВСЕМ позициям будущего массива — перестраховка
                // на случай не идеально горизонтальной подошвы окна (один массив не может нести
                // разную длину вертикали на разных позициях, см. GetWindowBottomZAt).
                double minWindowBottomZ = double.MaxValue;
                var positionsProj = new List<double>();
                for (int k = 0; k < count; k++)
                {
                    double proj = firstProj + k * actualStepFt;
                    positionsProj.Add(proj);
                    XYZ xy = wallStart + wallDir.Multiply(proj - startProj);
                    double z = GetWindowBottomZAt(solids, xy, foundTopZ, wallTopZ, tolFt) ?? win.BottomZ;
                    if (z < minWindowBottomZ) minWindowBottomZ = z;
                }

                double topZ = minWindowBottomZ - gapFt;
                double vertLen = topZ - polkaZ;
                if (vertLen <= 0) continue; // окно слишком низко — выпуск здесь не влезает

                XYZ firstXY = wallStart + wallDir.Multiply(firstProj - startProj);

                // Поперечное смещение от оси стены к наружному/внутреннему краю — БЕЗ него оба
                // массива (наружный и внутренний) ложились ОДИН НА ДРУГОЙ по оси стены. Логика —
                // как у остальных (основных) выпусков, НЕ как у горизонтальной арматуры: там
                // TieCoverMm минус половина диаметра (TieCoverMm измеряется до ДАЛЬНЕЙ от грани
                // стены поверхности стержня). Здесь TieCoverMm — защитный слой от грани стены до
                // БЛИЖНЕЙ (внешней) поверхности стержня, поэтому до оси стержня — TieCoverMm
                // ПЛЮС половина диаметра.
                double faceToCenterlineMm = TieCoverMm + WindowFillerDiaMm / 2.0;
                double halfB = wall.Width / 2.0 - MmToFt(faceToCenterlineMm);
                if (halfB <= 0)
                    throw new InvalidOperationException(
                        $"Толщина стены {FtToMm(wall.Width):0}мм меньше двойного отступа под выпуск под окном " +
                        $"({2 * faceToCenterlineMm:0}мм = 2×({TieCoverMm:0}+{WindowFillerDiaMm / 2.0:0.#}), ⌀{WindowFillerDiaMm}мм).");

                foreach (bool isExteriorFace in new[] { true, false })
                {
                    XYZ polkaDir = isExteriorFace ? exterior : exterior.Negate();
                    XYZ acrossOffset = polkaDir.Multiply(halfB);
                    XYZ faceXY = new XYZ(firstXY.X + acrossOffset.X, firstXY.Y + acrossOffset.Y, firstXY.Z);

                    XYZ bendPt = new XYZ(faceXY.X, faceXY.Y, polkaZ);
                    XYZ topPt = bendPt + XYZ.BasisZ.Multiply(vertLen);

                    XYZ footEndPt = bendPt + polkaDir.Multiply(polkaLen);
                    if (!HasSolidAt(host, footEndPt, polkaZ - edgeCheckHalfFt, edgeCheckHalfFt * 2, geomCache))
                    {
                        polkaDir = polkaDir.Negate();
                        footEndPt = bendPt + polkaDir.Multiply(polkaLen);
                    }

                    var curves = new List<Curve>
                    {
                        Line.CreateBound(topPt, bendPt),
                        Line.CreateBound(bendPt, footEndPt)
                    };

                    Rebar starter = Rebar.CreateFromCurvesAndShape(
                        doc, shape, starterBarType, null, null, host, wallDir, curves,
                        RebarHookOrientation.Right, RebarHookOrientation.Right);
                    if (starter == null)
                        throw new InvalidOperationException(
                            $"Не удалось создать выпуск под окном по форме «{shape.Name}» — кривые не подошли под форму.");

                    FixShapeParams(starter, new[] { VertParam, PolkaParam }, new[] { vertLen, polkaLen });

                    if (count > 1)
                        starter.GetShapeDrivenAccessor().SetLayoutAsNumberWithSpacing(count, actualStepFt, true, true, true);

                    SetIfExists(starter, MarkParamName, StarterMarkValue);
                    ids.Add(starter.Id);
                }

                allProjections.AddRange(positionsProj);
            }

            return (ids, allProjections);
        }

        // Абсолютная (мировая) точка низа стержня в заданной позиции массива. НЕ через
        // GetBarPositionTransform — он возвращает transform ОТНОСИТЕЛЬНО позиции 0 (у позиции 0
        // Origin всегда (0;0;0)), а не мировые координаты — это и оказалось причиной того, что
        // все выпуски строились в одной точке далеко от стены. GetCenterlineCurves отдаёт
        // реальную геометрию стержня в координатах модели (см. тот же приём в
        // WallRebarAnnotation.cs/WallSectionRebarTags.cs).
        internal static XYZ GetBarBottomPoint(Rebar rebar, int barPositionIndex)
        {
            IList<Curve> curves = rebar.GetCenterlineCurves(
                false, false, false, MultiplanarOption.IncludeOnlyPlanarCurves, barPositionIndex);
            if (curves == null || curves.Count == 0)
                throw new InvalidOperationException(
                    $"Rebar {rebar.Id}: не удалось получить геометрию позиции {barPositionIndex}.");

            XYZ best = null;
            foreach (Curve c in curves)
            {
                XYZ p0c = c.GetEndPoint(0);
                XYZ p1c = c.GetEndPoint(1);
                if (best == null || p0c.Z < best.Z) best = p0c;
                if (p1c.Z < best.Z) best = p1c;
            }
            return best;
        }

        // Смещение между позициями i и j в массиве стержней — АБСОЛЮТНЫЙ (не зависящий от
        // активного вида) способ узнать шаг/направление раскладки: GetBarPositionTransform
        // возвращает transform каждой позиции ОТНОСИТЕЛЬНО позиции 0, поэтому нужная величина —
        // разница Origin двух вызовов (см. тот же приём в WallSectionRebarTags.cs), а не Origin
        // одной позиции сам по себе (см. комментарий у GetBarBottomPoint про прежний баг
        // "выпуски за 169м"). Раньше шаг/направление читали кластеризацией геометрии на
        // активном виде (CollectVerticalLineStartPoints/GetArrayBarPoints) — тот подход зависел
        // от того, какой вид открыт (на поперечном разрезе стены кластеризация схлопывалась в
        // одну точку, шаг получался 0 и валил SetLayoutAsNumberWithSpacing), и требовал
        // отдельного 3D-вида "про запас"; этот способ не зависит от вида вообще.
        internal static XYZ GetBarPositionOffset(Rebar rebar, int fromIndex, int toIndex)
        {
            RebarShapeDrivenAccessor acc = rebar.GetShapeDrivenAccessor();
            if (acc == null)
                throw new InvalidOperationException($"Rebar {rebar.Id}: нет RebarShapeDrivenAccessor.");
            return acc.GetBarPositionTransform(toIndex).Origin - acc.GetBarPositionTransform(fromIndex).Origin;
        }

        // Проекции ВСЕХ позиций стержня на wallDir (вдоль стены) — нужно хомутам, чтобы считать
        // длину по факту расстояния до конкретной соседней пары, а не по "среднему" шагу
        // семейства (см. комментарий в Execute про wallPairProjections/BuildTiesForWall).
        internal static List<double> GetAllBarProjections(Rebar rebar, XYZ wallDir)
        {
            int count = rebar.NumberOfBarPositions;
            XYZ first = GetBarBottomPoint(rebar, 0);
            var result = new List<double> { first.DotProduct(wallDir) };
            if (count > 1)
            {
                // Не через GetBarPositionOffset в цикле — тот на КАЖДЫЙ вызов заново получает
                // accessor и transform(0), хотя они одни и те же для всех позиций одного
                // стержня. Получаем один раз и переиспользуем.
                RebarShapeDrivenAccessor acc = rebar.GetShapeDrivenAccessor();
                if (acc == null)
                    throw new InvalidOperationException($"Rebar {rebar.Id}: нет RebarShapeDrivenAccessor.");
                XYZ t0 = acc.GetBarPositionTransform(0).Origin;
                for (int i = 1; i < count; i++)
                    result.Add((first + (acc.GetBarPositionTransform(i).Origin - t0)).DotProduct(wallDir));
            }
            return result;
        }

        // Грани (внутр./наружн.) + высота (низкий/высокий)
        internal SetInfo ClassifySet(Rebar wallSet, Wall wall, XYZ exterior)
        {
            XYZ p0 = GetBarBottomPoint(wallSet, 0);

            // Сторона грани: знак проекции (позиция бара - ось стены) на наружную нормаль
            XYZ axisPt = (wall.Location as LocationCurve).Curve.Project(p0).XYZPoint;
            double side = (p0 - axisPt).DotProduct(exterior);
            bool isExterior = side > 0;

            double bottomZ = p0.Z;

            return new SetInfo
            {
                IsExterior = isExterior,
                BottomZ = bottomZ
            };
        }

        // ---- Построение выпусков --------------------------------------------

        private (Rebar Starter, double SpacingFt, double MinProjFt, double MaxProjFt, XYZ WallDir, int DiaMm) BuildStarterSet(
            Document doc, RebarShape shape, Dictionary<string, RebarBarType> rebarBarTypesByName, Rebar wallSet, Element host,
            Wall wall, SetInfo info, XYZ exterior, double foundBotZ, double coverFt,
            Dictionary<ElementId, List<Solid>> geomCache)
        {
            // Диаметр берём у СТЕНОВОГО стержня — от него зависит и длина полки/вылета (таблицы
            // ниже завязаны на диаметр стыкуемой арматуры стены), и имя типоразмера выпуска.
            RebarBarType wallBarType = doc.GetElement(wallSet.GetTypeId()) as RebarBarType;
            double diaFt = wallBarType.BarModelDiameter;
            int diaMm = (int)Math.Round(FtToMm(diaFt));

            string starterTypeName = StarterBarTypeName(diaMm);
            rebarBarTypesByName.TryGetValue(starterTypeName, out RebarBarType starterBarType);
            if (starterBarType == null)
                throw new InvalidOperationException($"Не найден типоразмер арматуры \"{starterTypeName}\".");

            // Геометрия выпуска
            double polkaLen = MmToFt(SelectPolkaLen(diaMm));            // фикс. длина полки по диаметру
            double polkaZ   = foundBotZ + coverFt;                      // полка по нижнему слою

            // Вертикаль — от полки до НИЗА ОСНОВНОЙ АРМАТУРЫ СТЕНЫ ПО ФАКТУ (info.BottomZ, а не
            // верх фундамента: реальный отступ основной арматуры от фундамента может быть любым
            // в разных проектах — 500мм, 200мм и т.д., берём его из модели). Положение info.BottomZ
            // относительно верха фундамента не проверяем — важно только итоговое значение
            // нахлёста/сварочного зазора, а не то, выше стержень фундамента или нет.
            //
            // Крупный диаметр (>= DiaThresholdMm) стыкуется СВАРКОЙ, а не внахлёст — нахлёст тут
            // не нужен вовсе, выпуск доходит почти до низа основной арматуры, не доходя
            // WeldGapMm (зазор под сварной шов).
            double vertLen;
            if (diaMm >= DiaThresholdMm)
            {
                vertLen = (info.BottomZ - polkaZ) - MmToFt(WeldGapMm);
            }
            else
            {
                string concreteClass = GetConcreteClass(doc, wall);
                double nahlestMm = SelectNahlest(concreteClass, diaMm); // чистый нахлёст из таблицы
                double nahlestFt = MmToFt(nahlestMm);
                vertLen = (info.BottomZ - polkaZ) + nahlestFt;
            }

            // Направление полки: наружу от центра стены для своей грани
            XYZ polkaDir = info.IsExterior ? exterior : exterior.Negate();

            // Раскладка исходного набора (стержень-в-стержень). Точку вставки (first) берём
            // ТОЛЬКО через GetBarBottomPoint/GetCenterlineCurves(index 0) — это официальный API,
            // возвращающий точную ось стержня (тот же метод уже верно работает в ClassifySet).
            // Шаг/направление массива — через GetBarPositionOffset (см. её комментарий): не
            // зависит от активного вида, в отличие от прежней кластеризации геометрии, которая
            // на поперечном разрезе стены схлопывалась в одну точку и валила
            // SetLayoutAsNumberWithSpacing нулевым шагом.
            int count = wallSet.NumberOfBarPositions;
            XYZ first = GetBarBottomPoint(wallSet, 0);
            XYZ last = count > 1 ? first + GetBarPositionOffset(wallSet, 0, count - 1) : first;
            double spacing = count > 1 ? first.DistanceTo(last) / (count - 1) : 0.0;

            Curve wallCurve = (wall.Location as LocationCurve)?.Curve;
            XYZ wallDir = wallCurve != null
                ? (wallCurve.GetEndPoint(1) - wallCurve.GetEndPoint(0)).Normalize()
                : XYZ.BasisZ.CrossProduct(polkaDir).Normalize();

            // Направление раскладки — прямо от first к last (совпадает с wallDir или обратно).
            XYZ arrayNormal = count > 1 ? (last - first).Normalize() : wallDir;
            double minProj = Math.Min(first.DotProduct(wallDir), last.DotProduct(wallDir));
            double maxProj = Math.Max(first.DotProduct(wallDir), last.DotProduct(wallDir));

            // Ломаная ЯВНЫМИ кривыми (а не origin/xVec/yVec формы): по опыту этого проекта
            // (см. RebarBuilder.CreateBentRebarElement) CreateFromRebarShape укладывал
            // Г-образные стержни не туда и не так — с точными координатами это надёжнее.
            XYZ bendPt = new XYZ(first.X, first.Y, polkaZ);      // низ, точка загиба
            XYZ topPt  = bendPt + XYZ.BasisZ.Multiply(vertLen);  // верх вертикального участка

            // Если стена стоит у края фундамента, полка, направленная наружу (как обычно для
            // наружной грани), может выйти за пределы тела фундамента — проверяем конец полки
            // и, если там пусто, разворачиваем внутрь.
            double edgeCheckHalfFt = MmToFt(50.0);
            XYZ footEndPt = bendPt + polkaDir.Multiply(polkaLen);
            if (!HasSolidAt(host, footEndPt, polkaZ - edgeCheckHalfFt, edgeCheckHalfFt * 2, geomCache))
            {
                polkaDir = polkaDir.Negate();
                footEndPt = bendPt + polkaDir.Multiply(polkaLen);
            }

            var curves = new List<Curve>
            {
                Line.CreateBound(topPt, bendPt),
                Line.CreateBound(bendPt, footEndPt)
            };

            Rebar starter = Rebar.CreateFromCurvesAndShape(
                doc, shape, starterBarType, null, null, host, arrayNormal, curves,
                RebarHookOrientation.Right, RebarHookOrientation.Right);
            if (starter == null)
                throw new InvalidOperationException(
                    $"Не удалось создать выпуск по форме «{shape.Name}» — кривые не подошли под форму.");

            // Тиражирование в набор вдоль оси стены
            if (count > 1)
            {
                starter.GetShapeDrivenAccessor().SetLayoutAsNumberWithSpacing(
                    count, spacing, true, true, true);
                // CALIBRATE: если раскладка пошла в обратную сторону — развернуть normal.
            }

            // Параметры формы, которые Revit вывел из кривых, надо принудительно поправить на
            // точные целевые значения; роль параметра (какой BI_* — вертикаль, какой — полка)
            // сопоставляем по близости величин, а не по имени (см. RebarBuilder.FixShapeParams —
            // для этой же формы «(форма)11» имя не гарантирует роль).
            FixShapeParams(starter, new[] { VertParam, PolkaParam }, new[] { vertLen, polkaLen });

            SetIfExists(starter, MarkParamName, StarterMarkValue);

            return (starter, spacing, minProj, maxProj, wallDir, diaMm);
        }

        // Группирует список реальных позиций пар "как для шага TieMinEffectiveSpacingMm"
        // (200мм) — ЖАДНО: в каждую группу добавляем позиции подряд, пока пролёт от первой до
        // очередной не превышает 200мм; как только следующая позиция вывела бы пролёт за
        // 200мм — закрываем группу и начинаем следующую с неё. Каждая группа — минимум 2
        // позиции (даже если пролёт уже больше 200мм — меньше 2 объединять нечего).
        //
        // Для родного шага 200мм (позиции ровно через 200мм) это даёт ПО 2 позиции на группу —
        // как и раньше, подтверждено пользователем. Для 150мм (не делится на 200 без остатка,
        // 2 позиции уже дают пролёт 300>200) — тоже по 2 позиции, пролёт остаётся честные
        // 150мм, растягивать нельзя (хомут получился бы длиннее, чем нужно). А вот для 100мм —
        // 2 позиции дают только 200мм пролёта РОВНО, третья добавляется (100+100=200, всё ещё
        // не больше 200), четвёртая уже дала бы 300>200 — значит группа из 3 позиций, пролёт
        // 200мм: на 12 стержней получается 4 хомута (12/3), а не 6 (12/2) — "как для шага
        // 200мм" в точности, а не просто растянутый по размеру, но частый по расстановке.
        internal static List<(double P1, double P2)> GroupTiePairPositions(List<double> pairProjections)
        {
            var groups = new List<(double, double)>();
            double targetFt = MmToFt(TieMinEffectiveSpacingMm) + MmToFt(0.5); // небольшой допуск на округление
            int i = 0;
            while (i + 1 < pairProjections.Count)
            {
                int j = i + 1;
                while (j + 1 < pairProjections.Count && pairProjections[j + 1] - pairProjections[i] <= targetFt)
                    j++;
                groups.Add((pairProjections[i], pairProjections[j]));
                i = j + 1;
            }
            return groups;
        }

        // Хомуты вокруг выпусков в теле фундамента. Один хомут — замкнутая горизонтальная рамка
        // (Условный_A вдоль стены, Условный_B поперёк), объединяющая ОБЕ соседние пары
        // (наружный+внутренний выпуск своего уровня И наружный+внутренний выпуск соседнего,
        // шахматно смещённого уровня — итого 4 стержня), как хомут колонны/пилона. Вертикально
        // хомуты стоят "родным" 1D-массивом Revit (аналогично выпускам вдоль стены, только
        // здесь вдоль Z): от (верх фундамента − TieTopOffsetMm) вниз с шагом TieVerticalStepMm,
        // пока не выйдем из тела фундамента.
        //
        // Вдоль стены — по ПОЛНОМУ списку реальных позиций пар (pairProjections, собран в
        // Execute из ВСЕХ наборов стены, не по одному "семейству" с одним шагом): группируем их
        // по 2 подряд (0&1, 2&3, 4&5...), длина каждого хомута считается по ФАКТИЧЕСКОМУ
        // расстоянию между конкретными двумя стержнями этой группы, а не по "среднему" шагу
        // семейства — на стыке зон с разным шагом (напр. 200мм и 150мм) хомут, объединяющий
        // последнюю пару одной зоны и первую пару другой, получает верную длину (150мм), а не
        // шаг соседней 200-миллиметровой зоны. Раз позиции РЕАЛЬНЫЕ (не экстраполированы от
        // одной опорной точки с фиксированным шагом), отдельная проверка "не вышел ли хомут за
        // физический конец стены" больше не нужна — весь список и так лежит внутри стены.
        private List<ElementId> BuildTiesForWall(
            Document doc, RebarShape tieShape, RebarBarType tieBarType,
            Element host, Wall wall, List<double> pairProjections, XYZ wallDir, int dowelDiaMm,
            double foundTopZ, double foundBotZ, double coverFt)
        {
            var tieIds = new List<ElementId>();
            if (pairProjections.Count < 2)
                return tieIds; // меньше 2 реальных пар — объединять нечего

            // Условный_B = толщина стены - 2×защитный слой + диаметр выпуска, округлённое ВВЕРХ
            // до кратного 5мм (подтверждено на трёх зонах шага: 300-2×35+12=242 → 245мм — везде
            // совпало, диаметр выпуска в расчёте константой не является, дальний край хомута
            // должен доходить до края выпуска, как и у горизонтальной арматуры, см.
            // BuildHorizontalBarsForWall). Не зависит от группы/шага — считаем один раз.
            double bFtRawMm = FtToMm(wall.Width) - 2 * TieCoverMm + dowelDiaMm;
            double bFt = MmToFt(Math.Ceiling(bFtRawMm / 5.0) * 5.0);
            if (bFt <= 0)
                throw new InvalidOperationException(
                    $"Толщина стены {FtToMm(wall.Width):0}мм меньше двойного защитного слоя хомута " +
                    $"({2 * TieCoverMm:0}мм) минус диаметр выпуска ({dowelDiaMm}мм) — Условный_B получается отрицательным.");
            double halfB = bFt / 2.0;

            XYZ acrossDir = wall.Orientation.Normalize(); // поперёк стены, из наружу-в-внутрь безразлично

            // Нижняя граница массива — не низ фундамента, а низ САМОГО ВЫПУСКА (polkaZ, та же
            // отметка, что и в BuildStarterSet): ниже нет вертикального участка стержня, обхватывать
            // там нечего.
            double dowelBottomZ = foundBotZ + coverFt;
            double topZ = foundTopZ - MmToFt(TieTopOffsetMm);
            double stepZFt = MmToFt(TieVerticalStepMm);
            int vCount = (int)Math.Floor((topZ - dowelBottomZ) / stepZFt) + 1;
            if (vCount < 1)
                throw new InvalidOperationException(
                    "Недостаточная высота фундамента для размещения хомутов " +
                    $"(верх−{TieTopOffsetMm:0}мм уже ниже низа выпуска).");

            Curve wallCurve = (wall.Location as LocationCurve)?.Curve;
            if (wallCurve == null)
                throw new InvalidOperationException("У стены нет прямой оси (LocationCurve) для расстановки хомутов.");
            XYZ wallStart = wallCurve.GetEndPoint(0);
            double startProj = wallStart.DotProduct(wallDir);

            double defaultAFt = MmToFt(TieDefaultAMm);
            double defaultBFt = MmToFt(TieDefaultBMm);

            foreach (var group in GroupTiePairPositions(pairProjections))
            {
                double p1 = group.P1;
                double p2 = group.P2;

                // Пролёт группы (ФАКТИЧЕСКОЕ расстояние от первой до последней позиции в ней,
                // см. GroupTiePairPositions) — не шаг "семейства". Для частых зон (100мм и
                // подобных) группа уже подобрана так, чтобы пролёт был ровно ~200мм (несколько
                // позиций внутри), поэтому дополнительно "дотягивать" здесь ничего не нужно.
                double aFt = (p2 - p1) + MmToFt(TieAExtraMm);
                double halfA = aFt / 2.0;

                double midProj = (p1 + p2) / 2.0;
                XYZ center = wallStart + wallDir.Multiply(midProj - startProj);
                XYZ c = new XYZ(center.X, center.Y, topZ);

                // При изменении Условный_A/B неподвижным остаётся ДАЛЬНИЙ угол (origin +
                // xVec×TieDefaultB + yVec×TieDefaultA, посчитанный по УМОЛЧАНИЯМ формы) — форма
                // "усыхает" к нему обратно, а не растёт от origin к цели (подтверждено
                // диагностикой: смещение BBox в точности равно TieDefaultB-B по xVec и
                // TieDefaultA-A по yVec). Умолчания — константы, а НЕ чтение LookupParameter с
                // только что созданного хомута: при массовой расстановке Revit подставляет в
                // новый экземпляр не чистое умолчание семейства, а последнее использованное в
                // сеансе значение — начиная со второго хомута компенсация считалась бы почти
                // нулевой. Считаем на КАЖДУЮ группу заново — aFt теперь свой у каждой.
                XYZ correction = acrossDir.Multiply(bFt - defaultBFt) + wallDir.Multiply(aFt - defaultAFt);

                // origin — УГОЛ хомута, не центр: RebarStyle формы хомута = Standard, и
                // CreateFromRebarShape растит форму от origin строго в сторону +xVec на
                // Условный_B и +yVec на Условный_A (без центрирования) — подтверждено
                // диагностикой (замер BBox трёх вариантов осей). Поэтому берём "нижний" угол,
                // чтобы итоговый прямоугольник лёг симметрично вокруг center.
                XYZ origin = c - wallDir.Multiply(halfA) - acrossDir.Multiply(halfB);

                // xVec → направление Условный_B (поперёк стены), yVec → направление
                // Условный_A (вдоль стены) — порядок ролей подтверждён тем же экспериментом,
                // он обратный "естественному" (не xVec=вдоль/yVec=поперёк).
                Rebar tie = Rebar.CreateFromRebarShape(doc, tieShape, tieBarType, host, origin, acrossDir, wallDir);
                if (tie == null)
                    throw new InvalidOperationException($"Не удалось создать хомут по форме «{tieShape.Name}».");

                SetShapeParamDirect(tie, TieParamA, aFt);
                SetShapeParamDirect(tie, TieParamB, bFt);

                if (!correction.IsZeroLength())
                    ElementTransformUtils.MoveElement(doc, tie.Id, correction);

                // Без Regenerate() между сдвигом-компенсацией и SetLayoutAsNumberWithSpacing
                // массив раскладывался по ещё не пересчитанной геометрии — верх хомута уезжал
                // от verhTopOffsetMm (подтверждено диагностикой: без Regenerate тут выходило
                // 59мм вместо 50мм, с ним — ровно 50мм).
                doc.Regenerate();

                if (vCount > 1)
                {
                    tie.GetShapeDrivenAccessor().SetLayoutAsNumberWithSpacing(vCount, stepZFt, true, true, true);
                    // CALIBRATE: направление массива = xVec×yVec, знак не проверен — если
                    // раскладка пошла вверх вместо вниз, потребуется скорректировать.
                }

                SetIfExists(tie, MarkParamName, StarterMarkValue);

                tieIds.Add(tie.Id);
            }

            return tieIds;
        }

        // Горизонтальная арматура вдоль стены (альтернатива хомутам) — два прямых стержня
        // (наружный и внутренний), каждый длиной "длина стены минус 20мм с каждой стороны",
        // тиражированные по высоте с пользовательским шагом. Прямой стержень — простой,
        // проверенный приём (см. RebarBuilder.CreateStraightRebarElement в RebarZones):
        // Rebar.CreateFromCurves с RebarStyle.Standard, без формы и без сюрпризов origin/xVec,
        // которые были у хомута.
        //
        // Отступ от грани стены — НЕ фиксированный защитный слой (как у хомута, TieCoverMm),
        // а половина диаметра САМОГО ВЫПУСКА (dowelDiaMm): горизонтальный стержень должен идти
        // вплотную к выпуску, чтобы их края совпадали, а не втапливаться в тело на произвольную
        // фиксированную величину.
        private List<ElementId> BuildHorizontalBarsForWall(
            Document doc, RebarBarType barType, double stepMm, Element host, Wall wall, XYZ wallDir, int dowelDiaMm,
            double foundTopZ, double foundBotZ, double coverFt, Dictionary<ElementId, List<Solid>> geomCache)
        {
            var ids = new List<ElementId>();

            Curve wallCurve = (wall.Location as LocationCurve)?.Curve;
            if (wallCurve == null)
                throw new InvalidOperationException("У стены нет прямой оси (LocationCurve) для расстановки горизонтальной арматуры.");
            XYZ wallStart = wallCurve.GetEndPoint(0);
            XYZ wallEnd = wallCurve.GetEndPoint(1);

            double topZ = foundTopZ - MmToFt(TieTopOffsetMm);
            double stepFt = MmToFt(stepMm);
            double dowelBottomZ = foundBotZ + coverFt;
            int vCount = (int)Math.Floor((topZ - dowelBottomZ) / stepFt) + 1;
            if (vCount < 1)
                throw new InvalidOperationException(
                    "Недостаточная высота фундамента для размещения горизонтальной арматуры " +
                    $"(верх−{TieTopOffsetMm:0}мм уже ниже низа выпуска).");

            XYZ acrossDir = wall.Orientation.Normalize();
            // Отступ от грани стены = защитный слой минус половина диаметра выпуска (та же
            // логика, что и в Условный_B хомута: wall.Width - 2×TieCoverMm + dowelDiaMm, см.
            // BuildTiesForWall) — край горизонтального стержня доходит до края выпуска, но не
            // игнорирует защитный слой целиком, как было раньше (отступ = просто половина
            // диаметра выпуска, без TieCoverMm вообще).
            double dowelCoverMm = TieCoverMm - dowelDiaMm / 2.0;
            double halfB = wall.Width / 2.0 - MmToFt(dowelCoverMm);
            if (halfB <= 0)
                throw new InvalidOperationException(
                    $"Толщина стены {FtToMm(wall.Width):0}мм меньше двойного отступа под выпуск " +
                    $"({2 * dowelCoverMm:0}мм = 2×({TieCoverMm:0}−{dowelDiaMm / 2.0:0.#}), ⌀{dowelDiaMm}мм).");

            // Проём в стене (в т.ч. вырез правкой профиля) — под ним у стены нет материала у
            // самого низа (foundTopZ), горизонтальная арматура там проходить не должна.
            // Сканируем всю длину стены и режем на отдельные СПЛОШНЫЕ участки — вместо одного
            // стержня на всю стену получаем по стержню на каждый участок, от края до края
            // (торец стены или край проёма — без отступа).
            double checkHeightFt = MmToFt(OpeningCheckHeightMm);
            double sampleStepFt = MmToFt(OpeningScanStepMm);
            var solidSegments = GetSolidWallSegments(wall, wallStart, wallEnd, wallDir, foundTopZ, checkHeightFt, sampleStepFt, geomCache);

            double startProj = wallStart.DotProduct(wallDir);
            var barSpans = new List<(XYZ P1, XYZ P2)>();
            foreach (var seg in solidSegments)
            {
                double segStartProj = seg.Start;
                double segEndProj = seg.End;
                if (segEndProj <= segStartProj) continue; // участок нулевой длины
                XYZ p1 = wallStart + wallDir.Multiply(segStartProj - startProj);
                XYZ p2 = wallStart + wallDir.Multiply(segEndProj - startProj);
                barSpans.Add((p1, p2));
            }
            if (barSpans.Count == 0)
                throw new InvalidOperationException(
                    "Не нашлось ни одного участка стены для горизонтальной арматуры " +
                    "(весь пролёт занят проёмами).");

            XYZ normalDown = XYZ.BasisZ.Negate(); // направление тиражирования массива вниз по Z

            foreach (XYZ lateral in new[] { acrossDir.Multiply(halfB), acrossDir.Negate().Multiply(halfB) })
            {
                foreach (var span in barSpans)
                {
                    XYZ p1 = new XYZ(span.P1.X + lateral.X, span.P1.Y + lateral.Y, topZ);
                    XYZ p2 = new XYZ(span.P2.X + lateral.X, span.P2.Y + lateral.Y, topZ);
                    var curves = new List<Curve> { Line.CreateBound(p1, p2) };

                    Rebar rebar = Rebar.CreateFromCurves(
                        doc, RebarStyle.Standard, barType, null, null, host,
                        normalDown, curves, RebarHookOrientation.Right, RebarHookOrientation.Right,
                        true, true);
                    if (rebar == null)
                        throw new InvalidOperationException("Не удалось создать горизонтальный стержень.");

                    if (vCount > 1)
                        rebar.GetShapeDrivenAccessor().SetLayoutAsNumberWithSpacing(vCount, stepFt, true, true, true);

                    SetIfExists(rebar, MarkParamName, StarterMarkValue);
                    ids.Add(rebar.Id);
                }
            }

            return ids;
        }

        // Шпильки — заменяют хомуты в режиме "Горизонтальная арматура": простой прямой
        // стержень поперёк стены (форма "(форма)шпилька", один параметр BI_A = вся длина),
        // стягивающий наружный и внутренний слои горизонтальной арматуры друг с другом.
        //
        // CreateFromCurvesAndShape (как у выпусков, BuildStarterSet) НЕ подошёл — форма
        // шпильки, как и форма хомута, не сводится к ОДНОЙ прямой кривой (видимо, как и у
        // хомута, в самой форме есть свои сегменты/загибы) и Revit возвращает null ("кривые
        // не подошли под форму"). Поэтому, как и для хомута, используем
        // CreateFromRebarShape(origin, xVec, yVec) — Revit сам строит геометрию формы, а не
        // подгоняет её под явные кривые (см. тот же приём в BuildTiesForWall).
        //
        // По стене — по ПОЛНОМУ списку реальных позиций пар (pairProjections, тот же список,
        // что и у хомутов, см. wallPairProjections/BuildTiesForWall в Execute), а не по шагу
        // "своего" семейства: берём КАЖДУЮ ВТОРУЮ позицию (индексы 0, 2, 4, ...) — реальные
        // пары идут через effectiveSpacingFt (см. BuildTiesForWall — там же объяснение
        // шахматной раскладки), значит "через одну" пару = через одну позицию в этом списке.
        // Раз позиции РЕАЛЬНЫЕ (не экстраполированы шагом одного семейства), переходы между
        // зонами с разным шагом обрабатываются сами собой — так же, как у хомутов. По высоте —
        // отдельный 1D-массив (как у хомута), от той же отметки (верх фундамента -
        // TieTopOffsetMm), но со своим шагом ShpilkaVerticalStepMm.
        private List<ElementId> BuildShpilkiForWall(
            Document doc, RebarShape shpilkaShape, RebarBarType shpilkaBarType,
            Element host, Wall wall, List<double> pairProjections, XYZ wallDir,
            double foundTopZ, double foundBotZ, double coverFt, Dictionary<ElementId, List<Solid>> geomCache)
        {
            var ids = new List<ElementId>();
            if (pairProjections.Count == 0)
                return ids; // нет реальных позиций — ставить не на чем

            double aFt = wall.Width - MmToFt(ShpilkaOffsetMm);
            if (aFt <= 0)
                throw new InvalidOperationException(
                    $"Толщина стены {FtToMm(wall.Width):0}мм меньше отступа шпильки ({ShpilkaOffsetMm:0}мм).");

            XYZ acrossDir = wall.Orientation.Normalize();

            double dowelBottomZ = foundBotZ + coverFt;
            double topZ = foundTopZ - MmToFt(TieTopOffsetMm);
            double stepZFt = MmToFt(ShpilkaVerticalStepMm);
            int vCount = (int)Math.Floor((topZ - dowelBottomZ) / stepZFt) + 1;
            if (vCount < 1)
                throw new InvalidOperationException(
                    "Недостаточная высота фундамента для размещения шпилек " +
                    $"(верх−{TieTopOffsetMm:0}мм уже ниже низа выпуска).");

            Curve wallCurve = (wall.Location as LocationCurve)?.Curve;
            if (wallCurve == null)
                throw new InvalidOperationException("У стены нет прямой оси (LocationCurve) для расстановки шпилек.");
            XYZ wallStart = wallCurve.GetEndPoint(0);
            double startProj = wallStart.DotProduct(wallDir);

            double halfA = aFt / 2.0;
            double checkHeightFt = MmToFt(OpeningCheckHeightMm);

            for (int i = 0; i < pairProjections.Count; i += 2)
            {
                XYZ center = wallStart + wallDir.Multiply(pairProjections[i] - startProj);
                XYZ c = new XYZ(center.X, center.Y, topZ);

                // Под проёмом (в т.ч. вырез правкой профиля стены) у стены нет материала у
                // самого низа (foundTopZ) — шпилька там стоять не должна, просто пропускаем
                // эту позицию (не сдвигаем на соседнюю — "через одну" раскладка остаётся как
                // задумана, под проёмом будет пробел).
                if (!HasSolidAt(wall, c, foundTopZ, checkHeightFt, geomCache))
                    continue;

                // origin — угол (не центр): по аналогии с хомутом CreateFromRebarShape растит
                // форму от origin в сторону +xVec на BI_A (без центрирования) — берём "ближний"
                // угол, чтобы итоговый стержень лёг симметрично вокруг center. xVec=поперёк
                // стены (направление BI_A), yVec задаёт только плоскость формы (роль как у
                // хомута: yVec=вдоль стены).
                XYZ origin = c - acrossDir.Multiply(halfA);

                Rebar shpilka = Rebar.CreateFromRebarShape(doc, shpilkaShape, shpilkaBarType, host, origin, acrossDir, wallDir);
                if (shpilka == null)
                    throw new InvalidOperationException($"Не удалось создать шпильку по форме «{shpilkaShape.Name}».");

                SetShapeParamDirect(shpilka, ShpilkaParamA, aFt);
                doc.Regenerate(); // как у хомута — без Regenerate высота массива уезжает

                // Массив СНАЧАЛА, поправка позиции — ПОСЛЕ: формульная компенсация "усыхания к
                // дальнему углу" (как у хомута, через ShpilkaDefaultAMm), применённая ДО
                // SetLayoutAsNumberWithSpacing, при построении массива частично "терялась"
                // (подтверждено диагностикой: после массива поправка по Z почти не действовала —
                // смещение верха BBox оставалось ≈350мм что до, что после неё). Поэтому сначала
                // строим массив как есть, а затем ОДНИМ финальным сдвигом (по уже готовому,
                // с массивом, элементу) выравниваем и X;Y (середина пары), и Z (верх BBox — на
                // topZ, крючки формы уходят от origin ВВЕРХ, а не вниз, как у хомута).
                if (vCount > 1)
                    shpilka.GetShapeDrivenAccessor().SetLayoutAsNumberWithSpacing(vCount, stepZFt, true, true, true);
                doc.Regenerate();

                BoundingBoxXYZ shpilkaBb = shpilka.get_BoundingBox(null);
                if (shpilkaBb != null)
                {
                    XYZ bbCenter = (shpilkaBb.Min + shpilkaBb.Max) * 0.5;
                    XYZ residual = new XYZ(c.X - bbCenter.X, c.Y - bbCenter.Y, topZ - shpilkaBb.Max.Z);
                    if (!residual.IsZeroLength())
                    {
                        ElementTransformUtils.MoveElement(doc, shpilka.Id, residual);
                        doc.Regenerate();
                    }
                }

                SetIfExists(shpilka, MarkParamName, StarterMarkValue);
                ids.Add(shpilka.Id);
            }

            return ids;
        }

        private static void SetIfExists(Element e, string paramName, string value)
        {
            Parameter p = e.LookupParameter(paramName);
            if (p != null && !p.IsReadOnly && p.StorageType == StorageType.String) p.Set(value);
        }

        private static void SetShapeParamDirect(Rebar r, string paramName, double valueFt)
        {
            Parameter p = r.LookupParameter(paramName);
            if (p != null && !p.IsReadOnly) p.Set(valueFt);
        }

        internal static double SelectPolkaLen(int diaMm) =>
            diaMm < DiaThresholdMm ? PolkaLenSmallMm : PolkaLenLargeMm;

        internal double SelectNahlest(string concreteClass, int diaMm)
        {
            if (!NahlestByClassAndDiaMm.TryGetValue(concreteClass, out var table))
                throw new InvalidOperationException($"Нет таблицы нахлёстов для класса бетона \"{concreteClass}\".");
            if (table.TryGetValue(diaMm, out var v))
                return v;
            throw new InvalidOperationException($"Нет нахлёста для диаметра {diaMm} мм (класс бетона {concreteClass}).");
        }

        // Сдвиг между нижним и верхним шахматными ярусами основной арматуры (см.
        // StaggerOffsetByClassAndDiaMm/ClassifyMainTierZs) — диаметр > StaggerOffsetLargeDiaThresholdMm
        // не зависит от класса бетона, класс бетона стены тогда не запрашиваем вовсе (чтобы не
        // падать с ошибкой на неподдержанном классе бетона там, где он для этого расчёта и не нужен).
        internal double SelectStaggerOffset(Document doc, Wall wall, int diaMm)
        {
            if (diaMm > StaggerOffsetLargeDiaThresholdMm)
                return StaggerOffsetLargeDiaMm;

            string concreteClass = GetConcreteClass(doc, wall);
            if (!StaggerOffsetByClassAndDiaMm.TryGetValue(concreteClass, out var table))
                throw new InvalidOperationException($"Нет таблицы сдвига ярусов для класса бетона \"{concreteClass}\".");
            if (table.TryGetValue(diaMm, out var v))
                return v;
            throw new InvalidOperationException($"Нет сдвига яруса для диаметра {diaMm} мм (класс бетона {concreteClass}).");
        }

        // Из уникальных (сгруппированных с допуском 5мм) отметок BottomZ одного диаметра арматуры
        // одной стены отбирает те, что относятся к ОСНОВНОЙ арматуре — по физическому смыслу, а
        // не по порядковому месту в списке (старая логика "2 самые нижние уникальные" ошибочно
        // засчитывала дополнительную арматуру за основную, если основной арматуры этого диаметра
        // в стене нет вовсе). Основная арматура стоит в шахматном порядке из двух ярусов: "нижний"
        // — единственная отметка в пределах LowerTierSearchBandMm от верха фундамента; "верхний" —
        // ближайшая к (нижний + сдвиг яруса, см. SelectStaggerOffset) отметка в пределах допуска
        // TierMatchToleranceMm (если в допуск попало несколько — берём ближайшую, а не все сразу).
        // Если нижнего яруса не нашлось — у этого диаметра в этой стене основной арматуры нет,
        // возвращаем пустой список (все наборы этого диаметра — дополнительные).
        internal List<double> ClassifyMainTierZs(Document doc, Wall wall, int diaMm, List<double> uniqueZsFt, double foundTopZ)
        {
            double tolFt = MmToFt(5.0);
            double lowerBandMinFt = foundTopZ - tolFt;
            double lowerBandMaxFt = foundTopZ + MmToFt(LowerTierSearchBandMm) + tolFt;

            var lowerCandidates = uniqueZsFt.Where(z => z >= lowerBandMinFt && z <= lowerBandMaxFt).ToList();
            if (lowerCandidates.Count == 0)
                return new List<double>();

            double lowerZ = lowerCandidates.Min();
            var keepZs = new List<double> { lowerZ };

            double staggerMm = SelectStaggerOffset(doc, wall, diaMm);
            double expectedUpperZ = lowerZ + MmToFt(staggerMm);
            double matchToleranceFt = MmToFt(TierMatchToleranceMm);

            double? bestUpperZ = null;
            double bestDiff = double.MaxValue;
            foreach (double z in uniqueZsFt)
            {
                if (Math.Abs(z - lowerZ) <= tolFt) continue; // сам нижний ярус, уже учтён
                double diff = Math.Abs(z - expectedUpperZ);
                if (diff <= matchToleranceFt && diff < bestDiff)
                {
                    bestDiff = diff;
                    bestUpperZ = z;
                }
            }
            if (bestUpperZ.HasValue)
                keepZs.Add(bestUpperZ.Value);

            return keepZs;
        }

        private const string StructuralMaterialParamName = "Материал несущих конструкций";

        // Класс бетона стены из параметра "Материал несущих конструкций". Имя материала
        // содержит код класса (см. ConcreteClassPattern) — если не нашли/класс не поддержан
        // таблицей NahlestByClassAndDiaMm, кидаем понятную ошибку, а не тихо считаем наугад.
        //
        // У стены этот параметр может оказаться и на экземпляре, и на типе (WallType) — по
        // умолчанию Revit его показывает как параметр типа для базовых стен; на практике же он
        // бывает и переопределён на экземпляре. Пробуем последовательно: BuiltInParameter на
        // экземпляре → BuiltInParameter на типе → по имени на экземпляре → по имени на типе.
        internal static string GetConcreteClass(Document doc, Wall wall)
        {
            Element wallType = doc.GetElement(wall.GetTypeId());

            Material mat =
                GetMaterialParam(doc, wall)
                ?? GetMaterialParam(doc, wallType)
                ?? GetMaterialParamByName(doc, wall)
                ?? GetMaterialParamByName(doc, wallType);

            string materialName = mat?.Name;
            if (string.IsNullOrEmpty(materialName))
                throw new InvalidOperationException(
                    $"У стены не задан материал в параметре \"{StructuralMaterialParamName}\" " +
                    "(ни на экземпляре, ни на типе).");

            Match m = ConcreteClassPattern.Match(materialName);
            if (!m.Success)
                throw new InvalidOperationException(
                    $"Не удалось определить класс бетона из материала \"{materialName}\".");

            string concreteClass = $"C{m.Groups[1].Value}/{m.Groups[2].Value}";
            if (!NahlestByClassAndDiaMm.ContainsKey(concreteClass))
                throw new InvalidOperationException(
                    $"Класс бетона \"{concreteClass}\" (материал \"{materialName}\") не поддержан — " +
                    $"ожидается один из: {string.Join(", ", NahlestByClassAndDiaMm.Keys)}.");

            return concreteClass;
        }

        private static Material GetMaterialParam(Document doc, Element elem)
        {
            Parameter p = elem?.get_Parameter(BuiltInParameter.STRUCTURAL_MATERIAL_PARAM);
            return MaterialFromParam(doc, p);
        }

        private static Material GetMaterialParamByName(Document doc, Element elem)
        {
            Parameter p = elem?.LookupParameter(StructuralMaterialParamName);
            return MaterialFromParam(doc, p);
        }

        private static Material MaterialFromParam(Document doc, Parameter p)
        {
            if (p == null || p.StorageType != StorageType.ElementId) return null;
            ElementId matId = p.AsElementId();
            if (matId == null || matId == ElementId.InvalidElementId) return null;
            return doc.GetElement(matId) as Material;
        }

        // Сопоставление "какой параметр — какая роль" по близости величин (сортировка текущих
        // авто-вычисленных значений и целевых по возрастанию, затем попарно), а не по имени —
        // см. RebarBuilder.FixShapeParams для той же формы «(форма)11». Параметр, вычисляемый
        // формулой из геометрии кривых, может оказаться read-only — тогда просто пропускаем его.
        private static void FixShapeParams(Rebar rebar, string[] paramNames, double[] targetFt)
        {
            Parameter[] pars = paramNames.Select(n => rebar.LookupParameter(n)).ToArray();
            var order = Enumerable.Range(0, pars.Length)
                .Where(i => pars[i] != null)
                .OrderBy(i => pars[i].AsDouble())
                .ToList();
            var targetOrder = targetFt.OrderBy(v => v).ToList();
            for (int k = 0; k < order.Count && k < targetOrder.Count; k++)
            {
                Parameter p = pars[order[k]];
                if (p.IsReadOnly) continue;
                p.Set(targetOrder[k]);
            }
        }

        // ---- Утилиты ---------------------------------------------------------

        internal static double MmToFt(double mm) =>
            UnitUtils.ConvertToInternalUnits(mm, UnitTypeId.Millimeters);

        internal static double FtToMm(double ft) =>
            UnitUtils.ConvertFromInternalUnits(ft, UnitTypeId.Millimeters);

        internal class SetInfo
        {
            public bool IsExterior;
            public double BottomZ;
        }
    }

    // ---- Фильтры выбора ------------------------------------------------------

    internal class AssemblyFilter : ISelectionFilter
    {
        public bool AllowElement(Element e) => e is AssemblyInstance;
        public bool AllowReference(Reference r, XYZ p) => false;
    }

    internal class CategoryFilter : ISelectionFilter
    {
        private readonly BuiltInCategory _cat;
        public CategoryFilter(BuiltInCategory cat) => _cat = cat;
        public bool AllowElement(Element e) =>
            e.Category != null && e.Category.Id == new ElementId(_cat);
        public bool AllowReference(Reference r, XYZ p) => false;
    }
}
