using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Universal_x86_Tuning_Utility.Models;
using Universal_x86_Tuning_Utility.Services;

namespace Universal_x86_Tuning_Utility.Views.Pages
{
    /// <summary>
    /// Keyboard RGB backlight control page.
    /// Uses HID feature reports to control the ITE lighting controller (vid_048d).
    /// The keyboard backlight is NOT controlled through EC registers.
    /// </summary>
    public partial class Keyboard : Page
    {
        private KeyboardHidService? _hidService;
        private UniwillECService? _ecService;

        public Keyboard()
        {
            InitializeComponent();
            InitializeDebounce();
            InitializeIdleTimer();

            // Show unavailable state initially; HID opens in background
            KeyboardAvailable.Visibility = Visibility.Collapsed;
            KeyboardUnavailable.Visibility = Visibility.Visible;

            // Open HID device on background thread to avoid blocking navigation
            _ = Task.Run(() =>
            {
                try
                {
                    var service = new KeyboardHidService();
                    bool hidAvailable = service.Open();

                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        if (hidAvailable)
                        {
                            _hidService = service;
                            KeyboardAvailable.Visibility = Visibility.Visible;
                            KeyboardUnavailable.Visibility = Visibility.Collapsed;

                            PopulateEffects();

                            var settings = KeyboardSettingsService.Load();
                            ApplySettings(settings);
                        }
                        else
                        {
                            service.Dispose();
                            Debug.WriteLine("[KBD] HID keyboard not available");
                        }
                    });
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[KBD] HID keyboard init failed: {ex.Message}");
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        KeyboardAvailable.Visibility = Visibility.Collapsed;
                        KeyboardUnavailable.Visibility = Visibility.Visible;
                    });
                }
            });

            // Initialize EC for diagnostic panel on background thread
            _ = Task.Run(() =>
            {
                try
                {
                    _ecService = App.GetService<UniwillECService>();
                }
                catch { /* EC optional for diagnostics */ }
            });
        }

        private bool _isFirstLoad = true;

        private void Page_Loaded(object sender, RoutedEventArgs e)
        {
            // Update UI to reflect saved settings (effect selector, color pickers, sliders)
            UpdateEffectUi();

            // Initialize speed byte tester
            InitializeSpeedTester();

            // Do NOT re-apply HID settings here — ApplyKeyboardOnStart() in MainWindow
            // already applied them at startup. Re-applying causes a visible flicker.
            // Live user changes trigger ApplySettingsToHid() directly via their event handlers.
            _isFirstLoad = false;

            // Run EC dump on background thread so it doesn't block page navigation
            Task.Run(() =>
            {
                RefreshEcDump();
            });
        }

        private void TsKeyboardPower_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isFirstLoad) return;
            // If user manually toggles, reset idle tracking
            if (tsKeyboardPower.IsChecked == false)
                _keyboardWasIdleOff = false;
            _pendingApply = true;
        }

        private void PopulateEffects()
        {
            cmbEffect.ItemsSource = Enum.GetValues<KeyboardEffect>();
        }

        private void CmbEffect_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isFirstLoad) return;
            UpdateEffectUi();
            _pendingApply = true;
        }

        private void MultiColorPicker_ColorsChanged(object sender, EventArgs e)
        {
            _pendingApply = true;
        }

        private void SpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isFirstLoad) return;
            SpeedValueText.Text = ((int)SpeedSlider.Value).ToString();
            _pendingApply = true;
        }

        /// <summary>
        /// Converts slider value (1=slowest, 10=fastest) to HID speed byte (0x0A=slow, 0x01=fast).
        /// Slider 1 → HID 0x0A (near stationary), Slider 10 → HID 0x01 (fast).
        /// HID byte 0x00 (max speed) and 0x0B (frozen) are excluded.
        /// </summary>
        private static byte SliderToHidSpeed(int sliderValue)
        {
            return (byte)(10 - Math.Clamp(sliderValue, 1, 10));
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

        /// <summary>Effects that support a 4-color palette (GamingMode).</summary>
        internal static readonly HashSet<KeyboardEffect> s_multiColor4Effects = new()
        {
            KeyboardEffect.GamingMode,
        };

        /// <summary>Effects with 4 colors for WASD/arrows + 1 for the rest (GamingModeFull).</summary>
        internal static readonly HashSet<KeyboardEffect> s_multiColor4Plus1Effects = new()
        {
            KeyboardEffect.GamingModeFull,
        };

        internal static bool IsMultiColor7Effect(KeyboardEffect effect) => s_multiColor7Effects.Contains(effect);
        internal static bool IsMultiColor4Effect(KeyboardEffect effect) => s_multiColor4Effects.Contains(effect);
        internal static bool IsMultiColor4Plus1Effect(KeyboardEffect effect) => s_multiColor4Plus1Effects.Contains(effect);

        /// <summary>All effects that show any multi-color picker.</summary>
        private static readonly HashSet<KeyboardEffect> s_multiColorEffects = new()
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
            KeyboardEffect.GamingMode,
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

        private void BrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isFirstLoad) return;
            BrightnessValueText.Text = ((int)BrightnessSlider.Value).ToString();
            _pendingApply = true;
        }

        #region EC Diagnostic

        private async void BtnRefreshEcDump_Click(object sender, RoutedEventArgs e)
        {
            await Task.Run(() => RefreshEcDump());
        }

        private void BtnCopyEcDump_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(txtEcDump.Text))
            {
                try
                {
                    Clipboard.SetText(txtEcDump.Text);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[KBD] Copy failed: {ex.Message}");
                }
            }
        }

        private void RefreshEcDump()
        {
            if (_ecService == null)
            {
                txtEcDump.Text = "EC service not available. This feature requires ACPIDriverDll.dll and admin privileges.";
                return;
            }

            try
            {
                var sb = new StringBuilder();

                // RGB keyboard registers
                var registers = new (ushort Address, string Name)[]
                {
                    (0x0765, "SUPPORT_1"),
                    (0x0766, "SUPPORT_2"),
                    (0x0767, "TRIGGER"),
                    (0x0769, "RGB_RED"),
                    (0x076A, "RGB_GREEN"),
                    (0x076B, "RGB_BLUE"),
                    (0x078C, "KBD_STATUS"),
                    (0x0740, "PROJECT_ID"),
                    (0x0741, "AP_OEM"),
                    (0x0751, "MANUAL_FAN_CTRL"),
                    (0x0782, "BIOS_OEM_2"),
                    (0x0783, "PL1_SETTING"),
                    (0x0784, "PL2_SETTING"),
                    (0x0785, "PL4_SETTING"),
                    (0x0786, "TCC_OFFSET"),
                    (0x07AB, "MODE_INDEX"),
                };

                sb.AppendLine("=== EC Register Dump (Keyboard RGB) ===");
                sb.AppendLine();
                sb.AppendLine($"{"Address":8}  {"Name":20}  {"Value (dec)":12}  {"Value (hex)":10}  {"Binary"}");
                sb.AppendLine(new string('-', 70));

                foreach (var (address, name) in registers)
                {
                    byte value = _ecService.ReadECRegister(address);
                    sb.AppendLine($"0x{address:X4}  {name,-20}  {value,12}  0x{value:X2}      {Convert.ToString(value, 2).PadLeft(8, '0')}");
                }

                sb.AppendLine();
                sb.AppendLine("--- Trigger register bit decode ---");
                byte trigger = _ecService.ReadECRegister(0x0767);
                sb.AppendLine($"TRIGGER (0x0767) = 0x{trigger:X2}");
                sb.AppendLine($"  bit 0 (0x01) KBD_POWER_OFF:         {((trigger & 0x01) != 0 ? "ON" : "OFF")}");
                sb.AppendLine($"  bit 1 (0x02) KBD_BRIGHTNESS_0:      {((trigger & 0x02) != 0 ? "SET" : "CLR")}");
                sb.AppendLine($"  bit 2 (0x04) KBD_BRIGHTNESS_1:      {((trigger & 0x04) != 0 ? "SET" : "CLR")}");
                sb.AppendLine($"  bit 3 (0x08) KBD_BRIGHTNESS_2:      {((trigger & 0x08) != 0 ? "SET" : "CLR")}");
                sb.AppendLine($"  bit 4 (0x10) KBD_APPLY:             {((trigger & 0x10) != 0 ? "SET" : "CLR")}");
                sb.AppendLine($"  bit 5 (0x20) KBD_WHITE_ONLY:        {((trigger & 0x20) != 0 ? "YES" : "NO")}");
                sb.AppendLine($"  bit 6 (0x40) TRIGGER_RGB_LOGO:      {((trigger & 0x40) != 0 ? "ACTIVE" : "INACTIVE")}");
                sb.AppendLine($"  bit 7 (0x80) TRIGGER_RGB_RAINBOW:   {((trigger & 0x80) != 0 ? "ACTIVE" : "INACTIVE")}");

                sb.AppendLine();
                sb.AppendLine("--- KBD_STATUS bit decode ---");
                byte kbdStatus = _ecService.ReadECRegister(0x078C);
                sb.AppendLine($"KBD_STATUS (0x078C) = 0x{kbdStatus:X2}");
                sb.AppendLine($"  bit 0 (0x01) KBD_POWER_ON:          {((kbdStatus & 0x01) != 0 ? "ON" : "OFF")}");
                sb.AppendLine($"  bit 1 (0x02) KBD_POWER_OFF:         {((kbdStatus & 0x02) != 0 ? "SET" : "CLR")}");
                sb.AppendLine($"  bit 2 (0x04) KBD_BRIGHTNESS_0:      {((kbdStatus & 0x04) != 0 ? "SET" : "CLR")}");
                sb.AppendLine($"  bit 3 (0x08) KBD_BRIGHTNESS_1:      {((kbdStatus & 0x08) != 0 ? "SET" : "CLR")}");
                sb.AppendLine($"  bit 4 (0x10) KBD_APPLY:             {((kbdStatus & 0x10) != 0 ? "SET" : "CLR")}");
                sb.AppendLine($"  bit 5 (0x20) KBD_WHITE_ONLY:        {((kbdStatus & 0x20) != 0 ? "YES" : "NO")}");
                sb.AppendLine($"  bits 6-7 (0xC0) BRIGHTNESS_LEVEL:   {((kbdStatus & 0xC0) >> 6),2} (0-3)");
                sb.AppendLine($"  brightness: {((kbdStatus & 0xC0) >> 6)}, white-only: {((kbdStatus & 0x20) != 0)}");

                sb.AppendLine();
                sb.AppendLine("--- SUPPORT flags ---");
                byte support1 = _ecService.ReadECRegister(0x0765);
                byte support2 = _ecService.ReadECRegister(0x0766);
                sb.AppendLine($"SUPPORT_1 (0x0765) = 0x{support1:X2}");
                sb.AppendLine($"  bit 0 (0x01) KBD_SUPPORT:           {((support1 & 0x01) != 0 ? "YES" : "NO")}");
                sb.AppendLine($"  bit 1 (0x02) RGB_SUPPORT:           {((support1 & 0x02) != 0 ? "YES" : "NO")}");
                sb.AppendLine($"SUPPORT_2 (0x0766) = 0x{support2:X2}");
                sb.AppendLine($"  bit 0 (0x01) KBD_SUPPORT_2:         {((support2 & 0x01) != 0 ? "YES" : "NO")}");
                sb.AppendLine($"  bit 1 (0x02) RGB_SUPPORT_2:         {((support2 & 0x02) != 0 ? "YES" : "NO")}");
                sb.AppendLine($"  bit 2 (0x04) RGB_SUPPORT_2_ALT:     {((support2 & 0x04) != 0 ? "YES" : "NO")}");

                sb.AppendLine();
                sb.AppendLine("--- Current RGB color ---");
                byte r = _ecService.ReadECRegister(0x0769);
                byte g = _ecService.ReadECRegister(0x076A);
                byte b = _ecService.ReadECRegister(0x076B);
                sb.AppendLine($"Color: R={r} G={g} B={b} (0x{r:X2}{g:X2}{b:X2})");

                sb.AppendLine();
                sb.AppendLine($"Dumped at {DateTime.Now:HH:mm:ss.fff}");

                // Dispatch back to UI thread to update the text block
                Application.Current.Dispatcher.Invoke(() =>
                {
                    txtEcDump.Text = sb.ToString();
                });
            }
            catch (Exception ex)
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    txtEcDump.Text = $"Error reading EC registers: {ex.Message}";
                });
                Debug.WriteLine($"[KBD] EC dump error: {ex.Message}");
            }
        }

        #endregion

        #region HID Diagnostic

        private async void BtnScanHidReports_Click(object sender, RoutedEventArgs e)
        {
            if (_hidService == null)
            {
                txtHidDump.Text = "HID service not available.";
                return;
            }

            txtHidDump.Text = "Scanning...";
            var hidService = _hidService;

            await Task.Run(() =>
            {
                try
                {
                    var sb = new StringBuilder();
                    sb.AppendLine("=== HID Feature Report Scan ===");
                    sb.AppendLine($"Scanning report IDs 0x00-0x1F at {DateTime.Now:HH:mm:ss.fff}");
                    sb.AppendLine();

                    int successCount = 0;
                    for (byte id = 0; id <= 0x1F; id++)
                    {
                        byte[]? result = hidService.ReadFeatureReport(id);
                        if (result != null)
                        {
                            sb.AppendLine($"Report 0x{id:X2}: {string.Join(" ", result.Select(b => b.ToString("X2")))}");
                            successCount++;
                        }
                    }

                    sb.AppendLine();
                    sb.AppendLine($"Found {successCount} readable report(s) out of 32");
                    sb.AppendLine();
                    sb.AppendLine("Note: If no reports are readable, the controller may not support HidD_GetFeature.");
                    sb.AppendLine("In that case, use the Raw Report Sender below to test effect modes.");

                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        txtHidDump.Text = sb.ToString();
                    });
                }
                catch (Exception ex)
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        txtHidDump.Text = $"Error scanning HID reports: {ex.Message}";
                    });
                    Debug.WriteLine($"[KBD] HID scan error: {ex.Message}");
                }
            });
        }

        private void BtnCopyHidDump_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(txtHidDump.Text))
            {
                try
                {
                    Clipboard.SetText(txtHidDump.Text);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[KBD] Copy failed: {ex.Message}");
                }
            }
        }

        private void BtnSendRawReport_Click(object sender, RoutedEventArgs e)
        {
            if (_hidService == null)
            {
                txtRawResult.Text = "HID service not available.";
                return;
            }

            try
            {
                string input = txtRawReport.Text.Trim();
                string[] parts = input.Split(new[] { ' ', ',', '\t' }, StringSplitOptions.RemoveEmptyEntries);

                if (parts.Length != 9)
                {
                    txtRawResult.Text = $"Expected 9 bytes, got {parts.Length}.";
                    return;
                }

                byte[] report = new byte[9];
                for (int i = 0; i < 9; i++)
                {
                    report[i] = byte.Parse(parts[i], System.Globalization.NumberStyles.HexNumber);
                }

                _hidService.SendRawReport(report);
                txtRawResult.Text = $"Sent: {string.Join(" ", report.Select(b => b.ToString("X2")))}";
                Debug.WriteLine($"[KBD] Raw report sent: {string.Join(" ", report.Select(b => b.ToString("X2")))}");
            }
            catch (Exception ex)
            {
                txtRawResult.Text = $"Error: {ex.Message}";
                Debug.WriteLine($"[KBD] Raw report error: {ex.Message}");
            }
        }

        private void EffectModeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_hidService == null || !_hidService.IsAvailable)
            {
                return;
            }

            try
            {
                byte effectMode = (byte)((int)EffectModeSlider.Value);
                int brightnessLevel = (int)BrightnessSlider.Value;
                int brightnessPercent = (brightnessLevel * 100) / 7;

                // Update the hex label
                EffectModeLabel.Text = $"0x{effectMode:X2}";

                // Build CMD_MODE_BRIGHTNESS report: 00 08 02 [effect] [speed] [brightness] 08 00 00
                // byte[3] = effect mode, byte[4] = speed (0=fastest, 0x0B=frozen)
                byte[] report = new byte[]
                {
                    0x00,                    // Report ID
                    0x08,                    // CMD_MODE_BRIGHTNESS
                    0x02,                    // Zone mask (keyboard)
                    effectMode,              // Effect mode byte to test (byte[3])
                    0x05,                    // Speed (medium)
                    (byte)brightnessPercent,  // Brightness
                    0x08,                    // Unknown constant
                    0x00,                    // Reserved
                    0x00                     // Reserved
                };

                _hidService.SendRawReport(report);
                txtEffectResult.Text = $"Sent: {string.Join(" ", report.Select(b => b.ToString("X2")))}";
                Debug.WriteLine($"[KBD] Effect test byte[3]=0x{effectMode:X2}: {string.Join(" ", report.Select(b => b.ToString("X2")))}");
            }
            catch (Exception ex)
            {
                txtEffectResult.Text = $"Error: {ex.Message}";
                Debug.WriteLine($"[KBD] Effect test error: {ex.Message}");
            }
        }

        #endregion

        #region Speed Byte Tester

        private void InitializeSpeedTester()
        {
            var items = new[]
            {
                "byte[4] - Speed (CONFIRMED: 0=fast, 0B=stop)",
                "byte[3] - Effect mode",
                "byte[5] - Brightness",
                "byte[6] - Unknown constant (08)",
                "byte[7] - Reserved",
                "byte[8] - Reserved",
            };
            cmbSpeedBytePos.ItemsSource = items;
            cmbSpeedBytePos.SelectedIndex = 0;
            SpeedTestSlider.Minimum = 0;
            SpeedTestSlider.Maximum = 11;
            SpeedTestSlider.Value = 5;
            SpeedTestValueLabel.Text = SpeedTestSlider.Value.ToString();
            UpdateSpeedTestPreview();
            _speedTesterReady = true;
        }

        private bool _speedTesterReady;

        /// <summary>Debounces rapid HID writes so the user gets instant UI feedback.</summary>
        private readonly DispatcherTimer _applyDebounce = new()
        {
            Interval = TimeSpan.FromMilliseconds(150),
            IsEnabled = true
        };

        private bool _pendingApply;

        private void InitializeDebounce()
        {
            _applyDebounce.Tick += (s, e) =>
            {
                if (_pendingApply)
                {
                    _pendingApply = false;
                    ApplySettingsToHid();
                }
            };
        }

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

        /// <summary>Checks for idle timeout. Runs every 30s while keyboard is on.</summary>
        private readonly DispatcherTimer _idleCheckTimer = new()
        {
            Interval = TimeSpan.FromSeconds(30),
            IsEnabled = false
        };

        /// <summary>Wakes the keyboard on user input. Runs every 500ms when keyboard is idle-off.</summary>
        private readonly DispatcherTimer _idleWakeTimer = new()
        {
            Interval = TimeSpan.FromMilliseconds(500),
            IsEnabled = false
        };

        /// <summary>Tracks whether the keyboard was turned off by the idle timer.</summary>
        private bool _keyboardWasIdleOff;

        /// <summary>Idle timer options matching XMG CC: seconds and minutes.</summary>
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
            cmbIdleTimer.SelectedIndex = 9; // 10 min default

            // Slow timer: checks if user has been idle long enough to turn off keyboard
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
                    // Switch timers: stop slow check, start fast wake
                    _idleCheckTimer.IsEnabled = false;
                    _idleWakeTimer.IsEnabled = true;
                    Debug.WriteLine($"[KBD] Idle timer: keyboard turned off after {idleMs / 1000}s of inactivity");
                }
            };
            _idleCheckTimer.IsEnabled = true;

            // Fast timer: wakes keyboard instantly when user becomes active
            _idleWakeTimer.Tick += (s, e) =>
            {
                if (!_keyboardWasIdleOff)
                    return;

                int idleMs = GetIdleMilliseconds();
                if (idleMs < 2000) // User interacted within last 2 seconds
                {
                    _keyboardWasIdleOff = false;
                    tsKeyboardPower.IsChecked = true;
                    ApplySettingsToHid();
                    // Switch timers back
                    _idleWakeTimer.IsEnabled = false;
                    _idleCheckTimer.IsEnabled = true;
                    Debug.WriteLine($"[KBD] Idle timer: keyboard re-activated after user input");
                }
            };
        }

        private void TsIdleTimer_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isFirstLoad) return;
            _pendingApply = true;
        }

        private void CmbIdleTimer_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isFirstLoad) return;
            _pendingApply = true;
        }

        #endregion

        private void SpeedTestSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_speedTesterReady || _hidService == null || !_hidService.IsAvailable)
                return;
            SpeedTestValueLabel.Text = ((int)SpeedTestSlider.Value).ToString();
            UpdateSpeedTestPreview();
            SendSpeedTestReport();
        }

        private void SpeedBytePos_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateSpeedTestPreview();
        }

        // Speed tester byte positions: index 0=byte[4], 1=byte[3], 2=byte[5], 3=byte[6], 4=byte[7], 5=byte[8]
        private static readonly byte[] s_speedTesterByteMap = { 4, 3, 5, 6, 7, 8 };

        private void UpdateSpeedTestPreview()
        {
            if (cmbSpeedBytePos.SelectedIndex < 0 || _hidService == null)
                return;

            int selectedIndex = cmbSpeedBytePos.SelectedIndex;
            byte speedValue = (byte)((int)SpeedTestSlider.Value);
            int brightnessLevel = (int)BrightnessSlider.Value;
            int brightnessPercent = (brightnessLevel * 100) / 7;

            byte targetByte = s_speedTesterByteMap[selectedIndex];

            byte[] report = new byte[]
            {
                0x00, 0x08, 0x02, 0x03, 0x05, (byte)brightnessPercent, 0x08, 0x00, 0x00
            };
            report[targetByte] = speedValue;

            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < report.Length; i++)
            {
                if (i > 0) sb.Append(" ");
                sb.Append(report[i].ToString("X2"));
            }
            SpeedTestReportPreview.Text = $"{sb}  (byte[{targetByte}] = {speedValue})";
        }

        private void SendSpeedTestReport()
        {
            if (_hidService == null || !_hidService.IsAvailable)
                return;

            try
            {
                int selectedIndex = cmbSpeedBytePos.SelectedIndex;
                byte speedValue = (byte)((int)SpeedTestSlider.Value);
                int brightnessLevel = (int)BrightnessSlider.Value;
                int brightnessPercent = (brightnessLevel * 100) / 7;

                byte targetByte = s_speedTesterByteMap[selectedIndex];

                byte[] report = new byte[]
                {
                    0x00, 0x08, 0x02, 0x03, 0x05, (byte)brightnessPercent, 0x08, 0x00, 0x00
                };
                report[targetByte] = speedValue;

                _hidService.SendRawReport(report);
                Debug.WriteLine($"[KBD] Speed test byte[{targetByte}]={speedValue}: {string.Join(" ", report.Select(b => b.ToString("X2")))}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[KBD] Speed test error: {ex.Message}");
            }
        }

        #endregion

        private void ApplySettingsToHid()
        {
            if (_hidService is null || !_hidService.IsAvailable) return;

            try
            {
                bool powerOn = tsKeyboardPower.IsChecked == true;

                // Brightness slider is 1-7, convert to 0-100 for HID
                int brightnessLevel = (int)BrightnessSlider.Value;
                int brightnessPercent = (brightnessLevel * 100) / 7;

                byte speed = SliderToHidSpeed((int)SpeedSlider.Value);

                if (powerOn)
                {
                    var color = ColorPicker.SelectedColor;
                    _hidService.TurnOn(color.R, color.G, color.B, brightnessPercent);

                    // Apply effect mode — use saved settings if ComboBox item isn't ready yet
                    KeyboardEffect effect = cmbEffect.SelectedItem is KeyboardEffect e ? e
                        : KeyboardSettingsService.Load().EffectMode;

                    // For multi-color effects, send the color palette first
                    if (s_multiColor7Effects.Contains(effect))
                    {
                        // Marquee displays colors in reverse order on the HID controller
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
                        // 4 WASD/arrow colors + 1 rest color
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

                // Persist settings
                SaveSettings();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[KBD] Keyboard HID error: {ex.Message}");
            }
        }

        private void ApplySettings(KeyboardSettings settings)
        {
            tsKeyboardPower.IsChecked = settings.PowerOn;
            BrightnessSlider.Value = Math.Clamp(settings.Brightness, 1, 7);
            BrightnessValueText.Text = Math.Clamp(settings.Brightness, 1, 7).ToString();
            // settings.Speed stores the HID byte. Slider is reversed (1=slow, 10=fast).
            // Invert: slider = 10 - hidSpeed, clamped to 1-10 range.
            int hidSpeed = Math.Clamp((int)settings.Speed, 1, 10);
            SpeedSlider.Value = 10 - hidSpeed;
            SpeedValueText.Text = (10 - hidSpeed).ToString();

            ColorPicker.SelectedColor = Color.FromRgb(settings.ColorR, settings.ColorG, settings.ColorB);
            cmbEffect.SelectedItem = settings.EffectMode;

            // Restore multi-color palette (default to 7, UpdateEffectUi will adjust count)
            if (!string.IsNullOrEmpty(settings.MultiColors))
            {
                var colors = ParseColorString(settings.MultiColors);
                if (colors.Count > 0)
                    MultiColorPicker.SetColors(colors, 7, suppressEvents: true);
            }

            // Restore idle timer settings
            tsIdleTimer.IsChecked = settings.IdleTimerEnabled;
            // Find the closest matching option index for the saved value
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
        }

        private void SaveSettings()
        {
            var settings = new KeyboardSettings
            {
                PowerOn = tsKeyboardPower.IsChecked == true,
                ColorR = ColorPicker.SelectedColor.R,
                ColorG = ColorPicker.SelectedColor.G,
                ColorB = ColorPicker.SelectedColor.B,
                Brightness = (int)BrightnessSlider.Value,
                EffectMode = (KeyboardEffect)cmbEffect.SelectedItem,
                Speed = SliderToHidSpeed((int)SpeedSlider.Value),
                Direction = 1, // TODO: direction control not yet implemented
                MultiColors = SerializeColors(MultiColorPicker.Colors),
                IdleTimerEnabled = tsIdleTimer.IsChecked == true,
                IdleTimerMinutes = cmbIdleTimer.SelectedIndex >= 0
                    ? s_idleTimerOptions[cmbIdleTimer.SelectedIndex].Seconds / 60
                    : 10,
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
                catch
                {
                    // Skip invalid colors
                }
            }
            return result;
        }

        private void Page_Unloaded(object sender, EventArgs e)
        {
            _hidService?.Dispose();
        }
    }
}
