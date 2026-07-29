using System;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Win32;
using KzhNotes;

namespace RevitKJChecklist
{
    public class ChecklistWindow : Window
    {
        private readonly WebView2 _web;
        private readonly string _assetFolder;
        private readonly string _initJson;
        private readonly string _savedJson;
        private readonly string _savePath;
        private readonly string _role; // "reviewer" или "executor"
        private bool _pushed;
        private bool _closePending;

        public ChecklistWindow(IntPtr revitHandle, string assetFolder,
                               string initJson, string savedJson, string savePath, string role)
        {
            _assetFolder = assetFolder;
            _initJson    = initJson;
            _savedJson   = savedJson;
            _savePath    = savePath;
            _role        = role ?? "executor";

            Title  = "Чек-лист КЖ";
            Width  = 1120;
            Height = 840;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            new WindowInteropHelper(this) { Owner = revitHandle };

            _web = new WebView2();
            Content = _web;

            Loaded += async (s, e) => await InitAsync();
            Closed += (s, e) => { try { _web?.Dispose(); } catch { } };
        }

        protected override async void OnClosing(CancelEventArgs e)
        {
            if (_closePending) { base.OnClosing(e); return; }
            e.Cancel = true;
            try
            {
                string json = await CollectStateAsync();
                if (!string.IsNullOrEmpty(json) && !string.IsNullOrEmpty(_savePath))
                    ProjectDataStore.SaveSection(_savePath, "checklist", json);
            }
            catch { }
            _closePending = true;
            Close();
        }

        private async Task InitAsync()
        {
            string userData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RevitKJChecklist", "WebView2");
            Directory.CreateDirectory(userData);

            var env = await CoreWebView2Environment.CreateAsync(null, userData, null);
            await _web.EnsureCoreWebView2Async(env);

            var core = _web.CoreWebView2;

            core.SetVirtualHostNameToFolderMapping(
                "checklist.assets", _assetFolder, CoreWebView2HostResourceAccessKind.Allow);

            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDevToolsEnabled = true;

            core.NavigationCompleted += OnNavigationCompleted;
            core.WebMessageReceived  += OnWebMessage;
            core.Navigate("https://checklist.assets/checklist.html");
        }

        private async void OnWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            string msg = e.TryGetWebMessageAsString();
            if (msg == "save-pdf")
            {
                var dlg = new SaveFileDialog
                {
                    Title            = "Сохранить PDF",
                    Filter           = "PDF|*.pdf",
                    FileName         = "Чеклист_КЖ",
                    DefaultExt       = ".pdf",
                    InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
                };
                if (dlg.ShowDialog() == true)
                {
                    try { await _web.CoreWebView2.PrintToPdfAsync(dlg.FileName); }
                    catch (Exception ex) { MessageBox.Show("Ошибка сохранения PDF:\n" + ex.Message); }
                }
            }
        }

        private async void OnNavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (_pushed || !e.IsSuccess) return;
            _pushed = true;

            // Загружаем сохранённое состояние (или начальное от Revit)
            string payload = string.IsNullOrEmpty(_savedJson) ? _initJson : _savedJson;
            if (!string.IsNullOrEmpty(payload))
                await _web.CoreWebView2.ExecuteScriptAsync("loadState(" + JsonUtil.Esc(payload) + ")");

            // Если восстановили сохранённое, добираем новые марки из модели (не трогая статусы)
            if (!string.IsNullOrEmpty(_savedJson) && !string.IsNullOrEmpty(_initJson))
            {
                string marksJson = ExtractMarksJson(_initJson);
                await _web.CoreWebView2.ExecuteScriptAsync("applyMarks(" + marksJson + ", false)");
            }

            // Применяем роль пользователя (проверяющий / исполнитель)
            await _web.CoreWebView2.ExecuteScriptAsync("setRole(" + JsonUtil.Esc(_role) + ")");
        }

        // Извлекает {"found":[...],"walls":[...],...} из initJson
        private static string ExtractMarksJson(string initJson)
        {
            const string key = "\"marks\":";
            int idx = initJson.IndexOf(key, StringComparison.Ordinal);
            if (idx < 0) return "{}";
            int start = initJson.IndexOf('{', idx + key.Length);
            if (start < 0) return "{}";
            int depth = 0, end = -1;
            for (int i = start; i < initJson.Length; i++)
            {
                if      (initJson[i] == '{') depth++;
                else if (initJson[i] == '}') { if (--depth == 0) { end = i; break; } }
            }
            return end >= 0 ? initJson.Substring(start, end - start + 1) : "{}";
        }

        public async Task<string> CollectStateAsync()
        {
            if (_web?.CoreWebView2 == null) return null;
            string raw = await _web.CoreWebView2.ExecuteScriptAsync("getStateJSON()");
            if (string.IsNullOrEmpty(raw) || raw == "null") return null;
            return JsonUtil.Unquote(raw);
        }

        public async Task ApplyMarksAsync(string marksJson, bool replace)
        {
            if (_web?.CoreWebView2 == null) return;
            await _web.CoreWebView2.ExecuteScriptAsync(
                "applyMarks(" + marksJson + ", " + (replace ? "true" : "false") + ")");
        }
    }
}
