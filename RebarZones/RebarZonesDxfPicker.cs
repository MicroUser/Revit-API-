using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.UI;

namespace LiraToRevit.Rebar
{
    /// <summary>
    /// Диалог выбора DXF (можно несколько за раз) + чтение + определение грань/направление
    /// (легенда либо явный вопрос) — общая логика для кнопки "📁 DXF" внутри уже открытого окна
    /// (см. RebarZonesWindow.OnWebMessage "loaddxf:"). Диалог/чтение файлов Revit API не
    /// касаются — чистый Win32/IO, вызывается прямо из UI-потока окна, без моста.
    /// </summary>
    internal static class RebarZonesDxfPicker
    {
        /// <summary>
        /// alreadyLoadedCombos — ключи вкладок ("top-x" и т.п.), уже занятых в текущем сеансе
        /// (для текста уведомления "заменил"). notes пополняется по ходу (пропуски/замены/ошибки).
        /// </summary>
        public static List<(DxfImportResult Dxf, Face Face, Dir Dir, string Path)> PickFiles(
            string dialogTitle, IEnumerable<string> alreadyLoadedCombos, List<string> notes)
        {
            var picked = new List<(DxfImportResult Dxf, Face Face, Dir Dir, string Path)>();
            var occupied = new HashSet<string>(alreadyLoadedCombos);

            var ofd = new Microsoft.Win32.OpenFileDialog
            {
                Title = dialogTitle,
                Filter = "DXF файлы (*.dxf)|*.dxf|Все файлы (*.*)|*.*",
                Multiselect = true
            };
            if (ofd.ShowDialog() != true) return picked;

            foreach (string path in ofd.FileNames)
            {
                string name = Path.GetFileName(path);
                DxfImportResult dxf;
                try { dxf = DxfArmoringReader.Read(path); }
                catch (IOException)
                {
                    notes.Add(name + ": файл сейчас открыт другой программой — пропущен.");
                    continue;
                }
                catch (Exception ex)
                {
                    notes.Add(name + ": ошибка импорта — " + ex.Message);
                    continue;
                }
                if (dxf.Cells.Count == 0)
                {
                    notes.Add(name + ": в файле не найдено ячеек (3DFACE на слое layer_elements) — пропущен.");
                    continue;
                }

                Face face; Dir dir;
                if (dxf.Face.HasValue && dxf.Dir.HasValue)
                {
                    // легенда найдена и разобрана — доверяем ей, без лишнего диалога
                    face = dxf.Face.Value;
                    dir = dxf.Dir.Value;
                }
                else
                {
                    // легенды нет или формат непривычный (другая версия ЛИРА) — спрашиваем явно
                    TaskDialog.Show("Легенда не найдена",
                        "В файле \"" + name + "\" не нашлось текста легенды («...по оси X у верхней грани» и т.п.) — "
                        + "укажите грань и направление вручную.");
                    if (!AskFaceDir(out face, out dir))
                    {
                        notes.Add(name + ": грань/направление не указаны — пропущен.");
                        continue;
                    }
                }

                string comboKey = RebarZonesDataStore.ComboKey(face, dir);
                // Если вкладка уже занята (этим же диалогом или ранее) — новый файл её ЗАМЕНЯЕТ.
                int existingIdx = picked.FindIndex(d => d.Face == face && d.Dir == dir);
                if (existingIdx >= 0)
                {
                    notes.Add(name + ": заменил файл, выбранный этим же диалогом для вкладки \"" + RebarZonesDataStore.ComboLabel(comboKey) + "\".");
                    picked.RemoveAt(existingIdx);
                }
                else if (occupied.Contains(comboKey))
                {
                    notes.Add(name + ": заменил ранее загруженный файл для вкладки \"" + RebarZonesDataStore.ComboLabel(comboKey) + "\".");
                }
                picked.Add((dxf, face, dir, path));
            }
            return picked;
        }

        internal static bool AskFaceDir(out Face face, out Dir dir)
        {
            var td = new TaskDialog("Направление и грань")
            {
                MainInstruction = "Какому DXF-файлу соответствует эта мозаика?",
                MainContent = "Экспорт ЛИРА содержит As только для ОДНОЙ комбинации грани плиты и направления стержней.",
                CommonButtons = TaskDialogCommonButtons.Cancel
            };
            td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Верх · X");
            td.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Верх · Y");
            td.AddCommandLink(TaskDialogCommandLinkId.CommandLink3, "Низ · X");
            td.AddCommandLink(TaskDialogCommandLinkId.CommandLink4, "Низ · Y");

            switch (td.Show())
            {
                case TaskDialogResult.CommandLink1: face = Face.Top; dir = Dir.X; return true;
                case TaskDialogResult.CommandLink2: face = Face.Top; dir = Dir.Y; return true;
                case TaskDialogResult.CommandLink3: face = Face.Bottom; dir = Dir.X; return true;
                case TaskDialogResult.CommandLink4: face = Face.Bottom; dir = Dir.Y; return true;
                default: face = Face.Top; dir = Dir.X; return false;
            }
        }
    }
}
