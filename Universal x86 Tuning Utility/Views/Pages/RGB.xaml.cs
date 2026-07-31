using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Universal_x86_Tuning_Utility.Models;
using Universal_x86_Tuning_Utility.Services;
using Universal_x86_Tuning_Utility.Views.Controls;

namespace Universal_x86_Tuning_Utility.Views.Pages
{
    /// <summary>
    /// Merged RGB control page with device selection (Keyboard/Lightbar),
    /// mode toggle (Effects/Per-Key), and shared idle timer.
    /// </summary>
    public partial class RGB : Page
    {
        private KeyboardHidService? _hidService;
        private KeyboardSettings? _settings;
        private bool _isFirstLoad = true;

        // Debounce timer for effects mode HID writes
        private readonly DispatcherTimer _applyDebounce = new()
        {
            Interval = TimeSpan.FromMilliseconds(150),
            IsEnabled = true
        };
        private bool _pendingApply;

        // Idle timer
        private readonly DispatcherTimer _idleCheckTimer = new()
        {
            Interval = TimeSpan.FromSeconds(30),
            IsEnabled = false
        };
        private readonly DispatcherTimer _idleWakeTimer = new()
        {
            Interval = TimeSpan.FromMilliseconds(500),
            IsEnabled = false
        };
        private bool _keyboardWasIdleOff;

