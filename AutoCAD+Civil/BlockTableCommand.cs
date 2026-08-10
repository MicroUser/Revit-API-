using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;

// Регистрируем класс с командами
[assembly: CommandClass(typeof(BlockTableGenerator.Commands))]

namespace BlockTableGenerator
{
    public class Commands
    {
        // ------------------------------------------------------------------
        // НАСТРОЙКИ — правь под свой чертёж
        // ------------------------------------------------------------------
        const string TableTitle       = "Ведомость малых архитектурных форм и переносных изделий";
        const bool   SkipXrefs        = true;  // true = не считать внешние ссылки (xref)
        const string IncludeKeyword   = "МАФ";        // считать ТОЛЬКО блоки, где имя содержит это ("" = все)
        const string ExcludeKeyword   = "спортивный"; // ...но НЕ содержит это ("" = ничего не исключать)

        // Теги текстовых атрибутов блока (Tag атрибута, регистр не важен) — значение атрибута
        // конкретной вставки блока идёт в соответствующую колонку. "" = не использовать атрибут,
        // тогда колонка "Наименование" берёт имя блока (br.Name), а "Примечание" остаётся пустой
        // (или сохранённой вручную — см. дальше).
        const string NameAttributeTag = "Наименование";
        const string NoteAttributeTag = "Примечание";
        const string TextStyleName    = "Основной"; // текстовый стиль для всех ячеек таблицы
        const string LayerName        = "KPSP-Надписи"; // слой таблицы и всего её текста

        // Отступы содержимого ячеек от границы, ед. чертежа (мм) — НЕ масштабируются вместе
        // с остальными размерами (заданы напрямую, отдельно от исходных 1.0/2.5/... мм).
        const double CellMarginH = 1.5;
        const double CellMarginV = 1.5;

        // Ширины столбцов, ед. чертежа (мм) — 0:"Поз." 1:"Обозначение" 2:"Наименование"
        // 3:"Кол." 4:"Примечание". Исходно 1.0/2.5/10.0/1.0/4.0 мм (сумма 18.5 = ширина
        // таблицы) — увеличено в 10 раз.
        const double Col0Width = 10.0;
        const double Col1Width = 25.0;
        const double Col2Width = 100.0;
        const double Col3Width = 10.0;
        const double Col4Width = 40.0;

        // Высоты строк, ед. чертежа (мм) — исходные 1.8/1.5/1.05 мм, увеличено в 10 раз.
        const double TitleRowHeight  = 18.0;  // строка 0 — заголовок таблицы
        const double HeaderRowHeight = 15.0;  // строка 1 — шапка колонок
        const double DataRowHeight   = 10.5;  // строки данных

        // Высоты текста, ед. чертежа (мм) — исходные 0.5/0.3/0.25 мм, увеличено в 10 раз.
        const double TitleTextHeight  = 5.0;
        const double HeaderTextHeight = 3.0;
        const double DataTextHeight   = 2.5;

        // Имя приложения для XData-метки, которой помечается созданная этой командой таблица —
        // по ней при повторном запуске находим СВОЮ таблицу, чтобы обновить её на месте, а не
        // плодить дубликаты. Сравнение по тексту заголовка ("Ведомость блоков") было бы хрупким —
        // пользователь мог его отредактировать, или в чертеже случайно есть другая таблица с
        // таким же текстом.
        const string AppName = "BLOCKTABLE_GEN";

        // Настройки команды MAFCONVERT (переименование блоков + добавление атрибутов).
        const string MafPrefix = "МАФ_"; // дописывается в НАЧАЛО имени блока, если его там ещё нет
        const double NewAttrTextHeight = 0.1;   // высота текста новых атрибутов, ед. блока (метры — под масштаб МАФ-блоков этого проекта)
        const double NewAttrRowGap     = 0.15;  // вертикальный шаг между двумя новыми атрибутами, ед. блока
        const string NoteDefaultValue  = "УСН РК 8.02-03-2023 8601-"; // значение атрибута "Примечание" по умолчанию — общий префикс кода каталога, пользователь дописывает остаток при вставке
        // ------------------------------------------------------------------

