using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.Revit.UI;

namespace LiraToRevit.Rebar
{
    /// <summary>Один успешно прочитанный DXF для пакетного анализатора марок (см. SlabMarkCommand).</summary>
    internal class SlabMarkFile
    {
        public string Path;
        public string FileName;
        public DxfImportResult Dxf;
        public Face Face;
        public Dir Dir;
        public string FaceDirSource; // "legend" | "manual" — для диагностики в отчёте
    }

    /// <summary>
    /// Пакетный выбор DXF для анализатора марок — в отличие от RebarZonesDxfPicker.PickFiles,
    /// здесь МНОГО файлов законно претендуют на один и тот же comboKey (по файлу на отметку/
    /// плиту, а не один файл на вкладку редактора), поэтому дедуп "тот же combo → замена" не
    /// подходит — берём все успешно прочитанные файлы как есть, без ограничения на комбинацию
    /// грань+направление. Грань/направление разбираются тем же способом (легенда → иначе диалог
    /// AskFaceDir), логика чтения/пропуска битых файлов — тоже общая с RebarZonesDxfPicker.
    /// </summary>
    internal static class SlabMarkDxfPicker
    {
        public static List<SlabMarkFile> PickFiles(string dialogTitle, List<string> notes)
        {
            var picked = new List<SlabMarkFile>();

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

                Face face; Dir dir; string source;
                if (dxf.Face.HasValue && dxf.Dir.HasValue)
                {
                    // легенда найдена и разобрана — доверяем ей, без лишнего диалога
                    face = dxf.Face.Value;
                    dir = dxf.Dir.Value;
                    source = "legend";
                }
                else
                {
                    // легенды нет или формат непривычный (другая версия ЛИРА) — спрашиваем явно
                    TaskDialog.Show("Легенда не найдена",
                        "В файле \"" + name + "\" не нашлось текста легенды («...по оси X у верхней грани» и т.п.) — "
                        + "укажите грань и направление вручную.");
                    if (!RebarZonesDxfPicker.AskFaceDir(out face, out dir))
                    {
                        notes.Add(name + ": грань/направление не указаны — пропущен.");
                        continue;
                    }
                    source = "manual";
                }

                picked.Add(new SlabMarkFile
                {
                    Path = path,
                    FileName = name,
                    Dxf = dxf,
                    Face = face,
                    Dir = dir,
                    FaceDirSource = source
                });
            }
            return picked;
        }
    }
}
