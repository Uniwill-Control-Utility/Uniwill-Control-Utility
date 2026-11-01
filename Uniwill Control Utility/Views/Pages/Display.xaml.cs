using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Serilog;
using System.Windows.Threading;
using Uniwill_Control_Utility.Properties;
using Uniwill_Control_Utility.Scripts.GPUs.NVIDIA;
using Uniwill_Control_Utility.Scripts.Misc;
using Uniwill_Control_Utility.Services;
using Wpf.Ui.Abstractions.Controls;
using UCU.Contracts.Capabilities;
using UCU.Core.Ec;

namespace Uniwill_Control_Utility.Views.Pages
{
    public partial class Display : Page, INavigationAware
    {
        private const int ApplyDelaySeconds = 2;

        // After a user change, skip syncing the combo box from EC for this many
        // milliseconds to avoid reading back the stale value before the EC commits.
        private const int ApplyGraceMs = 10000; // 10 seconds

        // NVRAM GUID for OemMagicVariable (the variable NvramAccess routes; the
        // page keeps only its own offset).
        private const int OffsetOemDisplayMode = 98; // OpenRevo probe confirmed offset 98 (not 99)

        private readonly IGpuMuxCapability? _mux;
        private readonly IEcTelemetryCapability? _telemetry;
        private readonly IEngineCapability? _engine;
        private EcPlatform? _ecPlatform;

        /// <summary>
        /// The platform the engine resolved, fetched once. The NVRAM mirror of the
        /// mux mode stores a raw byte whose encoding is platform-specific, and only
        /// the engine knows the platform (it reads the boot CPU vendor), so the page
        /// asks the engine's identity verb one time and caches the answer: the
        /// processor cannot change during a session.
        /// </summary>
        private EcPlatform EcPlatformForNvram =>
            _ecPlatform ??= _engine!.GetInfoAsync().GetAwaiter().GetResult().Platform;
        private bool _isEcAvailable;
        private bool _isLoaded;
        private bool _isAdministrator;
        private bool _isNvramAvailable;
        private bool _localDimmingToggling;
        private bool _refreshRateBusy;
        private bool _colorCalBusy;

        /// <summary>The last non-imported profile the user selected: the
        /// fallback when the active imported profile is deleted (U-205).</summary>
        private string _lastShippedColorProfile =
            Uniwill_Control_Utility.Scripts.Misc.Display.DefaultColorProfileFile;

        // Delayed refresh timer created after each user change.

        // --- MiniLED Local Dimming ---
        // The WMI handshake (class, instance, command words, expected reply
        // bytes) lives in MiniLedAccess now: the engine's device verb runs it
        // as LocalSystem, the app's WMI code as that route's fallback. The
        // page keeps only the EC readback pair (0x72B bit 0x02), which rides
        // the telemetry verb.
        private const ushort EcLocalDimmingRegister = 0x072B;
        private const byte EcLocalDimmingBit = 0x02;
        private DispatcherTimer? _delayTimer;
        private DispatcherTimer? _cooldownTimer;
        private DispatcherTimer? _statusTimer;

        // Shared across page instances (AddScoped creates a new page on each navigation).
        private static DateTime _lastUserApplyTime;

        // The currently-alive page instance — the GPU power saving engine repaints it
        // live when it applies a mode (e.g. the Automations engine on charger transitions).
        public static Display? Instance;

        // The power saving mode the buttons currently show (gates the live notify overlay).
        private int _displayedPowerSavingMode = 2; // Auto

        // The NVRAM P/Invokes this page used to carry (firmware-variable read/
        // write and the token-privilege adjust) live in NvramAccess now, as
        // that route's local fallback; the page calls the route.

        public Display()
        {
            InitializeComponent();
            Instance = this;

            _mux = App.GetService<IGpuMuxCapability>();
            _telemetry = App.GetService<IEcTelemetryCapability>();
            _engine = App.GetService<IEngineCapability>();
            if (_mux is not null && _engine is not null)
            {
                try
                {
                    _isEcAvailable = _engine.IsHardwareAvailableAsync().GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Display] Failed to initialize EC service: {ex.Message}");
                    _isEcAvailable = false;
                }
            }

            // Check if running as administrator (needed for NVRAM access)
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                _isAdministrator = principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                _isAdministrator = false;
            }

            // NVRAM availability is probed through NvramAccess, not gated on
            // this process being administrator: the engine route answers from
            // its LocalSystem token, so an unelevated app has NVRAM whenever
            // the engine does. (The token-adjust the elevated probe used to do
            // survives inside NvramAccess's local fallback.)
            _isNvramAvailable = NvramAccess.ReadOemMagic() is not null;
            Debug.WriteLine($"[Display] NVRAM available: {_isNvramAvailable}");

            // Show MiniLED card only on MiniLED panels
            CardLocalDimming.Visibility = App.HasMiniLEDPanel ? Visibility.Visible : Visibility.Collapsed;

            // Show the refresh-rate card only when the panel offers more than one rate
            CardRefreshRate.Visibility = AvailableRefreshRates().Count > 1 ? Visibility.Visible : Visibility.Collapsed;

