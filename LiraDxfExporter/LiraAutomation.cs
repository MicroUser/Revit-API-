using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;
using DxfCleaner;

namespace LiraDxfExporter
{
    /// <summary>
    /// Управление живым процессом ЛИРА-САПР (LiraSapr.exe) через UI Automation. Клики по ленте
    /// у этого приложения НЕ порождают WM_COMMAND (проверено вживую через Spy++ — лог пуст при
    /// любом фильтре), поэтому автоматизация идёт через InvokePattern на конкретных элементах —
    /// подтверждено вживую через Accessibility Insights for Windows на кнопках закреплённых
    /// тулбаров ("Армирование пластин") и на панели быстрого доступа (куда специально вынесена
    /// команда "DXF (схема/результаты)" — как пункт выпадающего меню "Файл" она не давала
    /// доступа к отдельному элементу).
    /// </summary>
    /// <summary>Одна из 4 комбинаций направление×грань армирования, повторяемых на каждом этаже.
    /// ButtonName — точное имя кнопки на тулбаре "Армирование пластин" (Accessibility Insights).
    /// FaceLetter/DirLetter — буквы для имени итогового файла ("{отметка}_{FaceLetter}_{DirLetter}.dxf").</summary>
    public readonly struct ExportCombo
    {
        public readonly string ButtonName;
        public readonly string FaceLetter;
        public readonly string DirLetter;
        public ExportCombo(string buttonName, string faceLetter, string dirLetter)
        {
            ButtonName = buttonName; FaceLetter = faceLetter; DirLetter = dirLetter;
        }
    }

    public static class LiraAutomation
    {
        private const string ProcessName = "LiraSapr";
        public const string ExportButtonName = "DXF (схема/результаты)";
        private const string DialogTitleSubstring = "Сохранить как";

        public static readonly ExportCombo[] Combos =
        {
            new ExportCombo("Верхняя арматура в пластинах по оси X1", "В", "X"),
            new ExportCombo("Нижняя  арматура в пластинах по оси X1", "Н", "X"), // в самой ЛИРА-САПР тут двойной пробел — иначе поиск идёт в медленный запасной проход по всему дереву (~6 сек)
            new ExportCombo("Верхняя арматура в пластинах по оси Y1", "В", "Y"),
            new ExportCombo("Нижняя арматура в пластинах по оси Y1",  "Н", "Y"),
        };

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr lpdwProcessId);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        private const int SW_RESTORE = 9;

        /// <summary>Переводит окно на передний план — у MFC-ленты внутренняя обработка команды
        /// иногда требует, чтобы окно реально было активным, иначе InvokePattern.Invoke()
        /// формально отрабатывает без ошибки, но команда до логики приложения не доходит. Простой
        /// SetForegroundWindow из фонового процесса Windows часто молча игнорирует (защита от
        /// "кражи" фокуса) — обходим через AttachThreadInput (стандартный приём). Возвращает,
        /// действительно ли окно стало активным (для диагностики).</summary>
        public static bool BringToForeground(AutomationElement window)
        {
            IntPtr hwnd = new IntPtr(window.Current.NativeWindowHandle);

            uint targetThread = GetWindowThreadProcessId(hwnd, IntPtr.Zero);
            uint currentThread = GetCurrentThreadId();
            bool attached = targetThread != currentThread && AttachThreadInput(currentThread, targetThread, true);
            try
            {
                ShowWindow(hwnd, SW_RESTORE);
                SetForegroundWindow(hwnd);
            }
            finally
            {
                if (attached) AttachThreadInput(currentThread, targetThread, false);
            }

            Thread.Sleep(100);
            return GetForegroundWindow() == hwnd;
        }

        /// <summary>Диагностика: список ВСЕХ top-level окон процесса LiraSapr прямо сейчас —
        /// чтобы увидеть, не завис ли где-то невидимый/забытый диалог (например, уведомление
        /// об экспорте с прошлой попытки), который может блокировать новую команду.</summary>
        public static string ListProcessWindows()
        {
            var procs = Process.GetProcessesByName(ProcessName);
            if (procs.Length == 0) return "  (процесс не найден)";

            var condition = new PropertyCondition(AutomationElement.ProcessIdProperty, procs[0].Id);
            var candidates = AutomationElement.RootElement.FindAll(TreeScope.Children, condition);
            var sb = new StringBuilder();
            foreach (AutomationElement el in candidates)
                sb.AppendLine($"  [{el.Current.ControlType.ProgrammaticName.Replace("ControlType.", "")}] " +
                    $"Name=\"{el.Current.Name}\" Visible={!el.Current.IsOffscreen}");
            return sb.Length > 0 ? sb.ToString().TrimEnd() : "  (нет окон)";
        }

