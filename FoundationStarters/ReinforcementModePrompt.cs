using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;

namespace DAN_Plugin
{
    internal enum ReinforcementMode
    {
        Ties,       // обычные хомуты
        Horizontal  // горизонтальные стержни вдоль стены (наружный+внутренний массив)
    }

    internal class ReinforcementChoice
    {
        public ReinforcementMode Mode;
        public RebarBarType TieBarType;        // задан только при Mode == Ties
        public RebarBarType HorizontalBarType; // задан только при Mode == Horizontal
        public double HorizontalStepMm;        // задан только при Mode == Horizontal
        public double BottomOffsetMm;          // от низа фундамента до ЦЕНТРА (оси) низа выпуска
    }

    // Запрос у пользователя: чем армировать тело фундамента вокруг выпусков — обычными
    // хомутами (с выбором диаметра) или горизонтальными стержнями (с выбором диаметра и шага).
    // Без отдельного .xaml, по тому же приёму, что и GroupNumberPrompt.
    internal static class ReinforcementModePrompt
    {
        // Хомуты — гладкая арматура класса А240, диаметр выбирает пользователь.
        private static readonly Regex TieBarTypePattern =
            new Regex(@"^\(арматура\)детали_d=(\d+)_А240$", RegexOptions.IgnoreCase);

        // Горизонтальная арматура — прямые (не гнутые, RebarStyle.Standard) стержни "погонным
        // метром" — отдельный типоразмер от гнутых выпусков (см. StarterBarTypeNameFormat в
        // FoundationStarterCommand, тот без суффикса " п.м" и используется с формой "(форма)11").
        private static readonly Regex HorizontalBarTypePattern =
            new Regex(@"^\(арматура\)выпуски_d=(\d+)_А500 п\.м$", RegexOptions.IgnoreCase);

        private static List<(RebarBarType BarType, int DiaMm)> FindBarTypes(Document doc, Regex pattern) =>
            new FilteredElementCollector(doc)
                .OfClass(typeof(RebarBarType))
                .Cast<RebarBarType>()
                .Select(bt => (BarType: bt, Match: pattern.Match(bt.Name)))
                .Where(x => x.Match.Success)
                .Select(x => (x.BarType, DiaMm: int.Parse(x.Match.Groups[1].Value)))
                .OrderBy(x => x.DiaMm)
                .ToList();

        public static ReinforcementChoice Ask(Document doc, double defaultBottomOffsetMm)
        {
            var tieBarTypes = FindBarTypes(doc, TieBarTypePattern);
            var horizontalBarTypes = FindBarTypes(doc, HorizontalBarTypePattern);

            var window = new Window
            {
                Title = "Формирование выпусков",
                Width = 340,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false
            };

            var panel = new StackPanel { Margin = new Thickness(12) };
            panel.Children.Add(new TextBlock
            {
                Text = "Выберите способ армирования",
                Margin = new Thickness(0, 0, 0, 8),
                TextWrapping = TextWrapping.Wrap
            });

            var rbTies = new RadioButton { Content = "Хомуты", GroupName = "mode", IsChecked = true, Margin = new Thickness(0, 0, 0, 4) };
            var rbHorizontal = new RadioButton { Content = "Горизонтальная арматура + шпильки", GroupName = "mode", Margin = new Thickness(0, 0, 0, 8) };
            panel.Children.Add(rbTies);
            panel.Children.Add(rbHorizontal);

            // Настройки хомутов и горизонтальной арматуры — в отдельных панелях, чтобы при
            // переключении режима показывалась только настройка выбранного способа, а не обе
            // сразу (одна из них серая, но видимая).
            var tiePanel = new StackPanel();
            tiePanel.Children.Add(new TextBlock { Text = "Диаметр хомута:", Margin = new Thickness(0, 4, 0, 2) });
            var tieDiamCombo = new ComboBox();
            foreach (var bt in tieBarTypes)
                tieDiamCombo.Items.Add(new ComboBoxItem { Content = $"d{bt.DiaMm}", Tag = bt.BarType });
            if (tieDiamCombo.Items.Count > 0) tieDiamCombo.SelectedIndex = 0;
            tiePanel.Children.Add(tieDiamCombo);
            panel.Children.Add(tiePanel);

            var horizontalPanel = new StackPanel { Visibility = System.Windows.Visibility.Collapsed };
            horizontalPanel.Children.Add(new TextBlock { Text = "Диаметр стержня:", Margin = new Thickness(0, 8, 0, 2) });
            var diamCombo = new ComboBox();
            foreach (var bt in horizontalBarTypes)
                diamCombo.Items.Add(new ComboBoxItem { Content = $"d{bt.DiaMm}", Tag = bt.BarType });
            if (diamCombo.Items.Count > 0) diamCombo.SelectedIndex = 0;
            horizontalPanel.Children.Add(diamCombo);

            horizontalPanel.Children.Add(new TextBlock { Text = "Шаг, мм:", Margin = new Thickness(0, 8, 0, 2) });
            var stepBox = new TextBox { Text = "400" };
            horizontalPanel.Children.Add(stepBox);
            panel.Children.Add(horizontalPanel);

            // Отступ низа выпуска от низа фундамента — общая настройка для обоих режимов
            // (задаёт нижнюю границу и для выпусков, и для хомутов/горизонтальной арматуры/
            // шпилек), поэтому вне переключаемых панелей tiePanel/horizontalPanel.
            panel.Children.Add(new TextBlock
            {
                Text = "Расстояние от низа фундамента до центра выпуска, мм:",
                Margin = new Thickness(0, 10, 0, 2),
                TextWrapping = TextWrapping.Wrap
            });
            var offsetBox = new TextBox { Text = defaultBottomOffsetMm.ToString("0.#") };
            panel.Children.Add(offsetBox);

            if (tieBarTypes.Count == 0)
            {
                rbTies.IsEnabled = false;
                tieDiamCombo.IsEnabled = false;
                panel.Children.Add(new TextBlock
                {
                    Text = "⚠ Не найдено ни одного типоразмера \"(арматура)детали_d=…_А240\" — хомуты недоступны.",
                    Foreground = System.Windows.Media.Brushes.DarkRed,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 4, 0, 0)
                });
                if (horizontalBarTypes.Count > 0)
                {
                    rbTies.IsChecked = false;
                    rbHorizontal.IsChecked = true;
                }
            }

