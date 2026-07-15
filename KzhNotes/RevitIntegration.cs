// RevitIntegration.cs
// Всё, что касается Revit API: снимок листов, запись в параметр,
// немодальный мост через ExternalEvent и сервис-операции (записать лист / обновить все).
// Требует ссылки на RevitAPI.dll и RevitAPIUI.dll.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
namespace KzhNotes
{
    /// <summary>Мост для немодального окна: очередь действий, выполняемых в валидном контексте Revit.</summary>
    public sealed class RevitEventBridge : IExternalEventHandler
    {
        private readonly ConcurrentQueue<Action<UIApplication>> _queue =
            new ConcurrentQueue<Action<UIApplication>>();
        private ExternalEvent _event;

        public void Init() { _event = ExternalEvent.Create(this); }

        /// <summary>Поставить действие в очередь и разбудить Revit (вызывать из UI-потока окна).</summary>
        public void Run(Action<UIApplication> action)
        {
            _queue.Enqueue(action);
            _event.Raise();
        }

        public void Execute(UIApplication app)
        {
            Action<UIApplication> a;
            while (_queue.TryDequeue(out a))
            {
                try { a(app); }
                catch (Exception ex)
                {
                    TaskDialog.Show("Примечания — ошибка", ex.Message);
                }
            }
        }

        public string GetName() { return "KzhNotes.RevitEventBridge"; }
    }

    /// <summary>Результат пакетной операции для отчёта в окне.</summary>
    public sealed class OpReport
    {
        public int Updated { get; set; }
        public int Skipped { get; set; }
        public List<string> Lines { get; } = new List<string>();
    }

    /// <summary>Сервис-операции над моделью (вызываются внутри моста, в валидном контексте).</summary>
    public static class NotesAppService
    {
        public const string ParamName = "BI_примечание_расширенное";

        // ---------- снимок ----------
        public static List<SheetInfo> BuildSnapshot(Document doc)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                .Where(s => !s.IsPlaceholder)
                .Select(s => new SheetInfo(s.SheetNumber, s.Name))
                .ToList();
        }

        /// <summary>Выделенные в Project Browser листы (+ активный вид, если это лист).</summary>
        public static List<ViewSheet> GetSelectedSheets(UIDocument uidoc)
        {
            var doc = uidoc.Document;
            var res = new List<ViewSheet>();
            foreach (var id in uidoc.Selection.GetElementIds())
            {
                var vs = doc.GetElement(id) as ViewSheet;
                if (vs != null && !vs.IsPlaceholder) res.Add(vs);
            }
            if (res.Count == 0 && uidoc.ActiveView is ViewSheet act && !act.IsPlaceholder)
                res.Add(act);
            return res.GroupBy(s => s.Id.IntegerValue).Select(g => g.First()).ToList();
        }

        // ---------- запись в параметр ----------
        private static bool WriteParam(ViewSheet sheet, string text, out string err)
        {
            var p = sheet.LookupParameter(ParamName);
            if (p == null) { err = "нет параметра «" + ParamName + "»"; return false; }
            if (p.StorageType != StorageType.String) { err = "параметр не текстовый"; return false; }
            if (p.IsReadOnly) { err = "параметр только для чтения"; return false; }
            p.Set(text ?? "");
            err = null;
            return true;
        }

        private static SheetInfo Find(List<SheetInfo> snap, string number)
        {
            return snap.FirstOrDefault(s => s.Number == number);
        }

        // ---------- операции ----------

        /// <summary>Сохранить состав листа в ES и записать пересобранный текст в параметр. Одна транзакция.</summary>
        public static OpReport ApplyAndSave(Document doc, ViewSheet sheet,
                                            IList<NoteItem> items, List<SheetInfo> snap)
        {
            var report = new OpReport();
            var engine = new NotesEngine(snap);
            var cur = Find(snap, sheet.SheetNumber) ?? new SheetInfo(sheet.SheetNumber, sheet.Name);
            var rendered = engine.Build(cur, items);

            using (var t = new Transaction(doc, "Примечания: записать лист " + sheet.SheetNumber))
            {
                t.Start();
                NotesStorage.Write(sheet, NotesStorage.FromItems(items));
                string err;
                if (WriteParam(sheet, rendered.Text, out err)) report.Updated++;
                else { report.Skipped++; report.Lines.Add(sheet.SheetNumber + ": " + err); }
                t.Commit();
            }
            foreach (var w in rendered.Warnings)
                report.Lines.Add(sheet.SheetNumber + " ⚠ " + w);
            return report;
        }

        /// <summary>Применить набор к нескольким листам (каждый пересчитывает ссылки под свою марку). Одна транзакция.</summary>
        public static OpReport ApplySetToSheets(Document doc, IEnumerable<ViewSheet> sheets,
                                                NoteSet set, List<SheetInfo> snap)
        {
            var report = new OpReport();
            var engine = new NotesEngine(snap);
            using (var t = new Transaction(doc, "Примечания: применить набор «" + set.Name + "»"))
            {
                t.Start();
                foreach (var sheet in sheets)
                {
                    var items = set.ToItems(NotesLibrary.ById);
                    var cur = Find(snap, sheet.SheetNumber) ?? new SheetInfo(sheet.SheetNumber, sheet.Name);
                    var rendered = engine.Build(cur, items);
                    NotesStorage.Write(sheet, NotesStorage.FromItems(items));
                    string err;
                    if (WriteParam(sheet, rendered.Text, out err)) report.Updated++;
                    else { report.Skipped++; report.Lines.Add(sheet.SheetNumber + ": " + err); }
                    foreach (var w in rendered.Warnings)
                        report.Lines.Add(sheet.SheetNumber + " ⚠ " + w);
                }
                t.Commit();
            }
            return report;
        }

        /// <summary>Пересобрать текст на ВСЕХ листах с сохранённым составом (после перенумерации). Одна транзакция.</summary>
        public static OpReport UpdateAllFromStorage(Document doc, List<SheetInfo> snap)
        {
            var report = new OpReport();
            var engine = new NotesEngine(snap);
            var sheets = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSheet)).Cast<ViewSheet>().Where(s => !s.IsPlaceholder);

            using (var t = new Transaction(doc, "Примечания: обновить все листы"))
            {
                t.Start();
                foreach (var sheet in sheets)
                {
                    var comp = NotesStorage.Read(sheet);
                    if (comp == null || comp.IsEmpty) { report.Skipped++; continue; }
                    var items = NotesStorage.ToNoteItems(comp);
                    var cur = Find(snap, sheet.SheetNumber) ?? new SheetInfo(sheet.SheetNumber, sheet.Name);
                    var rendered = engine.Build(cur, items);
                    string err;
                    if (WriteParam(sheet, rendered.Text, out err)) report.Updated++;
                    else { report.Skipped++; report.Lines.Add(sheet.SheetNumber + ": " + err); }
                    foreach (var w in rendered.Warnings)
                        report.Lines.Add(sheet.SheetNumber + " ⚠ " + w);
                }
                t.Commit();
            }
            return report;
        }
    }
}
