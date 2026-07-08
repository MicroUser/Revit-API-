// NotesWindow.xaml.cs
// РќРµРјРѕРґР°Р»СЊРЅРѕРµ РѕРєРЅРѕ: СЂР°Р±РѕС‚Р°РµС‚ СЃРѕ РЎРќРРњРљРћРњ Р»РёСЃС‚РѕРІ (Р±РµР· Revit API РІ UI-РїРѕС‚РѕРєРµ).
// Р›СЋР±РѕРµ РѕР±СЂР°С‰РµРЅРёРµ Рє РјРѕРґРµР»Рё вЂ” С‚РѕР»СЊРєРѕ С‡РµСЂРµР· RevitEventBridge.Run(...).

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.DB;
// using Autodesk.Revit.UI; -- removed: TextBox/Visibility conflict; UIDocument used via var only

namespace KzhNotes
{
    /// <summary>РћР±С‘СЂС‚РєР° Р±РёР±Р»РёРѕС‚РµС‡РЅРѕРіРѕ РїСѓРЅРєС‚Р° РґР»СЏ СЃРїРёСЃРєР° (СЃ РїСЂРµРІСЊСЋ Рё РїРѕРјРµС‚РєРѕР№ РЅРµРІС‹С‡РёС‚Р°РЅРЅС‹С… СЃСЃС‹Р»РѕРє).</summary>
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

    /// <summary>Р­РєР·РµРјРїР»СЏСЂ РїСѓРЅРєС‚Р° РІ СЃРѕСЃС‚Р°РІРµ Р»РёСЃС‚Р° (РґР»СЏ РїСЂР°РІРѕРіРѕ СЃРїРёСЃРєР°).</summary>
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
        private List<SheetInfo> _selInfos = new List<SheetInfo>();   // SheetInfo РІС‹Р±СЂР°РЅРЅС‹С… Р»РёСЃС‚РѕРІ
        private List<int> _selIds = new List<int>();                 // ElementId.IntegerValue РІС‹Р±СЂР°РЅРЅС‹С… Р»РёСЃС‚РѕРІ

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

            // С„РёР»СЊС‚СЂ РїРѕ РіСЂСѓРїРїР°Рј
            var groups = new List<string> { "(РІСЃРµ)" };
            groups.AddRange(NotesLibrary.Groups().OrderBy(g => g));
            cboGroup.ItemsSource = groups;
            cboGroup.SelectedIndex = 0;

