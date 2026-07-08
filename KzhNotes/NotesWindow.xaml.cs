// NotesWindow.xaml.cs
// Немодальное окно: работает со СНИМКОМ листов (без Revit API в UI-потоке).
// Любое обращение к модели — только через RevitEventBridge.Run(...).

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.DB;
// using Autodesk.Revit.UI; -- убрано: конфликт TextBox/Visibility; UIDocument используется только через var

namespace KzhNotes
{
    /// <summary>Обёртка библиотечного пункта для списка (с превью и пометкой невычитанных ссылок).</summary>
    public sealed class LibVM
    {
        public PunktDef Def { get; }
        public LibVM(PunktDef def) { Def = def; }
        public string Id { get { return Def.Id; } }
        public string Preview { get { return Def.Preview; } }
        public System.Windows.Visibility ReviewWarnVisibility
        {
            get { return Def.RefsReviewed ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible; }
        }
    }

    /// <summary>Экземпляр пункта в составе листа (для правого списка).</summary>
    public sealed class ItemVM
    {
        public PunktDef Def { get; }
        public NoteItem Item { get; }
        public ItemVM(PunktDef def, NoteItem item) { Def = def; Item = item; }
        public string Display
        {
            get { return (Def != null ? Def.Id + "  " : "") + (Def != null ? Def.Preview : Item.Template); }
        }
    }

    public partial class NotesWindow : Window
    {
        private readonly RevitEventBridge _bridge;

        private List<SheetInfo> _snapshot = new List<SheetInfo>();
        private List<SheetInfo> _selInfos = new List<SheetInfo>();   // SheetInfo выбранных листов
        private List<int> _selIds = new List<int>();                 // ElementId.IntegerValue выбранных листов

        private readonly ObservableCollection<ItemVM> _items = new ObservableCollection<ItemVM>();
        private List<NoteSet> _sets = new List<NoteSet>();

        private static string SetsPath
        {
            get
            {
                string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                return Path.Combine(dir, "note_sets.json");
            }
        }

        public NotesWindow(RevitEventBridge bridge)
        {
            _bridge = bridge;
            InitializeComponent();

            lstSelected.ItemsSource = _items;

            // фильтр по группам
            var groups = new List<string> { "(все)" };
            groups.AddRange(NotesLibrary.Groups().OrderBy(g => g));
            cboGroup.ItemsSource = groups;
            cboGroup.SelectedIndex = 0;

            LoadSets();
            RefreshLibrary();
            UpdateModeUi();
        }

        // ---------- библиотека ----------
        private void Filter_Changed(object sender, EventArgs e) { RefreshLibrary(); }

        private void RefreshLibrary()
        {
            if (cboGroup == null) return;
            string grp = cboGroup.SelectedItem as string;
            string q = (txtSearch.Text ?? "").Trim().ToLowerInvariant();

            IEnumerable<PunktDef> src = NotesLibrary.Punkts;
            if (!string.IsNullOrEmpty(grp) && grp != "(все)")
                src = src.Where(p => p.Group == grp);
            if (q.Length > 0)
                src = src.Where(p => (p.Body ?? "").ToLowerInvariant().Contains(q)
                                   || (p.Id ?? "").ToLowerInvariant().Contains(q));

            lstLibrary.ItemsSource = src.Select(p => new LibVM(p)).ToList();
        }

        // ---------- добавление / удаление / порядок ----------
        private void btnAdd_Click(object sender, RoutedEventArgs e)
        {
            var lib = lstLibrary.SelectedItem as LibVM;
            if (lib == null) return;
            _items.Add(new ItemVM(lib.Def, lib.Def.NewItem()));
            RefreshPreview();
        }

        private void btnRemove_Click(object sender, RoutedEventArgs e)
        {
            var vm = lstSelected.SelectedItem as ItemVM;
            if (vm == null) return;
            _items.Remove(vm);
            BuildFieldsPanel(null);
            RefreshPreview();
        }

        private void btnUp_Click(object sender, RoutedEventArgs e) { Move(-1); }
        private void btnDown_Click(object sender, RoutedEventArgs e) { Move(1); }

        private void Move(int delta)
        {
            int i = lstSelected.SelectedIndex;
            if (i < 0) return;
            int j = i + delta;
            if (j < 0 || j >= _items.Count) return;
            var vm = _items[i];
            _items.RemoveAt(i);
            _items.Insert(j, vm);
            lstSelected.SelectedIndex = j;
            RefreshPreview();
        }

