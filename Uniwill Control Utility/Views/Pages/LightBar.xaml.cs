using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Uniwill_Control_Utility.Properties;
using Uniwill_Control_Utility.Services;
using Uniwill_Control_Utility.Views.Controls;

namespace Uniwill_Control_Utility.Views.Pages
{
    /// <summary>
    /// Front light bar control page.
    /// </summary>
    public partial class LightBar : Page
    {
        private KeyboardHidService? _hidService;
        private bool _isFirstLoad = true;

        // Debounce timer for color/brightness HID writes
        private readonly DispatcherTimer _applyDebounce = new()
        {
            Interval = TimeSpan.FromMilliseconds(150),
            IsEnabled = true
        };
        private bool _pendingApply;

        // Supported effects for the light bar (zone 2).
        // Each entry: (display name, effect byte, requires speed, color count)
        // colorCount=0 means no color control (fixed palette, e.g. Wave)
        // colorCount=1 means single color picker
        private static readonly (string Name, byte Value, bool HasSpeed, int ColorCount)[] Effects = new (string, byte, bool, int)[]
        {
            ("Static",    0x01, false, 1),
            ("Breathing", 0x02, true,  1),
            ("Wave",      0x03, true,  0),   // Fixed rainbow palette — no color control
            ("Loading",   0x04, true,  1),
            ("Marquee",   0x05, true,  0),   // Fixed palette — no color control
            ("Meteor",    0x06, true,  1),
        };