            if (horizontalBarTypes.Count == 0)
            {
                rbHorizontal.IsEnabled = false;
                panel.Children.Add(new TextBlock
                {
                    Text = "⚠ Не найдено ни одного типоразмера \"(арматура)выпуски_d=…_А500 п.м\" — горизонтальная арматура недоступна.",
                    Foreground = System.Windows.Media.Brushes.DarkRed,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 4, 0, 0)
                });
            }

            rbHorizontal.Checked += (s, e) =>
            {
                tiePanel.Visibility = System.Windows.Visibility.Collapsed;
                horizontalPanel.Visibility = System.Windows.Visibility.Visible;
            };
            rbTies.Checked += (s, e) =>
            {
                tiePanel.Visibility = System.Windows.Visibility.Visible;
                horizontalPanel.Visibility = System.Windows.Visibility.Collapsed;
            };
            // При старте видимость панели хомута нужно выставить явно — событие Checked не
            // срабатывает для того RadioButton, что уже был IsChecked=true до подписки.
            tiePanel.Visibility = rbTies.IsChecked == true ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            horizontalPanel.Visibility = rbTies.IsChecked == true ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

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
                if (rbTies.IsChecked == true)
                {
                    if (tieDiamCombo.SelectedItem == null)
                    {
                        MessageBox.Show(window, "Выберите диаметр хомута.", "Не выбран диаметр",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                }
                else
                {
                    if (diamCombo.SelectedItem == null)
                    {
                        MessageBox.Show(window, "Выберите диаметр.", "Не выбран диаметр",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                    if (!double.TryParse(stepBox.Text.Trim(), out double stepMm) || stepMm <= 0)
                    {
                        MessageBox.Show(window, "Введите положительное число (шаг, мм).", "Некорректный шаг",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                }
                if (!double.TryParse(offsetBox.Text.Trim(), out double offsetMm) || offsetMm < 0)
                {
                    MessageBox.Show(window, "Введите неотрицательное число (расстояние, мм).", "Некорректное расстояние",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                window.DialogResult = true;
            };
            cancelButton.Click += (s, e) => window.DialogResult = false;

            bool? result = window.ShowDialog();
            if (result != true) return null;

            double bottomOffsetMm = double.Parse(offsetBox.Text.Trim());

            if (rbTies.IsChecked == true)
            {
                var tieBarType = (RebarBarType)((ComboBoxItem)tieDiamCombo.SelectedItem).Tag;
                return new ReinforcementChoice { Mode = ReinforcementMode.Ties, TieBarType = tieBarType, BottomOffsetMm = bottomOffsetMm };
            }

            var barType = (RebarBarType)((ComboBoxItem)diamCombo.SelectedItem).Tag;
            double horizStepMm = double.Parse(stepBox.Text.Trim());
            return new ReinforcementChoice
            {
                Mode = ReinforcementMode.Horizontal,
                HorizontalBarType = barType,
                HorizontalStepMm = horizStepMm,
                BottomOffsetMm = bottomOffsetMm
            };
        }
    }
}
