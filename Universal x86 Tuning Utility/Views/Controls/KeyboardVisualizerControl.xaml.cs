using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Universal_x86_Tuning_Utility.Models;

namespace Universal_x86_Tuning_Utility.Views.Controls
{
    /// <summary>
    /// Full keyboard visualizer with 126 per-key zones on a Canvas.
    /// Canvas-based layout (like LenovoLegionToolkit) with Viewbox for scaling.
    /// Supports click-to-select, Ctrl+multi-select, and color updates.
    /// </summary>
    public partial class KeyboardVisualizerControl : UserControl
    {
        private readonly Dictionary<int, KeyboardZoneControl> _zoneControls = new();
        private bool _isBuilt;

        /// <summary>
        /// Raised when keys are selected (single or multi-select).
        /// </summary>
        public event Action<IList<int>>? KeysSelected;

        public KeyboardVisualizerControl()
        {
            InitializeComponent();
        }

        /// <summary>Lazy-loads the keyboard layout on first access to avoid navigation lag.</summary>
        private void EnsureBuilt()
        {
            if (_isBuilt)
                return;
            BuildKeyboard();
            _isBuilt = true;
        }

        private void BuildKeyboard()
        {
            var zones = KeyboardZone.GetAllZones();

            foreach (var zone in zones)
            {
                var control = new KeyboardZoneControl
                {
                    ZoneIndex = zone.Index,
                    Label = zone.Label,
                    ZoneBrush = new SolidColorBrush(zone.Color),
                    Width = zone.Width,
                    Height = zone.Height,
                    IsTabStop = false,
                };

                control.Click += OnZoneClick;

                Canvas.SetLeft(control, zone.X);
                Canvas.SetTop(control, zone.Y);

                _keyboardCanvas.Children.Add(control);
                _zoneControls[zone.Index] = control;
            }
        }

        private void OnZoneClick(object sender, RoutedEventArgs e)
        {
            if (sender is not KeyboardZoneControl control)
                return;

            bool isMultiSelect = Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl);

            if (!isMultiSelect)
            {
                // Clear all selections
                foreach (var c in _zoneControls.Values)
                    c.IsSelected = false;
            }

            // Toggle this key
            control.IsSelected = !control.IsSelected;

            // Fire event with selected zone indices
            var selected = GetSelectedZoneIndices();
            KeysSelected?.Invoke(selected);
        }

        /// <summary>
        /// Returns the list of currently selected zone indices.
        /// </summary>
        public IList<int> GetSelectedZoneIndices()
        {
            EnsureBuilt();
            var selected = new List<int>();
            foreach (var kvp in _zoneControls)
            {
                if (kvp.Value.IsSelected)
                    selected.Add(kvp.Key);
            }
            selected.Sort();
            return selected;
        }

        /// <summary>
        /// Clears all key selections.
        /// </summary>
        public void ClearSelection()
        {
            EnsureBuilt();
            foreach (var c in _zoneControls.Values)
                c.IsSelected = false;
        }

        /// <summary>
        /// Sets the color for a specific zone index.
        /// </summary>
        public void SetZoneColor(int zoneIndex, Color color)
        {
            EnsureBuilt();
            if (_zoneControls.TryGetValue(zoneIndex, out var control))
            {
                control.ZoneBrush = new SolidColorBrush(color);
            }
        }

        /// <summary>
        /// Sets colors for multiple zones at once.
        /// </summary>
        public void SetZoneColors(Dictionary<int, Color> colors)
        {
            EnsureBuilt();
            foreach (var kvp in colors)
            {
                if (_zoneControls.TryGetValue(kvp.Key, out var control))
                {
                    control.ZoneBrush = new SolidColorBrush(kvp.Value);
                }
            }
        }

        /// <summary>
        /// Gets the current color for a zone.
        /// </summary>
        public Color? GetZoneColor(int zoneIndex)
        {
            EnsureBuilt();
            if (_zoneControls.TryGetValue(zoneIndex, out var control))
                return control.ZoneColor;
            return null;
        }

        /// <summary>
        /// Gets all zone colors as a dictionary.
        /// </summary>
        public Dictionary<int, Color> GetAllColors()
        {
            EnsureBuilt();
            var colors = new Dictionary<int, Color>();
            foreach (var kvp in _zoneControls)
            {
                if (kvp.Value.ZoneColor.HasValue)
                    colors[kvp.Key] = kvp.Value.ZoneColor.Value;
            }
            return colors;
        }

        /// <summary>
        /// Selects all active zones.
        /// </summary>
        public void SelectAll()
        {
            EnsureBuilt();
            foreach (var c in _zoneControls.Values)
                c.IsSelected = true;
        }
    }
}