        // ---------- поля выбранного пункта ----------
        private void lstSelected_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            BuildFieldsPanel(lstSelected.SelectedItem as ItemVM);
        }

        private void BuildFieldsPanel(ItemVM vm)
        {
            pnlFields.Children.Clear();
            if (vm == null || vm.Def == null || vm.Def.Fields.Count == 0)
            {
                pnlFields.Children.Add(new TextBlock
                {
                    Text = "— у пункта нет редактируемых полей —",
                    Foreground = System.Windows.Media.Brushes.Gray
                });
                return;
            }
            foreach (var fd in vm.Def.Fields)
            {
                var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
                var lbl = new TextBlock { Text = fd.Label + ":", Width = 120, VerticalAlignment = VerticalAlignment.Center };
                DockPanel.SetDock(lbl, Dock.Left);
                string key = fd.Name;
                string val;
                vm.Item.Fields.TryGetValue(key, out val);
                var tb = new TextBox { Text = val ?? "" };
                tb.TextChanged += (s, a) =>
                {
                    vm.Item.Fields[key] = ((TextBox)s).Text;
                    RefreshPreview();
                };
                row.Children.Add(lbl);
                row.Children.Add(tb);
                pnlFields.Children.Add(row);
            }
        }

        // ---------- предпросмотр (для первого выбранного листа) ----------
        private void RefreshPreview()
        {
            if (_selInfos.Count == 0)
            {
                txtPreview.Text = "(лист не выбран — предпросмотр недоступен)";
                txtWarnings.Text = "";
                return;
            }
            var cur = _selInfos[0];
            var engine = new NotesEngine(_snapshot);
            var res = engine.Build(cur, _items.Select(v => v.Item).ToList());
            txtPreview.Text = res.Text;
            txtWarnings.Text = res.Warnings.Count > 0
                ? "⚠ " + string.Join("   |   ", res.Warnings)
                : "";
        }

        // ---------- режим (один лист / несколько) ----------
        private void UpdateModeUi()
        {
            int n = _selIds.Count;
            if (n == 0)
                lblSheets.Text = "Листы не выбраны — выделите лист(ы) и нажмите «Обновить выбор».";
            else if (n == 1)
                lblSheets.Text = "Лист: " + _selInfos[0].Number + " — " + _selInfos[0].Name +
                                 "   |   Марка: " + (_selInfos[0].Mark ?? "—");
            else
                lblSheets.Text = "Выбрано листов: " + n + " — режим применения набора (правка пунктов недоступна).";

            bool single = n == 1;
            btnWrite.IsEnabled = single;
            btnAdd.IsEnabled = single; btnRemove.IsEnabled = single;
            btnUp.IsEnabled = single; btnDown.IsEnabled = single;
            btnApplySet.IsEnabled = n >= 1;
            btnSaveSet.IsEnabled = single;
        }

        // ---------- наборы ----------
        private void LoadSets()
        {
            _sets = NoteSetStore.Load(SetsPath);
            cboSets.ItemsSource = _sets;
            if (_sets.Count > 0) cboSets.SelectedIndex = 0;
        }

        private void btnSaveSet_Click(object sender, RoutedEventArgs e)
        {
            string name = (txtSetName.Text ?? "").Trim();
            if (name.Length == 0) { MessageBox.Show("Введите имя набора."); return; }
            if (_items.Count == 0) { MessageBox.Show("Состав пуст."); return; }

            var set = new NoteSet { Name = name };
            foreach (var vm in _items)
                set.Items.Add(new NoteSetItem
                {
                    LibraryId = vm.Item.LibraryId,
                    Fields = new Dictionary<string, string>(vm.Item.Fields)
                });

            _sets.RemoveAll(s => s.Name == name);
            _sets.Add(set);
            NoteSetStore.Save(SetsPath, _sets);
            cboSets.ItemsSource = null; cboSets.ItemsSource = _sets;
            cboSets.SelectedItem = set;
            MessageBox.Show("Набор «" + name + "» сохранён.");
        }

