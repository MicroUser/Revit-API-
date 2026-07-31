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
        private bool _closePending;

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

        /// <summary>
        /// Сохраняем полный снимок зон (см. window.getStateJSON) перед фактическим закрытием —
        /// по образцу ChecklistWindow.OnClosing. Save — обычный файл на диске (см.
        /// RebarZonesDataStore), но путь к нему и UniqueId плиты — Revit API, поэтому идём через
        /// _bridge (валидный контекст), а не читаем doc/floor напрямую из UI-потока окна.
        /// </summary>
        protected override async void OnClosing(CancelEventArgs e)
        {
            if (_closePending) { base.OnClosing(e); return; }
            e.Cancel = true;
            try
            {
                string zonesJson = await CollectStateAsync();
                if (!string.IsNullOrEmpty(zonesJson))
                    _bridge.Run(app => SaveZonesJson(app, zonesJson));
            }
            catch { }
            _closePending = true;
            Close();
        }

        private void SaveZonesJson(Autodesk.Revit.UI.UIApplication app, string zonesJson)
        {
            var doc = app.ActiveUIDocument.Document;
            var floor = doc.GetElement(_floorId) as Floor;
            if (floor == null) return;
            RebarZonesDataStore.SaveZonesJson(doc, floor, zonesJson);
        }

        private async Task<string> CollectStateAsync()
        {
            if (_web?.CoreWebView2 == null) return null;
            string raw = await _web.CoreWebView2.ExecuteScriptAsync("getStateJSON()");
            if (string.IsNullOrEmpty(raw) || raw == "null") return null;
            return JsonUtil.Unquote(raw);
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
            if (msg == null) return;
            if (msg.StartsWith("place:")) { PlaceZones(msg.Substring("place:".Length)); return; }
            if (msg.StartsWith("loaddxf:")) { LoadDxfRequested(msg.Substring("loaddxf:".Length)); return; }
        }

        /// <summary>
        /// Кнопка "📁 DXF" в редакторе — файл больше не выбирается при открытии плиты (см.
        /// RebarZonesCommand), а подгружается/заменяется по требованию отсюда. Сам диалог выбора
        /// файла и чтение DXF — чистый Win32/IO (см. RebarZonesDxfPicker), Revit API не нужен,
        /// поэтому выполняется прямо в обработчике сообщения (уже UI-поток окна), без моста;
        /// мост нужен только для сохранения путей и построения JSON зон (нужен Floor/Document).
        /// occupiedJson — [«top-x»,...] уже загруженные в ЭТОМ сеансе вкладки (из JS, а не заново
        /// из диска — сессия могла добавлять/менять вкладки, а не только то, что в кэше).
        /// </summary>
        private void LoadDxfRequested(string occupiedJson)
        {
            List<string> occupied;
            try
            {
                using (var doc = System.Text.Json.JsonDocument.Parse(occupiedJson))
                    occupied = doc.RootElement.EnumerateArray().Select(el => el.GetString()).ToList();
            }
            catch { occupied = new List<string>(); }

            var notes = new List<string>();
            var picked = RebarZonesDxfPicker.PickFiles(
                "DXF мозаики армирования (ЛИРА) — можно выбрать сразу несколько файлов (Верх/Низ × X/Y)",
                occupied, notes);

            if (picked.Count == 0)
            {
                if (notes.Count > 0) ReportToUser(string.Join(" · ", notes));
                return;
            }

            _bridge.Run(app =>
            {
                var doc = app.ActiveUIDocument.Document;
                var floor = doc.GetElement(_floorId) as Floor;
                if (floor == null)
                {
                    Dispatcher.Invoke(() => ReportToUser("Плита не найдена в модели — возможно, элемент был удалён."));
                    return;
                }

                RebarZonesDataStore.MergePaths(doc, floor,
                    picked.ToDictionary(d => RebarZonesDataStore.ComboKey(d.Face, d.Dir), d => d.Path));

                string addJson = RebarZonesCommand.BuildAddJson(
                    picked.Select(d => (d.Dxf, d.Face, d.Dir)).ToList(), floor, out var buildNotes);
                var allNotes = new List<string>(notes);
                allNotes.AddRange(buildNotes);

                Dispatcher.Invoke(() =>
                {
                    if (allNotes.Count > 0) ReportToUser(string.Join(" · ", allNotes));
                    SendAddDataset(addJson);
                });
            });
        }

        private async void SendAddDataset(string json)
        {
            if (_web?.CoreWebView2 == null) return;
            try { await _web.CoreWebView2.ExecuteScriptAsync("addDataset(" + JsonUtil.Esc(json) + ")"); }
            catch { }
        }

        private void PlaceZones(string payloadJson)
        {
            List<JsZone> jsZones;
            Face face; Dir dir;
            try
            {
                // Сообщение — {"face":"top"|"bottom","dir":"x"|"y","zones":[...]}: грань/направление
                // берём из САМОГО сообщения (текущая вкладка редактора на момент клика), а не из
                // initJson окна — при нескольких загруженных DXF на плиту (см. RebarZonesCommand)
                // окно уже не привязано к одной-единственной комбинации грань+направление.
                using (var doc = System.Text.Json.JsonDocument.Parse(payloadJson))
                {
                    var root = doc.RootElement;
                    face = root.GetProperty("face").GetString() == "bottom" ? Face.Bottom : Face.Top;
                    dir = root.GetProperty("dir").GetString() == "y" ? Dir.Y : Dir.X;
                    jsZones = JsZone.ParseArray(root.GetProperty("zones"));
                }
            }
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

            _bridge.Run(app =>
            {
                var doc = app.ActiveUIDocument.Document;
                var floor = doc.GetElement(_floorId) as Floor;
                string report;
                List<RebarZonesDataStore.PlacedRecord> justPlacedRecords = null;
                string comboKeyForMark = null;
                if (floor == null)
                {
                    report = "Плита не найдена в модели (Id=" + _floorId + ") — возможно, элемент был удалён.";
                }
                else
                {
                    // Зоны, для которых арматура уже реально создана — определяем по ГЕОМЕТРИИ
                    // (габарит рабочей зоны + диаметр + шаг), а не по id зоны: id в rebar_zones.html
                    // генерируется заново при каждом regenerate() и может ПЕРЕИСПОЛЬЗОВАТЬСЯ для
                    // другой зоны (например, если исходную удалить и создать новую в другом месте
                    // с тем же диаметром) — сверка по id в этом случае ошибочно сочла бы новую зону
                    // уже размещённой и молча пропустила бы её.
                    string comboKey = RebarZonesDataStore.ComboKey(face, dir);
                    comboKeyForMark = comboKey;
                    var placedHistory = RebarZonesDataStore.LoadPlaced(doc, floor);
                    var existing = placedHistory.TryGetValue(comboKey, out var existingList)
                        ? existingList : new List<RebarZonesDataStore.PlacedRecord>();

                    bool IsAlreadyPlaced(JsZone z)
                    {
                        const double tolMm = 50.0;
                        double x1 = z.Poly.Min(p => p.X) * 1000.0, x2 = z.Poly.Max(p => p.X) * 1000.0;
                        double y1 = z.Poly.Min(p => p.Y) * 1000.0, y2 = z.Poly.Max(p => p.Y) * 1000.0;
                        return existing.Any(r => r.D == z.Diameter && r.Step == z.Step
                            && Math.Abs(r.X1 - x1) <= tolMm && Math.Abs(r.X2 - x2) <= tolMm
                            && Math.Abs(r.Y1 - y1) <= tolMm && Math.Abs(r.Y2 - y2) <= tolMm);
                    }

                    var alreadyPlaced = jsZones.Where(IsAlreadyPlaced).Select(z => z.Id).ToList();
                    var freshJs = jsZones.Where(z => z.Diameter > 0 && !IsAlreadyPlaced(z)).ToList();
                    var skipped = jsZones.Where(z => z.Diameter <= 0 && !IsAlreadyPlaced(z)).Select(z => z.Id).ToList();
                    var zones = freshJs.Select(z => z.ToZoneDef(face, dir)).ToList();

                    if (zones.Count == 0)
                    {
                        report = alreadyPlaced.Count > 0
                            ? "Новых зон нет — все выбранные (" + alreadyPlaced.Count + ") уже были размещены ранее."
                            : "Все выбранные зоны — СПЕЦ (Aₛ выше шкалы диаметров), автоматически разместить нечем.";
                    }
                    else
                    {
                        // Фундаменты в Revit API — тоже класс Floor (другая категория,
                        // OST_StructuralFoundation), поэтому выбор элемента не менялся — но им
                        // нужны другой шаблон имени типа, другой рабочий набор и всегда прямые
                        // стержни (без Г/П-образного загиба у края).
                        bool isFoundation = floor.Category != null
                            && floor.Category.Id.IntValue() == (int)BuiltInCategory.OST_StructuralFoundation;
                        var settings = new PlacementSettings();
                        // Диаметр фоновой (основной) арматуры — из HTML (одно значение на всю
                        // партию зон в этом клике), а не жёстко зашитое значение по умолчанию.
                        settings.FirstLayerThickness = freshJs[0].BgD;
                        if (isFoundation)
                        {
                            settings.TypeNameTemplate = "(арматура)фундамент_доп_{F}{D}_d={d}_А500";
                            settings.WorksetName = ".#01_Арм_Фундаменты";
                            settings.AlwaysStraight = true;
                        }
                        var placer = new RebarPlacer(doc, settings);
                        PlacementResult res;
                        try { res = placer.Place(floor, zones); }
                        catch (Exception ex)
                        {
                            report = "Ошибка размещения: " + ex.Message;
                            Dispatcher.Invoke(() => ReportToUser(report));
                            return;
                        }

                        // Персистентная история размещения — в JSON проекта (см. RebarZonesDataStore),
                        // чтобы при следующем открытии этой же плиты (и при повторном клике в этом
                        // же сеансе — см. IsAlreadyPlaced выше) было видно, какая допка уже стоит.
                        // Обычный файл на диске, не элемент модели — транзакция не нужна.
                        var placedRecords = freshJs.Select(z => new RebarZonesDataStore.PlacedRecord
                        {
                            X1 = z.Poly.Min(p => p.X) * 1000.0,
                            Y1 = z.Poly.Min(p => p.Y) * 1000.0,
                            X2 = z.Poly.Max(p => p.X) * 1000.0,
                            Y2 = z.Poly.Max(p => p.Y) * 1000.0,
                            D = z.Diameter,
                            Step = z.Step
                        }).ToList();
                        RebarZonesDataStore.AppendPlaced(doc, floor, comboKey, placedRecords);
                        justPlacedRecords = placedRecords;

                        var sb = new StringBuilder();
                        sb.Append("Размещено стержней: ").Append(res.Bars.Count)
                          .Append(" (зон: ").Append(zones.Count).Append(')');
                        if (skipped.Count > 0)
                            sb.Append(". Пропущено СПЕЦ-зон: ").Append(skipped.Count).Append(" — ").Append(string.Join(", ", skipped));
                        if (alreadyPlaced.Count > 0)
                            sb.Append(". Уже было размещено ранее (пропущено): ").Append(alreadyPlaced.Count);
                        if (res.Errors.Count > 0)
                            sb.Append(". Ошибки: ").Append(string.Join("; ", res.Errors));
                        foreach (var w in res.LengthWarnings())
                            sb.Append(". ").Append(w);
                        report = sb.ToString();
                    }
                }
                Dispatcher.Invoke(() =>
                {
                    ReportToUser(report);
                    if (justPlacedRecords != null && justPlacedRecords.Count > 0)
                        MarkPlacedAndSave(comboKeyForMark, justPlacedRecords);
                });
            });
        }

        private async void ReportToUser(string text)
        {
            if (_web?.CoreWebView2 == null) return;
            try { await _web.CoreWebView2.ExecuteScriptAsync("toast(" + JsonUtil.Esc(text) + ")"); }
            catch { }
        }

        /// <summary>Сообщает JS, какие зоны реально размещены — см. window.markPlaced в
        /// rebar_zones.html (сверяет по габариту+диаметру+шагу — как и applyPlacedHistory —
        /// и проставляет z.placed, чтобы зона визуально отмечалась и не отправлялась повторно,
        /// пока не будет отредактирована). По id НЕ сверяем — см. IsAlreadyPlaced в PlaceZones.</summary>
        private async Task MarkPlacedAsync(string comboKey, List<RebarZonesDataStore.PlacedRecord> records)
        {
            if (_web?.CoreWebView2 == null) return;
            try
            {
                string recordsJson = "[" + string.Join(",", records.Select(r =>
                    "{\"x1\":" + (r.X1 / 1000.0).ToString(System.Globalization.CultureInfo.InvariantCulture) +
                    ",\"y1\":" + (r.Y1 / 1000.0).ToString(System.Globalization.CultureInfo.InvariantCulture) +
                    ",\"x2\":" + (r.X2 / 1000.0).ToString(System.Globalization.CultureInfo.InvariantCulture) +
                    ",\"y2\":" + (r.Y2 / 1000.0).ToString(System.Globalization.CultureInfo.InvariantCulture) +
                    ",\"d\":" + r.D + ",\"step\":" + r.Step + "}")) + "]";
                await _web.CoreWebView2.ExecuteScriptAsync(
                    "markPlaced(" + JsonUtil.Esc(comboKey) + "," + recordsJson + ")");
            }
            catch { }
        }

        /// <summary>После размещения — сразу сохраняем полный снимок зон (не только на закрытии
        /// окна), чтобы отметка "размещена" не терялась, если Revit закроется без штатного
        /// закрытия этого окна. Ждём markPlaced (см. выше), иначе снимок соберётся ДО того, как
        /// в JS проставится z.placed, и уйдёт без свежих отметок.</summary>
        private async void MarkPlacedAndSave(string comboKey, List<RebarZonesDataStore.PlacedRecord> records)
        {
            await MarkPlacedAsync(comboKey, records);
            string zonesJson = await CollectStateAsync();
            if (!string.IsNullOrEmpty(zonesJson))
                _bridge.Run(app => SaveZonesJson(app, zonesJson));
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
        public double BgD;   // диаметр фоновой (основной) арматуры, мм — см. PlacementSettings.FirstLayerThickness
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
        /// Принимает уже разобранный JsonElement (массив) — вызывающий код (PlaceZones) сначала
        /// разбирает внешний конверт {"face":...,"dir":...,"zones":[...]}.
        /// </summary>
        public static System.Collections.Generic.List<JsZone> ParseArray(System.Text.Json.JsonElement arr)
        {
            var result = new System.Collections.Generic.List<JsZone>();
            foreach (var el in arr.EnumerateArray())
            {
                var z = new JsZone
                {
                    Id = el.GetProperty("id").GetString(),
                    Diameter = el.GetProperty("d").GetInt32(),
                    Step = el.GetProperty("step").GetInt32(),
                    Avg = el.GetProperty("avg").GetBoolean(),
                    Am = el.GetProperty("am").GetDouble(),
                    How = el.TryGetProperty("how", out var howEl) ? howEl.GetString() : "расчёт",
                    BgD = el.TryGetProperty("bgD", out var bgDEl) ? bgDEl.GetDouble() : 10.0,
                    Poly = new System.Collections.Generic.List<(double, double)>()
                };
                foreach (var pt in el.GetProperty("poly").EnumerateArray())
                {
                    var arr2 = pt.EnumerateArray().ToArray();
                    z.Poly.Add((arr2[0].GetDouble(), arr2[1].GetDouble()));
                }
                result.Add(z);
            }
            return result;
        }
    }
}