        public LightBar()
        {
            InitializeComponent();
            InitializeDebounce();
            PopulateEffects();

            // Show unavailable state initially; HID opens in background
            LightBarAvailable.Visibility = Visibility.Collapsed;
            LightBarUnavailable.Visibility = Visibility.Visible;

            // Open HID device on background thread
            _ = Task.Run(() =>
            {
                try
                {
                    var service = new KeyboardHidService();
                    bool available = service.Open();

                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        if (available && service.IsLightBarAvailable)
                        {
                            _hidService = service;
                            LightBarAvailable.Visibility = Visibility.Visible;
                            LightBarUnavailable.Visibility = Visibility.Collapsed;
                            Debug.WriteLine("[LB] HID light bar available");
                        }
                        else
                        {
                            service.Dispose();
                            Debug.WriteLine("[LB] HID light bar not available");
                        }
                    });
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[LB] HID init failed: {ex.Message}");
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        LightBarAvailable.Visibility = Visibility.Collapsed;
                        LightBarUnavailable.Visibility = Visibility.Visible;
                    });
                }
            });
        }

        private void PopulateEffects()
        {
            foreach (var (name, _, _, _) in Effects)
            {
                cbEffect.Items.Add(name);
            }
            cbEffect.SelectedIndex = 0; // Static
        }

        private byte GetSelectedEffect()
        {
            int index = cbEffect.SelectedIndex;
            if (index >= 0 && index < Effects.Length)
                return Effects[index].Value;
            return 0x01; // Static fallback
        }

        private (bool HasSpeed, int ColorCount) GetSelectedEffectInfo()
        {
            int index = cbEffect.SelectedIndex;
            if (index >= 0 && index < Effects.Length)
                return (Effects[index].HasSpeed, Effects[index].ColorCount);
            return (false, 1);
        }

        private void Page_Loaded(object sender, RoutedEventArgs e)
        {
            // Load saved settings from disk
            bool enabled = Settings.Default.LightBarEnabled;
            byte r = (byte)Settings.Default.LightBarR;
            byte g = (byte)Settings.Default.LightBarG;
            byte b = (byte)Settings.Default.LightBarB;
            int brightness = (int)Settings.Default.LightBarBrightness;
            int effectIndex = (int)Settings.Default.LightBarEffectIndex;
            int speed = (int)Settings.Default.LightBarSpeed;

            tsLightBarPower.IsChecked = enabled;
            ColorPicker.SelectedColor = Color.FromRgb(r, g, b);
            BrightnessSlider.Value = brightness;
            BrightnessValueText.Text = brightness.ToString();
            cbEffect.SelectedIndex = Math.Clamp(effectIndex, 0, Effects.Length - 1);
            SpeedSlider.Value = Math.Clamp(speed, 0, 11);
            SpeedValueText.Text = Math.Clamp(speed, 0, 11).ToString();

            _isFirstLoad = false;

            // Update UI visibility based on selected effect
            UpdateEffectUi();

            // Apply saved settings to HID on first load
            if (enabled && _hidService != null)
            {
                _ = Task.Run(() =>
                {
                    try
                    {
                        Thread.Sleep(200);
                        byte effect = GetSelectedEffect();
                        // UI speed is 0-11 (slow→fast), HID speed is 0-11 (fast→slow)
                        byte hidSpeed = (byte)(11 - SpeedSlider.Value);

                        _hidService?.LightBarTurnOn(r, g, b, brightness);
                        _hidService?.LightBarSetEffect(effect, hidSpeed, brightness);
                        Debug.WriteLine($"[LB] Page_Loaded apply: enabled={enabled}, color=({r},{g},{b}), brightness={brightness}, effect=0x{effect:X2}, hidSpeed={hidSpeed}");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[LB] Page_Loaded apply failed: {ex.Message}");
                    }
                });
            }
        }

        private void Page_Unloaded(object sender, RoutedEventArgs e)
        {
            // Keep HID handle open across navigation
        }

        private void InitializeDebounce()
        {
            _applyDebounce.Tick += (s, e) =>
            {
                if (_pendingApply)
                {
                    _pendingApply = false;
                    ApplyToHid();
                }
            };
        }

        private void TsLightBarPower_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isFirstLoad || _hidService == null)
                return;

            bool isOn = tsLightBarPower.IsChecked == true;
            var color = ColorPicker.SelectedColor;
            int brightness = (int)BrightnessSlider.Value;
            byte effect = GetSelectedEffect();
            byte hidSpeed = (byte)(11 - SpeedSlider.Value);

            _ = Task.Run(() =>
            {
                try
                {
                    Thread.Sleep(50);
                    if (isOn)
                    {
                        _hidService?.LightBarTurnOn(color.R, color.G, color.B, brightness);
                        _hidService?.LightBarSetEffect(effect, hidSpeed, brightness);
                    }
                    else
                    {
                        _hidService?.LightBarTurnOff();
                    }
                    Debug.WriteLine($"[LB] Power toggled: isOn={isOn}");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[LB] Power toggle failed: {ex.Message}");
                }
            });

            SaveSettings();
        }

        private void ColorPicker_ColorChangedDelayed(object sender, EventArgs e)
        {
            if (_isFirstLoad)
                return;

            _pendingApply = true;
            SaveSettings();
        }

        private void BrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            BrightnessValueText.Text = ((int)BrightnessSlider.Value).ToString();

            if (_isFirstLoad)
                return;

            _pendingApply = true;
            SaveSettings();
        }

        private void CbEffect_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isFirstLoad)
                return;

            UpdateEffectUi();
            _pendingApply = true;
            SaveSettings();
        }

        private void SpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            SpeedValueText.Text = ((int)SpeedSlider.Value).ToString();

            if (_isFirstLoad)
                return;

            _pendingApply = true;
            SaveSettings();
        }

        private void ApplyToHid()
        {
            if (_hidService == null)
                return;

            bool isOn = tsLightBarPower.IsChecked == true;
            var color = ColorPicker.SelectedColor;
            int brightness = (int)BrightnessSlider.Value;
            byte effect = GetSelectedEffect();
            byte hidSpeed = (byte)(11 - SpeedSlider.Value);

            _ = Task.Run(() =>
            {
                try
                {
                    Thread.Sleep(50);
                    if (isOn)
                    {
                        _hidService?.LightBarSetColor(color.R, color.G, color.B, brightness);
                        _hidService?.LightBarSetEffect(effect, hidSpeed, brightness);
                    }
                    else
                    {
                        _hidService?.LightBarTurnOff();
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[LB] Apply failed: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Updates the visibility of Speed card, Color card, and MultiColor card
        /// based on the currently selected effect.
        /// </summary>
        private void UpdateEffectUi()
        {
            var (hasSpeed, colorCount) = GetSelectedEffectInfo();

            // Speed card: visible only for animated effects
            SpeedCard.Visibility = hasSpeed ? Visibility.Visible : Visibility.Collapsed;

            // Color card: hidden for fixed-palette effects (Wave, Marquee)
            ColorCard.Visibility = colorCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SaveSettings()
        {
            Settings.Default.LightBarEnabled = tsLightBarPower.IsChecked == true;
            var color = ColorPicker.SelectedColor;
            Settings.Default.LightBarR = color.R;
            Settings.Default.LightBarG = color.G;
            Settings.Default.LightBarB = color.B;
            Settings.Default.LightBarBrightness = (int)BrightnessSlider.Value;
            Settings.Default.LightBarEffectIndex = cbEffect.SelectedIndex;
            Settings.Default.LightBarSpeed = (int)SpeedSlider.Value;
            Settings.Default.Save();
        }
    }
}
