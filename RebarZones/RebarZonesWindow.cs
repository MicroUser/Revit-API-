using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using KzhNotes;
using RevitKJChecklist;

namespace LiraToRevit.Rebar
{
    /// <summary>
    /// Окно подтверждения зон допармирования: хостит HTML/JS-редактор (rebar_zones.html)
    /// через WebView2 — по образцу ChecklistWindow. C# передаёт ячейки из DXF в JS (loadState),
    /// JS присылает обратно подтверждённые зоны (postMessage "place:...") для размещения арматуры.
    /// </summary>
    public class RebarZonesWindow : Window
    {
        private readonly WebView2 _web;
        private readonly string _assetFolder;
        private readonly string _initJson;
        private readonly RevitEventBridge _bridge;
        private readonly ElementId _floorId;
        private bool _pushed;

        public RebarZonesWindow(IntPtr revitHandle, string assetFolder, string initJson,
                                 RevitEventBridge bridge, ElementId floorId)
        {
            _assetFolder = assetFolder;
            _initJson = initJson;
            _bridge = bridge;
            _floorId = floorId;

            Title = "Допармирование — подтверждение зон";
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

        private async Task InitAsync()
        {
            string userData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LiraToRevit", "WebView2");
            Directory.CreateDirectory(userData);

            var env = await CoreWebView2Environment.CreateAsync(null, userData, null);
            await _web.EnsureCoreWebView2Async(env);

            var core = _web.CoreWebView2;
            core.SetVirtualHostNameToFolderMapping(
                "rebarzones.assets", _assetFolder, CoreWebView2HostResourceAccessKind.Allow);

            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDevToolsEnabled = true;

            core.NavigationCompleted += OnNavigationCompleted;
            core.WebMessageReceived += OnWebMessage;
            core.Navigate("https://rebarzones.assets/rebar_zones.html");
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
            if (msg == null || !msg.StartsWith("place:")) return;
            string zonesJson = msg.Substring("place:".Length);
            PlaceZones(zonesJson);
        }

        private void PlaceZones(string zonesJson)
        {
            List<JsZone> jsZones;
            try { jsZones = JsZone.ParseArray(zonesJson); }
            catch (Exception ex)
            {
                ReportToUser("Не удалось разобрать зоны: " + ex.Message);
                return;
            }
            if (jsZones.Count == 0)
            {
                ReportToUser("Нет подтверждённых зон — нечего размещать.");
                return;
            }

            var faceDir = ContextOf(_initJson);

            _bridge.Run(app =>
            {
                var doc = app.ActiveUIDocument.Document;
                var floor = doc.GetElement(_floorId) as Floor;
                string report;
                if (floor == null)
                {
                    report = "Плита не найдена в модели (Id=" + _floorId + ") — возможно, элемент был удалён.";
                }
                else
                {
                    var skipped = jsZones.Where(z => z.Diameter <= 0).Select(z => z.Id).ToList();
                    var zones = jsZones.Where(z => z.Diameter > 0)
                        .Select(z => z.ToZoneDef(faceDir.Face, faceDir.Dir)).ToList();

                    if (zones.Count == 0)
                    {
                        report = "Все выбранные зоны — СПЕЦ (Aₛ выше шкалы диаметров), автоматически разместить нечем.";
                    }
                    else
                    {
                        var placer = new RebarPlacer(doc, new PlacementSettings());
                        PlacementResult res;
                        try { res = placer.Place(floor, zones); }
                        catch (Exception ex)
                        {
                            report = "Ошибка размещения: " + ex.Message;
                            Dispatcher.Invoke(() => ReportToUser(report));
                            return;
                        }

                        var sb = new StringBuilder();
                        sb.Append("Размещено стержней: ").Append(res.Bars.Count)
                          .Append(" (зон: ").Append(zones.Count).Append(')');
                        if (skipped.Count > 0)
                            sb.Append(". Пропущено СПЕЦ-зон: ").Append(skipped.Count).Append(" — ").Append(string.Join(", ", skipped));
                        if (res.Errors.Count > 0)
                            sb.Append(". Ошибки: ").Append(string.Join("; ", res.Errors));
                        foreach (var w in res.LengthWarnings())
                            sb.Append(". ").Append(w);
                        report = sb.ToString();
                    }
                }
                Dispatcher.Invoke(() => ReportToUser(report));
            });
        }

        private async void ReportToUser(string text)
        {
            if (_web?.CoreWebView2 == null) return;
            try { await _web.CoreWebView2.ExecuteScriptAsync("toast(" + JsonUtil.Esc(text) + ")"); }
            catch { }
        }

        private static (Face Face, Dir Dir) ContextOf(string initJson)
        {
            bool bottom = initJson != null && initJson.Contains("\"face\":\"bottom\"");
            bool dirY = initJson != null && initJson.Contains("\"dir\":\"y\"");
            return (bottom ? Face.Bottom : Face.Top, dirY ? Dir.Y : Dir.X);
        }
    }

    /// <summary>Зона, полученная от JS ("place:" сообщение) — минимальный ручной парсер JSON-массива.</summary>
    internal sealed class JsZone
    {
        public string Id;
        public int Diameter;   // 0 = СПЕЦ (k===8), автоматически не размещается
        public int Step;
        public bool Avg;
        public double Am;
        public string How;
        public System.Collections.Generic.List<(double X, double Y)> Poly;

        public ZoneDef ToZoneDef(Face face, Dir dir)
        {
            return new ZoneDef
            {
                Id = Id,
                Face = face,
                Dir = dir,
                Diameter = Diameter,
                Step = Step,
                ByMean = Avg,
                AsCalc = Am,
                Source = How,
                Accepted = true,
                Polygon = Poly.Select(p => new XYZ(
                    UnitUtils.ConvertToInternalUnits(p.X, UnitTypeId.Meters),
                    UnitUtils.ConvertToInternalUnits(p.Y, UnitTypeId.Meters),
                    0)).ToList()
            };
        }

        /// <summary>
        /// Разбирает JSON-массив вида [{"id":"..","k":1,"d":10,"step":200,"avg":false,"am":4.4,
        /// "how":"расчёт","poly":[[x,y],...]},...] без сторонних библиотек (System.Text.Json
        /// не даёт удобного динамического доступа без DTO, а плоский формат здесь проще руками).
        /// </summary>
        public static System.Collections.Generic.List<JsZone> ParseArray(string json)
        {
            using (var doc = System.Text.Json.JsonDocument.Parse(json))
            {
                var result = new System.Collections.Generic.List<JsZone>();
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    var z = new JsZone
                    {
                        Id = el.GetProperty("id").GetString(),
                        Diameter = el.GetProperty("d").GetInt32(),
                        Step = el.GetProperty("step").GetInt32(),
                        Avg = el.GetProperty("avg").GetBoolean(),
                        Am = el.GetProperty("am").GetDouble(),
                        How = el.TryGetProperty("how", out var howEl) ? howEl.GetString() : "расчёт",
                        Poly = new System.Collections.Generic.List<(double, double)>()
                    };
                    foreach (var pt in el.GetProperty("poly").EnumerateArray())
                    {
                        var arr = pt.EnumerateArray().ToArray();
                        z.Poly.Add((arr[0].GetDouble(), arr[1].GetDouble()));
                    }
                    result.Add(z);
                }
                return result;
            }
        }
    }
}
