using System.Collections.Generic;
using System.Text.Json;
using Autodesk.Revit.DB;
using KzhNotes;

namespace LiraToRevit.Rebar
{
    /// <summary>
    /// Данные допармирования по плите — пути к выбранным DXF (по вкладке грань+направление) и
    /// история уже реально размещённой арматуры (габарит рабочей зоны + диаметр + шаг). Хранится
    /// не отдельным файлом/Extensible Storage, а секцией "rebar_zones" в ТОМ ЖЕ JSON-файле
    /// проекта, что чек-лист и наборы примечаний (см. KzhNotes.ProjectDataStore, путь — тот же,
    /// что и у чек-листа: RevitKJChecklist.ChecklistCommand.GetSavePath, рядом с центральной
    /// моделью). Записи внутри секции — по ключу Floor.UniqueId (переживает пересохранение,
    /// смену ElementId между локальной/центральной копией и т.п.).
    /// </summary>
    internal static class RebarZonesDataStore
    {
        private const string SectionKey = "rebar_zones";

        public class PlacedRecord
        {
            public double X1 { get; set; }
            public double Y1 { get; set; }
            public double X2 { get; set; }
            public double Y2 { get; set; }
            public int D { get; set; }
            public int Step { get; set; }
        }

        public class FloorData
        {
            public Dictionary<string, string> Paths { get; set; } = new Dictionary<string, string>();
            public Dictionary<string, List<PlacedRecord>> Placed { get; set; } = new Dictionary<string, List<PlacedRecord>>();
            // Полный снимок зон по вкладкам — сырой JSON из JS (window.getStateJSON), не
            // разбираем на C#, только передаём обратно в rebar_zones.html при следующем открытии
            // (см. RebarZonesWindow.CollectStateAsync/OnClosing). Формат: {comboKey:[zone,...]}.
            public string SavedZonesJson { get; set; }
        }

        /// <summary>Ключ вкладки: "top-x" / "top-y" / "bottom-x" / "bottom-y".</summary>
        public static string ComboKey(Face face, Dir dir) =>
            (face == Face.Bottom ? "bottom" : "top") + "-" + (dir == Dir.Y ? "y" : "x");

        /// <summary>Читаемая подпись вкладки по ключу — для сообщений пользователю.</summary>
        public static string ComboLabel(string key)
        {
            string[] p = key.Split('-');
            return (p[0] == "bottom" ? "Низ" : "Верх") + " · " + p[1].ToUpperInvariant();
        }

        private static Dictionary<string, FloorData> LoadAll(string filePath)
        {
            string raw = ProjectDataStore.LoadSection(filePath, SectionKey);
            if (string.IsNullOrEmpty(raw)) return new Dictionary<string, FloorData>();
            try
            {
                return JsonSerializer.Deserialize<Dictionary<string, FloorData>>(raw)
                       ?? new Dictionary<string, FloorData>();
            }
            catch { return new Dictionary<string, FloorData>(); }
        }

        private static void SaveAll(string filePath, Dictionary<string, FloorData> all) =>
            ProjectDataStore.SaveSection(filePath, SectionKey, JsonSerializer.Serialize(all));

        private static FloorData GetOrEmpty(Dictionary<string, FloorData> all, Floor floor) =>
            all.TryGetValue(floor.UniqueId, out var fd) ? fd : new FloorData();

        public static Dictionary<string, string> LoadPaths(Document doc, Floor floor) =>
            GetOrEmpty(LoadAll(RevitKJChecklist.ChecklistCommand.GetSavePath(doc)), floor).Paths;

        public static Dictionary<string, List<PlacedRecord>> LoadPlaced(Document doc, Floor floor) =>
            GetOrEmpty(LoadAll(RevitKJChecklist.ChecklistCommand.GetSavePath(doc)), floor).Placed;

        /// <summary>Перезаписывает сохранённые пути DXF для этой плиты. Не требует транзакции —
        /// обычный файл на диске, не элемент модели.</summary>
        public static void SavePaths(Document doc, Floor floor, Dictionary<string, string> paths)
        {
            string filePath = RevitKJChecklist.ChecklistCommand.GetSavePath(doc);
            var all = LoadAll(filePath);
            if (!all.TryGetValue(floor.UniqueId, out var fd)) { fd = new FloorData(); all[floor.UniqueId] = fd; }
            fd.Paths = paths;
            SaveAll(filePath, all);
        }

        /// <summary>Добавляет/заменяет только указанные вкладки, остальные сохранённые пути не
        /// трогает — для кнопки "📁 DXF" внутри уже открытого окна (см. RebarZonesWindow), где
        /// SavePaths (полная перезапись) стёр бы пути вкладок, не участвовавших в этом выборе.</summary>
        public static void MergePaths(Document doc, Floor floor, Dictionary<string, string> newPaths)
        {
            string filePath = RevitKJChecklist.ChecklistCommand.GetSavePath(doc);
            var all = LoadAll(filePath);
            if (!all.TryGetValue(floor.UniqueId, out var fd)) { fd = new FloorData(); all[floor.UniqueId] = fd; }
            foreach (var kv in newPaths) fd.Paths[kv.Key] = kv.Value;
            SaveAll(filePath, all);
        }

        /// <summary>Добавляет записи о реально размещённой арматуре для вкладки этой плиты.</summary>
        public static void AppendPlaced(Document doc, Floor floor, string comboKey, IEnumerable<PlacedRecord> newRecords)
        {
            string filePath = RevitKJChecklist.ChecklistCommand.GetSavePath(doc);
            var all = LoadAll(filePath);
            if (!all.TryGetValue(floor.UniqueId, out var fd)) { fd = new FloorData(); all[floor.UniqueId] = fd; }
            if (!fd.Placed.TryGetValue(comboKey, out var list)) { list = new List<PlacedRecord>(); fd.Placed[comboKey] = list; }
            list.AddRange(newRecords);
            SaveAll(filePath, all);
        }

        /// <summary>Сырой снимок зон (см. FloorData.SavedZonesJson) для этой плиты, если есть.</summary>
        public static string LoadZonesJson(Document doc, Floor floor) =>
            GetOrEmpty(LoadAll(RevitKJChecklist.ChecklistCommand.GetSavePath(doc)), floor).SavedZonesJson;

        /// <summary>Перезаписывает полный снимок зон (все вкладки сразу — JS присылает их все).</summary>
        public static void SaveZonesJson(Document doc, Floor floor, string zonesJson)
        {
            string filePath = RevitKJChecklist.ChecklistCommand.GetSavePath(doc);
            var all = LoadAll(filePath);
            if (!all.TryGetValue(floor.UniqueId, out var fd)) { fd = new FloorData(); all[floor.UniqueId] = fd; }
            fd.SavedZonesJson = zonesJson;
            SaveAll(filePath, all);
        }
    }
}
