using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Universal_x86_Tuning_Utility.Models;
using Universal_x86_Tuning_Utility.Services;
using Universal_x86_Tuning_Utility.Views.Controls;

namespace Universal_x86_Tuning_Utility.Views.Pages
{
    /// <summary>
    /// Keyboard backlight control page with effects and per-key RGB modes.
    /// </summary>
    public partial class Keyboard : Page
    {
        private KeyboardHidService? _hidService;
        private KeyboardSettings? _settings;
        private bool _isFirstLoad = true;
        private bool _isInitialized; // true after ApplySettings has synced UI from disk
        private KeyboardDirection _currentDirection = KeyboardDirection.LeftRight;

        // Static flag to prevent re-applying HID settings on every page navigation.
        // The MainWindow's ApplyKeyboardOnStart handles the initial startup apply.
        // This only applies on the very first Page_Loaded after that.
        private static bool s_hasAppliedOnce;

        // Adaptive Mode override
        private Wpf.Ui.Controls.Snackbar? _adaptiveSnackbar;
        private bool _isSyncingFromPreset;

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

        public Keyboard()
        {
            InitializeComponent();
            InitializeDebounce();
            InitializeIdleTimer();

            // Subscribe to override state changes so the UI updates when Adaptive
            // Mode activates (which may happen after Page_Loaded at startup).
            var deviceApplier = App.GetService<DeviceApplier>();
            if (deviceApplier != null)
                deviceApplier.KeyboardOverrideChanged += OnKeyboardOverrideChanged;
 
            // Show unavailable state initially; HID opens in background
            KeyboardAvailable.Visibility = Visibility.Collapsed;
            KeyboardUnavailable.Visibility = Visibility.Visible;

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
                            KeyboardAvailable.Visibility = Visibility.Visible;
                            KeyboardUnavailable.Visibility = Visibility.Collapsed;

                            PopulateEffects();

                            var settings = KeyboardSettingsService.Load();
                            Debug.WriteLine($"[KBD] Loaded settings: PerKeyMode={settings.PerKeyMode}, PerKeyColors={(string.IsNullOrEmpty(settings.PerKeyColors) ? "null" : settings.PerKeyColors.Length + " chars")}");
                            ApplySettings(settings);

                            // HID apply is deferred to Page_Loaded which is the single source
                            // of truth for startup applies. The constructor only opens the
                            // HID handle and syncs UI from settings.
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
                    Debug.WriteLine($"[KBD] HID init failed: {ex.Message}");
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        KeyboardAvailable.Visibility = Visibility.Collapsed;
                        KeyboardUnavailable.Visibility = Visibility.Visible;
                    });
                }
            });
        }

        private void Page_Loaded(object sender, RoutedEventArgs e)
        {
            _visualizer.KeysSelected += OnKeysSelected;
            _visualizer.RefreshLayout();
            UpdateEffectUi();
            UpdateDirectionButtons();
            _isFirstLoad = false;

            // Reconcile DeviceApplier override state with UI.
            // (Event subscription is in the constructor; Page_Loaded just syncs state.)
            var deviceApplier = App.GetService<DeviceApplier>();
            if (deviceApplier != null)
            {
                bool isOverridden = deviceApplier.IsKeyboardOverridden;

                // Always force override state on Page_Loaded. The override event may
                // have fired in the constructor (before the presenter was in the
                // visual tree), leaving a stale snackbar that never appeared.
                // Similarly, _adaptiveSnackbar may be null after Page_Unloaded
                // cleared it, even though override is still active.
                ApplyOverrideState(isOverridden);

                // If override is still active and a preset was applied while the page
                // was not visible, sync UI to match the preset. Skip this when override
                // is lifted — the restored user settings take precedence.
                if (isOverridden && deviceApplier.LastAppliedKeyboardPreset != null)
                {
                    SyncUiFromPreset(deviceApplier.LastAppliedKeyboardPreset);
                }
            }

            // If HID is not available yet (slow startup, or constructor's open raced
            // with Adaptive Mode's HID apply), dispose stale handle and retry.
            bool hidNeedsReopen = _hidService == null || !_hidService.IsAvailable;
            if (hidNeedsReopen)
            {
                _hidService?.Dispose();
                _hidService = null;
            }
            if (_hidService == null)
            {
                bool opened = false;
                for (int attempt = 0; attempt < 10 && !opened; attempt++)
                {
                    if (attempt > 0)
                        Thread.Sleep(500);

                    try
                    {
                        var service = new KeyboardHidService();
                        if (service.Open())
                        {
                            _hidService = service;
                            opened = true;
                        }
                        else
                        {
                            service.Dispose();
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[KBD] Page_Loaded HID open attempt {attempt + 1} failed: {ex.Message}");
                    }
                }

                if (opened)
                {
                    KeyboardAvailable.Visibility = Visibility.Visible;
                    KeyboardUnavailable.Visibility = Visibility.Collapsed;
                }
                else
                {
                    Debug.WriteLine("[KBD] Page_Loaded HID open gave up after retries");
                    return;
                }
            }

            // Apply the current mode to HID on background thread.
            // Skip when Adaptive Mode override is active — the Adaptive Mode update()
            // loop is the authority and will re-apply the preset if we overwrite it.
            // Also skip on re-navigation: the MainWindow's ApplyKeyboardOnStart already
            // applied settings at app launch, and the user hasn't changed anything.
            bool isOverrideActive = deviceApplier?.IsKeyboardOverridden == true;
            if (!isOverrideActive && !s_hasAppliedOnce)
            {
                s_hasAppliedOnce = true;

                // Determine the saved mode. _settings may be null if the constructor's
                // async HID open hasn't called ApplySettings() yet, so fall back to
                // reading from disk directly. This prevents a race where Page_Loaded
                // fires before ApplySettings, leading to isPerKey=false, which would
                // call ApplySettingsToHid() -> SaveSettings() with PerKeyMode=false,
                // overwriting the user's saved per-key mode on disk.
                bool isPerKey = _settings?.PerKeyMode == true;
                if (!isPerKey)
                {
                    var diskSettings = KeyboardSettingsService.Load();
                    isPerKey = diskSettings.PerKeyMode;
                }

                _ = Task.Run(() =>
                {
                    try
                    {
                        // Give the firmware time to settle after HID open.
                        // Without this delay, the direction byte is ignored on startup.
                        Thread.Sleep(200);

                        Debug.WriteLine($"[KBD] Page_Loaded apply: isPerKey={isPerKey}");
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
                        Debug.WriteLine($"[KBD] Page_Loaded apply failed: {ex.Message}");
                    }

                    if (isPerKey)
                    {
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            LoadPerKeyColors();
                            Debug.WriteLine("[KBD] Page_Loaded: per-key colors reloaded after HID apply");
                        });
                    }
                });
            }

            // Always sync visualizer to saved per-key colors on navigation.
            // _settings may be null if ApplySettings() hasn't run yet (async race),
            // so fall back to reading from disk.
            bool visualizerPerKey = _settings?.PerKeyMode == true;
            if (!visualizerPerKey)
            {
                var diskSettings = KeyboardSettingsService.Load();
                visualizerPerKey = diskSettings.PerKeyMode;
            }
            if (visualizerPerKey)
            {
                LoadPerKeyColors();
                Debug.WriteLine("[KBD] Page_Loaded: per-key colors synced to visualizer");
            }
        }

        private void Page_Unloaded(object sender, RoutedEventArgs e)
        {
            _visualizer.KeysSelected -= OnKeysSelected;
            // Keep HID handle open across navigation — disposing it causes
            // the handle to be lost on return navigation, breaking all keyboard control.

            // Clean up Adaptive Mode snackbar so it can re-show on next navigation
            HideAdaptiveSnackbar();
            _adaptiveSnackbar = null;

            // Unsubscribe from override events to prevent memory leaks.
            var deviceApplier = App.GetService<DeviceApplier>();
            if (deviceApplier != null)
                deviceApplier.KeyboardOverrideChanged -= OnKeyboardOverrideChanged;
        }

        #region Idle Timer Chevron Hide

        private void IdleTimerCard_Loaded(object sender, RoutedEventArgs e)
        {
            Application.Current.Dispatcher.BeginInvoke(
                (Action)(() =>
                {
                    var toggle = IdleTimerCard.Template?.FindName("ExpanderToggleButton", IdleTimerCard) as ToggleButton;
                    if (toggle?.Template is not null)
                    {
                        var chevronGrid = toggle.Template.FindName("ChevronGrid", toggle) as FrameworkElement;
                        if (chevronGrid is not null)
                        {
                            chevronGrid.Visibility = Visibility.Collapsed;
                            return;
                        }
                    }

                    WalkAndHideChevron(IdleTimerCard);
                }),
                DispatcherPriority.Loaded);
        }

        private void WalkAndHideChevron(DependencyObject parent)
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is Wpf.Ui.Controls.SymbolIcon)
                {
                    ((FrameworkElement)child).Visibility = Visibility.Collapsed;
                    return;
                }
                WalkAndHideChevron(child);
            }
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

            Debug.WriteLine($"[KBD] Mode toggle: isPerKey={isPerKey}");
            // Auto-apply the active mode when switching
            if (isPerKey)
            {
                LoadPerKeyColors();
                ApplyPerKeyColorsToHid();
            }
            else
            {
                // Switching from Per-Key to Effects: the firmware retains cached
                // per-key color data in UserMode. A full off-then-on cycle is needed
                // to clear the cache and reinitialize the effect engine properly.
                // Must run off the UI thread to avoid freezing during delays.
                _ = Task.Run(() =>
                {
                    try
                    {
                        if (_hidService == null || !_hidService.IsAvailable) return;

                        Thread.Sleep(100);

                        // Full power cycle: turn off completely, wait, then turn on.
                        // This ensures per-key color cache is cleared and the effect
                        // engine is reinitialized (similar to cold startup).
                        _hidService.TurnOff();
                        Thread.Sleep(100);

                        bool powerOn = (bool)Application.Current.Dispatcher.Invoke(
                            () => tsKeyboardPower.IsChecked);

                        if (powerOn)
                        {
                            int brightnessLevel = (int)Application.Current.Dispatcher.Invoke(
                                () => BrightnessSlider.Value);
                            int brightnessPercent = (brightnessLevel * 100) / 7;

                            var color = (System.Windows.Media.Color)Application.Current.Dispatcher.Invoke(
                                () => ColorPicker.SelectedColor);

                            KeyboardEffect effect = (KeyboardEffect)Application.Current.Dispatcher.Invoke(
                                () => cmbEffect.SelectedItem is KeyboardEffect e ? e : KeyboardEffect.Static);

                            byte speed = SliderToHidSpeed(5);

                            // Combine power-on and effect into one sequence to avoid
                            // a visible flash of intermediate Static color.
                            _hidService.TurnOnWithEffect(
                                color.R, color.G, color.B, brightnessPercent,
                                effect, speed, _currentDirection);

                            // Multi-color palette (if needed for this effect)
                            if (s_multiColor7Effects.Contains(effect))
                            {
                                var colors = effect == KeyboardEffect.Marquee
                                    ? Application.Current.Dispatcher.Invoke(
                                        () => MultiColorPicker.Colors.AsEnumerable().Reverse().ToList())
                                    : Application.Current.Dispatcher.Invoke(
                                        () => MultiColorPicker.Colors.ToList());
                                _hidService.SetMultiColor(colors);
                            }
                            else if (s_multiColor4Effects.Contains(effect))
                            {
                                var colors = Application.Current.Dispatcher.Invoke(
                                    () => MultiColorPicker.Colors.ToList());
                                _hidService.SetMultiColor(colors.Take(4));
                            }
                            else if (s_multiColor4Plus1Effects.Contains(effect))
                            {
                                var colors = Application.Current.Dispatcher.Invoke(() =>
                                {
                                    var list = MultiColorPicker.Colors.ToList();
                                    var rest = GamingModeFullRestColor.SelectedColor;
                                    list.AddRange(new[] { rest });
                                    return list;
                                });
                                _hidService.SetMultiColor(colors);
                            }
                            else
                            {
                                // Rainbow and other non-multi-color effects still need the
                                // firmware color palette reset after per-key mode. Sending the
                                // default palette clears corrupted per-key color data that
                                // would otherwise break the effect's internal gradient.
                                var colors = Application.Current.Dispatcher.Invoke(
                                    () => MultiColorPicker.Colors.ToList());
                                _hidService.SetMultiColor(colors);
                            }
                        }

                        Application.Current.Dispatcher.Invoke(() => SaveSettings());
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[KBD] Mode toggle off primer error: {ex.Message}");
                    }
                });
                Debug.WriteLine("[KBD] Mode toggle off: full power cycle + apply queued");
            }

            SaveSettings();
        }

        private void ApplyPerKeyColorsToHid()
        {
            if (_hidService == null || !_hidService.IsAvailable || _settings == null)
                return;

            try
            {
                // Read brightness from the UI control on the UI thread.
                // This method is called from a background Task.Run in Page_Loaded.
                int brightnessPercent = Application.Current.Dispatcher.Invoke(() =>
                {
                    int brightnessLevel = (int)BrightnessSlider.Value;
                    return (brightnessLevel * 100) / 7;
                });
                _hidService.SetPerKeyBrightness(brightnessPercent);

                var colors = _settings.GetPerKeyColors();
                // Enter UserMode directly without a Static primer to avoid
                // a visible flash of a single color before per-key data arrives.
                _hidService.SendAllPerKeyColorsFromDict(colors);
                Debug.WriteLine("[KBD] Applied saved per-key colors to HID");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[KBD] Per-key apply error: {ex.Message}");
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
            KeyboardEffect.TouchRipple,
            KeyboardEffect.Marquee,
            KeyboardEffect.Raindrop,
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

        internal static bool IsMultiColor7Effect(KeyboardEffect effect) => s_multiColor7Effects.Contains(effect);
        internal static bool IsMultiColor4Effect(KeyboardEffect effect) => s_multiColor4Effects.Contains(effect);
        internal static bool IsMultiColor4Plus1Effect(KeyboardEffect effect) => s_multiColor4Plus1Effects.Contains(effect);

        private static readonly HashSet<KeyboardEffect> s_animatedEffects = new()
        {
            KeyboardEffect.Breathing,
            KeyboardEffect.Wave,
            KeyboardEffect.Reactive,
            KeyboardEffect.Ripple,
            KeyboardEffect.TouchRipple,
            KeyboardEffect.Marquee,
            KeyboardEffect.Raindrop,
            KeyboardEffect.Aurora,
            KeyboardEffect.TouchAurora,
            KeyboardEffect.TouchSpark,
            KeyboardEffect.Spark,
            KeyboardEffect.Music,
        };

        private void CmbEffect_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Skip during initial setup: _isInitialized guards against PopulateEffects
            // triggering a selection change before ApplySettings has synced UI from disk.
            if (_isFirstLoad || !_isInitialized) return;
            UpdateEffectUi();
            if (!_isSyncingFromPreset)
            {
                SaveSettings();
                _pendingApply = true;
            }
        }

        /// <summary>
        /// Prevents the CardExpander's ToggleButton from intercepting clicks on ComboBoxes
        /// nested inside its header. Uses the bubbling MouseLeftButtonDown so the ComboBox
        /// processes the click first, then we block it from reaching the ToggleButton above.
        /// </summary>
        private void ComboBoxInCardExpander_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
        }

        private void UpdateEffectUi()
        {
            if (cmbEffect.SelectedItem is not KeyboardEffect effect)
            {
                BrightnessSeparator.Visibility = Visibility.Collapsed;
                ColorRow.Visibility = Visibility.Collapsed;
                MultiColorRow.Visibility = Visibility.Collapsed;
                GamingModeFullRestRow.Visibility = Visibility.Collapsed;
                SpeedSeparator.Visibility = Visibility.Collapsed;
                SpeedRow.Visibility = Visibility.Collapsed;
                DirectionSeparator.Visibility = Visibility.Collapsed;
                DirectionRow.Visibility = Visibility.Collapsed;
                return;
            }

            // Rainbow has fixed colors — no color controls, no speed, no direction.
            // Brightness is still available inside the CardExpander but no divider below it.
            if (effect == KeyboardEffect.Rainbow)
            {
                BrightnessSeparator.Visibility = Visibility.Collapsed;
                ColorRow.Visibility = Visibility.Collapsed;
                MultiColorRow.Visibility = Visibility.Collapsed;
                GamingModeFullRestRow.Visibility = Visibility.Collapsed;
                SpeedSeparator.Visibility = Visibility.Collapsed;
                SpeedRow.Visibility = Visibility.Collapsed;
                DirectionSeparator.Visibility = Visibility.Collapsed;
                DirectionRow.Visibility = Visibility.Collapsed;
                return;
            }

            // Non-Rainbow effects have controls below Brightness — show the divider.
            BrightnessSeparator.Visibility = Visibility.Visible;

            if (s_multiColor7Effects.Contains(effect))
            {
                ColorRow.Visibility = Visibility.Collapsed;
                MultiColorRow.Visibility = Visibility.Visible;
                GamingModeFullRestRow.Visibility = Visibility.Collapsed;
                MultiColorTitle.Text = "Effect Colors";
                MultiColorSubtitle.Text = "7 colors required for this effect. Click a swatch to edit.";
                MultiColorPicker.SetColors(MultiColorPicker.Colors, 7);
            }
            else if (s_multiColor4Effects.Contains(effect))
            {
                ColorRow.Visibility = Visibility.Collapsed;
                MultiColorRow.Visibility = Visibility.Visible;
                GamingModeFullRestRow.Visibility = Visibility.Collapsed;
                MultiColorTitle.Text = "WASD &amp; Arrow Keys";
                MultiColorSubtitle.Text = "4 colors for gaming keys. Click a swatch to edit.";
                MultiColorPicker.SetColors(MultiColorPicker.Colors, 4);
            }
            else if (s_multiColor4Plus1Effects.Contains(effect))
            {
                ColorRow.Visibility = Visibility.Collapsed;
                MultiColorRow.Visibility = Visibility.Visible;
                GamingModeFullRestRow.Visibility = Visibility.Visible;
                MultiColorTitle.Text = "WASD &amp; Arrow Keys";
                MultiColorSubtitle.Text = "4 colors for gaming keys. Click a swatch to edit.";
                MultiColorPicker.SetColors(MultiColorPicker.Colors, 4);
            }
            else
            {
                // Single-color effects (Static, etc.)
                ColorRow.Visibility = Visibility.Visible;
                MultiColorRow.Visibility = Visibility.Collapsed;
                GamingModeFullRestRow.Visibility = Visibility.Collapsed;
            }

            bool showSpeed = s_animatedEffects.Contains(effect);
            SpeedSeparator.Visibility = showSpeed ? Visibility.Visible : Visibility.Collapsed;
            SpeedRow.Visibility = showSpeed ? Visibility.Visible : Visibility.Collapsed;
            bool showDirection = effect == KeyboardEffect.Wave;
            DirectionSeparator.Visibility = showDirection ? Visibility.Visible : Visibility.Collapsed;
            DirectionRow.Visibility = showDirection ? Visibility.Visible : Visibility.Collapsed;

            // Show color separator if any control row below Type is visible.
            bool anyControlVisible = ColorRow.Visibility == Visibility.Visible
                || MultiColorRow.Visibility == Visibility.Visible
                || SpeedRow.Visibility == Visibility.Visible;
        }

        private void DirectionButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isFirstLoad) return;
            if (sender is System.Windows.Controls.Button btn && btn.Tag is string tag)
            {
                _currentDirection = tag switch
                {
                    "LeftRight" => KeyboardDirection.LeftRight,
                    "RightLeft" => KeyboardDirection.RightLeft,
                    "DownUp" => KeyboardDirection.DownUp,
                    "UpDown" => KeyboardDirection.UpDown,
                    "DiagonalBottomRightToTopLeft" => KeyboardDirection.DiagonalBottomRightToTopLeft,
                    "DiagonalBottomLeftToTopRight" => KeyboardDirection.DiagonalBottomLeftToTopRight,
                    _ => KeyboardDirection.LeftRight,
                };
                UpdateDirectionButtons();
                if (!_isSyncingFromPreset)
                {
                    SaveSettings();
                    _pendingApply = true;
                }
            }
        }

        /// <summary>
        /// Highlights the button matching the current direction.
        /// </summary>
        private void UpdateDirectionButtons()
        {
            bool isActive(KeyboardDirection dir) => _currentDirection == dir;
            
            SetDirStyle(btnDirLeftRight, isActive(KeyboardDirection.LeftRight));
            SetDirStyle(btnDirRightLeft, isActive(KeyboardDirection.RightLeft));
            SetDirStyle(btnDirDownUp, isActive(KeyboardDirection.DownUp));
            SetDirStyle(btnDirUpDown, isActive(KeyboardDirection.UpDown));
            SetDirStyle(btnDiagBRTL, isActive(KeyboardDirection.DiagonalBottomRightToTopLeft));
            SetDirStyle(btnDiagBLTR, isActive(KeyboardDirection.DiagonalBottomLeftToTopRight));
        }

        private static void SetDirStyle(Wpf.Ui.Controls.Button btn, bool active)
        {
            btn.Appearance = active ? Wpf.Ui.Controls.ControlAppearance.Primary : Wpf.Ui.Controls.ControlAppearance.Transparent;
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
            SaveSettings();
            _pendingApply = true;
        }

        private void GamingModeFullRestColor_ColorChangedDelayed(object sender, EventArgs e)
        {
            if (_isFirstLoad) return;
            SaveSettings();
            _pendingApply = true;
        }

        private void MultiColorPicker_ColorsChanged(object sender, EventArgs e)
        {
            if (_isFirstLoad) return;
            SaveSettings();
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
                if (!_isSyncingFromPreset)
                    _pendingApply = true;
            }
        }

        private void BrightnessSliderPerKey_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isFirstLoad) return;
            BrightnessValueTextPerKey.Text = ((int)BrightnessSliderPerKey.Value).ToString();

            ApplyPerKeyBrightness();
            SaveSettings();
        }

        private void ApplyPerKeyBrightness()
        {
            if (_hidService == null || !_hidService.IsAvailable)
                return;

            try
            {
                int brightnessLevel = (int)BrightnessSliderPerKey.Value;
                int brightnessPercent = (brightnessLevel * 100) / 7;
                _hidService.SetPerKeyBrightness(brightnessPercent);

                // Re-send per-key colors so the brightness takes effect
                if (_settings != null)
                {
                    var colors = _settings.GetPerKeyColors();
                    _hidService.SendAllPerKeyColorsFromDict(colors);
                }

                Debug.WriteLine($"[KBD] Per-key brightness set to {brightnessPercent}%");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[KBD] Per-key brightness error: {ex.Message}");
            }
        }

        private void SpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isFirstLoad) return;
            SpeedValueText.Text = ((int)SpeedSlider.Value).ToString();
            if (!_isSyncingFromPreset)
            {
                SaveSettings();
                _pendingApply = true;
            }
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
                    KeyboardEffect effect = cmbEffect.SelectedItem is KeyboardEffect e ? e
                        : KeyboardSettingsService.Load().EffectMode;

                    // Use fast apply to minimize visible flash. TurnOnWithEffect
                    // combines power-on and effect into one sequence so the
                    // firmware never shows an intermediate static color.
                    _hidService.TurnOnWithEffect(
                        color.R, color.G, color.B, brightnessPercent,
                        effect, speed, _currentDirection);

                    // Multi-color palette (after effect is set).
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
                }
                else
                {
                    _hidService.TurnOff();
                }

                SaveSettings();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[KBD] HID error: {ex.Message}");
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
            // Use _settings if available, otherwise read from disk. This handles
            // the race where Page_Loaded fires before ApplySettings() populates _settings.
            KeyboardSettings? settings = _settings;
            if (settings == null)
            {
                settings = KeyboardSettingsService.Load();
                Debug.WriteLine("[KBD] LoadPerKeyColors: _settings was null, loaded from disk");
            }

            var colors = settings.GetPerKeyColors();
            var mediaColors = new Dictionary<int, Color>();

            foreach (var kvp in colors)
            {
                mediaColors[kvp.Key] = Color.FromRgb(kvp.Value.R, kvp.Value.G, kvp.Value.B);
            }

            // Count non-black zones for diagnostics
            int nonBlack = colors.Count(kvp => kvp.Value.R != 0 || kvp.Value.G != 0 || kvp.Value.B != 0);
            Debug.WriteLine($"[KBD] LoadPerKeyColors: {mediaColors.Count} zones, {nonBlack} non-black");

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
                // Update visualizer first (UI thread).
                for (int i = 0; i < KeyboardHidService.MaxPerKeyZones; i++)
                {
                    _visualizer.SetZoneColor(i, color);
                }

                if (_settings == null)
                    _settings = new KeyboardSettings();

                // Build color dict for HID send.
                var colors = new Dictionary<int, (byte, byte, byte)>();
                for (int i = 0; i < 126; i++)
                    colors[i] = (color.R, color.G, color.B);

                // Save to disk first so the color data persists even if the HID
                // send fails (e.g., controller disconnects mid-transfer).
                _settings.SetPerKeyColors(colors);
                _settings.PerKeyMode = true;
                KeyboardSettingsService.Save(_settings);

                // Send all per-key colors in one batch (enters UserMode internally).
                _hidService.SendAllPerKeyColorsFromDict(colors);

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
                // Save to disk first so the color data persists even if the HID
                // send fails (e.g., controller disconnects mid-transfer).
                _settings.SetPerKeyColors(allColors);
                // Ensure PerKeyMode is in sync with the current toggle state.
                // _settings.PerKeyMode may be stale if the user just toggled to
                // per-key mode (ModeToggle_Checked saves a fresh KeyboardSettings
                // but doesn't update the _settings field instance).
                _settings.PerKeyMode = true;
                KeyboardSettingsService.Save(_settings);
                Debug.WriteLine($"[KBD-PERKEY] Saved: PerKeyMode={_settings.PerKeyMode}, PerKeyColors length={_settings.PerKeyColors?.Length ?? 0}");

                _hidService.SendAllPerKeyColorsFromDict(allColors);

                Debug.WriteLine($"[KBD-PERKEY] Applied {color} to zones: {string.Join(", ", selected)}");
                _statusText.Text = $"Applied {color} to {selected.Count} key{(selected.Count > 1 ? "s" : "")}.";

                // Deselect all keys after applying so the visualizer is clean.
                _visualizer.ClearSelection();
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
            (60, "1 min"), (120, "2 min"), (300, "5 min"),
            (1200, "20 min"), (1800, "30 min"), (3600, "1 h"),
            (7200, "2 h"), (10800, "3 h"),
        };

        /// <summary>
        /// Sets the idle-check poll interval to 1 s.
        /// GetLastInputInfo is a lightweight Win32 call — polling every second
        /// has negligible CPU cost and keeps the turn-off time accurate.
        /// </summary>
        private static readonly TimeSpan IdleCheckInterval = TimeSpan.FromSeconds(1);

        private void InitializeIdleTimer()
        {
            cmbIdleTimer.ItemsSource = s_idleTimerOptions.Select(o => o.Label).ToList();
            cmbIdleTimer.SelectedIndex = 9;

            _idleCheckTimer.Interval = IdleCheckInterval;

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
                    Debug.WriteLine($"[KBD] Idle timer: keyboard turned off after {idleMs / 1000}s");
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
                    if (ModeToggle.IsChecked == true)
                    {
                        // Per-key mode: re-apply per-key colors.
                        // Do NOT call ApplySettingsToHid() — it exits per-key mode.
                        ApplyPerKeyColorsToHid();
                    }
                    else
                    {
                        ApplySettingsToHid();
                    }
                    _idleWakeTimer.IsEnabled = false;
                    _idleCheckTimer.IsEnabled = true;
                    Debug.WriteLine($"[KBD] Idle timer: keyboard re-activated");
                }
            };
        }

        private void TsIdleTimer_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isFirstLoad) return;

            // Toggle ON → expand; toggle OFF → collapse
            IdleTimerCard.IsExpanded = tsIdleTimer.IsChecked == true;

            if (ModeToggle.IsChecked != true)
                _pendingApply = true;
            SaveSettings();
        }

        /// <summary>
        /// If the user clicked the chevron (not the toggle), sync the toggle to match.
        /// </summary>
        private void IdleTimerCard_Expanded(object sender, RoutedEventArgs e)
        {
            if (tsIdleTimer.IsChecked != true)
            {
                tsIdleTimer.IsChecked = true;
                if (ModeToggle.IsChecked != true)
                    _pendingApply = true;
                SaveSettings();
            }
        }

        /// <summary>
        /// If the user clicked the chevron (not the toggle), sync the toggle to match.
        /// </summary>
        private void IdleTimerCard_Collapsed(object sender, RoutedEventArgs e)
        {
            if (tsIdleTimer.IsChecked != false)
            {
                tsIdleTimer.IsChecked = false;
                if (ModeToggle.IsChecked != true)
                    _pendingApply = true;
                SaveSettings();
            }
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
            BrightnessSliderPerKey.Value = Math.Clamp(settings.Brightness, 1, 7);
            BrightnessValueTextPerKey.Text = Math.Clamp(settings.Brightness, 1, 7).ToString();

            int hidSpeed = Math.Clamp((int)settings.Speed, 1, 10);
            SpeedSlider.Value = 10 - hidSpeed;
            SpeedValueText.Text = (10 - hidSpeed).ToString();

            ColorPicker.SelectedColor = Color.FromRgb(settings.ColorR, settings.ColorG, settings.ColorB);
            cmbEffect.SelectedItem = settings.EffectMode;
            _currentDirection = settings.Direction;
            UpdateDirectionButtons();

            if (!string.IsNullOrEmpty(settings.MultiColors))
            {
                var colors = ParseColorString(settings.MultiColors);
                if (colors.Count > 0)
                    MultiColorPicker.SetColors(colors, 7);
            }

            tsIdleTimer.IsChecked = settings.IdleTimerEnabled;
            IdleTimerCard.IsExpanded = settings.IdleTimerEnabled;
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

            // Restore per-key color picker color
            if (!string.IsNullOrEmpty(settings.PerKeyPickerColor))
            {
                var parts = settings.PerKeyPickerColor.Split(',');
                if (parts.Length == 3 && byte.TryParse(parts[0], out var pr)
                    && byte.TryParse(parts[1], out var pg) && byte.TryParse(parts[2], out var pb))
                {
                    _colorPicker.SelectedColor = Color.FromRgb(pr, pg, pb);
                }
            }

            // Sync content visibility to match the saved mode. ModeToggle_Checked
            // won't fire for programmatic IsChecked changes, so we must set the
            // visibility here otherwise the visualizer stays in a collapsed panel.
            EffectsContent.Visibility = settings.PerKeyMode ? Visibility.Collapsed : Visibility.Visible;
            PerKeyContent.Visibility = settings.PerKeyMode ? Visibility.Visible : Visibility.Collapsed;

            // Load per-key colors into the visualizer now. This ensures the UI
            // reflects saved data even if Page_Loaded fires before this method
            // (race between constructor's async HID open and Page_Loaded).
            // Page_Loaded will also call LoadPerKeyColors() but that's harmless.
            if (settings.PerKeyMode)
            {
                LoadPerKeyColors();
            }

            // Sync picker brush so hover/selection overlays use the right color.
            _visualizer.SetPickerBrush(new SolidColorBrush(_colorPicker.SelectedColor));

            // Mark as initialized so that SelectionChanged handlers triggered by
            // PopulateEffects (which runs before this) don't fire after we're done.
            _isInitialized = true;
        }

        private void SaveSettings()
        {
            // Guard: if UI isn't ready yet (e.g., during lifecycle transitions), skip.
            if (cmbEffect.SelectedItem is not KeyboardEffect)
                return;

            // Preserve existing per-key colors from the live _settings instance.
            // Apply_Click updates _settings.PerKeyColors in-place, so this always
            // reads the latest per-key color data.
            var savedPerKeyColors = _settings?.PerKeyColors;
            var pickerColor = _colorPicker.SelectedColor;

            var settings = new KeyboardSettings
            {
                PowerOn = tsKeyboardPower.IsChecked == true,
                ColorR = ColorPicker.SelectedColor.R,
                ColorG = ColorPicker.SelectedColor.G,
                ColorB = ColorPicker.SelectedColor.B,
                Brightness = (int)BrightnessSlider.Value,
                EffectMode = (KeyboardEffect)cmbEffect.SelectedItem,
                Speed = SliderToHidSpeed((int)SpeedSlider.Value),
                Direction = _currentDirection,
                MultiColors = SerializeColors(MultiColorPicker.Colors),
                IdleTimerEnabled = tsIdleTimer.IsChecked == true,
                IdleTimerMinutes = cmbIdleTimer.SelectedIndex >= 0
                    ? s_idleTimerOptions[cmbIdleTimer.SelectedIndex].Seconds / 60
                    : 10,
                PerKeyMode = ModeToggle.IsChecked == true,
                PerKeyColors = savedPerKeyColors,
                PerKeyPickerColor = $"{pickerColor.R},{pickerColor.G},{pickerColor.B}",
            };
            KeyboardSettingsService.Save(settings);
            Debug.WriteLine($"[KBD] SaveSettings: PerKeyMode={settings.PerKeyMode}, PerKeyColors={(string.IsNullOrEmpty(savedPerKeyColors) ? "null" : "saved")}");
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
                        Debug.WriteLine("[KBD] Debounce firing: ApplySettingsToHid");
                        ApplySettingsToHid();
                    }
                }
            };
        }

        #endregion

        #region Adaptive Mode Override

        /// <summary>
        /// Event handler for <see cref="DeviceApplier.KeyboardOverrideChanged"/>.
        /// </summary>
        private void OnKeyboardOverrideChanged(object? sender, bool isOverridden)
        {
            ApplyOverrideState(isOverridden);
        }

        /// <summary>
        /// Applies or lifts the override UI state (snackbar, control enablement).
        /// </summary>
        private void ApplyOverrideState(bool isOverridden)
        {
            overlayAdaptiveWarning.Visibility = isOverridden ? Visibility.Visible : Visibility.Collapsed;

            if (isOverridden)
            {
                SetControlsEnabled(false);
                ShowAdaptiveSnackbar();
            }
            else
            {
                // Override lifted — hide snackbar and overlay.
                HideAdaptiveSnackbar();

                // Re-sync UI from the restored user settings.
                SyncUiFromSettings();
                SetControlsEnabled(true);
            }
        }

        /// <summary>
        /// Re-syncs UI controls from the service's restored settings after override is lifted.
        /// Reloads from disk since DisableKeyboardOverride just persisted the saved settings.
        /// The HID device will be updated when the user navigates to this page (Page_Loaded).
        /// </summary>
        private void SyncUiFromSettings()
        {
            var settings = KeyboardSettingsService.Load();
            _settings = settings;

            _isSyncingFromPreset = true;
            try
            {
                ApplySettings(settings);
            }
            finally
            {
                _isSyncingFromPreset = false;
            }
        }

        /// <summary>
        /// Event handler for <see cref="DeviceApplier.KeyboardPresetApplied"/>.
        /// Syncs the Keyboard page's UI controls to reflect the profile's values.
        /// </summary>
        private void OnKeyboardPresetApplied(object? sender, KeyboardPresetAppliedEventArgs e)
        {
            _isSyncingFromPreset = true;
            try
            {
                SyncUiFromPreset(e);
            }
            finally
            {
                _isSyncingFromPreset = false;
            }
        }

        /// <summary>
        /// Syncs UI controls from a preset applied by Adaptive Mode.
        /// </summary>
        private void SyncUiFromPreset(KeyboardPresetAppliedEventArgs e)
        {
            // Set mode toggle
            ModeToggle.IsChecked = e.PerKeyMode;

            // Update effect UI visibility based on mode
            EffectsContent.Visibility = e.PerKeyMode ? Visibility.Collapsed : Visibility.Visible;
            PerKeyContent.Visibility = e.PerKeyMode ? Visibility.Visible : Visibility.Collapsed;

            // Brightness
            BrightnessSlider.Value = Math.Clamp(e.Brightness, 1, 7);
            BrightnessValueText.Text = e.Brightness.ToString();
            BrightnessSliderPerKey.Value = Math.Clamp(e.Brightness, 1, 7);
            BrightnessValueTextPerKey.Text = e.Brightness.ToString();

            // Idle timer
            tsIdleTimer.IsChecked = e.IdleTimerEnabled;
            IdleTimerCard.IsExpanded = e.IdleTimerEnabled;

            // Find closest idle timer index
            int savedSeconds = e.IdleTimerMinutes * 60;
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

            if (!e.PerKeyMode)
            {
                // Effects mode — sync effect controls
                tsKeyboardPower.IsChecked = true;

                // Find effect index by name
                foreach (KeyboardEffect effect in cmbEffect.Items.Cast<KeyboardEffect>())
                {
                    if (effect.ToString() == e.EffectMode)
                    {
                        cmbEffect.SelectedItem = effect;
                        break;
                    }
                }

                // Color / Multi-colors
                ColorPicker.SelectedColor = Color.FromRgb(e.ColorR, e.ColorG, e.ColorB);

                if (!string.IsNullOrEmpty(e.MultiColors))
                {
                    var colors = ParseColorString(e.MultiColors);
                    if (colors.Count > 0)
                    {
                        // Determine swatch count from effect mode name
                        int count = e.EffectMode == "GamingModeFull" || e.EffectMode == "GamingMode" ? 4 : 7;
                        MultiColorPicker.SetColors(colors, count);
                    }
                }

                // Rest color for GamingModeFull
                GamingModeFullRestColor.SelectedColor = Color.FromRgb(e.RestColorR, e.RestColorG, e.RestColorB);

                // Speed
                SpeedSlider.Value = HidSpeedToSlider(e.EffectSpeed);
                SpeedValueText.Text = SpeedSlider.Value.ToString();

                // Direction
                _currentDirection = e.Direction;
                UpdateDirectionButtons();
            }
        }

        /// <summary>
        /// Converts HID speed value back to slider value.
        /// </summary>
        private static int HidSpeedToSlider(byte hidSpeed)
        {
            // Slider 1-10 maps to HID 11-2 (inverted: slider 1 = fastest = HID 11)
            return Math.Clamp(12 - hidSpeed, 1, 10);
        }

        /// <summary>
        /// Shows the Adaptive Mode override snackbar.
        /// </summary>
        private void ShowAdaptiveSnackbar()
        {
            // Hide existing snackbar before showing a new one
            if (_adaptiveSnackbar != null)
                SnackbarPresenter.HideCurrent();

            _adaptiveSnackbar = new Wpf.Ui.Controls.Snackbar(SnackbarPresenter)
            {
                Title = "Adaptive Mode Override",
                Content = "Adaptive Mode is currently controlling the keyboard RGB. Controls on this page are currently disabled.",
                Appearance = Wpf.Ui.Controls.ControlAppearance.Primary,
                Icon = new Wpf.Ui.Controls.SymbolIcon(Wpf.Ui.Controls.SymbolRegular.Warning24),
                IsCloseButtonEnabled = false,
                Timeout = TimeSpan.FromHours(1), // effectively infinite — dismissed on page Unloaded
            };

            // Defer showing until the presenter is in the visual tree.
            if (SnackbarPresenter.IsLoaded)
                _adaptiveSnackbar.Show(true);
            else
                SnackbarPresenter.Loaded += (s, e) => _adaptiveSnackbar?.Show(true);
        }

        /// <summary>
        /// Hides the Adaptive Mode override snackbar.
        /// </summary>
        private void HideAdaptiveSnackbar()
        {
            SnackbarPresenter.HideCurrent();
            _adaptiveSnackbar = null;
        }

        /// <summary>
        /// Enables or disables all keyboard controls.
        /// </summary>
        private void SetControlsEnabled(bool enabled)
        {
            ModeToggle.IsEnabled = enabled;
            tsIdleTimer.IsEnabled = enabled;
            IdleTimerCard.IsEnabled = enabled;
            cmbIdleTimer.IsEnabled = enabled;
            BrightnessSlider.IsEnabled = enabled;
            BrightnessSliderPerKey.IsEnabled = enabled;
            tsKeyboardPower.IsEnabled = enabled;
            cmbEffect.IsEnabled = enabled;
            ColorPicker.IsEnabled = enabled;
            MultiColorPicker.IsEnabled = enabled;
            GamingModeFullRestColor.IsEnabled = enabled;
            SpeedSlider.IsEnabled = enabled;
            _visualizer.IsEnabled = enabled;
            _selectAllBtn.IsEnabled = enabled;
            _clearSelectBtn.IsEnabled = enabled;
            _fillAllBtn.IsEnabled = enabled;
            _colorPicker.IsEnabled = enabled;
            _applyBtn.IsEnabled = enabled;
        }

        private void _colorPicker_ColorChangedDelayed(object sender, EventArgs e)
        {
            // Push the current picker color to all visualizer zones so hover and
            // selection overlays use the right color.
            _visualizer.SetPickerBrush(new SolidColorBrush(_colorPicker.SelectedColor));
        }

        #endregion
    }
}
