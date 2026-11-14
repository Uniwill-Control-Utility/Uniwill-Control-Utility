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
        // 116: measured floor for this template in Inline mode is 115 px - "-50" first paints
        // its full 20 px of ink there, while 114 clips a column - plus 1 px for hinting and
        // rounding at other display scales.
        private const double BoxWidth = 116;
        private const double HelperBoxWidth = 116;

        /// <summary>Core boxes in tab order, each paired with the topology core index it writes.</summary>
        private readonly List<(NumberBox Box, int CoreIndex, double Seed)> _rows = new();

        /// <summary>The per-CCD "Set all" inputs, kept with their seed for the same re-seat.</summary>
        private readonly List<(NumberBox Box, double Seed)> _helpers = new();

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
                // The template is applied by now. Seat every box from its seed once more, so a
                // control that dropped its text on the way in cannot open as an empty field.
                foreach ((NumberBox box, _, double seed) in _rows)
                    Seat(box, seed);
                foreach ((NumberBox box, double seed) in _helpers)
                    Seat(box, seed);

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
            var cells = new List<(FrameworkElement Element, int Row, int Column, int ColumnSpan)>();
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

                    // Header cell spans the whole group: [name][chip] on the left, the per-CCD
                    // helper on the right, filler between. ClipToBounds keeps a long name from
                    // painting over anything.
                    var header = new Grid { ClipToBounds = true, Margin = new Thickness(0, 0, 0, 4) };
                    header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                                  // name
                    header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                                  // chip
                    header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });            // filler
                    header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                                  // helper

                    var ccdName = new TextBlock
                    {
                        Text = string.Format(
                            CultureInfo.CurrentCulture,
                            LocalizationService.Get("CCD {0}"),
                            ccd.Index),
                        FontSize = 13,
                        FontWeight = FontWeights.SemiBold,
                        VerticalAlignment = VerticalAlignment.Center,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    };
                    Grid.SetColumn(ccdName, 0);
                    header.Children.Add(ccdName);

                    Border? chip = null;
                    if (ccd.IsX3D)
                    {
                        chip = X3DChip();
                        Grid.SetColumn(chip, 1);
                        header.Children.Add(chip);
                    }

                    double seed = SeedForCcd(ccd, currentValues);
                    NumberBox setAllBox = NewNumberBox(seed, HelperBoxWidth);
                    _helpers.Add((setAllBox, seed));

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
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(8, 0, 0, 0),
                    };
                    setAllPanel.Children.Add(setAllBox);
                    setAllPanel.Children.Add(setAllButton);
                    Grid.SetColumn(setAllPanel, 3);
                    header.Children.Add(setAllPanel);

                    // The chip and the helper keep their width; the name is what gives up room.
                    // The budget comes from the grid, not from the header's own width: a header
                    // that sized itself to its content would never shrink, and the group would
                    // spill past the window edge at the minimum width.
                    void CapNameWidth()
                    {
                        double budget = band.Count == 1
                            ? CoreGrid.ActualWidth
                            : (CoreGrid.ActualWidth - ColGap.ActualWidth) / band.Count;

                        double taken = setAllPanel.ActualWidth + setAllPanel.Margin.Left + setAllPanel.Margin.Right;
                        if (chip is not null)
                            taken += chip.ActualWidth + chip.Margin.Left + chip.Margin.Right;

                        ccdName.MaxWidth = Math.Max(0, budget - taken);
                    }

                    CoreGrid.SizeChanged += (_, _) => CapNameWidth();
                    setAllPanel.SizeChanged += (_, _) => CapNameWidth();
                    if (chip is not null)
                        chip.SizeChanged += (_, _) => CapNameWidth();
                    header.Loaded += (_, _) => CapNameWidth();

                    cells.Add((header, headerRow, labelColumn, 2));

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
                        cells.Add((label, row, labelColumn, 1));

                        double coreSeed = SeedForCore(core, currentValues);
                        NumberBox box = NewNumberBox(coreSeed, BoxWidth);
                        box.HorizontalAlignment = HorizontalAlignment.Right;
                        cells.Add((box, row, labelColumn + 1, 1));

                        coreBoxes.Add(box);
                        _rows.Add((box, core.Index, coreSeed));
                        row++;
                    }

                    setAllButton.Click += (_, _) =>
                    {
                        foreach (NumberBox box in coreBoxes)
                            Seat(box, setAllBox.Value ?? 0);
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

            foreach ((FrameworkElement element, int row, int column, int columnSpan) in cells.OrderBy(c => c.Row).ThenBy(c => c.Column))
            {
                Grid.SetRow(element, row);
                Grid.SetColumn(element, column);
                Grid.SetColumnSpan(element, columnSpan);
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

        /// <summary>The 3D V-Cache qualifier, on the CCD name's line: a bordered chip in the theme accent.</summary>
        private Border X3DChip() => new()
        {
            BorderBrush = (Brush)FindResource("AccentFillColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 1, 6, 2),
            Margin = new Thickness(8, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = LocalizationService.Get("3D V-Cache"),
                FontSize = 11,
                Foreground = (Brush)FindResource("AccentTextFillColorPrimaryBrush"),
            },
        };

        private NumberBox NewNumberBox(double value, double width)
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
                // Inline: the two separate chevrons every other NumberBox in the app shows
                // (Compact fuses them into one flyout button until the box has focus).
                SpinButtonPlacementMode = Wpf.Ui.Controls.NumberBoxSpinButtonPlacementMode.Inline,
                Width = width,
                MinHeight = 32,
                Padding = new Thickness(10, 4, 4, 4),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 4, 0, 4),
            };

            Seat(box, value);
            box.TextChanged += (_, _) => Validate();
            return box;
        }

        /// <summary>
        /// Puts the value in the box and in its text. A number box renders nothing while its
        /// value is null, and it nulls the value itself when its text is empty at template
        /// time, so an unseated or blank field would stay blank for good. Clamped, never null.
        /// </summary>
        private static void Seat(NumberBox box, double value)
        {
            value = Math.Clamp(value, CustomPreset.CoMinimum, 0);

            if (box.Value != value)
                box.Value = value;

            if (string.IsNullOrEmpty(box.Text))
                box.Text = value.ToString("0", CultureInfo.CurrentCulture);
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

            foreach ((NumberBox box, _, _) in _rows)
                Seat(box, 0);

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
            foreach ((NumberBox box, int coreIndex, _) in _rows)
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
