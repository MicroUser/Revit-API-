using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LiraToRevit.Rebar;

namespace DAN_Plugin
{
    /// <summary>
    /// Пакетный анализатор марок допармирования плит — точка входа. Без кнопки на ленте,
    /// запуск только через Add-In Manager (по образцу ImportDxfArmoringTest в
    /// RebarZones/DxfArmoringImport.cs — тот же минимальный рецепт: [Transaction], без
    /// [Regeneration], без записи в Loader.cs/.addin манифест). Работает ТОЛЬКО с DXF-файлами,
    /// без выбора элементов в Revit: инженер пачкой выбирает экспорты ЛИРА по всем плитам/
    /// отметкам здания, инструмент сам разносит их по высоте (из Z вершин 3DFACE — см.
    /// DxfCell.Polygon, уже парсится, просто не использовался) и в JS-отчёте предлагает
    /// группировку по маркам. Вся аналитика — в slab_mark_report.html; C# только читает файлы
    /// и передаёт сырые данные (см. SlabMarkWindow).
    /// Manual, а не ReadOnly (хотя команда и не трогает документ): Add-in Manager сверяет
    /// TransactionMode по всей сборке разом, а весь остальной код в проекте — Manual, кроме
    /// служебного ImportDxfArmoringTest — смешение вызывало отказ Add-in Manager запускать
    /// команду ("...are not the same as the mode set to Add-In Manager").
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class SlabMarkCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            string assetFolder = AssetFolder();
            if (assetFolder == null)
            {
                TaskDialog.Show("Ошибка", "Не найден RebarZones\\slab_mark_report.html рядом с плагином.");
                return Result.Failed;
            }

            var notes = new List<string>();
            var files = SlabMarkDxfPicker.PickFiles(
                "DXF мозаики армирования (ЛИРА) — выберите пачкой все плиты по всем отметкам", notes);
            if (files.Count == 0)
            {
                if (notes.Count > 0) TaskDialog.Show("Анализ марок", string.Join("\n", notes));
                return Result.Cancelled;
            }

            string json = BuildFilesJson(files, notes);

            var uiApp = commandData.Application;
            var win = new SlabMarkWindow(uiApp.MainWindowHandle, assetFolder, json);
            win.Show();

            return Result.Succeeded;
        }

        private static double M(double ft) => UnitUtils.ConvertFromInternalUnits(ft, UnitTypeId.Meters);

        /// <summary>
        /// Сырая передача данных в JS — C# ничего аналитического не считает (ни отметку, ни
        /// взвешенное As, ни кластеризацию): координаты ячеек идут КАК ЕСТЬ из DXF, включая Z
        /// (в отличие от RebarZonesCommand.BuildDatasetsPayload, где Z сейчас отбрасывается за
        /// ненадобностью) — вся аналитика на JS берёт отметку из Z сама. Координаты не
        /// привязываются к какой-либо Revit-плите (её тут нет вообще) — предполагается, что вся
        /// загруженная пачка из одной модели ЛИРА одного здания, поэтому координаты разных
        /// файлов напрямую сопоставимы. internal — переиспользуется SlabMarkWindow для кнопки
        /// "📁 Загрузить DXF" внутри уже открытого окна (та же форма JSON что для первого
        /// открытия — {files:[...],notes:[...]} — JS сам решает, заменить или добавить к уже
        /// загруженному, см. loadState/addFiles в slab_mark_report.html).
        /// </summary>
        internal static string BuildFilesJson(List<SlabMarkFile> files, List<string> notes)
        {
            var payload = new
            {
                files = files.Select(f => new
                {
                    fileName = f.FileName,
                    comboKey = RebarZonesDataStore.ComboKey(f.Face, f.Dir),
                    faceDirSource = f.FaceDirSource,
                    cells = f.Dxf.Cells.Select(c => new
                    {
                        p = c.Polygon.Select(pt => new[]
                        {
                            Math.Round(M(pt.X), 4), Math.Round(M(pt.Y), 4), Math.Round(M(pt.Z), 4)
                        }).ToArray(),
                        a = c.As,
                        hasValue = c.HasValue
                    }).ToArray()
                }).ToArray(),
                notes
            };
            return JsonSerializer.Serialize(payload);
        }

        private static string AssetFolder()
        {
            string dir = RevitKJChecklist.DependencyResolver.OriginalFolder;
            if (!string.IsNullOrEmpty(dir))
            {
                var candidate = Path.Combine(dir, "RebarZones");
                if (File.Exists(Path.Combine(candidate, "slab_mark_report.html"))) return candidate;
            }
            try
            {
                string loc = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                var candidate = Path.Combine(loc ?? "", "RebarZones");
                if (File.Exists(Path.Combine(candidate, "slab_mark_report.html"))) return candidate;
            }
            catch { }
            return null;
        }
    }
}
