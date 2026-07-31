using System;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using KzhNotes;

namespace RevitKJChecklist
{
    [Transaction(TransactionMode.Manual)]
    public class ChecklistCommand : IExternalCommand
    {
        // Логины Revit (Application.Username) пользователей с правами проверяющего —
        // читаются из файла на сервере (по одному логину на строку), чтобы список можно
        // было обновлять без пересборки и переустановки плагина. Все остальные открывают
        // чек-лист в режиме исполнителя (только чтение статусов).
        private const string ReviewerListPath =
            @"K:\04_Файлообменник\BIM\Levin Daniil\Проверяющие_NickName.txt";

        private static string[] ReadReviewerLogins()
        {
            try
            {
                if (!File.Exists(ReviewerListPath)) return Array.Empty<string>();
                return File.ReadAllLines(ReviewerListPath, Encoding.UTF8)
                    .Select(l => l.Trim())
                    .Where(l => l.Length > 0 && !l.StartsWith("//"))
                    .ToArray();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        private static ChecklistWindow _window;

        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            string pluginDir = AppDomain.CurrentDomain.GetData("DAN_PluginDir") as string;
            if (!string.IsNullOrEmpty(pluginDir))
                DependencyResolver.SetFolder(pluginDir);

            DependencyResolver.EnsureHooked();

            var uiapp = data.Application;
            var doc = uiapp.ActiveUIDocument?.Document;
            if (doc == null) { message = "Нет активного документа."; return Result.Failed; }

            if (_window != null && _window.IsLoaded)
            {
                _window.Activate();
                return Result.Succeeded;
            }

            string assetFolder = WebAssets.ResolveFolder();
            if (assetFolder == null)
            {
                message = "Не найден checklist.html. Положите web\\checklist.html рядом с DLL.";
                return Result.Failed;
            }

            var meta  = ModelReader.ReadProjectMeta(doc);
            var marks = ModelReader.ReadAssemblyMarks(doc);

            string initJson  = JsonUtil.BuildInit(meta, marks);
            string savePath  = GetSavePath(doc);
            string savedJson = LoadSaved(savePath);
            string user      = (uiapp.Application.Username ?? "").Trim();
            string role      = IsReviewer(user) ? "reviewer" : "executor";

            IntPtr revitHandle = uiapp.MainWindowHandle;

            _window = new ChecklistWindow(revitHandle, assetFolder, initJson, savedJson, savePath, role);
            _window.Closed += (s, e) => _window = null;
            _window.Show();

            return Result.Succeeded;
        }

        private static bool IsReviewer(string username) =>
            Array.Exists(ReadReviewerLogins(), r => string.Equals(r, username, StringComparison.OrdinalIgnoreCase));

        // internal (не private) — этот же файл/путь переиспользует RebarZonesCommand/
        // RebarZonesWindow (см. LiraToRevit.Rebar.RebarZonesDataStore) для своей секции
        // "rebar_zones" в том же JSON проекта, что чек-лист и примечания.
        internal static string GetSavePath(Document doc)
        {
            string path = GetDocumentPath(doc);
            if (!string.IsNullOrEmpty(path))
            {
                string dir  = Path.GetDirectoryName(path);
                string stem = Path.GetFileNameWithoutExtension(path);
                return Path.Combine(dir, stem + "_kzh.json");
            }
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "RevitKJChecklist", "unsaved.json");
        }

        // Для workshared-моделей возвращает путь к центральному файлу,
        // чтобы JSON сохранялся рядом с ним, а не рядом с локальной копией.
        private static string GetDocumentPath(Document doc)
        {
            if (doc.IsWorkshared)
            {
                try
                {
                    var mp = doc.GetWorksharingCentralModelPath();
                    string central = ModelPathUtils.ConvertModelPathToUserVisiblePath(mp);
                    // Используем только локальные / UNC пути (не BIM 360 / Revit Server)
                    if (!string.IsNullOrEmpty(central) &&
                        (central.Length > 1 && central[1] == ':' || central.StartsWith(@"\\")))
                        return central;
                }
                catch { }
            }
            return doc.PathName;
        }

        private static string LoadSaved(string savePath)
        {
            return ProjectDataStore.LoadSection(savePath, "checklist");
        }
    }
}
