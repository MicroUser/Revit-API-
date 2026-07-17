// NotesModel.cs
// Модель данных библиотеки и наборов. Чистый C#, без Revit API.
//   PunktDef  — определение пункта в библиотеке (тело с токенами + поля + флаг вычитки ссылок).
//   NotesLibrary (см. NotesLibrary.cs) — статический список PunktDef.
//   NoteSet   — именованный набор-шаблон (список пунктов с значениями полей и порядком).
//   NoteSetStore — загрузка/сохранение наборов во внешний json рядом с плагином.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace KzhNotes
{
    /// <summary>Поле-значение пункта. Если задан Options — в UI это выпадающий список (с возможностью ввода).</summary>
    public sealed class FieldDef
    {
        public string Name { get; set; }     // ключ, напр. "класс"
        public string Default { get; set; }  // значение по умолчанию
        public string Label { get; set; }    // подпись в UI
        public List<string> Options { get; set; }  // варианты для выпадающего списка (null = обычный текст)

        /// <summary>Если задано — в UI выпадающий список заполняется не из Options, а марками
        /// текущего проекта с этим семейством (напр. "Л" -> Л-1, Л-2, Л-3 из снимка листов).</summary>
        public string MarkFamily { get; set; }

        public FieldDef() { }
        public FieldDef(string name, string def, string label = null, List<string> options = null)
        {
            Name = name; Default = def ?? ""; Label = label ?? name; Options = options;
        }
    }

    /// <summary>Определение пункта в библиотеке.</summary>
    public sealed class PunktDef
    {
        public string Id { get; set; }          // "СТ-08"
        public string Group { get; set; }       // "СТ" | "ФМ" | ... | "—"
        public string Body { get; set; }        // тело с токенами {{...}}
        public List<FieldDef> Fields { get; set; } = new List<FieldDef>();

        /// <summary>true — ссылки в теле вычитаны/надёжны; false — авто-догадка, требует проверки.</summary>
        public bool RefsReviewed { get; set; }

        /// <summary>Явная подсказка для списка библиотеки (перекрывает авто-превью).</summary>
        public string Hint { get; set; }

        /// <summary>Короткое превью для списка в UI (без токенов).</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string Preview
        {
            get
            {
                if (!string.IsNullOrEmpty(Hint)) return Hint;
                string s = System.Text.RegularExpressions.Regex.Replace(Body ?? "", @"\{\{.*?\}\}", "…");
                return s.Length > 90 ? s.Substring(0, 90) + "…" : s;
            }
        }

        /// <summary>Создать экземпляр-пункт (для состава листа) со значениями полей по умолчанию.</summary>
        public NoteItem NewItem()
        {
            var f = new Dictionary<string, string>();
            foreach (var fd in Fields) f[fd.Name] = fd.Default;
            return new NoteItem(Body, f, Id);
        }
    }

    /// <summary>Пункт внутри набора-шаблона: ссылка на библиотечный ID + переопределённые значения полей.</summary>
    public sealed class NoteSetItem
    {
        public string LibraryId { get; set; }
        public Dictionary<string, string> Fields { get; set; } = new Dictionary<string, string>();
    }

    /// <summary>Именованный набор пунктов (напр. «СТ. Опалубка»), применяемый к листу/листам.</summary>
    public sealed class NoteSet
    {
        public string Name { get; set; }
        public string Note { get; set; }               // комментарий/для какой марки-типа
        public List<NoteSetItem> Items { get; set; } = new List<NoteSetItem>();

        /// <summary>Развернуть набор в список NoteItem для конкретного листа (тела берём из библиотеки).</summary>
        public List<NoteItem> ToItems(Func<string, PunktDef> byId)
        {
            var result = new List<NoteItem>();
            foreach (var it in Items)
            {
                var def = byId(it.LibraryId);
                if (def == null) continue;             // пункт исчез из библиотеки — пропускаем
                var item = def.NewItem();
                foreach (var kv in it.Fields) item.Fields[kv.Key] = kv.Value; // переопределения набора
                result.Add(item);
            }
            return result;
        }
    }

    /// <summary>Загрузка/сохранение наборов в секцию "note_sets" общего файла проекта.</summary>
    public static class NoteSetStore
    {
        private const string SectionKey = "note_sets";

        private static readonly System.Text.Json.JsonSerializerOptions Opt =
            new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };

        public static List<NoteSet> Load(string dataPath)
        {
            string raw = ProjectDataStore.LoadSection(dataPath, SectionKey);
            if (raw == null) return new List<NoteSet>();
            try
            {
                return System.Text.Json.JsonSerializer.Deserialize<List<NoteSet>>(raw, Opt)
                       ?? new List<NoteSet>();
            }
            catch { return new List<NoteSet>(); }
        }

        public static void Save(string dataPath, IEnumerable<NoteSet> sets)
        {
            string raw = System.Text.Json.JsonSerializer.Serialize(sets.ToList(), Opt);
            ProjectDataStore.SaveSection(dataPath, SectionKey, raw);
        }
    }

    /// <summary>
    /// Читает и записывает именованные секции из единого JSON-файла проекта.
    /// Формат файла: { "note_sets": [...], "checklist": {...} }
    /// </summary>
    internal static class ProjectDataStore
    {
        public static string LoadSection(string filePath, string key)
        {
            if (!File.Exists(filePath)) return null;
            try
            {
                using (var doc = System.Text.Json.JsonDocument.Parse(
                    File.ReadAllText(filePath, Encoding.UTF8)))
                {
                    System.Text.Json.JsonElement el;
                    if (doc.RootElement.TryGetProperty(key, out el))
                        return el.GetRawText();
                }
            }
            catch { }
            return null;
        }

        public static void SaveSection(string filePath, string key, string rawJson)
        {
            // Читаем существующие секции как сырой текст
            var sections = new List<KeyValuePair<string, string>>();
            bool keyFound = false;
            if (File.Exists(filePath))
            {
                try
                {
                    using (var doc = System.Text.Json.JsonDocument.Parse(
                        File.ReadAllText(filePath, Encoding.UTF8)))
                    {
                        foreach (var prop in doc.RootElement.EnumerateObject())
                        {
                            if (prop.Name == key)
                            {
                                sections.Add(new KeyValuePair<string, string>(key, rawJson));
                                keyFound = true;
                            }
                            else
                            {
                                sections.Add(new KeyValuePair<string, string>(
                                    prop.Name, prop.Value.GetRawText()));
                            }
                        }
                    }
                }
                catch { }
            }
            if (!keyFound)
                sections.Add(new KeyValuePair<string, string>(key, rawJson));

            Directory.CreateDirectory(Path.GetDirectoryName(filePath));

            var sb = new StringBuilder();
            sb.Append("{\r\n");
            for (int i = 0; i < sections.Count; i++)
            {
                if (i > 0) sb.Append(",\r\n");
                sb.Append("  ")
                  .Append(System.Text.Json.JsonSerializer.Serialize(sections[i].Key))
                  .Append(": ")
                  .Append(sections[i].Value);
            }
            sb.Append("\r\n}");
            File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
        }
    }
}