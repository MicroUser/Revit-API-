using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DAN_Plugin
{
    public class BreakRange
    {
        public double BottomMm { get; set; }
        public double TopMm { get; set; }
    }

    public partial class SettingsWindow : Window
    {
        public bool CreateBreak { get; private set; }
        public bool Recreate { get; private set; }
        public List<BreakRange> BreakRanges { get; private set; } = new List<BreakRange>();

        private readonly List<(TextBox bottom, TextBox top)> _rows = new List<(TextBox, TextBox)>();

        public SettingsWindow()
        {
            InitializeComponent();
            AddBreakRow(30000, 50000);
        }

        private void ChkCreateBreak_Changed(object sender, RoutedEventArgs e)
        {
            if (PnlBreaks == null) return;
            PnlBreaks.IsEnabled = ChkCreateBreak.IsChecked == true;
            Validate();
        }

        private void BtnAddBreak_Click(object sender, RoutedEventArgs e)
        {
            AddBreakRow(0, 0);
            Validate();
        }

        private void AddBreakRow(double bottomDefault, double topDefault)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });

            var txtBottom = CreateTextBox(bottomDefault > 0 ? bottomDefault.ToString("0") : "");
            var txtTop = CreateTextBox(topDefault > 0 ? topDefault.ToString("0") : "");

            Grid.SetColumn(txtBottom, 0);
            Grid.SetColumn(txtTop, 2);

            // Кнопка удалить
            var btnRemove = new Button
            {
                Content = "✕",
                Width = 28,
                Height = 28,
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(107, 114, 128)),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center
            };

            var row = (txtBottom, txtTop);
            btnRemove.Click += (s, e) =>
            {
                if (_rows.Count <= 1) return; // минимум одна строка
                BreakRowsPanel.Children.Remove(grid);
                _rows.Remove(row);
                Validate();
            };

            Grid.SetColumn(btnRemove, 3);

            grid.Children.Add(txtBottom);
            grid.Children.Add(txtTop);
            grid.Children.Add(btnRemove);

            _rows.Add(row);
            BreakRowsPanel.Children.Add(grid);

            txtBottom.TextChanged += (s, e) => Validate();
            txtTop.TextChanged += (s, e) => Validate();
        }

        private TextBox CreateTextBox(string text)
        {
            return new TextBox
            {
                Text = text,
                Height = 34,
                Padding = new Thickness(8, 0, 8, 0),
                FontSize = 12,
                VerticalContentAlignment = VerticalAlignment.Center,
                Background = Brushes.White,
                Foreground = new SolidColorBrush(Color.FromRgb(31, 41, 55)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(209, 213, 219)),
                BorderThickness = new Thickness(1)
            };
        }

        private void Validate()
        {
            if (BtnCreate == null || PnlError == null) return;

            if (ChkCreateBreak.IsChecked != true)
            {
                PnlError.Visibility = Visibility.Collapsed;
                BtnCreate.IsEnabled = true;
                return;
            }

            var ranges = new List<(double bottom, double top, int index)>();

            for (int i = 0; i < _rows.Count; i++)
            {
                var (txtBottom, txtTop) = _rows[i];

                if (!double.TryParse(txtBottom.Text, out double bottom) || bottom < 0)
                {
                    ShowError($"Разрыв {i + 1}: некорректная нижняя высота.");
                    BtnCreate.IsEnabled = false;
                    return;
                }
                if (!double.TryParse(txtTop.Text, out double top) || top < 0)
                {
                    ShowError($"Разрыв {i + 1}: некорректная верхняя высота.");
                    BtnCreate.IsEnabled = false;
                    return;
                }
                if (bottom >= top)
                {
                    ShowError($"Разрыв {i + 1}: нижняя высота должна быть меньше верхней.");
                    BtnCreate.IsEnabled = false;
                    return;
                }
                ranges.Add((bottom, top, i + 1));
            }

            // Проверяем что диапазоны не пересекаются
            var sorted = ranges.OrderBy(r => r.bottom).ToList();
            for (int i = 0; i < sorted.Count - 1; i++)
            {
                if (sorted[i].top > sorted[i + 1].bottom)
                {
                    ShowError($"Разрывы {sorted[i].index} и {sorted[i + 1].index} пересекаются.");
                    BtnCreate.IsEnabled = false;
                    return;
                }
            }

            PnlError.Visibility = Visibility.Collapsed;
            BtnCreate.IsEnabled = true;
        }

        private void ShowError(string text)
        {
            TxtError.Text = text;
            PnlError.Visibility = Visibility.Visible;
        }

        private void BtnCreate_Click(object sender, RoutedEventArgs e)
        {
            CreateBreak = ChkCreateBreak.IsChecked == true;
            Recreate = ChkRecreate.IsChecked == true;
            BreakRanges = _rows
                .Select(r => new BreakRange
                {
                    BottomMm = double.Parse(r.bottom.Text),
                    TopMm = double.Parse(r.top.Text)
                })
                .OrderBy(r => r.BottomMm)
                .ToList();

            DialogResult = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}