        public RGB()
        {
            InitializeComponent();
            InitializeDebounce();
            InitializeIdleTimer();

            // Show unavailable state initially; HID opens in background
            RgbAvailable.Visibility = Visibility.Collapsed;
            RgbUnavailable.Visibility = Visibility.Visible;

            // Open HID device on background thread
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
                            RgbAvailable.Visibility = Visibility.Visible;
                            RgbUnavailable.Visibility = Visibility.Collapsed;

                            PopulateEffects();

                            var settings = KeyboardSettingsService.Load();
                            ApplySettings(settings);

                            // Apply saved mode to HID on background thread to avoid UI lag.
                            _ = Task.Run(() =>
                            {
                                try
                                {
                                    if (settings.PerKeyMode)
                                    {
                                        ApplyPerKeyColorsToHid();
                                    }
                                    else
                                    {
                                        ApplySettingsToHid();
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Debug.WriteLine($"[RGB] Startup apply failed: {ex.Message}");
                                }

                                // Load visualizer colors on UI thread after HID apply
                                if (settings.PerKeyMode)
                                {
                                    Application.Current.Dispatcher.Invoke(() => LoadPerKeyColors());
                                }
                            });
                        }
                        else
                        {
                            service.Dispose();
                            Debug.WriteLine("[RGB] HID keyboard not available");
                        }
                    });
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[RGB] HID init failed: {ex.Message}");
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        RgbAvailable.Visibility = Visibility.Collapsed;
                        RgbUnavailable.Visibility = Visibility.Visible;
                    });
                }
            });
        }

        private void Page_Loaded(object sender, RoutedEventArgs e)
        {
            _visualizer.KeysSelected += OnKeysSelected;
            UpdateEffectUi();
            _isFirstLoad = false;

            // If HID is already open, apply the saved mode on background thread.
            // This handles the case where HID callback fired before Page_Loaded
            // and the toggle event was suppressed by _isFirstLoad.
            if (_hidService?.IsAvailable == true)
            {
                var isPerKey = ModeToggle.IsChecked == true;
                _ = Task.Run(() =>
                {
                    try
                    {
                        if (isPerKey)
                        {
                            ApplyPerKeyColorsToHid();
                        }
                        else
                        {
                            ApplySettingsToHid();
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[RGB] Page_Loaded apply failed: {ex.Message}");
                    }

                    if (isPerKey)
                    {
                        Application.Current.Dispatcher.Invoke(() => LoadPerKeyColors());
                    }
                });
            }
        }

        private void Page_Unloaded(object sender, RoutedEventArgs e)
        {
            _visualizer.KeysSelected -= OnKeysSelected;
            _hidService?.Dispose();
        }

        #region Device Segment

        private void DeviceSegment_RadioButton_Click(object sender, RoutedEventArgs e)
        {
            bool isKeyboard = DeviceKeyboardRadio.IsChecked == true;
            KeyboardContent.Visibility = isKeyboard ? Visibility.Visible : Visibility.Collapsed;
            LightbarContent.Visibility = isKeyboard ? Visibility.Collapsed : Visibility.Visible;
        }

        #endregion

        #region Mode Toggle

        private void ModeToggle_Checked(object sender, RoutedEventArgs e)
        {
            bool isPerKey = ModeToggle.IsChecked == true;
            EffectsContent.Visibility = isPerKey ? Visibility.Collapsed : Visibility.Visible;
            PerKeyContent.Visibility = isPerKey ? Visibility.Visible : Visibility.Collapsed;

            if (_isFirstLoad)
                return;

            // Auto-apply the active mode when switching
            if (isPerKey)
            {
                LoadPerKeyColors();
                ApplyPerKeyColorsToHid();
            }
            else
            {
                ApplySettingsToHid();
            }

            SaveSettings();
        }

        private void ApplyPerKeyColorsToHid()
        {
            if (_hidService == null || !_hidService.IsAvailable || _settings == null)
                return;

            try
            {
                // Sync brightness from slider to HID service first.
                // The service's internal _brightness defaults to 50 and is only
                // updated when the user moves the slider. On startup/navigation
                // we must sync it so SendAllPerKeyColorsFromDict uses the right value.
                int brightnessLevel = (int)BrightnessSlider.Value;
                int brightnessPercent = (brightnessLevel * 100) / 7;
                _hidService.SetPerKeyBrightness(brightnessPercent);

                var colors = _settings.GetPerKeyColors();
                _hidService.SetEffect(KeyboardEffect.Static);
                _hidService.SendAllPerKeyColorsFromDict(colors);
                Debug.WriteLine("[RGB] Applied saved per-key colors to HID");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RGB] Per-key apply error: {ex.Message}");
            }
        }

        #endregion

        #region Effects Mode

        private void PopulateEffects()
        {
            cmbEffect.ItemsSource = Enum.GetValues<KeyboardEffect>();
        }

        /// <summary>Effects that support a 7-color palette.</summary>
        internal static readonly HashSet<KeyboardEffect> s_multiColor7Effects = new()
        {
            KeyboardEffect.Breathing,
            KeyboardEffect.Wave,
            KeyboardEffect.Reactive,
            KeyboardEffect.Ripple,
            KeyboardEffect.Marquee,
            KeyboardEffect.Raindrop,
            KeyboardEffect.RaindropFast,
            KeyboardEffect.Aurora,
            KeyboardEffect.TouchAurora,
            KeyboardEffect.TouchSpark,
            KeyboardEffect.Spark,
            KeyboardEffect.Music,
        };

        internal static readonly HashSet<KeyboardEffect> s_multiColor4Effects = new()
        {
            KeyboardEffect.GamingMode,
        };

        internal static readonly HashSet<KeyboardEffect> s_multiColor4Plus1Effects = new()
        {
            KeyboardEffect.GamingModeFull,
        };

        private static readonly HashSet<KeyboardEffect> s_animatedEffects = new()
        {
            KeyboardEffect.Breathing,
            KeyboardEffect.Wave,
            KeyboardEffect.Reactive,
            KeyboardEffect.Ripple,
            KeyboardEffect.TouchRipple,
            KeyboardEffect.Marquee,
            KeyboardEffect.Raindrop,
            KeyboardEffect.RaindropFast,
            KeyboardEffect.Aurora,
            KeyboardEffect.TouchAurora,
            KeyboardEffect.TouchSpark,
            KeyboardEffect.Spark,
            KeyboardEffect.Music,
        };

        private void CmbEffect_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isFirstLoad) return;
            UpdateEffectUi();
            _pendingApply = true;
        }

        private void UpdateEffectUi()
        {
            if (cmbEffect.SelectedItem is not KeyboardEffect effect)
            {
                ColorRow.Visibility = Visibility.Collapsed;
                MultiColorRow.Visibility = Visibility.Collapsed;
                GamingModeFullRestRow.Visibility = Visibility.Collapsed;
                SpeedRow.Visibility = Visibility.Collapsed;
                return;
            }

            if (s_multiColor7Effects.Contains(effect))
            {
                ColorRow.Visibility = Visibility.Collapsed;
                MultiColorRow.Visibility = Visibility.Visible;
                GamingModeFullRestRow.Visibility = Visibility.Collapsed;
                MultiColorTitle.Text = "Effect Colors";
                MultiColorSubtitle.Text = "7 colors required for this effect. Click a swatch to edit.";
                MultiColorPicker.SetColors(MultiColorPicker.Colors, 7, suppressEvents: true);
            }
            else if (s_multiColor4Effects.Contains(effect))
            {
                ColorRow.Visibility = Visibility.Collapsed;
                MultiColorRow.Visibility = Visibility.Visible;
                GamingModeFullRestRow.Visibility = Visibility.Collapsed;
                MultiColorTitle.Text = "WASD &amp; Arrow Keys";
                MultiColorSubtitle.Text = "4 colors for gaming keys. Click a swatch to edit.";
                MultiColorPicker.SetColors(MultiColorPicker.Colors, 4, suppressEvents: true);
            }
            else if (s_multiColor4Plus1Effects.Contains(effect))
            {
                ColorRow.Visibility = Visibility.Collapsed;
                MultiColorRow.Visibility = Visibility.Visible;
                GamingModeFullRestRow.Visibility = Visibility.Visible;
                MultiColorTitle.Text = "WASD &amp; Arrow Keys";
                MultiColorSubtitle.Text = "4 colors for gaming keys. Click a swatch to edit.";
                MultiColorPicker.SetColors(MultiColorPicker.Colors, 4, suppressEvents: true);
            }
            else
            {
                ColorRow.Visibility = Visibility.Visible;
                MultiColorRow.Visibility = Visibility.Collapsed;
                GamingModeFullRestRow.Visibility = Visibility.Collapsed;
            }

            SpeedRow.Visibility = s_animatedEffects.Contains(effect) ? Visibility.Visible : Visibility.Collapsed;
        }

        private void TsKeyboardPower_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isFirstLoad) return;
            if (tsKeyboardPower.IsChecked == false)
                _keyboardWasIdleOff = false;

            if (ModeToggle.IsChecked == true)
            {
                // Per-key mode: turn on/off without switching to effects
                if (_hidService?.IsAvailable == true)
                {
                    if (tsKeyboardPower.IsChecked == true)
                        ApplyPerKeyColorsToHid();
                    else
                        _hidService.TurnOff();
                }
                SaveSettings();
            }
            else
            {
                _pendingApply = true;
            }
        }

        private void ColorPicker_ColorChangedDelayed(object sender, EventArgs e)
        {
            if (_isFirstLoad) return;
            _pendingApply = true;
        }

        private void GamingModeFullRestColor_ColorChangedDelayed(object sender, EventArgs e)
        {
            if (_isFirstLoad) return;
            _pendingApply = true;
        }

        private void MultiColorPicker_ColorsChanged(object sender, EventArgs e)
        {
            _pendingApply = true;
        }

        private void BrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isFirstLoad) return;
            BrightnessValueText.Text = ((int)BrightnessSlider.Value).ToString();

            if (ModeToggle.IsChecked == true)
            {
                // Per-key mode: update brightness without switching to effects
                ApplyPerKeyBrightness();
                SaveSettings();
            }
            else
            {
                // Effects mode: debounce-apply
                _pendingApply = true;
            }
        }

        private void ApplyPerKeyBrightness()
        {
            if (_hidService == null || !_hidService.IsAvailable)
                return;

            try
            {
                int brightnessLevel = (int)BrightnessSlider.Value;
                int brightnessPercent = (brightnessLevel * 100) / 7;
                _hidService.SetPerKeyBrightness(brightnessPercent);

                // Re-send per-key colors so the brightness takes effect
                if (_settings != null)
                {
                    var colors = _settings.GetPerKeyColors();
                    _hidService.SendAllPerKeyColorsFromDict(colors);
                }

                Debug.WriteLine($"[RGB] Per-key brightness set to {brightnessPercent}%");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RGB] Per-key brightness error: {ex.Message}");
            }
        }

        private void SpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isFirstLoad) return;
            SpeedValueText.Text = ((int)SpeedSlider.Value).ToString();
            _pendingApply = true;
        }

        private static byte SliderToHidSpeed(int sliderValue)
        {
            return (byte)(10 - Math.Clamp(sliderValue, 1, 10));
        }

        private void ApplySettingsToHid()
        {
            if (_hidService is null || !_hidService.IsAvailable) return;

            try
            {
                bool powerOn = tsKeyboardPower.IsChecked == true;
                int brightnessLevel = (int)BrightnessSlider.Value;
                int brightnessPercent = (brightnessLevel * 100) / 7;
                byte speed = SliderToHidSpeed((int)SpeedSlider.Value);

                if (powerOn)
                {
                    var color = ColorPicker.SelectedColor;
                    _hidService.TurnOn(color.R, color.G, color.B, brightnessPercent);

                    KeyboardEffect effect = cmbEffect.SelectedItem is KeyboardEffect e ? e
                        : KeyboardSettingsService.Load().EffectMode;

                    if (s_multiColor7Effects.Contains(effect))
                    {
                        var colors = effect == KeyboardEffect.Marquee
                            ? MultiColorPicker.Colors.AsEnumerable().Reverse().ToList()
                            : MultiColorPicker.Colors;
                        _hidService.SetMultiColor(colors);
                    }
                    else if (s_multiColor4Effects.Contains(effect))
                    {
                        _hidService.SetMultiColor(MultiColorPicker.Colors.Take(4));
                    }
                    else if (s_multiColor4Plus1Effects.Contains(effect))
                    {
                        var allColors = MultiColorPicker.Colors.Take(4)
                            .Concat(new[] { GamingModeFullRestColor.SelectedColor })
                            .ToList();
                        _hidService.SetMultiColor(allColors);
                    }

                    _hidService.SetEffect(effect, speed);
                }
                else
                {
                    _hidService.TurnOff();
                }

                SaveSettings();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RGB] HID error: {ex.Message}");
            }
        }

        #endregion

        #region Per-Key Mode

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
                _hidService.SetEffect(KeyboardEffect.Static);

                for (int i = 0; i < KeyboardHidService.MaxPerKeyZones; i++)
                {
                    _hidService.SetPerKeyColor(i, color.R, color.G, color.B);
                    _visualizer.SetZoneColor(i, color);
                }

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

            foreach (var zoneIndex in selected)
            {
                _visualizer.SetZoneColor(zoneIndex, color);
            }

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
                _settings.SetPerKeyColors(allColors);
                KeyboardSettingsService.Save(_settings);

                Debug.WriteLine($"[RGB-PERKEY] Applied {color} to zones: {string.Join(", ", selected)}");
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

        #endregion

        #region Idle Timer

        [StructLayout(LayoutKind.Sequential)]
        private struct LASTINPUTINFO
        {
            public uint cbSize;
            public uint dwTime;
        }

        [DllImport("user32.dll")]
        private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

        private static int GetIdleMilliseconds()
        {
            var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO)) };
            if (GetLastInputInfo(ref lii))
                return Environment.TickCount - (int)lii.dwTime;
            return int.MaxValue;
        }

        private static readonly (int Seconds, string Label)[] s_idleTimerOptions =
        {
            (10, "10 s"), (15, "15 s"), (20, "20 s"), (30, "30 s"), (45, "45 s"),
            (60, "1 min"), (120, "2 min"), (300, "5 min"), (600, "10 min"),
            (1200, "20 min"), (1800, "30 min"), (3600, "1 h"),
            (7200, "2 h"), (10800, "3 h"),
        };

        private void InitializeIdleTimer()
        {
            cmbIdleTimer.ItemsSource = s_idleTimerOptions.Select(o => o.Label).ToList();
            cmbIdleTimer.SelectedIndex = 9;

            _idleCheckTimer.Tick += (s, e) =>
            {
                if (tsIdleTimer.IsChecked != true || _keyboardWasIdleOff)
                    return;

                int idleMs = GetIdleMilliseconds();
                int selectedIndex = cmbIdleTimer.SelectedIndex;
                if (selectedIndex < 0 || selectedIndex >= s_idleTimerOptions.Length)
                    return;

                int timeoutMs = s_idleTimerOptions[selectedIndex].Seconds * 1000;

                if (idleMs >= timeoutMs && _hidService?.IsAvailable == true)
                {
                    _hidService.TurnOff();
                    _keyboardWasIdleOff = true;
                    _idleCheckTimer.IsEnabled = false;
                    _idleWakeTimer.IsEnabled = true;
                    Debug.WriteLine($"[RGB] Idle timer: keyboard turned off after {idleMs / 1000}s");
                }
            };
            _idleCheckTimer.IsEnabled = true;

            _idleWakeTimer.Tick += (s, e) =>
            {
                if (!_keyboardWasIdleOff)
                    return;

                int idleMs = GetIdleMilliseconds();
                if (idleMs < 2000)
                {
                    _keyboardWasIdleOff = false;
                    tsKeyboardPower.IsChecked = true;
                    ApplySettingsToHid();
                    _idleWakeTimer.IsEnabled = false;
                    _idleCheckTimer.IsEnabled = true;
                    Debug.WriteLine($"[RGB] Idle timer: keyboard re-activated");
                }
            };
        }

        private void TsIdleTimer_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isFirstLoad) return;
            if (ModeToggle.IsChecked != true)
                _pendingApply = true;
            SaveSettings();
        }

        private void CmbIdleTimer_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isFirstLoad) return;
            if (ModeToggle.IsChecked != true)
                _pendingApply = true;
            SaveSettings();
        }

        #endregion

        #region Settings

        private void ApplySettings(KeyboardSettings settings)
        {
            tsKeyboardPower.IsChecked = settings.PowerOn;
            BrightnessSlider.Value = Math.Clamp(settings.Brightness, 1, 7);
            BrightnessValueText.Text = Math.Clamp(settings.Brightness, 1, 7).ToString();

            int hidSpeed = Math.Clamp((int)settings.Speed, 1, 10);
            SpeedSlider.Value = 10 - hidSpeed;
            SpeedValueText.Text = (10 - hidSpeed).ToString();

            ColorPicker.SelectedColor = Color.FromRgb(settings.ColorR, settings.ColorG, settings.ColorB);
            cmbEffect.SelectedItem = settings.EffectMode;

            if (!string.IsNullOrEmpty(settings.MultiColors))
            {
                var colors = ParseColorString(settings.MultiColors);
                if (colors.Count > 0)
                    MultiColorPicker.SetColors(colors, 7, suppressEvents: true);
            }

            tsIdleTimer.IsChecked = settings.IdleTimerEnabled;
            int savedSeconds = settings.IdleTimerMinutes * 60;
            int closestIndex = 0;
            int closestDiff = int.MaxValue;
            for (int i = 0; i < s_idleTimerOptions.Length; i++)
            {
                int diff = Math.Abs(s_idleTimerOptions[i].Seconds - savedSeconds);
                if (diff < closestDiff)
                {
                    closestDiff = diff;
                    closestIndex = i;
                }
            }
            cmbIdleTimer.SelectedIndex = closestIndex;

            // Restore per-key colors if in per-key mode
            _settings = settings;

            // Set mode toggle based on saved per-key mode
            ModeToggle.IsChecked = settings.PerKeyMode;
        }

        private void SaveSettings()
        {
            // Preserve existing per-key colors if in per-key mode
            var savedPerKeyColors = _settings?.PerKeyColors;

            var settings = new KeyboardSettings
            {
                PowerOn = tsKeyboardPower.IsChecked == true,
                ColorR = ColorPicker.SelectedColor.R,
                ColorG = ColorPicker.SelectedColor.G,
                ColorB = ColorPicker.SelectedColor.B,
                Brightness = (int)BrightnessSlider.Value,
                EffectMode = (KeyboardEffect)cmbEffect.SelectedItem,
                Speed = SliderToHidSpeed((int)SpeedSlider.Value),
                Direction = 1,
                MultiColors = SerializeColors(MultiColorPicker.Colors),
                IdleTimerEnabled = tsIdleTimer.IsChecked == true,
                IdleTimerMinutes = cmbIdleTimer.SelectedIndex >= 0
                    ? s_idleTimerOptions[cmbIdleTimer.SelectedIndex].Seconds / 60
                    : 10,
                PerKeyMode = ModeToggle.IsChecked == true,
                PerKeyColors = savedPerKeyColors,
            };
            KeyboardSettingsService.Save(settings);
        }

        private static string SerializeColors(List<Color> colors)
        {
            return string.Join(",", colors.Select(c => $"#{c.R:X2}{c.G:X2}{c.B:X2}"));
        }

        private static List<Color> ParseColorString(string data)
        {
            var result = new List<Color>();
            foreach (var part in data.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    var hex = part.Trim();
                    if (hex.StartsWith("#"))
                        hex = hex.Substring(1);
                    if (hex.Length == 6)
                    {
                        var r = byte.Parse(hex.Substring(0, 2), System.Globalization.NumberStyles.HexNumber);
                        var g = byte.Parse(hex.Substring(2, 2), System.Globalization.NumberStyles.HexNumber);
                        var b = byte.Parse(hex.Substring(4, 2), System.Globalization.NumberStyles.HexNumber);
                        result.Add(Color.FromRgb(r, g, b));
                    }
                }
                catch { }
            }
            return result;
        }

        #endregion

        #region Debounce

        private void InitializeDebounce()
        {
            _applyDebounce.Tick += (s, e) =>
            {
                if (_pendingApply)
                {
                    _pendingApply = false;

                    // Only apply effects-mode settings when NOT in per-key mode.
                    // Per-key mode has its own direct apply paths (brightness, power, etc.)
                    // and should not be overwritten by effects commands.
                    if (ModeToggle.IsChecked != true)
                    {
                        ApplySettingsToHid();
                    }
                }
            };
        }

        #endregion
    }
}
