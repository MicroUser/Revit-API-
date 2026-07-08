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

namespace KzhNotes
{
    /// <summary>Поле-значение пункта (правит пользователь; тип пока — просто текст).</summary>
    public sealed class FieldDef
    {
        public string Name { get; set; }     // ключ, напр. "класс"
        public string Default { get; set; }  // значение по умолчанию, напр. "С25/30"
        public string Label { get; set; }    // подпись в UI (может совпадать с Name)

        public FieldDef() { }
        public FieldDef(string name, string def, string label = null)
        {
            Name = name; Default = def ?? ""; Label = label ?? name;
        }
    }

    /// <summary>Определение пункта в библиотеке.</summary>
    public sealed class PunktDef
    {
        public string Id { get; set; }          // "СНМ-08"
        public string Group { get; set; }       // "СНМ" | "ФМ" | ... | "—"
        public string Body { get; set; }        // тело с токенами {{...}}
        public List<FieldDef> Fields { get; set; } = new List<FieldDef>();

        /// <summary>true — ссылки в теле вычитаны/надёжны; false — авто-догадка, требует проверки.</summary>
        public bool RefsReviewed { get; set; }

        /// <summary>Короткое превью для списка в UI (без токенов).</summary>
        public string Preview
        {
            get
            {
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

    /// <summary>Именованный набор пунктов (напр. «СНм. Опалубка»), применяемый к листу/листам.</summary>
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

    /// <summary>Загрузка/сохранение наборов в json рядом с плагином (переносимо между проектами).
    /// Зависимость: System.Text.Json (NuGet — работает и на .NET Framework 4.8).
    /// Если в проекте уже есть Newtonsoft.Json — замените две строки на JsonConvert.</summary>
    public static class NoteSetStore
    {
        private static readonly System.Text.Json.JsonSerializerOptions Opt =
            new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping // кириллица как есть
            };

        public static List<NoteSet> Load(string path)
        {
            if (!File.Exists(path)) return new List<NoteSet>();
            try
            {
                var sets = System.Text.Json.JsonSerializer.Deserialize<List<NoteSet>>(File.ReadAllText(path), Opt);
                return sets ?? new List<NoteSet>();
            }
            catch { return new List<NoteSet>(); }
        }

        public static void Save(string path, IEnumerable<NoteSet> sets)
        {
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(sets.ToList(), Opt));
        }
    }
}
