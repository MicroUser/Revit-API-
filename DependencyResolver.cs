using System;
using System.IO;
using System.Reflection;

namespace RevitKJChecklist
{
    internal static class DependencyResolver
    {
        private static bool _hooked;
        private static string _probeDir;

        public static void EnsureHooked()
        {
            if (_hooked) return;
            _hooked = true;
            if (_probeDir == null) _probeDir = OriginalDir(); // SetFolder() мог уже задать путь

            AppDomain.CurrentDomain.AssemblyResolve += (s, a) =>
            {
                try
                {
                    if (_probeDir == null) return null;
                    var file = new AssemblyName(a.Name).Name + ".dll";
                    var path = Path.Combine(_probeDir, file);
                    if (File.Exists(path)) return Assembly.LoadFrom(path);
                }
                catch { }
                return null;
            };
        }

        public static string OriginalFolder => _probeDir ?? (_probeDir = OriginalDir());

        public static void SetFolder(string dir)
        {
            if (!string.IsNullOrEmpty(dir)) _probeDir = dir;
        }

        private static string OriginalDir()
        {
            // Loader.dll всегда в реальной папке плагина и никогда не shadow-copy
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm.GetName().Name != "Loader") continue;
                    string loc = asm.Location;
                    if (!string.IsNullOrEmpty(loc))
                        return Path.GetDirectoryName(loc);
                }
            }
            catch { }
            // fallback: CodeBase текущей сборки (может быть temp при shadow-copy)
            try
            {
                var codebase = Assembly.GetExecutingAssembly().CodeBase;
                if (!string.IsNullOrEmpty(codebase))
                    return Path.GetDirectoryName(new Uri(codebase).LocalPath);
            }
            catch { }
            try { return Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location); }
            catch { return null; }
        }
    }
}
