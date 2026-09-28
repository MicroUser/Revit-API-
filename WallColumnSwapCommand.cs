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
    /// <summary>
    /// Интерактивная конвертация стена ↔ структурная колонна.
    /// Направление берётся из типа выбранного элемента (стена → колонна,
    /// колонна → стена), длиной ничего не ограничивается — решает проектировщик.
    ///
    /// Сечение колонны задаётся параметрами BI_ширина (поперёк, = толщина стены)
    /// и BI_длина (вдоль, = длина стены). Команда сама определяет, параметры это
    /// экземпляра (заполняются на месте) или типа (подбирается/создаётся типоразмер).
    ///
    /// Режимы:
    ///   • есть выделение перед запуском → конвертируется всё подходящее за один Undo
    ///     (выделенная сборка разворачивается в свои стены и колонны, новые элементы остаются в сборке);
    ///   • выделения нет → множественный выбор (рамка/клики), «Готово» — конвертировать, Esc — отмена.
    ///
    /// ЧТО НАСТРОИТЬ (константы ниже):
    ///   ColumnFamilyName — имя семейства колонны, ColumnTypeTemplateName — типоразмер-образец в нём;
    ///   RotationOffset   — 0 или Math.PI/2: калибруется один раз, если колонна
    ///                      встала не вдоль оси стены.
    /// Предполагается: BI_ширина по локальной X семейства, BI_длина по локальной Y.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class WallColumnSwapCommand : IExternalCommand
    {
        // ==== Настройки проекта ====
        const double ModuleMm         = 10.0;    // округление длины колонны из стены
        const string ColumnFamilyName = "(Колонны_монолитные)Прямоугольная";       // семейство
        const string ColumnTypeTemplateName = "(колонна)железобетон_C20/25_400х400"; // типоразмер-образец в нём
        const string WallTemplateName = "(стена)железобетон_C20/25_t=300";
        const string ParamWidth       = "BI_ширина"; // поперёк, локальная X
        const string ParamLength      = "BI_длина";  // вдоль, локальная Y
        const string WallWorkset      = "04_Стены";    // рабочий набор новых стен
        const string ColumnWorkset    = "05_Колонны";  // рабочий набор новых колонн
        const double RotationOffset   = -Math.PI / 2; // BI_длина идёт по локальной Y → ось Y колонны совмещаем с осью стены

        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            UIDocument uidoc = data.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            Family colFamily = FindColumnFamily(doc, ref message);
            if (colFamily == null) return message.Length > 0 ? Result.Failed : Result.Cancelled;

            WallType wallTemplate = FindWallTemplate(doc);
            if (wallTemplate == null)
            {
                message = $"Не найден однослойный базовый тип стены (искали «{WallTemplateName}»).";
                return Result.Failed;
            }

            // Выделение перед запуском ИЛИ рамка/клики в PickObjects (Enter/«Готово» — принять, Esc — отмена)
            List<ElementId> ids = Expand(doc, uidoc.Selection.GetElementIds()
                .Select(id => doc.GetElement(id)).Where(e => e != null));

            if (ids.Count == 0)
            {
                try
                {
                    ids = Expand(doc, uidoc.Selection.PickObjects(ObjectType.Element, new SwapFilter(),
                            "Выберите стены, колонны или сборки (рамкой или кликами), затем «Готово»")
                        .Select(r => doc.GetElement(r.ElementId)));
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                {
                    return Result.Cancelled;
                }
            }

            missingWorksets.Clear();
            int wallsToCols = 0, colsToWalls = 0;
            var errors = new List<string>();
            using (var t = new Transaction(doc, "Стена ↔ колонна"))
            {
                t.Start();
                foreach (var id in ids)
                {
                    var el = doc.GetElement(id);
                    if (el == null) continue;
                    // SubTransaction: сбой на одном элементе не оставляет недоделанных объектов
                    using (var st = new SubTransaction(doc))
                    {
                        st.Start();
                        try
                        {
                            bool wasWall = el is Wall;
                            ConvertOne(doc, el, colFamily, wallTemplate);
                            st.Commit();
                            if (wasWall) wallsToCols++; else colsToWalls++;
                        }
                        catch (Exception ex)
                        {
                            st.RollBack();
                            errors.Add($"{id.IntValue()}: {ex.Message}");
                        }
                    }
                }
                t.Commit();
            }

            string report = "Стен изменено на колонн: " + wallsToCols + Environment.NewLine
                          + "Колонн изменено на стены: " + colsToWalls;
            if (errors.Count > 0)
                report += Environment.NewLine + "Не удалось: " + errors.Count + Environment.NewLine
                    + string.Join(Environment.NewLine, errors.Take(10));
            if (missingWorksets.Count > 0)
                report += Environment.NewLine + "Не найден рабочий набор (элементы оставлены в текущем): "
                    + string.Join(", ", missingWorksets);
            TaskDialog.Show("Готово", report);
            return Result.Succeeded;
        }

        static void ConvertOne(Document doc, Element e, Family fam, WallType tmpl)
        {
            Element created = null;
            if (e is Wall w) created = WallToColumn(doc, w, fam);
            else if (e is FamilyInstance fi) created = ColumnToWall(doc, fi, tmpl);
            if (created == null) return;

            ApplyWorkset(doc, created, created is Wall ? WallWorkset : ColumnWorkset);

            // Новый элемент остаётся в той же сборке. Добавляем ДО удаления старого,
            // иначе сборка из одного элемента исчезнет вместе с ним.
            ElementId asmId = e.AssemblyInstanceId;
            if (asmId != null && asmId != ElementId.InvalidElementId
                && doc.GetElement(asmId) is AssemblyInstance asm)
            {
                try { asm.AddMemberIds(new List<ElementId> { created.Id }); } catch { }
            }
            doc.Delete(e.Id);
        }

        static readonly HashSet<string> missingWorksets = new HashSet<string>();

        // Только для проектов с совместной работой; нет набора — элемент остаётся в текущем
        static void ApplyWorkset(Document doc, Element e, string name)
        {
            if (!doc.IsWorkshared) return;
            Workset ws = new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset)
                .FirstOrDefault(w => w.Name == name);
            if (ws == null) { missingWorksets.Add(name); return; }
            Parameter p = e.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);
            if (p != null && !p.IsReadOnly) p.Set(ws.Id.IntegerValue);
        }

        // Сборка → все её конвертируемые элементы (остальное выделение — как есть), без повторов
        static List<ElementId> Expand(Document doc, IEnumerable<Element> picked)
        {
            var result = new List<ElementId>();
            var seen = new HashSet<ElementId>();
            foreach (var e in picked)
            {
                IEnumerable<Element> items = e is AssemblyInstance asm
                    ? asm.GetMemberIds().Select(doc.GetElement).Where(m => m != null)
                    : new[] { e };
                foreach (var m in items)
                    if (IsConvertible(m) && seen.Add(m.Id)) result.Add(m.Id);
            }
            return result;
        }

        // ==== Поиск семейства и шаблона ====

        static string chosenFamilyName; // выбор пользователя живёт до перезапуска Revit

        static string Norm(string s) =>
            (s ?? "").ToLowerInvariant().Replace(" ", "").Replace('x', 'х'); // латинская x → кириллическая х

        static Family FindColumnFamily(Document doc, ref string message)
        {
            var cands = new FilteredElementCollector(doc)
                .OfClass(typeof(Family)).Cast<Family>()
                .Where(f => f.FamilyCategory != null
                    && f.FamilyCategory.Id.IntValue() == (int)BuiltInCategory.OST_StructuralColumns)
                .OrderBy(f => Norm(f.Name).Contains("монолитн") && !Norm(f.Name).Contains("временн") ? 0 : 1)
                .ThenBy(f => f.Name)
                .ToList();

            string want = Norm(chosenFamilyName ?? ColumnFamilyName);
            Family hit = cands.FirstOrDefault(f => Norm(f.Name) == want);
            if (hit != null) return hit;

            if (cands.Count == 0)
            {
                message = "В проекте нет загруженных семейств несущих колонн.";
                return null;
            }
            if (cands.Count == 1) return chosenFamily(cands[0]);

            var td = new TaskDialog("Семейство колонны")
            {
                MainInstruction = $"Семейство «{ColumnFamilyName}» не найдено. Выберите семейство колонны",
                MainContent = "Оно должно иметь параметры " + ParamWidth + " и " + ParamLength +
                              (cands.Count > 4 ? $"\nПоказаны первые 4 из {cands.Count}." : ""),
                CommonButtons = TaskDialogCommonButtons.Cancel
            };
            var links = new[] { TaskDialogCommandLinkId.CommandLink1, TaskDialogCommandLinkId.CommandLink2,
                                TaskDialogCommandLinkId.CommandLink3, TaskDialogCommandLinkId.CommandLink4 };
            int n = Math.Min(4, cands.Count);
            for (int i = 0; i < n; i++) td.AddCommandLink(links[i], cands[i].Name);

            var res = td.Show();
            for (int i = 0; i < n; i++)
                if ((int)res == (int)TaskDialogResult.CommandLink1 + i) return chosenFamily(cands[i]);

            message = "";
            return null;
        }

        static Family chosenFamily(Family f) { chosenFamilyName = f.Name; return f; }

        static WallType FindWallTemplate(Document doc)
        {
            var cands = new FilteredElementCollector(doc)
                .OfClass(typeof(WallType)).Cast<WallType>()
                .Where(wt => wt.Kind == WallKind.Basic
                    && wt.GetCompoundStructure() != null
                    && wt.GetCompoundStructure().LayerCount == 1)
                .OrderBy(wt => Norm(wt.Name) == Norm(WallTemplateName) ? 0
                             : Norm(wt.Name).Contains("железобетон") ? 1 : 2)
                .ThenBy(wt => wt.Name)
                .ToList();
            return cands.FirstOrDefault();
        }

        // ==== Что можно конвертировать ====

        static bool IsConvertible(Element e)
        {
            if (e is Wall w) return IsStraightSingleLayer(w);
            if (e is FamilyInstance fi)
                return fi.Category != null
                    && fi.Category.Id.IntValue() == (int)BuiltInCategory.OST_StructuralColumns
                    && fi.Location is LocationPoint;
            return false;
        }

        static bool IsStraightSingleLayer(Wall w)
        {
            if (!(w.Location is LocationCurve lc) || !(lc.Curve is Line)) return false;
            var cs = w.WallType.GetCompoundStructure();
            return w.WallType.Kind == WallKind.Basic && cs != null && cs.LayerCount == 1;
        }

        class SwapFilter : ISelectionFilter
        {
            public bool AllowElement(Element e) =>
                IsConvertible(e) || (e is AssemblyInstance a && Expand(a.Document, new[] { e }).Count > 0);
            public bool AllowReference(Reference r, XYZ p) => false;
        }

        // ==== Стена → колонна ====

        static Element WallToColumn(Document doc, Wall wall, Family colFamily)
        {
            var line = (Line)((LocationCurve)wall.Location).Curve;
            XYZ center = (line.GetEndPoint(0) + line.GetEndPoint(1)) * 0.5;
            double wallAngle = Math.Atan2(line.Direction.Y, line.Direction.X);

            double widthMm = ToMm(wall.WallType.Width);                  // толщина как есть
            double lengthMm = RoundToModule(ToMm(line.Length), ModuleMm); // длина округлённая

            ElementId baseLvlId = wall.get_Parameter(BuiltInParameter.WALL_BASE_CONSTRAINT).AsElementId();
            double baseOff = wall.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET).AsDouble();
            ElementId topLvlId = wall.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE).AsElementId();
            double topOff = wall.get_Parameter(BuiltInParameter.WALL_TOP_OFFSET).AsDouble();
            double unconnH = wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM).AsDouble();
            Level baseLvl = (Level)doc.GetElement(baseLvlId);

            FamilySymbol sym0 = TemplateSymbol(doc, colFamily);
            if (!sym0.IsActive) { sym0.Activate(); doc.Regenerate(); }

            var col = doc.Create.NewFamilyInstance(center, sym0, baseLvl, StructuralType.Column);
            ElementTransformUtils.RotateElement(doc, col.Id,
                Line.CreateBound(center, center + XYZ.BasisZ), wallAngle + RotationOffset);

            ApplySize(doc, col, colFamily, widthMm, lengthMm);

            SetParam(col, BuiltInParameter.FAMILY_BASE_LEVEL_PARAM, baseLvlId);
            SetParam(col, BuiltInParameter.FAMILY_BASE_LEVEL_OFFSET_PARAM, baseOff);
            if (topLvlId != null && topLvlId != ElementId.InvalidElementId)
            {
                SetParam(col, BuiltInParameter.FAMILY_TOP_LEVEL_PARAM, topLvlId);
                SetParam(col, BuiltInParameter.FAMILY_TOP_LEVEL_OFFSET_PARAM, topOff);
            }
            else
            {
                SetParam(col, BuiltInParameter.FAMILY_TOP_LEVEL_PARAM, baseLvlId);
                SetParam(col, BuiltInParameter.FAMILY_TOP_LEVEL_OFFSET_PARAM, baseOff + unconnH);
            }

            return col;
        }

        /// <summary>
        /// Задаёт сечение колонны через BI_ширина / BI_длина. Если это редактируемые
        /// параметры экземпляра — заполняет их; иначе подбирает/создаёт типоразмер.
        /// </summary>
        static void ApplySize(Document doc, FamilyInstance col, Family fam, double widthMm, double lengthMm)
        {
            double w = ToInternal(widthMm), l = ToInternal(lengthMm);
            var pw = col.LookupParameter(ParamWidth);
            var pl = col.LookupParameter(ParamLength);

            // Параметры экземпляра — только если их НЕТ у типоразмера (иначе Set изменил бы сам тип)
            bool instanceParams = pw != null && pl != null && !pw.IsReadOnly && !pl.IsReadOnly
                && col.Symbol.LookupParameter(ParamWidth) == null
                && col.Symbol.LookupParameter(ParamLength) == null;

            if (instanceParams)
            {
                pw.Set(w);
                pl.Set(l);   // параметры экземпляра управляют геометрией
            }
            else
            {
                FamilySymbol typed = GetOrCreateColumnType(doc, fam, widthMm, lengthMm);
                if (!typed.IsActive) { typed.Activate(); doc.Regenerate(); }
                col.Symbol = typed;
            }
        }

        // ==== Колонна → стена ====

        static Element ColumnToWall(Document doc, FamilyInstance col, WallType template)
        {
            var lp = (LocationPoint)col.Location;
            XYZ p = lp.Point;
            double r = lp.Rotation;

            double widthMm = ToMm(GetDimFt(col, ParamWidth));
            double lengthMm = ToMm(GetDimFt(col, ParamLength));
            double thickMm = Math.Min(widthMm, lengthMm); // меньший габарит → толщина
            double lenMm = Math.Max(widthMm, lengthMm);   // больший габарит → длина

            // BI_ширина — локальная X, BI_длина — локальная Y
            XYZ dir = (lengthMm >= widthMm)
                ? new XYZ(-Math.Sin(r), Math.Cos(r), 0)
                : new XYZ(Math.Cos(r), Math.Sin(r), 0);

            ElementId baseLvlId = col.get_Parameter(BuiltInParameter.FAMILY_BASE_LEVEL_PARAM).AsElementId();
            double baseOff = col.get_Parameter(BuiltInParameter.FAMILY_BASE_LEVEL_OFFSET_PARAM).AsDouble();
            ElementId topLvlId = col.get_Parameter(BuiltInParameter.FAMILY_TOP_LEVEL_PARAM).AsElementId();
            double topOff = col.get_Parameter(BuiltInParameter.FAMILY_TOP_LEVEL_OFFSET_PARAM).AsDouble();
            Level baseLvl = (Level)doc.GetElement(baseLvlId);
            Level topLvl = (Level)doc.GetElement(topLvlId);

            double half = ToInternal(lenMm) * 0.5;
            double z = baseLvl.Elevation;
            XYZ a = new XYZ(p.X - dir.X * half, p.Y - dir.Y * half, z);
            XYZ b = new XYZ(p.X + dir.X * half, p.Y + dir.Y * half, z);
            Line axis = Line.CreateBound(a, b);

            WallType wt = GetOrCreateWallType(doc, template, thickMm);

            double nominalH = (topLvl.Elevation + topOff) - (baseLvl.Elevation + baseOff);
            if (nominalH <= 0) nominalH = ToInternal(3000);

            Wall wall = Wall.Create(doc, axis, wt.Id, baseLvlId, nominalH, baseOff, false, true);
            wall.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE).Set(topLvlId);
            wall.get_Parameter(BuiltInParameter.WALL_TOP_OFFSET).Set(topOff);

            return wall;
        }

        // ==== Имена новых типов (по образцу шаблона) ====

        static readonly Regex SizeRx = new Regex(@"\d+(?:[.,]\d+)?\s*[хxХX×]\s*\d+(?:[.,]\d+)?", RegexOptions.Compiled);
        static readonly Regex ThickRx = new Regex(@"(?<=[tтТT]\s*=\s*)\d+(?:[.,]\d+)?", RegexOptions.Compiled);

        // Колонна: размер «400х400» подменяется в имени семейства (имя типа — запасной вариант)
        // Типоразмер-образец: по имени, иначе первый «железобетон», иначе любой
        static FamilySymbol TemplateSymbol(Document doc, Family fam)
        {
            var syms = fam.GetFamilySymbolIds().Select(id => (FamilySymbol)doc.GetElement(id)).OrderBy(x => x.Name).ToList();
            return syms.FirstOrDefault(x => Norm(x.Name) == Norm(ColumnTypeTemplateName))
                ?? syms.FirstOrDefault(x => Norm(x.Name).Contains("железобетон"))
                ?? syms.First();
        }

        // Имя нового типоразмера: размер «400х400» подменяется в имени типоразмера-образца
        static string ColumnTypeBaseName(Family fam, FamilySymbol baseSym, double widthMm, double lengthMm)
        {
            string size = $"{widthMm:0}х{lengthMm:0}";
            return SizeRx.IsMatch(baseSym.Name)
                ? SizeRx.Replace(baseSym.Name, size, 1)
                : $"{baseSym.Name}_{size}";
        }

        static string ColumnTypeName(Family fam, FamilySymbol baseSym, double widthMm, double lengthMm) =>
            Unique(ColumnTypeBaseName(fam, baseSym, widthMm, lengthMm),
                fam.GetFamilySymbolIds().Select(id => fam.Document.GetElement(id).Name));

        // Стена: «t=300» подменяется в имени шаблонного типа стены
        static string WallTypeName(Document doc, WallType template, double tMm)
        {
            string name = ThickRx.IsMatch(template.Name)
                ? ThickRx.Replace(template.Name, $"{tMm:0}", 1)
                : $"{template.Name}_t={tMm:0}";
            return Unique(name, new FilteredElementCollector(doc).OfClass(typeof(WallType)).Select(e => e.Name));
        }

        static string WallTypeBaseName(WallType template, double tMm) =>
            ThickRx.IsMatch(template.Name)
                ? ThickRx.Replace(template.Name, $"{tMm:0}", 1)
                : $"{template.Name}_t={tMm:0}";

        static string Unique(string name, IEnumerable<string> existing)
        {
            var used = new HashSet<string>(existing);
            string res = name;
            for (int i = 2; used.Contains(res); i++) res = $"{name}_{i}";
            return res;
        }

        // ==== Типы ====

        static FamilySymbol GetOrCreateColumnType(Document doc, Family fam, double widthMm, double lengthMm)
        {
            double w = ToInternal(widthMm), l = ToInternal(lengthMm);
            var baseSym = TemplateSymbol(doc, fam);
            string expected = Norm(ColumnTypeBaseName(fam, baseSym, widthMm, lengthMm));
            // Переиспользуем только тип с тем же именем (тот же класс бетона) и размерами:
            // «C25/30_250х900» не подходит для «C20/25_250х900»
            FamilySymbol match = fam.GetFamilySymbolIds()
                .Select(id => (FamilySymbol)doc.GetElement(id))
                .FirstOrDefault(s => Norm(s.Name) == expected
                    && Near(GetLen(s, ParamWidth), w) && Near(GetLen(s, ParamLength), l));
            if (match != null) return match;
            var ns = (FamilySymbol)baseSym.Duplicate(ColumnTypeName(fam, baseSym, widthMm, lengthMm));
            ns.LookupParameter(ParamWidth).Set(w);
            ns.LookupParameter(ParamLength).Set(l);
            doc.Regenerate();
            return ns;
        }

        static WallType GetOrCreateWallType(Document doc, WallType template, double tMm)
        {
            double t = ToInternal(tMm);
            ElementId mat = template.GetCompoundStructure().GetMaterialId(0); // бетон шаблона

            var found = new FilteredElementCollector(doc)
                .OfClass(typeof(WallType)).Cast<WallType>()
                .FirstOrDefault(wt => wt.Kind == WallKind.Basic
                    && wt.GetCompoundStructure() != null
                    && wt.GetCompoundStructure().LayerCount == 1
                    && wt.GetCompoundStructure().GetMaterialId(0) == mat
                    && Near(wt.Width, t));
            if (found != null) return found;

            var nt = (WallType)template.Duplicate(WallTypeName(doc, template, tMm));
            var cs = nt.GetCompoundStructure();
            cs.SetLayerWidth(0, t);
            nt.SetCompoundStructure(cs);
            doc.Regenerate();
            return nt;
        }

        // ==== Утилиты ====

        // Размер колонны: сперва как параметр экземпляра, иначе как параметр типа
        static double GetDimFt(FamilyInstance col, string name)
        {
            var p = col.LookupParameter(name) ?? col.Symbol.LookupParameter(name);
            if (p == null) throw new InvalidOperationException($"Нет параметра «{name}» у колонны.");
            return p.AsDouble();
        }

        static double GetLen(FamilySymbol s, string name)
        {
            var p = s.LookupParameter(name);
            if (p == null) throw new InvalidOperationException($"Нет параметра «{name}» в семействе.");
            return p.AsDouble();
        }

        static void SetParam(Element e, BuiltInParameter bip, ElementId v) => e.get_Parameter(bip).Set(v);
        static void SetParam(Element e, BuiltInParameter bip, double v) => e.get_Parameter(bip).Set(v);

        static double RoundToModule(double mm, double module) => Math.Round(mm / module) * module;
        static double ToInternal(double mm) => UnitUtils.ConvertToInternalUnits(mm, UnitTypeId.Millimeters);
        static double ToMm(double ft) => UnitUtils.ConvertFromInternalUnits(ft, UnitTypeId.Millimeters);
        static bool Near(double a, double b) => Math.Abs(a - b) < ToInternal(0.5);
    }
}
