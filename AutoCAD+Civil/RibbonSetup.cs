using System;
using System.IO;
using System.Linq;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Windows;

[assembly: ExtensionApplication(typeof(BlockTableGenerator.RibbonSetup))]

namespace BlockTableGenerator
{
    /// <summary>Добавляет на ленту AutoCAD вкладку "AutoCAD+Civil" с кнопками для BLOCKTABLE и
    /// MAFCONVERT — чтобы не нужно было помнить/вводить имена команд. Иконки рисуются прямо в
    /// коде (DrawingVisual → RenderTargetBitmap), без внешних файлов картинок — не от чего
    /// зависеть при установке/раздаче плагина.</summary>
    public class RibbonSetup : IExtensionApplication
    {
        private const string TabId = "AUTOCAD_CIVIL_TAB";

        public void Initialize()
        {
            // На момент загрузки плагина (особенно при автозагрузке из bundle при старте
            // AutoCAD) лента может быть ещё не готова — тогда откладываем до события её
            // инициализации.
            try
            {
                if (ComponentManager.Ribbon != null)
                    BuildRibbon();
                else
                    ComponentManager.ItemInitialized += ComponentManager_ItemInitialized;
            }
            catch
            {
                // ComponentManager.ItemInitialized — общий статический event на весь процесс
                // AutoCAD: на него подписываются и другие плагины (например, CAD Addin
                // Manager — тем же способом, ожидая готовности ленты). Так как это
                // multicast-делегат, необработанное исключение в ОДНОМ обработчике обрывает
                // вызов ВСЕХ следующих в цепочке — если наш обработчик упадёт и зарегистрирован
                // раньше чужого (бандлы грузятся по алфавиту, "AutoCAD+Civil" раньше
                // "CadAddinManager"), у другого плагина просто не появится лента, хотя сама
                // сборка загрузится нормально. Поэтому наш код не должен пропускать исключения
                // наружу ни при каких обстоятельствах — максимум, что мы теряем при сбое,
                // это собственную вкладку ленты.
            }
        }

        public void Terminate() { }

        private void ComponentManager_ItemInitialized(object sender, RibbonItemEventArgs e)
        {
            try
            {
                if (ComponentManager.Ribbon == null) return;
                ComponentManager.ItemInitialized -= ComponentManager_ItemInitialized;
                BuildRibbon();
            }
            catch
            {
                // См. пояснение в Initialize() — нельзя пробрасывать исключение из общего
                // статического event, иначе ломаем инициализацию ленты у других плагинов.
            }
        }

        private void BuildRibbon()
        {
            RibbonControl ribbon = ComponentManager.Ribbon;
            if (ribbon == null) return;

            // Не плодим вкладку заново при повторной загрузке плагина (переоткрытие
            // документа, повторный NETLOAD и т.п.) — пересоздаём содержимое существующей.
            RibbonTab tab = ribbon.Tabs.FirstOrDefault(t => t.Id == TabId);
            if (tab == null)
            {
                tab = new RibbonTab { Title = "DAN", Id = TabId };
                ribbon.Tabs.Add(tab);
            }
            else
            {
                tab.Title = "DAN"; // на случай, если вкладка осталась в памяти от старой версии с другим названием
                tab.Panels.Clear();
            }

            var panelSource = new RibbonPanelSource { Title = "МАФы" };
            var panel = new RibbonPanel { Source = panelSource };
            tab.Panels.Add(panel);

            panelSource.Items.Add(CreateButton(
                "Ведомость\nблоков", "BLOCKTABLE",
                "Создать или обновить ведомость малых архитектурных форм и переносных изделий",
                DrawTableIcon(32), DrawTableIcon(16)));

            panelSource.Items.Add(CreateButton(
                "Подготовка\nблоков", "MAFCONVERT",
                "Переименовать выбранные блоки (префикс МАФ_) и добавить атрибуты Наименование/Примечание",
                DrawTagIcon(32), DrawTagIcon(16)));

            var hatchPanelSource = new RibbonPanelSource { Title = "Площади" };
            var hatchPanel = new RibbonPanel { Source = hatchPanelSource };
            tab.Panels.Add(hatchPanel);

            hatchPanelSource.Items.Add(CreateButton(
                "Ведомость\nплощадей", "HATCHTABLE",
                "Создать или обновить ведомость площадей по типам штриховок",
                DrawHatchTableIcon(32), DrawHatchTableIcon(16)));

            hatchPanelSource.Items.Add(CreateButton(
                "Условные\nобозначения", "HATCHLEGEND",
                "Создать или обновить таблицу условных обозначений типов штриховок",
                DrawLegendIcon(32), DrawLegendIcon(16)));

            tab.IsActive = false; // не переключаем пользователя на новую вкладку без его выбора

            TryKickstartAddinManager();
        }