            LoadSets();
            RefreshLibrary();
            UpdateModeUi();
        }

        // ---------- Р±РёР±Р»РёРѕС‚РµРєР° ----------
        private void Filter_Changed(object sender, EventArgs e) { RefreshLibrary(); }

        private void RefreshLibrary()
        {
            if (cboGroup == null) return;
            string grp = cboGroup.SelectedItem as string;
            string q = (txtSearch.Text ?? "").Trim().ToLowerInvariant();

            IEnumerable<PunktDef> src = NotesLibrary.Punkts;
            if (!string.IsNullOrEmpty(grp) && grp != "(РІСЃРµ)")
                src = src.Where(p => p.Group == grp);
            if (q.Length > 0)
                src = src.Where(p => (p.Body ?? "").ToLowerInvariant().Contains(q)
                                   || (p.Id ?? "").ToLowerInvariant().Contains(q));

            lstLibrary.ItemsSource = src.Select(p => new LibVM(p)).ToList();
        }

        // ---------- РґРѕР±Р°РІР»РµРЅРёРµ / СѓРґР°Р»РµРЅРёРµ / РїРѕСЂСЏРґРѕРє ----------
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

        // ---------- РїРѕР»СЏ РІС‹Р±СЂР°РЅРЅРѕРіРѕ РїСѓРЅРєС‚Р° ----------
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
                    Text = "вЂ” Сѓ РїСѓРЅРєС‚Р° РЅРµС‚ СЂРµРґР°РєС‚РёСЂСѓРµРјС‹С… РїРѕР»РµР№ вЂ”",
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

        // ---------- РїСЂРµРґРїСЂРѕСЃРјРѕС‚СЂ (РґР»СЏ РїРµСЂРІРѕРіРѕ РІС‹Р±СЂР°РЅРЅРѕРіРѕ Р»РёСЃС‚Р°) ----------
        private void RefreshPreview()
        {
            if (_selInfos.Count == 0)
            {
                txtPreview.Text = "(Р»РёСЃС‚ РЅРµ РІС‹Р±СЂР°РЅ вЂ” РїСЂРµРґРїСЂРѕСЃРјРѕС‚СЂ РЅРµРґРѕСЃС‚СѓРїРµРЅ)";
                txtWarnings.Text = "";
                return;
            }
            var cur = _selInfos[0];
            var engine = new NotesEngine(_snapshot);
            var res = engine.Build(cur, _items.Select(v => v.Item).ToList());
            txtPreview.Text = res.Text;
            txtWarnings.Text = res.Warnings.Count > 0
                ? "вљ  " + string.Join("   |   ", res.Warnings)
                : "";
        }

        // ---------- СЂРµР¶РёРј (РѕРґРёРЅ Р»РёСЃС‚ / РЅРµСЃРєРѕР»СЊРєРѕ) ----------
        private void UpdateModeUi()
        {
            int n = _selIds.Count;
            if (n == 0)
                lblSheets.Text = "Р›РёСЃС‚С‹ РЅРµ РІС‹Р±СЂР°РЅС‹ вЂ” РІС‹РґРµР»РёС‚Рµ Р»РёСЃС‚(С‹) Рё РЅР°Р¶РјРёС‚Рµ В«РћР±РЅРѕРІРёС‚СЊ РІС‹Р±РѕСЂВ».";
            else if (n == 1)
                lblSheets.Text = "Р›РёСЃС‚: " + _selInfos[0].Number + " вЂ” " + _selInfos[0].Name +
                                 "   |   РњР°СЂРєР°: " + (_selInfos[0].Mark ?? "вЂ”");
            else
                lblSheets.Text = "Р’С‹Р±СЂР°РЅРѕ Р»РёСЃС‚РѕРІ: " + n + " вЂ” СЂРµР¶РёРј РїСЂРёРјРµРЅРµРЅРёСЏ РЅР°Р±РѕСЂР° (РїСЂР°РІРєР° РїСѓРЅРєС‚РѕРІ РЅРµРґРѕСЃС‚СѓРїРЅР°).";

            bool single = n == 1;
            btnWrite.IsEnabled = single;
            btnAdd.IsEnabled = single; btnRemove.IsEnabled = single;
            btnUp.IsEnabled = single; btnDown.IsEnabled = single;
            btnApplySet.IsEnabled = n >= 1;
            btnSaveSet.IsEnabled = single;
        }

        // ---------- РЅР°Р±РѕСЂС‹ ----------
        private void LoadSets()
        {
            _sets = NoteSetStore.Load(SetsPath);
            cboSets.ItemsSource = _sets;
            if (_sets.Count > 0) cboSets.SelectedIndex = 0;
        }

        private void btnSaveSet_Click(object sender, RoutedEventArgs e)
        {
            string name = (txtSetName.Text ?? "").Trim();
            if (name.Length == 0) { MessageBox.Show("Р’РІРµРґРёС‚Рµ РёРјСЏ РЅР°Р±РѕСЂР°."); return; }
            if (_items.Count == 0) { MessageBox.Show("РЎРѕСЃС‚Р°РІ РїСѓСЃС‚."); return; }

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
            MessageBox.Show("РќР°Р±РѕСЂ В«" + name + "В» СЃРѕС…СЂР°РЅС‘РЅ.");
        }

        private void btnApplySet_Click(object sender, RoutedEventArgs e)
        {
            var set = cboSets.SelectedItem as NoteSet;
            if (set == null) { MessageBox.Show("Р’С‹Р±РµСЂРёС‚Рµ РЅР°Р±РѕСЂ."); return; }

            if (_selIds.Count <= 1)
            {
                // РѕРґРёРЅ Р»РёСЃС‚: Р·Р°РіСЂСѓР¶Р°РµРј РЅР°Р±РѕСЂ РІ СЂРµРґР°РєС‚РѕСЂ (РјРѕР¶РЅРѕ РґРѕРїСЂР°РІРёС‚СЊ Рё Р·Р°РїРёСЃР°С‚СЊ)
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
                // РЅРµСЃРєРѕР»СЊРєРѕ Р»РёСЃС‚РѕРІ: РїСЂРёРјРµРЅСЏРµРј СЃСЂР°Р·Сѓ Рє РІС‹Р±СЂР°РЅРЅС‹Рј (РєР°Р¶РґС‹Р№ РїРµСЂРµСЃС‡РёС‚Р°РµС‚ СЃСЃС‹Р»РєРё)
                var ids = _selIds.ToList();
                var snap = _snapshot;
                _bridge.Run(app =>
                {
                    var doc = app.ActiveUIDocument.Document;
                    var sheets = ids.Select(id => doc.GetElement(new ElementId(id)) as ViewSheet)
                                    .Where(s => s != null).ToList();
                    var rep = NotesAppService.ApplySetToSheets(doc, sheets, set, snap);
                    Dispatcher.Invoke(() => ShowReport(rep, "РџСЂРёРјРµРЅС‘РЅ РЅР°Р±РѕСЂ В«" + set.Name + "В»"));
                });
            }
        }

        // ---------- РѕРїРµСЂР°С†РёРё СЃ РјРѕРґРµР»СЊСЋ (С‡РµСЂРµР· РјРѕСЃС‚) ----------

        /// <summary>РџСЂРѕС‡РёС‚Р°С‚СЊ РІС‹РґРµР»РµРЅРёРµ Рё СЃРЅРёРјРѕРє РёР· Revit Рё РѕР±РЅРѕРІРёС‚СЊ РѕРєРЅРѕ.</summary>
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
            if (_selIds.Count != 1) { MessageBox.Show("Р’С‹Р±РµСЂРёС‚Рµ СЂРѕРІРЅРѕ РѕРґРёРЅ Р»РёСЃС‚."); return; }
            var id = _selIds[0];
            var items = _items.Select(v => v.Item).ToList();
            var snap = _snapshot;
            _bridge.Run(app =>
            {
                var doc = app.ActiveUIDocument.Document;
                var sheet = doc.GetElement(new ElementId(id)) as ViewSheet;
                if (sheet == null) return;
                var rep = NotesAppService.ApplyAndSave(doc, sheet, items, snap);
                Dispatcher.Invoke(() => ShowReport(rep, "Р—Р°РїРёСЃР°РЅ Р»РёСЃС‚ " + sheet.SheetNumber));
            });
        }

        private void btnUpdateAll_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show("РџРµСЂРµСЃРѕР±СЂР°С‚СЊ С‚РµРєСЃС‚ РїСЂРёРјРµС‡Р°РЅРёР№ РЅР° Р’РЎР•РҐ Р»РёСЃС‚Р°С… СЃ СЃРѕС…СЂР°РЅС‘РЅРЅС‹Рј СЃРѕСЃС‚Р°РІРѕРј?",
                    "РћР±РЅРѕРІРёС‚СЊ РІСЃС‘", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
            var snap = _snapshot;
            _bridge.Run(app =>
            {
                var doc = app.ActiveUIDocument.Document;
                var rep = NotesAppService.UpdateAllFromStorage(doc, snap);
                Dispatcher.Invoke(() => ShowReport(rep, "РћР±РЅРѕРІР»РµРЅРёРµ РІСЃРµС… Р»РёСЃС‚РѕРІ"));
            });
        }

        private void ShowReport(OpReport rep, string title)
        {
            string msg = "РћР±РЅРѕРІР»РµРЅРѕ: " + rep.Updated + "\nРџСЂРѕРїСѓС‰РµРЅРѕ: " + rep.Skipped;
            if (rep.Lines.Count > 0)
                msg += "\n\n" + string.Join("\n", rep.Lines.Take(40)) +
                       (rep.Lines.Count > 40 ? "\nвЂ¦ (" + (rep.Lines.Count - 40) + " РµС‰С‘)" : "");
            MessageBox.Show(msg, title);
            // РїРѕСЃР»Рµ Р·Р°РїРёСЃРё РїРѕР»РµР·РЅРѕ РїРµСЂРµС‡РёС‚Р°С‚СЊ СЃРЅРёРјРѕРє (РЅРѕРјРµСЂР° РјРѕРіР»Рё РїРѕРјРµРЅСЏС‚СЊСЃСЏ РІ РґСЂ. СЃРµСЃСЃРёРё)
            RefreshPreview();
        }
    }
}
