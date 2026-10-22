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
        private DeviceApplier? _deviceApplier;
        private TrayQuickControlService? _quickControl;
        private bool _isFirstLoad = true;
        private Wpf.Ui.Controls.Snackbar? _adaptiveSnackbar;

        // Debounce timer for color/brightness HID writes
        private readonly DispatcherTimer _applyDebounce = new()
        {
            Interval = TimeSpan.FromMilliseconds(150),
            IsEnabled = true
        };
        private bool _pendingApply;

        // Suppresses UpdateEffectUi/SaveSettings side effects while OnCultureChanged
        // re-populates the effect ComboBox (restoring the selection fires
        // SelectionChanged).
        private bool _refreshingEffects;

        // BetterRGB light bar sync ownership is read live from the streamer
        // (IsLightBarSyncActive — a cheap field read on the running loop + the
        // live parameter snapshot), so the overlay tracks engine start/stop and
        // a SetParams flag flip while this page is open, with no cached state.

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

        /// <summary>
        /// Whether the BetterRGB streamer currently owns the strip: the loop is
        /// running AND the live parameter snapshot carries the sync flag. The streamer
        /// only runs while the BetterRGB engine is active, so this implies the engine
        /// check; a SetParams flip or an engine stop changes it within one frame.
        /// </summary>
        private bool IsBetterRgbSyncActive()
        {
            var streamer = App.GetService<BetterRgbStreamService>();
            return streamer != null && streamer.IsLightBarSyncActive;
        }

        public LightBar()
        {
            InitializeComponent();
            InitializeDebounce();
            PopulateEffects();

            // Subscribe to DeviceApplier events for override management
            _deviceApplier = App.GetService<DeviceApplier>();
            if (_deviceApplier != null)
            {
                _deviceApplier.LightBarOverrideChanged += OnLightBarOverrideChanged;
            }

            // Re-sync the visible page when the mini-tray writes the Light Bar power
            // state, so the power row never shows a stale ON/OFF without re-navigation.
            _quickControl = App.GetService<TrayQuickControlService>();
            if (_quickControl != null)
                _quickControl.LightBarSettingsChanged += OnTrayLightBarSettingsChanged;

            // Effect names are localized at population (see PopulateEffects), so a
            // language switch must re-populate the list.
            LocalizationService.CultureChanged += OnCultureChanged;

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
                // Localize at population: the selected-value box is a ContentPresenter
                // inside the ComboBox template (TemplatedParent set), so
                // LocalizationService skips it — the item strings must already be
                // translated. Selection logic is index-based, so the strings are
                // display-only.
                cbEffect.Items.Add(LocalizationService.Get(name));
            }
            cbEffect.SelectedIndex = 0; // Static
        }

        /// <summary>
        /// Re-populates the effect list with the new culture's names after a language
        /// switch, preserving the selection. The selection churn is guarded by
        /// <see cref="_refreshingEffects"/>.
        /// </summary>
        private void OnCultureChanged(object? sender, EventArgs e)
        {
            _refreshingEffects = true;
            try
            {
                int index = cbEffect.SelectedIndex;
                cbEffect.Items.Clear();
                PopulateEffects();
                cbEffect.SelectedIndex = index >= 0 ? index : 0;
            }
            finally
            {
                _refreshingEffects = false;
            }
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
            // Re-evaluate the override overlay on navigation from both owners (Adaptive
            // Mode and BetterRGB sync). The override event may have fired before this
            // page was in the visual tree, leaving stale UI state.
            // Reset the snackbar transition flag: a navigation hands us a fresh
            // presenter, so the override must re-show once (the debounce tick then
            // won't re-show it again).
            _overrideSnackbarShown = false;
            RefreshOverrideState();

            // Load saved settings from disk
            SyncControlsFromSettings();

            _isFirstLoad = false;

            // Update UI visibility based on power state + selected effect
            ApplyPowerVisibility();

            // Apply saved settings to HID on first load. Suppressed when the auto-power
            // option is enabled and the laptop is currently unplugged — the lights must
            // stay off on battery (MainWindow's auto-power policy owns the suppression)
            // — and while BetterRGB sync owns the strip (the streamer pushes the
            // bottom-row mirror instead; it restores these settings when sync stops).
            bool unpluggedNow = System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus
                == System.Windows.Forms.PowerLineStatus.Offline;
            if (Settings.Default.LightBarEnabled && _hidService != null
                && !(Settings.Default.LightBarAutoPowerOffOnBattery && unpluggedNow)
                && !IsBetterRgbSyncActive())
            {
                _ = Task.Run(() =>
                {
                    try
                    {
                        Thread.Sleep(200);
                        byte r = Settings.Default.LightBarR;
                        byte g = Settings.Default.LightBarG;
                        byte b = Settings.Default.LightBarB;
                        int brightness = Math.Clamp(Settings.Default.LightBarBrightness, 10, 100);
                        byte effect = GetSelectedEffect();
                        // UI speed is 0-11 (slow→fast), HID speed is 0-11 (fast→slow)
                        byte hidSpeed = (byte)(11 - SpeedSlider.Value);

                        _hidService?.LightBarTurnOn(r, g, b, brightness);
                        _hidService?.LightBarSetEffect(effect, hidSpeed, brightness);
                        Debug.WriteLine($"[LB] Page_Loaded apply: enabled=true, color=({r},{g},{b}), brightness={brightness}, effect=0x{effect:X2}, hidSpeed={hidSpeed}");
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
            if (_quickControl != null)
                _quickControl.LightBarSettingsChanged -= OnTrayLightBarSettingsChanged;
        }

        /// <summary>
        /// Pulls the persisted Light Bar state (Fluent Settings) into the page controls.
        /// Shared by Page_Loaded and the mini-tray re-sync so a tray toggle updates a
        /// visible page without re-navigation.
        /// </summary>
        private void SyncControlsFromSettings()
        {
            bool enabled = Settings.Default.LightBarEnabled;
            byte r = Settings.Default.LightBarR;
            byte g = Settings.Default.LightBarG;
            byte b = Settings.Default.LightBarB;
            int brightness = Settings.Default.LightBarBrightness;
            int effectIndex = Settings.Default.LightBarEffectIndex;
            int speed = Settings.Default.LightBarSpeed;
            bool autoPower = Settings.Default.LightBarAutoPowerOffOnBattery;

            tsLightBarPower.IsChecked = enabled;
            tsAutoPowerUnplug.IsChecked = autoPower;
            ColorPicker.SelectedColor = Color.FromRgb(r, g, b);
            // UI floor is 10 — a stale 0 from older versions would throw on the slider.
            BrightnessSlider.Value = Math.Clamp(brightness, 10, 100);
            BrightnessValueText.Text = Math.Clamp(brightness, 10, 100).ToString();
            cbEffect.SelectedIndex = Math.Clamp(effectIndex, 0, Effects.Length - 1);
            SpeedSlider.Value = Math.Clamp(speed, 0, 11);
            SpeedValueText.Text = Math.Clamp(speed, 0, 11).ToString();
        }

        /// <summary>
        /// The mini-tray wrote the Light Bar power state while this page is visible:
        /// re-sync the controls (and the power-off hiding) so the page never shows a
        /// stale ON/OFF. Dispatcher-wrapped: the tray save may come from a non-render
        /// thread. Skipped before the first Page_Loaded so half-built controls stay untouched.
        /// </summary>
        private void OnTrayLightBarSettingsChanged(object? sender, EventArgs e)
        {
            if (_isFirstLoad)
                return;
            Application.Current.Dispatcher.Invoke((Action)(() =>
            {
                SyncControlsFromSettings();
                ApplyPowerVisibility();
            }));
        }

        private void InitializeDebounce()
        {
            _applyDebounce.Tick += (s, e) =>
            {
                // Keep the override overlay live while the page is open: the streamer
                // can start/stop and the sync flag can flip via SetParams without an
                // event on this page — IsLightBarSyncActive is a cheap field read.
                RefreshOverrideState();
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
                    if (IsBetterRgbSyncActive())
                    {
                        Debug.WriteLine($"[LB] Power toggled (saved only): isOn={isOn}, sync owns strip");
                        return;
                    }
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
            ApplyPowerVisibility();
        }

        /// <summary>
        /// Power-off collapses the page to just the power row: auto-power, color,
        /// brightness, effect and speed cards are hidden while the light bar is off.
        /// Restoring re-runs UpdateEffectUi so the speed/color cards follow the
        /// selected effect again.
        /// </summary>
        private void ApplyPowerVisibility()
        {
            bool on = tsLightBarPower.IsChecked == true;
            AutoPowerCard.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            BrightnessCard.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            EffectCard.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            if (on)
                UpdateEffectUi();
            else
            {
                SpeedCard.Visibility = Visibility.Collapsed;
                ColorCard.Visibility = Visibility.Collapsed;
            }
        }

        /// <summary>
        /// "Auto off when unplugged" toggle. Persists the option and immediately applies
        /// the effective lighting state for the current plug state: battery + enabled →
        /// lights off; disabled or plugged in → the user's saved power intent.
        /// </summary>
        private void TsAutoPowerUnplug_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isFirstLoad || _hidService == null)
                return;

            bool autoOn = tsAutoPowerUnplug.IsChecked == true;
            bool pluggedIn = System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus
                != System.Windows.Forms.PowerLineStatus.Offline;
            bool effectiveOn = autoOn && !pluggedIn ? false : tsLightBarPower.IsChecked == true;

            var color = ColorPicker.SelectedColor;
            int brightness = (int)BrightnessSlider.Value;
            byte effect = GetSelectedEffect();
            byte hidSpeed = (byte)(11 - SpeedSlider.Value);

            _ = Task.Run(() =>
            {
                try
                {
                    Thread.Sleep(50);
                    if (IsBetterRgbSyncActive())
                    {
                        Debug.WriteLine($"[LB] Auto-power toggled (saved only): effectiveOn={effectiveOn}, sync owns strip");
                        return;
                    }
                    if (effectiveOn)
                    {
                        _hidService?.LightBarTurnOn(color.R, color.G, color.B, brightness);
                        _hidService?.LightBarSetEffect(effect, hidSpeed, brightness);
                    }
                    else
                    {
                        _hidService?.LightBarTurnOff();
                    }
                    Debug.WriteLine($"[LB] Auto-power toggled: autoOn={autoOn}, pluggedIn={pluggedIn}, effectiveOn={effectiveOn}");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[LB] Auto-power toggle failed: {ex.Message}");
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
            if (_isFirstLoad || _refreshingEffects)
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
                    if (IsBetterRgbSyncActive())
                        return; // sync owns the strip; the saved edit lands on disk only
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
            Settings.Default.LightBarAutoPowerOffOnBattery = tsAutoPowerUnplug.IsChecked == true;
            var color = ColorPicker.SelectedColor;
            Settings.Default.LightBarR = color.R;
            Settings.Default.LightBarG = color.G;
            Settings.Default.LightBarB = color.B;
            Settings.Default.LightBarBrightness = (int)BrightnessSlider.Value;
            Settings.Default.LightBarEffectIndex = cbEffect.SelectedIndex;
            Settings.Default.LightBarSpeed = (int)SpeedSlider.Value;
            Settings.Default.Save();
        }

        // Snackbar transition tracking: the snackbar is shown only when the page
        // TRANSITIONS into an overridden state (or the owning source changes), not
        // on every RefreshOverrideState call — the debounce tick re-evaluates 6-7x/
        // s, and re-showing a fresh Snackbar each tick is the visible seizure.
        private bool _overrideSnackbarShown;
        private bool _overrideSnackbarAdaptive;

        private void OnLightBarOverrideChanged(object? sender, bool isOverridden)
        {
            // Recompute from both owners rather than trusting the event flag alone:
            // BetterRGB sync may own the strip when this (adaptive) event fires.
            RefreshOverrideState();
        }

        /// <summary>
        /// Re-evaluates the override overlay from both owners: Adaptive Mode
        /// (DeviceApplier) and BetterRGB light bar sync (streamer running with the
        /// sync flag). The two are mutually exclusive \u2014 an adaptive override
        /// suspends the streamer \u2014 so at most one snackbar message applies.
        /// </summary>
        private void RefreshOverrideState()
        {
            bool adaptive = _deviceApplier != null && _deviceApplier.IsLightBarOverridden;
            bool sync = IsBetterRgbSyncActive();
            bool overridden = adaptive || sync;

            overlayAdaptiveWarning.Visibility = overridden ? Visibility.Visible : Visibility.Collapsed;

            if (overridden)
            {
                LightBarAvailable.IsEnabled = false;
                // Show only on a transition into override, or when the owning source
                // changes (adaptive <-> sync) — not on every tick re-evaluation.
                if (!_overrideSnackbarShown || _overrideSnackbarAdaptive != adaptive)
                    ShowOverrideSnackbar(adaptive);
            }
            else
            {
                LightBarAvailable.IsEnabled = true;
                HideAdaptiveSnackbar();
            }
        }

        private void ShowOverrideSnackbar(bool adaptive)
        {
            if (_adaptiveSnackbar != null)
                SnackbarPresenter.HideCurrent();

            _adaptiveSnackbar = new Wpf.Ui.Controls.Snackbar(SnackbarPresenter)
            {
                Title = adaptive ? "Adaptive Mode Override" : "BetterRGB Light Bar Sync",
                Content = adaptive
                    ? "Adaptive Mode is currently controlling the light bar. Controls on this page are currently disabled."
                    : "BetterRGB is driving the light bar in sync with the keyboard. Controls on this page are disabled while the engine streams; your saved settings are restored when it stops.",
                Appearance = Wpf.Ui.Controls.ControlAppearance.Info,
                Icon = new Wpf.Ui.Controls.FontIcon { FontFamily = new FontFamily("Segoe Fluent Icons"), Glyph = "\uF167", FontSize = 16, Margin = new Thickness(0, 0, 10, 0) },
                IsCloseButtonEnabled = false,
                Timeout = TimeSpan.FromHours(1),
            };

            if (SnackbarPresenter.IsLoaded)
                _adaptiveSnackbar.Show(true);
            else
                SnackbarPresenter.Loaded += (s, e) => _adaptiveSnackbar?.Show(true);

            _overrideSnackbarShown = true;
            _overrideSnackbarAdaptive = adaptive;
        }

        private void HideAdaptiveSnackbar()
        {
            SnackbarPresenter.HideCurrent();
            _adaptiveSnackbar = null;
            _overrideSnackbarShown = false;
        }
    }
}
