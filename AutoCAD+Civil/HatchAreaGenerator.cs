using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;

// Регистрируем класс с командами
[assembly: CommandClass(typeof(HatchAreaGenerator.Commands))]

namespace HatchAreaGenerator
{
    public class Commands
    {
        // ------------------------------------------------------------------
        // НАСТРОЙКИ — правь под свой чертёж
        // ------------------------------------------------------------------

        // ---- Ведомость проездов, тротуаров, дорожек и площадок (HATCHTABLE) ----
        const string TableTitle   = "Ведомость проездов, тротуаров, дорожек и площадок";
        const double AreaScale    = 1.0; // множитель площади. Чертёж в мм, нужно м²? -> 0.000001
        const int    AreaDecimals = 0;   // знаков после запятой (0 = округление до целых)

        // Ширины столбцов, ед. чертежа (мм) — 0:"Поз." 1:"Наименование" 2:"Тип"
        // 3:"Площадь покрытия, м2" 4:"Примечание". Сумма 9+100+9+25+22 = 165 мм — ширина
        // таблицы целиком.
        const double Col0Width = 9.0;
        const double Col1Width = 100.0;
        const double Col2Width = 9.0;
        const double Col3Width = 25.0;
        const double Col4Width = 22.0;

        const double TitleRowHeight  = 18.0;
        const double HeaderRowHeight = 35.0; // выше обычного — заголовки вроде "Площадь покрытия, м2" длинные и переносятся на несколько строк
        const double LevelRowHeight  = 10.5; // строка-заголовок уровня ("по грунту", "по кровле" и т.п.)
        const double DataRowHeight   = 10.5;

        const string AppName = "HATCHTABLE_GEN";

        // ---- Условные обозначения (HATCHLEGEND) ----
        const string LegendTableTitle    = "Условные обозначения";
        // Таблица визуально в 2 столбца ("Наименование" 135мм / "Обозначение" 50мм), но
        // "Наименование" внутри разбит на 2 подколонки БЕЗ видимой границы между ними — узкую
        // "Тип" (текст "тип N" всегда прижат к правому краю) и собственно название. Так название
        // остаётся слева, а "тип N" — справа, в одной визуальной ячейке, но с независимым
        // выравниванием каждого текста (в одной ячейке две разные выключки не сделать).
        const double LegendNameColWidth  = 120.0; // "Наименование" (было 135, минус LegendTypeColWidth)
        const double LegendTypeColWidth  = 15.0;  // "Тип N", без видимой границы с LegendNameColWidth
        const double LegendCol1Width     = 50.0;  // "Обозначение"
        const double LegendTitleRowHeight  = 18.0;
        const double LegendHeaderRowHeight = 12.0;
        const double LegendLevelRowHeight  = 10.5;
        const double LegendDataRowHeight   = 10.0; // вмещает миниатюру SwatchHeight с запасом
        const double SwatchWidth  = 35.0; // размер прямоугольника-миниатюры в "Обозначение", мм
        const double SwatchHeight = 5.0;
        const double SwatchPatternAngle = 0.0; // угол/масштаб узора в миниатюре — фиксированные,
        const double SwatchPatternScale = 0.5; // НЕ копируются с реальной штриховки (см. EnsureSwatchBlock)
        const string LegendAppName = "HATCHLEGEND_GEN";

        // ---- Общее для обеих таблиц ----
        const string TextStyleName = "Основной";     // текстовый стиль для всех ячеек таблицы
        const string LayerName     = "KPSP-Надписи"; // слой таблицы и всего её текста
        const double CellMarginH = 1.5; // отступы содержимого ячеек от границы, ед. чертежа (мм)
        const double CellMarginV = 1.5;
        const double TitleTextHeight  = 5.0;
        const double HeaderTextHeight = 3.0;
        const double DataTextHeight   = 2.5;

        // Уровень, под которым группируются типы, у которых он ещё не назначен в словаре.
        const string NoLevelLabel = "(уровень не назначен)";

