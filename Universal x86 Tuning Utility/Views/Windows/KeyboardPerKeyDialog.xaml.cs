using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Universal_x86_Tuning_Utility.Models;
using Universal_x86_Tuning_Utility.Services;
using Universal_x86_Tuning_Utility.Views.Controls;
using Wpf.Ui.Controls;

namespace Universal_x86_Tuning_Utility.Views.Windows
{
    /// <summary>
    /// Dialog for editing per-key RGB colors. Used from Adaptive Mode to let users
    /// configure per-key colors per profile without navigating to the Keyboard page.
    /// </summary>
    public partial class KeyboardPerKeyDialog : FluentWindow
    {
        private KeyboardSettings _settings;

        /// <summary>
        /// Returns true if the user clicked OK (applied changes), false for Cancel.
        /// </summary>
        public bool Applied { get; private set; }

        /// <summary>
        /// Returns the current per-key colors from the visualizer after OK.
        /// </summary>
        public Dictionary<int, (byte R, byte G, byte B)>? ResultColors { get; private set; }

        public KeyboardPerKeyDialog(KeyboardSettings settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            InitializeComponent();
            Loaded += KeyboardPerKeyDialog_Loaded;
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
            // Ensure the visualizer is built and has been measured.
            _visualizer.RefreshLayout();

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
            var color = _colorPicker.SelectedColor;
            var statuses = new[] { _fillAllBtn, _applyBtn };

            foreach (var btn in statuses)
                btn.IsEnabled = false;
            _statusText.Text = "Applying to all keys...";

            _ = Task.Run(() =>
            {
                try
                {
                    var hid = new KeyboardHidService();
                    try
                    {
                        if (!hid.Open())
                        {
                            Application.Current.Dispatcher.Invoke(() =>
                            {
                                _statusText.Text = "HID controller not available.";
                                foreach (var btn in statuses)
                                    btn.IsEnabled = true;
                            });
                            return;
                        }

                        hid.SetEffect(KeyboardEffect.Static);

                        for (int i = 0; i < KeyboardHidService.MaxPerKeyZones; i++)
                        {
                            hid.SetPerKeyColor(i, color.R, color.G, color.B);
                        }

                        // Save settings
                        var colors = new Dictionary<int, (byte, byte, byte)>();
                        for (int i = 0; i < KeyboardHidService.MaxPerKeyZones; i++)
                            colors[i] = (color.R, color.G, color.B);
                        _settings.SetPerKeyColors(colors);
                        KeyboardSettingsService.Save(_settings);

                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            for (int i = 0; i < KeyboardHidService.MaxPerKeyZones; i++)
                                _visualizer.SetZoneColor(i, color);
                            _visualizer.ClearSelection();
                            _statusText.Text = $"All keys set to R={color.R} G={color.G} B={color.B}.";
                            foreach (var btn in statuses)
                                btn.IsEnabled = true;
                        });
                    }
                    catch (ObjectDisposedException)
                    {
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            _statusText.Text = "HID controller disconnected.";
                            foreach (var btn in statuses)
                                btn.IsEnabled = true;
                        });
                    }
                    finally
                    {
                        hid.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        _statusText.Text = $"Fill All failed: {ex.Message}";
                        foreach (var btn in statuses)
                            btn.IsEnabled = true;
                    });
                }
            });
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
            var statuses = new[] { _fillAllBtn, _applyBtn };

            foreach (var btn in statuses)
                btn.IsEnabled = false;
            _statusText.Text = "Applying...";

            _ = Task.Run(() =>
            {
                try
                {
                    var hid = new KeyboardHidService();
                    try
                    {
                        if (!hid.Open())
                        {
                            Application.Current.Dispatcher.Invoke(() =>
                            {
                                _statusText.Text = "HID controller not available.";
                                foreach (var btn in statuses)
                                    btn.IsEnabled = true;
                            });
                            return;
                        }

                        // Update settings dictionary with new colors
                        var allColors = _settings.GetPerKeyColors();
                        foreach (int zoneIndex in selected)
                        {
                            allColors[zoneIndex] = (color.R, color.G, color.B);
                        }

                        // Send all colors to device
                        hid.SendAllPerKeyColorsFromDict(allColors);
                        _settings.SetPerKeyColors(allColors);
                        KeyboardSettingsService.Save(_settings);

                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            foreach (int zoneIndex in selected)
                                _visualizer.SetZoneColor(zoneIndex, color);
                            _visualizer.ClearSelection();
                            _statusText.Text = $"Applied R={color.R} G={color.G} B={color.B} to {selected.Count} key{(selected.Count > 1 ? "s" : "")}.";
                            foreach (var btn in statuses)
                                btn.IsEnabled = true;
                        });
                    }
                    catch (ObjectDisposedException)
                    {
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            _statusText.Text = "HID controller disconnected.";
                            foreach (var btn in statuses)
                                btn.IsEnabled = true;
                        });
                    }
                    finally
                    {
                        hid.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        _statusText.Text = $"Apply failed: {ex.Message}";
                        foreach (var btn in statuses)
                            btn.IsEnabled = true;
                    });
                }
            });
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            // Collect colors from visualizer
            var allColors = _visualizer.GetAllColors();
            ResultColors = new Dictionary<int, (byte, byte, byte)>();

            foreach (var kvp in allColors)
            {
                ResultColors[kvp.Key] = (kvp.Value.R, kvp.Value.G, kvp.Value.B);
            }

            // Update the settings
            _settings.SetPerKeyColors(ResultColors);
            KeyboardSettingsService.Save(_settings);

            // Send colors to device on background thread
            _ = Task.Run(() =>
            {
                var hid = new KeyboardHidService();
                try
                {
                    if (hid.Open())
                    {
                        hid.SendAllPerKeyColorsFromDict(ResultColors);
                    }
                }
                catch
                {
                    // Device may not be available — settings are saved anyway
                }
                finally
                {
                    hid.Dispose();
                }
            });

            Applied = true;
            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            Applied = false;
            ResultColors = null;
            DialogResult = false;
            Close();
        }
    }
}
