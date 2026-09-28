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
    /// <summary>
    /// Армирование парапета. Пользователь выбирает сборку парапета,
    /// команда обрабатывает ВСЕ стены сборки и создаёт для каждой:
    ///   • Г-образные выпуски из плиты (по одному у каждой грани стены) —
    ///     горизонтальная полка в плите (к оси стены) + вертикальная ножка вдоль грани;
    ///   • продольные горизонтальные стержни с шагом по высоте.
    /// Выпуски раскладываются вдоль стены с шагом 200 мм.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class ReinforceParapetCommand : IExternalCommand
    {
        // ============================ CONFIG ============================
        // Подтверждено:
        private const double SpacingMm       = 200;  // шаг поперечных П вдоль стены
        private const double TopCoverMm      = 20;   // защитный слой сверху
        private const double SideCoverMm     = 35;   // защитный слой по бокам
        private const double SlabBotCoverMm  = 20;   // защитный слой снизу в плите
        private const double LegTopGapMm     = 20;   // ноги нижней П не доходят до верха на 20 мм
        private const double ParapetBarDiaMm = 12;   // диаметр П (не зависит от высоты)

        // От грани стены до ОСИ Г-образного выпуска — задаётся сразу до оси (без +D/2), CoverToBarFace на это не влияет.
        private const double StarterAxisCoverMm = 40;

        private const double LongFirstOffsetMm = 50;   // первый продольный от низа парапета
        private const double LongStepMm        = 200;  // шаг продольных по высоте

        // TODO — уточнить по примеру / нормам:
        private const double LapInBarDiameters = 40;   // длина нахлёста верхней П = 40·D (для ⌀12 → 480 мм)
        private const double LongBarDiaMm      = 12;   // диаметр продольных (задаётся отдельно от П) — ПОДТВЕРДИТЬ
        private const int    LongBarsPerRow    = 2;    // продольных в ряду: по одному у каждой грани — ПОДТВЕРДИТЬ
        private const double EndStirrupOffMm   = SideCoverMm; // отступ крайних П от торца стены — ПОДТВЕРДИТЬ

        // true  — заданные слои считаются до ГРАНИ стержня (+D/2 до оси, как в Revit);
        // false — заданные слои считаются сразу до ОСИ стержня.
        private const bool CoverToBarFace = true;
        // ===============================================================

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            // 1. Сборка парапета (из выбора либо выбрать вручную) — до транзакции
            AssemblyInstance assembly = GetAssembly(uidoc);
            if (assembly == null)
            {
                message = "Выберите сборку парапета.";
                return Result.Cancelled;
            }

            // 2. Стены сборки
            List<Wall> walls = assembly.GetMemberIds()
                .Select(id => doc.GetElement(id))
                .OfType<Wall>()
                .ToList();
            if (walls.Count == 0)
            {
                message = "В сборке не найдено ни одной стены.";
                return Result.Failed;
            }

            // 3. Типоразмеры арматуры
            RebarBarType barP = FindBarType(doc, ParapetBarDiaMm);
            RebarBarType barL = FindBarType(doc, LongBarDiaMm);
            if (barP == null || barL == null)
            {
                message = "В проекте нет подходящих типоразмеров арматурных стержней (RebarBarType).";
                return Result.Failed;
            }

            var skipped = new List<string>();

            using (Transaction t = new Transaction(doc, "Армирование парапета"))
            {
                t.Start();

                foreach (Wall wall in walls)
                {
                    // Стена должна быть валидным хостом (несущей)
                    RebarHostData host = RebarHostData.GetRebarHostData(wall);
                    if (host == null || !host.IsValidHost())
                    {
                        skipped.Add($"Стена id{wall.Id.IntValue()}: не является хостом арматуры (сделайте несущей).");
                        continue;
                    }

                    WallGeom g = GetWallGeom(wall);

                    // Плита под стеной — из членов сборки
                    double penetration = 0;
                    Floor slab = FindSlabUnder(assembly, g, doc);
                    if (slab != null)
                    {
                        double slabThick = SlabThickness(slab, doc);
                        penetration = Math.Max(0, slabThick - Axis(SlabBotCoverMm, barP.BarModelDiameter));
                    }
                    else
                    {
                        skipped.Add($"Стена id{wall.Id.IntValue()}: плита под стеной не найдена — нижняя П без заглубления.");
                    }

                    CreateStarterBars(doc, wall, g, barP, penetration);
                    CreateLongitudinalBars(doc, wall, g, barL);
                }

                // TODO: угловые П на пересечении стен.
                // Определяем общие торцевые точки стен сборки; в каждом узле по правилу
                // ставим 2 отдельные П. Геометрию узла нужно подтвердить (ориентация,
                // привязка к какой из стен) — поэтому здесь пока только детекция.
                var corners = FindSharedCorners(walls);
                // foreach (XYZ c in corners) { /* добавить 2 П по правилу узла */ }

                t.Commit();
            }

            if (skipped.Count > 0)
                TaskDialog.Show("Армирование парапета",
                    $"Обработано стен: {walls.Count - skipped.Count} из {walls.Count}.\n\n" +
                    string.Join("\n", skipped));

            return Result.Succeeded;
        }

        // ---------------------------------------------------------------
        //  Г-ОБРАЗНЫЕ ВЫПУСКИ ИЗ ПЛИТЫ
        // ---------------------------------------------------------------
        private void CreateStarterBars(Document doc, Wall wall, WallGeom g,
                                       RebarBarType bar, double penetration)
        {
            double barD = bar.BarModelDiameter;

            // Точка на осевой линии (у низа стены) для первого стержня раскладки
            XYZ origin = g.Base + g.RunDir * Mm(EndStirrupOffMm);
            double arrayLength = g.Length - 2 * Mm(EndStirrupOffMm);
            if (arrayLength <= 0) arrayLength = g.Length; // защита от очень коротких стен

            var shapes = new[]
            {
                BuildStarterL(origin, g, penetration, barD, positiveFace: true),
                BuildStarterL(origin, g, penetration, barD, positiveFace: false)
            };

            foreach (IList<Curve> curves in shapes)
            {
                Rebar r = Rebar.CreateFromCurves(
                    doc, RebarStyle.Standard, bar, null, null,
                    wall, g.RunDir, curves,
                    RebarHookOrientation.Right, RebarHookOrientation.Right,
                    true, false);

                if (r == null) continue;

                RebarShapeDrivenAccessor acc = r.GetShapeDrivenAccessor();
                acc.SetLayoutAsMaximumSpacing(
                    Mm(SpacingMm), arrayLength,
                    barsOnNormalSide: true, includeFirstBar: true, includeLastBar: true);
            }
        }

        /// <summary>
        /// Г-образный выпуск из плиты у одной из граней стены: вертикальная ножка вдоль
        /// грани (от глубины заделки в плите до верха с отступом LegTopGapMm) + горизонтальная
        /// полка в плите. Оба стержня (у обеих граней) гнутся в одну сторону (+WidthDir) —
        /// сонаправлены, а не зеркальны друг другу.
        /// </summary>
        private IList<Curve> BuildStarterL(XYZ o, WallGeom g, double penetration, double barD, bool positiveFace)
        {
            double hw   = g.Width / 2 - Mm(StarterAxisCoverMm); // 40 мм от грани стены до оси Г-стержня
            double zTop = g.Height - Axis(LegTopGapMm, barD);   // нога не доходит до верха на 20
            double zBot = -penetration;                          // уходит вниз, в плиту
            double faceSign = positiveFace ? 1 : -1;
            XYZ w = g.WidthDir, z = XYZ.BasisZ;

            XYZ face  = o + w * hw * faceSign;   // точка у грани стены (на отметке базы)
            XYZ top   = face + z * zTop;         // верх вертикальной ножки
            XYZ bend  = face + z * zBot;         // точка гиба (глубина заделки в плите)
            XYZ inner = bend + w * hw;           // конец горизонтальной полки — оба стержня гнутся в сторону +WidthDir

            return new List<Curve>
            {
                Line.CreateBound(top, bend),   // вертикальная ножка вдоль грани
                Line.CreateBound(bend, inner)  // горизонтальная полка в плите, гиб в одну сторону
            };
        }

        // ---------------------------------------------------------------
        //  ПРОДОЛЬНЫЕ (горизонтальные)
        // ---------------------------------------------------------------
        private void CreateLongitudinalBars(Document doc, Wall wall, WallGeom g, RebarBarType bar)
        {
            double barD = bar.BarModelDiameter;
            double hw   = g.Width / 2 - Axis(SideCoverMm, barD);

            XYZ s = g.Base + g.RunDir * Mm(SideCoverMm);
            XYZ e = g.Base + g.RunDir * (g.Length - Mm(SideCoverMm));

            // Положения по толщине: у одной или обеих граней
            var faces = LongBarsPerRow >= 2
                ? new[] { -g.WidthDir * hw, g.WidthDir * hw }
                : new[] { XYZ.Zero };

            foreach (double zRow in LongitudinalRowElevations(g.Height, barD))
            {
                foreach (XYZ face in faces)
                {
                    XYZ off = face + XYZ.BasisZ * zRow;
                    Curve c = Line.CreateBound(s + off, e + off);
                    Rebar.CreateFromCurves(
                        doc, RebarStyle.Standard, bar, null, null,
                        wall, XYZ.BasisZ, new List<Curve> { c },
                        RebarHookOrientation.Right, RebarHookOrientation.Right,
                        true, false);
                }
            }
        }

        /// <summary>
        /// Отметки рядов продольной арматуры от низа парапета.
        /// Первый ряд — 50 мм, далее шаг 200 мм, пока не выше (верх − защ. слой).
        /// TODO: заменить на точную логику count(height) из примера.
        /// </summary>
        private IEnumerable<double> LongitudinalRowElevations(double height, double barD)
        {
            double first = Mm(LongFirstOffsetMm) + barD / 2;
            double step  = Mm(LongStepMm);
            double top   = height - Axis(TopCoverMm, barD);

            for (double z = first; z <= top + 1e-6; z += step)
                yield return z;
        }

        // ---------------------------------------------------------------
        //  ГЕОМЕТРИЯ СТЕНЫ
        // ---------------------------------------------------------------
        private class WallGeom
        {
            public XYZ Base;      // точка на осевой линии на отметке низа стены
            public XYZ RunDir;    // вдоль стены (ед.)
            public XYZ WidthDir;  // поперёк стены (ед.)
            public double Length; // длина
            public double Height; // высота (параметр «Высота»)
            public double Width;  // толщина
        }

        private WallGeom GetWallGeom(Wall wall)
        {
            var line = (Line)((LocationCurve)wall.Location).Curve;
            XYZ p0 = line.GetEndPoint(0);
            XYZ run = (line.GetEndPoint(1) - p0).Normalize();

            double h        = wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM).AsDouble();
            double baseOff  = wall.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET).AsDouble();
            ElementId lvlId = wall.get_Parameter(BuiltInParameter.WALL_BASE_CONSTRAINT).AsElementId();
            double baseZ    = ((wall.Document.GetElement(lvlId) as Level)?.Elevation ?? 0) + baseOff;

            return new WallGeom
            {
                Base     = new XYZ(p0.X, p0.Y, baseZ),
                RunDir   = run,
                WidthDir = XYZ.BasisZ.CrossProduct(run).Normalize(),
                Length   = line.Length,
                Height   = h,
                Width    = wall.Width
            };
        }

        // ---------------------------------------------------------------
        //  ПЛИТА
        // ---------------------------------------------------------------
        private Floor FindSlabUnder(AssemblyInstance asm, WallGeom g, Document doc)
        {
            double baseZ = g.Base.Z;
            XYZ mid = g.Base + g.RunDir * (g.Length / 2);

            return asm.GetMemberIds()
                .Select(id => doc.GetElement(id))
                .OfType<Floor>()
                .Select(f => new { F = f, BB = f.get_BoundingBox(null) })
                .Where(x => x.BB != null
                            && x.BB.Max.Z <= baseZ + Mm(5)               // под низом стены
                            && mid.X >= x.BB.Min.X - Mm(50) && mid.X <= x.BB.Max.X + Mm(50)
                            && mid.Y >= x.BB.Min.Y - Mm(50) && mid.Y <= x.BB.Max.Y + Mm(50))
                .OrderByDescending(x => x.BB.Max.Z)                       // ближайшая сверху
                .Select(x => x.F)
                .FirstOrDefault();
        }

        private double SlabThickness(Floor f, Document doc)
        {
            var cs = (doc.GetElement(f.GetTypeId()) as FloorType)?.GetCompoundStructure();
            if (cs != null) return cs.GetWidth();
            return f.get_Parameter(BuiltInParameter.FLOOR_ATTR_THICKNESS_PARAM).AsDouble();
        }

        // ---------------------------------------------------------------
        //  УГЛЫ (детекция общих торцов стен сборки)
        // ---------------------------------------------------------------
        private List<XYZ> FindSharedCorners(List<Wall> walls)
        {
            var ends = new List<XYZ>();
            foreach (Wall w in walls)
            {
                var line = (Line)((LocationCurve)w.Location).Curve;
                ends.Add(line.GetEndPoint(0));
                ends.Add(line.GetEndPoint(1));
            }

            var corners = new List<XYZ>();
            for (int i = 0; i < ends.Count; i++)
                for (int j = i + 1; j < ends.Count; j++)
                    if (ends[i].DistanceTo(ends[j]) < Mm(5) &&
                        !corners.Any(c => c.DistanceTo(ends[i]) < Mm(5)))
                        corners.Add(ends[i]);

            return corners;
        }

        // ---------------------------------------------------------------
        //  ВСПОМОГАТЕЛЬНОЕ
        // ---------------------------------------------------------------
        private AssemblyInstance GetAssembly(UIDocument uidoc)
        {
            Document doc = uidoc.Document;

            foreach (ElementId id in uidoc.Selection.GetElementIds())
                if (doc.GetElement(id) is AssemblyInstance ai)
                    return ai;

            try
            {
                Reference r = uidoc.Selection.PickObject(ObjectType.Element, "Выберите сборку парапета");
                return doc.GetElement(r) as AssemblyInstance;
            }
            catch
            {
                return null; // отмена выбора
            }
        }

        private static RebarBarType FindBarType(Document doc, double diameterMm)
        {
            double target = Mm(diameterMm);
            return new FilteredElementCollector(doc)
                .OfClass(typeof(RebarBarType))
                .Cast<RebarBarType>()
                .OrderBy(bt => Math.Abs(bt.BarModelDiameter - target))
                .FirstOrDefault();
        }

        private static double Mm(double mm) =>
            UnitUtils.ConvertToInternalUnits(mm, UnitTypeId.Millimeters);

        /// <summary>Смещение от грани стены до ОСИ стержня с учётом режима задания слоя.</summary>
        private static double Axis(double coverMm, double barModelDiameter) =>
            CoverToBarFace ? Mm(coverMm) + barModelDiameter / 2 : Mm(coverMm);
    }
}