            // Show the color-calibration card when Windows offers more than just Default
            CardColorCalibration.Visibility = OfferedColorProfileFiles().Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <inheritdoc />
        public async Task OnNavigatedToAsync()
        {
            SyncFromEcAndRefresh();

            // Keep the bottom-bar panel-model status live (WMI UserFriendlyName via PanelDatabase).
            _statusTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Normal,
                (s, e) => tbRefreshRateTarget.Text = LaptopModelLabel(), Dispatcher);
            _statusTimer.Start();
        }

        /// <inheritdoc />
        public async Task OnNavigatedFromAsync()
        {
            _delayTimer?.Stop();
            _delayTimer = null;
            _isLoaded = false;
            if (Instance == this)
                Instance = null;
        }

        private void Page_Loaded(object sender, RoutedEventArgs e)
        {
            if (!_isEcAvailable)
            {
                DisplayAvailable.Visibility = Visibility.Collapsed;
                DisplayUnavailable.Visibility = Visibility.Visible;
            }
        }

        private void Page_Unloaded(object sender, RoutedEventArgs e)
        {
            _delayTimer?.Stop();
            _delayTimer = null;
            _cooldownTimer?.Stop();
            borderPowerSavingCooldown.Visibility = Visibility.Collapsed;
            _isLoaded = false;
        }

        /// <summary>
        /// Syncs combo box from NVRAM (the authoritative source), refreshes GPU info panel.
        /// Does NOT auto-reconcile EC from NVRAM — if the EC differs (e.g., BIOS or
        /// another tool changed the mode), the user sees the actual persisted value and
        /// can decide whether to re-apply it.
        /// </summary>
        private void SyncFromEcAndRefresh()
        {
            if (!_isEcAvailable)
            {
                DisplayAvailable.Visibility = Visibility.Collapsed;
                DisplayUnavailable.Visibility = Visibility.Visible;
                return;
            }

            DisplayAvailable.Visibility = Visibility.Visible;
            DisplayUnavailable.Visibility = Visibility.Collapsed;

            // Read NVRAM mode (authoritative source for persisted GPU mode)
            int mode;
            if (_isNvramAvailable)
            {
                try
                {
                    byte? nvramMode = ReadNvramMode();
                    if (nvramMode.HasValue)
                    {
                        mode = nvramMode.Value;
                        // Note: EC register may differ if BIOS or another tool changed
                        // the mode. We show the NVRAM value and let the user decide.
                        try
                        {
                            int ecMode = (int)_mux!.GetModeAsync().GetAwaiter().GetResult();
                            if (ecMode != mode)
                            {
                                Debug.WriteLine($"[Display] EC mode ({ecMode}) differs from NVRAM ({mode}) — showing NVRAM value");
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[Display] Failed to read EC mode: {ex.Message}");
                        }
                    }
                    else
                    {
                        mode = 0; // Default to Hybrid if NVRAM read fails
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Display] NVRAM read failed: {ex.Message}");
                    mode = 0;
                }
            }
            else
            {
                // Fallback: read from EC if NVRAM is unavailable
                try
                {
                    mode = (int)_mux!.GetModeAsync().GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Display] Failed to read MUX mode from EC: {ex.Message}");
                    mode = 0;
                }
            }

            UpdateGpuModeButtons(mode);

            // Show/hide GPU Power Saving card — only available in Hybrid mode
            bool isHybrid = mode == 0;
            CardGpuPowerSaving.Visibility = isHybrid ? Visibility.Visible : Visibility.Collapsed;

            // Load GPU Power Saving mode from settings
            int powerSavingMode = Settings.Default.GpuPowerSavingMode;
            UpdatePowerSavingButtons(powerSavingMode);

            // Load Local Dimming state from settings
            if (App.HasMiniLEDPanel)
            {
                tsLocalDimming.IsChecked = Settings.Default.DisplayLocalDimming;
            }

            // Sync the refresh-rate combo from the live Windows display mode
            UpdateRefreshRateCombo(GetRefreshRateSafe());

            // Sync the color-calibration combo from the live ICM association
            UpdateColorCalibrationCombo(GetColorProfileSafe());

            InitBrightnessRow();

            RefreshAll();
            _isLoaded = true;
        }

        // ---- Brightness + screen off (U-205) --------------------------------

        private bool _brightnessLoading;
        private DispatcherTimer? _brightnessTimer;

        /// <summary>Shows the brightness row only where the panel exposes the
        /// WDDM brightness control, and seeds it from the live value.</summary>
        private void InitBrightnessRow()
        {
            if (!Uniwill_Control_Utility.Scripts.Misc.Display.TryGetBrightness(out int percent))
            {
                CardBrightness.Visibility = Visibility.Collapsed;
                return;
            }

            CardBrightness.Visibility = Visibility.Visible;

            _brightnessLoading = true;
            try
            {
                slBrightness.Value = percent;
                tbBrightness.Text = percent + "%";
            }
            finally
            {
                _brightnessLoading = false;
            }
        }

        /// <summary>Debounced apply: a slider drag must not issue one WMI write
        /// per pixel of travel.</summary>
        private void slBrightness_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            tbBrightness.Text = (int)Math.Round(e.NewValue) + "%";

            if (_brightnessLoading || !_isLoaded)
                return;

            _brightnessTimer ??= new DispatcherTimer(
                TimeSpan.FromMilliseconds(400),
                DispatcherPriority.Normal,
                (_, _) =>
                {
                    _brightnessTimer!.Stop();
                    ApplyBrightness();
                },
                Dispatcher);

            _brightnessTimer.Stop();
            _brightnessTimer.Start();
        }

        private void ApplyBrightness()
        {
            int wanted = (int)Math.Round(slBrightness.Value);

            if (!Uniwill_Control_Utility.Scripts.Misc.Display.TrySetBrightness(wanted, out int applied))
                Log.Warning("[Display] brightness write to {Wanted}% did not verify (readback {Applied})", wanted, applied);
            else
                tbBrightness.Text = applied + "%";
        }

        private void btnScreenOff_Click(object sender, RoutedEventArgs e)
        {
            Uniwill_Control_Utility.Scripts.Misc.Display.TurnOffDisplay();
        }

        /// <summary>
        /// Updates the visual state of the three GPU mode buttons to reflect the current mode.
        /// </summary>
        private void UpdateGpuModeButtons(int mode)
        {
            Style? inactiveStyle = (Style)Resources["GpuModeButtonStyle"];
            Style? activeStyle = (Style)Resources["GpuModeButtonActiveStyle"];

            // Reset all to inactive
            btnIgpuOnly.Style = inactiveStyle;
            btnHybrid.Style = inactiveStyle;
            btnDgpuOnly.Style = inactiveStyle;

            // Activate the matching button
            switch (mode)
            {
                case 0: // Hybrid
                    btnHybrid.Style = activeStyle;
                    break;
                case 1: // dGPU Only
                    btnDgpuOnly.Style = activeStyle;
                    break;
                case 2: // iGPU Only
                    btnIgpuOnly.Style = activeStyle;
                    break;
            }
        }

        private void btnGpuMode_Click(object sender, RoutedEventArgs e)
        {
            if (!_isLoaded)
                return;

            if (sender is not Button btn || btn.Tag is not string tag)
                return;

            if (!int.TryParse(tag, out int mode))
                return;

            // Ignore click if this mode is already active
            Style? activeStyle = (Style?)Resources["GpuModeButtonActiveStyle"];
            if (btn.Style == activeStyle)
                return;

            try
            {
                // Write EC register
                _mux!.SetModeAsync((GpuMuxMode)mode).GetAwaiter().GetResult();
                _lastUserApplyTime = DateTime.UtcNow;
                Debug.WriteLine($"[Display] Applied GPU mode: {(GpuMuxMode)mode}");

                // Update button visual state immediately
                UpdateGpuModeButtons(mode);

                // Write NVRAM for persistence
                try
                {
                    WriteNvramMode((byte)mode);
                }
                catch (Exception nvramEx)
                {
                    Debug.WriteLine($"[Display] NVRAM write failed: {nvramEx.Message}");
                }

                // Show reboot warning
                cardRebootWarning.Visibility = Visibility.Visible;

                // Delay refresh to let the EC process the change
                _delayTimer?.Stop();
                _delayTimer = new DispatcherTimer(TimeSpan.FromSeconds(ApplyDelaySeconds), DispatcherPriority.Normal, (s, ev) =>
                {
                    RefreshAll();
                    ((DispatcherTimer)s!).Stop();
                    _delayTimer = null;
                }, Dispatcher);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Display] Failed to apply mode: {ex.Message}");
            }
        }

        /// <summary>
        /// Updates the visual state of the three power saving buttons.
        /// </summary>
        private void UpdatePowerSavingButtons(int mode)
        {
            _displayedPowerSavingMode = mode;

            Style? inactiveStyle = (Style)Resources["GpuModeButtonStyle"];
            Style? activeStyle = (Style)Resources["GpuModeButtonActiveStyle"];

            // Reset all to inactive
            btnPowerSavingOn.Style = inactiveStyle;
            btnPowerSavingOff.Style = inactiveStyle;
            btnPowerSavingAuto.Style = inactiveStyle;

            // Activate the matching button
            switch (mode)
            {
                case 0: // On (Power Saving)
                    btnPowerSavingOn.Style = activeStyle;
                    break;
                case 1: // Off (Performance)
                    btnPowerSavingOff.Style = activeStyle;
                    break;
                case 2: // Auto
                default:
                    btnPowerSavingAuto.Style = activeStyle;
                    break;
            }
        }

        /// <summary>
        /// Called by the GPU power saving engine after it applies a mode (including the
        /// Automations engine on charger plug/unplug transitions). Repaints the power
        /// saving buttons and shows the cooldown overlay while the page is open, so the
        /// selection follows suite without navigating away. May run from a power-event
        /// callback thread — the repaint is posted through the application Dispatcher.
        /// </summary>
        public void NotifyPowerSavingApplied(int mode)
        {
            if (!_isLoaded)
                return;

            Application.Current.Dispatcher.Invoke(() =>
            {
                // Only a visible transition repaints — repeated StatusChange events
                // re-applying the same mode must not re-trigger the cooldown overlay.
                if (mode == _displayedPowerSavingMode)
                    return;

                UpdatePowerSavingButtons(mode);
                StartCooldownOverlay();
                Log.Information("[Display] Power saving buttons live-updated to {Mode} with cooldown overlay", mode);
            });
        }

        private async void btnPowerSaving_Click(object sender, RoutedEventArgs e)
        {
            if (!_isLoaded)
                return;

            if (sender is not Button btn || btn.Tag is not string tag)
                return;

            if (!int.TryParse(tag, out int mode))
                return;

            // Ignore click if this mode is already active
            Style? activeStyle = (Style?)Resources["GpuModeButtonActiveStyle"];
            if (btn.Style == activeStyle)
                return;

            // Block clicks during GPU driver transition cooldown
            if (GpuInteropGuard.IsBlocked)
                return;

            try
            {
                // Update button visual state immediately
                UpdatePowerSavingButtons(mode);

                // Persist to settings
                Settings.Default.GpuPowerSavingMode = mode;
                Settings.Default.Save();

                // Apply the power saving mode (always uses PnP hotswap)
                var powerSavingMode = (GpuPowerSavingMode)mode;
                string pnpStatusBefore = GpuPowerSaving.GetDgpuPnpStatus();
                await GpuPowerSaving.ApplyAsync(powerSavingMode);
                string pnpStatusAfter = GpuPowerSaving.GetDgpuPnpStatus();

                // Reset LHM Computer so it re-probes with fresh driver handles.
                // After a PnP hotswap the GPU availability changes and LHM needs
                // to re-enumerate to pick up the new active GPU (iGPU fallback).
                try
                {
                    var monitoring = App.GetService<IHardwareMonitoringService>();
                    monitoring?.ResetComputer();
                    Log.Information("[Display] LHM Computer reset after GPU power saving mode change");
                }
                catch (Exception resetEx)
                {
                    Log.Information("[Display] LHM reset after power saving change failed: {Message}", resetEx.Message);
                }

                // After a PnP hotswap the GPU driver reinitializes, which can break
                // the taskbar icon association. Wait for the driver to settle then
                // re-assert the AppUserModelID to restore the taskbar icon.
                _ = Task.Run(async () =>
                {
                    await Task.Delay(3000);
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        App.ReassertAppUserModelID();
                    });
                });

                // Start cooldown overlay to block user clicks during the guard window
                StartCooldownOverlay();

                Log.Information("[Display] GPU Power Saving set to {Mode}, dGPU PnP: {Before} -> {After}, GuardBlocked: {Blocked}, IsAdmin: {IsAdmin}",
                    GpuPowerSaving.GetModeDisplayName(powerSavingMode), pnpStatusBefore, pnpStatusAfter, GpuInteropGuard.IsBlocked, _isAdministrator);
            }
            catch (Exception ex)
            {
                Log.Information("[Display] [ERROR] Failed to apply GPU power saving mode: {Message}", ex.Message);
            }
        }

        private void StartCooldownOverlay()
        {
            _cooldownTimer?.Stop();

            int remainingSeconds = 15;
            txtCooldownCountdown.Text = remainingSeconds.ToString();
            borderPowerSavingCooldown.Visibility = Visibility.Visible;

            _cooldownTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _cooldownTimer.Tick += (s, e) =>
            {
                remainingSeconds--;
                if (remainingSeconds <= 0)
                {
                    _cooldownTimer?.Stop();
                    borderPowerSavingCooldown.Visibility = Visibility.Collapsed;
                    txtCooldownCountdown.Text = string.Empty;
                }
                else
                {
                    txtCooldownCountdown.Text = remainingSeconds.ToString();
                }
            };
            _cooldownTimer.Start();
        }

        #region Display Refresh Rate

        /// <summary>
        /// The laptop's internal panel — always, never the display in use. The refresh-rate
        /// control targets the panel exclusively: ChangeDisplaySettingsEx addresses it by
        /// device name, so the write lands on the panel whether it is primary or secondary,
        /// and Windows' per-display registry carries the mode across primary/secondary
        /// switches. When the panel is detached (lid closed / external-only) GDI hides it
        /// and a selection is deferred (DisplayRefreshRateDeferred) until it reattaches.
        /// </summary>
        private string? RefreshRateTarget()
        {
            try
            {
                return Uniwill_Control_Utility.Scripts.Misc.Display.InternalPanelName();
            }
            catch
            {
                return null;
            }
        }

        private int GetRefreshRateSafe()
        {
            try
            {
                int live = Uniwill_Control_Utility.Scripts.Misc.Display.GetInternalRefreshRate();
                if (live > 0)
                    return live;
                // Panel detached (external-only) — fall back to the last known/selected
                // rate so the combo keeps a selection across the panel being switched off.
                return Settings.Default.DisplayRefreshRateLastKnown;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>True while the runtime mux mode is dGPU-only (unlocks the 300 Hz internal-panel mode).</summary>
        private bool IsDgpuOnlyMode()
        {
            try
            {
                return _mux is not null
                    && _mux.GetModeAsync().GetAwaiter().GetResult() == GpuMuxMode.DgpuOnly;
            }
            catch
            {
                return false;
            }
        }

        private List<int> AvailableRefreshRates()
        {
            try
            {
                // Internal panel only — falls back to the panel's known mode set when the
                // panel is detached (external-only), so the controls stay offered.
                return Uniwill_Control_Utility.Scripts.Misc.Display.GetInternalRefreshRates(IsDgpuOnlyMode());
            }
            catch
            {
                return new List<int>();
            }
        }

        /// <summary>
        /// "Panel model: NE160QDM-NM9" for the bottom bar — the WMI UserFriendlyName of the
        /// laptop panel via PanelDatabase ("--" when WMI reports no name).
        /// </summary>
        private string LaptopModelLabel()
        {
            var model = Uniwill_Control_Utility.Models.PanelDatabase.CurrentPanelModel();
            if (model is null)
                Log.Warning("[Display] Bottom-bar model label: WMI panel model unavailable");
            return model is not null && model.Length > 0 ? $"Panel model: {model}" : "--";
        }

        /// <summary>
        /// Rebuilds the combo box with exactly the rates the display in use offers,
        /// selected at the current one. The programmatic selection detaches the
        /// handler so the sync does not re-apply the setting.
        /// </summary>
        private void UpdateRefreshRateCombo(int currentHz)
        {
            var rates = AvailableRefreshRates();

            cbRefreshRate.Items.Clear();
            int selectedIndex = -1;
            for (int i = 0; i < rates.Count; i++)
            {
                cbRefreshRate.Items.Add(new ComboBoxItem { Content = $"{rates[i]} Hz", Tag = rates[i] });
                if (rates[i] == currentHz)
                    selectedIndex = i;
            }

            // Monitor status first — it must refresh even if the selection sync below fails.
            tbRefreshRateTarget.Text = LaptopModelLabel();

            if (selectedIndex >= 0)
            {
                cbRefreshRate.SelectionChanged -= cbRefreshRate_SelectionChanged;
                try
                {
                    cbRefreshRate.SelectedIndex = selectedIndex;
                }
                catch (Exception ex)
                {
                    Log.Warning("[Display] Refresh rate selection sync failed: {Message}", ex.Message);
                }
                finally
                {
                    cbRefreshRate.SelectionChanged += cbRefreshRate_SelectionChanged;
                }
            }
        }

        private void cbRefreshRate_SelectionChanged(object sender, EventArgs e)
        {
            if (!_isLoaded || _refreshRateBusy)
                return;

            if (cbRefreshRate.SelectedItem is not ComboBoxItem item || item.Tag is not int hz)
                return;

            // Ignore selection of the rate that is already active
            if (hz == GetRefreshRateSafe())
                return;

            _refreshRateBusy = true;

            // The GDI mode switch can take ~1s — keep it off the UI thread.
            _ = Task.Run(() =>
            {
                int applied;
                bool deferredSelection = false;
                if (string.IsNullOrEmpty(Uniwill_Control_Utility.Scripts.Misc.Display.InternalPanelName()))
                {
                    // Panel detached (lid closed / internal display disconnected) — GDI
                    // hides a display that is off, so the write cannot land now. Persist
                    // the selection (deferred for reattach) and remember it as the panel's
                    // value so the combo keeps a selection while the panel is off.
                    Settings.Default.DisplayRefreshRateDeferred = hz;
                    Settings.Default.DisplayRefreshRateLastKnown = hz;
                    deferredSelection = true;
                    Settings.Default.Save();
                    Log.Information("[Display] Refresh rate {Hz} Hz deferred — internal panel detached (external-only)", hz);
                    applied = hz;
                }
                else
                {
                    Settings.Default.DisplayRefreshRateDeferred = 0;
                    Settings.Default.Save();
                    try
                    {
                        applied = Uniwill_Control_Utility.Scripts.Misc.Display.ApplyInternalRefreshRate(hz);
                    }
                    catch (Exception ex)
                    {
                        Log.Information("[Display] [ERROR] Refresh rate apply failed: {Message}", ex.Message);
                        applied = 0;
                    }
                    if (applied == hz)
                    {
                        Settings.Default.DisplayRefreshRateLastKnown = applied;
                        Settings.Default.Save();
                    }
                }

                // Verify via readback — the engine reports failures only to the console.
                // A deferred (detached) selection logged its own line above; skip the
                // success line so it is not misreported.
                if (!deferredSelection)
                {
                    bool success = applied == hz;
                    Log.Information("[Display] Internal panel refresh rate set to {Applied} Hz (requested {Hz} Hz, success: {Success})",
                        applied, hz, success);
                }

                Application.Current.Dispatcher.Invoke(() =>
                {
                    UpdateRefreshRateCombo(applied);
                    _refreshRateBusy = false;
                });
            });
        }

        #endregion

        #region Display Color Calibration

        private string? GetColorProfileSafe()
        {
            try
            {
                // Color calibration is laptop-panel-only — never the display in use.
                var panel = Uniwill_Control_Utility.Scripts.Misc.Display.InternalPanelName();
                if (string.IsNullOrEmpty(panel))
                {
                    // Panel detached (external-only) — fall back to the last known/selected
                    // profile so the combo keeps a selection across the panel being switched off.
                    string lastKnown = Settings.Default.DisplayColorProfileLastKnown;
                    return lastKnown.Length > 0 ? lastKnown : null;
                }
                return Uniwill_Control_Utility.Scripts.Misc.Display.GetCurrentColorProfile(panel);
            }
            catch
            {
                return null;
            }
        }

        private List<string> OfferedColorProfileFiles()
        {
            try
            {
                return Uniwill_Control_Utility.Scripts.Misc.Display.GetOfferedColorProfileFiles();
            }
            catch
            {
                return new List<string>();
            }
        }

        /// <summary>
        /// Rebuilds the combo with exactly the ICC profiles Windows offers, selected at the
        /// profile currently associated with the laptop panel. The programmatic selection
        /// detaches the handler so the sync does not re-apply the setting.
        /// </summary>
        private void UpdateColorCalibrationCombo(string? currentFile)
        {
            var files = OfferedColorProfileFiles();

            cbColorCalibration.Items.Clear();
            int selectedIndex = -1;
            for (int i = 0; i < files.Count; i++)
            {
                string label = Uniwill_Control_Utility.Scripts.Misc.Display.ColorProfileLabel(files[i]);
                cbColorCalibration.Items.Add(new ComboBoxItem { Content = label, Tag = files[i] });
                if (files[i] == currentFile)
                    selectedIndex = i;
            }

            if (selectedIndex >= 0)
            {
                cbColorCalibration.SelectionChanged -= cbColorCalibration_SelectionChanged;
                try
                {
                    cbColorCalibration.SelectedIndex = selectedIndex;
                }
                catch (Exception ex)
                {
                    Log.Warning("[Display] Color calibration selection sync failed: {Message}", ex.Message);
                }
                finally
                {
                    cbColorCalibration.SelectionChanged += cbColorCalibration_SelectionChanged;
                }
            }

            UpdateColorDeleteVisibility();
        }

        /// <summary>Delete is offered for imported profiles only: Windows'
        /// shipped profiles are not the user's to remove.</summary>
        private void UpdateColorDeleteVisibility()
        {
            bool imported = cbColorCalibration.SelectedItem is ComboBoxItem item
                            && item.Tag is string file
                            && Uniwill_Control_Utility.Scripts.Misc.Display.IsImportedColorProfile(file);

            btnDeleteColorProfile.Visibility = imported ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>Deletes the selected imported profile (U-205). If it is the
        /// associated one, the association is dropped first - leaving the
        /// registry pointing at a missing file would have Windows fail every
        /// colour lookup for the display. The selection then falls back to the
        /// last shipped profile the user had chosen (Default if none), so the
        /// combo never ends up blank.</summary>
        private async void btnDeleteColorProfile_Click(object sender, RoutedEventArgs e)
        {
            if (cbColorCalibration.SelectedItem is not ComboBoxItem item || item.Tag is not string file)
                return;

            _colorCalBusy = true;
            try
            {
                bool wasAssociated = string.Equals(file, GetColorProfileSafe(), StringComparison.OrdinalIgnoreCase);

                if (wasAssociated)
                {
                    await Task.Run(() => Uniwill_Control_Utility.Scripts.Misc.Display.ApplyColorProfile(
                        Uniwill_Control_Utility.Scripts.Misc.Display.InternalPanelName(), string.Empty));
                }

                if (!Uniwill_Control_Utility.Scripts.Misc.Display.DeleteImportedColorProfile(file, out string? error))
                {
                    ToastNotification.ShowToastNotification(
                        LocalizationService.Get("Color Calibration"),
                        LocalizationService.Format("The ICC profile could not be deleted: {0}", error ?? string.Empty));
                    return;
                }

                // Re-associate the shipped profile the display had before the
                // imported one (Default when there is none), so deleting the
                // active profile leaves a calibrated, selected display instead
                // of an empty combo and a blank association.
                List<string> offered = OfferedColorProfileFiles();
                string? fallback = offered.Contains(_lastShippedColorProfile)
                    ? _lastShippedColorProfile
                    : offered.FirstOrDefault();

                if (wasAssociated && !string.IsNullOrEmpty(fallback))
                {
                    await Task.Run(() => Uniwill_Control_Utility.Scripts.Misc.Display.ApplyColorProfile(
                        Uniwill_Control_Utility.Scripts.Misc.Display.InternalPanelName(), fallback!));
                }

                UpdateColorCalibrationCombo(GetColorProfileSafe() ?? fallback);
                ToastNotification.ShowToastNotification(
                    LocalizationService.Get("Color Calibration"),
                    LocalizationService.Get("The ICC profile was deleted."));
            }
            finally
            {
                _colorCalBusy = false;
            }
        }

        /// <summary>Imports a user ICC profile (U-205): the Core validator
        /// decides, Core refuses garbage before anything is associated. A
        /// successful import is switched to immediately - choosing a file in
        /// the import dialog is the user's intent to use it.</summary>
        private async void btnImportColorProfile_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                AddExtension = true,
                DefaultExt = ".icc",
                Filter = LocalizationService.Get("ICC profile (*.icc;*.icm)|*.icc;*.icm"),
            };

            if (dialog.ShowDialog() != true)
                return;

            if (!Uniwill_Control_Utility.Scripts.Misc.Display.ImportColorProfile(
                    dialog.FileName, out string? file, out string? error) ||
                file is null)
            {
                ToastNotification.ShowToastNotification(
                    LocalizationService.Get("Color Calibration"),
                    LocalizationService.Format("The ICC profile could not be imported: {0}", error ?? string.Empty));
                return;
            }

            _colorCalBusy = true;
            try
            {
                string? panel = Uniwill_Control_Utility.Scripts.Misc.Display.InternalPanelName();
                if (string.IsNullOrEmpty(panel))
                {
                    // Panel detached (external-only): defer, exactly like a
                    // combo selection made in that state.
                    Settings.Default.DisplayColorProfileDeferred = file;
                    Settings.Default.DisplayColorProfileLastKnown = file;
                    Settings.Default.Save();
                }
                else
                {
                    await Task.Run(() => Uniwill_Control_Utility.Scripts.Misc.Display.ApplyColorProfile(panel, file));
                }

                UpdateColorCalibrationCombo(GetColorProfileSafe() ?? file);
            }
            finally
            {
                _colorCalBusy = false;
            }

            ToastNotification.ShowToastNotification(
                LocalizationService.Get("Color Calibration"),
                LocalizationService.Get("The ICC profile was imported."));
        }

        /// <summary>Copies the display's associated profile out (U-205).</summary>
        private void btnExportColorProfile_Click(object sender, RoutedEventArgs e)
        {
            string? current = GetColorProfileSafe();

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                AddExtension = true,
                DefaultExt = ".icc",
                FileName = string.IsNullOrEmpty(current) ? "UCU-color-profile.icc" : current!,
                Filter = LocalizationService.Get("ICC profile (*.icc;*.icm)|*.icc;*.icm"),
            };

            if (dialog.ShowDialog() != true)
                return;

            if (!Uniwill_Control_Utility.Scripts.Misc.Display.ExportColorProfile(
                    Uniwill_Control_Utility.Scripts.Misc.Display.InternalPanelName(),
                    dialog.FileName, out string? error))
            {
                ToastNotification.ShowToastNotification(
                    LocalizationService.Get("Color Calibration"),
                    LocalizationService.Format("The ICC profile could not be exported: {0}", error ?? string.Empty));
                return;
            }

            ToastNotification.ShowToastNotification(
                LocalizationService.Get("Color Calibration"),
                LocalizationService.Get("The ICC profile was exported."));
        }

        private void cbColorCalibration_SelectionChanged(object sender, RoutedEventArgs e)
        {
            UpdateColorDeleteVisibility();

            // Remember the shipped choice before an imported one can take over:
            // deleting the imported profile returns the display to it.
            if (cbColorCalibration.SelectedItem is ComboBoxItem chosen
                && chosen.Tag is string chosenFile
                && !Uniwill_Control_Utility.Scripts.Misc.Display.IsImportedColorProfile(chosenFile))
            {
                _lastShippedColorProfile = chosenFile;
            }

            if (!_isLoaded || _colorCalBusy)
                return;

            if (cbColorCalibration.SelectedItem is not ComboBoxItem item || item.Tag is not string file)
                return;

            // Ignore selection of the profile that is already active
            if (file == GetColorProfileSafe())
                return;

            _colorCalBusy = true;

            // The ICM association reload can take ~1s — keep it off the UI thread.
            _ = Task.Run(() =>
            {
                string? applied;
                bool deferredSelection = false;
                if (string.IsNullOrEmpty(Uniwill_Control_Utility.Scripts.Misc.Display.InternalPanelName()))
                {
                    // Panel detached (external-only) — persist the selection (deferred for
                    // reattach) and remember it so the combo keeps a selection while the
                    // panel is off.
                    Settings.Default.DisplayColorProfileDeferred = file;
                    Settings.Default.DisplayColorProfileLastKnown = file;
                    deferredSelection = true;
                    Settings.Default.Save();
                    Log.Information("[Display] Color profile {File} deferred — internal panel detached (external-only)", file);
                    applied = file;
                }
                else
                {
                    Settings.Default.DisplayColorProfileDeferred = string.Empty;
                    Settings.Default.Save();
                    try
                    {
                        Uniwill_Control_Utility.Scripts.Misc.Display.ApplyColorProfile(
                            Uniwill_Control_Utility.Scripts.Misc.Display.InternalPanelName(), file);
                    }
                    catch (Exception ex)
                    {
                        Log.Information("[Display] [ERROR] Color calibration apply failed: {Message}", ex.Message);
                    }

                    applied = Uniwill_Control_Utility.Scripts.Misc.Display.GetInternalColorProfile();
                    if (applied == file)
                    {
                        Settings.Default.DisplayColorProfileLastKnown = file;
                        Settings.Default.Save();
                    }
                }

                // Verify via readback — the registry write reports failures only via return code.
                // A deferred (detached) selection logged its own line above.
                if (!deferredSelection)
                {
                    bool success = applied == file;
                    Log.Information("[Display] Color profile set to {Applied} (requested {File}, success: {Success})",
                        applied, file, success);
                }

                Application.Current.Dispatcher.Invoke(() =>
                {
                    UpdateColorCalibrationCombo(applied);
                    _colorCalBusy = false;
                });
            });
        }

        #endregion

        private void btnRestartNow_Click(object sender, RoutedEventArgs e)
        {
            // Initiate system restart
            try
            {
                Debug.WriteLine("[Display] Restart requested by user");
                System.Diagnostics.Process.Start(new ProcessStartInfo
                {
                    FileName = "shutdown",
                    Arguments = "/r /t 0",
                    // No "runas" verb: the console user already holds
                    // SeShutdownPrivilege, so the verb bought nothing except
                    // the UAC prompt on the user's own restart click.
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Display] Restart failed: {ex.Message}");
            }
        }

        private void RefreshAll()
        {
            if (_mux is null) return;

            // Skip syncing the buttons if the user recently changed the setting
            bool withinGrace = (DateTime.UtcNow - _lastUserApplyTime).TotalMilliseconds < ApplyGraceMs;

            if (!withinGrace)
            {
                try
                {
                    // Read from NVRAM (authoritative) or fall back to EC
                    int mode;
                    if (_isNvramAvailable)
                    {
                        byte? nvramMode = ReadNvramMode();
                        mode = nvramMode ?? 0;
                    }
                    else
                    {
                        mode = (int)_mux!.GetModeAsync().GetAwaiter().GetResult();
                    }
                    UpdateGpuModeButtons(mode);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Display] Failed to read MUX mode: {ex.Message}");
                }
            }

            // Re-sync the power saving buttons from the persisted selection — the
            // Automations engine updates it when it applies a mode on power transitions.
            UpdatePowerSavingButtons(Settings.Default.GpuPowerSavingMode);

            // Track the live panel values (so they survive the panel being switched off)
            // and consume deferred refresh-rate/color selections from an external-only
            // session before re-reading the live values so the combos show the result.
            ReconcileDeferredPanelSettings();

            // The refresh-rate combo tracks the live Windows display mode
            UpdateRefreshRateCombo(GetRefreshRateSafe());
        }

        /// <summary>
        /// While the panel is attached: remembers the live refresh rate and color profile
        /// as the persisted last-known values (so they survive the panel being switched
        /// off) and consumes deferred selections from an external-only session — applying
        /// them once the panel is attached again and clearing them on readback match.
        /// </summary>
        private void ReconcileDeferredPanelSettings()
        {
            if (string.IsNullOrEmpty(Uniwill_Control_Utility.Scripts.Misc.Display.InternalPanelName()))
            {
                // Log only when something is stuck waiting for the panel — a persistent
                // "detached" read while the panel is visibly on points at detection, not
                // at the deferral logic.
                if (Settings.Default.DisplayRefreshRateDeferred > 0
                    || Settings.Default.DisplayColorProfileDeferred.Length > 0)
                    Log.Warning("[Display] Deferred panel settings pending — internal panel still reads as detached");
                return;
            }

            // Remember the live values before a possible disconnect.
            int liveHz = Uniwill_Control_Utility.Scripts.Misc.Display.GetInternalRefreshRate();
            if (liveHz > 0)
                Settings.Default.DisplayRefreshRateLastKnown = liveHz;
            Settings.Default.DisplayColorProfileLastKnown =
                Uniwill_Control_Utility.Scripts.Misc.Display.GetInternalColorProfile() ?? string.Empty;

            int deferred = Settings.Default.DisplayRefreshRateDeferred;
            if (deferred > 0)
            {
                int applied = Uniwill_Control_Utility.Scripts.Misc.Display.ReconcileInternalRefreshRate(deferred);
                if (applied == deferred)
                {
                    Settings.Default.DisplayRefreshRateDeferred = 0;
                    Settings.Default.DisplayRefreshRateLastKnown = applied;
                    Settings.Default.Save();
                    Log.Information("[Display] Deferred refresh rate {Hz} Hz applied on panel reattach", applied);
                }
            }

            string deferredProfile = Settings.Default.DisplayColorProfileDeferred;
            if (deferredProfile.Length > 0)
            {
                string appliedProfile = Uniwill_Control_Utility.Scripts.Misc.Display.ReconcileInternalColorProfile(deferredProfile);
                if (appliedProfile == deferredProfile)
                {
                    Settings.Default.DisplayColorProfileDeferred = string.Empty;
                    Settings.Default.DisplayColorProfileLastKnown = appliedProfile;
                    Settings.Default.Save();
                    Log.Information("[Display] Deferred color profile {Profile} applied on panel reattach", appliedProfile);
                }
            }

            Settings.Default.Save();
        }

        #region NVRAM Access (GPU-mode firmware mirror via NvramAccess)

        // The NVRAM reads and writes route through NvramAccess: the engine's
        // device.nvram verbs answer from its LocalSystem token (writing
        // firmware variables demands SeSystemEnvironmentPrivilege, which an
        // unelevated process can never enable — the page's own P/Invokes were
        // silently dead in the field), and the page's original direct calls
        // survive in NvramAccess as the fallback. The offset-98 encoding stays
        // page-side: the mux codec and the platform the engine reports decode
        // the raw byte, exactly as the factory write encoded it.

        /// <summary>
        /// Reads the OemDisplayMode byte from the OemMagicVariable NVRAM entry (offset 98).
        /// </summary>
        private byte? ReadNvramMode()
        {
            byte[]? nvram = NvramAccess.ReadOemMagic();
            if (nvram == null || nvram.Length <= OffsetOemDisplayMode)
                return null;

            // The NVRAM byte uses the same platform-specific encoding as the EC
            // register; decode through the shared codec with the platform the engine
            // resolved, so the value written at factory time decodes as written.
            return (byte)EcMuxCodec.RawToMode(nvram[OffsetOemDisplayMode], EcPlatformForNvram);
        }

        /// <summary>
        /// Writes the OemDisplayMode byte to the OemMagicVariable NVRAM entry (offset 98).
        /// This is the same mechanism OpenRevo uses for BIOS-visible GPU mode persistence.
        /// </summary>
        private void WriteNvramMode(byte mode)
        {
            byte[]? nvram = NvramAccess.ReadOemMagic();
            if (nvram == null)
            {
                throw new InvalidOperationException("Cannot read OemMagicVariable for NVRAM write");
            }

            // The caller passes the semantic mode; the firmware expects the
            // platform-specific raw byte (swapped on Intel platforms), so encode with
            // the same platform the engine reports.
            byte raw = EcMuxCodec.ModeToRaw((GpuMuxMode)mode, EcPlatformForNvram);
            byte[] modified = (byte[])nvram.Clone();
            modified[OffsetOemDisplayMode] = raw;

            if (!NvramAccess.WriteOemMagic(modified))
            {
                throw new InvalidOperationException("NVRAM OemMagicVariable write failed (see [Nvram] log)");
            }

            Debug.WriteLine($"[Display] NVRAM OemDisplayMode set to {raw} (mode {mode}) at offset {OffsetOemDisplayMode}");
        }

        #endregion

        #region MiniLED Local Dimming

        /// <summary>
        /// The row's toggle: the handshake actuates through MiniLedAccess
        /// (engine verb first, the page's original WMI as that route's
        /// fallback), then the EC 0x72B readback verifies; the toggle flips
        /// back on the firmware's answer, never on the call's.
        /// </summary>
        private void TsLocalDimming_Toggled(object sender, RoutedEventArgs e)
        {
            if (_localDimmingToggling)
                return;

            bool isOn = tsLocalDimming.IsChecked == true;

            _localDimmingToggling = true;
            tsLocalDimming.IsEnabled = false;

            // WMI calls carry COM apartment affinity - they must run on a
            // dedicated thread (the route's local fallback does too).
            _ = Task.Run(() =>
            {
                try
                {
                    bool success = SetLocalDimming(isOn);
                    Debug.WriteLine($"[Display] Local Dimming {(isOn ? "enabled" : "disabled")}: {(success ? "success" : "failed")}");

                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        if (!success)
                        {
                            tsLocalDimming.IsChecked = !isOn;
                        }
                        tsLocalDimming.IsEnabled = true;
                        _localDimmingToggling = false;

                        Settings.Default.DisplayLocalDimming = tsLocalDimming.IsChecked == true;
                        Settings.Default.Save();
                    });
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Display] Local Dimming toggle failed: {ex.Message}");

                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        tsLocalDimming.IsChecked = !isOn;
                        tsLocalDimming.IsEnabled = true;
                        _localDimmingToggling = false;

                        Settings.Default.DisplayLocalDimming = tsLocalDimming.IsChecked == true;
                        Settings.Default.Save();
                    });
                }
            });
        }

        private bool SetLocalDimming(bool enable)
        {
            // The WMI handshake routes through MiniLedAccess: the engine's
            // device verb performs the OEM ACPI two-step pair as LocalSystem
            // (executing a custom root\WMI method needs write access an
            // unelevated process does not have — the row was silently dead
            // in that state), and the page's original WMI code survives in
            // MiniLedAccess as the fallback.
            if (!MiniLedAccess.SetLocalDimming(enable))
                return false;

            Thread.Sleep(100);

            // Verify via EC readback (the telemetry verb, already engine-routed).
            if (_telemetry != null && _isEcAvailable)
            {
                byte ecValue = _telemetry.ReadRegisterAsync(EcLocalDimmingRegister).GetAwaiter().GetResult();
                bool actualState = (ecValue & EcLocalDimmingBit) != 0;

                if (actualState != enable)
                {
                    Debug.WriteLine($"[Display] EC 0x72B readback 0x{ecValue:X2} shows {(actualState ? "ON" : "OFF")}, expected {(enable ? "ON" : "OFF")} — mismatch");
                    return false;
                }

                Debug.WriteLine($"[Display] EC 0x72B readback 0x{ecValue:X2} confirms Local Dimming {(enable ? "ON" : "OFF")}");
            }

            return true;
        }

        #endregion
    }
}
