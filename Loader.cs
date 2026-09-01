using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
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
            // Синхронизация с сетевой шарой (см. UpdateSync) — до SetupRibbon, чтобы
            // DAN_Plugin.dll успел обновиться раньше, чем пользователь нажмёт любую кнопку.
            UpdateSync.Run(a.ControlledApplication.VersionNumber);
            // Вкладку нужно создать до первого CreateRibbonPanel — иначе панель
            // молча не создастся (CreateRibbonPanel кинет исключение "вкладка не найдена",
            // проглоченное try/catch внутри SetupVersionPanel/SetupRibbon).
            try { a.CreateRibbonTab(RibbonTabName); } catch { }
            SetupVersionPanel(a);
            SetupRibbon(a);
            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication a) => Result.Succeeded;

        // Отдельная панель с номером версии — не мешает панели "КЖ", открывает статус
        // обновления по клику (см. ProxyVersionInfoCommand).
        private static void SetupVersionPanel(UIControlledApplication a)
        {
            try
            {
                RibbonPanel panel = a.CreateRibbonPanel(RibbonTabName, "Версия");
                string path = Assembly.GetExecutingAssembly().Location;
                string label = UpdateSync.IsCached
                    ? $"v{UpdateSync.CurrentVersion}\n(нет связи)"
                    : $"v{UpdateSync.CurrentVersion}";

                var btn = (PushButton)panel.AddItem(new PushButtonData(
                    "VersionInfo", label, path,
                    "MyPlugin.Loader.ProxyVersionInfoCommand")
                {
                    ToolTip = UpdateSync.IsCached
                        ? "Не удалось подключиться к серверу обновлений — используется локальная копия. Нажмите для подробностей."
                        : "Версия плагина синхронизирована с сервером. Нажмите для подробностей."
                });
                btn.LargeImage = LoadIcon("version-control_32.png");
                btn.Image      = LoadIcon("version-control_16.png");
            }
            catch { }
        }

        private static void SetupRibbon(UIControlledApplication a)
        {
            // Вкладка уже создана в OnStartup (до SetupVersionPanel).
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

    // Синхронизация с эталонной копией плагина на сетевой шаре — вызывается один раз при
    // старте Revit (LoaderApp.OnStartup), до создания ленты. Источник правды —
    // K:\02_BIM\DAN_Plugin\<версия Revit>\ (структура файлов = как в Installer/*.iss:
    // *.dll, checklist.html и т.д. прямо в этой папке) + version.txt с номером версии,
    // который проставляется вручную при публикации (см. Installer/Publish*.ps1).
    //
    // Перезаписывает содержимое LoaderApp.PluginDir — КРОМЕ самого Loader.dll: это уже
    // загруженная Revit'ом сборка (OnStartup выполняется из её же кода), и на практике
    // подтверждено, что пока Revit открыт, Windows держит такой файл заблокированным
    // ("Папка уже используется"). DAN_Plugin.dll в эту блокировку не попадает, т.к. Revit
    // его напрямую не грузит — HotLoader ниже всегда копирует его во временный файл перед
    // загрузкой, поэтому оригинал свободен для перезаписи в любой момент.
    //
    // Из-за этого обновление самого Loader.dll (новые кнопки на ленте, правки в этом же
    // файле) через живую синхронизацию невозможно — для таких изменений нужна обычная
    // переустановка (см. Installer/Build*.ps1) при закрытом Revit. Обновления в
    // DAN_Plugin.dll (бизнес-логика команд — основная масса правок) применяются мгновенно.
    //
    // Если шара недоступна (нет сети/сервер выключен) — тихо продолжаем работать на
    // локальной копии, IsCached=true отражается в кнопке версии на ленте.
    internal static class UpdateSync
    {
        private const string ServerRoot = @"K:\02_BIM\DAN_Plugin";
        private const int TimeoutMs = 3000;

        public static string CurrentVersion { get; private set; } = "?";
        public static bool IsCached { get; private set; }
        public static string Detail { get; private set; } = "";

        public static void Run(string revitVersionNumber)
        {
            string localVersionFile = Path.Combine(LoaderApp.PluginDir, "version.txt");
            bool synced;
            string error = null;
            try
            {
                Task<bool> task = Task.Run(() => TrySync(revitVersionNumber, localVersionFile));
                synced = task.Wait(TimeoutMs) && task.Result;
            }
            catch (Exception ex)
            {
                synced = false;
                error = ex.Message;
            }

            CurrentVersion = File.Exists(localVersionFile) ? File.ReadAllText(localVersionFile).Trim() : "?";
            IsCached = !synced;
            Detail = synced
                ? $"Синхронизировано с сервером обновлений {DateTime.Now:dd.MM.yyyy HH:mm}."
                : "Не удалось подключиться к серверу обновлений (" + ServerRoot + ") — " +
                  "используется последняя загруженная версия." + (error != null ? "\n\n" + error : "");
        }

        private static bool TrySync(string revitVersionNumber, string localVersionFile)
        {
            string serverDir = Path.Combine(ServerRoot, revitVersionNumber);
            string serverVersionFile = Path.Combine(serverDir, "version.txt");
            if (!File.Exists(serverVersionFile)) return false;

            string serverVersion = File.ReadAllText(serverVersionFile).Trim();
            string localVersion = File.Exists(localVersionFile) ? File.ReadAllText(localVersionFile).Trim() : null;
            if (serverVersion == localVersion) return true; // уже актуально, копировать не нужно

            CopyDirectory(serverDir, LoaderApp.PluginDir);
            return true;
        }

        private static void CopyDirectory(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (string file in Directory.GetFiles(src))
            {
                string name = Path.GetFileName(file);
                // Loader.dll заблокирован Windows, пока Revit открыт (см. комментарий
                // над классом) — обновляется только переустановкой, не трогаем его здесь.
                if (string.Equals(name, "Loader.dll", StringComparison.OrdinalIgnoreCase))
                    continue;

                try { File.Copy(file, Path.Combine(dst, name), true); }
                catch { /* файл временно занят (антивирус и т.п.) — пропускаем, попробуем в следующий раз */ }
            }
            foreach (string dir in Directory.GetDirectories(src))
                CopyDirectory(dir, Path.Combine(dst, Path.GetFileName(dir)));
        }
    }

    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class ProxyVersionInfoCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData cd, ref string msg, ElementSet els)
        {
            TaskDialog.Show("DAN Plugin — версия",
                $"Версия: {UpdateSync.CurrentVersion}\n" +
                $"Статус: {(UpdateSync.IsCached ? "локальная копия (нет связи с сервером)" : "синхронизировано с сервером")}\n\n" +
                UpdateSync.Detail);
            return Result.Succeeded;
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