        private static bool _addinManagerKickSent = false;

        /// <summary>Обходной путь для стороннего бага в отдельно установленном плагине CAD Addin
        /// Manager: его команда "InitAddinManager" помечена в его собственном PackageContents.xml
        /// как StartupCommand="True" (должна запускаться сама при старте AutoCAD и строить его
        /// вкладку на ленте) — но фактически не срабатывает. Подтверждено вживую: баг
        /// воспроизводится и БЕЗ установленного AutoCAD+Civil (проверяли, временно убирая наш
        /// бандл), то есть это не наш код тому причиной; при этом ручной ввод "INITADDINMANAGER"
        /// после старта AutoCAD вкладку строит без проблем — похоже, их код просто не ждёт
        /// готовности ленты (в отличие от нашего BuildRibbon/ComponentManager.ItemInitialized).
        /// Раз мы это готовность и так уже дожидаемся, заодно "доталкиваем" и чужую команду — один
        /// раз за сессию AutoCAD, и только если CAD Addin Manager вообще установлен на машине,
        /// чтобы не выдавать "Неизвестная команда" тем, у кого его нет.</summary>
        private static void TryKickstartAddinManager()
        {
            if (_addinManagerKickSent) return;
            _addinManagerKickSent = true;

            try
            {
                bool cadAddinManagerInstalled =
                    Directory.Exists(Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "Autodesk", "ApplicationPlugins", "CadAddinManager.bundle"))
                    || Directory.Exists(Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                        "Autodesk", "ApplicationPlugins", "CadAddinManager.bundle"));
                if (!cadAddinManagerInstalled) return;

