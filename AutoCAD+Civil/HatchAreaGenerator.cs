using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
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
        const double LevelRowHeight  = 8.0; // строка-заголовок уровня ("по грунту", "по кровле" и т.п.)
        const double DataRowHeight   = 8.0;

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
        const double LegendLevelRowHeight  = 8.0;
        const double LegendDataRowHeight   = 8.0; // = SwatchHeight(5) + отступы 1.5×2 — впритык, без запаса
        const double SwatchWidth  = 35.0; // размер прямоугольника-миниатюры в "Обозначение", мм
        const double SwatchHeight = 5.0;
        const double SwatchPatternAngle = 0.0; // угол/масштаб узора в миниатюре — фиксированные,
        const double SwatchPatternScale = 0.3; // НЕ копируются с реальной штриховки (см. EnsureSwatchBlock)
        const string LegendAppName = "HATCHLEGEND_GEN";

        // ---- Общее для обеих таблиц ----
        const string TextStyleName = "Основной";     // текстовый стиль для всех ячеек таблицы
        const string LayerName     = "KPSP-Надписи"; // слой таблицы и всего её текста
        const double CellMarginH = 1.5; // отступы содержимого ячеек от границы, ед. чертежа (мм)
        const double CellMarginV = 1.5;
        const double TitleTextHeight  = 5.0;
        const double HeaderTextHeight = 3.0;
        const double DataTextHeight   = 2.5;

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

        // ============ Журнал строк (чтобы не терять ручные строки пользователя при обновлении) ============

        /// <summary>Один "слот" из журнала строк, который команда хранит в XData таблицы (см.
        /// SaveManifest/LoadManifest) — чем была строка номер N (считая от 2, т.е. после
        /// заголовка и шапки) при ПРОШЛОМ запуске: либо заголовком уровня (IsLevel=true, Level),
        /// либо строкой данных конкретного типа (TypeNumber). При следующем обновлении по этому
        /// журналу отличаем СВОИ строки от добавленных пользователем вручную — те не входят в
        /// журнал и остаются на прежнем месте (см. ForeignRow ниже), а не затираются/удаляются.</summary>
        private struct ManifestEntry
        {
            public bool IsLevel;
            public string Level;
            public int TypeNumber;
            public static ManifestEntry ForLevel(string level) => new ManifestEntry { IsLevel = true, Level = level };
            public static ManifestEntry ForType(int typeNumber) => new ManifestEntry { IsLevel = false, TypeNumber = typeNumber };
        }

        /// <summary>Снимок содержимого одной строки, добавленной пользователем ВРУЧНУЮ (не
        /// нашедшейся в журнале прошлого запуска) — текст/выравнивание/картинка блока по каждой
        /// колонке, чтобы буквально вернуть её на место после пересборки таблицы. AnchorIndex —
        /// индекс элемента журнала, СРАЗУ ПОСЛЕ которого эта строка стояла в прошлый раз (-1 —
        /// перед самой первой нашей строкой) — по нему строка возвращается на то же место
        /// относительно соседних "наших" строк, даже если те сами сдвинулись.</summary>
        private class ForeignRow
        {
            public int AnchorIndex;
            public string[] Text;
            public CellAlignment[] Align;
            public ObjectId[] BlockId;
        }

        /// <summary>Один элемент финального плана строк таблицы — либо "своя" строка (заголовок
        /// уровня или данные типа), либо "чужая" (см. ForeignRow) — используется, чтобы построить
        /// полный порядок строк ПЕРЕД тем, как менять размер таблицы и записывать содержимое (см.
        /// CreateHatchTable/CreateHatchLegend).</summary>
        private class RowPlanItem
        {
            public bool IsForeign;
            public ForeignRow Foreign;
            public bool IsLevel;
            public string Level;
            public KeyValuePair<string, HatchTypeEntry> DataItem;
        }

        private const char ManifestSep = '';

        /// <summary>Сохраняет журнал строк в XData таблицы — заодно это и есть метка "таблица
        /// создана этой командой" (см. FindExistingTableId), отдельный маркер больше не нужен.
        /// Код 1000 (ExtendedDataAsciiString) ограничен ~255 символами на запись — режем длинную
        /// строку на куски и пишем несколько записей подряд под одним RegApp (это допустимо и
        /// штатно читается обратно как единый набор через GetXDataForApplication).</summary>
        static void SaveManifest(Table tb, string appName, List<ManifestEntry> manifest)
        {
            string serialized = string.Join(ManifestSep.ToString(),
                manifest.Select(e => e.IsLevel ? "L" + e.Level : "T" + e.TypeNumber));

            var values = new List<TypedValue> { new TypedValue((int)DxfCode.ExtendedDataRegAppName, appName) };
            const int chunkSize = 250;
            if (serialized.Length == 0)
            {
                values.Add(new TypedValue((int)DxfCode.ExtendedDataAsciiString, ""));
            }
            else
            {
                for (int i = 0; i < serialized.Length; i += chunkSize)
                    values.Add(new TypedValue((int)DxfCode.ExtendedDataAsciiString,
                        serialized.Substring(i, Math.Min(chunkSize, serialized.Length - i))));
            }
            tb.XData = new ResultBuffer(values.ToArray());
        }

        /// <summary>Читает журнал строк из XData таблицы (см. SaveManifest). null, если XData для
        /// этого приложения нет вовсе (например, таблица ещё не отслеживается) — ОТ таблиц, уже
        /// созданных СТАРОЙ версией этой команды (до появления журнала), XData для appName как
        /// раз есть (там был простой маркер "table" без структуры журнала) — в этом случае парсинг
        /// просто не найдёт ни одной записи и вернёт пустой список, что тоже безопасно
        /// (см. использование в CreateHatchTable/CreateHatchLegend — пустой журнал означает "не
        /// нашли ни одной ожидаемой строки", т.е. все текущие строки будут сочтены чужими один
        /// раз, после чего журнал перезапишется правильно).</summary>
        static List<ManifestEntry> LoadManifest(Table tb, string appName)
        {
            ResultBuffer rb = tb.GetXDataForApplication(appName);
            if (rb == null) return null;

            var sb = new StringBuilder();
            foreach (TypedValue tv in rb)
                if (tv.TypeCode == (int)DxfCode.ExtendedDataAsciiString)
                    sb.Append((string)tv.Value);

            var result = new List<ManifestEntry>();
            string serialized = sb.ToString();
            if (serialized.Length == 0) return result;
            foreach (string part in serialized.Split(ManifestSep))
            {
                if (part.Length == 0) continue;
                if (part[0] == 'L') result.Add(ManifestEntry.ForLevel(part.Substring(1)));
                else if (part[0] == 'T' && int.TryParse(part.Substring(1), out int t)) result.Add(ManifestEntry.ForType(t));
            }
            return result;
        }

        /// <summary>Снимает содержимое строки row (все nCols колонок: текст, выравнивание,
        /// картинка блока, если есть) — для сохранения "чужой" строки перед пересборкой таблицы.</summary>
        static ForeignRow CaptureRow(Table tb, int row, int nCols, int anchorIndex)
        {
            var snap = new ForeignRow
            {
                AnchorIndex = anchorIndex,
                Text = new string[nCols],
                Align = new CellAlignment[nCols],
                BlockId = new ObjectId[nCols],
            };
            for (int c = 0; c < nCols; c++)
            {
                snap.Text[c] = tb.Cells[row, c].TextString;
                snap.Align[c] = tb.Cells[row, c].Alignment ?? CellAlignment.MiddleLeft;
                try { snap.BlockId[c] = tb.GetBlockTableRecordId(row, c, 0); }
                catch { snap.BlockId[c] = ObjectId.Null; }
            }
            return snap;
        }

        /// <summary>Возвращает снятое содержимое строки (см. CaptureRow) в ячейки row.</summary>
        static void RestoreRow(Table tb, int row, ForeignRow snap)
        {
            for (int c = 0; c < snap.Text.Length; c++)
            {
                if (!snap.BlockId[c].IsNull)
                    tb.SetBlockTableRecordId(row, c, snap.BlockId[c], false);
                else if (!string.IsNullOrEmpty(snap.Text[c]))
                    tb.Cells[row, c].TextString = snap.Text[c];
                tb.Cells[row, c].Alignment = snap.Align[c];
            }
        }

        /// <summary>Строит финальный порядок строк: "свои" (managedSpecs, в нужном порядке) плюс
        /// "чужие" (foreignRows), вставленные на прежние места по AnchorIndex — сразу после того
        /// элемента managedSpecs, после которого они стояли в прошлый раз (-1 — перед самым
        /// первым). "Чужие" строки, чей AnchorIndex указывает на уже несуществующий (например,
        /// managedSpecs стало меньше) элемент, уезжают в конец — лучше, чем потерять их совсем.</summary>
        static List<RowPlanItem> BuildRowPlan(List<RowPlanItem> managedSpecs, List<ForeignRow> foreignRows)
        {
            var plan = new List<RowPlanItem>();
            plan.AddRange(foreignRows.Where(f => f.AnchorIndex == -1)
                .Select(f => new RowPlanItem { IsForeign = true, Foreign = f }));
            for (int i = 0; i < managedSpecs.Count; i++)
            {
                plan.Add(managedSpecs[i]);
                plan.AddRange(foreignRows.Where(f => f.AnchorIndex == i)
                    .Select(f => new RowPlanItem { IsForeign = true, Foreign = f }));
            }
            plan.AddRange(foreignRows.Where(f => f.AnchorIndex >= managedSpecs.Count)
                .Select(f => new RowPlanItem { IsForeign = true, Foreign = f }));
            return plan;
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

                const int nCols = 5;

                Table tb;
                // Текст, УЖЕ стоящий в ячейке таблицы, главнее вычисленного значения — тот же
                // принцип, что и в BLOCKTABLE: если пользователь вручную поправил "Наименование"
                // прямо в таблице, повторный запуск не должен затирать эту правку своим
                // вычисленным значением, даже если оно отличается — и, как и в BLOCKTABLE, эта
                // правка ЗАПИСЫВАЕТСЯ ОБРАТНО в источник (там — атрибут блока, здесь — поле
                // "название" в словаре, см. запись в nameMap ниже и повторный SaveNameMap), чтобы
                // источник снова совпал с тем, что показано в таблице. "Примечание" источника не
                // имеет вовсе (только вручную) — сохраняется как есть, без сравнения.
                // Ключ сохранения для ОБОИХ — TypeNumber (столбец "Тип"): он стабилен (см.
                // HatchTypeEntry.TypeNumber), в отличие от текста "Наименование", который теперь
                // сам может быть тем, что мы сохраняем/перезаписываем.
                Dictionary<int, string> preservedNames = new Dictionary<int, string>();
                Dictionary<int, string> preservedNotes = new Dictionary<int, string>();
                // Строки, добавленные пользователем ВРУЧНУЮ (не найденные в журнале прошлого
                // запуска — см. ManifestEntry/LoadManifest выше) — сохраняются буквально и
                // возвращаются на прежнее относительное место после пересборки (см. BuildRowPlan).
                List<ForeignRow> foreignRows = new List<ForeignRow>();

                if (isUpdate)
                {
                    tb = (Table)tr.GetObject(existingId, OpenMode.ForWrite);

                    List<ManifestEntry> oldManifest = LoadManifest(tb, AppName);
                    if (oldManifest != null)
                    {
                        int mi = 0;
                        for (int r = 2; r < tb.Rows.Count; r++)
                        {
                            bool isMatch = false;
                            if (mi < oldManifest.Count)
                            {
                                ManifestEntry expected = oldManifest[mi];
                                if (expected.IsLevel)
                                {
                                    isMatch = tb.Cells[r, 0].TextString == expected.Level;
                                }
                                else if (int.TryParse(tb.Cells[r, 2].TextString, out int rowType) && rowType == expected.TypeNumber)
                                {
                                    isMatch = true;
                                    string rowName = tb.Cells[r, 1].TextString;
                                    if (!string.IsNullOrEmpty(rowName)) preservedNames[rowType] = rowName;
                                    string rowNote = tb.Cells[r, 4].TextString;
                                    if (!string.IsNullOrEmpty(rowNote)) preservedNotes[rowType] = rowNote;
                                }
                            }

                            if (isMatch) { mi++; continue; }
                            foreignRows.Add(CaptureRow(tb, r, nCols, mi - 1));
                        }
                    }
                    else
                    {
                        // Журнала нет — таблица создана до появления этой возможности. Работаем по
                        // старой эвристике ("Поз." число = строка данных), ничего не выделяем как
                        // чужое (чтобы не размножить существующие строки уровня как "чужие" —
                        // отличить их от НАСТОЯЩИХ ручных строк без журнала нечем). Журнал появится
                        // после этого запуска, и со следующего обновления сохранение ручных строк
                        // заработает.
                        for (int r = 2; r < tb.Rows.Count; r++)
                        {
                            if (!int.TryParse(tb.Cells[r, 0].TextString, out _)) continue;
                            if (!int.TryParse(tb.Cells[r, 2].TextString, out int rowType)) continue;
                            string rowName = tb.Cells[r, 1].TextString;
                            if (!string.IsNullOrEmpty(rowName)) preservedNames[rowType] = rowName;
                            string rowNote = tb.Cells[r, 4].TextString;
                            if (!string.IsNullOrEmpty(rowNote)) preservedNotes[rowType] = rowNote;
                        }
                    }

                    // Снимаем ВСЕ объединения ячеек, оставшиеся с прошлой раскладки (строки-
                    // заголовки уровня могли оказаться на других номерах строк после обновления —
                    // группировка/сортировка не стабильна между запусками) — иначе запись текста
                    // в отдельную колонку у бывшей объединённой строки может повести себя
                    // непредсказуемо. Объединяем заново ниже, уже по новой раскладке.
                    if (tb.Rows.Count > 0 && tb.Columns.Count > 0)
                        tb.UnmergeCells(CellRange.Create(tb, 0, 0, tb.Rows.Count - 1, tb.Columns.Count - 1));
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

                // "Свои" строки (уровни+данные) в нужном порядке, затем "чужие" — на прежние
                // относительные места (см. BuildRowPlan). Отсюда же — итоговое число строк.
                var managedSpecs = new List<RowPlanItem>();
                foreach (LevelSection section in sections)
                {
                    if (!string.IsNullOrEmpty(section.Level))
                        managedSpecs.Add(new RowPlanItem { IsLevel = true, Level = section.Level });
                    foreach (KeyValuePair<string, HatchTypeEntry> kv in section.Items)
                        managedSpecs.Add(new RowPlanItem { IsLevel = false, DataItem = kv });
                }
                int dataRowCount = managedSpecs.Count(s => !s.IsLevel);
                List<RowPlanItem> rowPlan = BuildRowPlan(managedSpecs, foreignRows);
                int nRows = 2 + rowPlan.Count; // заголовок + шапка + (уровни + данные + чужие строки)

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

                // Данные — по плану rowPlan; "Поз." сквозная нумерация только по своим строкам
                // данных (чужие строки её не сбивают и сохраняют собственный текст как есть).
                string fmt = "F" + AreaDecimals;
                bool dictionaryChanged = false; // текст таблицы переписал nameMap — см. ниже
                int pos = 1;
                int row = 2;
                var levelRows = new List<int>();
                var newManifest = new List<ManifestEntry>();

                foreach (RowPlanItem item in rowPlan)
                {
                    if (item.IsForeign)
                    {
                        RestoreRow(tb, row, item.Foreign);
                        row++;
                        continue;
                    }

                    if (item.IsLevel)
                    {
                        levelRows.Add(row);
                        tb.Cells[row, 0].TextString = item.Level;
                        tb.Cells[row, 0].Alignment = CellAlignment.MiddleLeft;
                        // Явно очищаем остальные колонки — при обновлении эта же строка на
                        // прошлой раскладке могла быть строкой ДАННЫХ (текст в этих колонках), а
                        // TextString выше трогает только колонку 0 — без явной очистки старый
                        // текст остаётся висеть под новой строкой уровня.
                        for (int c = 1; c < nCols; c++)
                            tb.Cells[row, c].Contents.Clear();
                        newManifest.Add(ManifestEntry.ForLevel(item.Level));
                        row++;
                        continue;
                    }

                    HatchTypeEntry entry = item.DataItem.Value;
                    HatchGroupInfo info = groups[item.DataItem.Key];
                    double area = info.Area * AreaScale;

                    // Текст таблицы главнее вычисленного значения (см. пояснение у
                    // preservedNames выше) — и если он отличается, записываем его обратно в
                    // словарь (nameMap), а не только показываем поверх.
                    string computedName = Resolve(entry, info.AutoLabel);
                    string name = computedName;
                    if (preservedNames.TryGetValue(entry.TypeNumber, out string existingName)
                        && !string.IsNullOrEmpty(existingName) && existingName != computedName)
                    {
                        name = existingName;
                        entry.Name = existingName;
                        dictionaryChanged = true;
                    }

                    tb.Cells[row, 0].TextString = pos.ToString();
                    tb.Cells[row, 0].Alignment = CellAlignment.MiddleCenter;

                    tb.Cells[row, 1].TextString = name;
                    tb.Cells[row, 1].Alignment = CellAlignment.MiddleLeft;

                    tb.Cells[row, 2].TextString = entry.TypeNumber.ToString();
                    tb.Cells[row, 2].Alignment = CellAlignment.MiddleCenter;

                    tb.Cells[row, 3].TextString = area.ToString(fmt);
                    tb.Cells[row, 3].Alignment = CellAlignment.MiddleCenter;

                    tb.Cells[row, 4].TextString = preservedNotes.TryGetValue(entry.TypeNumber, out string note) ? note : "";
                    tb.Cells[row, 4].Alignment = CellAlignment.MiddleCenter;

                    newManifest.Add(ManifestEntry.ForType(entry.TypeNumber));
                    pos++;
                    row++;
                }

                // Ручные правки "Наименование" прямо в таблице переписали nameMap (см. цикл выше)
                // — пересохраняем словарь, чтобы источник (файл) снова совпал с тем, что показано
                // в таблице.
                if (dictionaryChanged)
                    SaveNameMap(ConfigPath, nameMap);

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

                // Заголовок таблицы (строка 0) объединяем СРАЗУ, до любого GenerateLayout — она
                // никогда не появляется через InsertRows (тот работает только с хвостом таблицы,
                // начиная со строки 2), так что "отстаиваться" ей не нужно. Если вызвать
                // GenerateLayout, пока заголовок ещё НЕ объединён, длинный текст вминается в узкую
                // колонку "Поз." (9 мм), переносится по одной букве на строку — и GenerateLayout
                // в этот момент раздувает высоту строки под этот перенос (проверено вживую: высота
                // "уезжала" до сотен мм); объединение ПОСЛЕ этого высоту обратно уже не сжимает.
                // Заголовок объединяем явно, а не полагаемся на автоматическое поведение стиля
                // таблицы: при ОБНОВЛЕНИИ ранее снимаются ВСЕ объединения на таблице целиком (см.
                // UnmergeCells выше — нужно было для сброса "зависших" объединений у бывших строк
                // уровня), и это заодно снимало объединение и у самой строки заголовка.
                tb.MergeCells(CellRange.Create(tb, 0, 0, 0, nCols - 1));

                // Строки-заголовки уровня, добавленные через InsertRows (уровней стало больше, чем
                // было), "не готовы" к объединению сразу — проверено вживую: MergeCells на такой
                // строке тихо не срабатывает, пока таблица не пересчитает раскладку хотя бы раз
                // ПОСЛЕ вставки. Промежуточный GenerateLayout "устаканивает" структуру перед их
                // MergeCells — заголовок к этому моменту уже объединён и не пострадает.
                tb.GenerateLayout();
                foreach (int r in levelRows)
                    tb.MergeCells(CellRange.Create(tb, r, 0, r, nCols - 1));

                // Тот же промежуточный GenerateLayout мог по той же причине (короткий текст
                // уровня в узкой НЕобъединённой колонке "Поз." переносится и раздувает высоту)
                // увеличить высоту строк уровня ДО их объединения — переустанавливаем явно ПОСЛЕ
                // объединения, когда тексту уже ничего не мешает уместиться в полную ширину.
                foreach (int r in levelRows)
                    tb.Rows[r].Height = LevelRowHeight;

                // Текстовый стиль — на ВСЕ ячейки, применяем после того как таблица дорощена/дана
                // нужным числом строк, но ДО GenerateLayout (он пересчитывает размеры ячеек под
                // содержимое и шрифт).
                ApplyTextStyle(db, tr, tb, TextStyleName, ed);

                tb.GenerateLayout();
                // Сбрасываем внутренний кеш графики содержимого ячеек — тот же приём, что и в
                // HATCHLEGEND (см. пояснение там): без него после объединения/переразметки часть
                // содержимого ячеек может отрисовываться по старому состоянию.
                tb.RecomputeTableBlock(true);

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
                    EnsureRegApp(db, tr, AppName);
                }

                // Журнал строк — заодно и метка "таблица создана этой командой" (см.
                // FindExistingTableId), пишем на каждом запуске (не только при создании), чтобы
                // при следующем обновлении журнал отражал АКТУАЛЬНУЮ раскладку.
                SaveManifest(tb, AppName, newManifest);

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

                const int nCols = 3;

                Table tb;
                // Текст "Наименование", уже стоящий в таблице, главнее вычисленного значения —
                // тот же принцип, что и в HATCHTABLE/BLOCKTABLE: правка записывается ОБРАТНО в
                // словарь (см. запись в nameMap ниже). Ключ — номер типа, извлечённый из соседней
                // ячейки "Тип" ("тип N") — она отдельная колонка (см. столбец 1 ниже), а не текст,
                // склеенный с названием, так что парсить несложно.
                Dictionary<int, string> preservedNames = new Dictionary<int, string>();
                // Строки, добавленные пользователем ВРУЧНУЮ (не найденные в журнале прошлого
                // запуска — см. ManifestEntry/LoadManifest) — сохраняются буквально и
                // возвращаются на прежнее относительное место после пересборки (см. BuildRowPlan).
                List<ForeignRow> foreignRows = new List<ForeignRow>();

                if (isUpdate)
                {
                    tb = (Table)tr.GetObject(existingId, OpenMode.ForWrite);

                    List<ManifestEntry> oldManifest = LoadManifest(tb, LegendAppName);
                    if (oldManifest != null)
                    {
                        int mi = 0;
                        for (int r = 2; r < tb.Rows.Count; r++)
                        {
                            bool isMatch = false;
                            if (mi < oldManifest.Count)
                            {
                                ManifestEntry expected = oldManifest[mi];
                                if (expected.IsLevel)
                                {
                                    isMatch = tb.Cells[r, 0].TextString == expected.Level;
                                }
                                else
                                {
                                    string typeText = tb.Cells[r, 1].TextString;
                                    if (!string.IsNullOrEmpty(typeText)
                                        && typeText.StartsWith("тип ", StringComparison.OrdinalIgnoreCase)
                                        && int.TryParse(typeText.Substring(4).Trim(), out int rowType)
                                        && rowType == expected.TypeNumber)
                                    {
                                        isMatch = true;
                                        string rowName = tb.Cells[r, 0].TextString;
                                        if (!string.IsNullOrEmpty(rowName)) preservedNames[rowType] = rowName;
                                    }
                                }
                            }

                            if (isMatch) { mi++; continue; }
                            foreignRows.Add(CaptureRow(tb, r, nCols, mi - 1));
                        }
                    }
                    else
                    {
                        // Журнала нет — таблица создана до появления этой возможности. Работаем по
                        // старой эвристике, ничего не выделяем как чужое (см. пояснение в
                        // CreateHatchTable) — журнал появится после этого запуска.
                        for (int r = 2; r < tb.Rows.Count; r++)
                        {
                            string typeText = tb.Cells[r, 1].TextString;
                            if (string.IsNullOrEmpty(typeText) || !typeText.StartsWith("тип ", StringComparison.OrdinalIgnoreCase))
                                continue;
                            if (!int.TryParse(typeText.Substring(4).Trim(), out int rowType)) continue;
                            string rowName = tb.Cells[r, 0].TextString;
                            if (!string.IsNullOrEmpty(rowName)) preservedNames[rowType] = rowName;
                        }
                    }

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

                // "Свои" строки (уровни+данные) в нужном порядке, затем "чужие" — на прежние
                // относительные места (см. BuildRowPlan). Отсюда же — итоговое число строк.
                var managedSpecs = new List<RowPlanItem>();
                foreach (LevelSection section in sections)
                {
                    if (!string.IsNullOrEmpty(section.Level))
                        managedSpecs.Add(new RowPlanItem { IsLevel = true, Level = section.Level });
                    foreach (KeyValuePair<string, HatchTypeEntry> kv in section.Items)
                        managedSpecs.Add(new RowPlanItem { IsLevel = false, DataItem = kv });
                }
                int dataRowCount = managedSpecs.Count(s => !s.IsLevel);
                List<RowPlanItem> rowPlan = BuildRowPlan(managedSpecs, foreignRows);
                int nRows = 2 + rowPlan.Count;

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

                bool dictionaryChanged = false; // текст таблицы переписал nameMap — см. ниже
                int row = 2;
                var levelRows = new List<int>();
                var newManifest = new List<ManifestEntry>();

                foreach (RowPlanItem item in rowPlan)
                {
                    if (item.IsForeign)
                    {
                        RestoreRow(tb, row, item.Foreign);
                        row++;
                        continue;
                    }

                    if (item.IsLevel)
                    {
                        levelRows.Add(row);
                        // Уровень — БЕЗ объединения ячеек (по просьбе), текст по центру своей
                        // ячейки (столбец 0), столбцы 1 и 2 в этой строке остаются пустыми.
                        tb.Cells[row, 0].TextString = item.Level;
                        tb.Cells[row, 0].Alignment = CellAlignment.MiddleCenter;
                        // Явно очищаем столбцы 1 (тип) и 2 (миниатюра) — при обновлении эта же
                        // строка на прошлой раскладке могла быть строкой ДАННЫХ с картинкой
                        // блока; TextString выше трогает только столбец 0, и без явной очистки
                        // старые "тип N" и миниатюра остаются висеть под новой строкой уровня.
                        tb.Cells[row, 1].Contents.Clear();
                        tb.Cells[row, 2].Contents.Clear();
                        newManifest.Add(ManifestEntry.ForLevel(item.Level));
                        row++;
                        continue;
                    }

                    HatchTypeEntry entry = item.DataItem.Value;
                    HatchGroupInfo info = groups[item.DataItem.Key];

                    // Текст таблицы главнее вычисленного значения (см. пояснение у
                    // preservedNames выше) — и если он отличается, записываем его обратно в
                    // словарь, а не только показываем поверх.
                    string computedName = Resolve(entry, info.AutoLabel);
                    string name = computedName;
                    if (preservedNames.TryGetValue(entry.TypeNumber, out string existingName)
                        && !string.IsNullOrEmpty(existingName) && existingName != computedName)
                    {
                        name = existingName;
                        entry.Name = existingName;
                        dictionaryChanged = true;
                    }

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

                    newManifest.Add(ManifestEntry.ForType(entry.TypeNumber));
                    row++;
                }

                // Ручные правки "Наименование" прямо в таблице переписали nameMap (см. цикл выше)
                // — пересохраняем словарь, чтобы источник (файл) снова совпал с тем, что показано
                // в таблице.
                if (dictionaryChanged)
                    SaveNameMap(ConfigPath, nameMap);

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
                // GenerateLayout пересчитывает только раскладку (размеры/позиции ячеек) — у
                // таблицы есть СВОЙ внутренний кеш графики содержимого ячеек ("блок таблицы"),
                // который не сбрасывается сам по себе при изменении определений блоков-миниатюр
                // (EnsureSwatchBlock переопределяет их содержимое в этой же транзакции, до этого
                // места). Без принудительного пересчёта ячейки показывают старую картинку, даже
                // если сам блок и его свойства (угол/масштаб штриховки) в базе уже верные.
                tb.RecomputeTableBlock(true);

                if (!isUpdate)
                {
                    BlockTableRecord targetSpaceWrite = (BlockTableRecord)tr.GetObject(
                        targetSpace.ObjectId, OpenMode.ForWrite);
                    targetSpaceWrite.AppendEntity(tb);
                    tr.AddNewlyCreatedDBObject(tb, true);
                    EnsureRegApp(db, tr, LegendAppName);
                }

                // Журнал строк — заодно и метка "таблица создана этой командой" (см.
                // FindExistingTableId), пишем на каждом запуске (не только при создании), чтобы
                // при следующем обновлении журнал отражал АКТУАЛЬНУЮ раскладку.
                SaveManifest(tb, LegendAppName, newManifest);

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

            var loopIds = new ObjectIdCollection { boundary.ObjectId };
            hatch.AppendLoop(HatchLoopTypes.Default, loopIds);
            hatch.EvaluateHatch(true); // первый расчёт геометрии узора — с параметрами по умолчанию

            // Угол/масштаб/цвета — ПОСЛЕ первого EvaluateHatch. Проверено вживую: если задать их
            // раньше (до контура/первого расчёта) и вызвать EvaluateHatch один раз, значение
            // PatternScale в свойствах записывается верно, но геометрия узора отрисовывается по
            // старому (умолчательному) масштабу — ровно то же самое, что чинит повторный ввод
            // ТОГО ЖЕ значения в палитре свойств вручную. Поэтому пересчитываем ЕЩЁ РАЗ уже после
            // того, как параметры выставлены на уже "разрешённом" (evaluated) объекте.
            hatch.PatternAngle = SwatchPatternAngle;
            hatch.PatternScale = SwatchPatternScale;
            hatch.Color = info.Color;
            if (info.BackgroundColor != null) hatch.BackgroundColor = info.BackgroundColor;
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
        /// "тип | уровень | название" (уровень/название могут быть пустыми), где название "*"
        /// ТОЖЕ означает исключение — это единственный способ исключить тип, у которого уже есть
        /// присвоенный номер (просто заменить всё значение на голое "*" нельзя: следующее
        /// сохранение присвоило бы номер заново, потеряв связь со старым). Если первая часть не
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

            if (entry.Name == "*")
            {
                entry.Excluded = true;
                entry.Name = "";
            }
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
                sw.WriteLine("# название — пусто = авто-название. '*' вместо названия (или вместо всего значения) — не учитывать штриховку вовсе.");
                sw.WriteLine();
                foreach (KeyValuePair<string, HatchTypeEntry> kv in map.OrderBy(kv => kv.Key, StringComparer.CurrentCultureIgnoreCase))
                {
                    // Если у исключённого типа уже есть номер (обычный случай — исключают, когда
                    // тип уже был на чертеже) — сохраняем "тип | уровень | *", а не голое "*": так
                    // при повторном включении (убрать "*") тип/уровень не потеряются и не
                    // присвоится новый номер взамен старого.
                    string valueText = kv.Value.Excluded
                        ? (kv.Value.TypeNumber > 0 ? $"{kv.Value.TypeNumber} | {kv.Value.Level} | *" : "*")
                        : $"{kv.Value.TypeNumber} | {kv.Value.Level} | {kv.Value.Name}";
                    sw.WriteLine(kv.Key + " = " + valueText);
                }
            }
        }

        static string Resolve(HatchTypeEntry entry, string autoLabel)
            => !string.IsNullOrWhiteSpace(entry.Name) ? entry.Name : autoLabel;

        /// <summary>Группирует записи по уровню — сразу, даже если размечена только часть типов.
        /// Размеченные уровни упорядочены по МИНИМАЛЬНОМУ номеру типа внутри них (порядок первого
        /// появления уровня в словаре) — предсказуемо и не скачет между запусками. Ещё не
        /// размеченные типы (Level пуст) собираются в ОДНУ секцию с Level="" и всегда идут
        /// ПОСЛЕДНЕЙ, не участвуя в сортировке по номеру типа вместе с размеченными — так
        /// назначение уровня одному типу перемещает только его, не перетасовывая остальные.
        /// Секция с Level="" — сигнал вызывающему коду не рисовать для неё строку-заголовок.</summary>
        static List<LevelSection> GroupByLevel(IEnumerable<KeyValuePair<string, HatchTypeEntry>> entries)
        {
            var list = entries.ToList();

            var sections = list
                .Where(kv => !string.IsNullOrWhiteSpace(kv.Value.Level))
                .GroupBy(kv => kv.Value.Level)
                .Select(g => new LevelSection { Level = g.Key, Items = g.OrderBy(kv => kv.Value.TypeNumber).ToList() })
                .OrderBy(s => s.Items.Min(kv => kv.Value.TypeNumber))
                .ToList();

            var unleveled = list
                .Where(kv => string.IsNullOrWhiteSpace(kv.Value.Level))
                .OrderBy(kv => kv.Value.TypeNumber)
                .ToList();
            if (unleveled.Count > 0)
                sections.Add(new LevelSection { Level = "", Items = unleveled });

            return sections;
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
