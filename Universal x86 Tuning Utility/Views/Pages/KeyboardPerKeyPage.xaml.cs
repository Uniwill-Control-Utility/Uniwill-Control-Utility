using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Universal_x86_Tuning_Utility.Models;
using Universal_x86_Tuning_Utility.Services;
using Universal_x86_Tuning_Utility.Views.Controls;

namespace Universal_x86_Tuning_Utility.Views.Pages
{
    /// <summary>
    /// Per-key RGB control page. Lets the user select individual keys on a keyboard
    /// visualizer and assign colors to each zone.
    /// </summary>
    public partial class KeyboardPerKeyPage : Page
    {
        private KeyboardHidService? _hidService;
        private KeyboardSettings? _settings;

        public KeyboardPerKeyPage()
        {
            InitializeComponent();

            // Show unavailable state initially
            PerKeyAvailable.Visibility = Visibility.Collapsed;
            PerKeyUnavailable.Visibility = Visibility.Visible;

            // Open HID on background thread
            _ = Task.Run(() =>
            {
                try
                {
                    var service = new KeyboardHidService();
                    bool available = service.Open();

                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        if (available)
                        {
                            _hidService = service;
                            PerKeyAvailable.Visibility = Visibility.Visible;
                            PerKeyUnavailable.Visibility = Visibility.Collapsed;

                            // Load saved settings
                            _settings = KeyboardSettingsService.Load();
                            LoadPerKeyColors();
                        }
                        else
                        {
                            service.Dispose();
                            Debug.WriteLine("[KBD-PERKEY] HID keyboard not available");
                        }
                    });
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[KBD-PERKEY] Init failed: {ex.Message}");
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        PerKeyAvailable.Visibility = Visibility.Collapsed;
                        PerKeyUnavailable.Visibility = Visibility.Visible;
                    });
                }
            });
        }

        private void Page_Loaded(object sender, RoutedEventArgs e)
        {
            // Wire up visualizer selection callback
            _visualizer.KeysSelected += OnKeysSelected;
        }

        private void Page_Unloaded(object sender, RoutedEventArgs e)
        {
            _visualizer.KeysSelected -= OnKeysSelected;
            _hidService?.Dispose();
        }

        private void OnKeysSelected(IList<int> indices)
        {
            if (indices.Count == 0)
            {
                _statusText.Text = "Select keys to edit their color.";
                return;
            }

            _statusText.Text = $"{indices.Count} key{(indices.Count > 1 ? "s" : "")} selected. Pick a color and click Apply.";
        }

        private void LoadPerKeyColors()
        {
            if (_settings == null)
                return;

            var colors = _settings.GetPerKeyColors();
            var mediaColors = new Dictionary<int, Color>();

            foreach (var kvp in colors)
            {
                mediaColors[kvp.Key] = Color.FromRgb(kvp.Value.R, kvp.Value.G, kvp.Value.B);
            }

            _visualizer.SetZoneColors(mediaColors);
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
            if (_hidService == null || !_hidService.IsAvailable)
            {
                _statusText.Text = "HID controller not available.";
                return;
            }

            var color = _colorPicker.SelectedColor;

            try
            {
                // Disable any active effect (Wave, Breathing, etc.) so per-key colors
                // aren't overridden by the firmware animation engine.
                _hidService.SetEffect(KeyboardEffect.Static);

                // Set all zones to the selected color
                for (int i = 0; i < KeyboardHidService.MaxPerKeyZones; i++)
                {
                    _hidService.SetPerKeyColor(i, color.R, color.G, color.B);
                    _visualizer.SetZoneColor(i, color);
                }

                // Update settings
                if (_settings == null)
                    _settings = new KeyboardSettings();

                var colors = new Dictionary<int, (byte, byte, byte)>();
                for (int i = 0; i < 126; i++)
                    colors[i] = (color.R, color.G, color.B);
                _settings.SetPerKeyColors(colors);
                KeyboardSettingsService.Save(_settings);

                _visualizer.ClearSelection();
                _statusText.Text = $"All keys set to {color}.";
            }
            catch (ObjectDisposedException)
            {
                _statusText.Text = "HID controller disconnected. Please navigate away and back.";
            }
            catch (Exception ex)
            {
                _statusText.Text = $"Fill All failed: {ex.Message}";
            }
        }

        private void Apply_Click(object sender, RoutedEventArgs e)
        {
            if (_hidService == null || !_hidService.IsAvailable)
            {
                _statusText.Text = "HID controller not available.";
                return;
            }

            var selected = _visualizer.GetSelectedZoneIndices();

            if (selected.Count == 0)
            {
                _statusText.Text = "No keys selected. Click keys on the keyboard first.";
                return;
            }

            var color = _colorPicker.SelectedColor;

            // Apply to hardware
            foreach (var zoneIndex in selected)
            {
                _visualizer.SetZoneColor(zoneIndex, color);
            }

            // Send all colors to hardware using new row-based per-key protocol
            if (_settings == null)
                _settings = new KeyboardSettings();

            var allColors = _settings.GetPerKeyColors();
            foreach (var zoneIndex in selected)
            {
                allColors[zoneIndex] = (color.R, color.G, color.B);
            }

            try
            {
                _hidService.SendAllPerKeyColorsFromDict(allColors);

                // Save settings
                _settings.SetPerKeyColors(allColors);
                KeyboardSettingsService.Save(_settings);

                Debug.WriteLine($"[KBD-PERKEY] Applied {color} to zones: {string.Join(", ", selected)}");
                _statusText.Text = $"Applied {color} to {selected.Count} key{(selected.Count > 1 ? "s" : "")}.";
            }
            catch (ObjectDisposedException)
            {
                _statusText.Text = "HID controller disconnected. Please navigate away and back.";
            }
            catch (Exception ex)
            {
                _statusText.Text = $"Apply failed: {ex.Message}";
            }
        }

        #region Debug

        private void _debugTestPattern_Click(object sender, RoutedEventArgs e)
        {
            if (_hidService == null)
            {
                AppendLog("HID device not available.");
                return;
            }

            try
            {
                _hidService.SendTestPattern();
                AppendLog("[CMD] TestPattern (row colors) - each row should show a solid color");
            }
            catch (Exception ex)
            {
                AppendLog($"TestPattern failed: {ex.Message}");
            }
        }

        private void _debugClearLog_Click(object sender, RoutedEventArgs e)
        {
            _debugLog.Text = string.Empty;
        }

        private void _debugCopyLog_Click(object sender, RoutedEventArgs e)
        {
            Clipboard.SetText(_debugLog.Text);
        }

        private void AppendLog(string line)
        {
            var ts = DateTime.Now.ToString("HH:mm:ss.fff");
            _debugLog.AppendText($"[{ts}] {line}{Environment.NewLine}");
            _debugLog.ScrollToEnd();
        }

        #endregion
    }
}