        // Файл словаря типов штриховок. Правится в блокноте, новые типы команды дописывают сами.
        // Лежит в %APPDATA%\HatchTable\HatchNames.txt. Формат строки:
        //   узор|цвет|фон = тип | уровень | название
        // См. LoadNameMap/SaveNameMap ниже.
        static string ConfigPath
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "HatchTable");
                return Path.Combine(dir, "HatchNames.txt");
            }
        }
        // ------------------------------------------------------------------

        /// <summary>Строка словаря типов штриховок — тип (стабильный номер "Тип N", присваивается
        /// один раз и не меняется, даже если тип временно пропадает с чертежа), уровень
        /// (группа для "Условные обозначения"/"Ведомость площадей", напр. "по грунту") и
        /// название (пусто = авто-название по узору/цвету). Excluded — значение было "*": эту
        /// штриховку не нужно учитывать вовсе, ни в легенде, ни в ведомости.</summary>
        private class HatchTypeEntry
        {
            public int TypeNumber; // 0 = ещё не присвоен
            public string Level = "";
            public string Name = "";
            public bool Excluded;
        }

        /// <summary>Данные одной группы штриховок (по ключу "узор|цвет|фон"), собранные со сканом
        /// чертежа — суммарная площадь плюс всё нужное, чтобы построить миниатюру-обозначение
        /// (узор и цвета одного из экземпляров этой группы; угол и масштаб узора в миниатюре —
        /// ФИКСИРОВАННЫЕ, см. SwatchPatternAngle/SwatchPatternScale, а не с реальной штриховки).</summary>
        private class HatchGroupInfo
        {
            public double Area;
            public string AutoLabel;
            public string PatternName;
            public HatchPatternType PatternType;
            public Autodesk.AutoCAD.Colors.Color Color;
            public Autodesk.AutoCAD.Colors.Color BackgroundColor; // null = фона нет
        }

        /// <summary>Один "уровень" (группа) для отображения — и в легенде, и в ведомости площадей:
        /// строка-заголовок на всю ширину таблицы с текстом Level, под ней — Items (отсортированы
        /// по номеру типа).</summary>
        private class LevelSection
        {
            public string Level;
            public List<KeyValuePair<string, HatchTypeEntry>> Items;
        }

        [CommandMethod("HATCHTABLE")]
        public void CreateHatchTable()
        {
            Document doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Database db = doc.Database;
            Editor ed = doc.Editor;

            Dictionary<string, HatchTypeEntry> nameMap = LoadNameMap(ConfigPath);

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                // Штриховки считаем ВСЕГДА из пространства модели — там лежит сама площадка,
                // независимо от того, на каком листе/пространстве в итоге размещается сама
                // таблица (см. targetSpace ниже) — тот же приём, что и в BLOCKTABLE.
                BlockTableRecord ms = (BlockTableRecord)tr.GetObject(
                    bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                // А сама таблица вставляется в ТЕКУЩЕЕ активное пространство — модель, если
                // активна вкладка "Модель", либо paper space активного листа — так таблицу можно
                // разместить прямо на листе, а не только в модели.
                BlockTableRecord targetSpace = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForRead);

                var groups = ScanHatchGroups(tr, ms, out int skipped);
                if (groups.Count == 0)
                {
                    ed.WriteMessage("\nШтриховки в пространстве модели не найдены.");
                    return;
                }

                // Дописать в словарь новые (ещё не описанные) типы, присвоить им номера и
                // пересохранить файл отсортированным (см. SyncDictionary).
                int added = SyncDictionary(ConfigPath, nameMap, groups.Keys);
                if (added > 0)
                    ed.WriteMessage("\nВ словарь добавлено новых типов: {0}. Файл: {1}",
                        added, ConfigPath);

                // Исключаем типы, помеченные в словаре звёздочкой ("узор|цвет|фон = *") — так
                // пользователь может вручную сказать "эту штриховку в ведомость не включать" прямо
                // в текстовом файле, не трогая сам чертёж.
                var present = groups.Keys
                    .Where(k => nameMap.TryGetValue(k, out HatchTypeEntry e) && !e.Excluded)
                    .ToList();
                if (present.Count == 0)
                {
                    ed.WriteMessage("\nПосле исключения помеченных \"*\" типов штриховок для ведомости ничего не осталось.");
                    return;
                }

                // Группировка по уровню ("по грунту", "по кровле" и т.д.) — тот же уровень, что и
                // в "Условные обозначения" (см. HATCHLEGEND), т.к. источник один — словарь.
                List<LevelSection> sections = GroupByLevel(
                    present.Select(k => new KeyValuePair<string, HatchTypeEntry>(k, nameMap[k])));

                // Ищем уже созданную этой командой таблицу — если есть, обновляем её на месте,
                // иначе создаём новую. Ищем только в ТЕКУЩЕМ пространстве (targetSpace) — так у
                // модели и у каждого листа может быть своя, независимо обновляемая таблица.
                ObjectId existingId = FindExistingTableId(tr, targetSpace, AppName);
                bool hasExisting = !existingId.IsNull;
                bool isUpdate;

                if (hasExisting)
                {
                    bool? choice = AskUpdateOrCreate("HATCHTABLE");
                    if (choice == null)
                    {
                        ed.WriteMessage("\nОтменено.");
                        return;
                    }
                    isUpdate = choice.Value;
                }
                else
                {
                    isUpdate = false;
                }

                if (hasExisting && !isUpdate)
                {
                    // Пользователь явно выбрал "Создать новую" при уже существующей таблице —
                    // снимаем XData-метку со старой (тот же приём, что и в BLOCKTABLE), иначе при
                    // следующем запуске стало бы неоднозначно, какая из двух таблиц "своя".
                    Table oldTb = (Table)tr.GetObject(existingId, OpenMode.ForWrite);
                    oldTb.XData = new ResultBuffer(new TypedValue((int)DxfCode.ExtendedDataRegAppName, AppName));
                }

                Table tb;
                // "Наименование" и "Тип" приходят из словаря (ConfigPath) — стабильны между
                // запусками сами по себе. А вот "Примечание" вводится ВРУЧНУЮ прямо в таблице и ни
                // из чего не вычисляется — при обновлении его нужно сохранить и перенести на новые
                // номера строк (сортировка/группировка могла сдвинуть строку), а не затереть. Ключ
                // для сохранения — итоговый текст "Наименование" (у штриховок, в отличие от
                // блоков в BLOCKTABLE, нет своего стабильного ObjectId-определения).
                Dictionary<string, string> preservedNotes = new Dictionary<string, string>();

                if (isUpdate)
                {
                    tb = (Table)tr.GetObject(existingId, OpenMode.ForWrite);

                    // Снимаем ВСЕ объединения ячеек, оставшиеся с прошлой раскладки (строки-
                    // заголовки уровня могли оказаться на других номерах строк после обновления —
                    // группировка/сортировка не стабильна между запусками) — иначе запись текста
                    // в отдельную колонку у бывшей объединённой строки может повести себя
                    // непредсказуемо. Объединяем заново ниже, уже по новой раскладке.
                    if (tb.Rows.Count > 0 && tb.Columns.Count > 0)
                        tb.UnmergeCells(CellRange.Create(tb, 0, 0, tb.Rows.Count - 1, tb.Columns.Count - 1));

                    for (int r = 2; r < tb.Rows.Count; r++)
                    {
                        // Различаем строку данных от строки-заголовка уровня по колонке "Поз." —
                        // у данных там число позиции, у заголовка уровня — текст уровня (не число).
                        if (!int.TryParse(tb.Cells[r, 0].TextString, out _)) continue;

                        string rowName = tb.Cells[r, 1].TextString;
                        if (string.IsNullOrEmpty(rowName)) continue;

                        string rowNote = tb.Cells[r, 4].TextString;
                        if (!string.IsNullOrEmpty(rowNote))
                            preservedNotes[rowName] = rowNote;
                    }
                }
                else
                {
                    // Точку вставки спрашиваем только при первом создании — при обновлении
                    // таблица остаётся там, где пользователь её разместил (и мог передвинуть).
                    PromptPointResult ppr = ed.GetPoint("\nУкажите точку вставки таблицы: ");
                    if (ppr.Status != PromptStatus.OK) return;

                    tb = new Table();
                    tb.TableStyle = db.Tablestyle; // текущий стиль таблиц чертежа
                    tb.Position = ppr.Value;
                }

                // Таблица и весь её текст — на слое LayerName.
                EnsureLayer(db, tr, LayerName);
                tb.Layer = LayerName;

                const int nCols = 5;
                int dataRowCount = sections.Sum(s => s.Items.Count);
                int nRows = 2 + sections.Count + dataRowCount; // заголовок + шапка + уровни + данные

                if (isUpdate)
                {
                    int curRows = tb.Rows.Count;
                    if (nRows > curRows)
                        tb.InsertRows(curRows, DataRowHeight, nRows - curRows);
                    else if (nRows < curRows)
                        tb.DeleteRows(nRows, curRows - nRows);
                }
                else
                {
                    tb.SetSize(nRows, nCols);
                }

                // Заголовок (строка 0)
                tb.Cells[0, 0].TextString = TableTitle;

                // Шапка (строка 1)
                string[] headers = { "Поз.", "Наименование", "Тип", "Площадь покрытия, м2", "Примечание" };
                for (int c = 0; c < nCols; c++)
                {
                    tb.Cells[1, c].TextString = headers[c];
                    tb.Cells[1, c].Alignment = CellAlignment.MiddleCenter;
                }

                // Данные — по уровням, "Поз." сквозная нумерация через все уровни
                string fmt = "F" + AreaDecimals;
                int pos = 1;
                int row = 2;
                var levelRows = new List<int>();
                foreach (LevelSection section in sections)
                {
                    levelRows.Add(row);
                    tb.Cells[row, 0].TextString = section.Level;
                    tb.Cells[row, 0].Alignment = CellAlignment.MiddleLeft;
                    row++;

                    foreach (KeyValuePair<string, HatchTypeEntry> kv in section.Items)
                    {
                        HatchTypeEntry entry = kv.Value;
                        HatchGroupInfo info = groups[kv.Key];
                        double area = info.Area * AreaScale;
                        string name = Resolve(entry, info.AutoLabel);

                        tb.Cells[row, 0].TextString = pos.ToString();
                        tb.Cells[row, 0].Alignment = CellAlignment.MiddleCenter;

                        tb.Cells[row, 1].TextString = name;
                        tb.Cells[row, 1].Alignment = CellAlignment.MiddleLeft;

                        tb.Cells[row, 2].TextString = entry.TypeNumber.ToString();
                        tb.Cells[row, 2].Alignment = CellAlignment.MiddleCenter;

                        tb.Cells[row, 3].TextString = area.ToString(fmt);
                        tb.Cells[row, 3].Alignment = CellAlignment.MiddleCenter;

                        tb.Cells[row, 4].TextString = preservedNotes.TryGetValue(name, out string note) ? note : "";
                        tb.Cells[row, 4].Alignment = CellAlignment.MiddleCenter;

                        pos++;
                        row++;
                    }
                }

                // Размеры столбцов, высоты строк, текста и отступы содержимого ячеек
                tb.HorizontalCellMargin = CellMarginH;
                tb.VerticalCellMargin = CellMarginV;

                tb.Columns[0].Width = Col0Width;
                tb.Columns[1].Width = Col1Width;
                tb.Columns[2].Width = Col2Width;
                tb.Columns[3].Width = Col3Width;
                tb.Columns[4].Width = Col4Width;

                tb.Rows[0].Height = TitleRowHeight;
                tb.Rows[1].Height = HeaderRowHeight;
                for (int r = 2; r < nRows; r++)
                    tb.Rows[r].Height = levelRows.Contains(r) ? LevelRowHeight : DataRowHeight;

                tb.Cells[0, 0].TextHeight = TitleTextHeight;
                for (int c = 0; c < nCols; c++)
                    tb.Cells[1, c].TextHeight = HeaderTextHeight;
                for (int r = 2; r < nRows; r++)
                    for (int c = 0; c < nCols; c++)
                        tb.Cells[r, c].TextHeight = DataTextHeight;

                // Объединяем строки-заголовки уровня на всю ширину таблицы — ПОСЛЕ того, как
                // заданы текст/высоты для всех строк, но ДО ApplyTextStyle/GenerateLayout, чтобы
                // стиль и раскладка учли уже объединённые ячейки.
                foreach (int r in levelRows)
                    tb.MergeCells(CellRange.Create(tb, r, 0, r, nCols - 1));

                // Текстовый стиль — на ВСЕ ячейки, применяем после того как таблица дорощена/дана
                // нужным числом строк, но ДО GenerateLayout (он пересчитывает размеры ячеек под
                // содержимое и шрифт).
                ApplyTextStyle(db, tr, tb, TextStyleName, ed);

                tb.GenerateLayout();

                // Добавление таблицы в модель (только при первом создании — при обновлении
                // таблица уже находится в базе). XData ставим ПОСЛЕ AppendEntity/
                // AddNewlyCreatedDBObject — на объекте, ещё не добавленном в базу чертежа, запись
                // XData может тихо не сохраниться (см. переписку по BLOCKTABLE).
                if (!isUpdate)
                {
                    BlockTableRecord targetSpaceWrite = (BlockTableRecord)tr.GetObject(
                        targetSpace.ObjectId, OpenMode.ForWrite);
                    targetSpaceWrite.AppendEntity(tb);
                    tr.AddNewlyCreatedDBObject(tb, true);

                    // ResultBuffer только с маркером имени приложения (код 1001) без данных после
                    // него не сохраняется вообще — добавляем реальное значение (код 1000) вслед за
                    // маркером (см. переписку по BLOCKTABLE).
                    EnsureRegApp(db, tr, AppName);
                    tb.XData = new ResultBuffer(
                        new TypedValue((int)DxfCode.ExtendedDataRegAppName, AppName),
                        new TypedValue((int)DxfCode.ExtendedDataAsciiString, "table"));
                }

                Layout targetLayout = (Layout)tr.GetObject(targetSpace.LayoutId, OpenMode.ForRead);
                tr.Commit();

                ed.WriteMessage(isUpdate
                    ? "\nОбновлено (лист \"{2}\"): типов покрытия — {0}, пропущено штриховок — {1}."
                    : "\nГотово (лист \"{2}\"): типов покрытия — {0}, пропущено штриховок — {1}.",
                    dataRowCount, skipped, targetLayout.LayoutName);
            }
        }

        /// <summary>Строит/обновляет таблицу "Условные обозначения" — 2 столбца (Наименование,
        /// Обозначение), сгруппированные по уровню строки с "Тип N. Название" и миниатюрой
        /// реальной штриховки (см. EnsureSwatchBlock). Источник данных — тот же словарь
        /// (ConfigPath), что и у HATCHTABLE, поэтому номера типов и названия у обеих таблиц всегда
        /// совпадают без прямой связи между самими AutoCAD-таблицами.</summary>
        [CommandMethod("HATCHLEGEND")]
        public void CreateHatchLegend()
        {
            Document doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Database db = doc.Database;
            Editor ed = doc.Editor;

            Dictionary<string, HatchTypeEntry> nameMap = LoadNameMap(ConfigPath);

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord ms = (BlockTableRecord)tr.GetObject(
                    bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                BlockTableRecord targetSpace = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForRead);

                var groups = ScanHatchGroups(tr, ms, out int _);
                if (groups.Count == 0)
                {
                    ed.WriteMessage("\nШтриховки в пространстве модели не найдены.");
                    return;
                }

                int added = SyncDictionary(ConfigPath, nameMap, groups.Keys);
                if (added > 0)
                    ed.WriteMessage("\nВ словарь добавлено новых типов: {0}. Файл: {1}",
                        added, ConfigPath);

                var present = groups.Keys
                    .Where(k => nameMap.TryGetValue(k, out HatchTypeEntry e) && !e.Excluded)
                    .ToList();
                if (present.Count == 0)
                {
                    ed.WriteMessage("\nПосле исключения помеченных \"*\" типов штриховок легенда пуста.");
                    return;
                }

                List<LevelSection> sections = GroupByLevel(
                    present.Select(k => new KeyValuePair<string, HatchTypeEntry>(k, nameMap[k])));

                ObjectId existingId = FindExistingTableId(tr, targetSpace, LegendAppName);
                bool hasExisting = !existingId.IsNull;
                bool isUpdate;

                if (hasExisting)
                {
                    bool? choice = AskUpdateOrCreate("HATCHLEGEND");
                    if (choice == null)
                    {
                        ed.WriteMessage("\nОтменено.");
                        return;
                    }
                    isUpdate = choice.Value;
                }
                else
                {
                    isUpdate = false;
                }

                if (hasExisting && !isUpdate)
                {
                    Table oldTb = (Table)tr.GetObject(existingId, OpenMode.ForWrite);
                    oldTb.XData = new ResultBuffer(new TypedValue((int)DxfCode.ExtendedDataRegAppName, LegendAppName));
                }

                Table tb;
                if (isUpdate)
                {
                    tb = (Table)tr.GetObject(existingId, OpenMode.ForWrite);
                    if (tb.Rows.Count > 0 && tb.Columns.Count > 0)
                        tb.UnmergeCells(CellRange.Create(tb, 0, 0, tb.Rows.Count - 1, tb.Columns.Count - 1));
                }
                else
                {
                    PromptPointResult ppr = ed.GetPoint("\nУкажите точку вставки таблицы: ");
                    if (ppr.Status != PromptStatus.OK) return;

                    tb = new Table();
                    tb.TableStyle = db.Tablestyle;
                    tb.Position = ppr.Value;
                }

                EnsureLayer(db, tr, LayerName);
                tb.Layer = LayerName;

                // 3 столбца: 0="Наименование", 1="Тип" (без видимой границы с 0 — см. ниже),
                // 2="Обозначение". Визуально это по-прежнему 2 столбца, как и просили (135/50 мм) —
                // 0 и 1 вместе как раз дают 135 мм.
                const int nCols = 3;
                int dataRowCount = sections.Sum(s => s.Items.Count);
                int nRows = 2 + sections.Count + dataRowCount;

                if (isUpdate)
                {
                    int curRows = tb.Rows.Count;
                    if (nRows > curRows)
                        tb.InsertRows(curRows, LegendDataRowHeight, nRows - curRows);
                    else if (nRows < curRows)
                        tb.DeleteRows(nRows, curRows - nRows);
                }
                else
                {
                    tb.SetSize(nRows, nCols);
                }

                tb.Cells[0, 0].TextString = LegendTableTitle;

                // Шапка: "Наименование" — общий заголовок над объединёнными столбцами 0+1,
                // "Обозначение" — над столбцом 2.
                tb.Cells[1, 0].TextString = "Наименование";
                tb.Cells[1, 0].Alignment = CellAlignment.MiddleCenter;
                tb.Cells[1, 2].TextString = "Обозначение";
                tb.Cells[1, 2].Alignment = CellAlignment.MiddleCenter;

                int row = 2;
                var levelRows = new List<int>();
                foreach (LevelSection section in sections)
                {
                    levelRows.Add(row);
                    // Уровень — БЕЗ объединения ячеек (по просьбе), текст по центру своей ячейки
                    // (столбец 0), столбцы 1 и 2 в этой строке остаются пустыми.
                    tb.Cells[row, 0].TextString = section.Level;
                    tb.Cells[row, 0].Alignment = CellAlignment.MiddleCenter;
                    row++;

                    foreach (KeyValuePair<string, HatchTypeEntry> kv in section.Items)
                    {
                        HatchTypeEntry entry = kv.Value;
                        HatchGroupInfo info = groups[kv.Key];
                        string name = Resolve(entry, info.AutoLabel);

                        tb.Cells[row, 0].TextString = name;
                        tb.Cells[row, 0].Alignment = CellAlignment.MiddleLeft;

                        // "Тип N" — отдельная (узкая) колонка, всегда прижат к правому краю,
                        // независимо от длины названия слева (см. LegendTypeColWidth выше).
                        tb.Cells[row, 1].TextString = $"тип {entry.TypeNumber}";
                        tb.Cells[row, 1].Alignment = CellAlignment.MiddleRight;

                        // autoFit=false — иначе AutoCAD растягивает блок под размер ячейки (у
                        // столбца "Обозначение" 50 мм минус отступы получалось ~47×6.71 мм вместо
                        // заданных 35×5) вместо показа в реальном размере блока.
                        ObjectId swatchId = EnsureSwatchBlock(db, tr, entry.TypeNumber, info);
                        tb.SetBlockTableRecordId(row, 2, swatchId, false);
                        tb.Cells[row, 2].Alignment = CellAlignment.MiddleCenter;

                        row++;
                    }
                }

                tb.HorizontalCellMargin = CellMarginH;
                tb.VerticalCellMargin = CellMarginV;

                tb.Columns[0].Width = LegendNameColWidth;
                tb.Columns[1].Width = LegendTypeColWidth;
                tb.Columns[2].Width = LegendCol1Width;

                tb.Rows[0].Height = LegendTitleRowHeight;
                tb.Rows[1].Height = LegendHeaderRowHeight;
                for (int r = 2; r < nRows; r++)
                    tb.Rows[r].Height = levelRows.Contains(r) ? LegendLevelRowHeight : LegendDataRowHeight;

                tb.Cells[0, 0].TextHeight = TitleTextHeight;
                tb.Cells[1, 0].TextHeight = HeaderTextHeight;
                tb.Cells[1, 2].TextHeight = HeaderTextHeight;
                for (int r = 2; r < nRows; r++)
                {
                    tb.Cells[r, 0].TextHeight = DataTextHeight;
                    tb.Cells[r, 1].TextHeight = DataTextHeight;
                }

                // Заголовок таблицы — на всю ширину (3 столбца).
                tb.MergeCells(CellRange.Create(tb, 0, 0, 0, nCols - 1));
                // Шапка — "Наименование" объединяет столбцы 0+1 (визуально один заголовок над
                // "Наименование"+"Тип"), столбец 2 ("Обозначение") отдельно, без объединения.
                tb.MergeCells(CellRange.Create(tb, 1, 0, 1, 1));

                // Граница между столбцами 0 ("Наименование") и 1 ("Тип") невидима на ВСЕХ строках
                // данных и уровня — чтобы название и "тип N" читались как один визуальный столбец,
                // хоть технически это два разных (иначе не получить независимое выравнивание текста
                // "название слева / тип N справа" внутри одной ячейки).
                for (int r = 2; r < nRows; r++)
                {
                    tb.Cells[r, 0].Borders.Right.IsVisible = false;
                    tb.Cells[r, 1].Borders.Left.IsVisible = false;
                }

                ApplyTextStyle(db, tr, tb, TextStyleName, ed);
                tb.GenerateLayout();

                if (!isUpdate)
                {
                    BlockTableRecord targetSpaceWrite = (BlockTableRecord)tr.GetObject(
                        targetSpace.ObjectId, OpenMode.ForWrite);
                    targetSpaceWrite.AppendEntity(tb);
                    tr.AddNewlyCreatedDBObject(tb, true);

                    EnsureRegApp(db, tr, LegendAppName);
                    tb.XData = new ResultBuffer(
                        new TypedValue((int)DxfCode.ExtendedDataRegAppName, LegendAppName),
                        new TypedValue((int)DxfCode.ExtendedDataAsciiString, "table"));
                }

                Layout targetLayout = (Layout)tr.GetObject(targetSpace.LayoutId, OpenMode.ForRead);
                tr.Commit();

                ed.WriteMessage(isUpdate
                    ? "\nОбновлено (лист \"{1}\"): типов покрытия в легенде — {0}."
                    : "\nГотово (лист \"{1}\"): типов покрытия в легенде — {0}.",
                    dataRowCount, targetLayout.LayoutName);
            }
        }

        // ============ Сканирование штриховок ============

        /// <summary>Сканирует пространство модели, группирует штриховки по ключу "узор|цвет|фон"
        /// (см. ColorToLabel/BackgroundColorToLabel) и суммирует площадь каждой группы. Заодно
        /// запоминает параметры узора ОДНОЙ (первой встреченной) штриховки каждой группы — нужно
        /// HATCHLEGEND для построения миниатюры-обозначения (см. EnsureSwatchBlock). Используется
        /// и HATCHTABLE, и HATCHLEGEND — общая логика сбора данных, а не общее состояние между
        /// командами (каждая читает чертёж заново).</summary>
        static Dictionary<string, HatchGroupInfo> ScanHatchGroups(Transaction tr, BlockTableRecord ms, out int skipped)
        {
            var result = new Dictionary<string, HatchGroupInfo>();
            skipped = 0;

            foreach (ObjectId id in ms)
            {
                if (id.ObjectClass.DxfName != "HATCH") continue;
                Hatch h = tr.GetObject(id, OpenMode.ForRead) as Hatch;
                if (h == null) continue;

                double area;
                try { area = h.Area; }
                catch { skipped++; continue; }

                string pattern = h.PatternName;
                string color   = ColorToLabel(h.Color, h, tr);
                string bgColor = BackgroundColorToLabel(h);
                // Цвет фона — часть ключа группировки, а не только цвет самого узора: две
                // штриховки с одинаковым узором и цветом линий, но разным цветом фона, должны
                // попадать в РАЗНЫЕ строки, а не суммироваться в одну.
                string key = pattern + "|" + color + "|" + bgColor;

                if (result.TryGetValue(key, out HatchGroupInfo info))
                {
                    info.Area += area;
                }
                else
                {
                    result[key] = new HatchGroupInfo
                    {
                        Area = area,
                        AutoLabel = bgColor == NoBackgroundLabel
                            ? pattern + " (" + color + ")"
                            : pattern + " (" + color + ", фон " + bgColor + ")",
                        PatternName = h.PatternName,
                        PatternType = h.PatternType,
                        Color = h.Color,
                        BackgroundColor = SafeBackgroundColor(h),
                    };
                }
            }
            return result;
        }

        static Autodesk.AutoCAD.Colors.Color SafeBackgroundColor(Hatch h)
        {
            try
            {
                Autodesk.AutoCAD.Colors.Color bg = h.BackgroundColor;
                return (bg != null && bg.ColorMethod != ColorMethod.None) ? bg : null;
            }
            catch { return null; }
        }

        /// <summary>Создаёт (или, если уже есть, полностью пересобирает) определение блока-
        /// миниатюры для легенды: прямоугольник SwatchWidth×SwatchHeight мм со штриховкой того же
        /// узора/угла/масштаба/цвета/фона, что у реальных штриховок этой группы. Имя блока
        /// завязано на НОМЕР ТИПА (стабильный, см. HatchTypeEntry.TypeNumber), поэтому повторный
        /// запуск HATCHLEGEND обновляет уже существующее определение, а не плодит дубликаты.</summary>
        static ObjectId EnsureSwatchBlock(Database db, Transaction tr, int typeNumber, HatchGroupInfo info)
        {
            string blockName = "ЛЕГЕНДА_ТИП_" + typeNumber;
            BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);

            BlockTableRecord btr;
            if (bt.Has(blockName))
            {
                btr = (BlockTableRecord)tr.GetObject(bt[blockName], OpenMode.ForWrite);
                // Пересоздаём содержимое с нуля — проще и надёжнее, чем пытаться понять, что из
                // старого содержимого можно переиспользовать (штриховка могла поменять узор/цвет
                // с прошлого раза).
                foreach (ObjectId id in btr)
                {
                    Entity ent = (Entity)tr.GetObject(id, OpenMode.ForWrite);
                    ent.Erase();
                }
            }
            else
            {
                bt.UpgradeOpen();
                btr = new BlockTableRecord { Name = blockName };
                bt.Add(btr);
                tr.AddNewlyCreatedDBObject(btr, true);
            }

            var boundary = new Polyline();
            boundary.AddVertexAt(0, new Point2d(0, 0), 0, 0, 0);
            boundary.AddVertexAt(1, new Point2d(SwatchWidth, 0), 0, 0, 0);
            boundary.AddVertexAt(2, new Point2d(SwatchWidth, SwatchHeight), 0, 0, 0);
            boundary.AddVertexAt(3, new Point2d(0, SwatchHeight), 0, 0, 0);
            boundary.Closed = true;
            boundary.SetDatabaseDefaults();
            boundary.Layer = "0";
            btr.AppendEntity(boundary);
            tr.AddNewlyCreatedDBObject(boundary, true);

            var hatch = new Hatch();
            btr.AppendEntity(hatch);
            tr.AddNewlyCreatedDBObject(hatch, true);
            hatch.SetDatabaseDefaults();
            hatch.Layer = "0";
            hatch.SetHatchPattern(info.PatternType, info.PatternName);
            hatch.PatternAngle = SwatchPatternAngle;
            hatch.PatternScale = SwatchPatternScale;
            hatch.Color = info.Color;
            if (info.BackgroundColor != null) hatch.BackgroundColor = info.BackgroundColor;

            var loopIds = new ObjectIdCollection { boundary.ObjectId };
            hatch.AppendLoop(HatchLoopTypes.Default, loopIds);
            hatch.EvaluateHatch(true);

            return btr.ObjectId;
        }

        // ============ Словарь типов штриховок ============

        static Dictionary<string, HatchTypeEntry> LoadNameMap(string path)
        {
            var map = new Dictionary<string, HatchTypeEntry>();
            if (!File.Exists(path)) return map;

            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                string key   = line.Substring(0, eq).Trim();
                string value = line.Substring(eq + 1).Trim();
                if (key.Length == 0) continue;
                map[key] = ParseEntry(value);
            }
            return map;
        }

        /// <summary>Разбирает значение справа от "=": "*" — исключить штриховку целиком; иначе
        /// "тип | уровень | название" (уровень/название могут быть пустыми). Если первая часть не
        /// число — это СТАРЫЙ формат файла ("узор|цвет|фон = название", без типа/уровня) или ещё
        /// не размеченная строка: всё значение целиком считаем названием, номер типа присвоится
        /// при следующем сохранении (см. SaveNameMap) — правки названий, сделанные до появления
        /// уровней/типов, так не теряются.</summary>
        static HatchTypeEntry ParseEntry(string value)
        {
            var entry = new HatchTypeEntry();
            if (value == "*") { entry.Excluded = true; return entry; }

            string[] parts = value.Split('|');
            if (parts.Length == 0 || !int.TryParse(parts[0].Trim(), out entry.TypeNumber))
            {
                entry.TypeNumber = 0;
                entry.Name = value;
                return entry;
            }

            if (parts.Length >= 2) entry.Level = parts[1].Trim();
            if (parts.Length >= 3) entry.Name = string.Join("|", parts.Skip(2)).Trim();
            return entry;
        }

        /// <summary>Дописывает в словарь новые (ещё не описанные на чертеже) ключи и сохраняет
        /// файл (см. SaveNameMap — присваивает номера типов и сортирует). Возвращает, сколько
        /// ключей добавлено новых.</summary>
        static int SyncDictionary(string path, Dictionary<string, HatchTypeEntry> map, IEnumerable<string> discoveredKeys)
        {
            var missing = discoveredKeys.Where(k => !map.ContainsKey(k)).Distinct().ToList();
            foreach (string k in missing) map[k] = new HatchTypeEntry();

            SaveNameMap(path, map);
            return missing.Count;
        }

        /// <summary>Присваивает стабильный номер типа записям, у которых его ещё нет (новые ключи
        /// или мигрированные из старого формата — см. ParseEntry), и ПОЛНОСТЬЮ перезаписывает файл,
        /// отсортировав все строки по алфавиту. Перезапись целиком (а не дописывание в конец)
        /// нужна и для сортировки, и для того, чтобы вписанные только что номера типов сохранились.
        /// Вызывается при КАЖДОМ запуске команды, даже без новых ключей — так файл остаётся
        /// отсортированным, даже если пользователь вручную добавил строку не по порядку.</summary>
        static void SaveNameMap(string path, Dictionary<string, HatchTypeEntry> map)
        {
            int nextType = map.Values.Where(e => !e.Excluded).Select(e => e.TypeNumber).DefaultIfEmpty(0).Max();
            foreach (KeyValuePair<string, HatchTypeEntry> kv in map.OrderBy(kv => kv.Key, StringComparer.CurrentCultureIgnoreCase))
                if (!kv.Value.Excluded && kv.Value.TypeNumber == 0)
                    kv.Value.TypeNumber = ++nextType;

            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (StreamWriter sw = new StreamWriter(path, false)) // false = перезаписать целиком
            {
                sw.WriteLine("# Словарь типов штриховок.  Формат строки:");
                sw.WriteLine("#   узор|цвет|фон = тип | уровень | название");
                sw.WriteLine("# тип — номер \"Тип N\", присваивается автоматически один раз и не меняется.");
                sw.WriteLine("# уровень — группа для \"Условные обозначения\"/\"Ведомость площадей\" (напр.: по грунту, по кровле). Пусто — ещё не назначен.");
                sw.WriteLine("# название — пусто = авто-название. '*' вместо всего значения после '=' — не учитывать штриховку вовсе.");
                sw.WriteLine();
                foreach (KeyValuePair<string, HatchTypeEntry> kv in map.OrderBy(kv => kv.Key, StringComparer.CurrentCultureIgnoreCase))
                {
                    string valueText = kv.Value.Excluded
                        ? "*"
                        : $"{kv.Value.TypeNumber} | {kv.Value.Level} | {kv.Value.Name}";
                    sw.WriteLine(kv.Key + " = " + valueText);
                }
            }
        }

        static string Resolve(HatchTypeEntry entry, string autoLabel)
            => !string.IsNullOrWhiteSpace(entry.Name) ? entry.Name : autoLabel;

        /// <summary>Группирует записи по уровню (пустой уровень — под NoLevelLabel). Уровни
        /// упорядочены по МИНИМАЛЬНОМУ номеру типа внутри них (т.е. в порядке появления первого
        /// типа этого уровня в словаре) — так порядок уровней в таблице предсказуем и не скачет
        /// между запусками. Внутри уровня записи упорядочены по номеру типа.</summary>
        static List<LevelSection> GroupByLevel(IEnumerable<KeyValuePair<string, HatchTypeEntry>> entries)
        {
            return entries
                .GroupBy(kv => string.IsNullOrWhiteSpace(kv.Value.Level) ? NoLevelLabel : kv.Value.Level)
                .Select(g => new LevelSection { Level = g.Key, Items = g.OrderBy(kv => kv.Value.TypeNumber).ToList() })
                .OrderBy(s => s.Items.Min(kv => kv.Value.TypeNumber))
                .ToList();
        }

        // ============ Цвет ============

        const string NoBackgroundLabel = "нет";

        /// <summary>Цвет ФОНА штриховки (Hatch.BackgroundColor) — отдельно от цвета самого узора
        /// (см. ColorToLabel ниже). У штриховки без залитого фона это свойство отдаёт
        /// ColorMethod.None — тогда возвращаем NoBackgroundLabel. В отличие от цвета узора, у
        /// фона нет смысла в ByLayer/ByBlock (задаётся только явным цветом или ACI-индексом),
        /// поэтому и разбор проще.</summary>
        static string BackgroundColorToLabel(Hatch h)
        {
            Autodesk.AutoCAD.Colors.Color bg;
            try { bg = h.BackgroundColor; }
            catch { return NoBackgroundLabel; } // старые версии/типы штриховок могут не поддерживать это свойство вовсе
            if (bg == null || bg.ColorMethod == ColorMethod.None) return NoBackgroundLabel;
            if (bg.IsByAci) return AciName(bg.ColorIndex);
            return string.Format("RGB {0},{1},{2}", bg.ColorValue.R, bg.ColorValue.G, bg.ColorValue.B);
        }

        static string ColorToLabel(Autodesk.AutoCAD.Colors.Color col, Entity ent, Transaction tr)
        {
            Autodesk.AutoCAD.Colors.Color eff = col;
            if (col.IsByLayer)
            {
                LayerTableRecord ltr = tr.GetObject(ent.LayerId, OpenMode.ForRead) as LayerTableRecord;
                if (ltr != null) eff = ltr.Color;
            }

            if (eff.IsByBlock) return "ByBlock";
            if (eff.IsByLayer) return "ByLayer";
            if (eff.IsByAci)   return AciName(eff.ColorIndex);
            return string.Format("RGB {0},{1},{2}", eff.ColorValue.R, eff.ColorValue.G, eff.ColorValue.B);
        }

        static string AciName(short aci)
        {
            switch (aci)
            {
                case 1: return "красный";
                case 2: return "жёлтый";
                case 3: return "зелёный";
                case 4: return "голубой";
                case 5: return "синий";
                case 6: return "фиолетовый";
                case 7: return "белый/чёрный";
                default: return "цвет " + aci;
            }
        }

        /// <summary>Маленький модальный диалог "в чертеже уже есть таблица — обновить или
        /// создать новую?". Возвращает true — обновить, false — создать новую, null — отмена.</summary>
        private static bool? AskUpdateOrCreate(string commandName)
        {
            using (var form = new Form())
            {
                form.Text = commandName;
                form.FormBorderStyle = FormBorderStyle.FixedDialog;
                form.StartPosition = FormStartPosition.CenterScreen;
                form.MinimizeBox = false;
                form.MaximizeBox = false;
                form.ShowInTaskbar = false;
                form.ClientSize = new Size(380, 130);

                var label = new Label
                {
                    Text = "В чертеже уже есть таблица, созданная этой командой.\nЧто сделать?",
                    Dock = DockStyle.Top,
                    Height = 60,
                    TextAlign = ContentAlignment.MiddleCenter,
                };

                var btnUpdate = new Button { Text = "Обновить", Width = 110, DialogResult = DialogResult.Yes };
                var btnCreate = new Button { Text = "Создать новую", Width = 130, DialogResult = DialogResult.No };
                var btnCancel = new Button { Text = "Отмена", Width = 90, DialogResult = DialogResult.Cancel };

                var panel = new FlowLayoutPanel
                {
                    Dock = DockStyle.Bottom,
                    FlowDirection = System.Windows.Forms.FlowDirection.RightToLeft,
                    Height = 50,
                    Padding = new Padding(10),
                };
                panel.Controls.Add(btnCancel);
                panel.Controls.Add(btnCreate);
                panel.Controls.Add(btnUpdate);

                form.Controls.Add(label);
                form.Controls.Add(panel);
                form.AcceptButton = btnUpdate;
                form.CancelButton = btnCancel;

                DialogResult result = form.ShowDialog();
                if (result == DialogResult.Yes) return true;
                if (result == DialogResult.No) return false;
                return null;
            }
        }

        /// <summary>Применяет текстовый стиль styleName ко ВСЕМ ячейкам таблицы — если такого
        /// стиля нет в чертеже, предупреждает и оставляет стиль по умолчанию, не прерывая
        /// выполнение команды.</summary>
        private static void ApplyTextStyle(Database db, Transaction tr, Table tb, string styleName, Editor ed)
        {
            TextStyleTable tst = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
            if (!tst.Has(styleName))
            {
                ed.WriteMessage($"\nВнимание: текстовый стиль \"{styleName}\" не найден в чертеже — оставлен стиль по умолчанию.");
                return;
            }

            ObjectId styleId = tst[styleName];
            for (int r = 0; r < tb.Rows.Count; r++)
                for (int c = 0; c < tb.Columns.Count; c++)
                    tb.Cells[r, c].TextStyleId = styleId;
        }

        /// <summary>Создаёт слой layerName, если его ещё нет в чертеже (цвет по умолчанию, линия
        /// Continuous).</summary>
        private static void EnsureLayer(Database db, Transaction tr, string layerName)
        {
            LayerTable lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (lt.Has(layerName)) return;

            lt.UpgradeOpen();
            LayerTableRecord ltr = new LayerTableRecord { Name = layerName };
            lt.Add(ltr);
            tr.AddNewlyCreatedDBObject(ltr, true);
        }

        /// <summary>Ищет в переданном пространстве (модель либо paper space конкретного листа)
        /// таблицу, помеченную XData этого приложения (созданную предыдущим запуском команды в
        /// ЭТОМ ЖЕ пространстве). ObjectId.Null, если не найдена.</summary>
        private static ObjectId FindExistingTableId(Transaction tr, BlockTableRecord space, string appName)
        {
            foreach (ObjectId id in space)
            {
                Table candidate = tr.GetObject(id, OpenMode.ForRead) as Table;
                if (candidate == null) continue;
                if (candidate.GetXDataForApplication(appName) != null)
                    return id;
            }
            return ObjectId.Null;
        }

        /// <summary>Регистрирует имя приложения в таблице RegApp чертежа, если ещё не
        /// зарегистрировано — без этого запись XData с этим именем приложения не разрешена.</summary>
        private static void EnsureRegApp(Database db, Transaction tr, string appName)
        {
            RegAppTable rat = (RegAppTable)tr.GetObject(db.RegAppTableId, OpenMode.ForRead);
            if (rat.Has(appName)) return;

            rat.UpgradeOpen();
            RegAppTableRecord ratr = new RegAppTableRecord { Name = appName };
            rat.Add(ratr);
            tr.AddNewlyCreatedDBObject(ratr, true);
        }
    }
}
