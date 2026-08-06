using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using Microsoft.Win32;

namespace DxfCleaner
{
    public partial class MainWindow : Window
    {
        private List<string> _selectedPaths = new List<string>();

        public MainWindow()
        {
            InitializeComponent();
        }

        private void BtnBrowse_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Выбор DXF",
                Filter = "DXF файлы (*.dxf)|*.dxf|Все файлы (*.*)|*.*",
                Multiselect = true
            };
            if (dlg.ShowDialog() == true) SetSelected(dlg.FileNames);
        }

        private void Window_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            var dxfs = files.Where(f => f.EndsWith(".dxf", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (dxfs.Length > 0) SetSelected(dxfs);
        }

        private void SetSelected(IEnumerable<string> paths)
        {
            _selectedPaths = paths.ToList();
            TxtPath.Text = _selectedPaths.Count == 1
                ? _selectedPaths[0]
                : $"Выбрано файлов: {_selectedPaths.Count}";
            BtnClean.IsEnabled = _selectedPaths.Count > 0;
            TxtLog.Text = "";
        }

        private void BtnClean_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedPaths.Count == 0) return;
            var sb = new StringBuilder();
            foreach (var path in _selectedPaths)
            {
                sb.AppendLine("=== " + Path.GetFileName(path) + " ===");
                try
                {
                    var summary = DxfCleanerCore.Clean(path);
                    sb.AppendLine(FormatSummary(summary));
                }
                catch (Exception ex)
                {
                    sb.AppendLine("Ошибка: " + ex.Message);
                }
                sb.AppendLine();
            }
            TxtLog.Text = sb.ToString();
        }

        private static string FormatSummary(CleanSummary s)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Граней КЭ (layer_elements): {s.CellsFound}");
            sb.AppendLine($"Сопоставлено с подписью As и центрировано: {s.CellsMatched}");
            sb.AppendLine($"Без подписи рядом (нужна ручная проверка): {s.CellsUnmatched}");
            sb.AppendLine($"Подписи As без своей ячейки (оставлены как есть): {s.OrphanValueTexts}");
            if (s.DroppedByLayer.Count == 0)
            {
                sb.AppendLine("Лишних слоёв не найдено.");
            }
            else
            {
                sb.AppendLine("Удалено элементов по слоям:");
                foreach (var kv in s.DroppedByLayer)
                    sb.AppendLine($"  {(string.IsNullOrEmpty(kv.Key) ? "(без слоя)" : kv.Key)}: {kv.Value}");
            }
            return sb.ToString();
        }
    }
}