                Document doc = Application.DocumentManager.MdiActiveDocument;
                if (doc == null) return;
                doc.SendStringToExecute("INITADDINMANAGER ", true, false, true);
            }
            catch
            {
                // Проблемы с чужим плагином не должны ронять инициализацию нашего.
            }
        }

        private static RibbonButton CreateButton(string text, string commandName, string tooltip,
            ImageSource largeImage, ImageSource smallImage)
        {
            return new RibbonButton
            {
                Text = text,
                ShowText = true,
                ShowImage = true,
                Orientation = System.Windows.Controls.Orientation.Vertical,
                Size = RibbonItemSize.Large,
                LargeImage = largeImage,
                Image = smallImage,
                ToolTip = tooltip,
                // "^C^C" — это синтаксис макросов меню/ленты (эмуляция Esc), а НЕ то, что
                // понимает SendStringToExecute: он отправляет текст ТАК, КАК ЕСЛИ БЫ его
                // напечатали в командной строке, где "^C^C_ИМЯ" воспринимается как буквальное
                // (несуществующее) имя команды — отсюда была ошибка "Неизвестная команда".
                // Просто имя команды + пробел (эмулирует Enter) — этого достаточно, т.к.
                // кнопка кликается из уже свободной командной строки.
                CommandParameter = $"{commandName} ",
                CommandHandler = new RunCommandHandler(),
            };
        }

        /// <summary>Единый обработчик клика по кнопке ленты — просто отправляет
        /// CommandParameter кнопки в командную строку активного документа.</summary>
        private class RunCommandHandler : ICommand
        {
            public event EventHandler CanExecuteChanged { add { } remove { } }

            public bool CanExecute(object parameter) => Application.DocumentManager.MdiActiveDocument != null;

            public void Execute(object parameter)
            {
                RibbonButton btn = parameter as RibbonButton;
                Document doc = Application.DocumentManager.MdiActiveDocument;
                if (btn == null || doc == null) return;
                doc.SendStringToExecute((string)btn.CommandParameter, true, false, true);
            }
        }

        /// <summary>Иконка "таблица" для BLOCKTABLE — рамка с сеткой 2×3, синим по белому.</summary>
        private static ImageSource DrawTableIcon(int size)
        {
            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                double margin = size * 0.14;
                double w = size - margin * 2;
                double h = size - margin * 2;
                var pen = new Pen(Brushes.SteelBlue, Math.Max(1.0, size / 16.0));

                dc.DrawRectangle(Brushes.White, pen, new System.Windows.Rect(margin, margin, w, h));

                double colX = margin + w / 2.0;
                dc.DrawLine(pen, new System.Windows.Point(colX, margin), new System.Windows.Point(colX, margin + h));

                double rowH = h / 3.0;
                dc.DrawLine(pen, new System.Windows.Point(margin, margin + rowH), new System.Windows.Point(margin + w, margin + rowH));
                dc.DrawLine(pen, new System.Windows.Point(margin, margin + rowH * 2), new System.Windows.Point(margin + w, margin + rowH * 2));
            }
            return Render(visual, size);
        }

        /// <summary>Иконка "бирка" (тег с отверстием) для MAFCONVERT — символ атрибута/метки,
        /// оранжевым по белому.</summary>
        private static ImageSource DrawTagIcon(int size)
        {
            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                double margin = size * 0.12;
                double cut = size * 0.16; // скос правого края бирки
                var pen = new Pen(Brushes.DarkOrange, Math.Max(1.0, size / 16.0));

                var geo = new StreamGeometry();
                using (StreamGeometryContext ctx = geo.Open())
                {
                    ctx.BeginFigure(new System.Windows.Point(margin, margin), true, true);
                    ctx.LineTo(new System.Windows.Point(size - margin - cut, margin), true, true);
                    ctx.LineTo(new System.Windows.Point(size - margin, size / 2.0), true, true);
                    ctx.LineTo(new System.Windows.Point(size - margin - cut, size - margin), true, true);
                    ctx.LineTo(new System.Windows.Point(margin, size - margin), true, true);
                }
                geo.Freeze();
                dc.DrawGeometry(Brushes.Moccasin, pen, geo);

                double holeR = size * 0.08;
                dc.DrawEllipse(Brushes.White, pen, new System.Windows.Point(margin + size * 0.16, size / 2.0), holeR, holeR);
            }
            return Render(visual, size);
        }

        /// <summary>Иконка "ведомость площадей" для HATCHTABLE — таблица со штриховкой в первой
        /// ячейке (диагональная штриховка), зелёным по белому.</summary>
        private static ImageSource DrawHatchTableIcon(int size)
        {
            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                double margin = size * 0.14;
                double w = size - margin * 2;
                double h = size - margin * 2;
                var pen = new Pen(Brushes.SeaGreen, Math.Max(1.0, size / 16.0));

                dc.DrawRectangle(Brushes.White, pen, new System.Windows.Rect(margin, margin, w, h));

                double colX = margin + w * 0.42;
                dc.DrawLine(pen, new System.Windows.Point(colX, margin), new System.Windows.Point(colX, margin + h));

                double rowH = h / 3.0;
                dc.DrawLine(pen, new System.Windows.Point(margin, margin + rowH), new System.Windows.Point(margin + w, margin + rowH));
                dc.DrawLine(pen, new System.Windows.Point(margin, margin + rowH * 2), new System.Windows.Point(margin + w, margin + rowH * 2));

                // Диагональная штриховка первой ячейки данных (строка 1, левый столбец).
                var hatchPen = new Pen(Brushes.SeaGreen, Math.Max(0.75, size / 24.0));
                dc.PushClip(new RectangleGeometry(new System.Windows.Rect(margin, margin + rowH, colX - margin, rowH)));
                double step = size * 0.11;
                for (double x = margin - rowH; x < colX + rowH; x += step)
                {
                    dc.DrawLine(hatchPen,
                        new System.Windows.Point(x, margin + rowH * 2),
                        new System.Windows.Point(x + rowH, margin + rowH));
                }
                dc.Pop();
            }
            return Render(visual, size);
        }

        /// <summary>Иконка "условные обозначения" для HATCHLEGEND — список из двух строк, у каждой
        /// слева квадрат-образец (штриховка/заливка), справа условная линия "текста", фиолетовым
        /// по белому.</summary>
        private static ImageSource DrawLegendIcon(int size)
        {
            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                double margin = size * 0.14;
                var pen = new Pen(Brushes.MediumPurple, Math.Max(1.0, size / 16.0));
                double swatch = size * 0.22;
                double lineY1 = margin + swatch * 0.5;
                double lineY2 = size - margin - swatch * 0.5;
                double textX = margin + swatch + size * 0.1;

                dc.DrawRectangle(Brushes.Lavender, pen, new System.Windows.Rect(margin, margin, swatch, swatch));
                dc.DrawLine(pen, new System.Windows.Point(textX, lineY1), new System.Windows.Point(size - margin, lineY1));

                dc.DrawRectangle(Brushes.MediumPurple, pen, new System.Windows.Rect(margin, size - margin - swatch, swatch, swatch));
                dc.DrawLine(pen, new System.Windows.Point(textX, lineY2), new System.Windows.Point(size - margin, lineY2));
            }
            return Render(visual, size);
        }

        private static ImageSource Render(DrawingVisual visual, int size)
        {
            var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);
            rtb.Freeze();
            return rtb;
        }
    }
}
