using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Uniwill_Control_Utility.Models;
using Uniwill_Control_Utility.Services;
using NumberBox = Wpf.Ui.Controls.NumberBox;

namespace Uniwill_Control_Utility.Views.Windows
{
    /// <summary>
    /// Per-core Curve Optimizer editor: one offset per physical core, grouped by CCD.
    /// The window owns nothing the caller does not store - it returns the map and the
    /// caller decides when to write it. Show it with <see cref="Window.ShowDialog"/>;
    /// <see cref="Result"/> stays null when the user cancels or closes the window.
    /// </summary>
    public partial class PerCoreCurveOptimizerWindow : Wpf.Ui.Controls.FluentWindow
    {
        private const double BoxWidth = 96;

        /// <summary>Core boxes in tab order, each paired with the topology core index it writes.</summary>
        private readonly List<(NumberBox Box, int CoreIndex)> _rows = new();

        private readonly int _coreCount;

        /// <summary>One offset per topology core, or null when the user cancelled.</summary>
        public IReadOnlyList<int>? Result { get; private set; }

        public PerCoreCurveOptimizerWindow(CpuTopology topology, IReadOnlyList<int>? currentValues)
        {
            ArgumentNullException.ThrowIfNull(topology);

            InitializeComponent();

            _coreCount = topology.CoreCount;
            BuildGroups(topology, currentValues);
            SortRowsInTabOrder();
            Validate();

            Loaded += (_, _) =>
            {
                if (_rows.Count > 0)
                    _rows[0].Box.Focus();
            };
        }

        /// <summary>
        /// Builds one group per CCD, two groups per row (the shipped parts have two, so the
        /// whole map fits without scrolling at 1080p; more CCDs just add another row band).
        /// Children are added in row-major order so Tab walks left to right, then down.
        /// </summary>
        private void BuildGroups(CpuTopology topology, IReadOnlyList<int>? currentValues)
        {
            var cells = new List<(FrameworkElement Element, int Row, int Column)>();
            var dividers = new List<(Border Element, int Row, int RowSpan)>();

            if (topology.Ccds.Count == 1)
            {
                ColGap.Width = new GridLength(0);
                ColLabel1.Width = new GridLength(0);
                ColBox1.Width = new GridLength(0);
            }

            for (int start = 0; start < topology.Ccds.Count; start += 2)
            {
                var band = topology.Ccds.Skip(start).Take(2).ToList();
                if (start > 0)
                    CoreGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(16) });

                int headerRow = CoreGrid.RowDefinitions.Count;
                CoreGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                int coreRows = band.Max(ccd => ccd.Cores.Count);
                for (int i = 0; i < coreRows; i++)
                    CoreGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                for (int slot = 0; slot < band.Count; slot++)
                {
                    Ccd ccd = band[slot];
                    int labelColumn = slot * 3;

                    var header = new TextBlock
                    {
                        Text = string.Format(
                            CultureInfo.CurrentCulture,
                            LocalizationService.Get(ccd.IsX3D ? "CCD {0} · 3D V-Cache" : "CCD {0}"),
                            ccd.Index),
                        FontSize = 13,
                        FontWeight = FontWeights.SemiBold,
                        Margin = new Thickness(0, 0, 0, 8),
                    };
                    cells.Add((header, headerRow, labelColumn));

                    NumberBox setAllBox = NewNumberBox(SeedForCcd(ccd, currentValues));
                    var setAllButton = new Button
                    {
                        Content = LocalizationService.Get("Set all"),
                        Padding = new Thickness(12, 4, 12, 4),
                        Margin = new Thickness(8, 0, 0, 0),
                        ToolTip = LocalizationService.Get("Applies this offset to every core in this CCD"),
                    };
                    var setAllPanel = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Margin = new Thickness(0, 0, 0, 8),
                    };
                    setAllPanel.Children.Add(setAllBox);
                    setAllPanel.Children.Add(setAllButton);
                    cells.Add((setAllPanel, headerRow, labelColumn + 1));

                    var coreBoxes = new List<NumberBox>(ccd.Cores.Count);
                    int row = headerRow + 1;
                    foreach (CpuCore core in ccd.Cores)
                    {
                        var label = new TextBlock
                        {
                            Text = string.Format(
                                CultureInfo.CurrentCulture,
                                LocalizationService.Get("Core {0}"),
                                core.IndexInCcd),
                            FontSize = 13,
                            VerticalAlignment = VerticalAlignment.Center,
                            Margin = new Thickness(0, 4, 0, 4),
                        };
                        cells.Add((label, row, labelColumn));

                        NumberBox box = NewNumberBox(SeedForCore(core, currentValues));
                        box.HorizontalAlignment = HorizontalAlignment.Right;
                        cells.Add((box, row, labelColumn + 1));

                        coreBoxes.Add(box);
                        _rows.Add((box, core.Index));
                        row++;
                    }

