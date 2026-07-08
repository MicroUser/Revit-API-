// NotesStorage.cs
// Хранение состава примечаний листа в ExtensibleStorage.
// Источник правды — СОСТАВ (список пунктов: LibraryId + значения полей + порядок),
// а текст в BI_примечание_расширенное — производное, пересобираемое из состава.
//
// Схема ES = одно строковое поле с JSON состава (надёжно для любых значений полей).
// Требует ссылки на RevitAPI.dll и System.Text.Json (NuGet).
//
// ВАЖНО: Write(...) и Clear(...) МЕНЯЮТ элемент — вызывать только внутри открытой транзакции
// (обычно из IExternalEventHandler). Read(...) транзакции не требует.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;

namespace KzhNotes
{
    /// <summary>Один пункт в сохранённом составе листа.</summary>
    public sealed class StoredItem
    {
        public string LibraryId { get; set; }
        public Dictionary<string, string> Fields { get; set; } = new Dictionary<string, string>();
    }

    /// <summary>Сохранённый состав примечаний листа (то, что лежит в ES).</summary>
    public sealed class SheetComposition
    {
        public int Version { get; set; } = 1;
        public List<StoredItem> Items { get; set; } = new List<StoredItem>();

        public bool IsEmpty { get { return Items == null || Items.Count == 0; } }
    }

    /// <summary>Чтение/запись состава листа в ExtensibleStorage.</summary>
    public static class NotesStorage
    {
        // Фиксированный GUID схемы — не менять после первого релиза (иначе потеряется доступ к данным).
        private static readonly Guid SchemaGuid = new Guid("7C3A1E42-9B21-4E7A-8C4F-2B0D6A5E1F90");
        private const string SchemaName = "KzhNotesComposition";
        private const string FieldName = "CompositionJson";
        private const string VendorId = "KZHNOTES";

        private static readonly System.Text.Json.JsonSerializerOptions JsonOpt =
            new System.Text.Json.JsonSerializerOptions
            {
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };

        // ---------- схема ----------
        private static Schema GetOrCreateSchema()
        {
            var existing = Schema.Lookup(SchemaGuid);
            if (existing != null) return existing;

            var sb = new SchemaBuilder(SchemaGuid);
            sb.SetSchemaName(SchemaName);
            sb.SetReadAccessLevel(AccessLevel.Public);
            sb.SetWriteAccessLevel(AccessLevel.Public);
            sb.SetVendorId(VendorId);
            sb.AddSimpleField(FieldName, typeof(string));
            return sb.Finish();
        }

        // ---------- публичное API ----------

        /// <summary>Прочитать состав листа. null — если состава ещё нет.</summary>
        public static SheetComposition Read(Element sheet)
        {
            if (sheet == null) return null;
            var schema = Schema.Lookup(SchemaGuid);
            if (schema == null) return null;

            Entity ent = sheet.GetEntity(schema);
            if (ent == null || !ent.IsValid()) return null;

            string json = ent.Get<string>(schema.GetField(FieldName));
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                return System.Text.Json.JsonSerializer.Deserialize<SheetComposition>(json, JsonOpt);
            }
            catch { return null; }
        }

        /// <summary>Записать состав листа. ТРЕБУЕТ ОТКРЫТОЙ ТРАНЗАКЦИИ.</summary>
        public static void Write(Element sheet, SheetComposition comp)
        {
            if (sheet == null) return;
            var schema = GetOrCreateSchema();
            var ent = new Entity(schema);
            string json = System.Text.Json.JsonSerializer.Serialize(comp ?? new SheetComposition(), JsonOpt);
            ent.Set<string>(schema.GetField(FieldName), json);
            sheet.SetEntity(ent);
        }

        /// <summary>Удалить состав листа. ТРЕБУЕТ ОТКРЫТОЙ ТРАНЗАКЦИИ.</summary>
        public static void Clear(Element sheet)
        {
            if (sheet == null) return;
            var schema = Schema.Lookup(SchemaGuid);
            if (schema != null) sheet.DeleteEntity(schema);
        }

        /// <summary>Есть ли у листа сохранённый состав.</summary>
        public static bool Has(Element sheet)
        {
            var c = Read(sheet);
            return c != null && !c.IsEmpty;
        }

        // ---------- мост состав ↔ движок ----------

        /// <summary>Состав → список NoteItem для движка (тела берём из актуальной библиотеки).</summary>
        public static List<NoteItem> ToNoteItems(SheetComposition comp)
        {
            var items = new List<NoteItem>();
            if (comp == null) return items;
            foreach (var st in comp.Items)
            {
                var def = NotesLibrary.ById(st.LibraryId);
                if (def == null) continue;                 // пункт удалён из библиотеки — пропускаем
                var item = def.NewItem();                  // тело + дефолты полей из библиотеки
                if (st.Fields != null)
                    foreach (var kv in st.Fields) item.Fields[kv.Key] = kv.Value; // сохранённые значения
                items.Add(item);
            }
            return items;
        }

        /// <summary>Список пунктов состава (из UI) → SheetComposition для сохранения.</summary>
        public static SheetComposition FromItems(IEnumerable<NoteItem> items)
        {
            var comp = new SheetComposition();
            foreach (var it in items)
            {
                comp.Items.Add(new StoredItem
                {
                    LibraryId = it.LibraryId,
                    Fields = new Dictionary<string, string>(it.Fields)
                });
            }
            return comp;
        }
    }
}