        /// <summary>Главное окно ЛИРА-САПР. Ищем по ProcessId (не по классу/заголовку — класс
        /// окна содержит билд-специфичный хэш, заголовок меняется с открытым документом).</summary>
        public static AutomationElement FindLiraMainWindow()
        {
            var procs = Process.GetProcessesByName(ProcessName);
            if (procs.Length == 0)
                throw new InvalidOperationException($"Процесс {ProcessName}.exe не найден — откройте ЛИРА-САПР.");
            if (procs.Length > 1)
                throw new InvalidOperationException($"Найдено {procs.Length} процессов {ProcessName}.exe — должен быть запущен ровно один.");

            int pid = procs[0].Id;
            var condition = new PropertyCondition(AutomationElement.ProcessIdProperty, pid);
            var candidates = AutomationElement.RootElement.FindAll(TreeScope.Children, condition);
            if (candidates.Count == 0)
                throw new InvalidOperationException($"У процесса {ProcessName}.exe (PID={pid}) не найдено ни одного top-level окна.");

            // Среди top-level окон процесса берём то, у которого самое длинное имя — это и есть
            // главное окно с заголовком "ПК ЛИРА-САПР ... - [документ]"; вспомогательные окна
            // (тултипы и т.п.) обычно безымянные или с коротким именем.
            AutomationElement best = null;
            int bestLen = -1;
            foreach (AutomationElement el in candidates)
            {
                int len = el.Current.Name?.Length ?? 0;
                if (len > bestLen) { bestLen = len; best = el; }
            }
            return best;
        }

        private static string NormalizeName(string s) =>
            s == null ? "" : string.Join(" ", s.Split((char[])null, StringSplitOptions.RemoveEmptyEntries));

        /// <summary>Ищет элемент по Name. Сначала точное совпадение; если не нашлось — запасной
        /// проход со "схлопнутыми" пробелами: в самой ЛИРА-САПР у части кнопок в подписи опечатка
        /// с двойным пробелом (подтверждено вживую — "Нижняя  арматура в пластинах по оси X1", но
        /// НЕ у соседней "Нижняя арматура в пластинах по оси Y1"), так что точный текст ненадёжен.</summary>
        public static AutomationElement FindByName(AutomationElement root, string name)
        {
            var condition = new PropertyCondition(AutomationElement.NameProperty, name);
            var exact = root.FindFirst(TreeScope.Descendants, condition);
            if (exact != null) return exact;

            // Запасной путь (только если точное имя не сработало) — сам по себе медленный на
            // этом приложении (у LiraSapr.exe нет нативного UIA-провайдера, Windows проксирует
            // через MSAA-мост, где каждый узел — отдельный синхронный вызов в чужой процесс).
            // PropertyCondition по NameProperty != "" фильтруется на стороне провайдера ДО
            // возврата — на порядок меньше узлов, чем полный TrueCondition-обход.
            string target = NormalizeName(name);
            var hasName = new NotCondition(new PropertyCondition(AutomationElement.NameProperty, ""));
            foreach (AutomationElement el in root.FindAll(TreeScope.Descendants, hasName))
                if (NormalizeName(el.Current.Name) == target)
                    return el;
            return null;
        }

        /// <summary>Находит элемент по точному Name и programmatically "нажимает" его через
        /// InvokePattern. Бросает понятную ошибку, если элемент не найден или не поддерживает
        /// нажатие — не проглатывает молча.</summary>
        public static void InvokeByName(AutomationElement root, string name)
        {
            // Ретрай — после экспорта/закрытия диалога сохранения дерево UI Automation иногда
            // на короткое время не отдаёт актуальные элементы тулбара (ribbon ещё перерисовывается).
            var el = FindByName(root, name);
            var sw = Stopwatch.StartNew();
            while (el == null && sw.Elapsed < TimeSpan.FromSeconds(5))
            {
                Thread.Sleep(300);
                el = FindByName(root, name);
            }

            if (el == null)
            {
                var buttonNames = root.FindAll(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button))
                    .Cast<AutomationElement>()
                    .Select(b => b.Current.Name)
                    .Where(n => !string.IsNullOrEmpty(n))
                    .Distinct()
                    .OrderBy(n => n)
                    .ToList();
                string dump = buttonNames.Count == 0
                    ? "  (кнопок в дереве вообще не найдено)"
                    : string.Join("\n", buttonNames.Select(n => "  \"" + n + "\""));
                throw new InvalidOperationException(
                    $"Элемент \"{name}\" не найден в дереве UI Automation главного окна (Name=\"{root.Current.Name}\").\n" +
                    "Сейчас в дереве видны эти кнопки:\n" + dump);
            }

