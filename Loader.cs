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

        // Название вкладки ленты Revit — "DAN" по умолчанию. Сборка с constant LD_BRAND (см.
        // Loader.csproj/Loader2026.csproj, конфигурация Release-LD, и
        // Installer/DAN_Plugin_Combined_LD.iss) показывает "LD" вместо этого — та же сборка,
        // тот же путь установки/manifest/DAN_Plugin.dll, меняется только этот видимый ярлык.
#if LD_BRAND
        private const string RibbonTabName = "LD";
#else
        private const string RibbonTabName = "DAN";
#endif

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
            try { a.CreateRibbonTab(RibbonTabName); } catch { }

            RibbonPanel panel = a.CreateRibbonPanel(RibbonTabName, "КЖ");
            string path = Assembly.GetExecutingAssembly().Location;

            var btnSchedule = (PushButton)panel.AddItem(new PushButtonData(
                "ScheduleMarking", "Спецификация\nкаркасов", path,
                "MyPlugin.Loader.ProxyScheduleMarking")
            { LongDescription = "Код ищет в именах листов марки конструкций и вписывает их сборкам." });
            btnSchedule.LargeImage = LoadIcon("cells.png");
            btnSchedule.Image      = LoadIcon("cells.png");
            btnSchedule.SetContextualHelp(new ContextualHelp(ContextualHelpType.Url, "https://youtu.be/EMZZ8qeVNzg"));

            panel.AddSeparator();

            // Допармирование плит (импорт DXF мозаики ЛИРА → зоны → арматура) + Аннотация доп.арм
            // — стакованная пара (одна колонка): сначала размещают допку, потом её аннотируют.
            var stackSlabRebar = panel.AddStackedItems(
                new PushButtonData("RebarZonesCommand", "Доп. армирование плит", path,
                    "MyPlugin.Loader.ProxyRebarZonesCommand")
                { LongDescription = "Импорт DXF мозаики армирования ЛИРА, подтверждение зон в редакторе и размещение дополнительной арматуры плит/фундаментов." },
                new PushButtonData("CreateRebarAnnotation", "Аннотация\nдоп.арм", path,
                    "MyPlugin.Loader.ProxyCreateRebarAnnotation")
                { LongDescription = "Создаёт аннотацию для дополнительной арматуры плит по центру." });
            var btnRebarZones = (PushButton)stackSlabRebar[0];
            btnRebarZones.LargeImage = LoadIcon("steel-mesh_32.png");
            btnRebarZones.Image      = LoadIcon("steel-mesh_16.png");
            btnRebarZones.SetContextualHelp(new ContextualHelp(ContextualHelpType.Url, "https://youtu.be/jdluFJ9SjIM"));
            var btnAnnot = (PushButton)stackSlabRebar[1];
            btnAnnot.LargeImage = LoadIcon("slab_32.png");
            btnAnnot.Image      = LoadIcon("slab_16.png");
            btnAnnot.SetContextualHelp(new ContextualHelp(ContextualHelpType.Url, "https://youtu.be/Dywl_Y47nVY"));

            panel.AddSeparator();

            // Опалубка стен + Аннотация арм. стен — стакованная пара (одна колонка)
            var stackWalls = panel.AddStackedItems(
                new PushButtonData("CreatElevationTags", "Опалубка стен", path,
                    "MyPlugin.Loader.ProxyCreatElevationTags")
                { LongDescription = "Создаёт высотные отметки и размеры." },
                new PushButtonData("WallRebarAnnotation", "Аннотация арм. стен", path,
                    "MyPlugin.Loader.ProxyWallRebarAnnotation")
                { LongDescription = "Создаёт аннотацию горизонтальной арматуры и П-шек для крайней стены сборки на каждом уровне." });
            var btnElev = (PushButton)stackWalls[0];
            btnElev.LargeImage = LoadIcon("dimension_32.png");
            btnElev.Image      = LoadIcon("dimension_16.png");
            btnElev.SetContextualHelp(new ContextualHelp(ContextualHelpType.Url, "https://youtu.be/1MxJJG7TpuI"));
            var btnWallAnnot = (PushButton)stackWalls[1];
            btnWallAnnot.LargeImage = LoadIcon("Annotation_32.png");
            btnWallAnnot.Image      = LoadIcon("Annotation_16.png");
            btnWallAnnot.SetContextualHelp(new ContextualHelp(ContextualHelpType.Url, "https://youtu.be/CH6c1r1kSNc"));

            panel.AddSeparator();

            // Чек-лист КЖ + Примечания КЖ — стакованная пара (одна колонка)
            var stackDocs = panel.AddStackedItems(
                new PushButtonData("ChecklistCommand", "Чек-лист КЖ", path,
                    "MyPlugin.Loader.ProxyChecklistCommand")
                { LongDescription = "Открывает чек-лист КЖ с автозаполнением из модели." },
                new PushButtonData("NotesCommand", "Примечания КЖ", path,
                    "MyPlugin.Loader.ProxyNotesCommand")
                { LongDescription = "Открывает редактор примечаний КЖ для выбранных листов." });
            var btnChecklist = (PushButton)stackDocs[0];
            btnChecklist.LargeImage = LoadIcon("checklist_32.png");
            btnChecklist.Image      = LoadIcon("checklist_16.png");
            btnChecklist.SetContextualHelp(new ContextualHelp(ContextualHelpType.Url, "https://youtu.be/tN2wmnG9PfM"));
            var btnNotes = (PushButton)stackDocs[1];
            btnNotes.LargeImage = LoadIcon("pencil_32.png");
            btnNotes.Image      = LoadIcon("pencil_16.png");
            btnNotes.SetContextualHelp(new ContextualHelp(ContextualHelpType.Url, "https://youtu.be/7AG21HpX1FY"));
        }

        private static BitmapSource LoadIcon(string fileName)
        {
            try
            {
                string resName = "MyPlugin.Loader.Resources." + fileName;
                using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(resName))
                {
                    if (s == null) return null;
                    var frame = BitmapFrame.Create(s, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                    // PNG-файлы имеют нестандартный DPI (~3–48 вместо 96).
                    // WPF считает логический размер как pixels*96/dpi, поэтому 32px @ 6dpi = 512 логических px.
                    // Пересоздаём BitmapSource с принудительным 96 DPI.
                    int stride = (frame.PixelWidth * frame.Format.BitsPerPixel + 7) / 8;
                    byte[] pixels = new byte[stride * frame.PixelHeight];
                    frame.CopyPixels(pixels, stride, 0);
                    var img = BitmapSource.Create(
                        frame.PixelWidth, frame.PixelHeight,
                        96, 96,
                        frame.Format,
                        frame.Palette,
                        pixels, stride);
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

            // WPF резолвит pack-URI ресурсов ("/DAN_Plugin;component/...") по ИМЕНИ сборки
            // через отдельный от LoadFrom контекст загрузки. Если в этот момент отдать
            // AssemblyResolve другой физической копии DAN_Plugin.dll (из папки плагина),
            // получаем два разных объекта Assembly с одинаковым именем ("version conflict")
            // и XamlParseException — ресурс ищут не в том экземпляре, что выполняется.
            // Поэтому для самого DAN_Plugin возвращаем именно уже загруженный экземпляр.
            Assembly loaded = null;
            ResolveEventHandler resolve = (s, args) =>
            {
                string name = new AssemblyName(args.Name).Name;
                if (name == "DAN_Plugin" && loaded != null) return loaded;

                string path = Path.Combine(LoaderApp.PluginDir, name + ".dll");
                return File.Exists(path) ? Assembly.LoadFrom(path) : null;
            };

            AppDomain.CurrentDomain.AssemblyResolve += resolve;
            try
            {
                loaded = Assembly.LoadFrom(tmp);
                Type t = loaded.GetType(typeName)
                    ?? throw new InvalidOperationException($"Тип '{typeName}' не найден в {src}");
                IExternalCommand cmd = (IExternalCommand)Activator.CreateInstance(t);
                return cmd.Execute(cd, ref msg, els);
            }
            finally
            {
                AppDomain.CurrentDomain.AssemblyResolve -= resolve;
            }
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
    public class ProxyRebarZonesCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData cd, ref string msg, ElementSet els)
            => HotLoader.Run("LiraToRevit.Rebar.RebarZonesCommand", cd, ref msg, els);
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

    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class ProxyNotesCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData cd, ref string msg, ElementSet els)
            => HotLoader.Run("KzhNotes.NotesCommand", cd, ref msg, els);
    }
}
