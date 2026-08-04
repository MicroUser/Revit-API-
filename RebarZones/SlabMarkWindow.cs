using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using DAN_Plugin;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using RevitKJChecklist;

namespace LiraToRevit.Rebar
{
    /// <summary>
    /// Окно пакетного анализатора марок допармирования (см. SlabMarkCommand) — хостит
    /// slab_mark_report.html через WebView2, по образцу RebarZonesWindow, но проще: инструмент
    /// не трогает открытый документ Revit после чтения DXF (нет выбора плиты в Revit — см.
    /// SlabMarkCommand), поэтому RevitEventBridge/ExternalEvent не нужен вообще — кнопка
    /// "📁 Загрузить DXF" внутри окна тоже просто читает файлы и шлёт их в JS напрямую, без моста.
    /// Данные передаются в JS при открытии и при каждой подгрузке; вся аналитика (кластеризация,
    /// группировка, живой пересчёт порогов, свободное сравнение) — целиком на JS. Состояние не
    /// сохраняется — разовый анализ загруженной пачки файлов, закрытие окна всё забывает.
    /// </summary>
    public class SlabMarkWindow : Window
    {
        private readonly WebView2 _web;
        private readonly string _assetFolder;
        private readonly string _initJson;
        private bool _pushed;

        public SlabMarkWindow(IntPtr revitHandle, string assetFolder, string initJson)
        {
            _assetFolder = assetFolder;
            _initJson = initJson;

            Title = "Анализ марок допармирования плит";
            Width = 1280;
            Height = 860;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            new WindowInteropHelper(this) { Owner = revitHandle };

            _web = new WebView2();
            Content = _web;

            Loaded += async (s, e) => await InitAsync();
            Closed += (s, e) => { try { _web?.Dispose(); } catch { } };
        }

        private async System.Threading.Tasks.Task InitAsync()
        {
            // Отдельная папка профиля WebView2 (не общая с RebarZonesWindow) — оба окна в
            // принципе могут быть открыты в одном сеансе Revit одновременно.
            string userData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LiraToRevit", "WebView2SlabMark");
            Directory.CreateDirectory(userData);

            var env = await CoreWebView2Environment.CreateAsync(null, userData, null);
            await _web.EnsureCoreWebView2Async(env);

            var core = _web.CoreWebView2;
            core.SetVirtualHostNameToFolderMapping(
                "slabmark.assets", _assetFolder, CoreWebView2HostResourceAccessKind.Allow);

            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDevToolsEnabled = true;

            core.NavigationCompleted += OnNavigationCompleted;
            core.WebMessageReceived += OnWebMessage;
            core.Navigate("https://slabmark.assets/slab_mark_report.html");
        }

        private async void OnNavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (_pushed || !e.IsSuccess) return;
            _pushed = true;
            if (!string.IsNullOrEmpty(_initJson))
                await _web.CoreWebView2.ExecuteScriptAsync("loadState(" + JsonUtil.Esc(_initJson) + ")");
        }

        private void OnWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            string msg = e.TryGetWebMessageAsString();
            if (msg == null) return;
            if (msg == "loaddxf:") { LoadDxfRequested(); return; }
        }

        /// <summary>
        /// Кнопка "📁 Загрузить DXF" внутри уже открытого окна — та же логика выбора/чтения
        /// файлов, что при первом запуске (см. SlabMarkCommand.Execute), но добавляет к уже
        /// загруженному в этой сессии, а не заменяет (см. window.addFiles в
        /// slab_mark_report.html — JS сам сливает новые файлы со старыми и пересчитывает всё
        /// заново). Никакого обращения к Document/Floor не нужно — выполняется прямо на
        /// UI-потоке окна, без моста.
        /// </summary>
        private async void LoadDxfRequested()
        {
            var notes = new List<string>();
            var files = SlabMarkDxfPicker.PickFiles(
                "DXF мозаики армирования (ЛИРА) — можно выбрать сразу несколько файлов", notes);
            if (files.Count == 0 && notes.Count == 0) return;

            string json = SlabMarkCommand.BuildFilesJson(files, notes);
            if (_web?.CoreWebView2 == null) return;
            try { await _web.CoreWebView2.ExecuteScriptAsync("addFiles(" + JsonUtil.Esc(json) + ")"); }
            catch { }
        }
    }
}
