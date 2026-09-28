using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

namespace DAN_Plugin
{
    // ─────────────────────────────────────────────────────────────────────────
    // Диагностика FoundationStarterCommand: почему выпуски арматуры в фундамент "улетают"
    // далеко от стены и получаются одинаковыми (см. FoundationStarterCommand.cs). Ничего не
    // создаёт и не меняет в модели (ReadOnly-транзакция) — считает ТЕ ЖЕ величины теми же
    // методами, что и основная команда (переиспользует её internal-члены), и печатает
    // промежуточные результаты, чтобы найти шаг, на котором координаты/классификация уезжают:
    // сырые позиции стержней стены, точку привязки проекта (частая причина "улетевших"
    // координат), стены-члены сборки, классификацию граней/высоты, итоговую геометрию выпуска.
    // Без кнопки на ленте — запуск через Add-In Manager, как и другие диагностические команды
    // модуля (см. Debug.cs).
    // ─────────────────────────────────────────────────────────────────────────
    [Transaction(TransactionMode.Manual)]
    public class FoundationStarterDiagnostics : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            UIDocument uidoc = data.Application.ActiveUIDocument;
            Document doc = uidoc.Document;
            var cmd = new FoundationStarterCommand();
            var sb = new StringBuilder();
            var geomCache = new Dictionary<ElementId, List<Solid>>();

            double MmX(double ft) => UnitUtils.ConvertFromInternalUnits(ft, UnitTypeId.Millimeters);
            string P(XYZ p) => p == null ? "null" : $"({MmX(p.X):0};{MmX(p.Y):0};{MmX(p.Z):0})";

            AssemblyInstance assembly;
            try
            {
                Reference r = uidoc.Selection.PickObject(
                    ObjectType.Element, new AssemblyFilter(), "Выберите сборку стены (диагностика)");
                assembly = doc.GetElement(r) as AssemblyInstance;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }
            if (assembly == null) { message = "Выбранный элемент не сборка."; return Result.Failed; }

            string mark = assembly.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString();
            sb.AppendLine($"Сборка: Id={assembly.Id}  марка(Комментарии)=\"{mark}\"");

            Element foundation;
            try
            {
                Reference r = uidoc.Selection.PickObject(
                    ObjectType.Element, new CategoryFilter(BuiltInCategory.OST_StructuralFoundation),
                    "Выберите плиту фундамента (диагностика)");
                foundation = doc.GetElement(r);
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }
            if (foundation == null) { message = "Не найдена плита фундамента."; return Result.Failed; }

            BoundingBoxXYZ fbb = foundation.get_BoundingBox(null);
            double foundTopZ = fbb.Max.Z, foundBotZ = fbb.Min.Z;
            sb.AppendLine($"Фундамент: Id={foundation.Id} Category={foundation.Category?.Name}");
            sb.AppendLine($"  BBox Min={P(fbb.Min)} Max={P(fbb.Max)} мм (абсолютные внутренние координаты)");
            sb.AppendLine();

            List<RebarBarType> allBarTypes = new FilteredElementCollector(doc)
                .OfClass(typeof(RebarBarType)).Cast<RebarBarType>().ToList();

