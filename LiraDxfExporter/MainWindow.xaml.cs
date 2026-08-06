using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;

namespace LiraDxfExporter
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            TxtFolder.Text = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "LiraDxfExport");
        }

        private void BtnBrowse_Click(object sender, RoutedEventArgs e)
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = "Папка назначения для экспортированных DXF",
                SelectedPath = Directory.Exists(TxtFolder.Text) ? TxtFolder.Text : "",
            };
            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                TxtFolder.Text = dialog.SelectedPath;
        }

        private void AppendLog(string line)
        {
            Dispatcher.Invoke(() =>
            {
                TxtLog.Text += line + "\n";
                TxtLog.ScrollToEnd();
            });
        }

        private async void BtnExportAll_Click(object sender, RoutedEventArgs e)
        {
            string destFolder = TxtFolder.Text?.Trim();
            if (string.IsNullOrEmpty(destFolder))
            {
                System.Windows.MessageBox.Show(this, "Укажите папку назначения.", "LiraDxfExporter",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            BtnExportAll.IsEnabled = false;
            BtnBrowse.IsEnabled = false;
            TxtLog.Text = "";
            try
            {
                AppendLog("Ищу окно ЛИРА-САПР...");
                var root = LiraAutomation.FindLiraMainWindow();
                AppendLog($"Найдено окно: \"{root.Current.Name}\"");

                bool foreground = LiraAutomation.BringToForeground(root);
                AppendLog($"Перевод на передний план: {(foreground ? "успешно" : "НЕ УДАЛОСЬ")}");

                await Task.Run(() => LiraAutomation.ExportAllCombos(root, destFolder, AppendLog));

                AppendLog("");
                AppendLog($"Готово. Все 4 файла сохранены в: {destFolder}");
            }
            catch (Exception ex)
            {
                AppendLog("");
                AppendLog("Ошибка: " + ex.Message);
            }
            finally
            {
                BtnExportAll.IsEnabled = true;
                BtnBrowse.IsEnabled = true;
            }
        }
    }
}
