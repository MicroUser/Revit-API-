using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.DB;

namespace DAN_Plugin
{
    // Простой запрос номера группы у пользователя (число после "_Выпуски_" в имени группы) —
    // без отдельного .xaml, окно собирается прямо в коде, т.к. это единственное поле ввода.
    internal static class GroupNumberPrompt
    {
        // Ищем уже существующие группы "{МаркаФундамента}_Выпуски_{число}" в документе и
        // предлагаем следующий свободный номер (макс.+1), а не всегда "1" — чтобы не пришлось
        // руками подбирать номер, если группы 1, 2, 3... уже созданы в прошлых запусках.
        private static int SuggestNextNumber(Document doc, string foundationMark)
        {
            if (doc == null || string.IsNullOrEmpty(foundationMark))
                return 1;

            string prefix = $"{foundationMark}_Выпуски_";
            var pattern = new Regex("^" + Regex.Escape(prefix) + @"(\d+)$");

            int maxExisting = new FilteredElementCollector(doc)
                .OfClass(typeof(GroupType))
                .Cast<GroupType>()
                .Select(gt => pattern.Match(gt.Name ?? string.Empty))
                .Where(m => m.Success)
                .Select(m => int.Parse(m.Groups[1].Value))
                .DefaultIfEmpty(0)
                .Max();

            return maxExisting + 1;
        }

        public static int? Ask(Document doc, string foundationMark)
        {
            int suggested = SuggestNextNumber(doc, foundationMark);

            var window = new Window
            {
                Title = "Номер группы выпусков",
                Width = 300,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false
            };

            var panel = new StackPanel { Margin = new Thickness(12) };
            panel.Children.Add(new TextBlock
            {
                Text = "Введите номер группы (число после \"_Выпуски_\") — предложен следующий свободный:",
                Margin = new Thickness(0, 0, 0, 8),
                TextWrapping = TextWrapping.Wrap
            });

            var textBox = new TextBox { Text = suggested.ToString() };
            panel.Children.Add(textBox);

            var buttonsPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };
            var okButton = new Button { Content = "OK", Width = 70, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
            var cancelButton = new Button { Content = "Отмена", Width = 70, IsCancel = true };
            buttonsPanel.Children.Add(okButton);
            buttonsPanel.Children.Add(cancelButton);
            panel.Children.Add(buttonsPanel);

            window.Content = panel;

            okButton.Click += (s, e) =>
            {
                if (int.TryParse(textBox.Text.Trim(), out _))
                    window.DialogResult = true;
                else
                    MessageBox.Show(window, "Введите целое число.", "Некорректный номер",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
            };
            cancelButton.Click += (s, e) => window.DialogResult = false;

            window.Loaded += (s, e) => { textBox.Focus(); textBox.SelectAll(); };

            bool? result = window.ShowDialog();
            if (result != true) return null;

            return int.Parse(textBox.Text.Trim());
        }
    }
}
