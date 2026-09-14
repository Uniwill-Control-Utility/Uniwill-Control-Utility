using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using Uniwill_Control_Utility.Views.Controls;
using Wpf.Ui.Controls;

namespace Uniwill_Control_Utility.Views.Windows
{
    /// <summary>
    /// Dialog for editing per-key RGB colors. Used from Adaptive Mode to let users
    /// configure per-key colors per profile without navigating to the Keyboard page.
    /// 
    /// This dialog works with an isolated copy of colors — it does NOT touch the
    /// shared KeyboardSettings, does NOT write to HID, and does NOT save to disk.
    /// The caller is responsible for persisting and applying the result.
    /// </summary>
    public partial class KeyboardPerKeyDialog : FluentWindow
    {
        /// <summary>
        /// Returns true if the user clicked OK, false for Cancel or X.
        /// </summary>
        public bool Applied { get; private set; }

        /// <summary>
        /// Returns the current per-key colors from the visualizer.
        /// Populated on every close (OK, Cancel, or X) so the caller can
        /// persist the colors even when the user closes with the X button.
        /// </summary>
        public Dictionary<int, (byte R, byte G, byte B)>? ResultColors { get; private set; }

        private Dictionary<int, Color> _initialColors;

        /// <summary>
        /// Creates a new per-key RGB editor dialog with the given initial colors.
        /// Pass null or an empty dictionary for all-black defaults.
        /// </summary>
        public KeyboardPerKeyDialog(Dictionary<int, (byte R, byte G, byte B)> initialColors)
        {
            InitializeComponent();
            Loaded += KeyboardPerKeyDialog_Loaded;

            // Build a Color dictionary for the visualizer from the initial colors.
            var mediaColors = new Dictionary<int, Color>();
            if (initialColors != null)
            {
                foreach (var kvp in initialColors)
                {
                    mediaColors[kvp.Key] = Color.FromRgb(kvp.Value.R, kvp.Value.G, kvp.Value.B);
                }
            }
            _initialColors = mediaColors;
        }

        private void KeyboardPerKeyDialog_Loaded(object sender, RoutedEventArgs e)
        {
            Loaded -= KeyboardPerKeyDialog_Loaded;
            // Defer to Render so the visualizer Viewbox has measured its children.
            Application.Current.Dispatcher.InvokeAsync(() => LoadColors(),
                System.Windows.Threading.DispatcherPriority.Render);
            _statusText.Text = "Ready — click keys and choose a color, then Apply.";
        }

        private void LoadColors()
        {
            _visualizer.RefreshLayout();
            _visualizer.SetZoneColors(_initialColors);

            // Sync picker brush so hover/selection overlays use the color picker's color.
            _visualizer.SetPickerBrush(new SolidColorBrush(_colorPicker.SelectedColor));
        }

        private void SelectAll_Click(object sender, RoutedEventArgs e)
        {
            _visualizer.SelectAll();
            _statusText.Text = "All keys selected. Pick a color and click Apply.";
        }

        private void ClearSelection_Click(object sender, RoutedEventArgs e)
        {
            _visualizer.ClearSelection();
            _statusText.Text = "Selection cleared.";
        }

        private void FillAll_Click(object sender, RoutedEventArgs e)
        {
            var color = _colorPicker.SelectedColor;

            for (int i = 0; i < 126; i++)
                _visualizer.SetZoneColor(i, color);
            _visualizer.ClearSelection();
            _statusText.Text = $"All keys set to R={color.R} G={color.G} B={color.B}.";
        }

        private void Apply_Click(object sender, RoutedEventArgs e)
        {
            var selected = _visualizer.GetSelectedZoneIndices();
            if (selected.Count == 0)
            {
                _statusText.Text = "No keys selected — click keys on the keyboard first.";
                return;
            }

            var color = _colorPicker.SelectedColor;

            foreach (int zoneIndex in selected)
                _visualizer.SetZoneColor(zoneIndex, color);
            _visualizer.ClearSelection();
            _statusText.Text = $"Applied R={color.R} G={color.G} B={color.B} to {selected.Count} key{(selected.Count > 1 ? "s" : "")}.";
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            CollectResultColors();
            Applied = true;
            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            CollectResultColors();
            Applied = false;
            DialogResult = false;
            Close();
        }

        /// <summary>
        /// Collects the current visualizer colors into ResultColors.
        /// Called on every close so the caller can persist changes even when
        /// the user closes with the X button instead of OK.
        /// </summary>
        private void CollectResultColors()
        {
            var allColors = _visualizer.GetAllColors();
            ResultColors = new Dictionary<int, (byte, byte, byte)>();

            foreach (var kvp in allColors)
            {
                ResultColors[kvp.Key] = (kvp.Value.R, kvp.Value.G, kvp.Value.B);
            }
        }

        private void _colorPicker_ColorChanged(object sender, EventArgs e)
        {
            // Push the current picker color to all visualizer zones so hover and
            // selection overlays use the right color.
            _visualizer.SetPickerBrush(new SolidColorBrush(_colorPicker.SelectedColor));
        }
    }
}
