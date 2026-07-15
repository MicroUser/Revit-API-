using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace DAN_Plugin
{
    [Transaction(TransactionMode.Manual)]
    public class RenameViewsCommand : IExternalCommand
    {
        // Словарь: точное имя шаблона вида → суффикс после марки конструкции.
        // Итоговое имя вида: {BI_марка_конструкции}_{суффикс}
        // Пример: шаблон "01_КЖ_(01_ФундПлитный_#доп_Н_по X)_ПНК" → суффикс "Арм_доп_Н_по_X"
        //         вид с BI_марка_конструкции="Фм-1" получит имя "Фм-1_Арм_доп_Н_по_X"
        internal static readonly Dictionary<string, string> TemplateSuffix =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // ── 01 Фундаментная плита ────────────────────────────────────────
            { "*01_КЖ_(01_ФундПлитный)_ПНК",              "Опалубка" },
            { "*01_КЖ_(01_ФундПлитный)_Сечение",          "" },
            { "*01_КЖ_(01_ФундПлитный_#арм)_ПНК",         "Арм_осн" },
            { "*01_КЖ_(01_ФундПлитный_#арм)_Сечение",     "" },
            { "*01_КЖ_(01_ФундПлитный_#доп_Н_по X)_ПНК",  "Арм_доп_Н_по_X" },
            { "*01_КЖ_(01_ФундПлитный_#доп_В_по X)_ПНК",  "Арм_доп_В_по_X" },
            { "*01_КЖ_(01_ФундПлитный_#доп_Н_по Y)_ПНК",  "Арм_доп_Н_по_Y" },
            { "*01_КЖ_(01_ФундПлитный_#доп_В_по Y)_ПНК",  "Арм_доп_В_по_Y" },
            { "*01_КЖ_(01_ФундПлитный_#поперечка)_ПНК",   "Арм_поперечка" },
            { "*01_КЖ_(01_ФундПлитный_#фиксаторы)_ПНК",   "Фиксаторы" },

            // ── 01 Фундамент ленточный ───────────────────────────────────────
            { "*01_КЖ_(01_ФундЛенточный_#доп_Н)_ПНК",    "" },
            { "*01_КЖ_(01_ФундЛенточный_#доп_В)_ПНК",    "" },

            // ── 01 Сваи / Выпуски ────────────────────────────────────────────
            { "*01_КЖ_(01_*Сваи)_ПНК",         "" },
            { "*01_КЖ_(01_Выпуски)_ПНК",        "Выпуски" },
            { "*01_КЖ_(01_Выпуски)_План",        "" },
            { "*01_КЖ_(01_Выпуски)_Сечение",     "" },

            // ── 02 Приямки ───────────────────────────────────────────────────
            { "*01_КЖ_(02_Приямки_#арм)_Разрез", "" },

            // ── 03 Стены цокольные ───────────────────────────────────────────
            { "*01_КЖ_(03_СтенЦок_#арм)_ПНК",   "" },

            // ── 04 Стены / Колонны ───────────────────────────────────────────
            { "*01_КЖ_(04_Монолитный пояс)_ПНК",     "" },
            { "*01_КЖ_(04_Армирован пояс)_ПНК",      "" },
            { "*01_КЖ_(04_Стены)_ПНК",               "Опалубка" },
            { "*01_КЖ_(04_Стены)_Разрез",             "Опалубка" },
            { "*01_КЖ_(04_Стены)_Сечение",            "" },
            { "*01_КЖ_(04_Стены_#арм)_ПНК",           "" },
            { "*01_КЖ_(04_Стены_#арм)_Разрез",        "Арм_Общ" },
            { "*01_КЖ_(04_Стены_#арм)_Сечение",       "" },
            { "*01_КЖ_(04_СтеныВерт_#арм)_Разрез",    "Арм_Верт" },
            { "*01_КЖ_(04_СтеныГор_#арм)_Разрез",     "Арм_Гор" },
            { "*01_КЖ_(04_СтеныНест_#арм)_Разрез",    "" },
            { "*01_КЖ_(04_СтеныНос_#арм)_Разрез",     "" },
            { "*01_КЖ_(04_СтеныШарн_#арм)_Разрез",    "" },
            { "*01_КЖ_(04_СтеныШарн_#арм)_Сечение",   "" },

            // ── 06 Капители ──────────────────────────────────────────────────
            { "*01_КЖ_(06_Капители_#арм)_ПНК",        "" },
            { "*01_КЖ_(06_Капители_#арм)_Сечение",    "" },

            // ── 06 Плиты ─────────────────────────────────────────────────────
            { "*01_КЖ_(06_Плиты)_ПНК",                        "Опалубка" },
            { "*01_КЖ_(06_Плиты)_Сечение",                    "" },
            { "*01_КЖ_(06_Плиты_#арм)_ПНК",                   "Арм_осн" },
            { "*01_КЖ_(06_Плиты_#арм)_ПНК_фрагмент",          "" },
            { "*01_КЖ_(06_Плиты_#арм)_Сечение",               "" },
            { "*01_КЖ_(06_Плиты_#доп_Н)_ПНК",                 "Арм_доп_Н" },
            { "*01_КЖ_(06_Плиты_#доп_Н_по X)_ПНК",            "Арм_доп_Н_по_X" },
            { "*01_КЖ_(06_Плиты_#доп_Н_по Y)_ПНК",            "Арм_доп_Н_по_Y" },
            { "*01_КЖ_(06_Плиты_#доп_В)_ПНК",                 "Арм_доп_В" },
            { "*01_КЖ_(06_Плиты_#доп_В_по X)_ПНК",            "Арм_доп_В_по_X" },
            { "*01_КЖ_(06_Плиты_#доп_В_по Y)_ПНК",            "Арм_доп_В_по_Y" },
            { "*01_КЖ_(06_Плиты_#поперечная)_ПНК",             "Арм_поперечка" },
            { "*01_КЖ_(06_Плиты_#выпуски)_ПНК",               "Выпуски" },
            { "*01_КЖ_(06_Плиты_#выпуски)_Сечение",           "" },
            { "*01_КЖ_(06_Плиты_#выпуски_парапет)_ПНК_фрагмент", "" },
            { "*01_КЖ_(06_Плиты_#конструкт)_ПНК",             "" },
            { "*01_КЖ_(06_Плиты_#усиление)_ПНК",              "Арм_усиление" },
            { "*01_КЖ_(06_Плиты_ПустБлоки)_ПНК",              "" },
            { "*01_КЖ_(06_Пустотки)_ПНК",                     "" },

            // ── 07 Парапеты ──────────────────────────────────────────────────
            { "*01_КЖ_(07_Парапеты)_ПНК",            "" },
            { "*01_КЖ_(07_Парапеты)_Разрез",          "" },
            { "*01_КЖ_(07_Парапеты)_Сечение",         "" },
            { "*01_КЖ_(07_Парапеты_#арм)_Разрез",     "" },
            { "*01_КЖ_(07_Парапеты_#арм)_Сечение",    "" },
            { "*01_КЖ_(07_Парапеты_#узел_арм)_ПНК",   "" },

            // ── 08 Балки ─────────────────────────────────────────────────────
            { "*01_КЖ_(08_Балки)_Разрез",          "" },
            { "*01_КЖ_(08_Балки_#арм)_Разрез",     "" },
            { "*01_КЖ_(08_Балки_#арм)_Сечение",    "" },

            // ── 09 Лестницы ──────────────────────────────────────────────────
            { "*01_КЖ_(09_Лест*Схема)_ПНК",               "Опалубка" },
            { "*01_КЖ_(09_Лест*Схема)_Разрез",             "Опалубка_Разрез" },
            { "*01_КЖ_(09_Лест*Схема)_Сечение",            "" },
            { "*01_КЖ_(09_ЛестМарш_#арм)_Разрез",          "Арм_Разрез" },
            { "*01_КЖ_(09_ЛестМарш_#арм)_Сечение",         "" },
            { "*01_КЖ_(09_ЛестПлощадка)_ПНК",              "Опалубка" },
            { "*01_КЖ_(09_ЛестПлощадка)_Фрагмент",         "" },
            { "*01_КЖ_(09_ЛестПлощадка_#арм)_ПНК",         "Арм" },
            { "*01_КЖ_(09_ЛестПлощадка_#арм)_Разрез",      "Арм_Разрез" },

            // ── 11 Разводка ──────────────────────────────────────────────────
            { "*01_КЖ_(11_Разводка)_ПНК",    "" },
            { "*01_КЖ_(11_Разводка)_Разрез",  "" },

            // ── 02_КЖ Координация ────────────────────────────────────────────
            { "*02_КЖ_(*Координация)_ПНК",   "" },
            { "*02_КЖ_(MEP)_ПНК",            "" },
            { "*02_КЖ_(Архитектура)_ПНК",    "" },
        };

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var uidoc = commandData.Application.ActiveUIDocument;
            var doc   = uidoc.Document;

            var views = uidoc.Selection.GetElementIds()
                .Select(id => doc.GetElement(id))
                .OfType<View>()
                .Where(v => !v.IsTemplate)
                .ToList();

            if (!views.Any())
            {
                message = "Выберите один или несколько видов.";
                return Result.Failed;
            }

            var renamed  = new List<string>();
            var warnings = new List<string>();

            using (var tx = new Transaction(doc, "Переименование видов"))
            {
                tx.Start();
                foreach (var view in views)
                {
                    string warn;
                    string line = TryRename(doc, view, out warn);
                    if (warn != null) warnings.Add(warn);
                    if (line != null) renamed.Add(line);
                }
                tx.Commit();
            }

            var sb = new StringBuilder();
            if (renamed.Count > 0)
            {
                sb.AppendLine("Переименовано: " + renamed.Count);
                foreach (var s in renamed) sb.AppendLine(s);
            }
            if (warnings.Count > 0)
            {
                if (renamed.Count > 0) sb.AppendLine();
                sb.AppendLine("Пропущено (" + warnings.Count + "):");
                foreach (var s in warnings) sb.AppendLine("  • " + s);
            }
            if (sb.Length == 0) sb.Append("Ничего не изменилось.");

            TaskDialog.Show("Переименование видов", sb.ToString().TrimEnd());
            return Result.Succeeded;
        }

        private static string TryRename(Document doc, View view, out string warning)
        {
            warning = null;
            string oldName = view.Name;

            // ── Параметр BI_марка_конструкции ────────────────────────────────
            var markParam = view.LookupParameter("BI_марка_конструкции");
            string mark   = markParam != null ? (markParam.AsString() ?? "").Trim() : "";

            // Ранний выход: имя уже совпадает с {mark}_{суффикс} из словаря
            if (!string.IsNullOrEmpty(mark))
            {
                foreach (var suf in TemplateSuffix.Values)
                {
                    if (string.Equals(oldName, mark + "_" + suf, StringComparison.OrdinalIgnoreCase))
                        return null;
                }
            }

            // ── Шаблон вида ─────────────────────────────────────────────────
            if (view.ViewTemplateId == ElementId.InvalidElementId)
            {
                warning = oldName + " — не назначен шаблон вида";
                return null;
            }
            var template = doc.GetElement(view.ViewTemplateId) as View;
            if (template == null)
            {
                warning = oldName + " — шаблон не найден";
                return null;
            }

            // ── Поиск в словаре (пробуем с * и без) ─────────────────────────
            string suffix;
            string tName = template.Name;
            if (!TemplateSuffix.TryGetValue(tName, out suffix) &&
                !TemplateSuffix.TryGetValue(tName.TrimStart('*'), out suffix))
            {
                warning = oldName + " — шаблон «" + tName + "» не найден в словаре";
                return null;
            }

            if (string.IsNullOrEmpty(suffix))
            {
                warning = oldName + " — суффикс для шаблона «" + tName + "» не заполнен в словаре";
                return null;
            }

            if (string.IsNullOrEmpty(mark))
            {
                warning = oldName + " — параметр BI_марка_конструкции пустой или отсутствует";
                return null;
            }

            // ── Новое имя ────────────────────────────────────────────────────
            string newName = mark + "_" + suffix;
            if (string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase)) return null;

            try
            {
                view.Name = newName;
                return oldName + "  →  " + newName;
            }
            catch (Exception ex)
            {
                warning = oldName + " — ошибка переименования: " + ex.Message;
                return null;
            }
        }
    }

    /// <summary>
    /// Собирает все уникальные имена шаблонов видов из проекта и записывает их
    /// на рабочий стол в формате, готовом для вставки в словарь RenameViewsCommand.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class ExportViewTemplatesCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var doc = commandData.Application.ActiveUIDocument.Document;

            // Все шаблоны видов в проекте
            var templates = new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(v => v.IsTemplate)
                .OrderBy(v => v.Name)
                .ToList();

            if (!templates.Any())
            {
                TaskDialog.Show("Шаблоны видов", "В проекте нет шаблонов видов.");
                return Result.Succeeded;
            }

            // Уже заполненные ключи из словаря RenameViewsCommand
            var existing = new HashSet<string>(
                RenameViewsCommand.TemplateSuffix.Keys,
                StringComparer.OrdinalIgnoreCase);

            var sb = new StringBuilder();
            sb.AppendLine("// Вставьте нужные строки в словарь TemplateSuffix и заполните суффиксы.");
            sb.AppendLine("// Формат итогового имени вида: {BI_марка_конструкции}_{суффикс}");
            sb.AppendLine("// Уже добавленные в словарь помечены «✓».");
            sb.AppendLine();

            foreach (var t in templates)
            {
                bool done = existing.Contains(t.Name) || existing.Contains(t.Name.TrimStart('*'));
                string mark = done ? "// ✓ " : "            ";
                sb.AppendLine(mark + "{ " + Quote(t.Name) + ", \"\" },");
            }

            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                "view_templates.txt");
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);

            // Открываем файл в Блокноте
            System.Diagnostics.Process.Start("notepad.exe", path);

            TaskDialog.Show("Шаблоны видов",
                "Найдено шаблонов: " + templates.Count + "\n" +
                "Уже в словаре: " + existing.Count + "\n\n" +
                "Файл сохранён:\n" + path);

            return Result.Succeeded;
        }

        private static string Quote(string s) => "\"" + s.Replace("\"", "\\\"") + "\"";
    }
}
