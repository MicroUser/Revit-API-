// NotesCommand.cs
// Точка входа (кнопка ленты): открывает немодальное окно примечаний.
// Требует ссылки на RevitAPI.dll, RevitAPIUI.dll, PresentationFramework, WindowsBase, System.Xaml.

using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace KzhNotes
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public sealed class NotesCommand : IExternalCommand
    {
        // Одно окно на сессию (немодальное) — повторный вызов активирует существующее.
        private static NotesWindow _window;

        // Для workshared-моделей возвращает путь к центральному файлу.
        private static string GetDocumentPath(Document doc)
        {
            if (doc.IsWorkshared)
            {
                try
                {
                    var mp = doc.GetWorksharingCentralModelPath();
                    string central = ModelPathUtils.ConvertModelPathToUserVisiblePath(mp);
                    if (!string.IsNullOrEmpty(central) &&
                        (central.Length > 1 && central[1] == ':' || central.StartsWith(@"\\")))
                        return central;
                }
                catch { }
            }
            return doc.PathName;
        }

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (_window != null)
            {
                _window.Activate();
                return Result.Succeeded;
            }

            var bridge = new RevitEventBridge();
            bridge.Init();

            var doc = commandData.Application.ActiveUIDocument.Document;
            string docPath     = GetDocumentPath(doc);
            string projectDir  = !string.IsNullOrEmpty(docPath) ? Path.GetDirectoryName(docPath)              : null;
            string projectStem = !string.IsNullOrEmpty(docPath) ? Path.GetFileNameWithoutExtension(docPath) : null;

            _window = new NotesWindow(bridge, projectDir, projectStem);
            _window.Closed += (s, e) => _window = null;

            // Немодально + поверх окна Revit (Revit при этом остаётся интерактивным).
            var helper = new WindowInteropHelper(_window)
            {
                Owner = Process.GetCurrentProcess().MainWindowHandle
            };
            _window.Show();

            // Первичная загрузка выделения/снимка.
            _window.RequestRefreshSelection();
            return Result.Succeeded;
        }
    }
}