                    setAllButton.Click += (_, _) =>
                    {
                        foreach (NumberBox box in coreBoxes)
                            box.Value = setAllBox.Value ?? 0;
                        Validate();
                    };
                }

                if (band.Count == 2)
                {
                    dividers.Add((
                        new Border
                        {
                            Width = 1,
                            Background = (Brush)FindResource("DividerStrokeColorDefaultBrush"),
                        },
                        headerRow,
                        coreRows + 1));
                }
            }

            foreach ((FrameworkElement element, int row, int column) in cells.OrderBy(c => c.Row).ThenBy(c => c.Column))
            {
                Grid.SetRow(element, row);
                Grid.SetColumn(element, column);
                CoreGrid.Children.Add(element);
            }

            foreach ((Border element, int row, int rowSpan) in dividers)
            {
                Grid.SetRow(element, row);
                Grid.SetRowSpan(element, rowSpan);
                Grid.SetColumn(element, 2);
                CoreGrid.Children.Add(element);
            }
        }

        private NumberBox NewNumberBox(double value)
        {
            // Item order matters: the range has to be in place before the value, or the
            // control formats the text twice.
            var box = new NumberBox
            {
                Minimum = CustomPreset.CoMinimum,
                Maximum = 0,
                MaxDecimalPlaces = 0,
                SmallChange = 1,
                LargeChange = 5,
                AcceptsExpression = false,
                ClearButtonEnabled = false,
                SpinButtonPlacementMode = Wpf.Ui.Controls.NumberBoxSpinButtonPlacementMode.Inline,
                Width = BoxWidth,
                MinHeight = 32,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 4, 0, 4),
            };

            box.Value = value;
            box.TextChanged += (_, _) => Validate();
            return box;
        }

        private static double SeedForCore(CpuCore core, IReadOnlyList<int>? currentValues) =>
            currentValues is not null && core.Index < currentValues.Count
                ? CustomPreset.ClampCo(currentValues[core.Index])
                : 0;

        private static double SeedForCcd(Ccd ccd, IReadOnlyList<int>? currentValues) =>
            ccd.Cores.Count > 0 ? SeedForCore(ccd.Cores[0], currentValues) : 0;

        /// <summary>Tab order is tree order, and the boxes were created per CCD; sort by cell.</summary>
        private void SortRowsInTabOrder()
        {
            _rows.Sort((a, b) =>
            {
                int byRow = Grid.GetRow(a.Box).CompareTo(Grid.GetRow(b.Box));
                return byRow != 0 ? byRow : Grid.GetColumn(a.Box).CompareTo(Grid.GetColumn(b.Box));
            });
        }

        /// <summary>
        /// Reads an offset from text, not from the control's value: the NumberBox clamps an
        /// out-of-range entry silently when focus leaves, so a typed +5 would read back as a
        /// valid 0. Shared with the tests.
        /// </summary>
        internal static bool TryParseOffset(string? text, out int value)
        {
            value = 0;

            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out double parsed))
                return false;

            if (parsed < CustomPreset.CoMinimum || parsed > 0)
                return false;

            value = (int)Math.Round(parsed);
            return true;
        }

        private void Validate()
        {
            int invalid = _rows.Count(row => !TryParseOffset(row.Box.Text, out _));

            ValidationText.Visibility = invalid > 0 ? Visibility.Visible : Visibility.Collapsed;
            SaveButton.IsEnabled = invalid == 0 && _rows.Count > 0;
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                DialogResult = false;
                return;
            }

            base.OnPreviewKeyDown(e);
        }

        private void Default_Click(object sender, RoutedEventArgs e)
        {
            MessageBoxResult answer = MessageBox.Show(
                this,
                LocalizationService.Get("Reset every core to 0? This discards your per-core offsets."),
                LocalizationService.Get("Reset to Default"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (answer != MessageBoxResult.Yes)
                return;

            foreach ((NumberBox box, _) in _rows)
                box.Value = 0;

            Validate();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            Result = null;
            DialogResult = false;
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            var values = new int[_coreCount];
            foreach ((NumberBox box, int coreIndex) in _rows)
            {
                if (!TryParseOffset(box.Text, out int value))
                {
                    Validate();
                    return;
                }

                values[coreIndex] = value;
            }

            Result = values;
            DialogResult = true;
        }
    }
}
