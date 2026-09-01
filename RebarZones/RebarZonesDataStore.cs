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
            // Диаметр/шаг фоновой (основной) арматуры (см. rebar_zones.html BGd/BGs и
            // BGdTop/BGsTop/BGdBottom/BGsBottom для фундаментов) — у РАЗНЫХ плит фон может быть
            // разным (у одной d12, у другой d14 и т.п.), поэтому хранится здесь, per-floor, а не
            // общей на весь проект секцией, как раньше. Сырой JSON без разбора на C#.
            public string BgSettingsJson { get; set; }
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

        /// <summary>Стирает историю "уже размещено" для одной вкладки (грань+направление) этой
        /// плиты — см. кнопку "Сбросить память размещения" (rebar_zones.html, требует пароль,
        /// намеренно непубличная функция). Саму созданную в Revit арматуру не трогает — только
        /// снимает пометку в JSON, которая иначе заставляла бы плагин молча пропускать эти зоны
        /// при следующем размещении (см. RebarZonesWindow.PlaceZones → IsAlreadyPlaced).</summary>
        public static void ClearPlaced(Document doc, Floor floor, string comboKey)
        {
            string filePath = RevitKJChecklist.ChecklistCommand.GetSavePath(doc);
            var all = LoadAll(filePath);
            if (!all.TryGetValue(floor.UniqueId, out var fd)) return;
            if (fd.Placed.Remove(comboKey)) SaveAll(filePath, all);
        }

        private const string AnchorSectionKey = "rebar_zones_anchor";

        /// <summary>Настраиваемая анкеровка (коэффициент lₐ=K·d и/или построчно исправленные
        /// длины по диаметрам — см. rebar_zones.html LA_COEF/LA) — общая на весь проект, а не на
        /// отдельную плиту, поэтому хранится отдельной секцией JSON, а не внутри FloorData.
        /// Сырой JSON без разбора на C#: при открытии окна просто пробрасывается обратно в
        /// редактор как есть (см. RebarZonesCommand.BuildInitJson), разбирается только при
        /// размещении, чтобы применить к PlacementSettings.Anchorage (RebarZonesWindow.PlaceZones).</summary>
        public static string LoadAnchorSettingsJson(Document doc) =>
            ProjectDataStore.LoadSection(RevitKJChecklist.ChecklistCommand.GetSavePath(doc), AnchorSectionKey);

        public static void SaveAnchorSettingsJson(Document doc, string rawJson) =>
            ProjectDataStore.SaveSection(RevitKJChecklist.ChecklistCommand.GetSavePath(doc), AnchorSectionKey, rawJson);

        private const string TopBendSectionKey = "rebar_zones_topbend";

        /// <summary>Глобальный переключатель "Загнутые/Прямые верхние стержни" (см.
        /// PlacementSettings.BendTopBars и rebar_zones.html BEND_TOP) — общий на весь проект,
        /// как и анкеровка выше, а не на отдельную плиту. Сырая JSON-строка ("true"/"false")
        /// без разбора на C#, кроме момента размещения (см. RebarZonesWindow.PlaceZones). Форма
        /// конкретной зоны (П/Г/Авто/Прямая) — отдельно, приходит per-zone в "place:" (см.
        /// JsZone.Shape), а не хранится здесь.</summary>
        public static string LoadTopBendJson(Document doc) =>
            ProjectDataStore.LoadSection(RevitKJChecklist.ChecklistCommand.GetSavePath(doc), TopBendSectionKey);

        public static void SaveTopBendJson(Document doc, string rawJson) =>
            ProjectDataStore.SaveSection(RevitKJChecklist.ChecklistCommand.GetSavePath(doc), TopBendSectionKey, rawJson);

        /// <summary>Диаметр/шаг фоновой (основной) арматуры (см. rebar_zones.html BGd/BGs и
        /// BGdTop/BGsTop/BGdBottom/BGsBottom для фундаментов) — per-floor (см. FloorData.BgSettingsJson):
        /// у разных плит фон может отличаться (у одной d12, у другой d14), в отличие от анкеровки/
        /// переключателя загиба выше, которые общие на весь проект. Без сохранения фон молча
        /// сбрасывался бы на дефолтные 10мм при каждом переоткрытии окна, даже если пользователь его
        /// менял и уже разместил зоны с другим фоном. Сырой JSON без разбора на C#.</summary>
        public static string LoadBgSettingsJson(Document doc, Floor floor) =>
            GetOrEmpty(LoadAll(RevitKJChecklist.ChecklistCommand.GetSavePath(doc)), floor).BgSettingsJson;

        public static void SaveBgSettingsJson(Document doc, Floor floor, string rawJson)
        {
            string filePath = RevitKJChecklist.ChecklistCommand.GetSavePath(doc);
            var all = LoadAll(filePath);
            if (!all.TryGetValue(floor.UniqueId, out var fd)) { fd = new FloorData(); all[floor.UniqueId] = fd; }
            fd.BgSettingsJson = rawJson;
            SaveAll(filePath, all);
        }

        /// <summary>Сырой снимок зон (см. FloorData.SavedZonesJson) для этой плиты, если есть.</summary>
        public static string LoadZonesJson(Document doc, Floor floor) =>
            GetOrEmpty(LoadAll(RevitKJChecklist.ChecklistCommand.GetSavePath(doc)), floor).SavedZonesJson;

        /// <summary>Сохраняет снимок зон, СЛИВАЯ его по вкладкам с уже сохранённым на диске, а
        /// не затирая целиком. zonesJson — {comboKey:[zone,...]} только по вкладкам, загруженным
        /// В ЭТОЙ сессии (см. window.getStateJSON) — если у пользователя, например, DXF другого
        /// пользователя не подгрузился, в его сессии будут не все 4 вкладки; полная перезапись
        /// стёрла бы зоны недостающих вкладок, хотя их никто не трогал. Не защищает от гонки при
        /// ОДНОВРЕМЕННОМ сохранении из двух Revit-сессий сразу (файл не блокируется) — только от
        /// потери данных из-за частично загруженной сессии, что и есть типичный случай совместной
        /// работы через общий сетевой файл.</summary>
        public static void SaveZonesJson(Document doc, Floor floor, string zonesJson)
        {
            string filePath = RevitKJChecklist.ChecklistCommand.GetSavePath(doc);
            var all = LoadAll(filePath);
            if (!all.TryGetValue(floor.UniqueId, out var fd)) { fd = new FloorData(); all[floor.UniqueId] = fd; }

            var merged = ParseZonesDict(fd.SavedZonesJson);
            foreach (var kv in ParseZonesDict(zonesJson)) merged[kv.Key] = kv.Value;
            fd.SavedZonesJson = JsonSerializer.Serialize(merged);

            SaveAll(filePath, all);
        }

        private static Dictionary<string, JsonElement> ParseZonesDict(string json)
        {
            if (string.IsNullOrEmpty(json)) return new Dictionary<string, JsonElement>();
            try { return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) ?? new Dictionary<string, JsonElement>(); }
            catch { return new Dictionary<string, JsonElement>(); }
        }
    }
}
