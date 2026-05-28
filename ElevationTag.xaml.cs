using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

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

        public SettingsWindow()
        {
            InitializeComponent();
            Validate();
        }

        private void ChkCreateBreak_Changed(object sender, RoutedEventArgs e)
        {
            if (PnlBreak == null) return;
            PnlBreak.IsEnabled = ChkCreateBreak.IsChecked == true;
            Validate();
        }

        private void Heights_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (TxtBottomHeight == null || TxtTopHeight == null) return;
            Validate();
        }

        private void Validate()
        {
            if (TxtBottomHeight == null || TxtTopHeight == null || BtnCreate == null) return;

            TxtBottomError.Visibility = Visibility.Collapsed;
            TxtTopError.Visibility = Visibility.Collapsed;
            PnlError.Visibility = Visibility.Collapsed;

            if (ChkCreateBreak.IsChecked != true)
            {
                BtnCreate.IsEnabled = true;
                return;
            }

            bool valid = true;

            if (!double.TryParse(TxtBottomHeight.Text, out double bottom) || bottom < 0)
            {
                TxtBottomError.Text = "Введите корректное положительное число";
                TxtBottomError.Visibility = Visibility.Visible;
                valid = false;
            }

            if (!double.TryParse(TxtTopHeight.Text, out double top) || top < 0)
            {
                TxtTopError.Text = "Введите корректное положительное число";
                TxtTopError.Visibility = Visibility.Visible;
                valid = false;
            }

            if (valid && bottom >= top)
            {
                TxtGeneralError.Text = "Нижняя высота должна быть меньше верхней";
                PnlError.Visibility = Visibility.Visible;
                valid = false;
            }

            BtnCreate.IsEnabled = valid;
        }

        private void BtnCreate_Click(object sender, RoutedEventArgs e)
        {
            CreateBreak = ChkCreateBreak.IsChecked == true;
            Recreate = ChkRecreate.IsChecked == true;

            if (CreateBreak)
                BreakRanges.Add(new BreakRange
                {
                    BottomMm = double.Parse(TxtBottomHeight.Text),
                    TopMm = double.Parse(TxtTopHeight.Text)
                });

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