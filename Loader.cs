using System;
using System.IO;
using System.Reflection;
using System.Windows.Media.Imaging;
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

            var btnSchedule = (PushButton)panel.AddItem(new PushButtonData(
                "ScheduleMarking", "Спецификация\nкаркасов", path,
                "MyPlugin.Loader.ProxyScheduleMarking")
            { LongDescription = "Код ищет в именах листов марки конструкций и вписывает их сборкам." });
            btnSchedule.LargeImage = LoadIcon("cells.png");
            btnSchedule.Image      = LoadIcon("cells.png");

            panel.AddSeparator();

            var btnAnnot = (PushButton)panel.AddItem(new PushButtonData(
                "CreateRebarAnnotation", "Аннотация\nдоп.арм", path,
                "MyPlugin.Loader.ProxyCreateRebarAnnotation")
            { LongDescription = "Создаёт аннотацию для дополнительной арматуры плит по центру." });
            btnAnnot.LargeImage = LoadIcon("slab_32.png");
            btnAnnot.Image      = LoadIcon("slab_16.png");

            panel.AddSeparator();

            var btnElev = (PushButton)panel.AddItem(new PushButtonData(
                "CreatElevationTags", "Опалубка\nстен", path,
                "MyPlugin.Loader.ProxyCreatElevationTags")
            { LongDescription = "Создаёт высотные отметки и размеры." });
            btnElev.LargeImage = LoadIcon("dimension_32.png");
            btnElev.Image      = LoadIcon("dimension_16.png");

            var btnWallAnnot = (PushButton)panel.AddItem(new PushButtonData(
                "WallRebarAnnotation", "Аннотация\nарм. стен", path,
                "MyPlugin.Loader.ProxyWallRebarAnnotation")
            { LongDescription = "Создаёт аннотацию горизонтальной арматуры и П-шек для крайней стены сборки на каждом уровне." });
            btnWallAnnot.LargeImage = LoadIcon("pencil_32.png");
            btnWallAnnot.Image      = LoadIcon("pencil_16.png");

            panel.AddSeparator();

            var btnChecklist = (PushButton)panel.AddItem(new PushButtonData(
                "ChecklistCommand", "Чек-лист\nКЖ", path,
                "MyPlugin.Loader.ProxyChecklistCommand")
            { LongDescription = "Открывает чек-лист КЖ с автозаполнением из модели." });
            btnChecklist.LargeImage = LoadIcon("Annotation_32.png");
            btnChecklist.Image      = LoadIcon("Annotation_16.png");
        }

        private static BitmapImage LoadIcon(string fileName)
        {
            try
            {
                string resName = $"MyPlugin.Loader.Resources.{fileName}";
                using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(resName))
                {
                    if (s == null) return null;
                    var img = new BitmapImage();
                    img.BeginInit();
                    img.StreamSource = s;
                    img.CacheOption  = BitmapCacheOption.OnLoad;
                    img.EndInit();
                    img.Freeze();
                    return img;
                }
            }
            catch { return null; }
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

            // Передаём путь к папке плагина в DAN_Plugin через AppDomain
            // (сборка грузится из temp, поэтому Assembly.Location там не поможет)
            AppDomain.CurrentDomain.SetData("DAN_PluginDir", LoaderApp.PluginDir);

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

    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class ProxyWallRebarAnnotation : IExternalCommand
    {
        public Result Execute(ExternalCommandData cd, ref string msg, ElementSet els)
            => HotLoader.Run("WallRebarAnnotation", cd, ref msg, els);
    }

    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class ProxyChecklistCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData cd, ref string msg, ElementSet els)
            => HotLoader.Run("RevitKJChecklist.ChecklistCommand", cd, ref msg, els);
    }
}
