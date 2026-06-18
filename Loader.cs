using System;
using System.IO;
using System.Reflection;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace MyPlugin.Loader
{
    public class LoaderApp : IExternalApplication
    {
        internal static string PluginDir;

        public Result OnStartup(UIControlledApplication a)
        {
            PluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            CleanOldTempFiles();
            SetupRibbon(a);
            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication a) => Result.Succeeded;

        private static void SetupRibbon(UIControlledApplication a)
        {
            try { a.CreateRibbonTab("DAN"); } catch { }

            RibbonPanel panel = a.CreateRibbonPanel("DAN", "КЖ");
            string path = Assembly.GetExecutingAssembly().Location;

            panel.AddItem(new PushButtonData(
                "ScheduleMarking", "Спецификация\nкаркасов", path,
                "MyPlugin.Loader.ProxyScheduleMarking")
            { LongDescription = "Код ищет в именах листов марки конструкций и вписывает их сборкам." });

            panel.AddSeparator();

            panel.AddItem(new PushButtonData(
                "CreateRebarAnnotation", "Аннотация\nдоп.арм", path,
                "MyPlugin.Loader.ProxyCreateRebarAnnotation")
            { LongDescription = "Создаёт аннотацию для дополнительной арматуры плит по центру." });

            panel.AddSeparator();

            panel.AddItem(new PushButtonData(
                "CreatElevationTags", "Опалубка\nстен", path,
                "MyPlugin.Loader.ProxyCreatElevationTags")
            { LongDescription = "Создаёт высотные отметки и размеры." });

            panel.AddSeparator();
        }

        private static void CleanOldTempFiles()
        {
            try
            {
                foreach (string f in Directory.GetFiles(Path.GetTempPath(), "DAN_Plugin_*.dll"))
                    try { File.Delete(f); } catch { }
            }
            catch { }
        }
    }

    // Загружает свежую копию DAN_Plugin.dll при каждом вызове команды —
    // оригинал никогда не блокируется, изменения подхватываются немедленно.
    static class HotLoader
    {
        public static Result Run(string typeName, ExternalCommandData cd, ref string msg, ElementSet els)
        {
            string src = Path.Combine(LoaderApp.PluginDir, "DAN_Plugin.dll");
            string tmp = Path.Combine(Path.GetTempPath(), $"DAN_Plugin_{Guid.NewGuid():N}.dll");
            File.Copy(src, tmp, true);

            AppDomain.CurrentDomain.AssemblyResolve += Resolve;
            try
            {
                Assembly asm = Assembly.LoadFrom(tmp);
                Type t = asm.GetType(typeName)
                    ?? throw new InvalidOperationException($"Тип '{typeName}' не найден в {src}");
                IExternalCommand cmd = (IExternalCommand)Activator.CreateInstance(t);
                return cmd.Execute(cd, ref msg, els);
            }
            finally
            {
                AppDomain.CurrentDomain.AssemblyResolve -= Resolve;
            }
        }

        private static Assembly Resolve(object sender, ResolveEventArgs args)
        {
            string path = Path.Combine(LoaderApp.PluginDir,
                new AssemblyName(args.Name).Name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        }
    }

    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class ProxyScheduleMarking : IExternalCommand
    {
        public Result Execute(ExternalCommandData cd, ref string msg, ElementSet els)
            => HotLoader.Run("DAN_Plugin.ScheduleMarking", cd, ref msg, els);
    }

    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class ProxyCreateRebarAnnotation : IExternalCommand
    {
        public Result Execute(ExternalCommandData cd, ref string msg, ElementSet els)
            => HotLoader.Run("CreateRebarAnnotation", cd, ref msg, els);
    }

    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class ProxyCreatElevationTags : IExternalCommand
    {
        public Result Execute(ExternalCommandData cd, ref string msg, ElementSet els)
            => HotLoader.Run("DAN_Plugin.CreatElevationTags", cd, ref msg, els);
    }
}
