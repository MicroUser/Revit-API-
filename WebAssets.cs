using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace RevitKJChecklist
{
    /// <summary>
    /// Находит папку с checklist.html.
    /// Приоритет: (1) файл на диске рядом с DLL — удобно при разработке;
    /// (2) встроенный в DLL ресурс — распаковывается автоматически, ничего класть не надо.
    /// </summary>
    internal static class WebAssets
    {
        public static string ResolveFolder()
        {
            // (1) файл на диске
            foreach (var dir in CandidateDirs())
            {
                if (string.IsNullOrEmpty(dir)) continue;
                var web = Path.Combine(dir, "web");
                if (File.Exists(Path.Combine(web, "checklist.html"))) return web;
                if (File.Exists(Path.Combine(dir, "checklist.html"))) return dir;
            }

            // (2) встроенный ресурс -> распаковать в LocalAppData
            return ExtractEmbedded();
        }

        private static IEnumerable<string> CandidateDirs()
        {
            yield return DependencyResolver.OriginalFolder;   // исходная папка сборки
            string loc = null;
            try { loc = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location); } catch { }
            yield return loc;                                 // теневая копия — на всякий
        }

        // Достаёт вшитый checklist.html и кладёт в %LOCALAPPDATA%\RevitKJChecklist\web\.
        // Перезаписывает при каждом запуске, чтобы копия соответствовала текущей DLL.
        private static string ExtractEmbedded()
        {
            var asm = Assembly.GetExecutingAssembly();
            string resName = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("checklist.html", StringComparison.OrdinalIgnoreCase));
            if (resName == null) return null;   // ресурс не встроен

            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RevitKJChecklist", "web");
            Directory.CreateDirectory(dir);
            string dest = Path.Combine(dir, "checklist.html");

            try
            {
                using (var s = asm.GetManifestResourceStream(resName))
                using (var f = File.Create(dest))
                    s.CopyTo(f);
            }
            catch
            {
                // не смогли перезаписать (например, файл занят) — используем то, что уже есть
                if (!File.Exists(dest)) return null;
            }
            return dir;
        }
    }
}