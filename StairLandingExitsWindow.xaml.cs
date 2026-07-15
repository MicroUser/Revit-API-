using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;

namespace DAN_Plugin
{
    public partial class StairLandingExitsWindow : Window
    {
        public RebarBarType SelectedBarType { get; private set; }
        public double ALength { get; private set; }
        public double BLength { get; private set; }

        private readonly List<DiamItem> _items;

        public StairLandingExitsWindow(IEnumerable<RebarBarType> detailBarTypes)
        {
            InitializeComponent();

            _items = detailBarTypes
                .Select(bt => new DiamItem(bt))
                .OrderBy(d => d.DiamMm)
                .ToList();

            cbDiameter.ItemsSource   = _items;
            cbDiameter.DisplayMemberPath = "Label";
            if (_items.Any()) cbDiameter.SelectedIndex = 0;
        }

        private void OK_Click(object sender, RoutedEventArgs e)
        {
            if (cbDiameter.SelectedItem == null)
            {
                MessageBox.Show("Выберите диаметр.", "Ошибка",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!TryParseMm(tbA.Text, out double aMm))
            {
                MessageBox.Show("Некорректное значение BI_A.", "Ошибка",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                tbA.Focus();
                return;
            }

            if (!TryParseMm(tbB.Text, out double bMm))
            {
                MessageBox.Show("Некорректное значение BI_B.", "Ошибка",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                tbB.Focus();
                return;
            }

            SelectedBarType = ((DiamItem)cbDiameter.SelectedItem).BarType;
            ALength = UnitUtils.ConvertToInternalUnits(aMm, UnitTypeId.Millimeters);
            BLength = UnitUtils.ConvertToInternalUnits(bMm, UnitTypeId.Millimeters);

            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        static bool TryParseMm(string text, out double mm)
        {
            mm = 0;
            if (!double.TryParse(text.Replace(',', '.'), NumberStyles.Any,
                CultureInfo.InvariantCulture, out mm)) return false;
            return mm > 0;
        }

        private sealed class DiamItem
        {
            public RebarBarType BarType { get; }
            public double DiamMm { get; }
            public string Label { get; }

            public DiamItem(RebarBarType bt)
            {
                BarType = bt;
                DiamMm  = UnitUtils.ConvertFromInternalUnits(
                    bt.get_Parameter(BuiltInParameter.REBAR_BAR_DIAMETER)?.AsDouble() ?? 0,
                    UnitTypeId.Millimeters);
                Label = $"{DiamMm:F0}";
            }
        }
    }
}