            if (!el.TryGetCurrentPattern(InvokePattern.Pattern, out object patternObj))
                throw new InvalidOperationException($"Элемент \"{name}\" найден, но не поддерживает InvokePattern (нельзя нажать программно).");

            ((InvokePattern)patternObj).Invoke();
        }

        /// <summary>Ждёт появления диалога с именем, СОДЕРЖАЩИМ nameSubstring (например
        /// "Сохранить как") — используется после клика по "DXF (схема/результаты)".
        /// Подтверждено вживую через Accessibility Insights: диалог "Сохранить как" в дереве UI
        /// Automation оказался ВЛОЖЕН внутрь главного окна ЛИРА-САПР ("окно 'ПК ЛИРА-САПР...'" →
        /// "диалоговое окно 'Сохранить как'"), а НЕ является отдельным top-level окном Рабочего
        /// стола — поэтому ищем в первую очередь среди ПОТОМКОВ главного окна (parentWindow), а
        /// поиск среди top-level окон Рабочего стола держим как запасной вариант на случай другого
        /// процесса/суррогата. Возвращает null по истечении timeout.</summary>
        public static AutomationElement WaitForDialog(AutomationElement parentWindow, string nameSubstring, TimeSpan timeout)
        {
            var sw = Stopwatch.StartNew();
            var windowCondition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window);
            while (sw.Elapsed < timeout)
            {
                // Сначала быстрый неглубокий поиск (диалог — прямой ребёнок главного окна, см.
                // выше), полный обход по Descendants — только запасной вариант, если не нашли.
                AutomationElement found = null;
                foreach (AutomationElement el in parentWindow.FindAll(TreeScope.Children, windowCondition))
                {
                    string name = el.Current.Name;
                    if (!string.IsNullOrEmpty(name) && name.IndexOf(nameSubstring, StringComparison.OrdinalIgnoreCase) >= 0)
                    { found = el; break; }
                }
                if (found != null) return found;

                foreach (AutomationElement el in parentWindow.FindAll(TreeScope.Descendants, windowCondition))
                {
                    string name = el.Current.Name;
                    if (!string.IsNullOrEmpty(name) && name.IndexOf(nameSubstring, StringComparison.OrdinalIgnoreCase) >= 0)
                        return el;
                }
                foreach (AutomationElement el in AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition))
                {
                    string name = el.Current.Name;
                    if (!string.IsNullOrEmpty(name) && name.IndexOf(nameSubstring, StringComparison.OrdinalIgnoreCase) >= 0)
                        return el;
                }
                Thread.Sleep(250);
            }
            return null;
        }

        /// <summary>Диагностический дамп дерева элементов (Name/ControlType/AutomationId) —
        /// на случай, если угаданная структура стандартного диалога "Сохранить как" не совпадёт
        /// с реальной (нестандартный/кастомный диалог) — сразу видно фактические элементы, без
        /// повторного похода в Accessibility Insights.</summary>
        public static string DumpTree(AutomationElement root, int maxDepth = 4)
        {
            var sb = new StringBuilder();
            void Walk(AutomationElement el, int depth)
            {
                if (depth > maxDepth) return;
                string name = el.Current.Name;
                string autoId = el.Current.AutomationId;
                sb.AppendLine(new string(' ', depth * 2) +
                    $"[{el.Current.ControlType.ProgrammaticName.Replace("ControlType.", "")}] " +
                    $"Name=\"{name}\" AutomationId=\"{autoId}\"");
                var children = el.FindAll(TreeScope.Children, Condition.TrueCondition);
                foreach (AutomationElement child in children)
                    Walk(child, depth + 1);
            }
            Walk(root, 0);
            return sb.ToString();
        }

        /// <summary>Ищет элемент повторно в течение timeout — шелл-диалоги Windows ("Сохранить
        /// как") подгружают своё дерево UI Automation асинхронно (список файлов и т.п.), сразу
        /// после появления окна не все дочерние элементы ещё доступны.</summary>
        private static AutomationElement FindWithRetry(AutomationElement root, Condition condition, TimeSpan timeout)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < timeout)
            {
                var el = root.FindFirst(TreeScope.Descendants, condition);
                if (el != null) return el;
                Thread.Sleep(300);
            }
            return null;
        }

        /// <summary>Ждёт, пока файл path не откроется на чтение БЕЗ шаринга (FileShare.None) —
        /// то есть пока писавший его процесс (диалог сохранения ЛИРА-САПР) не закроет свой
        /// хэндл. File.Exists становится true уже в момент создания файла, а не после того, как
        /// запись/сброс буфера завершены, поэтому его одного недостаточно для гарантии, что файл
        /// готов к чтению. Опрос вместо однократной проверки: файловые события (FileSystemWatcher)
        /// здесь избыточны для короткого, разового ожидания.</summary>
        private static bool WaitUntilFileIsReadable(string path, TimeSpan timeout)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < timeout)
            {
                try
                {
                    using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None)) { }
                    return true;
                }
                catch (IOException)
                {
                    Thread.Sleep(250);
                }
            }
            return false;
        }

        /// <summary>Заполняет поле имени файла и жмёт "Сохранить" в диалоге сохранения.
        /// Подтверждено вживую через Accessibility Insights: поле — "поле со списком 'Имя файла:'"
        /// (ControlType.ComboBox), а НЕ обычный Edit с AutomationId="1148" (это оказалось неверным
        /// предположением). Рядом есть отдельная подпись "текстовый 'Имя файла:'" с ТЕМ ЖЕ Name —
        /// поэтому фильтруем строго по ControlType.ComboBox, иначе FindFirst может попасть на
        /// подпись вместо поля ввода. Если поле не найдено — бросает ошибку С ДАМПОМ дерева
        /// диалога, чтобы сразу было видно реальную структуру.</summary>
        public static void SaveAsAndConfirm(AutomationElement dialog, string fullPath)
        {
            var comboCondition = new AndCondition(
                new PropertyCondition(AutomationElement.NameProperty, "Имя файла:"),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ComboBox));
            var edit = FindWithRetry(dialog, comboCondition, TimeSpan.FromSeconds(5))
                ?? FindWithRetry(dialog, new PropertyCondition(AutomationElement.AutomationIdProperty, "1148"), TimeSpan.FromSeconds(2));
            if (edit == null)
                throw new InvalidOperationException(
                    "Не нашёл поле \"Имя файла:\" в диалоге.\nДерево диалога:\n" + DumpTree(dialog, maxDepth: 8));

            if (!edit.TryGetCurrentPattern(ValuePattern.Pattern, out object valuePatternObj))
            {
                // ComboBox иногда не отдаёт ValuePattern напрямую на себе — тогда пишем во
                // вложенный Edit (текстовая часть редактируемого комбобокса).
                var innerEdit = edit.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
                if (innerEdit == null || !innerEdit.TryGetCurrentPattern(ValuePattern.Pattern, out valuePatternObj))
                    throw new InvalidOperationException(
                        "Поле \"Имя файла:\" найдено, но ни оно само, ни вложенный Edit не поддерживают ValuePattern.\n" +
                        "Дерево диалога:\n" + DumpTree(dialog, maxDepth: 8));
            }
            ((ValuePattern)valuePatternObj).SetValue(fullPath);

            var saveButtonCondition = new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                new PropertyCondition(AutomationElement.NameProperty, "Сохранить"));
            var saveButton = FindWithRetry(dialog, saveButtonCondition, TimeSpan.FromSeconds(5));
            if (saveButton == null)
                throw new InvalidOperationException(
                    "Не нашёл кнопку \"Сохранить\" в диалоге.\nДерево диалога:\n" + DumpTree(dialog, maxDepth: 8));

            if (!saveButton.TryGetCurrentPattern(InvokePattern.Pattern, out object invokePatternObj))
                throw new InvalidOperationException("Кнопка \"Сохранить\" найдена, но не поддерживает InvokePattern.");
            ((InvokePattern)invokePatternObj).Invoke();
        }

        /// <summary>Экспортирует все 4 комбинации (см. <see cref="Combos"/>) для уже вручную
        /// выбранного на этаже набора КЭ: переключает направление/грань → жмёт экспорт DXF →
        /// заполняет диалог сохранения временным именем в destFolder → ждёт файл на диске →
        /// чистит его через <see cref="DxfCleanerCore.Clean"/> → переименовывает по отметке,
        /// взятой из самого DXF (медиана Z граней), в "{отметка:0.000}_{В|Н}_{X|Y}.dxf".
        /// log вызывается после каждого шага — по нему строится прогресс в UI. Останавливается и
        /// бросает исключение с понятным текстом при первой же ошибке (диалог не появился, файл не
        /// создался и т.п.) — не продолжает молча с оставшимися комбинациями.</summary>
        public static void ExportAllCombos(AutomationElement root, string destFolder, Action<string> log)
        {
            Directory.CreateDirectory(destFolder);

            // Численный показ значений As ("Значения на мозаике контрастным цветом") и проекция
            // на XOY — настройки вида документа, которые пользователь включает вручную перед
            // запуском (автоматическая проверка их состояния убрана — LegacyIAccessiblePattern,
            // единственный способ прочитать "нажата ли кнопка" у этих элементов, отсутствует в
            // .NET-порте UIAutomationClient.dll для net8.0-windows).

            foreach (var combo in Combos)
            {
                string label = $"{combo.FaceLetter}/{combo.DirLetter}";

                // Переданный извне AutomationElement главного окна "протухает" после того, как в
                // ЛИРА-САПР открывался и закрывался диалог "Сохранить как" — проверено вживую:
                // на 2-й комбинации FindFirst по старой ссылке перестаёт находить кнопки тулбара,
                // хотя они видны на экране. Поэтому перед каждой комбинацией ищем окно заново.
                root = FindLiraMainWindow();
                BringToForeground(root);
                log($"[{label}] Окно: \"{root.Current.Name}\"");

                log($"[{label}] Нажимаю \"{combo.ButtonName}\"...");
                InvokeByName(root, combo.ButtonName);
                Thread.Sleep(60); // пауза на перерисовку мозаики перед экспортом

                log($"[{label}] Нажимаю \"{ExportButtonName}\"...");
                InvokeByName(root, ExportButtonName);

                log($"[{label}] Жду диалог сохранения...");
                var dialog = WaitForDialog(root, DialogTitleSubstring, TimeSpan.FromSeconds(15));
                if (dialog == null)
                    throw new InvalidOperationException($"[{label}] Диалог сохранения не появился за 15 сек.");

                string tempPath = Path.Combine(destFolder, $"_tmp_{Guid.NewGuid():N}.dxf");
                log($"[{label}] Сохраняю во временный файл...");
                SaveAsAndConfirm(dialog, tempPath);

                var sw = Stopwatch.StartNew();
                while (sw.Elapsed < TimeSpan.FromSeconds(15) && !File.Exists(tempPath))
                    Thread.Sleep(250);
                if (!File.Exists(tempPath))
                    throw new InvalidOperationException($"[{label}] Файл не появился на диске за 15 сек после нажатия \"Сохранить\".");

                // File.Exists становится true уже в момент СОЗДАНИЯ файла — ЛИРА-САПР в этот
                // момент может ещё дописывать/сбрасывать буфер и держать файл открытым
                // эксклюзивно. Если сразу читать (DxfDocument.Load), получаем "процесс не может
                // получить доступ к файлу, так как он используется другим процессом" — ждём,
                // пока файл не откроется хотя бы на чтение БЕЗ шаринга (FileShare.None), это и
                // значит, что писатель его отпустил.
                if (!WaitUntilFileIsReadable(tempPath, TimeSpan.FromSeconds(15)))
                    throw new InvalidOperationException($"[{label}] Файл сохранён, но остаётся занят другим процессом дольше 15 сек.");

                log($"[{label}] Чищу DXF...");
                var summary = DxfCleanerCore.Clean(tempPath);

                // InvariantCulture — иначе на русской локали ToString даёт запятую вместо точки
                // ("4,100" вместо "4.100").
                string elevationStr = summary.ElevationM.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
                string finalName = $"{elevationStr}_{combo.FaceLetter}_{combo.DirLetter}.dxf";
                string finalPath = Path.Combine(destFolder, finalName);
                File.Move(tempPath, finalPath, true);

                log($"[{label}] Готово: {finalName} (ячеек {summary.CellsFound}, сопоставлено {summary.CellsMatched}, " +
                    $"без подписи {summary.CellsUnmatched}, подписей без ячейки {summary.OrphanValueTexts}).");
            }
        }
    }
}