        [CommandMethod("BLOCKTABLE")]
        public void CreateBlockTable()
        {
            Document doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Database db = doc.Database;
            Editor ed = doc.Editor;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                // Блоки МАФ считаем ВСЕГДА из пространства модели — там лежит сама площадка,
                // независимо от того, на каком листе/пространстве в итоге размещается сама
                // таблица (см. targetSpace ниже).
                BlockTableRecord ms = (BlockTableRecord)tr.GetObject(
                    bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                // А вот САМА таблица вставляется в ТЕКУЩЕЕ активное пространство — CurrentSpaceId
                // отдаёт пространство модели, если активна вкладка "Модель", либо paper space
                // конкретного листа, если активна вкладка листа — так пользователь может
                // разместить ведомость прямо на листе, а не только в модели.
                BlockTableRecord targetSpace = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForRead);

                // 1. Сбор и агрегация по имени блока
                var counts = new Dictionary<string, int>();
                var defIds = new Dictionary<string, ObjectId>(); // имя -> ObjectId определения блока
                // имя блока -> список РАЗЛИЧАЮЩИХСЯ непустых значений атрибута со всех его вставок
                // (несколько вставок одного блока могут быть заполнены по-разному — напр. одна
                // вставка "Скамья деревянная", другая "Скамья из досок" — обе идут в таблицу,
                // объединённые через " / "; пустые значения не учитываются вовсе).
                var nameAttrs = new Dictionary<string, List<string>>();
                var noteAttrs = new Dictionary<string, List<string>>();
                // имя блока -> ObjectId ВСЕХ его вставок (не только первой) — нужно ниже, чтобы
                // при заполненном атрибуте хотя бы у одной вставки прописать то же значение и во
                // все остальные (пустые) вставки того же блока: иначе если значение задано только
                // у одной, удаление именно её потеряло бы данные для всей группы.
                var instanceIdsByName = new Dictionary<string, List<ObjectId>>();

                foreach (ObjectId id in ms)
                {
                    // быстрый отсев: интересуют только вставки блоков
                    if (id.ObjectClass.DxfName != "INSERT") continue;

                    BlockReference br = tr.GetObject(id, OpenMode.ForRead) as BlockReference;
                    if (br == null) continue;

                    // Для ДИНАМИЧЕСКИХ блоков br.BlockTableRecord указывает на анонимное
                    // "*U..." определение конкретного состояния параметров этой вставки — нужное
                    // нам "настоящее" (видимое, с человеческим именем) определение отдаётся через
                    // DynamicBlockTableRecord. Для обычных блоков IsDynamicBlock=false, и это
                    // просто совпадает с br.BlockTableRecord. Без этого блок с ЛЮБЫМ параметром
                    // (даже безобидным вроде "Базовая точка") отфильтровывался бы ниже как
                    // анонимный и пропадал из ведомости — этот баг и разбирали.
                    ObjectId effectiveBtrId = br.IsDynamicBlock ? br.DynamicBlockTableRecord : br.BlockTableRecord;
                    BlockTableRecord btr = (BlockTableRecord)tr.GetObject(effectiveBtrId, OpenMode.ForRead);

                    // фильтры
                    if (btr.IsAnonymous || btr.IsLayout) continue;
                    if (SkipXrefs && btr.IsFromExternalReference) continue;

                    string name = btr.Name; // имя НАСТОЯЩЕГО (резолвленного) определения — надёжно и для динамических блоков

                    // фильтр по имени: только содержащие IncludeKeyword, но без ExcludeKeyword
                    if (IncludeKeyword.Length > 0 &&
                        name.IndexOf(IncludeKeyword, StringComparison.CurrentCultureIgnoreCase) < 0)
                        continue;
                    if (ExcludeKeyword.Length > 0 &&
                        name.IndexOf(ExcludeKeyword, StringComparison.CurrentCultureIgnoreCase) >= 0)
                        continue;

                    if (counts.ContainsKey(name))
                    {
                        counts[name]++;
                    }
                    else
                    {
                        counts[name] = 1;
                        defIds[name] = effectiveBtrId; // запоминаем НАСТОЯЩЕЕ определение для картинки (см. комментарий выше про DynamicBlockTableRecord)
                    }

                    if (!instanceIdsByName.TryGetValue(name, out List<ObjectId> instList))
                        instanceIdsByName[name] = instList = new List<ObjectId>();
                    instList.Add(id);

                    // Значения атрибутов — со ВСЕХ вставок этого блока (не только с первой):
                    // разные вставки одного и того же блока вполне могут быть заполнены
                    // по-разному, и все различающиеся непустые значения должны попасть в
                    // таблицу (объединяются через " / " при выводе — см. ниже). Атрибуты просто
                    // текстовые — TextString отдаёт готовое значение как есть. Пустое значение
                    // не учитывается вовсе (не "занимает" единственное место пустышкой).
                    if (NameAttributeTag.Length > 0 || NoteAttributeTag.Length > 0)
                    {
                        foreach (ObjectId attId in br.AttributeCollection)
                        {
                            AttributeReference attRef = tr.GetObject(attId, OpenMode.ForRead) as AttributeReference;
                            if (attRef == null || string.IsNullOrWhiteSpace(attRef.TextString)) continue;
                            string val = attRef.TextString.Trim();

                            if (NameAttributeTag.Length > 0 &&
                                string.Equals(attRef.Tag, NameAttributeTag, StringComparison.OrdinalIgnoreCase))
                            {
                                if (!nameAttrs.TryGetValue(name, out List<string> list))
                                    nameAttrs[name] = list = new List<string>();
                                if (!list.Contains(val)) list.Add(val);
                            }
                            else if (NoteAttributeTag.Length > 0 &&
                                string.Equals(attRef.Tag, NoteAttributeTag, StringComparison.OrdinalIgnoreCase))
                            {
                                if (!noteAttrs.TryGetValue(name, out List<string> list))
                                    noteAttrs[name] = list = new List<string>();
                                if (!list.Contains(val)) list.Add(val);
                            }
                        }
                    }
                }

                if (counts.Count == 0)
                {
                    ed.WriteMessage("\nБлоки в пространстве модели не найдены.");
                    return;
                }

                // 2. Сортировка по имени.
                //    Для сортировки по количеству (убыв.) замени на:
                //    counts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key)
                var items = counts
                    .OrderBy(kv => kv.Key, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();

                // 3. Ищем уже созданную этой командой таблицу — если есть, обновляем её на месте
                //    (без нового запроса точки вставки и без дублирования), иначе создаём новую.
                //    Ищем только в ТЕКУЩЕМ пространстве (targetSpace) — так у модели и у каждого
                //    листа может быть своя, независимо обновляемая таблица.
                ObjectId existingId = FindExistingTableId(tr, targetSpace, AppName);
                bool hasExisting = !existingId.IsNull;
                bool isUpdate;

                if (hasExisting)
                {
                    bool? choice = AskUpdateOrCreate();
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
                    // снимаем XData-метку со старой (иначе при следующем запуске стало бы
                    // неоднозначно, какая из двух таблиц "своя"). Снятие метки — это запись
                    // XData этого приложения БЕЗ данных после маркера (ResultBuffer только с
                    // ExtendedDataRegAppName и без последующих кодов АutoCAD трактует как
                    // удаление XData этого приложения — то самое поведение, которое раньше
                    // мешало метке появиться при СОЗДАНИИ, здесь используем сознательно для
                    // очистки) — сама старая таблица не трогается и не удаляется, просто
                    // перестаёт быть отслеживаемой этой командой.
                    Table oldTb = (Table)tr.GetObject(existingId, OpenMode.ForWrite);
                    oldTb.XData = new ResultBuffer(new TypedValue((int)DxfCode.ExtendedDataRegAppName, AppName));
                }

                Table tb;
                // Ключ — ObjectId определения блока в колонке "Обозначение" (картинка), а НЕ текст
                // колонки "Наименование": та теперь может браться из атрибута блока и не совпадать
                // с именем блока, так что для устойчивой привязки строки к типу блока годится
                // только ObjectId — он не меняется от того, что показывается в тексте ячеек.
                //
                // Текст, УЖЕ стоящий в ячейке таблицы, главнее того, что заново вычислил бы из
                // атрибутов блок — если пользователь вручную поправил "Наименование"/"Примечание"
                // прямо в таблице, повторный запуск BLOCKTABLE не должен затирать эту правку своим
                // вычисленным значением, даже если оно отличается. Поэтому ПЕРЕД перестройкой строк
                // запоминаем текущий текст обеих колонок по каждому блоку, а при заполнении ниже —
                // если сохранённый текст непустой и отличается от вычисленного, оставляем его.
                Dictionary<ObjectId, string> preservedNames = new Dictionary<ObjectId, string>();
                Dictionary<ObjectId, string> preservedNotes = new Dictionary<ObjectId, string>();

                if (isUpdate)
                {
                    tb = (Table)tr.GetObject(existingId, OpenMode.ForWrite);

                    // Переносим сохранённый текст на новые номера строк (сортировка по имени
                    // могла сдвинуть блок на другую позицию), а не теряем его при перестройке.
                    for (int row = 2; row < tb.Rows.Count; row++)
                    {
                        ObjectId rowBlockId = tb.GetBlockTableRecordId(row, 1, 0);
                        if (rowBlockId.IsNull) continue;

                        string rowName = tb.Cells[row, 2].TextString;
                        if (!string.IsNullOrEmpty(rowName))
                            preservedNames[rowBlockId] = rowName;

                        string rowNote = tb.Cells[row, 4].TextString;
                        if (!string.IsNullOrEmpty(rowNote))
                            preservedNotes[rowBlockId] = rowNote;
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

                // Таблица и весь её текст — на слое LayerName (у ячеек Table нет собственного
                // Layer, слой всей сущности покрывает и содержимое).
                EnsureLayer(db, tr, LayerName);
                tb.Layer = LayerName;

                const int nCols = 5;
                int nRows = items.Count + 2;   // заголовок + шапка + строки данных

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

                // Заголовок (строка 0 — объединяется стилем "Standard")
                tb.Cells[0, 0].TextString = TableTitle;

                // Шапка (строка 1).
                // ВНИМАНИЕ: если твой стиль БЕЗ строки-заголовка — перенеси шапку и данные
                // на одну строку вверх (headerRow = 0, dataStart = 1, nRows = items.Count + 1).
                string[] headers = { "Поз.", "Обозначение", "Наименование", "Кол.", "Примечание" };
                for (int c = 0; c < nCols; c++)
                {
                    tb.Cells[1, c].TextString = headers[c];
                    tb.Cells[1, c].Alignment = CellAlignment.MiddleCenter;
                }

                // Записывает resolvedValue в атрибут tag у вставок блока forName. onlyIfEmpty=true —
                // только у тех, где он сейчас ПУСТОЙ (обычная синхронизация: если атрибут заполнен
                // только у ОДНОЙ из N вставок, удаление именно этой вставки теряло бы данные для
                // всей группы — значение копируется во все остальные пустые вставки того же блока,
                // так что данные переживают удаление любой одной вставки). onlyIfEmpty=false —
                // у ВСЕХ вставок безусловно, даже если там уже что-то своё стоит: этим веткой
                // пользуется ручная правка текста прямо в таблице (см. ниже) — она главнее того,
                // что было в блоке, и должна его переписать, а не просто отобразиться поверх.
                void SetAttributeValueOnAllInstances(string forName, string tag, string resolvedValue, bool onlyIfEmpty)
                {
                    if (tag.Length == 0 || string.IsNullOrEmpty(resolvedValue)) return;
                    if (!instanceIdsByName.TryGetValue(forName, out List<ObjectId> instIds)) return;

                    foreach (ObjectId instId in instIds)
                    {
                        BlockReference instBr = tr.GetObject(instId, OpenMode.ForRead) as BlockReference;
                        if (instBr == null) continue;

                        foreach (ObjectId attId in instBr.AttributeCollection)
                        {
                            AttributeReference attRef = tr.GetObject(attId, OpenMode.ForRead) as AttributeReference;
                            if (attRef == null || !string.Equals(attRef.Tag, tag, StringComparison.OrdinalIgnoreCase)) continue;
                            if (onlyIfEmpty && !string.IsNullOrWhiteSpace(attRef.TextString)) continue;

                            attRef.UpgradeOpen();
                            attRef.TextString = resolvedValue;
                            attRef.DowngradeOpen();
                        }
                    }
                }

                // Данные
                for (int i = 0; i < items.Count; i++)
                {
                    int row = i + 2;
                    string name = items[i].Key;
                    int qty = items[i].Value;

                    tb.Cells[row, 0].TextString = (i + 1).ToString();
                    tb.Cells[row, 0].Alignment = CellAlignment.MiddleCenter;

                    // Колонка 1 "Обозначение" — сам блок как содержимое ячейки (картинка,
                    // autoFit = вписать в ячейку)
                    tb.SetBlockTableRecordId(row, 1, defIds[name], true);

                    // Колонка 2 "Наименование" — все различающиеся значения атрибута
                    // NameAttributeTag со вставок этого блока через " / ", если он есть; иначе
                    // имя блока (br.Name) как раньше. Но если в таблице УЖЕ стоит другой,
                    // непустой текст (пользователь поправил его вручную) — текст таблицы главнее:
                    // он остаётся в ячейке И переписывает им атрибут(ы) блока на ВСЕХ вставках
                    // (а не только заполняет пустые), чтобы источник данных (блок) снова совпал с
                    // тем, что показано в таблице — при следующем обновлении расхождения уже не
                    // будет.
                    bool nameFromAttr = nameAttrs.TryGetValue(name, out List<string> nameVals) && nameVals.Count > 0;
                    string computedNameValue = nameFromAttr ? string.Join(" / ", nameVals) : name;
                    string nameCellValue = computedNameValue;
                    if (preservedNames.TryGetValue(defIds[name], out string existingName)
                        && !string.IsNullOrEmpty(existingName) && existingName != computedNameValue)
                    {
                        nameCellValue = existingName;
                        SetAttributeValueOnAllInstances(name, NameAttributeTag, existingName, onlyIfEmpty: false);
                    }
                    else if (nameFromAttr)
                    {
                        // Обычная синхронизация пустых вставок — см. пояснение у
                        // SetAttributeValueOnAllInstances выше.
                        SetAttributeValueOnAllInstances(name, NameAttributeTag, computedNameValue, onlyIfEmpty: true);
                    }
                    tb.Cells[row, 2].TextString = nameCellValue;
                    tb.Cells[row, 2].Alignment = CellAlignment.MiddleLeft;

                    tb.Cells[row, 3].TextString = qty.ToString();
                    tb.Cells[row, 3].Alignment = CellAlignment.MiddleCenter;

                    // Колонка 4 "Примечание" — аналогично, все различающиеся значения атрибута
                    // NoteAttributeTag через " / ", но СНАЧАЛА отбрасываем "недописанные"
                    // значения — те, что являются началом другого, более полного значения того
                    // же блока (типично: значение осталось равно шаблонному дефолту у части
                    // вставок, потому что пользователь его не дозаполнил — см. DropPrefixValues).
                    // Если атрибута нет ни на одной вставке — вычисленное значение пусто. И в том,
                    // и в другом случае текст, УЖЕ стоящий в таблице (вписан вручную), главнее —
                    // переписывает атрибут(ы) блока, тот же приоритет, что и у "Наименование" выше.
                    bool noteFromAttr = noteAttrs.TryGetValue(name, out List<string> noteVals) && noteVals.Count > 0;
                    string computedNoteValue = noteFromAttr ? string.Join(" / ", DropPrefixValues(noteVals)) : "";
                    string noteCellValue = computedNoteValue;
                    if (preservedNotes.TryGetValue(defIds[name], out string existingNote)
                        && !string.IsNullOrEmpty(existingNote) && existingNote != computedNoteValue)
                    {
                        noteCellValue = existingNote;
                        SetAttributeValueOnAllInstances(name, NoteAttributeTag, existingNote, onlyIfEmpty: false);
                    }
                    else if (noteFromAttr)
                    {
                        SetAttributeValueOnAllInstances(name, NoteAttributeTag, computedNoteValue, onlyIfEmpty: true);
                    }
                    tb.Cells[row, 4].TextString = noteCellValue;
                    tb.Cells[row, 4].Alignment = CellAlignment.MiddleCenter;
                }

                // 4. Размеры столбцов, высоты строк, текста и отступы содержимого ячеек
                tb.HorizontalCellMargin = CellMarginH;
                tb.VerticalCellMargin = CellMarginV;

                tb.Columns[0].Width = Col0Width;
                tb.Columns[1].Width = Col1Width;
                tb.Columns[2].Width = Col2Width;
                tb.Columns[3].Width = Col3Width;
                tb.Columns[4].Width = Col4Width;

                tb.Rows[0].Height = TitleRowHeight;
                tb.Rows[1].Height = HeaderRowHeight;
                for (int i = 0; i < items.Count; i++)
                    tb.Rows[i + 2].Height = DataRowHeight;

                tb.Cells[0, 0].TextHeight = TitleTextHeight;
                for (int c = 0; c < nCols; c++)
                    tb.Cells[1, c].TextHeight = HeaderTextHeight;
                for (int i = 0; i < items.Count; i++)
                {
                    int row = i + 2;
                    for (int c = 0; c < nCols; c++)
                        tb.Cells[row, c].TextHeight = DataTextHeight;
                }

                // Текстовый стиль — на ВСЕ ячейки (заголовок, шапка, данные), поэтому применяем
                // после того как таблица дорощена/дана нужным числом строк, но ДО GenerateLayout
                // (он пересчитывает размеры ячеек под содержимое и шрифт).
                ApplyTextStyle(db, tr, tb, TextStyleName, ed);

                tb.GenerateLayout();

                // 5. Добавление таблицы в модель (только при первом создании — при обновлении
                //    таблица уже находится в базе). XData ставим ПОСЛЕ AppendEntity/
                //    AddNewlyCreatedDBObject, а не до — на объекте, ещё не добавленном в базу
                //    чертежа, запись XData может тихо не сохраниться (без исключения, но при
                //    следующем запуске GetXDataForApplication на нём возвращает null — именно
                //    так и терялась метка, из-за чего FindExistingTableId никогда не находил
                //    уже созданную таблицу и КАЖДЫЙ запуск команды создавал новую).
                if (!isUpdate)
                {
                    BlockTableRecord targetSpaceWrite = (BlockTableRecord)tr.GetObject(
                        targetSpace.ObjectId, OpenMode.ForWrite);
                    targetSpaceWrite.AppendEntity(tb);
                    tr.AddNewlyCreatedDBObject(tb, true);

                    // Диагностика показала: XData с ОДНИМ только маркером имени приложения
                    // (код 1001) без данных после него не сохранялась вообще — ни разу за много
                    // прогонов, хотя RegApp регистрировался успешно. Добавляем реальное значение
                    // (код 1000, обычная ASCII-строка) вслед за маркером — минимальный набор,
                    // который AutoCAD гарантированно сохраняет.
                    EnsureRegApp(db, tr, AppName);
                    tb.XData = new ResultBuffer(
                        new TypedValue((int)DxfCode.ExtendedDataRegAppName, AppName),
                        new TypedValue((int)DxfCode.ExtendedDataAsciiString, "table"));
                }

                Layout targetLayout = (Layout)tr.GetObject(targetSpace.LayoutId, OpenMode.ForRead);
                tr.Commit();

                ed.WriteMessage(isUpdate
                    ? "\nОбновлено (лист \"{2}\"): типов блоков — {0}, всего вставок — {1}."
                    : "\nГотово (лист \"{2}\"): типов блоков — {0}, всего вставок — {1}.",
                    items.Count, counts.Values.Sum(), targetLayout.LayoutName);
            }
        }

        /// <summary>Готовит выбранные блоки для BLOCKTABLE: дописывает префикс MafPrefix к имени
        /// ОПРЕДЕЛЕНИЯ блока (если его там ещё нет — попадает под IncludeKeyword="МАФ" фильтр
        /// BLOCKTABLE) и добавляет атрибуты NameAttributeTag/NoteAttributeTag (если их ещё нет).
        /// Работает по ОПРЕДЕЛЕНИЮ блока, не по конкретной вставке — если выделено несколько
        /// вставок одного и того же блока, обрабатывает его один раз, но правки (переименование,
        /// новые атрибуты) применяются ко ВСЕМ вставкам этого блока в чертеже, а не только к
        /// выделенным (иначе разные вставки одного блока разошлись бы по имени/набору
        /// атрибутов — то же определение обязано быть одинаковым везде).</summary>
        [CommandMethod("MAFCONVERT")]
        public void ConvertToMafBlocks()
        {
            Document doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Database db = doc.Database;
            Editor ed = doc.Editor;

            var filter = new SelectionFilter(new[] { new TypedValue((int)DxfCode.Start, "INSERT") });
            PromptSelectionOptions pso = new PromptSelectionOptions { MessageForAdding = "\nВыберите блоки: " };
            PromptSelectionResult psr = ed.GetSelection(pso, filter);
            if (psr.Status != PromptStatus.OK) return;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                // Стиль текста новых атрибутов — тот же TextStyleName, что и у таблицы
                // (ObjectId.Null, если такого стиля в чертеже нет — тогда атрибут просто
                // получит стиль по умолчанию активного в момент создания).
                TextStyleTable tst = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
                ObjectId attrTextStyleId = tst.Has(TextStyleName) ? tst[TextStyleName] : ObjectId.Null;

                var processedBtrIds = new HashSet<ObjectId>();
                int renamedCount = 0, attrAddedCount = 0, skippedCount = 0;
                var warnings = new List<string>();

                foreach (SelectedObject so in psr.Value)
                {
                    if (so == null) continue;
                    BlockReference br = tr.GetObject(so.ObjectId, OpenMode.ForRead) as BlockReference;
                    if (br == null) continue;

                    // Для динамических блоков br.BlockTableRecord указывает на анонимное
                    // "*U..." определение конкретного состояния параметров ЭТОЙ вставки —
                    // переименовывать/дополнять атрибутами нужно НАСТОЯЩЕЕ (видимое, общее для
                    // всех состояний) определение — DynamicBlockTableRecord. Тот же баг разбирали
                    // в BLOCKTABLE: без этого блок с любым параметром (например, "Базовая точка")
                    // либо правился не туда, либо вообще переставал находиться.
                    ObjectId btrId = br.IsDynamicBlock ? br.DynamicBlockTableRecord : br.BlockTableRecord;
                    if (!processedBtrIds.Add(btrId)) continue; // уже обработали в рамках этого запуска

                    BlockTableRecord btr = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForWrite);
                    if (btr.IsAnonymous || btr.IsLayout || btr.IsFromExternalReference)
                    {
                        skippedCount++;
                        continue;
                    }

                    // 1. Переименование определения блока
                    if (!btr.Name.StartsWith(MafPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        string oldName = btr.Name;
                        try
                        {
                            btr.Name = MafPrefix + oldName;
                            renamedCount++;
                        }
                        catch (Autodesk.AutoCAD.Runtime.Exception ex)
                        {
                            warnings.Add($"Не удалось переименовать \"{oldName}\": {ex.Message}");
                        }
                    }

                    // 2. Добавление атрибутов "Наименование"/"Примечание", если их ещё нет —
                    //    и синхронизация ВСЕХ уже существующих вставок этого блока в чертеже
                    //    (не только выделенных), иначе у старых вставок этих атрибутов не будет.
                    bool hasName = HasAttributeDef(tr, btr, NameAttributeTag);
                    bool hasNote = HasAttributeDef(tr, btr, NoteAttributeTag);
                    if (hasName && hasNote) continue;

                    // Атрибуты создаются ВИДИМЫМИ (Visible=true в CreateAttributeDefinition) —
                    // проверено вживую: программное скрытие (Visible=false), в каком бы месте
                    // кода его ни ставить, приводит к тому, что атрибут ведёт себя как удалённый
                    // (BATTMAN его не видит, EATTEDIT — "нет редактируемых атрибутов"), тогда как
                    // атрибут, скрытый ВРУЧНУЮ через редактор блока (BEDIT), работает нормально.
                    // Если нужно скрыть — сделайте это вручную в BEDIT после того, как атрибут
                    // создан этой командой.
                    var newAttDefs = new List<AttributeDefinition>();
                    double y = 0;
                    if (!hasName) { newAttDefs.Add(CreateAttributeDefinition(NameAttributeTag, "", y, attrTextStyleId)); y -= NewAttrRowGap; }
                    if (!hasNote) { newAttDefs.Add(CreateAttributeDefinition(NoteAttributeTag, NoteDefaultValue, y, attrTextStyleId)); }

                    foreach (AttributeDefinition attDef in newAttDefs)
                    {
                        btr.AppendEntity(attDef);
                        tr.AddNewlyCreatedDBObject(attDef, true);
                    }

                    ObjectIdCollection refIds = btr.GetBlockReferenceIds(true, true);
                    foreach (ObjectId refId in refIds)
                    {
                        BlockReference existingBr = tr.GetObject(refId, OpenMode.ForWrite) as BlockReference;
                        if (existingBr == null) continue;

                        foreach (AttributeDefinition attDef in newAttDefs)
                        {
                            AttributeReference attRef = new AttributeReference();
                            attRef.SetAttributeFromBlock(attDef, existingBr.BlockTransform);
                            // SetAttributeFromBlock копирует свойства именно АТРИБУТА (тег,
                            // подсказка, позиция, стиль…), но не гарантированно копирует общие
                            // свойства сущности вроде Transparency — ставим явно тем же значением.
                            attRef.Transparency = attDef.Transparency;
                            existingBr.AttributeCollection.AppendAttribute(attRef);
                            tr.AddNewlyCreatedDBObject(attRef, true);
                        }
                    }

                    attrAddedCount++;
                }

                tr.Commit();

                foreach (string w in warnings) ed.WriteMessage("\n" + w);
                ed.WriteMessage($"\nГотово: переименовано определений — {renamedCount}, " +
                    $"добавлены атрибуты у — {attrAddedCount}, пропущено (анонимные/xref/лист) — {skippedCount}.");
            }
        }

        /// <summary>Есть ли среди сущностей определения блока AttributeDefinition с данным
        /// тегом (регистр не важен).</summary>
        private static bool HasAttributeDef(Transaction tr, BlockTableRecord btr, string tag)
        {
            foreach (ObjectId id in btr)
            {
                AttributeDefinition ad = tr.GetObject(id, OpenMode.ForRead) as AttributeDefinition;
                if (ad != null && string.Equals(ad.Tag, tag, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>Новое определение атрибута — тег = подсказка = tag, значение по умолчанию
        /// defaultValue (пусто — под ручное заполнение), позиция на вертикальном смещении y от
        /// начала координат блока (ед. блока), стиль текста textStyleId (ObjectId.Null — стиль
        /// по умолчанию). LockPositionInBlock=true и явный стиль — чтобы совпадать с обычным
        /// вручную созданным в BEDIT атрибутом (см. переписку — сверялись по свойствам
        /// работающего атрибута пользователя). Расположение стартовое — при необходимости
        /// поправить в редакторе блока (BEDIT), команда только создаёт атрибут, не подбирает
        /// точное место под конкретную геометрию.</summary>
        private static AttributeDefinition CreateAttributeDefinition(string tag, string defaultValue, double y, ObjectId textStyleId)
        {
            var ad = new AttributeDefinition
            {
                Tag = tag,
                Prompt = tag,
                TextString = defaultValue,
                Position = new Point3d(0, y, 0),
                Height = NewAttrTextHeight,
                Justify = AttachmentPoint.BaseLeft,
                Visible = true,
                Constant = false,
                Verifiable = false,
                Preset = false,
                LockPositionInBlock = true,
                // Программное Visible=false ведёт себя как удаление атрибута (см. переписку) —
                // вместо этого делаем текст практически невидимым через 100%-ю прозрачность,
                // которая не трогает флаг "Скрытый" в режимах атрибута и работает как обычное
                // свойство сущности.
                Transparency = new Transparency((byte)0),
            };
            if (!textStyleId.IsNull) ad.TextStyleId = textStyleId;
            return ad;
        }

        /// <summary>Маленький модальный диалог "в чертеже уже есть таблица — обновить или
        /// создать новую?". Возвращает true — обновить, false — создать новую, null — отмена
        /// (пользователь закрыл окно/нажал "Отмена", ничего не делаем).</summary>
        private static bool? AskUpdateOrCreate()
        {
            using (var form = new Form())
            {
                form.Text = "BLOCKTABLE";
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

        /// <summary>Убирает из списка значения, являющиеся НАЧАЛОМ другого значения того же
        /// списка (напр. значения = ["УСН РК 8.02-03-2023 8601-", "УСН РК 8.02-03-2023 8601-123"]
        /// — первое отбрасывается, остаётся только более полное второе). Не трогает значения,
        /// не связанные отношением "префикс — продолжение" — те остаются оба.</summary>
        private static List<string> DropPrefixValues(List<string> values)
        {
            if (values.Count <= 1) return values;
            return values.Where(v => !values.Any(w => w != v && w.StartsWith(v, StringComparison.Ordinal))).ToList();
        }

        /// <summary>Применяет текстовый стиль styleName ко ВСЕМ ячейкам таблицы (заголовок,
        /// шапка, данные) — если такого стиля нет в чертеже, предупреждает и оставляет стиль
        /// по умолчанию, не прерывая выполнение команды.</summary>
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

        /// <summary>Создаёт слой layerName, если его ещё нет в чертеже (цвет по умолчанию,
        /// линия Continuous) — в отличие от текстового стиля, слой безопасно создать самим,
        /// без риска "угадать" чужие настройки.</summary>
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