        private void btnApplySet_Click(object sender, RoutedEventArgs e)
        {
            var set = cboSets.SelectedItem as NoteSet;
            if (set == null) { MessageBox.Show("Выберите набор."); return; }

            if (_selIds.Count <= 1)
            {
                // один лист: загружаем набор в редактор (можно доправить и записать)
                _items.Clear();
                foreach (var it in set.Items)
                {
                    var def = NotesLibrary.ById(it.LibraryId);
                    if (def == null) continue;
                    var item = def.NewItem();
                    foreach (var kv in it.Fields) item.Fields[kv.Key] = kv.Value;
                    _items.Add(new ItemVM(def, item));
                }
                RefreshPreview();
            }
            else
            {
                // несколько листов: применяем сразу к выбранным (каждый пересчитает ссылки)
                var ids = _selIds.ToList();
                var snap = _snapshot;
                _bridge.Run(app =>
                {
                    var doc = app.ActiveUIDocument.Document;
                    var sheets = ids.Select(id => doc.GetElement(new ElementId(id)) as ViewSheet)
                                    .Where(s => s != null).ToList();
                    var rep = NotesAppService.ApplySetToSheets(doc, sheets, set, snap);
                    Dispatcher.Invoke(() => ShowReport(rep, "Применён набор «" + set.Name + "»"));
                });
            }
        }

        // ---------- операции с моделью (через мост) ----------

        /// <summary>Прочитать выделение и снимок из Revit и обновить окно.</summary>
        public void RequestRefreshSelection()
        {
            _bridge.Run(app =>
            {
                var uidoc = app.ActiveUIDocument;
                var doc = uidoc.Document;
                var snap = NotesAppService.BuildSnapshot(doc);
                var sheets = NotesAppService.GetSelectedSheets(uidoc);
                var ids = sheets.Select(s => s.Id.IntegerValue).ToList();
                var infos = sheets.Select(s => new SheetInfo(s.SheetNumber, s.Name)).ToList();
                SheetComposition comp = sheets.Count == 1 ? NotesStorage.Read(sheets[0]) : null;

                Dispatcher.Invoke(() => ApplyRefresh(snap, ids, infos, comp));
            });
        }

        private void ApplyRefresh(List<SheetInfo> snap, List<int> ids, List<SheetInfo> infos, SheetComposition comp)
        {
            _snapshot = snap; _selIds = ids; _selInfos = infos;

            _items.Clear();
            if (comp != null)
            {
                foreach (var st in comp.Items)
                {
                    var def = NotesLibrary.ById(st.LibraryId);
                    if (def == null) continue;
                    var item = def.NewItem();
                    if (st.Fields != null)
                        foreach (var kv in st.Fields) item.Fields[kv.Key] = kv.Value;
                    _items.Add(new ItemVM(def, item));
                }
            }
            BuildFieldsPanel(null);
            UpdateModeUi();
            RefreshPreview();
        }

        private void btnRefresh_Click(object sender, RoutedEventArgs e) { RequestRefreshSelection(); }

        private void btnWrite_Click(object sender, RoutedEventArgs e)
        {
            if (_selIds.Count != 1) { MessageBox.Show("Выберите ровно один лист."); return; }
            var id = _selIds[0];
            var items = _items.Select(v => v.Item).ToList();
            var snap = _snapshot;
            _bridge.Run(app =>
            {
                var doc = app.ActiveUIDocument.Document;
                var sheet = doc.GetElement(new ElementId(id)) as ViewSheet;
                if (sheet == null) return;
                var rep = NotesAppService.ApplyAndSave(doc, sheet, items, snap);
                Dispatcher.Invoke(() => ShowReport(rep, "Записан лист " + sheet.SheetNumber));
            });
        }

        private void btnUpdateAll_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show("Пересобрать текст примечаний на ВСЕХ листах с сохранённым составом?",
                    "Обновить всё", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
            var snap = _snapshot;
            _bridge.Run(app =>
            {
                var doc = app.ActiveUIDocument.Document;
                var rep = NotesAppService.UpdateAllFromStorage(doc, snap);
                Dispatcher.Invoke(() => ShowReport(rep, "Обновление всех листов"));
            });
        }

        private void ShowReport(OpReport rep, string title)
        {
            string msg = "Обновлено: " + rep.Updated + "\nПропущено: " + rep.Skipped;
            if (rep.Lines.Count > 0)
                msg += "\n\n" + string.Join("\n", rep.Lines.Take(40)) +
                       (rep.Lines.Count > 40 ? "\n… (" + (rep.Lines.Count - 40) + " ещё)" : "");
            MessageBox.Show(msg, title);
            // после записи полезно перечитать снимок (номера могли поменяться в др. сессии)
            RefreshPreview();
        }
    }
}
