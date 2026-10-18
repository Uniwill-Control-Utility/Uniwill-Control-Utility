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
using Uniwill_Control_Utility.Models;
using Uniwill_Control_Utility.Services;
using Uniwill_Control_Utility.Views.Controls;

namespace Uniwill_Control_Utility.Views.Pages
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
        private bool _pageSavingSettings; // page's own Save — suppress the external re-sync
        private KeyboardDirection _currentDirection = KeyboardDirection.LeftRight;

        // Static flag to prevent re-applying HID settings on every page navigation.
        // The MainWindow's AsyncApplyKeyboardOnStart (from mainWindow_Loaded) handles
        // the initial startup apply with delay and retries for HID device discovery.
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

        // FPS presets for the BetterRGB streamer engine.

        /// <summary>
        /// Returns the streamer engine when it is the live consumer of the keyboard
        /// HID (engine persisted as BetterRGB AND the loop is running) — every HID
        /// apply path routes through it instead of sending reports itself, so only
        /// one consumer ever touches the endpoint.
        /// </summary>
        private BetterRgbStreamService? GetRunningStreamer()
        {
            var streamer = App.GetService<BetterRgbStreamService>();
            if (streamer == null || !streamer.IsRunning)
                return null;
            var disk = KeyboardSettingsService.Load();
            return disk.Engine == KeyboardEngine.BetterRGB ? streamer : null;
        }

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

            // BetterRGB streamer FPS presets (SelectedIndex 2 = 60, OpenRevo's default).

            // Subscribe to override state changes so the UI updates when Adaptive
            // Mode activates (which may happen after Page_Loaded at startup).
            var deviceApplier = App.GetService<DeviceApplier>();
            if (deviceApplier != null)
                deviceApplier.KeyboardOverrideChanged += OnKeyboardOverrideChanged;

            // Re-sync the power row when settings change externally (mini-tray
            // Keyboard RGB toggle, Adaptive apply) so the visible page never shows
            // a stale ON/OFF without re-navigation.
            KeyboardSettingsService.SettingsChanged += OnKeyboardSettingsChangedExternally;
 
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

                            var settings = KeyboardSettingsService.Load();
                            PopulateEffects(settings.Engine);
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
            // Re-assert the BetterRGB discrete-button highlights on every navigation: the
            // constructor's async ApplySettings can race with the CardExpander content
            // instantiation, so the startup pass may have skipped them (null-guarded).
            UpdateFpsButtons();
            UpdateWaveAngleButtons();
            UpdateRcDurationButtons();
            UpdateMtStyleButtons();
            UpdateStLayoutButtons();
            UpdateBetterRgbStatus();
            _isFirstLoad = false;

            // Re-sync the UI controls from disk on EVERY navigation: the page instance
            // persists across navigation (the constructor's disk sync runs only once),
            // so a tray/Adaptive change made while this page was hidden would otherwise
            // show stale ON/OFF until the SettingsChanged event or a later navigation
            // catches up. The override block below re-syncs from the preset when active.
            SyncUiFromSettings();

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
            // Also skip on re-navigation: the MainWindow's AsyncApplyKeyboardOnStart
            // already applied settings at app launch, and the user hasn't changed anything.
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

                        // BetterRGB: start the streamer instead of firmware applies
                        // (idempotent — MainWindow's startup path may have started it).
                        var diskSettings = KeyboardSettingsService.Load();

                        // Auto-power policy: on battery with the option enabled, the
                        // lights must stay off — suppress the page-load apply instead
                        // of turning them on (stop the streamer / firmware off path).
                        bool autoSuppressed = diskSettings.AutoPowerOffOnBattery
                            && System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus
                                == System.Windows.Forms.PowerLineStatus.Offline;
                        if (autoSuppressed)
                        {
                            var autoStreamer = App.GetService<BetterRgbStreamService>();
                            if (autoStreamer != null && diskSettings.Engine == KeyboardEngine.BetterRGB)
                            {
                                autoStreamer.StopEngine("auto-power-battery");
                                // StopEngine alone leaves the last streamed frame lit —
                                // pair it with a one-shot TurnOff (as RequestIdleOff does).
                                _hidService?.TurnOff();
                            }
                            else
                                _hidService?.TurnOff();
                            Debug.WriteLine("[KBD] Page_Loaded apply suppressed: auto-power on battery");
                            return;
                        }

                        if (diskSettings.Engine == KeyboardEngine.BetterRGB)
                        {
                            var streamer = App.GetService<BetterRgbStreamService>();
                            if (streamer != null)
                            {
                                streamer.SetParams(diskSettings);
                                streamer.StartEngine("page-load");
                            }
                            return;
                        }

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
            KeyboardSettingsService.SettingsChanged -= OnKeyboardSettingsChangedExternally;
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

        private static void WalkAndHideChevron(DependencyObject parent)
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is Wpf.Ui.Controls.SymbolIcon symbolIcon)
                {
                    symbolIcon.Visibility = Visibility.Collapsed;
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
            // Power + mode visibility (the power row is the single off switch).
            ApplyPowerVisibility();

            // The BetterRGB engine is a global-effects feature: hidden while Per-Key
            // RGB owns the keyboard-zone consumer.
            tsBetterRgbEngine.Visibility = isPerKey ? Visibility.Collapsed : Visibility.Visible;
            tsBetterRgbEngine.IsEnabled = !isPerKey;
            UpdateStreamerRowsVisibility();
            UpdateSpeedSliderRange();
            if (isPerKey)
                _statusText.Text = "Select keys to edit their color.";
            else
                UpdateBetterRgbStatus();

            if (_isFirstLoad)
                return;

            Debug.WriteLine($"[KBD] Mode toggle: isPerKey={isPerKey}");
            // Auto-apply the active mode when switching
            if (isPerKey)
            {
                // One live consumer rule: the streamer owns the keyboard-zone HID
                // handle while running — stop it before the per-key apply opens its
                // own. The engine is also hidden/disabled in per-key mode.
                App.GetService<BetterRgbStreamService>()?.StopEngine("mode-switch-per-key");
                // Persist the mode switch FIRST: ApplyPerKeyColorsToHid routes on the
                // SAVED engine, and until this save the disk still says BetterRGB —
                // which sent the per-key apply back into the streamer instead of the
                // firmware per-key path.
                SaveSettings();
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

                        // BetterRGB: no firmware power cycle needed — the renderer
                        // switches effects on its own timeline.
                        var diskEngine = KeyboardSettingsService.Load();
                        if (diskEngine.Engine == KeyboardEngine.BetterRGB)
                        {
                            var streamer = App.GetService<BetterRgbStreamService>();
                            if (streamer != null)
                            {
                                streamer.SetParams(diskEngine);
                                streamer.StartEngine("mode-switch");
                            }
                            return;
                        }

                        // Full power cycle: turn off completely, wait, then turn on.
                        // This ensures per-key color cache is cleared and the effect
                        // engine is reinitialized (similar to cold startup).
                        _hidService.TurnOff();
                        Thread.Sleep(100);

                        bool powerOn = Application.Current.Dispatcher.Invoke(
                            () => tsKeyboardPower.IsChecked ?? false);

                        if (powerOn)
                        {
                            int brightnessLevel = (int)Application.Current.Dispatcher.Invoke(
                                () => BrightnessSlider.Value);
                            int brightnessPercent = (brightnessLevel * 100) / 7;

                            var color = Application.Current.Dispatcher.Invoke(
                                () => ColorPicker.SelectedColor);

                            KeyboardEffect effect = Application.Current.Dispatcher.Invoke(
                                () => cmbEffect.SelectedItem is KeyboardEffect e ? e : KeyboardEffect.Static);

                            byte speed = SliderToHidSpeed(defaultEffectSpeed);

                            // Combine power-on and effect into one sequence to avoid
                            // a visible flash of intermediate Static color.
                            bool touchReactive = Application.Current.Dispatcher.Invoke(
                                () => tsTouchReactive.IsChecked ?? false);

                            if (effect == KeyboardEffect.Rainbow)
                            {
                                // Rainbow: use software-driven per-key gradient.
                                // The ITE firmware's built-in Rainbow (0x05) leaves the
                                // leftmost columns dark due to a hardcoded LED layout mismatch.
                                _hidService.SendRainbowEffect(brightnessPercent);
                            }
                            else
                            {
                                _hidService.TurnOnWithEffect(
                                    color.R, color.G, color.B, brightnessPercent,
                                    effect, speed, _currentDirection, touchReactive);

                                // Multi-color palette (if needed for this effect)
                                if (s_multiColor7Effects.Contains(effect))
                                {
                                    var colors = effect == KeyboardEffect.Marquee
                                        ? Application.Current.Dispatcher.Invoke(
                                            () => MultiColorPicker.GetColors().AsEnumerable().Reverse().ToList())
                                        : Application.Current.Dispatcher.Invoke(
                                            () => MultiColorPicker.GetColors().ToList());
                                    _hidService.SetMultiColor(colors);
                                }
                                else if (s_multiColor4Effects.Contains(effect))
                                {
                                    var colors = Application.Current.Dispatcher.Invoke(
                                        () => MultiColorPicker.GetColors().ToList());
                                    _hidService.SetMultiColor(colors.Take(4));
                                }
                                else
                                {
                                    // Non-multi-color effects still need the firmware color
                                    // palette reset after per-key mode to clear corrupted data.
                                    var colors = Application.Current.Dispatcher.Invoke(
                                        () => MultiColorPicker.GetColors().ToList());
                                    _hidService.SetMultiColor(colors);
                                }
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

        private void EngineToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isFirstLoad || !_isInitialized) return;

            SaveSettings();

            // The effect list depends on the engine — refresh it on the toggle.
            PopulateEffects(tsBetterRgbEngine.IsChecked == true
                ? KeyboardEngine.BetterRGB : KeyboardEngine.Hardware);
            PopulateBetterRgbEffects();
            UpdateBetterRgbEffectUi();

            var streamer = App.GetService<BetterRgbStreamService>();
            if (tsBetterRgbEngine.IsChecked == true)
            {
                // Engine on: start the streamer (idempotent if already running).
                streamer?.SetParams(KeyboardSettingsService.Load());
                streamer?.StartEngine("user");
            }
            else
            {
                // Engine off: stop the loop, then let the debounce re-apply the
                // current settings through the firmware path (the persisted engine
                // is Hardware now, so ApplySettingsToHid is no longer routed).
                streamer?.StopEngine("user");
                _pendingApply = true;
            }

            UpdateStreamerRowsVisibility();
            UpdateSpeedSliderRange();
            UpdateBetterRgbStatus();
        }

        /// <summary>
        /// Mirrors the streamer engine state in the bottom bar's status slot
        /// (OpenRevo's "total rendered frames" diagnostic). Only while Per-Key
        /// RGB is off — per-key mode owns the bottom bar content then.
        /// </summary>
        private void UpdateBetterRgbStatus()
        {
            if (ModeToggle.IsChecked == true)
                return;

            var streamer = App.GetService<BetterRgbStreamService>();
            if (streamer == null)
            {
                _statusText.Text = "Hardware MCU";
                return;
            }
            if (streamer.IsUnsupported)
            {
                _statusText.Text = "Streamer unsupported (hardware fallback active)";
                return;
            }
            if (streamer.IsRunning)
                _statusText.Text = $"Streamer: running @{streamer.EffectiveFps} FPS — {streamer.TotalRenderedFrames:N0} frames";
            else
                _statusText.Text = "Hardware MCU";
        }

        /// <summary>
        /// The streamer FPS and brightness rows are only meaningful while the
        /// engine card is shown and the engine is on. While the engine IS on, the
        /// firmware Brightness row (1-7 levels) is hidden and disabled — the
        /// streamer's own 0-100 Brightness row replaces it.
        /// </summary>
        /// <summary>
        /// The two Effects cards are mutually exclusive: the firmware card while the
        /// streamer engine is off, the BetterRGB card while it is on; both hidden in
        /// per-key mode. The streamer's 0-100 brightness row lives in the BetterRGB
        /// card, the firmware 1-7 Brightness row in the hardware card.
        /// </summary>
        private void UpdateStreamerRowsVisibility()
        {
            bool visible = ModeToggle.IsChecked != true && tsBetterRgbEngine.IsChecked == true;
            EffectsCardExpander.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
            BetterRgbEffectsCardExpander.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

            BrightnessPercentRow.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            FpsRow.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            FpsSeparator.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            // Light Bar Sync is a dedicated top-level row now (below the engine toggle).
            LightBarSyncCard.Visibility = visible && tsKeyboardPower.IsChecked == true
                ? Visibility.Visible : Visibility.Collapsed;

            BrightnessRow.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
            BrightnessSlider.IsEnabled = !visible;
            // The divider below the firmware Brightness row follows the effect rule
            // (Rainbow has no controls below it) once the row is back on screen.
            if (visible)
                BrightnessSeparator.Visibility = Visibility.Collapsed;
            else
                BrightnessSeparator.Visibility = cmbEffect.SelectedItem is KeyboardEffect.Rainbow
                    ? Visibility.Collapsed : Visibility.Visible;
        }

        private bool BetterRgbActive() => tsBetterRgbEngine.IsChecked == true && ModeToggle.IsChecked != true;

        #region BetterRGB effects card (separate control surface)

        /// <summary>Discrete BetterRGB choices tracked in fields (buttons have no
        /// value to read back): reactive duration ms, matrix style index, static
        /// layout index.</summary>
        private int _brDuration = 20;
        private int _brStyle;
        private int _brLayout;
        private int _brAngle = 180; // BetterRGB Wave angle, degrees (0-315, 45-degree steps; default = left-to-right)
        private int _brFps = 30; // BetterRGB target stream rate (15/30/45/60)

        private void PopulateBetterRgbEffects()
        {
            cmbBetterRgbEffect.ItemsSource = Enum.GetValues<BetterRgbEffect>();
        }

        private void CmbBetterRgbEffect_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isFirstLoad || !_isInitialized) return;
            UpdateBetterRgbEffectUi();
            if (!_isSyncingFromPreset)
            {
                SaveSettings();
                _pendingApply = true;
            }
        }

        /// <summary>Shows only the selected BetterRGB effect's control rows
        /// (mirrors UpdateEffectUi for the separate BetterRGB card).</summary>
        private void UpdateBetterRgbEffectUi()
        {
            // CardExpander content instantiates lazily; toggle handlers can fire
            // mid-instantiation while sibling controls are still null.
            if (BcColorRow == null || RpWidthRow == null || CuWidthRow == null)
                return;

            var m = cmbBetterRgbEffect.SelectedItem is BetterRgbEffect e ? e : BetterRgbEffect.Breathing;
            bool sel(BetterRgbEffect x) => m == x;

            // Each divider sits above its row and shows only when that row does (the
            // first row of every effect has no divider - BetterRgbEffectSeparator
            // already separates the FPS row from the selected effect's block).
            // Each divider sits above its row and shows only when that row does (the
            // first row of every effect has no divider - BetterRgbEffectSeparator
            // already separates the FPS row from the selected effect's block).
            // Random color replaces the fixed color: with the toggle ON, the color
            // row (and the block separator above it — the next row brings its own)
            // is hidden.
            // The toggles live inside the CardExpander body — null until expanded.
            bool bcRandomOn = tsBcRandom?.IsChecked == true;
            bool rcRandomOn = tsRcRandom?.IsChecked == true;
            bool rpRandomOn = tsRpRandom?.IsChecked == true;

            BcColorRow.Visibility = sel(BetterRgbEffect.Breathing) && !bcRandomOn ? Visibility.Visible : Visibility.Collapsed;
            BcSpeedRow.Visibility = sel(BetterRgbEffect.Breathing) ? Visibility.Visible : Visibility.Collapsed;
            BcSpeedSeparator.Visibility = BcSpeedRow.Visibility;
            BcSmoothRow.Visibility = sel(BetterRgbEffect.Breathing) ? Visibility.Visible : Visibility.Collapsed;
            BcSmoothSeparator.Visibility = BcSmoothRow.Visibility;
            BcRandomRow.Visibility = sel(BetterRgbEffect.Breathing) ? Visibility.Visible : Visibility.Collapsed;
            BcRandomSeparator.Visibility = BcRandomRow.Visibility;

            WvAngleRow.Visibility = sel(BetterRgbEffect.Wave) ? Visibility.Visible : Visibility.Collapsed;
            WvSpeedRow.Visibility = sel(BetterRgbEffect.Wave) ? Visibility.Visible : Visibility.Collapsed;
            WvSpeedSeparator.Visibility = WvSpeedRow.Visibility;

            SpSpeedRow.Visibility = sel(BetterRgbEffect.Sparkle) ? Visibility.Visible : Visibility.Collapsed;
            SpDensityRow.Visibility = sel(BetterRgbEffect.Sparkle) ? Visibility.Visible : Visibility.Collapsed;
            SpDensitySeparator.Visibility = SpDensityRow.Visibility;

            RcColorRow.Visibility = sel(BetterRgbEffect.Reactive) && !rcRandomOn ? Visibility.Visible : Visibility.Collapsed;
            RcRandomRow.Visibility = sel(BetterRgbEffect.Reactive) ? Visibility.Visible : Visibility.Collapsed;
            RcRandomSeparator.Visibility = RcRandomRow.Visibility;
            RcDurationRow.Visibility = sel(BetterRgbEffect.Reactive) ? Visibility.Visible : Visibility.Collapsed;
            RcDurationSeparator.Visibility = RcDurationRow.Visibility;

            WhReverseRow.Visibility = sel(BetterRgbEffect.Wheel) ? Visibility.Visible : Visibility.Collapsed;
            WhSpeedRow.Visibility = sel(BetterRgbEffect.Wheel) ? Visibility.Visible : Visibility.Collapsed;
            WhSpeedSeparator.Visibility = WhSpeedRow.Visibility;

            LtSpeedRow.Visibility = sel(BetterRgbEffect.Lightning) ? Visibility.Visible : Visibility.Collapsed;
            LtSmoothRow.Visibility = sel(BetterRgbEffect.Lightning) ? Visibility.Visible : Visibility.Collapsed;
            LtSmoothSeparator.Visibility = LtSmoothRow.Visibility;
            LtWidthRow.Visibility = sel(BetterRgbEffect.Lightning) ? Visibility.Visible : Visibility.Collapsed;
            LtWidthSeparator.Visibility = LtWidthRow.Visibility;
            LtConcurrentRow.Visibility = sel(BetterRgbEffect.Lightning) ? Visibility.Visible : Visibility.Collapsed;
            LtConcurrentSeparator.Visibility = LtConcurrentRow.Visibility;

            FlSpeedRow.Visibility = sel(BetterRgbEffect.Flame) ? Visibility.Visible : Visibility.Collapsed;
            FlSmoothRow.Visibility = sel(BetterRgbEffect.Flame) ? Visibility.Visible : Visibility.Collapsed;
            FlSmoothSeparator.Visibility = FlSmoothRow.Visibility;

            RnSpeedRow.Visibility = sel(BetterRgbEffect.Raindrop) ? Visibility.Visible : Visibility.Collapsed;
            RnDensityRow.Visibility = sel(BetterRgbEffect.Raindrop) ? Visibility.Visible : Visibility.Collapsed;
            RnDensitySeparator.Visibility = RnDensityRow.Visibility;
            RnColorsRow.Visibility = sel(BetterRgbEffect.Raindrop) ? Visibility.Visible : Visibility.Collapsed;
            RnColorsSeparator.Visibility = RnColorsRow.Visibility;

            MtStyleRow.Visibility = sel(BetterRgbEffect.Matrix) ? Visibility.Visible : Visibility.Collapsed;
            MtSpeedRow.Visibility = sel(BetterRgbEffect.Matrix) ? Visibility.Visible : Visibility.Collapsed;
            MtSpeedSeparator.Visibility = MtSpeedRow.Visibility;
            MtDensityRow.Visibility = sel(BetterRgbEffect.Matrix) ? Visibility.Visible : Visibility.Collapsed;
            MtDensitySeparator.Visibility = MtDensityRow.Visibility;
            MtColorsRow.Visibility = sel(BetterRgbEffect.Matrix) ? Visibility.Visible : Visibility.Collapsed;
            MtColorsSeparator.Visibility = MtColorsRow.Visibility;

            StLayoutRow.Visibility = sel(BetterRgbEffect.Static) ? Visibility.Visible : Visibility.Collapsed;
            StZonesRow.Visibility = sel(BetterRgbEffect.Static) ? Visibility.Visible : Visibility.Collapsed;
            StZonesSeparator.Visibility = StZonesRow.Visibility;
            StColorsRow.Visibility = sel(BetterRgbEffect.Static) ? Visibility.Visible : Visibility.Collapsed;
            StColorsSeparator.Visibility = StColorsRow.Visibility;

            RpColorRow.Visibility = sel(BetterRgbEffect.Ripple) && !rpRandomOn ? Visibility.Visible : Visibility.Collapsed;
            RpRandomRow.Visibility = sel(BetterRgbEffect.Ripple) ? Visibility.Visible : Visibility.Collapsed;
            RpRandomSeparator.Visibility = RpRandomRow.Visibility;
            RpSpeedRow.Visibility = sel(BetterRgbEffect.Ripple) ? Visibility.Visible : Visibility.Collapsed;
            RpSpeedSeparator.Visibility = RpSpeedRow.Visibility;
            RpWidthRow.Visibility = sel(BetterRgbEffect.Ripple) ? Visibility.Visible : Visibility.Collapsed;
            RpWidthSeparator.Visibility = RpWidthRow.Visibility;

            RbSpeedRow.Visibility = sel(BetterRgbEffect.Rainbow) ? Visibility.Visible : Visibility.Collapsed;

            QsColorsRow.Visibility = sel(BetterRgbEffect.Quicksand) ? Visibility.Visible : Visibility.Collapsed;
            QsSpeedRow.Visibility = sel(BetterRgbEffect.Quicksand) ? Visibility.Visible : Visibility.Collapsed;
            QsSpeedSeparator.Visibility = QsSpeedRow.Visibility;
            QsScaleRow.Visibility = sel(BetterRgbEffect.Quicksand) ? Visibility.Visible : Visibility.Collapsed;
            QsScaleSeparator.Visibility = QsScaleRow.Visibility;

            CuColorsRow.Visibility = sel(BetterRgbEffect.Current) ? Visibility.Visible : Visibility.Collapsed;
            CuSpeedRow.Visibility = sel(BetterRgbEffect.Current) ? Visibility.Visible : Visibility.Collapsed;
            CuSpeedSeparator.Visibility = CuSpeedRow.Visibility;
            CuWidthRow.Visibility = sel(BetterRgbEffect.Current) ? Visibility.Visible : Visibility.Collapsed;
            CuWidthSeparator.Visibility = CuWidthRow.Visibility;

            // Hidden when the selected effect's first row (its color row) is hidden —
            // the next row in the block brings its own separator.
            bool firstRowHidden = (sel(BetterRgbEffect.Breathing) && bcRandomOn)
                || (sel(BetterRgbEffect.Reactive) && rcRandomOn)
                || (sel(BetterRgbEffect.Ripple) && rpRandomOn);
            BetterRgbEffectSeparator.Visibility = BetterRgbActive() && !firstRowHidden ? Visibility.Visible : Visibility.Collapsed;
        }

        private void MarkBetterRgbChanged()
        {
            if (_isFirstLoad) return;
            if (!_isSyncingFromPreset)
            {
                SaveSettings();
                _pendingApply = true;
            }
        }

        private void RcDurationButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isFirstLoad) return;
            if (sender is System.Windows.Controls.Button btn && btn.Tag is string tag)
                _brDuration = tag switch
                {
                    "Short" => 10,
                    "Long" => 45,
                    _ => 20,
                };
            UpdateRcDurationButtons();
            MarkBetterRgbChanged();
        }

        private void MtStyleButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isFirstLoad) return;
            if (sender is System.Windows.Controls.Button btn && btn.Tag is string tag)
                _brStyle = tag switch
                {
                    "Columns" => 1,
                    "Lasers" => 2,
                    "Rings" => 3,
                    _ => 0,
                };
            UpdateMtStyleButtons();
            MarkBetterRgbChanged();
        }

        private void StLayoutButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isFirstLoad) return;
            if (sender is System.Windows.Controls.Button btn && btn.Tag is string tag)
                _brLayout = tag == "Vertical" ? 1 : 0;
            UpdateStLayoutButtons();
            MarkBetterRgbChanged();
        }

        // ---- BetterRGB card control handlers (each refreshes its value label,
        // then marks the settings dirty) ------------------------------------------
        private void FpsButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isFirstLoad) return;
            if (sender is System.Windows.Controls.Button btn && btn.Tag is string tag && tag is not null)
                _brFps = int.Parse(tag);
            UpdateFpsButtons();
            MarkBetterRgbChanged();
        }

        /// <summary>Highlights the FPS button matching the current target rate.
        /// Guarded: the BetterRGB card lives inside a CardExpander whose content is
        /// instantiated lazily, so these buttons can still be null mid-instantiation.</summary>
        private void UpdateFpsButtons()
        {
            if (btnFps15 == null)
                return;
            bool isActive(int f) => _brFps == f;

            SetDirStyle(btnFps15, isActive(15));
            SetDirStyle(btnFps30, isActive(30));
            SetDirStyle(btnFps45, isActive(45));
            SetDirStyle(btnFps60, isActive(60));
        }

        /// <summary>Light bar sync toggle: routes through the debounce so the
        /// streamer's parameter snapshot picks the flag up live (SetParams).</summary>
        private void TsBetterRgbLightBarSync_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isFirstLoad) return;
            MarkBetterRgbChanged();
        }

        private void BcSpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            BcSpeedValueText.Text = ((int)BcSpeedSlider.Value).ToString();
            MarkBetterRgbChanged();
        }

        private void BcSmoothSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            BcSmoothValueText.Text = ((int)BcSmoothSlider.Value).ToString();
            MarkBetterRgbChanged();
        }

        private void WvAngleButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isFirstLoad) return;
            if (sender is System.Windows.Controls.Button btn && btn.Tag is string tag && tag is not null)
                _brAngle = int.Parse(tag);
            UpdateWaveAngleButtons();
            MarkBetterRgbChanged();
        }

        /// <summary>Highlights the direction button matching the current Wave angle.</summary>
        private void UpdateWaveAngleButtons()
        {
            if (btnWvA0 == null)
                return;
            bool isActive(int a) => _brAngle == a;

            SetDirStyle(btnWvA0, isActive(0));
            SetDirStyle(btnWvA45, isActive(45));
            SetDirStyle(btnWvA90, isActive(90));
            SetDirStyle(btnWvA135, isActive(135));
            SetDirStyle(btnWvA180, isActive(180));
            SetDirStyle(btnWvA225, isActive(225));
            SetDirStyle(btnWvA270, isActive(270));
            SetDirStyle(btnWvA315, isActive(315));
        }

        /// <summary>Highlights the Reactive fade-duration button matching the current
        /// duration (mirrors the Adaptive page's UpdateKbBrDurationButtons).</summary>
        private void UpdateRcDurationButtons()
        {
            if (btnRcShort == null)
                return;
            SetDirStyle(btnRcShort, _brDuration == 10);
            SetDirStyle(btnRcMedium, _brDuration == 20);
            SetDirStyle(btnRcLong, _brDuration == 45);
        }

        /// <summary>Highlights the Matrix style button matching the current style
        /// (mirrors the Adaptive page's UpdateKbBrStyleButtons).</summary>
        private void UpdateMtStyleButtons()
        {
            if (btnMtDiagonal == null)
                return;
            SetDirStyle(btnMtDiagonal, _brStyle == 0);
            SetDirStyle(btnMtColumns, _brStyle == 1);
            SetDirStyle(btnMtLasers, _brStyle == 2);
            SetDirStyle(btnMtRings, _brStyle == 3);
        }

        /// <summary>Highlights the Static layout button matching the current layout
        /// (mirrors the Adaptive page's UpdateKbBrLayoutButtons).</summary>
        private void UpdateStLayoutButtons()
        {
            if (btnStHorizontal == null)
                return;
            SetDirStyle(btnStHorizontal, _brLayout == 0);
            SetDirStyle(btnStVertical, _brLayout == 1);
        }

        private void WvSpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            WvSpeedValueText.Text = ((int)WvSpeedSlider.Value).ToString();
            MarkBetterRgbChanged();
        }

        private void SpSpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            SpSpeedValueText.Text = ((int)SpSpeedSlider.Value).ToString();
            MarkBetterRgbChanged();
        }

        private void SpDensitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            SpDensityValueText.Text = ((int)SpDensitySlider.Value).ToString();
            MarkBetterRgbChanged();
        }

        private void WhSpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            WhSpeedValueText.Text = ((int)WhSpeedSlider.Value).ToString();
            MarkBetterRgbChanged();
        }

        private void LtSpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            LtSpeedValueText.Text = ((int)LtSpeedSlider.Value).ToString();
            MarkBetterRgbChanged();
        }

        private void LtSmoothSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            LtSmoothValueText.Text = ((int)LtSmoothSlider.Value).ToString();
            MarkBetterRgbChanged();
        }

        private void LtWidthSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            LtWidthValueText.Text = ((int)LtWidthSlider.Value).ToString();
            MarkBetterRgbChanged();
        }

        private void LtConcurrentSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            LtConcurrentValueText.Text = ((int)LtConcurrentSlider.Value).ToString();
            MarkBetterRgbChanged();
        }

        private void FlSpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            FlSpeedValueText.Text = ((int)FlSpeedSlider.Value).ToString();
            MarkBetterRgbChanged();
        }

        private void FlSmoothSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            FlSmoothValueText.Text = ((int)FlSmoothSlider.Value).ToString();
            MarkBetterRgbChanged();
        }

        private void RnSpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            RnSpeedValueText.Text = ((int)RnSpeedSlider.Value).ToString();
            MarkBetterRgbChanged();
        }

        private void RnDensitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            RnDensityValueText.Text = ((int)RnDensitySlider.Value).ToString();
            MarkBetterRgbChanged();
        }

        private void MtSpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            MtSpeedValueText.Text = ((int)MtSpeedSlider.Value).ToString();
            MarkBetterRgbChanged();
        }

        private void MtDensitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            MtDensityValueText.Text = ((int)MtDensitySlider.Value).ToString();
            MarkBetterRgbChanged();
        }

        private void StZonesSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            StZonesValueText.Text = ((int)StZonesSlider.Value).ToString();
            MarkBetterRgbChanged();
        }

        private void RpSpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            RpSpeedValueText.Text = ((int)RpSpeedSlider.Value).ToString();
            MarkBetterRgbChanged();
        }

        private void RpWidthSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            RpWidthValueText.Text = ((int)RpWidthSlider.Value).ToString();
            MarkBetterRgbChanged();
        }

        private void RbSpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            RbSpeedValueText.Text = ((int)RbSpeedSlider.Value).ToString();
            MarkBetterRgbChanged();
        }

        private void QsSpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            QsSpeedValueText.Text = ((int)QsSpeedSlider.Value).ToString();
            MarkBetterRgbChanged();
        }

        private void QsScaleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            QsScaleValueText.Text = ((int)QsScaleSlider.Value).ToString();
            MarkBetterRgbChanged();
        }

        private void CuSpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            CuSpeedValueText.Text = ((int)CuSpeedSlider.Value).ToString();
            MarkBetterRgbChanged();
        }

        private void CuWidthSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            CuWidthValueText.Text = ((int)CuWidthSlider.Value).ToString();
            MarkBetterRgbChanged();
        }

        private void TsBcRandom_Toggled(object sender, RoutedEventArgs e)
        { UpdateBetterRgbEffectUi(); MarkBetterRgbChanged(); }
        private void TsRcRandom_Toggled(object sender, RoutedEventArgs e)
        { UpdateBetterRgbEffectUi(); MarkBetterRgbChanged(); }
        private void TsWhReverse_Toggled(object sender, RoutedEventArgs e) => MarkBetterRgbChanged();
        private void TsRpRandom_Toggled(object sender, RoutedEventArgs e)
        { UpdateBetterRgbEffectUi(); MarkBetterRgbChanged(); }

        private void BcColorPicker_ColorChangedDelayed(object sender, EventArgs e) => MarkBetterRgbChanged();
        private void RcColorPicker_ColorChangedDelayed(object sender, EventArgs e) => MarkBetterRgbChanged();
        private void RpColorPicker_ColorChangedDelayed(object sender, EventArgs e) => MarkBetterRgbChanged();

        private void RnPicker_ColorsChanged(object sender, EventArgs e) => MarkBetterRgbChanged();
        private void MtPicker_ColorsChanged(object sender, EventArgs e) => MarkBetterRgbChanged();
        private void StPicker_ColorsChanged(object sender, EventArgs e) => MarkBetterRgbChanged();
        private void QsPicker_ColorsChanged(object sender, EventArgs e) => MarkBetterRgbChanged();
        private void CuPicker_ColorsChanged(object sender, EventArgs e) => MarkBetterRgbChanged();

        #endregion

        /// <summary>
        /// OpenRevo/BetterRGB speed slider ranges are PER EFFECT (mk_slider in
        /// better_rgb.c): breathing 1-40, wave/rainbow/wheel 1-20, everything
        /// else 1-10 — all higher = faster. The UI caps every BetterRGB speed
        /// slider at 5 (the reference maxima are far too fast on the strip).
        /// </summary>
        private static int SpeedRangeMax(KeyboardEffect e) => Math.Min(e switch
        {
            KeyboardEffect.Breathing => 40,
            KeyboardEffect.Wave or KeyboardEffect.Rainbow or KeyboardEffect.Wheel => 20,
            _ => 10,
        }, 5);

        /// <summary>
        /// While the streamer engine is on, the Speed slider adopts the current
        /// effect's C range (higher = faster, like OpenRevo); off, it keeps the
        /// firmware 1-10 convention (1 = fastest).
        /// </summary>
        private void UpdateSpeedSliderRange()
        {
            bool engineOn = BetterRgbActive();
            var effect = cmbEffect.SelectedItem is KeyboardEffect e ? e : KeyboardEffect.Static;
            int max = engineOn ? SpeedRangeMax(effect) : 10;
            SpeedSlider.Minimum = 1;
            SpeedSlider.Maximum = max;
            SpeedSlider.TickFrequency = Math.Max(1.0, max / 10.0); // keep integer ticks when capped
            var v = Math.Clamp((int)SpeedSlider.Value, 1, max);
            SpeedSlider.Value = v;
            SpeedValueText.Text = v.ToString();
            SpeedSubtitle.Text = engineOn
                ? $"Effect speed 1-{max}, higher = faster"
                : "Control the animation speed";
        }

        /// <summary>
        /// Streamer brightness 0-100 — OpenRevo/BetterRGB's g_brightness slider
        /// range (continuous integers), not the firmware 1-7 levels.
        /// </summary>
        private int CurrentBrightnessPercent() => Math.Clamp((int)BrightnessPercentSlider.Value, 3, 100);

        private void BrightnessPercentSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            BrightnessPercentValueText.Text = CurrentBrightnessPercent().ToString();
            if (_isFirstLoad) return;
            if (!_isSyncingFromPreset)
            {
                SaveSettings();
                _pendingApply = true;
            }
            UpdateBetterRgbStatus();
        }

        private void ApplyPerKeyColorsToHid()
        {
            if (_hidService == null || !_hidService.IsAvailable || _settings == null)
                return;

            // BetterRGB: the streamer owns the handle — re-param it with the current
            // settings (the per-key grid is rebuilt from PerKeyColors in the snapshot).
            var streamer = App.GetService<BetterRgbStreamService>();
            if (streamer != null && KeyboardSettingsService.Load().Engine == KeyboardEngine.BetterRGB)
            {
                SaveSettings();
                var snapshot = KeyboardSettingsService.Load();
                streamer.SetParams(snapshot);
                if (snapshot.PowerOn)
                    streamer.StartEngine("per-key-apply");
                return;
            }

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

        /// <summary>
        /// Effects the ITE firmware MCU understands (the hardware engine's list).
        /// </summary>
        internal static readonly HashSet<KeyboardEffect> s_softwareOnlyEffects = new()
        {
            KeyboardEffect.Matrix,
            KeyboardEffect.Lightning,
            KeyboardEffect.Flame,
            KeyboardEffect.Wheel,
        };

        private void PopulateEffects(KeyboardEngine engine)
        {
            // Filter out TouchRipple, TouchAurora, TouchSpark — the touch-reactive
            // toggle covers those variants for Ripple, Aurora, and Spark respectively.
            // Software-only effects (BetterRGB engine) are hidden for Hardware.
            cmbEffect.ItemsSource = Enum.GetValues<KeyboardEffect>()
                .Where(e => e != KeyboardEffect.TouchRipple
                         && e != KeyboardEffect.TouchAurora
                         && e != KeyboardEffect.TouchSpark
                         && (engine == KeyboardEngine.BetterRGB
                             || !s_softwareOnlyEffects.Contains(e)));
        }

        /// <summary>Effects that support a 7-color palette.</summary>
        internal static readonly HashSet<KeyboardEffect> s_multiColor7Effects = new()
        {
            KeyboardEffect.Breathing,
            KeyboardEffect.Wave,
            KeyboardEffect.Random,
            KeyboardEffect.Ripple,
            KeyboardEffect.TouchRipple,
            KeyboardEffect.Marquee,
            KeyboardEffect.Raindrop,
            KeyboardEffect.Aurora,
            KeyboardEffect.TouchAurora,
            KeyboardEffect.TouchSpark,
            KeyboardEffect.Spark,
            KeyboardEffect.Matrix,
        };

        internal static readonly HashSet<KeyboardEffect> s_multiColor4Effects = new()
        {
            KeyboardEffect.GamingMode,
        };

        internal static bool IsMultiColor7Effect(KeyboardEffect effect) => s_multiColor7Effects.Contains(effect);
        internal static bool IsMultiColor4Effect(KeyboardEffect effect) => s_multiColor4Effects.Contains(effect);

        /// <summary>Effects that support a touch-reactive variant (Random, Ripple, Aurora, Spark).</summary>
        private static readonly HashSet<KeyboardEffect> s_touchReactiveEffects = new()
        {
            KeyboardEffect.Random,
            KeyboardEffect.Ripple,
            KeyboardEffect.Aurora,
            KeyboardEffect.Spark,
        };

        private static readonly HashSet<KeyboardEffect> s_animatedEffects = new()
        {
            KeyboardEffect.Breathing,
            KeyboardEffect.Wave,
            KeyboardEffect.Random,
            KeyboardEffect.Ripple,
            KeyboardEffect.TouchRipple,
            KeyboardEffect.Marquee,
            KeyboardEffect.Raindrop,
            KeyboardEffect.Aurora,
            KeyboardEffect.TouchAurora,
            KeyboardEffect.Matrix,
            KeyboardEffect.Lightning,
            KeyboardEffect.Flame,
            KeyboardEffect.Wheel,
            KeyboardEffect.TouchSpark,
            KeyboardEffect.Spark,
        };

        /// <summary>Default slider speed (1-10) used when switching from per-key to effects mode.</summary>
        private const int defaultEffectSpeed = 5;

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
            UpdateSpeedSliderRange();
            if (cmbEffect.SelectedItem is not KeyboardEffect effect)
            {
                BrightnessSeparator.Visibility = Visibility.Collapsed;
                ColorRow.Visibility = Visibility.Collapsed;
                MultiColorRow.Visibility = Visibility.Collapsed;
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
                SpeedSeparator.Visibility = Visibility.Collapsed;
                SpeedRow.Visibility = Visibility.Collapsed;
                DirectionSeparator.Visibility = Visibility.Collapsed;
                DirectionRow.Visibility = Visibility.Collapsed;
                return;
            }

            // Non-Rainbow effects have controls below Brightness — show the divider,
            // except while the BetterRGB engine is on: the firmware Brightness row
            // (and its divider) are hidden then.
            BrightnessSeparator.Visibility = tsBetterRgbEngine.IsChecked == true && ModeToggle.IsChecked != true
                ? Visibility.Collapsed : Visibility.Visible;

            if (s_multiColor7Effects.Contains(effect))
            {
                ColorRow.Visibility = Visibility.Collapsed;
                MultiColorRow.Visibility = Visibility.Visible;
                MultiColorTitle.Text = "Effect Colors";
                MultiColorSubtitle.Text = "7 colors required for this effect";
                MultiColorPicker.SetColors(MultiColorPicker.GetColors(), 7);
            }
            else if (s_multiColor4Effects.Contains(effect))
            {
                ColorRow.Visibility = Visibility.Collapsed;
                MultiColorRow.Visibility = Visibility.Visible;
                MultiColorTitle.Text = "WASD & Arrow Keys";
                MultiColorSubtitle.Text = "4 colors for gaming keys";
                MultiColorPicker.SetColors(MultiColorPicker.GetColors(), 4);
            }
            else
            {
                // Single-color effects (Static, etc.)
                ColorRow.Visibility = Visibility.Visible;
                MultiColorRow.Visibility = Visibility.Collapsed;
            }

            bool showSpeed = s_animatedEffects.Contains(effect);
            SpeedSeparator.Visibility = showSpeed ? Visibility.Visible : Visibility.Collapsed;
            SpeedRow.Visibility = showSpeed ? Visibility.Visible : Visibility.Collapsed;

            bool showTouchReactive = s_touchReactiveEffects.Contains(effect);
            TouchReactiveSeparator.Visibility = showTouchReactive ? Visibility.Visible : Visibility.Collapsed;
            TouchReactiveRow.Visibility = showTouchReactive ? Visibility.Visible : Visibility.Collapsed;

            bool showDirection = effect == KeyboardEffect.Wave;
            DirectionSeparator.Visibility = showDirection ? Visibility.Visible : Visibility.Collapsed;
            DirectionRow.Visibility = showDirection ? Visibility.Visible : Visibility.Collapsed;
        }

        private void TsTouchReactive_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isFirstLoad) return;
            if (!_isSyncingFromPreset)
            {
                SaveSettings();
                _pendingApply = true;
            }
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
            if (btnDirLeftRight == null)
                return;
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
                    {
                        var streamer = App.GetService<BetterRgbStreamService>();
                        if (streamer != null && KeyboardSettingsService.Load().Engine == KeyboardEngine.BetterRGB)
                            streamer.StopEngine("power-off");
                        else
                            _hidService.TurnOff();
                    }
                }
                SaveSettings();
            }
            else
            {
                _pendingApply = true;
            }

            ApplyPowerVisibility();
        }

        /// <summary>
        /// Power-off collapses the whole page to just the power row: the mode/engine
        /// cards, idle timer, auto-power row, effects content, per-key content and the
        /// bottom bar are all hidden while the keyboard is off. Restoring re-lays out
        /// by the current mode. The mini-tray Keyboard RGB button mirrors this same
        /// PowerOn state (TrayQuickControlService writes/saves it on every toggle).
        /// </summary>
        private void ApplyPowerVisibility()
        {
            bool on = tsKeyboardPower.IsChecked == true;
            bool isPerKey = ModeToggle.IsChecked == true;

            ModeToggleCard.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            // The BetterRGB engine (and its Light Bar Sync row) are effects-mode only:
            // per-key RGB owns the keyboard-zone consumer, so hide the whole card —
            // hiding just the toggle left a card with no control on it.
            EngineCard.Visibility = on && !isPerKey ? Visibility.Visible : Visibility.Collapsed;
            IdleTimerCard.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            AutoPowerCard.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

            EffectsContent.Visibility = on && !isPerKey ? Visibility.Visible : Visibility.Collapsed;
            PerKeyContent.Visibility = on && isPerKey ? Visibility.Visible : Visibility.Collapsed;
            PerKeyActionButtons.Visibility = on && isPerKey ? Visibility.Visible : Visibility.Collapsed;
            PerKeyPickerPanel.Visibility = on && isPerKey ? Visibility.Visible : Visibility.Collapsed;
            // The bottom bar exists only for per-key RGB — no streamer-status strip
            // in effects/BetterRGB modes.
            PerKeyBottomBar.Visibility = on && isPerKey ? Visibility.Visible : Visibility.Collapsed;

            // The Light Bar Sync row belongs to the BetterRGB engine only.
            LightBarSyncCard.Visibility = on && !isPerKey && tsBetterRgbEngine.IsChecked == true
                ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>
        /// "Auto off when unplugged" toggle. Persists the option and immediately applies
        /// the effective lighting state for the current plug state: battery + enabled →
        /// lights off; disabled or plugged in → the user's saved power intent. Routing
        /// mirrors TsKeyboardPower_Toggled so per-key/effects/engine paths stay identical.
        /// </summary>
        private void TsAutoPowerUnplug_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isFirstLoad) return;

            bool autoOn = tsAutoPowerUnplug.IsChecked == true;
            bool pluggedIn = System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus
                != System.Windows.Forms.PowerLineStatus.Offline;
            bool effectiveOn = autoOn && !pluggedIn ? false : tsKeyboardPower.IsChecked == true;

            if (ModeToggle.IsChecked == true)
            {
                // Per-key mode: turn on/off without switching to effects (same as power toggle).
                if (_hidService?.IsAvailable == true)
                {
                    if (effectiveOn)
                        ApplyPerKeyColorsToHid();
                    else
                    {
                        var streamer = App.GetService<BetterRgbStreamService>();
                        if (streamer != null && KeyboardSettingsService.Load().Engine == KeyboardEngine.BetterRGB)
                        {
                            streamer.StopEngine("auto-power-battery");
                            // StopEngine alone leaves the last streamed frame lit — pair
                            // it with a one-shot TurnOff (as RequestIdleOff does).
                            _hidService?.TurnOff();
                        }
                        else
                            _hidService.TurnOff();
                    }
                }
            }
            else
            {
                // Effects mode: debounce re-applies with power-on semantics; power-off
                // goes straight to the streamer/firmware off path.
                if (effectiveOn)
                    _pendingApply = true;
                else
                {
                    var streamer = App.GetService<BetterRgbStreamService>();
                    if (streamer != null && KeyboardSettingsService.Load().Engine == KeyboardEngine.BetterRGB)
                    {
                        streamer.StopEngine("auto-power-battery");
                        // StopEngine alone leaves the last streamed frame lit — pair
                        // it with a one-shot TurnOff (as RequestIdleOff does).
                        _hidService?.TurnOff();
                    }
                    else
                        _hidService?.TurnOff();
                }
            }

            SaveSettings();
        }

        private void ColorPicker_ColorChangedDelayed(object sender, EventArgs e)
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
            // BetterRGB: route to the streamer's parameter snapshot instead of
            // firmware reports. Engine-based (not IsRunning-based) so a manual
            // power-off stops the loop and a power-on restarts it.
            var streamer = App.GetService<BetterRgbStreamService>();
            if (streamer != null && KeyboardSettingsService.Load().Engine == KeyboardEngine.BetterRGB)
            {
                SaveSettings();
                var snapshot = KeyboardSettingsService.Load();
                streamer.SetParams(snapshot);
                if (snapshot.PowerOn)
                    streamer.StartEngine("apply"); // no-op when already running
                else
                    streamer.StopEngine("power-off");
                return;
            }

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

                    if (effect == KeyboardEffect.Static)
                    {
                        // Static: use SetColor to update color and brightness without
                        // the off/reset/on cycle that TurnOnWithEffect causes (visible blackout).
                        _hidService.SetColor(color.R, color.G, color.B, brightnessPercent);
                    }
                    else if (effect == KeyboardEffect.Rainbow)
                    {
                        // Rainbow: use software-driven per-key gradient.
                        // The ITE firmware's built-in Rainbow (0x05) leaves the leftmost
                        // columns dark due to a hardcoded LED layout mismatch.
                        _hidService.SendRainbowEffect(brightnessPercent);
                    }
                    else
                    {
                        // For animated and multi-color effects, use TurnOnWithEffect only
                        // when switching effects. For brightness/color changes on the same
                        // effect, use incremental updates to avoid the off/reset blackout.
                        bool touchReactive = tsTouchReactive.IsChecked == true;
                        _hidService.TurnOnWithEffect(
                            color.R, color.G, color.B, brightnessPercent,
                            effect, speed, _currentDirection, touchReactive);
                    }

                    // Multi-color palette (after effect is set).
                    if (s_multiColor7Effects.Contains(effect))
                    {
                        var colors = effect == KeyboardEffect.Marquee
                            ? MultiColorPicker.GetColors().AsEnumerable().Reverse().ToList()
                            : MultiColorPicker.GetColors();
                        _hidService.SetMultiColor(colors);
                    }
                    else if (s_multiColor4Effects.Contains(effect))
                    {
                        _hidService.SetMultiColor(MultiColorPicker.GetColors().Take(4));
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
                for (int i = 0; i < KeyboardHidService.MaxPerKeyZones; i++)
                    colors[i] = (color.R, color.G, color.B);

                // Save to disk first so the color data persists even if the HID
                // send fails (e.g., controller disconnects mid-transfer).
                _settings.SetPerKeyColors(colors);
                _settings.PerKeyMode = true;
                _pageSavingSettings = true;
                try { KeyboardSettingsService.Save(_settings); } finally { _pageSavingSettings = false; }

                // Send all per-key colors in one batch (enters UserMode internally),
                // or re-param the streamer when it owns the endpoint.
                var streamer = GetRunningStreamer();
                if (streamer != null)
                    streamer.SetParams(_settings);
                else
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
                _pageSavingSettings = true;
                try { KeyboardSettingsService.Save(_settings); } finally { _pageSavingSettings = false; }
                Debug.WriteLine($"[KBD-PERKEY] Saved: PerKeyMode={_settings.PerKeyMode}, PerKeyColors length={_settings.PerKeyColors?.Length ?? 0}");

                var streamer = GetRunningStreamer();
                if (streamer != null)
                    streamer.SetParams(_settings);
                else
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
            var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
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
        private static readonly TimeSpan idleCheckInterval = TimeSpan.FromSeconds(1);

        private void InitializeIdleTimer()
        {
            cmbIdleTimer.ItemsSource = s_idleTimerOptions.Select(o => o.Label).ToList();
            cmbIdleTimer.SelectedIndex = 9;

            _idleCheckTimer.Interval = idleCheckInterval;

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
                    var streamer = GetRunningStreamer();
                    if (streamer != null)
                        streamer.RequestIdleOff(); // stops the loop, then turns off
                    else
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
            // The effect list depends on the engine (software-only effects for
            // BetterRGB). Re-populate so preset/engine switches refresh it.
            PopulateEffects(settings.Engine);

            tsKeyboardPower.IsChecked = settings.PowerOn;
            BrightnessSlider.Value = Math.Clamp(settings.Brightness, 1, 7);
            BrightnessValueText.Text = Math.Clamp(settings.Brightness, 1, 7).ToString();
            BrightnessSliderPerKey.Value = Math.Clamp(settings.Brightness, 1, 7);
            BrightnessValueTextPerKey.Text = Math.Clamp(settings.Brightness, 1, 7).ToString();

            if (settings.Engine == KeyboardEngine.BetterRGB && settings.PerKeyMode != true)
            {
                // Streamer path: the slider carries the raw C speed for the saved
                // effect's range (higher = faster).
                var cSpeed = Math.Clamp(settings.BetterRgbSpeed, 1, SpeedRangeMax(settings.EffectMode));
                SpeedSlider.Value = cSpeed;
                SpeedValueText.Text = cSpeed.ToString();
            }
            else
            {
                int hidSpeed = Math.Clamp((int)settings.Speed, 1, 10);
                SpeedSlider.Value = 10 - hidSpeed;
                SpeedValueText.Text = (10 - hidSpeed).ToString();
            }

            ColorPicker.SelectedColor = Color.FromRgb(settings.ColorR, settings.ColorG, settings.ColorB);
            cmbEffect.SelectedItem = settings.EffectMode;
            _currentDirection = settings.Direction;
            UpdateDirectionButtons();

            if (!string.IsNullOrEmpty(settings.MultiColors))
            {
                var colors = ParseColorString(settings.MultiColors);
                if (colors.Count > 0)
                {
                    int count = settings.EffectMode == KeyboardEffect.GamingMode ? 4 : 7;
                    MultiColorPicker.SetColors(colors, count);
                }
            }

            tsIdleTimer.IsChecked = settings.IdleTimerEnabled;
            IdleTimerCard.IsExpanded = settings.IdleTimerEnabled;
            tsTouchReactive.IsChecked = settings.TouchReactive;
            tsAutoPowerUnplug.IsChecked = settings.AutoPowerOffOnBattery;

            // BetterRGB engine + FPS sync from disk.
            tsBetterRgbEngine.IsChecked = settings.Engine == KeyboardEngine.BetterRGB;
            var savedBrightness = Math.Clamp(settings.BrightnessPercent, 3, 100);
            BrightnessPercentSlider.Value = savedBrightness;
            BrightnessPercentValueText.Text = savedBrightness.ToString();

            // BetterRGB card: restore mode, sliders, toggles and palettes.
            PopulateBetterRgbEffects();
            cmbBetterRgbEffect.SelectedItem = settings.BetterRgbMode;
            var savedFps = Math.Clamp(settings.BetterRgbFps, 15, 60);
            _brFps = Math.Clamp((int)Math.Round(savedFps / 15.0) * 15, 15, 60);
            UpdateFpsButtons();
            if (tsBetterRgbLightBarSync != null)
                tsBetterRgbLightBarSync.IsChecked = settings.BetterRgbLightBarSync;
            BcSpeedSlider.Value = Math.Clamp(settings.BetterRgbBreathSpeed, 1, 5);
            BcSpeedValueText.Text = Math.Clamp(settings.BetterRgbBreathSpeed, 1, 5).ToString();
            BcSmoothSlider.Value = Math.Clamp(settings.BetterRgbBreathSmooth, 2, 150);
            BcSmoothValueText.Text = Math.Clamp(settings.BetterRgbBreathSmooth, 2, 150).ToString();
            tsBcRandom.IsChecked = settings.BetterRgbBreathRandom;
            BcColorPicker.SelectedColor = ParseColorOr(settings.BetterRgbBreathColor, Color.FromRgb(0x00, 0x78, 0xFF));
            _brAngle = Math.Clamp(settings.BetterRgbWaveAngle, 0, 315);
            UpdateWaveAngleButtons();
            UpdateRcDurationButtons();
            UpdateMtStyleButtons();
            UpdateStLayoutButtons();
            WvSpeedSlider.Value = Math.Clamp(settings.BetterRgbWaveSpeed, 1, 5);
            WvSpeedValueText.Text = Math.Clamp(settings.BetterRgbWaveSpeed, 1, 5).ToString();
            SpSpeedSlider.Value = Math.Clamp(settings.BetterRgbSparkleSpeed, 1, 5);
            SpSpeedValueText.Text = Math.Clamp(settings.BetterRgbSparkleSpeed, 1, 5).ToString();
            SpDensitySlider.Value = Math.Clamp(settings.BetterRgbSparkleDensity, 3, 20);
            SpDensityValueText.Text = Math.Clamp(settings.BetterRgbSparkleDensity, 3, 20).ToString();
            tsRcRandom.IsChecked = settings.BetterRgbReactiveRandom;
            RcColorPicker.SelectedColor = ParseColorOr(settings.BetterRgbReactiveColor, Color.FromRgb(0xFF, 0xFF, 0xFF));
            _brDuration = Math.Clamp(settings.BetterRgbReactiveDuration, 10, 45);
            WhSpeedSlider.Value = Math.Clamp(settings.BetterRgbWheelSpeed, 1, 5);
            WhSpeedValueText.Text = Math.Clamp(settings.BetterRgbWheelSpeed, 1, 5).ToString();
            tsWhReverse.IsChecked = settings.BetterRgbWheelReverse;
            LtSpeedSlider.Value = Math.Clamp(settings.BetterRgbLightningSpeed, 1, 5);
            LtSpeedValueText.Text = Math.Clamp(settings.BetterRgbLightningSpeed, 1, 5).ToString();
            LtSmoothSlider.Value = Math.Clamp(settings.BetterRgbLightningSmooth, 1, 20);
            LtSmoothValueText.Text = Math.Clamp(settings.BetterRgbLightningSmooth, 1, 20).ToString();
            LtWidthSlider.Value = Math.Clamp(settings.BetterRgbLightningWidth, 1, 4);
            LtWidthValueText.Text = Math.Clamp(settings.BetterRgbLightningWidth, 1, 4).ToString();
            LtConcurrentSlider.Value = Math.Clamp(settings.BetterRgbLightningConcurrent, 1, 4);
            LtConcurrentValueText.Text = Math.Clamp(settings.BetterRgbLightningConcurrent, 1, 4).ToString();
            FlSpeedSlider.Value = Math.Clamp(settings.BetterRgbFlameSpeed, 1, 5);
            FlSpeedValueText.Text = Math.Clamp(settings.BetterRgbFlameSpeed, 1, 5).ToString();
            FlSmoothSlider.Value = Math.Clamp(settings.BetterRgbFlameSmooth, 1, 10);
            FlSmoothValueText.Text = Math.Clamp(settings.BetterRgbFlameSmooth, 1, 10).ToString();
            RnSpeedSlider.Value = Math.Clamp(settings.BetterRgbRainSpeed, 1, 5);
            RnSpeedValueText.Text = Math.Clamp(settings.BetterRgbRainSpeed, 1, 5).ToString();
            RnDensitySlider.Value = Math.Clamp(settings.BetterRgbRainDensity, 1, 5);
            RnDensityValueText.Text = Math.Clamp(settings.BetterRgbRainDensity, 1, 5).ToString();
            RnPicker.SetColors(ParseColorString(settings.BetterRgbRainColors));
            MtSpeedSlider.Value = Math.Clamp(settings.BetterRgbMatrixSpeed, 1, 5);
            MtSpeedValueText.Text = Math.Clamp(settings.BetterRgbMatrixSpeed, 1, 5).ToString();
            _brStyle = Math.Clamp(settings.BetterRgbMatrixStyle, 0, 3);
            MtDensitySlider.Value = Math.Clamp(settings.BetterRgbMatrixDensity, 1, 5);
            MtDensityValueText.Text = Math.Clamp(settings.BetterRgbMatrixDensity, 1, 5).ToString();
            MtPicker.SetColors(ParseColorString(settings.BetterRgbMatrixColors));
            _brLayout = Math.Clamp(settings.BetterRgbStaticLayout, 0, 1);
            StZonesSlider.Value = Math.Clamp(settings.BetterRgbStaticZones, 1, 7);
            StZonesValueText.Text = Math.Clamp(settings.BetterRgbStaticZones, 1, 7).ToString();
            StPicker.SetColors(ParseColorString(settings.BetterRgbStaticZoneColors), 7);
            RpSpeedSlider.Value = Math.Clamp(settings.BetterRgbRippleSpeed, 1, 5);
            RpSpeedValueText.Text = Math.Clamp(settings.BetterRgbRippleSpeed, 1, 5).ToString();
            RpWidthSlider.Value = Math.Clamp(settings.BetterRgbRippleWidth, 1, 4);
            RpWidthValueText.Text = Math.Clamp(settings.BetterRgbRippleWidth, 1, 4).ToString();
            tsRpRandom.IsChecked = settings.BetterRgbRippleRandom;
            RpColorPicker.SelectedColor = ParseColorOr(settings.BetterRgbRippleColor, Color.FromRgb(0x00, 0xB4, 0xFF));
            RbSpeedSlider.Value = Math.Clamp(settings.BetterRgbRainbowSpeed, 1, 5);
            RbSpeedValueText.Text = Math.Clamp(settings.BetterRgbRainbowSpeed, 1, 5).ToString();
            QsSpeedSlider.Value = Math.Clamp(settings.BetterRgbQuicksandSpeed, 1, 5);
            QsSpeedValueText.Text = Math.Clamp(settings.BetterRgbQuicksandSpeed, 1, 5).ToString();
            QsScaleSlider.Value = Math.Clamp(settings.BetterRgbQuicksandScale, 1, 10);
            QsScaleValueText.Text = Math.Clamp(settings.BetterRgbQuicksandScale, 1, 10).ToString();
            QsPicker.SetColors(ParseColorString(settings.BetterRgbQuicksandColors));
            CuSpeedSlider.Value = Math.Clamp(settings.BetterRgbCurrentSpeed, 1, 5);
            CuSpeedValueText.Text = Math.Clamp(settings.BetterRgbCurrentSpeed, 1, 5).ToString();
            CuWidthSlider.Value = Math.Clamp(settings.BetterRgbCurrentWidth, 2, 8);
            CuWidthValueText.Text = Math.Clamp(settings.BetterRgbCurrentWidth, 2, 8).ToString();
            CuPicker.SetColors(ParseColorString(settings.BetterRgbCurrentColors));

            UpdateStreamerRowsVisibility();
            UpdateSpeedSliderRange();
            UpdateBetterRgbEffectUi();

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

            // Sync content visibility to match the saved mode + power state. ModeToggle_Checked
            // won't fire for programmatic IsChecked changes, so we must set the
            // visibility here otherwise the visualizer stays in a collapsed panel.
            ApplyPowerVisibility();

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

            // Update effect UI (visibility of multi-color rows, speed, direction, etc.)
            // based on the loaded effect.
            UpdateEffectUi();

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
                // While the streamer engine is on, the slider carries the raw C
                // speed (saved as BetterRgbSpeed); the firmware Speed byte keeps
                // its last hardware-path value.
                Speed = BetterRgbActive()
                    ? (byte)Math.Clamp((int)(_settings?.Speed ?? 5), 1, 10)
                    : SliderToHidSpeed((int)SpeedSlider.Value),
                Direction = _currentDirection,
                MultiColors = SerializeColors(MultiColorPicker.GetColors()),
                IdleTimerEnabled = tsIdleTimer.IsChecked == true,
                AutoPowerOffOnBattery = tsAutoPowerUnplug.IsChecked == true,
                IdleTimerMinutes = cmbIdleTimer.SelectedIndex >= 0
                    ? s_idleTimerOptions[cmbIdleTimer.SelectedIndex].Seconds / 60
                    : 10,
                TouchReactive = tsTouchReactive.IsChecked == true,
                PerKeyMode = ModeToggle.IsChecked == true,
                PerKeyColors = savedPerKeyColors,
                PerKeyPickerColor = $"{pickerColor.R},{pickerColor.G},{pickerColor.B}",
                Engine = tsBetterRgbEngine.IsChecked == true && ModeToggle.IsChecked != true
                    ? KeyboardEngine.BetterRGB : KeyboardEngine.Hardware,
                TargetFps = _brFps,
                BrightnessPercent = CurrentBrightnessPercent(),
                BetterRgbSpeed = BetterRgbActive()
                    ? Math.Clamp((int)SpeedSlider.Value, 1, SpeedRangeMax((KeyboardEffect)cmbEffect.SelectedItem))
                    : 10,

                // BetterRGB card controls (separate from the firmware card above).
                BetterRgbMode = cmbBetterRgbEffect.SelectedItem is BetterRgbEffect brMode
                    ? brMode : BetterRgbEffect.Breathing,
                BetterRgbFps = _brFps,
                BetterRgbLightBarSync = tsBetterRgbLightBarSync?.IsChecked == true,
                BetterRgbBreathSpeed = Math.Clamp((int)BcSpeedSlider.Value, 1, 5),
                BetterRgbBreathSmooth = Math.Clamp((int)BcSmoothSlider.Value, 2, 150),
                BetterRgbBreathRandom = tsBcRandom.IsChecked == true,
                BetterRgbBreathColor = SerializeColor1(BcColorPicker.SelectedColor),
                BetterRgbWaveAngle = _brAngle,
                BetterRgbWaveSpeed = Math.Clamp((int)WvSpeedSlider.Value, 1, 5),
                BetterRgbSparkleSpeed = Math.Clamp((int)SpSpeedSlider.Value, 1, 5),
                BetterRgbSparkleDensity = Math.Clamp((int)SpDensitySlider.Value, 3, 20),
                BetterRgbReactiveRandom = tsRcRandom.IsChecked == true,
                BetterRgbReactiveColor = SerializeColor1(RcColorPicker.SelectedColor),
                BetterRgbReactiveDuration = _brDuration,
                BetterRgbWheelSpeed = Math.Clamp((int)WhSpeedSlider.Value, 1, 5),
                BetterRgbWheelReverse = tsWhReverse.IsChecked == true,
                BetterRgbLightningSpeed = Math.Clamp((int)LtSpeedSlider.Value, 1, 5),
                BetterRgbLightningSmooth = Math.Clamp((int)LtSmoothSlider.Value, 1, 20),
                BetterRgbLightningWidth = Math.Clamp((int)LtWidthSlider.Value, 1, 4),
                BetterRgbLightningConcurrent = Math.Clamp((int)LtConcurrentSlider.Value, 1, 4),
                BetterRgbFlameSpeed = Math.Clamp((int)FlSpeedSlider.Value, 1, 5),
                BetterRgbFlameSmooth = Math.Clamp((int)FlSmoothSlider.Value, 1, 10),
                BetterRgbRainSpeed = Math.Clamp((int)RnSpeedSlider.Value, 1, 5),
                BetterRgbRainDensity = Math.Clamp((int)RnDensitySlider.Value, 1, 5),
                BetterRgbRainColorCount = Math.Clamp(RnPicker.GetColors().Count, 1, 5),
                BetterRgbRainColors = SerializeColors(RnPicker.GetColors()),
                BetterRgbMatrixSpeed = Math.Clamp((int)MtSpeedSlider.Value, 1, 5),
                BetterRgbMatrixStyle = _brStyle,
                BetterRgbMatrixDensity = Math.Clamp((int)MtDensitySlider.Value, 1, 5),
                BetterRgbMatrixColorCount = Math.Clamp(MtPicker.GetColors().Count, 1, 3),
                BetterRgbMatrixColors = SerializeColors(MtPicker.GetColors()),
                BetterRgbStaticLayout = _brLayout,
                BetterRgbStaticZones = Math.Clamp((int)StZonesSlider.Value, 1, 7),
                BetterRgbStaticZoneColors = SerializeColors(StPicker.GetColors()),
                BetterRgbRippleSpeed = Math.Clamp((int)RpSpeedSlider.Value, 1, 5),
                BetterRgbRippleWidth = Math.Clamp((int)RpWidthSlider.Value, 1, 4),
                BetterRgbRippleRandom = tsRpRandom.IsChecked == true,
                BetterRgbRippleColor = SerializeColor1(RpColorPicker.SelectedColor),
                BetterRgbRainbowSpeed = Math.Clamp((int)RbSpeedSlider.Value, 1, 5),
                BetterRgbQuicksandSpeed = Math.Clamp((int)QsSpeedSlider.Value, 1, 5),
                BetterRgbQuicksandScale = Math.Clamp((int)QsScaleSlider.Value, 1, 10),
                BetterRgbQuicksandColors = SerializeColors(QsPicker.GetColors()),
                BetterRgbCurrentSpeed = Math.Clamp((int)CuSpeedSlider.Value, 1, 5),
                BetterRgbCurrentWidth = Math.Clamp((int)CuWidthSlider.Value, 2, 8),
                BetterRgbCurrentColors = SerializeColors(CuPicker.GetColors()),
            };
            _pageSavingSettings = true;
            try { KeyboardSettingsService.Save(settings); } finally { _pageSavingSettings = false; }
            Debug.WriteLine($"[KBD] SaveSettings: PerKeyMode={settings.PerKeyMode}, PerKeyColors={(string.IsNullOrEmpty(savedPerKeyColors) ? "null" : "saved")}");
        }

        private static string SerializeColor1(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

        private static Color ParseColorOr(string data, Color fallback)
        {
            var colors = ParseColorString(data);
            return colors.Count > 0 ? colors[0] : fallback;
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
                    if (hex.StartsWith('#'))
                        hex = hex.Substring(1);
                    if (hex.Length == 6)
                    {
                        var r = byte.Parse(hex.Substring(0, 2), System.Globalization.NumberStyles.HexNumber);
                        var g = byte.Parse(hex.Substring(2, 2), System.Globalization.NumberStyles.HexNumber);
                        var b = byte.Parse(hex.Substring(4, 2), System.Globalization.NumberStyles.HexNumber);
                        result.Add(Color.FromRgb(r, g, b));
                    }
                }
                catch (FormatException)
                {
                    // Invalid hex color entry skipped
                }
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
        /// KeyboardSettingsService.Save fired from elsewhere (mini-tray Keyboard RGB
        /// toggle, Adaptive apply): full re-sync of the visible page so the power row
        /// and every control reflect the new state without requiring re-navigation.
        /// Guarded on _isInitialized so pre-init saves don't touch half-built controls;
        /// Dispatcher-wrapped because the save may come from a non-render thread.
        /// </summary>
        private void OnKeyboardSettingsChangedExternally(object? sender, EventArgs e)
        {
            if (!_isInitialized || _pageSavingSettings)
                return;
            Application.Current.Dispatcher.Invoke((Action)(() => SyncUiFromSettings()));
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
            if (e.BetterRgbMode)
            {
                SyncBetterRgbUiFromPreset(e);
                return;
            }

            // Set mode toggle
            ModeToggle.IsChecked = e.PerKeyMode;

            // Power + mode visibility (the power row is the single off switch).
            tsKeyboardPower.IsChecked = KeyboardSettingsService.Load().PowerOn;
            ApplyPowerVisibility();

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
                        int count = e.EffectMode == "GamingMode" ? 4 : 7;
                        MultiColorPicker.SetColors(colors, count);
                    }
                }

                // Speed
                SpeedSlider.Value = HidSpeedToSlider(e.EffectSpeed);
                SpeedValueText.Text = SpeedSlider.Value.ToString();

                // Direction
                _currentDirection = e.Direction;
                UpdateDirectionButtons();
            }
        }

        /// <summary>
        /// Syncs the BetterRGB card (and engine toggle) from a BetterRGB preset
        /// applied by Adaptive Mode.
        /// </summary>
        private void SyncBetterRgbUiFromPreset(KeyboardPresetAppliedEventArgs e)
        {
            tsBetterRgbEngine.IsChecked = true;

            PopulateBetterRgbEffects();
            cmbBetterRgbEffect.SelectedItem = e.BetterRgbEffectMode;

            var color = ParseColorOr(e.BetterRgbColor, Color.FromRgb(0xFF, 0xFF, 0xFF));
            var colors = ParseColorString(e.BetterRgbMultiColors);
            switch (e.BetterRgbEffectMode)
            {
                case BetterRgbEffect.Breathing: BcColorPicker.SelectedColor = color; break;
                case BetterRgbEffect.Reactive: RcColorPicker.SelectedColor = color; break;
                case BetterRgbEffect.Ripple: RpColorPicker.SelectedColor = color; break;
                case BetterRgbEffect.Raindrop: if (colors.Count > 0) RnPicker.SetColors(colors); break;
                case BetterRgbEffect.Matrix: if (colors.Count > 0) MtPicker.SetColors(colors); break;
                case BetterRgbEffect.Quicksand: if (colors.Count > 0) QsPicker.SetColors(colors); break;
                case BetterRgbEffect.Current: if (colors.Count > 0) CuPicker.SetColors(colors); break;
            }

            _brFps = Math.Clamp((int)Math.Round(Math.Clamp(e.BetterRgbFps, 15, 60) / 15.0) * 15, 15, 60);
            UpdateFpsButtons();

            var brightness = Math.Clamp(e.BetterRgbBrightness, 2, 100);
            BrightnessPercentSlider.Value = brightness;
            BrightnessPercentValueText.Text = brightness.ToString();

            UpdateBetterRgbEffectUi();
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
                Appearance = Wpf.Ui.Controls.ControlAppearance.Info,
                Icon = new Wpf.Ui.Controls.FontIcon { FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons"), Glyph = "\uF167", FontSize = 16, Margin = new System.Windows.Thickness(0, 0, 10, 0) },
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
            tsBetterRgbEngine.IsEnabled = enabled;
            IdleTimerCard.IsEnabled = enabled;
            cmbIdleTimer.IsEnabled = enabled;
            // The firmware Brightness row is replaced by the streamer's 0-100 row
            // while the engine is on — keep it disabled in that case.
            BrightnessSlider.IsEnabled = enabled && tsBetterRgbEngine.IsChecked != true;
            BrightnessSliderPerKey.IsEnabled = enabled;
            tsKeyboardPower.IsEnabled = enabled;
            cmbEffect.IsEnabled = enabled;
            ColorPicker.IsEnabled = enabled;
            MultiColorPicker.IsEnabled = enabled;
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
