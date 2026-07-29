using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace DAN_Plugin
{
    public class BreakRange
    {
        public double BottomMm { get; set; }
        public double TopMm { get; set; }
    }

    /// <summary>Строка ввода одного разрыва (источник для ItemsControl).</summary>
    public class BreakRangeRow : INotifyPropertyChanged
    {
        private string _bottom = "";
        private string _top = "";

        public string Bottom
        {
            get => _bottom;
            set { _bottom = value; OnPropertyChanged(nameof(Bottom)); }
        }

        public string Top
        {
            get => _top;
            set { _top = value; OnPropertyChanged(nameof(Top)); }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public partial class SettingsWindow : Window
    {
        // Окно теперь немодальное (см. CreatElevationTags.Execute — открывается через Show()
        // с ручной прокачкой сообщений, а не ShowDialog()), поэтому штатный DialogResult
        // использовать нельзя — WPF бросает исключение, если окно не показано через
        // ShowDialog(). Результат читаем через это свойство после закрытия окна.
        public bool? Result { get; private set; }

        public bool CreateBreak { get; private set; }
        public bool Recreate { get; private set; }
        public bool CreateSections { get; private set; }
        public List<BreakRange> BreakRanges { get; private set; } = new List<BreakRange>();

        private readonly ObservableCollection<BreakRangeRow> _rows =
            new ObservableCollection<BreakRangeRow>();

        public SettingsWindow()
        {
            InitializeComponent();

            // Разрыв по умолчанию
            _rows.Add(new BreakRangeRow { Bottom = "30000", Top = "50000" });
            RangesItems.ItemsSource = _rows;

            // Поля координат разрыва изначально недоступны, если галочка не отмечена
            PnlBreak.IsEnabled = ChkCreateBreak.IsChecked == true;

            Validate();
        }

        private void ChkCreateBreak_Changed(object sender, RoutedEventArgs e)
        {
            if (PnlBreak == null) return;
            PnlBreak.IsEnabled = ChkCreateBreak.IsChecked == true;
            Validate();
        }

        private void Ranges_TextChanged(object sender, TextChangedEventArgs e) => Validate();

        private void BtnAddRange_Click(object sender, RoutedEventArgs e)
        {
            _rows.Add(new BreakRangeRow());
            Validate();
        }

        private void BtnRemoveRange_Click(object sender, RoutedEventArgs e)
        {
            if (_rows.Count <= 1) return; // всегда оставляем минимум одну строку
            if ((sender as FrameworkElement)?.DataContext is BreakRangeRow row)
                _rows.Remove(row);
            Validate();
        }

        private void Validate()
        {
            if (BtnCreate == null) return;

            PnlError.Visibility = Visibility.Collapsed;

            if (ChkCreateBreak.IsChecked != true)
            {
                BtnCreate.IsEnabled = true;
                return;
            }

            var errors = new List<string>();
            var parsed = new List<(double bottom, double top)>();

            int i = 1;
            foreach (var row in _rows)
            {
                bool okB = double.TryParse(row.Bottom, out double b) && b >= 0;
                bool okT = double.TryParse(row.Top, out double t) && t >= 0;

                if (!okB || !okT)
                    errors.Add($"Разрыв {i}: введите корректные положительные числа.");
                else if (b >= t)
                    errors.Add($"Разрыв {i}: нижняя высота должна быть меньше верхней.");
                else
                    parsed.Add((b, t));

                i++;
            }

            // Пересечения среди корректных диапазонов
            var sorted = parsed.OrderBy(p => p.bottom).ToList();
            for (int k = 1; k < sorted.Count; k++)
            {
                if (sorted[k].bottom < sorted[k - 1].top)
                {
                    errors.Add("Разрывы не должны пересекаться.");
                    break;
                }
            }

            if (_rows.Count == 0)
                errors.Add("Добавьте хотя бы один разрыв.");

            if (errors.Any())
            {
                TxtGeneralError.Text = string.Join("\n", errors);
                PnlError.Visibility = Visibility.Visible;
                BtnCreate.IsEnabled = false;
            }
            else
            {
                BtnCreate.IsEnabled = true;
            }
        }

        private void BtnCreate_Click(object sender, RoutedEventArgs e)
        {
            CreateBreak = ChkCreateBreak.IsChecked == true;
            Recreate = ChkRecreate.IsChecked == true;
            CreateSections = ChkCreateSections.IsChecked == true;

            BreakRanges.Clear();
            if (CreateBreak)
            {
                foreach (var row in _rows)
                {
                    if (double.TryParse(row.Bottom, out double b) &&
                        double.TryParse(row.Top, out double t) && b < t)
                    {
                        BreakRanges.Add(new BreakRange { BottomMm = b, TopMm = t });
                    }
                }
            }

            Result = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            Result = false;
            Close();
        }
    }
}