            // Точка привязки проекта / геодезии — частая причина "улетевших" на сотни метров
            // координат (смешение internal и shared coordinates).
            ProjectLocation pl = doc.ActiveProjectLocation;
            Transform plTransform = pl.GetTotalTransform();
            sb.AppendLine($"ActiveProjectLocation \"{pl.Name}\": internal→shared смещение Origin={P(plTransform.Origin)}");
            Element basePoint = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_ProjectBasePoint).FirstOrDefault();
            Element surveyPoint = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_SharedBasePoint).FirstOrDefault();
            XYZ bp = (basePoint?.Location as LocationPoint)?.Point;
            XYZ sp = (surveyPoint?.Location as LocationPoint)?.Point;
            sb.AppendLine($"Project Base Point (внутр. координаты): {P(bp)}");
            sb.AppendLine($"Survey Point (внутр. координаты): {P(sp)}");
            sb.AppendLine();

            List<ElementId> allWallIds = FoundationStarterCommand.GetMemberWallIds(doc, assembly).ToList();
            HashSet<ElementId> lowestWallIds = new HashSet<ElementId>(
                FoundationStarterCommand.FilterLowestWalls(doc, allWallIds));
            sb.AppendLine($"Стен-членов сборки найдено: {allWallIds.Count} (используется только нижний ярус — {lowestWallIds.Count} шт.)");
            foreach (ElementId id in allWallIds)
            {
                Wall w = doc.GetElement(id) as Wall;
                Curve lc = (w?.Location as LocationCurve)?.Curve;
                double? botZ = w?.get_BoundingBox(null)?.Min.Z;
                bool isLowest = lowestWallIds.Contains(id);
                sb.AppendLine($"  {(isLowest ? "[низ]" : "[выше]")} Стена Id={id} Name=\"{w?.Name}\" низ Z={(botZ.HasValue ? $"{MmX(botZ.Value):0}мм" : "?")}" +
                    $"  ось: {P(lc?.GetEndPoint(0))} -> {P(lc?.GetEndPoint(1))}  Orientation={w?.Orientation}");
            }
            sb.AppendLine();

            List<Rebar> wallRebars = cmd.CollectWallRebar(doc, assembly, mark);
            sb.AppendLine($"Найдено Rebar с {FoundationStarterCommand.MarkParamName}=\"{mark}\" среди стен сборки: {wallRebars.Count}");
            sb.AppendLine();

            var sets = new List<(Rebar Rebar, Wall Wall, XYZ Exterior, FoundationStarterCommand.SetInfo Info, int DiaMm)>();
            foreach (Rebar wallSet in wallRebars)
            {
                Wall wall = cmd.GetHostWall(doc, wallSet);
                sb.AppendLine($"--- Rebar Id={wallSet.Id}  HostId={wallSet.GetHostId()}  (хост-стена найдена: {wall != null}) ---");
                if (wall == null)
                {
                    sb.AppendLine("  !! host wall == null — этот набор был бы пропущен основной командой");
                    continue;
                }

                RebarBarType barType = doc.GetElement(wallSet.GetTypeId()) as RebarBarType;
                int diaMm = (int)Math.Round(FoundationStarterCommand.FtToMm(barType.BarModelDiameter));

                int count = wallSet.NumberOfBarPositions;
                XYZ p0 = FoundationStarterCommand.GetBarBottomPoint(wallSet, 0);
                XYZ pLast = count > 1 ? p0 + FoundationStarterCommand.GetBarPositionOffset(wallSet, 0, count - 1) : p0;
                sb.AppendLine($"  Тип={barType?.Name} диаметр={diaMm}мм  позиций(NumberOfBarPositions)={count}  " +
                    $"p0(index0)={P(p0)}  pLast={P(pLast)}");

                XYZ exterior = wall.Orientation.Normalize();
                FoundationStarterCommand.SetInfo info = cmd.ClassifySet(wallSet, wall, exterior);
                sb.AppendLine($"  Стена: Id={wall.Id}  Orientation(наружу)={exterior}  IsExterior={info.IsExterior}  BottomZ={MmX(info.BottomZ):0}мм");

                sets.Add((wallSet, wall, exterior, info, diaMm));
            }
            sb.AppendLine();

            // Дополнительная арматура (сверх основной, того же диаметра) выпусков не требует.
            // Физический критерий (см. ClassifyMainTierZs в основной команде): "нижний" ярус —
            // отметка BottomZ в пределах LowerTierSearchBandMm от верха фундамента; "верхний" —
            // ближайшая к (нижний + сдвиг яруса по таблице) в пределах допуска
            // TierMatchToleranceMm. Если нижнего яруса не нашлось — у этого диаметра в этой
            // стене основной арматуры нет вовсе, весь набор дополнительный (в отличие от старой
            // логики "2 самые нижние уникальные", которая в этом случае ошибочно принимала
            // дополнительную арматуру за основную).
            sb.AppendLine("=== Фильтр основной/дополнительной арматуры (ClassifyMainTierZs: нижний ярус + сдвиг по таблице) ===");
            var mainSets = new List<(Rebar Rebar, Wall Wall, XYZ Exterior, FoundationStarterCommand.SetInfo Info, int DiaMm)>();
            double filterTolFt = FoundationStarterCommand.MmToFt(5.0);
            foreach (var group in sets.GroupBy(s => (s.Wall.Id, s.DiaMm)))
            {
                var ordered = group.OrderBy(s => s.Info.BottomZ).ToList();
                Wall groupWall = ordered[0].Wall;
                int groupDiaMm = group.Key.Item2;

                var uniqueZs = new List<double>();
                foreach (double z in ordered.Select(s => s.Info.BottomZ))
                    if (uniqueZs.Count == 0 || z - uniqueZs[uniqueZs.Count - 1] > filterTolFt)
                        uniqueZs.Add(z);

                List<double> keepZs = cmd.ClassifyMainTierZs(doc, groupWall, groupDiaMm, uniqueZs, foundTopZ);
                var kept = ordered.Where(s => keepZs.Any(kz => Math.Abs(s.Info.BottomZ - kz) <= filterTolFt)).ToList();
                var dropped = ordered.Except(kept).ToList();

                string tierInfo;
                if (keepZs.Count == 0)
                    tierInfo = "нижний ярус НЕ найден (нет отметки в пределах LowerTierSearchBandMm от верха фундамента) — основной арматуры этого диаметра в стене нет";
                else
                {
                    double staggerMm = cmd.SelectStaggerOffset(doc, groupWall, groupDiaMm);
                    tierInfo = $"нижний ярус={MmX(keepZs[0]):0}мм  ожидаемый верхний={MmX(keepZs[0] + FoundationStarterCommand.MmToFt(staggerMm)):0}мм (сдвиг={staggerMm:0}мм)" +
                        (keepZs.Count > 1 ? $"  найден верхний={MmX(keepZs[1]):0}мм" : "  верхний не найден в допуске");
                }

                sb.AppendLine($"  Стена Id={group.Key.Item1}  диаметр={groupDiaMm}мм: наборов={ordered.Count}  " +
                    $"уникальных отметок={uniqueZs.Count} ({string.Join(", ", uniqueZs.Select(z => $"{MmX(z):0}мм"))})  {tierInfo}");
                sb.AppendLine($"    оставлено(основная)={string.Join(", ", kept.Select(s => $"Id={s.Rebar.Id}(BottomZ={MmX(s.Info.BottomZ):0}мм)"))}" +
                    (dropped.Count > 0
                        ? $"  отброшено(дополнительная)={string.Join(", ", dropped.Select(s => $"Id={s.Rebar.Id}(BottomZ={MmX(s.Info.BottomZ):0}мм)"))}"
                        : ""));
                mainSets.AddRange(kept);
            }
            sets = mainSets;
            sb.AppendLine();

            // Наборы, у которых в точке вставки нет материала стены у самого низа (проём,
            // в т.ч. вырез правкой профиля стены) — выпуск им не нужен, основная команда их
            // отбрасывает. Проверяем реальную геометрию (Solid), а не только вставки/Opening.
            sb.AppendLine($"=== Проверка на материал стены у низа (отрезок {FoundationStarterCommand.OpeningCheckHeightMm:0}мм от верха фундамента={MmX(foundTopZ):0}мм) ===");
            var withoutOpenings = new List<(Rebar Rebar, Wall Wall, XYZ Exterior, FoundationStarterCommand.SetInfo Info, int DiaMm)>();
            double checkHeightFt = FoundationStarterCommand.MmToFt(FoundationStarterCommand.OpeningCheckHeightMm);
            foreach (var s in sets)
            {
                XYZ xy = FoundationStarterCommand.GetBarBottomPoint(s.Rebar, 0);
                bool hasSolid = FoundationStarterCommand.HasSolidAt(s.Wall, xy, foundTopZ, checkHeightFt, geomCache);
                sb.AppendLine($"  Rebar Id={s.Rebar.Id}  точка={P(xy)}  " +
                    (hasSolid ? "→ материал есть, выпуск строится" : "→ ПУСТО (проём), выпуск пропускается"));
                if (hasSolid) withoutOpenings.Add(s);
            }
            sets = withoutOpenings;
            sb.AppendLine();

            sb.AppendLine("=== Геометрия выпуска (расчёт как в BuildStarterSet, без создания в модели) ===");
            double coverFt = FoundationStarterCommand.MmToFt(FoundationStarterCommand.DowelBottomOffsetDefaultMm);
            // Группируем не только по стене, но и по "семейству" арматуры — шагу массива
            // (округлён до мм). На одной стене может быть несколько независимых семейств
            // стержней с разным шагом (напр. обычная зона с общим шагом 200мм и локальная
            // более частая зона с общим шагом 100мм) — см. familiesByWall/BuildTiesForWall
            // в основной команде; тут та же группировка, чтобы диагностика видела ОБА
            // семейства, а не только первое попавшееся.
            var familiesByWall = new Dictionary<ElementId, Dictionary<int, (double SpacingFt, double MinProjFt, double MaxProjFt, XYZ WallDir, int MaxDiaMm)>>();
            // Для хомутов нужен не "средний" шаг семейства, а РЕАЛЬНОЕ расстояние до конкретной
            // соседней пары — см. wallPairProjections/BuildTiesForWall в основной команде; тут
            // та же коллекция для диагностики.
            var wallPairProjections = new Dictionary<ElementId, List<double>>();
            foreach (var s in sets)
            {
                RebarBarType barType = doc.GetElement(s.Rebar.GetTypeId()) as RebarBarType;
                int diaMm = (int)Math.Round(FoundationStarterCommand.FtToMm(barType.BarModelDiameter));

                string starterTypeName = FoundationStarterCommand.StarterBarTypeName(diaMm);
                RebarBarType starterBarType = allBarTypes.FirstOrDefault(bt => bt.Name == starterTypeName);
                sb.AppendLine(starterBarType != null
                    ? $"Типоразмер выпуска \"{starterTypeName}\" найден: Id={starterBarType.Id}"
                    : $"⚠ Типоразмер выпуска \"{starterTypeName}\" НЕ НАЙДЕН — основная команда упадёт на этом наборе");

                double polkaLen = FoundationStarterCommand.MmToFt(FoundationStarterCommand.SelectPolkaLen(diaMm));
                double polkaZ = foundBotZ + coverFt;

                double vertLen;
                double nahlestMm = double.NaN;
                if (diaMm >= FoundationStarterCommand.DiaThresholdMm)
                {
                    // Крупный диаметр — сварка, не нахлёст (см. WeldGapMm в BuildStarterSet).
                    vertLen = (s.Info.BottomZ - polkaZ) - FoundationStarterCommand.MmToFt(FoundationStarterCommand.WeldGapMm);
                    sb.AppendLine($"Диаметр {diaMm}мм >= порога сварки — класс бетона/нахлёст не требуются, зазор под сварку {FoundationStarterCommand.WeldGapMm:0}мм");
                }
                else
                {
                    string concreteClass;
                    try
                    {
                        concreteClass = FoundationStarterCommand.GetConcreteClass(doc, s.Wall);
                        sb.AppendLine($"Класс бетона стены: {concreteClass}");
                    }
                    catch (Exception ex)
                    {
                        sb.AppendLine($"⚠ Класс бетона стены не определён: {ex.Message} — основная команда упадёт на этом наборе");
                        sb.AppendLine();
                        continue;
                    }

                    nahlestMm = cmd.SelectNahlest(concreteClass, diaMm);
                    double nahlestFt = FoundationStarterCommand.MmToFt(nahlestMm);
                    // Вертикаль — от полки до факта низа основной арматуры стены (info.BottomZ),
                    // а не до верха фундамента (см. тот же комментарий в BuildStarterSet).
                    vertLen = (s.Info.BottomZ - polkaZ) + nahlestFt;
                }

                // first — через GetBarBottomPoint (точная ось, тот же метод, что и в
                // ClassifySet), last/шаг — через GetBarPositionOffset (см. её комментарий: не
                // зависит от активного вида, в отличие от прежней кластеризации геометрии).
                int count = s.Rebar.NumberOfBarPositions;
                XYZ first = FoundationStarterCommand.GetBarBottomPoint(s.Rebar, 0);
                XYZ last = count > 1 ? first + FoundationStarterCommand.GetBarPositionOffset(s.Rebar, 0, count - 1) : first;
                double spacing = count > 1 ? first.DistanceTo(last) / (count - 1) : 0.0;
                Curve wallCurve = (s.Wall.Location as LocationCurve)?.Curve;
                XYZ wallDir = wallCurve != null
                    ? (wallCurve.GetEndPoint(1) - wallCurve.GetEndPoint(0)).Normalize()
                    : XYZ.BasisZ.CrossProduct(s.Exterior).Normalize();
                double minProj = Math.Min(first.DotProduct(wallDir), last.DotProduct(wallDir));
                double maxProj = Math.Max(first.DotProduct(wallDir), last.DotProduct(wallDir));

                XYZ polkaDir = s.Info.IsExterior ? s.Exterior : s.Exterior.Negate();

                XYZ bendPt = new XYZ(first.X, first.Y, polkaZ);
                XYZ topPt = bendPt + XYZ.BasisZ.Multiply(vertLen);

                double edgeCheckHalfFt = FoundationStarterCommand.MmToFt(50.0);
                XYZ footEndPt = bendPt + polkaDir.Multiply(polkaLen);
                bool footInsideFoundation = FoundationStarterCommand.HasSolidAt(
                    foundation, footEndPt, polkaZ - edgeCheckHalfFt, edgeCheckHalfFt * 2, geomCache);
                bool flipped = false;
                if (!footInsideFoundation)
                {
                    polkaDir = polkaDir.Negate();
                    footEndPt = bendPt + polkaDir.Multiply(polkaLen);
                    flipped = true;
                }

                double distToWallAxis = wallCurve != null ? MmX(wallCurve.Project(bendPt).Distance) : double.NaN;
                double distToOrigin = MmX(bendPt.DistanceTo(XYZ.Zero));
                double distToFoundCenter = MmX(bendPt.DistanceTo((fbb.Min + fbb.Max) * 0.5));

                sb.AppendLine($"Rebar Id={s.Rebar.Id}  IsExterior={s.Info.IsExterior}  dia={diaMm}мм  " +
                    (double.IsNaN(nahlestMm) ? "сварка (без нахлёста)" : $"нахлёст={nahlestMm:0}мм") +
                    $"  BottomZ(факт)={MmX(s.Info.BottomZ):0}мм  верх фундамента={MmX(foundTopZ):0}мм");
                sb.AppendLine($"  Позиций(NumberOfBarPositions)={count}  шаг(spacing)={MmX(spacing):0}мм");
                sb.AppendLine($"  first={P(first)}  last={P(last)}");
                sb.AppendLine($"  polkaLen={MmX(polkaLen):0}мм  polkaZ={MmX(polkaZ):0}мм  vertLen={MmX(vertLen):0}мм");
                sb.AppendLine($"  bendPt={P(bendPt)}  topPt={P(topPt)}  footEndPt={P(footEndPt)}" +
                    (flipped ? "  ⚠ полка развёрнута внутрь (у края фундамента)" : ""));
                sb.AppendLine($"  Расстояние bendPt→ось своей стены: {distToWallAxis:0}мм  |  bendPt→(0;0;0): {distToOrigin:0}мм  |  bendPt→центр фундамента: {distToFoundCenter:0}мм");
                sb.AppendLine();

                if (!familiesByWall.TryGetValue(s.Wall.Id, out var wallFamilies))
                {
                    wallFamilies = new Dictionary<int, (double, double, double, XYZ, int)>();
                    familiesByWall[s.Wall.Id] = wallFamilies;
                }
                int familyKey = (int)Math.Round(MmX(spacing));
                if (!wallFamilies.TryGetValue(familyKey, out var existingFamily))
                    wallFamilies[familyKey] = (spacing, minProj, maxProj, wallDir, diaMm);
                else
                    wallFamilies[familyKey] = (
                        existingFamily.Item1,
                        Math.Min(existingFamily.Item2, minProj),
                        Math.Max(existingFamily.Item3, maxProj),
                        existingFamily.Item4,
                        Math.Max(existingFamily.Item5, diaMm));

                if (!wallPairProjections.TryGetValue(s.Wall.Id, out var projList))
                {
                    projList = new List<double>();
                    wallPairProjections[s.Wall.Id] = projList;
                }
                projList.AddRange(FoundationStarterCommand.GetAllBarProjections(s.Rebar, wallDir));
            }

            double pairDedupTolFt = FoundationStarterCommand.MmToFt(5.0);
            foreach (ElementId wallId in wallPairProjections.Keys.ToList())
            {
                var sortedProj = wallPairProjections[wallId].OrderBy(p => p).ToList();
                var dedupedProj = new List<double>();
                foreach (double p in sortedProj)
                    if (dedupedProj.Count == 0 || p - dedupedProj[dedupedProj.Count - 1] > pairDedupTolFt)
                        dedupedProj.Add(p);
                wallPairProjections[wallId] = dedupedProj;
            }

            // === Хомуты (предпросмотр расчёта, без создания в модели) ===
            sb.AppendLine("=== Хомуты вокруг выпусков (предпросмотр, без создания в модели) ===");
            RebarShape tieShape = new FilteredElementCollector(doc)
                .OfClass(typeof(RebarShape)).Cast<RebarShape>()
                .FirstOrDefault(s => s.Name == FoundationStarterCommand.TieShapeName);
            sb.AppendLine(tieShape != null
                ? $"Форма хомута \"{FoundationStarterCommand.TieShapeName}\" найдена: Id={tieShape.Id}"
                : $"⚠ Форма хомута \"{FoundationStarterCommand.TieShapeName}\" НЕ НАЙДЕНА");
            // Диаметр хомута теперь выбирает пользователь (см. ReinforcementModePrompt) — для
            // предпросмотра берём весь список доступных диаметров (класс А240) и тестируем на
            // первом (наименьшем) найденном.
            var tieBarTypePattern = new System.Text.RegularExpressions.Regex(
                @"^\(арматура\)детали_d=(\d+)_А240$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var tieBarTypeOptions = allBarTypes
                .Select(bt => (BarType: bt, Match: tieBarTypePattern.Match(bt.Name)))
                .Where(x => x.Match.Success)
                .Select(x => (x.BarType, DiaMm: int.Parse(x.Match.Groups[1].Value)))
                .OrderBy(x => x.DiaMm)
                .ToList();
            sb.AppendLine(tieBarTypeOptions.Count > 0
                ? $"Типоразмеров хомута \"(арматура)детали_d=…_А240\" найдено: {tieBarTypeOptions.Count} " +
                  $"({string.Join(", ", tieBarTypeOptions.Select(x => $"d{x.DiaMm}"))})"
                : "⚠ Не найдено ни одного типоразмера \"(арматура)детали_d=…_А240\" — хомуты недоступны");
            RebarBarType tieBarType = tieBarTypeOptions.FirstOrDefault().BarType;
            if (tieShape != null)
                sb.AppendLine($"RebarStyle формы хомута: {tieShape.RebarStyle}");
            sb.AppendLine();

            // === Проверка подтверждённого способа: CreateFromRebarShape(origin=угол,
            // xVec=поперёк стены, yVec=вдоль стены) + принудительная установка Условный_A/B +
            // компенсация смещения константами умолчаний формы (TieDefaultAMm/TieDefaultBMm —
            // НЕ чтением LookupParameter с только что созданного хомута: при массовой
            // расстановке Revit подставляет в новый экземпляр последнее использованное в
            // сеансе значение, а не чистое умолчание семейства — начиная со 2-го хомута это
            // ломало компенсацию). Создаём НЕСКОЛЬКО хомутов подряд (как в реальном цикле
            // BuildTiesForWall), чтобы поймать именно эту накопительную проблему, а не только
            // одиночный случай. Транзакция открывается и сразу откатывается — модель не меняется.
            if (tieShape != null && tieBarType != null && foundation != null && wallPairProjections.Count > 0)
            {
                using (Transaction expTx = new Transaction(doc, "Диагностика хомута (тест, не сохраняется)"))
                {
                    expTx.Start();
                    sb.AppendLine("=== Проверка BuildTiesForWall на нескольких хомутах подряд (первая стена, по реальным позициям пар) ===");
                    var firstWallProj = wallPairProjections.First();
                    ElementId expWallId = firstWallProj.Key;
                    Wall expWall = doc.GetElement(expWallId) as Wall;
                    List<double> pairProjections = firstWallProj.Value;
                    var wallFamilies2 = familiesByWall.TryGetValue(expWallId, out var fams2)
                        ? fams2.Values.ToList() : new List<(double, double, double, XYZ, int)>();
                    int wallMaxDiaMm = wallFamilies2.Count > 0 ? wallFamilies2.Max(f => f.Item5) : 0;
                    XYZ wallDir2 = wallFamilies2.Count > 0 ? wallFamilies2[0].Item4 : null;

                    sb.AppendLine($"  Стена Id={expWallId}: реальных позиций пар={pairProjections.Count}  диаметр выпуска(макс.)={wallMaxDiaMm}мм");

                    Curve expWallCurve = (expWall?.Location as LocationCurve)?.Curve;
                    if (expWall == null || expWallCurve == null || wallDir2 == null || pairProjections.Count < 2)
                    {
                        sb.AppendLine("    Пропущено: нет стены, оси стены или меньше 2 реальных пар.");
                    }
                    else
                    {
                        double bFtRawMm = FoundationStarterCommand.FtToMm(expWall.Width) - 2 * FoundationStarterCommand.TieCoverMm + wallMaxDiaMm;
                        double bFt = FoundationStarterCommand.MmToFt(Math.Ceiling(bFtRawMm / 5.0) * 5.0);
                        double topZ = foundTopZ - FoundationStarterCommand.MmToFt(FoundationStarterCommand.TieTopOffsetMm);
                        double halfB = bFt / 2.0;
                        XYZ acrossDir = expWall.Orientation.Normalize();
                        XYZ wallStart2 = expWallCurve.GetEndPoint(0);
                        double startProj2 = wallStart2.DotProduct(wallDir2);
                        double defaultAFt = FoundationStarterCommand.MmToFt(FoundationStarterCommand.TieDefaultAMm);
                        double defaultBFt = FoundationStarterCommand.MmToFt(FoundationStarterCommand.TieDefaultBMm);

                        // Группируем ЖАДНО, как в BuildTiesForWall.GroupTiePairPositions: пока
                        // пролёт от первой позиции группы не превышает 200мм, добавляем
                        // следующие позиции подряд (для частых зон типа 100мм получаются группы
                        // по 3 и больше позиций, а не строго по 2). Тестируем первые несколько
                        // групп И последнюю (см. комментарий ниже про "прилипание"). Реальные
                        // позиции и так лежат внутри стены — отдельная проверка "не вышел ли
                        // хомут за физический конец стены" не нужна.
                        var allGroups = FoundationStarterCommand.GroupTiePairPositions(pairProjections);
                        var groupIndices = new List<int>();
                        for (int g = 0; g < Math.Min(allGroups.Count, 3); g++) groupIndices.Add(g);
                        if (allGroups.Count - 1 >= 0 && !groupIndices.Contains(allGroups.Count - 1)) groupIndices.Add(allGroups.Count - 1);

                        foreach (int g in groupIndices)
                        {
                            double p1 = allGroups[g].P1;
                            double p2 = allGroups[g].P2;
                            double aFt = (p2 - p1) + FoundationStarterCommand.MmToFt(FoundationStarterCommand.TieAExtraMm);
                            double halfA = aFt / 2.0;
                            double midProj = (p1 + p2) / 2.0;
                            XYZ centerRaw = wallStart2 + wallDir2.Multiply(midProj - startProj2);
                            XYZ center = new XYZ(centerRaw.X, centerRaw.Y, topZ);
                            XYZ correction = acrossDir.Multiply(bFt - defaultBFt) + wallDir2.Multiply(aFt - defaultAFt);
                            XYZ origin = center - wallDir2.Multiply(halfA) - acrossDir.Multiply(halfB);

                            sb.AppendLine($"  Группа g={g}: p1={MmX(p1):0}мм  p2={MmX(p2):0}мм  пролёт={MmX(p2 - p1):0}мм  " +
                                $"Условный_A(целевое)={MmX(aFt):0}мм");

                            try
                            {
                                Rebar tie = Rebar.CreateFromRebarShape(doc, tieShape, tieBarType, foundation, origin, acrossDir, wallDir2);
                                if (tie == null)
                                {
                                    sb.AppendLine("    [FAIL] CreateFromRebarShape вернул null");
                                    continue;
                                }

                                Parameter pA = tie.LookupParameter("Условный_A");
                                Parameter pB = tie.LookupParameter("Условный_B");
                                if (pA != null && !pA.IsReadOnly) pA.Set(aFt);
                                if (pB != null && !pB.IsReadOnly) pB.Set(bFt);
                                if (!correction.IsZeroLength())
                                    ElementTransformUtils.MoveElement(doc, tie.Id, correction);
                                doc.Regenerate();

                                BoundingBoxXYZ bb = tie.get_BoundingBox(null);
                                XYZ bbCenter = bb != null ? (bb.Min + bb.Max) * 0.5 : null;
                                double offMm = bbCenter != null ? MmX(bbCenter.DistanceTo(center)) : double.NaN;

                                sb.AppendLine($"    [OK] Id={tie.Id}  center(расчётный)={P(center)}  BBox центр={P(bbCenter)}  " +
                                    $"расстояние={offMm:0}мм" + (offMm > 50 ? "  ⚠ смещено" : "  ок"));

                                // Диагностика выше не вызывала SetLayoutAsNumberWithSpacing — а в
                                // реальном BuildTiesForWall он вызывается сразу после создания.
                                // Проверяем НА КАЖДОЙ группе (не только на первой) — та же
                                // история уже была с Условный_A/B: изолированная первая группа
                                // проходила идеально, а при последовательном создании нескольких
                                // хомутов подряд второй и далее "прилипало" что-то от предыдущего.
                                double dowelBottomZ2 = foundBotZ + coverFt;
                                double stepZFt2 = FoundationStarterCommand.MmToFt(FoundationStarterCommand.TieVerticalStepMm);
                                int vCount2 = (int)Math.Floor((topZ - dowelBottomZ2) / stepZFt2) + 1;
                                if (vCount2 > 1)
                                    tie.GetShapeDrivenAccessor().SetLayoutAsNumberWithSpacing(vCount2, stepZFt2, true, true, true);
                                doc.Regenerate();

                                BoundingBoxXYZ bbArr = tie.get_BoundingBox(null);
                                double topGapMm = bbArr != null ? MmX(foundTopZ - bbArr.Max.Z) : double.NaN;
                                double botGapMm = bbArr != null ? MmX(bbArr.Min.Z - foundBotZ) : double.NaN;
                                sb.AppendLine($"         После SetLayoutAsNumberWithSpacing(vCount={vCount2}, шаг={FoundationStarterCommand.TieVerticalStepMm:0}мм):" +
                                    $"  Массив BBox: Min={P(bbArr?.Min)} Max={P(bbArr?.Max)}");
                                sb.AppendLine($"         Отступ верх хомута ← верх фундамента: {topGapMm:0}мм (ожидали {FoundationStarterCommand.TieTopOffsetMm:0}мм)" +
                                    (Math.Abs(topGapMm - FoundationStarterCommand.TieTopOffsetMm) > 1 ? "  ⚠" : "  ок") +
                                    $"  |  низ хомута ← низ фундамента: {botGapMm:0}мм");
                            }
                            catch (Exception ex)
                            {
                                sb.AppendLine($"    [FAIL] {ex.GetType().Name}: {ex.Message}");
                            }
                        }
                    }
                    sb.AppendLine();
                    expTx.RollBack();
                }
            }

            // Сводка по хомутам — по КАЖДОЙ группе реальных позиций (0&1, 2&3, ...) на каждой
            // стене, а не по "семействам" — на стыке зон с разным шагом видно фактический
            // (а не усреднённый) зазор для каждой конкретной пары хомутов.
            foreach (var wallProjKv in wallPairProjections)
            {
                ElementId wallId = wallProjKv.Key;
                List<double> pairProjections = wallProjKv.Value;
                Wall wall = doc.GetElement(wallId) as Wall;
                var wallFamiliesSummary = familiesByWall.TryGetValue(wallId, out var famsSummary)
                    ? famsSummary.Values.ToList() : new List<(double, double, double, XYZ, int)>();
                int wallMaxDiaMmSummary = wallFamiliesSummary.Count > 0 ? wallFamiliesSummary.Max(f => f.Item5) : 0;
                XYZ wallDirSummary = wallFamiliesSummary.Count > 0 ? wallFamiliesSummary[0].Item4 : null;

                double bFtRawMmSummary = FoundationStarterCommand.FtToMm(wall?.Width ?? 0) - 2 * FoundationStarterCommand.TieCoverMm + wallMaxDiaMmSummary;
                double bFt = FoundationStarterCommand.MmToFt(Math.Ceiling(bFtRawMmSummary / 5.0) * 5.0);
                double topZ = foundTopZ - FoundationStarterCommand.MmToFt(FoundationStarterCommand.TieTopOffsetMm);
                double stepZFt = FoundationStarterCommand.MmToFt(FoundationStarterCommand.TieVerticalStepMm);
                double dowelBottomZ = foundBotZ + coverFt;
                int vCount = (int)Math.Floor((topZ - dowelBottomZ) / stepZFt) + 1;

                sb.AppendLine($"Стена Id={wallId}: толщина={MmX(wall?.Width ?? 0):0}мм  диаметр выпуска(макс.)={wallMaxDiaMmSummary}мм  " +
                    $"реальных позиций пар={pairProjections.Count}  Условный_B={MmX(bFt):0}мм" +
                    (bFt <= 0 ? "  ⚠ B<=0, толщина стены меньше 2×защитного слоя хомута!" : ""));
                sb.AppendLine($"  По высоте: первый на {MmX(topZ):0}мм, шаг {FoundationStarterCommand.TieVerticalStepMm:0}мм, уровней={vCount}" +
                    (vCount < 1 ? "  ⚠ фундамент слишком тонкий" : ""));

                if (wallDirSummary == null || pairProjections.Count < 2)
                {
                    sb.AppendLine("  ⚠ Меньше 2 реальных пар или не удалось определить направление стены — хомуты не строятся.");
                    sb.AppendLine();
                    continue;
                }

                // Жадная группировка (см. GroupTiePairPositions) — для частых зон (100мм и
                // подобных) в группу попадает БОЛЬШЕ 2 позиций (пока пролёт не превышает
                // 200мм), поэтому число групп меньше, чем pairProjections.Count/2.
                var groupsSummary = FoundationStarterCommand.GroupTiePairPositions(pairProjections);
                foreach (var (p1, p2) in groupsSummary)
                {
                    double aFt = (p2 - p1) + FoundationStarterCommand.MmToFt(FoundationStarterCommand.TieAExtraMm);
                    sb.AppendLine($"  p1={MmX(p1):0}мм  p2={MmX(p2):0}мм  пролёт={MmX(p2 - p1):0}мм  Условный_A={MmX(aFt):0}мм");
                }
                sb.AppendLine($"  Хомутов: {groupsSummary.Count}");
                sb.AppendLine();
            }

            // === Горизонтальная арматура (альтернативный режим ReinforcementMode.Horizontal —
            // см. ReinforcementModePrompt/BuildHorizontalBarsForWall). Пробуем реально создать
            // по одному стержню на каждой стороне стены в откатываемой транзакции — именно так
            // раньше (см. блок хомутов выше) ловились реальные геометрические баги, которые
            // одним расчётом на бумаге не видны.
            sb.AppendLine("=== Горизонтальная арматура вокруг выпусков (предпросмотр, без создания в модели) ===");
            var detailBarTypePattern = new System.Text.RegularExpressions.Regex(
                @"^\(арматура\)выпуски_d=(\d+)_А500 п\.м$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var horizontalBarTypes = allBarTypes
                .Select(bt => (BarType: bt, Match: detailBarTypePattern.Match(bt.Name)))
                .Where(x => x.Match.Success)
                .Select(x => (x.BarType, DiaMm: int.Parse(x.Match.Groups[1].Value)))
                .OrderBy(x => x.DiaMm)
                .ToList();
            sb.AppendLine(horizontalBarTypes.Count > 0
                ? $"Типоразмеров \"(арматура)выпуски_d=…_А500 п.м\" найдено: {horizontalBarTypes.Count} " +
                  $"({string.Join(", ", horizontalBarTypes.Select(x => $"d{x.DiaMm}"))})"
                : "⚠ Не найдено ни одного типоразмера \"(арматура)выпуски_d=…_А500 п.м\" — горизонтальная арматура недоступна");

            if (horizontalBarTypes.Count > 0 && familiesByWall.Count > 0)
            {
                RebarBarType hBarType = horizontalBarTypes.First().BarType;
                double testStepMm = FoundationStarterCommand.TieVerticalStepMm; // произвольный шаг для теста — в реальности его вводит пользователь в ReinforcementModePrompt
                sb.AppendLine($"Тестовый типоразмер d{horizontalBarTypes.First().DiaMm}, тестовый шаг={testStepMm:0}мм (реальный шаг задаёт пользователь)");

                using (Transaction expTx2 = new Transaction(doc, "Диагностика горизонтальной арматуры (тест, не сохраняется)"))
                {
                    expTx2.Start();
                    // Горизонтальная арматура — одна пара стержней на всю стену (не на семейство),
                    // отступ от грани — по максимальному диаметру выпуска среди ВСЕХ семейств.
                    foreach (var kv in familiesByWall)
                    {
                        Wall wall = doc.GetElement(kv.Key) as Wall;
                        Curve wallCurve = (wall?.Location as LocationCurve)?.Curve;
                        if (wall == null || wallCurve == null)
                        {
                            sb.AppendLine($"  Стена Id={kv.Key}: ⚠ нет LocationCurve — горизонтальная арматура невозможна");
                            continue;
                        }
                        int dowelDiaMm = kv.Value.Values.Max(f => f.MaxDiaMm);

                        XYZ wallStart = wallCurve.GetEndPoint(0);
                        XYZ wallEnd = wallCurve.GetEndPoint(1);
                        XYZ wallDirRaw = (wallEnd - wallStart).Normalize();

                        double topZ = foundTopZ - FoundationStarterCommand.MmToFt(FoundationStarterCommand.TieTopOffsetMm);
                        double stepFt = FoundationStarterCommand.MmToFt(testStepMm);
                        double dowelBottomZ = foundBotZ + coverFt;
                        int vCount = (int)Math.Floor((topZ - dowelBottomZ) / stepFt) + 1;

                        XYZ acrossDir = wall.Orientation.Normalize();
                        // Отступ = защитный слой минус половина диаметра выпуска — та же
                        // логика, что и в Условный_B хомута (см. тот же расчёт в
                        // BuildHorizontalBarsForWall).
                        double dowelCoverMm = FoundationStarterCommand.TieCoverMm - dowelDiaMm / 2.0;
                        double halfB = wall.Width / 2.0 - FoundationStarterCommand.MmToFt(dowelCoverMm);

                        // Проём режет стержень на отдельные сплошные участки (см.
                        // GetSolidWallSegments/BuildHorizontalBarsForWall) — сканируем всю
                        // стену и режем по границам КАЖДОГО участка, без отступа от краёв.
                        double openingCheckHeightFt = FoundationStarterCommand.MmToFt(FoundationStarterCommand.OpeningCheckHeightMm);
                        double sampleStepFt = FoundationStarterCommand.MmToFt(FoundationStarterCommand.OpeningScanStepMm);
                        var solidSegments = FoundationStarterCommand.GetSolidWallSegments(
                            wall, wallStart, wallEnd, wallDirRaw, foundTopZ, openingCheckHeightFt, sampleStepFt, geomCache);
                        double wallStartProj = wallStart.DotProduct(wallDirRaw);
                        var barSpans = new List<(XYZ P1, XYZ P2)>();
                        foreach (var seg in solidSegments)
                        {
                            double segStartProj = seg.Start;
                            double segEndProj = seg.End;
                            if (segEndProj <= segStartProj) continue;
                            XYZ p1 = wallStart + wallDirRaw.Multiply(segStartProj - wallStartProj);
                            XYZ p2 = wallStart + wallDirRaw.Multiply(segEndProj - wallStartProj);
                            barSpans.Add((p1, p2));
                        }

                        sb.AppendLine($"  Стена Id={kv.Key}: диаметр выпуска={dowelDiaMm}мм  сплошных участков(без проёмов)={solidSegments.Count}  " +
                            $"стержней(участков)={barSpans.Count}  " +
                            $"halfB(отступ от оси стены)={MmX(halfB):0}мм{(halfB <= 0 ? "  ⚠ толщина стены меньше диаметра выпуска" : "")}  " +
                            $"вертикальных уровней={vCount}{(vCount < 1 ? "  ⚠ фундамент слишком тонкий" : "")}");
                        foreach (var seg in solidSegments)
                            sb.AppendLine($"    участок: [{MmX(seg.Start):0};{MmX(seg.End):0}]мм  длина={MmX(seg.End - seg.Start):0}мм");

                        if (barSpans.Count == 0 || halfB <= 0 || vCount < 1) continue;

                        XYZ normalDown = XYZ.BasisZ.Negate();
                        foreach (XYZ lateral in new[] { acrossDir.Multiply(halfB), acrossDir.Negate().Multiply(halfB) })
                        {
                            foreach (var span in barSpans)
                            {
                                XYZ p1 = new XYZ(span.P1.X + lateral.X, span.P1.Y + lateral.Y, topZ);
                                XYZ p2 = new XYZ(span.P2.X + lateral.X, span.P2.Y + lateral.Y, topZ);
                                var curves = new List<Curve> { Line.CreateBound(p1, p2) };
                                try
                                {
                                    Rebar rebar = Rebar.CreateFromCurves(
                                        doc, RebarStyle.Standard, hBarType, null, null, foundation,
                                        normalDown, curves, RebarHookOrientation.Right, RebarHookOrientation.Right,
                                        true, true);
                                    if (rebar == null)
                                    {
                                        sb.AppendLine("    [FAIL] CreateFromCurves вернул null");
                                        continue;
                                    }
                                    if (vCount > 1)
                                        rebar.GetShapeDrivenAccessor().SetLayoutAsNumberWithSpacing(vCount, stepFt, true, true, true);
                                    doc.Regenerate();
                                    BoundingBoxXYZ bb = rebar.get_BoundingBox(null);
                                    double topGapMm = bb != null ? MmX(foundTopZ - bb.Max.Z) : double.NaN;
                                    sb.AppendLine($"    [OK] Id={rebar.Id}  BBox Min={P(bb?.Min)} Max={P(bb?.Max)}  " +
                                        $"отступ верх←верх фундамента={topGapMm:0}мм (ожидали {FoundationStarterCommand.TieTopOffsetMm:0}мм)");
                                }
                                catch (Exception ex)
                                {
                                    sb.AppendLine($"    [FAIL] {ex.GetType().Name}: {ex.Message}");
                                }
                            }
                        }
                    }
                    expTx2.RollBack();
                }
            }
            sb.AppendLine();

            // === Выпуски под окнами (проёмы, НЕ доходящие до низа фундамента — см.
            // BuildWindowFillerStarters/GetWindowOpenings). Чистый расчёт, без создания в
            // модели — печатаем РЕАЛЬНО обнаруженные границы проёма (после уточнения бисекцией,
            // см. RefineBoundary) и параметры расстановки (отступ от края по правилу делимости
            // на 200мм, число стержней, фактический шаг).
            sb.AppendLine("=== Выпуски под окнами (предпросмотр, без создания в модели) ===");
            foreach (var kv in familiesByWall)
            {
                Wall wall = doc.GetElement(kv.Key) as Wall;
                Curve wallCurve = (wall?.Location as LocationCurve)?.Curve;
                if (wall == null || wallCurve == null) continue;

                XYZ wallStart = wallCurve.GetEndPoint(0);
                XYZ wallEnd = wallCurve.GetEndPoint(1);
                XYZ wallDirRaw = (wallEnd - wallStart).Normalize();
                double wallTopZ = wall.get_BoundingBox(null).Max.Z;
                double sampleStepFt = FoundationStarterCommand.MmToFt(FoundationStarterCommand.OpeningScanStepMm);

                if (!geomCache.TryGetValue(wall.Id, out List<Solid> solids))
                {
                    GeometryElement geom = wall.get_Geometry(new Options { ComputeReferences = false, DetailLevel = ViewDetailLevel.Fine });
                    solids = geom?.OfType<Solid>().Where(s => !s.Faces.IsEmpty).ToList();
                    geomCache[wall.Id] = solids;
                }
                if (solids == null || solids.Count == 0) continue;

                var windows = FoundationStarterCommand.GetWindowOpenings(
                    solids, wallStart, wallEnd, wallDirRaw, foundTopZ, wallTopZ, sampleStepFt);
                sb.AppendLine($"  Стена Id={kv.Key}: обнаружено окон={windows.Count}");
                if (windows.Count == 0) continue;

                double nominalStepFt = FoundationStarterCommand.MmToFt(FoundationStarterCommand.WindowFillerStepMm);
                double gapFt = FoundationStarterCommand.MmToFt(FoundationStarterCommand.WindowFillerGapMm);
                double polkaZ = foundBotZ + coverFt;

                foreach (var win in windows)
                {
                    double widthMm = MmX(win.EndProj - win.StartProj);
                    double roundedWidthMm = Math.Round(widthMm / 5.0) * 5.0;
                    double remainderMm = roundedWidthMm % FoundationStarterCommand.WindowFillerStepMm;
                    bool divisibleBy200 = remainderMm < 2.5 || remainderMm > FoundationStarterCommand.WindowFillerStepMm - 2.5;
                    double edgeMarginMm = divisibleBy200
                        ? FoundationStarterCommand.WindowFillerEdgeMarginEvenMm
                        : FoundationStarterCommand.WindowFillerEdgeMarginOtherMm;
                    double edgeCoverFt = FoundationStarterCommand.MmToFt(edgeMarginMm);

                    // Считаем по ОКРУГЛЁННОЙ (до 5мм) ширине проёма, а не по сырой геометрии
                    // (см. тот же приём в BuildWindowFillerStarters), центрируя её на середине
                    // обнаруженного проёма.
                    double centerProj = (win.StartProj + win.EndProj) / 2.0;
                    double halfWidthFt = FoundationStarterCommand.MmToFt(roundedWidthMm) / 2.0;
                    double effectiveStartProj = centerProj - halfWidthFt;
                    double effectiveEndProj = centerProj + halfWidthFt;

                    double usableStart = effectiveStartProj + edgeCoverFt;
                    double usableEnd = effectiveEndProj - edgeCoverFt;
                    double usableWidth = usableEnd - usableStart;

                    int count;
                    double actualStepFt;
                    if (usableWidth <= 0)
                    {
                        count = 1;
                        actualStepFt = 0;
                    }
                    else
                    {
                        count = (int)Math.Ceiling(usableWidth / nominalStepFt - 1e-6) + 1;
                        actualStepFt = usableWidth / (count - 1);
                    }

                    double topZ = win.BottomZ - gapFt;
                    double vertLen = topZ - polkaZ;

                    sb.AppendLine($"    окно: [{MmX(win.StartProj):0};{MmX(win.EndProj):0}]мм  " +
                        $"ширина={widthMm:0.0}мм (округл.5мм={roundedWidthMm:0}мм, остаток/200={remainderMm:0.0}мм, делится={divisibleBy200})  " +
                        $"отступ от края={edgeMarginMm:0}мм  стержней={count}  шаг={MmX(actualStepFt):0.0}мм  " +
                        $"низ окна(средн.)={MmX(win.BottomZ):0}мм  верх выпуска={MmX(topZ):0}мм  длина вертикали={MmX(vertLen):0}мм" +
                        (vertLen <= 0 ? "  ⚠ окно слишком низко — выпуск не влезает" : ""));
                }
            }
            sb.AppendLine();

            // === Шпильки (заменяют хомуты в режиме "Горизонтальная арматура" — см.
            // BuildShpilkiForWall). Та же схема проверки: реальное создание в откатываемой
            // транзакции, несколько подряд (по стене), чтобы поймать накопительные баги, как
            // раньше с хомутами.
            sb.AppendLine("=== Шпильки (режим \"Горизонтальная арматура\", предпросмотр, без создания в модели) ===");
            RebarShape shpilkaShape = new FilteredElementCollector(doc)
                .OfClass(typeof(RebarShape)).Cast<RebarShape>()
                .FirstOrDefault(s => s.Name == FoundationStarterCommand.ShpilkaShapeName);
            sb.AppendLine(shpilkaShape != null
                ? $"Форма шпильки \"{FoundationStarterCommand.ShpilkaShapeName}\" найдена: Id={shpilkaShape.Id}"
                : $"⚠ Форма шпильки \"{FoundationStarterCommand.ShpilkaShapeName}\" НЕ НАЙДЕНА");
            RebarBarType shpilkaBarType = allBarTypes.FirstOrDefault(bt => bt.Name == FoundationStarterCommand.ShpilkaBarTypeName);
            sb.AppendLine(shpilkaBarType != null
                ? $"Типоразмер шпильки \"{FoundationStarterCommand.ShpilkaBarTypeName}\" найден: Id={shpilkaBarType.Id}"
                : $"⚠ Типоразмер шпильки \"{FoundationStarterCommand.ShpilkaBarTypeName}\" НЕ НАЙДЕН");

            if (shpilkaShape != null && shpilkaBarType != null && wallPairProjections.Count > 0)
            {
                using (Transaction expTx3 = new Transaction(doc, "Диагностика шпильки (тест, не сохраняется)"))
                {
                    expTx3.Start();
                    // По ПОЛНОМУ списку реальных позиций пар (как и хомуты) — берём КАЖДУЮ
                    // ВТОРУЮ позицию (индексы 0,2,4,...), а не шаг "своего" семейства, см.
                    // BuildShpilkiForWall.
                    foreach (var wallProjKv3 in wallPairProjections)
                    {
                        ElementId wallId3 = wallProjKv3.Key;
                        List<double> pairProjections3 = wallProjKv3.Value;
                        Wall wall = doc.GetElement(wallId3) as Wall;
                        Curve wallCurve = (wall?.Location as LocationCurve)?.Curve;
                        if (wall == null || wallCurve == null)
                        {
                            sb.AppendLine($"  Стена Id={wallId3}: ⚠ нет LocationCurve — шпильки невозможны");
                            continue;
                        }
                        if (pairProjections3.Count == 0)
                        {
                            sb.AppendLine($"  Стена Id={wallId3}: ⚠ нет реальных позиций — через одну ставить не на чем");
                            continue;
                        }
                        var wallFamilies3 = familiesByWall.TryGetValue(wallId3, out var fams3)
                            ? fams3.Values.ToList() : new List<(double, double, double, XYZ, int)>();
                        XYZ wallDir3 = wallFamilies3.Count > 0 ? wallFamilies3[0].Item4 : (wallCurve.GetEndPoint(1) - wallCurve.GetEndPoint(0)).Normalize();

                        double aFt = wall.Width - FoundationStarterCommand.MmToFt(FoundationStarterCommand.ShpilkaOffsetMm);
                        double topZ = foundTopZ - FoundationStarterCommand.MmToFt(FoundationStarterCommand.TieTopOffsetMm);
                        double stepZFt3 = FoundationStarterCommand.MmToFt(FoundationStarterCommand.ShpilkaVerticalStepMm);
                        double dowelBottomZ3 = foundBotZ + coverFt;
                        int vCount3 = (int)Math.Floor((topZ - dowelBottomZ3) / stepZFt3) + 1;
                        int shpilkaCount3 = (pairProjections3.Count + 1) / 2;

                        sb.AppendLine($"  Стена Id={wallId3}: толщина={MmX(wall.Width):0}мм  BI_A={MmX(aFt):0}мм" +
                            (aFt <= 0 ? "  ⚠ BI_A<=0, толщина стены меньше отступа шпильки" : "") +
                            $"  реальных позиций пар={pairProjections3.Count}  шпилек(через одну)={shpilkaCount3}  " +
                            $"по высоте: первый на {MmX(topZ):0}мм, шаг {FoundationStarterCommand.ShpilkaVerticalStepMm:0}мм, уровней={vCount3}" +
                            (vCount3 < 1 ? "  ⚠ фундамент слишком тонкий" : ""));

                        if (aFt <= 0 || vCount3 < 1) continue;

                        XYZ acrossDir3 = wall.Orientation.Normalize();
                        XYZ wallStart3 = wallCurve.GetEndPoint(0);
                        double startProj3 = wallStart3.DotProduct(wallDir3);
                        double halfA3 = aFt / 2.0;

                        double openingCheckHeightFt3 = FoundationStarterCommand.MmToFt(FoundationStarterCommand.OpeningCheckHeightMm);

                        // Тестируем первые несколько И последний индекс (см. комментарий ниже
                        // про "прилипание" — та же история, что и у хомутов).
                        var indices3 = new List<int>();
                        for (int i = 0; i < pairProjections3.Count; i += 2)
                            if (indices3.Count < 3 || i + 2 >= pairProjections3.Count) indices3.Add(i);

                        foreach (int i in indices3)
                        {
                            int h = i / 2;
                            XYZ center = wallStart3 + wallDir3.Multiply(pairProjections3[i] - startProj3);
                            XYZ c = new XYZ(center.X, center.Y, topZ);

                            // Под проёмом (нет материала стены у низа) шпилька не ставится —
                            // см. тот же приём в BuildShpilkiForWall.
                            if (!FoundationStarterCommand.HasSolidAt(wall, c, foundTopZ, openingCheckHeightFt3, geomCache))
                            {
                                sb.AppendLine($"    h={h}: пропущена — под проёмом (нет материала стены у низа)");
                                continue;
                            }

                            XYZ origin = c - acrossDir3.Multiply(halfA3);

                            try
                            {
                                Rebar shpilka = Rebar.CreateFromRebarShape(doc, shpilkaShape, shpilkaBarType, foundation, origin, acrossDir3, wallDir3);
                                if (shpilka == null)
                                {
                                    sb.AppendLine($"    h={h} [FAIL] CreateFromRebarShape вернул null");
                                    continue;
                                }

                                Parameter pA = shpilka.LookupParameter("BI_A");
                                if (pA != null && !pA.IsReadOnly) pA.Set(aFt);
                                doc.Regenerate();

                                // Массив СНАЧАЛА, доводка — ПОСЛЕ: поправка, применённая до
                                // SetLayoutAsNumberWithSpacing, при построении массива "терялась"
                                // (смещение по Z оставалось ≈350мм что до, что после неё) — см. тот
                                // же приём в BuildShpilkiForWall.
                                if (vCount3 > 1)
                                    shpilka.GetShapeDrivenAccessor().SetLayoutAsNumberWithSpacing(vCount3, stepZFt3, true, true, true);
                                doc.Regenerate();

                                // Довора́чиваем остаточную разницу точным сдвигом по УЖЕ ГОТОВОМУ
                                // (с массивом) элементу: по X;Y — до c, по Z — верх BBox до topZ
                                // (крючки формы уходят ВВЕРХ от origin, без этого шпилька вставала
                                // НАД фундаментом, а не в его теле).
                                BoundingBoxXYZ bbPre = shpilka.get_BoundingBox(null);
                                if (bbPre != null)
                                {
                                    XYZ bbPreCenter = (bbPre.Min + bbPre.Max) * 0.5;
                                    XYZ residual = new XYZ(c.X - bbPreCenter.X, c.Y - bbPreCenter.Y, topZ - bbPre.Max.Z);
                                    if (!residual.IsZeroLength())
                                    {
                                        ElementTransformUtils.MoveElement(doc, shpilka.Id, residual);
                                        doc.Regenerate();
                                    }
                                }

                                BoundingBoxXYZ bb = shpilka.get_BoundingBox(null);
                                XYZ bbCenterAcross = bb != null
                                    ? new XYZ((bb.Min.X + bb.Max.X) / 2.0, (bb.Min.Y + bb.Max.Y) / 2.0, bb.Min.Z)
                                    : null;
                                double offAcrossMm = bbCenterAcross != null
                                    ? MmX(bbCenterAcross.DistanceTo(new XYZ(c.X, c.Y, bb.Min.Z)))
                                    : double.NaN;
                                double topGapMm = bb != null ? MmX(foundTopZ - bb.Max.Z) : double.NaN;
                                sb.AppendLine($"    h={h} [OK] Id={shpilka.Id}  center(расчётный)={P(c)}  BBox Min={P(bb?.Min)} Max={P(bb?.Max)}  " +
                                    $"смещение поперёк стены от расчётного центра≈{offAcrossMm:0}мм{(offAcrossMm > 50 ? "  ⚠ смещено" : "  ок")}  " +
                                    $"отступ верх←верх фундамента={topGapMm:0}мм (ожидали {FoundationStarterCommand.TieTopOffsetMm:0}мм)");
                            }
                            catch (Exception ex)
                            {
                                sb.AppendLine($"    h={h} [FAIL] {ex.GetType().Name}: {ex.Message}");
                            }
                        }
                    }
                    expTx3.RollBack();
                }
            }
            sb.AppendLine();

            TaskDialog.Show("Диагностика выпусков в фундамент", sb.ToString());
            return Result.Succeeded;
        }
    }
}
