using Microsoft.Extensions.Logging;
using Serilog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Uniwill_Control_Utility.Models;
using Uniwill_Control_Utility.Properties;
using Uniwill_Control_Utility.Scripts;
using Uniwill_Control_Utility.Scripts.GPUs.NVIDIA;
using Uniwill_Control_Utility.Scripts.Intel_Backend;
using Uniwill_Control_Utility.Scripts.Misc;
using Uniwill_Control_Utility.Services;
using UCU.Core.Supervision;
using Wpf.Ui.Abstractions.Controls;
namespace Uniwill_Control_Utility.Views.Pages
{
    public partial class Presets : INavigationAware
    {
        private readonly ILogger<Presets> _logger;
        private readonly GpuInventoryService? _gpuInventory;
        private readonly HydroUIService? _waterCooler;
        private readonly DeviceApplier? _deviceApplier;
        private readonly PresetsNavigationRequest _navigationRequest;
        private string _currentProfile = "Gaming";

        // The engine's GPU power-control answer (the EC published its power table). An
        // unsupported card is inert, so a preset apply must not write from a guessed base.
        private bool _gpuPowerSupported;

        // V-F curve preset state (managed by CustomPresetStore now)
        private int _vfActivePreset = -1;
        private Views.Windows.VfCurveEditorWindow? _vfEditorWindow;
        private bool _userTouchedCoreSlider; // true when user manually changes the core clock slider

        // Custom preset slot state (1-5, maps to index 0-4)
        private int _customPresetSlot = 0;

        /// <summary>
        /// Returns the active PresetMode based on Power boost mode setting.
        /// </summary>
        private Models.PresetMode ActivePresetMode =>
            Settings.Default.EnablePowerBoostMode
                ? Models.PresetMode.Boost
                : Models.PresetMode.Normal;

        /// <summary>
        /// Gets or sets the slot index for the active preset mode, persisted separately per mode.
        /// </summary>
        private int ActivePresetSlot
        {
            get => ActivePresetMode == Models.PresetMode.Boost
                ? Settings.Default.customPresetSlotBoost
                : Settings.Default.customPresetSlot;
            set
            {
                if (ActivePresetMode == Models.PresetMode.Boost)
                    Settings.Default.customPresetSlotBoost = value;
                else
                    Settings.Default.customPresetSlot = value;
            }
        }

        // Custom profile fan curve persistence
        private FanControlSettings? _savedCustomFanSettings;

        // Diagnostic logging (visible in Serilog log file)
        private static void DebugLog(string message)
        {
            try { Log.Information("[Presets] {Message}", message); } catch { }
        }

        // Adaptive Mode override snackbar
        private Wpf.Ui.Controls.Snackbar? _adaptiveSnackbar;

        // Stored profile values for status card (independent of slider UI state).
        private double _profileSustained = 75;
        private double _profileBoost = 85;
        private int _profileTjMax = 95;
        private int _profileCoCcd0 = 0;
        private int _profileCoCcd1 = 0;

        // Per-core CO state: the map the user configured, the mode to go back to if the
        // per-core dialog is cancelled, and the topology read once for this page.
        private int[] _coPerCore = Array.Empty<int>();
        private Models.CpuCoMode _coModeBeforePerCore = Models.CpuCoMode.Unified;
        private CpuTopology? _coTopology;

        private CpuTopology CoTopology => _coTopology ??= CpuTopologyReader.Read();
        private double _profileGpuTgp = 150;
        private bool _profileGpuDb = false;
        private double _profileGpuDbOffset = 0;
        private double _profileIntelPl1 = 45;
        private double _profileIntelPl2 = 60;

        // Voltage control (Curve Optimizer) is AMD-only and gated by the EnableCpuVoltageControl setting.
        // Intel scenarios have no voltage control at all — the undervolt UI and MSR commands are removed.
        private bool CpuVoltageEnabled => Settings.Default.EnableCpuVoltageControl;

        public Presets(
            ILogger<Presets> logger,
            PresetsNavigationRequest navigationRequest,
            GpuInventoryService? gpuInventory = null,
            HydroUIService? waterCooler = null,
            DeviceApplier? deviceApplier = null,
            QKeyButtonService? qKeyButton = null)
        {
            _logger = logger;
            _navigationRequest = navigationRequest;
            _gpuInventory = gpuInventory;
            _waterCooler = waterCooler;
            _deviceApplier = deviceApplier;
            // Lets a preset applied by the physical button drive this page's selection state.
            qKeyButton?.RegisterPresetsPage(this);
            InitializeComponent();
            _ = Tablet.TabletDevices;
        }

        public async Task OnNavigatedToAsync()
        {
            try
            {
                // Subscribe to DiagnosticLogger events so deep code (EC, NVML) posts to our UI log
                DiagnosticLogger.OnMessage += DiagnosticLogger_OnMessage;

                await applyPlatformVisibilityAsync().ConfigureAwait(true);
                applyPowerLimits();
                setProfileTooltips();
                setRomInfoTooltip();
                loadSavedProfile();

                // For Custom profile, restore slider values.
                // Must run in OnNavigatedToAsync because Page_Loaded only fires once
                // for scoped pages that stay in the visual tree.
                // selectProfile() above already called LoadCustomPreset from
                // CustomPresetStore. Only fall back to Settings.Default values
                // if the CustomPresetStore slot is empty (legacy compatibility).
                if (_currentProfile == "Custom")
                {
                    var presets = Models.CustomPresetStore.Load(ActivePresetMode);
                    int slot = _customPresetSlot;
                    DebugLog($"OnNavigatedToAsync Custom: _customPresetSlot={slot}, presets[{slot}].HasChanges={slot < presets.Length && presets[slot].HasChanges}");
                    if (slot < 0 || slot >= presets.Length || !presets[slot].HasChanges)
                    {
                        // Slot empty — fall back to Settings.Default values saved by last Apply.
                        DebugLog($"OnNavigatedToAsync Custom: Falling back to LoadCustomValues()");
                        LoadCustomValues();
                    }
                    updateStatus();
                    updateAllLabels();

                    // Force a visual refresh on cached pages. WPF doesn't re-render
                    // TextBlock elements when .Text is set programmatically on a
                    // page that was previously displayed with different values.
                    // Defer a second pass to the next dispatcher cycle so the visual
                    // tree has processed the initial changes.
                    _ = this.Dispatcher.InvokeAsync(() =>
                    {
                        updateStatus();
                        updateAllLabels();
                        this.UpdateLayout();
                    }, DispatcherPriority.Background);
                }

                // After ALL slider value assignments (profile load + Custom fallback),
                // clamp to current limits so Power Boost mode changes are respected.
                // Must run after the Custom block above because LoadCustomValues()
                // can overwrite slider values that were already clamped.
                clampSlidersToLimits();

                // Refresh preset mode label and bottom bar stroke on every navigation
                // (handles settings changes from the Settings page).
                UpdatePresetModeLabel(Settings.Default.EnablePowerBoostMode);

                // Update overlay based on Adaptive Mode override state
                UpdateAdaptiveOverlay();

                OpenRequestedTuningSection();

                // The GPU power-ceiling card reads the driver key through the engine,
                // so its state is re-read on every navigation (the Settings page owns
                // the Power boost mode toggle this card gates on). Not awaited: the
                // page must not wait on the engine to finish painting.
                _ = RefreshRomOverrideAsync();
            }
            catch (Exception ex)
            {
                // Broad catch: OnNavigatedToAsync can fail from any UI control
                // initialization issue (missing resources, null controls, etc.).
                _logger.LogError(ex, "Failed to navigate to Presets page");
            }
        }

        private void OpenRequestedTuningSection()
        {
            if (!_navigationRequest.TryConsume(out PresetsTuningSection section))
                return;

            // Tuning controls live in the Custom profile. Selecting it without
            // applying changes only reveals the editors and restores their values.
            selectProfile("Custom", apply: false);

            Wpf.Ui.Controls.CardExpander target = section switch
            {
                PresetsTuningSection.Cpu when Family.TYPE == Family.ProcessorType.Intel => sdIntelTuning,
                PresetsTuningSection.Cpu => sdCpuPower,
                _ => sdNvidia
            };

            if (target.Visibility != Visibility.Visible)
                return;

            target.IsExpanded = true;
            Dispatcher.BeginInvoke(
                () => target.BringIntoView(),
                DispatcherPriority.Loaded);
        }

        public Task OnNavigatedFromAsync()
        {
            DiagnosticLogger.OnMessage -= DiagnosticLogger_OnMessage;
            return Task.CompletedTask;
        }

        private void DiagnosticLogger_OnMessage(string line)
        {
            // Only show messages from files relevant to GPU power control debugging.
            // Filters out Adaptive game-detection spam, fan polling, etc.
            if (line.Contains("[RyzenAdj_To_UCU.") ||
                line.Contains("[NvTuning.") ||
                line.Contains("[EcEngine.") ||
                line.Contains("[Presets.") ||
                line.Contains("[Translate") ||
                line.Contains("[WinPower]") ||
                line.Contains("[VfCurveEditor.") ||
                line.Contains("SMU ") ||
                line.Contains("[PresetProfileApplier]"))
            {
                DebugLog(line);
            }
        }

        private void Page_Loaded(object sender, RoutedEventArgs e)
        {
            // Detect if a V-F curve preset has non-zero offsets on disk and activate it.
            // _vfActivePreset starts at -1; it gets set to 0 when the user opens the
            // curve editor. On app restart we need to re-detect so the "Curve" label
            // shows correctly even if the editor was never opened in this session.
            detectAndActivateVfCurve();

            // For Custom profile, restore UI state (radio buttons, apply-on-startup checkbox).
            // LoadCustomValues() is already called in OnNavigatedToAsync.
            if (_currentProfile == "Custom")
            {
                // Sync _customPresetSlot to the active mode's saved slot
                _customPresetSlot = ActivePresetSlot;
                if (_customPresetSlot < 0 || _customPresetSlot >= Models.CustomPreset.MaxSlots)
                    _customPresetSlot = 0;

                // Sync radio button selection
                var buttons = new RadioButton[] { rbProfile1, rbProfile2, rbProfile3, rbProfile4, rbProfile5 };
                for (int i = 0; i < buttons.Length; i++)
                    buttons[i].IsChecked = i == _customPresetSlot;

                updateRadioButtonStyles();
            }

            // Initialize slider defaults from stored profile values so the status card
            // shows correct values even when the custom panel is collapsed.
            // For Custom profile, LoadCustomValues() already loaded slider values, so skip.
            if (_currentProfile != "Custom")
            {
                sdSustained.Value = _profileSustained;
                sdBoost.Value = _profileBoost;
                sdTjMax.Value = _profileTjMax;

                sdNvPower.Value = _profileGpuTgp;
                togDynamicBoost.IsChecked = _profileGpuDb;
                sdNvDbOffset.Value = _profileGpuDbOffset;
                spNvDbOffset.Visibility = _profileGpuDb ? Visibility.Visible : Visibility.Collapsed;
            }

            // Read back GPU clock offsets from hardware (Afterburner, NVCP, etc.)
            // For Custom profile, saved values are already loaded by LoadCustomValues().
            // Only override with hardware readback if Custom saved values are zero.
            if (sdNvidia.Visibility == Visibility.Visible)
            {
                if (NvTuning.TryGetClockOffsets(out int coreOff, out int memOff))
                {
                    // Use hardware readback unless Custom has non-zero saved values
                    if (_currentProfile != "Custom" ||
                        (Settings.Default.customNvCore == 0 && Settings.Default.customNvMem == 0))
                    {
                        sdNvCore.Value = coreOff;
                        sdNvMem.Value = memOff;
                    }
                    DebugLog($"GPU clock offsets read: core={coreOff:+0;-0;0} MHz, mem={memOff:+0;-0;0} MHz");
                }
                else
                {
                    // Fallback: only set 0 if Custom doesn't have saved values
                    if (_currentProfile != "Custom")
                    {
                        sdNvCore.Value = 0;
                        sdNvMem.Value = 0;
                    }
                }
            }

            // Update status from stored values (sliders may be in collapsed panel)
            updateStatus();

            // Update labels — always refresh so the TGP label matches the slider value
            // even when the custom panel is collapsed (pre-made profiles).
            updateAllLabels();

            // Clamp slider values to current limits after Page_Loaded overwrites them.
            // (OnNavigatedToAsync clamps too, but Page_Loaded fires after and resets values.)
            clampSlidersToLimits();
            updateAllLabels();

            // Update overlay based on Adaptive Mode override state
            UpdateAdaptiveOverlay();
        }

        /// <summary>
        /// Shows/hides the adaptive override overlay and snackbar on the Presets page.
        /// Only active when Adaptive Mode is running AND CPU or GPU override is enabled.
        /// </summary>
        private void UpdateAdaptiveOverlay()
        {
            bool shouldShow = Settings.Default.isAdaptiveModeRunning
                && (Settings.Default.AdaptiveCpuEnabled || Settings.Default.AdaptiveGpuEnabled);

            if (shouldShow)
            {
                overlayAdaptiveWarning.Visibility = Visibility.Visible;
                ShowAdaptiveSnackbar();
            }
            else
            {
                overlayAdaptiveWarning.Visibility = Visibility.Collapsed;
                HideAdaptiveSnackbar();
            }
        }

        private void ShowAdaptiveSnackbar()
        {
            if (_adaptiveSnackbar != null)
                SnackbarPresenter.HideCurrent();

            _adaptiveSnackbar = new Wpf.Ui.Controls.Snackbar(SnackbarPresenter)
            {
                Title = "Adaptive Mode Override",
                Content = "Adaptive Mode is currently controlling CPU/GPU tuning. Controls on this page are currently disabled.",
                Appearance = Wpf.Ui.Controls.ControlAppearance.Info,
                Icon = new Wpf.Ui.Controls.FontIcon { FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons"), Glyph = "\uF167", FontSize = 16, Margin = new System.Windows.Thickness(0, 0, 10, 0) },
                IsCloseButtonEnabled = false,
                Timeout = TimeSpan.FromHours(1),
            };

            if (SnackbarPresenter.IsLoaded)
                _adaptiveSnackbar.Show(true);
            else
                SnackbarPresenter.Loaded += (s, e) => _adaptiveSnackbar?.Show(true);
        }

        private void HideAdaptiveSnackbar()
        {
            SnackbarPresenter.HideCurrent();
            _adaptiveSnackbar = null;
        }

        /// <summary>
        /// Shows a short-lived snackbar above the bottom bar for save/clear actions.
        /// Primary-styled, no close button, 3 second timeout.
        /// </summary>
        private void ShowPresetSnackbar(string title, string message)
        {
            SnackbarPresenter.HideCurrent();

            var snackbar = new Wpf.Ui.Controls.Snackbar(SnackbarPresenter)
            {
                Title = title,
                Content = message,
                Appearance = Wpf.Ui.Controls.ControlAppearance.Primary,
                IsCloseButtonEnabled = false,
                Timeout = TimeSpan.FromSeconds(3)
            };

            if (SnackbarPresenter.IsLoaded)
                snackbar.Show(true);
            else
                SnackbarPresenter.Loaded += (s, e) => snackbar.Show(true);
        }

        private void Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (sender == sdNvCore)
                _userTouchedCoreSlider = true;
            updateAllLabels();
        }

        private void rbCoMode_Click(object sender, RoutedEventArgs e)
        {
            // Remembered so a cancelled per-core dialog can put the selection back.
            if (rbCoUnified.IsChecked == true)
                _coModeBeforePerCore = Models.CpuCoMode.Unified;
            else if (rbCoPerCcd.IsChecked == true)
                _coModeBeforePerCore = Models.CpuCoMode.PerCcd;

            updateCoVisibility();
        }

        private void updateCoVisibility()
        {
            bool unified = rbCoUnified.IsChecked == true;
            bool perCore = rbCoPerCore.IsChecked == true;
            bool perCcd = !unified && !perCore;

            gridCustomCoUnifiedSlider.Visibility = unified ? Visibility.Visible : Visibility.Collapsed;
            gridCustomCoCcd0.Visibility = perCcd ? Visibility.Visible : Visibility.Collapsed;
            gridCustomCoCcd1.Visibility = perCcd ? Visibility.Visible : Visibility.Collapsed;
            dividerCoPerCcd.Visibility = perCcd ? Visibility.Visible : Visibility.Collapsed;
            gridCustomCoPerCore.Visibility = perCore ? Visibility.Visible : Visibility.Collapsed;

            // No topology, no per-core map to edit.
            rbCoPerCore.IsEnabled = CoTopology.CoreCount > 0;

            if (perCore)
                updateCoPerCoreSummary();
        }

        private void btnCoPerCore_Click(object sender, RoutedEventArgs e)
        {
            var topology = CoTopology;
            if (topology.CoreCount == 0)
                return;

            var dialog = new Views.Windows.PerCoreCurveOptimizerWindow(topology, CurrentCoMap())
            {
                Owner = Application.Current.MainWindow
            };

            if (dialog.ShowDialog() == true && dialog.Result is not null)
            {
                _coPerCore = dialog.Result.ToArray();
                rbCoPerCore.IsChecked = true;
                updateCoVisibility();
                updateStatus();
                return;
            }

            // Cancelled: nothing was tuned, so a mode picked only for the dialog goes back.
            if (_coPerCore.Length == 0)
            {
                rbCoUnified.IsChecked = _coModeBeforePerCore == Models.CpuCoMode.Unified;
                rbCoPerCcd.IsChecked = _coModeBeforePerCore == Models.CpuCoMode.PerCcd;
                updateCoVisibility();
            }
        }

        /// <summary>
        /// The per-core map as the user sees it: stored cores win, the rest fall back to
        /// their CCD offset so the dialog never opens on stale zeros.
        /// </summary>
        private int[] CurrentCoMap()
        {
            var topology = CoTopology;
            if (topology.CoreCount == 0)
                return _coPerCore;

            return new Models.CustomPreset
            {
                CpuCoCcd0 = sdCoCcd0.Value,
                CpuCoCcd1 = sdCoCcd1.Value,
                CpuCoPerCore = _coPerCore
            }.BuildPerCoreMap(topology);
        }

        private void updateCoPerCoreSummary()
        {
            var map = CurrentCoMap();
            lblCoPerCore.Text = LocalizationService.Format(
                "{0} cores · {1} set",
                map.Length,
                map.Count(value => value != 0));
        }

        private void togDynamicBoost_Checked(object sender, RoutedEventArgs e)
        {
            spNvDbOffset.Visibility = Visibility.Visible;
        }

        private void togDynamicBoost_Unchecked(object sender, RoutedEventArgs e)
        {
            spNvDbOffset.Visibility = Visibility.Collapsed;
            sdNvDbOffset.Value = 0;
            updateAllLabels();
        }

        private void updateAllLabels()
        {
            // Labels live inside customScroll which may be collapsed (controls not yet realized).
            if (customScroll.Visibility != Visibility.Visible)
                return;

            lblSustained.Text = $"{sdSustained.Value:F0} W";
            lblBoost.Text = $"{sdBoost.Value:F0} W";
            lblTjMax.Text = $"{sdTjMax.Value:F0} °C";

            lblIntelPl1.Text = $"{sdIntelPl1.Value:F0} W";
            lblIntelPl2.Text = $"{sdIntelPl2.Value:F0} W";
            lblIntelVolt.Text = $"{sdIntelVolt.Value:F0} mV";

            lblCoCcd0.Text = $"{sdCoCcd0.Value:+0;-0;0}";
            lblCoCcd1.Text = $"{sdCoCcd1.Value:+0;-0;0}";
            lblCoUnified.Text = $"{sdCoUnified.Value:+0;-0;0}";

            lblNvPower.Text = $"{sdNvPower.Value:F0} W";
            lblNvDbOffset.Text = $"{sdNvDbOffset.Value:F0} W";
            // Show "Curve" only if curve is active AND user hasn't touched the slider.
            // If the user manually changes the slider, the curve is ignored for Apply.
            bool stockPreset = Services.PresetProfileApplier.IsBuiltInProfile(_currentProfile);
            lblNvCore.Text = (!stockPreset && VfCurveActive && !_userTouchedCoreSlider) ? "Curve" : $"{sdNvCore.Value:F0} MHz";
            lblNvMem.Text = $"{sdNvMem.Value:F0} MHz";
        }

        private async Task applyPlatformVisibilityAsync()
        {
            // Hide AMD-specific controls on Intel
            if (Family.TYPE == Family.ProcessorType.Intel)
            {
                sdCpuPower.Visibility = Visibility.Collapsed;

                // Status card: switch labels to Intel-appropriate names
                tbPl1Label.Text = "PL1";
                tbPl2Label.Text = "PL2";
                tbTjMaxLabel.Visibility = Visibility.Collapsed;
                tbTjMax.Visibility = Visibility.Collapsed;
            }
            // Hide Intel-specific controls on AMD
            else
            {
                sdIntelTuning.Visibility = Visibility.Collapsed;
            }

            // Voltage control is AMD-only (Curve Optimizer): hide the CO row in the status card and the
            // CO slider controls when EnableCpuVoltageControl is OFF. Intel has no voltage control at all.
            bool voltageEnabled = CpuVoltageEnabled && Family.TYPE != Family.ProcessorType.Intel;
            tbCoLabel.Visibility = voltageEnabled ? Visibility.Visible : Visibility.Collapsed;
            tbCoOffset.Visibility = voltageEnabled ? Visibility.Visible : Visibility.Collapsed;
            coControlsPanel?.SetValue(VisibilityProperty, voltageEnabled ? Visibility.Visible : Visibility.Collapsed);

            // NVIDIA visibility — check GPU inventory or fall back to NvTuning probe
            bool hasNvidia = false;
            if (_gpuInventory != null)
            {
                try
                {
                    var snapshot = await _gpuInventory.GetSnapshotAsync().ConfigureAwait(true);
                    hasNvidia = snapshot.NvidiaCount > 0;
                }
                catch
                {
                    // Intentional broad catch: GPU inventory may fail for any reason
                    // (driver not loaded, permissions, hardware absent). Fall through
                    // to the NvTuning probe as a secondary detection path.
                }
            }
            if (!hasNvidia)
            {
                hasNvidia = !string.IsNullOrEmpty(NvTuning.GetGpuName());
            }
            sdNvidia.Visibility = hasNvidia ? Visibility.Visible : Visibility.Collapsed;

            // The card is only as live as the EC's own power table: the cTGP offset is
            // measured from the board base TGP it publishes.
            if (hasNvidia)
                await applyGpuPowerGateAsync().ConfigureAwait(true);

            // The power-ceiling override is an NVIDIA driver key: no card without one.
            // The rest of the gate (Power boost mode) is applied on every render, because
            // the owner can switch it in Settings while this page lives on.
            _hasNvidia = hasNvidia;

            // Hide GPU clock controls when EnableGpuClockControl is OFF
            bool gpuClockEnabled = Settings.Default.EnableGpuClockControl;
            GpuClockControlsPanel.Visibility = gpuClockEnabled ? Visibility.Visible : Visibility.Collapsed;
            tbCoreLabel.Visibility = gpuClockEnabled ? Visibility.Visible : Visibility.Collapsed;
            tbCoreClock.Visibility = gpuClockEnabled ? Visibility.Visible : Visibility.Collapsed;
            tbMemLabel.Visibility = gpuClockEnabled ? Visibility.Visible : Visibility.Collapsed;
            tbMemClock.Visibility = gpuClockEnabled ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>
        /// Gates the GPU tuning card on the engine's answer: a cTGP offset is measured against
        /// the board's own base TGP (ECSpec), so without that table a request would be written
        /// from a guess. The card stays visible and readable with the reason either way - a
        /// control that disappears cannot explain itself. An unanswered read is unsupported.
        /// </summary>
        private async Task applyGpuPowerGateAsync()
        {
            UCU.Contracts.GpuPowerInfoDto? info = null;
            try
            {
                if (RyzenAdj_To_UCU.EcService is { } ec)
                {
                    info = await ec.GetGpuPowerInfoAsync().ConfigureAwait(true);
                }
            }
            catch (Exception ex)
            {
                DebugLog($"GPU power info unavailable: {ex.Message}");
            }

            _gpuPowerSupported = info?.Supported == true;
            sdNvidiaBody.IsEnabled = _gpuPowerSupported;
            sdNvidiaBody.Opacity = _gpuPowerSupported ? 1.0 : 0.55;
            tbGpuPowerReason.Visibility = _gpuPowerSupported ? Visibility.Collapsed : Visibility.Visible;

            if (_gpuPowerSupported)
            {
                return;
            }

            // Expanded as well as visible: the inert sliders and the reason are read together.
            sdNvidia.IsExpanded = true;
            tbGpuPowerReason.Text = LocalizationService.Get("GPU power control is not available on this machine.")
                + " " + RomReasonLine(string.IsNullOrWhiteSpace(info?.Reason)
                    ? LocalizationService.Get("The engine service is not available, so the current state could not be read.")
                    : info!.Reason);
        }

        private bool IsHydroConnected => _waterCooler?.IsConnected == true;

      /// <summary>
        /// Returns the resolved SKU power limits for the current cooling state, or null if no SKU matched.
        /// Falls back to hardcoded limits if SKU resolution failed.
        /// </summary>
        private Models.SkuPowerLimitsCooling? GetPowerLimits()
        {
            var limits = Services.SkuResolver.GetPowerLimits(IsHydroConnected);
            if (limits != null)
                return limits;

            // Fallback: hardcoded limits for known hardware
            bool hydro = IsHydroConnected;
            if (Family.TYPE == Family.ProcessorType.Amd_Apu)
            {
                // AMD Ryzen 9 9955HX/3D
                return new Models.SkuPowerLimitsCooling
                {
                    CpuPl1Sustained = hydro ? 160 : 130,
                    CpuPl2Boost = hydro ? 160 : 135,
                    GpuTgpBaseline = 150,
                    GpuTgpWithDb = 175,
                    CombinedCpuGpu = hydro ? 280 : 240,
                    DynamicBoostMax = 25
                };
            }
            else if (Family.TYPE == Family.ProcessorType.Intel)
            {
                // Intel Core Ultra 9 275HX
                return new Models.SkuPowerLimitsCooling
                {
                    CpuPl1Sustained = hydro ? 200 : 160,
                    CpuPl2Boost = hydro ? 200 : 200,
                    GpuTgpBaseline = 150,
                    GpuTgpWithDb = 175,
                    CombinedCpuGpu = hydro ? 280 : 240,
                    DynamicBoostMax = 25
                };
            }

            return null;
        }

        /// <summary>
        /// Updates a slider Maximum and adjusts the value if the range changed.
        /// If the max shrinks and the value exceeds it, clamps down.
        /// If the max grows and the value was at the old max, bumps to the new max.
        /// </summary>
        private static void applySliderMaxChange(Slider slider, double newMax)
        {
            double oldMax = slider.Maximum;
            double value = slider.Value;

            slider.Maximum = newMax;

            if (value > newMax)
            {
                slider.Value = newMax;
            }
            else if (newMax > oldMax && value >= oldMax)
            {
                slider.Value = newMax;
            }
        }

        private void applyPowerLimits()
        {
            var limits = GetPowerLimits();
            if (limits == null)
                return;

            var pl = limits;
            bool powerBoostMode = Settings.Default.EnablePowerBoostMode;

            // When Power boost mode is ON, raise the slider cap to the water-cooling limits
            // (same limits that Hydro UI connection would unlock), instead of the EC maximum.
            // AMD: 160W/160W, Intel: 255W/255W.
            int boostPl1 = Family.TYPE == Family.ProcessorType.Amd_Apu ? 160 : 255;
            int boostPl2 = Family.TYPE == Family.ProcessorType.Amd_Apu ? 160 : 255;

            if (Family.TYPE == Family.ProcessorType.Amd_Apu)
            {
                applySliderMaxChange(sdSustained, powerBoostMode ? boostPl1 : pl.CpuPl1Sustained);
                applySliderMaxChange(sdBoost, powerBoostMode ? boostPl2 : pl.CpuPl2Boost);
            }
            else if (Family.TYPE == Family.ProcessorType.Intel)
            {
                applySliderMaxChange(sdIntelPl1, powerBoostMode ? boostPl1 : pl.CpuPl1Sustained);
                applySliderMaxChange(sdIntelPl2, powerBoostMode ? boostPl2 : pl.CpuPl2Boost);
            }

            SetGpuSliderLimits(pl.GpuTgpBaseline);

            // Dynamic Boost offset — max is the DB max from SKU config
            sdNvDbOffset.Maximum = pl.DynamicBoostMax;
        }

        /// <summary>
        /// Clamps slider values to current limits after profile loading.
        /// If the slider max grew (Power Boost enabled) and the value was at the old max,
        /// bumps it to the new max so the label and thumb stay in sync.
        /// </summary>
        private void clampSlidersToLimits()
        {
            var limits = GetPowerLimits();
            if (limits == null)
                return;

            var pl = limits;
            bool powerBoostMode = Settings.Default.EnablePowerBoostMode;

            // CPU sliders
            int boostPl1 = Family.TYPE == Family.ProcessorType.Amd_Apu ? 160 : 255;
            int boostPl2 = Family.TYPE == Family.ProcessorType.Amd_Apu ? 160 : 255;

            if (Family.TYPE == Family.ProcessorType.Amd_Apu)
            {
                int effectiveMax = powerBoostMode ? boostPl1 : pl.CpuPl1Sustained;
                if (sdSustained.Value > effectiveMax) sdSustained.Value = effectiveMax;
                if (powerBoostMode && sdSustained.Value == pl.CpuPl1Sustained) sdSustained.Value = boostPl1;

                effectiveMax = powerBoostMode ? boostPl2 : pl.CpuPl2Boost;
                if (sdBoost.Value > effectiveMax) sdBoost.Value = effectiveMax;
                if (powerBoostMode && sdBoost.Value == pl.CpuPl2Boost) sdBoost.Value = boostPl2;
            }
            else if (Family.TYPE == Family.ProcessorType.Intel)
            {
                int effectiveMax = powerBoostMode ? boostPl1 : pl.CpuPl1Sustained;
                if (sdIntelPl1.Value > effectiveMax) sdIntelPl1.Value = effectiveMax;
                if (powerBoostMode && sdIntelPl1.Value == pl.CpuPl1Sustained) sdIntelPl1.Value = boostPl1;

                effectiveMax = powerBoostMode ? boostPl2 : pl.CpuPl2Boost;
                if (sdIntelPl2.Value > effectiveMax) sdIntelPl2.Value = effectiveMax;
                if (powerBoostMode && sdIntelPl2.Value == pl.CpuPl2Boost) sdIntelPl2.Value = boostPl2;
            }

            // GPU TGP slider — clamp to current max.
            if (sdNvPower.Value > sdNvPower.Maximum)
            {
                sdNvPower.Value = sdNvPower.Maximum;
            }
        }

        private void SetGpuSliderLimits(int fallbackMax)
        {
            string gpuName = NvTuning.GetGpuName() ?? string.Empty;
            bool boostEnabled = Settings.Default.EnablePowerBoostMode;
            (int min, int max) = gpuName.ToLowerInvariant() switch
            {
                var n when n.Contains("rtx 5090") => (95, boostEnabled ? 250 : 150),
                var n when n.Contains("rtx 5080") => (80, boostEnabled ? 250 : 150),
                var n when n.Contains("rtx 5070") => (60, boostEnabled ? 250 : 115),
                _ => (50, fallbackMax)
            };

            // The ceiling the boost path writes is 225 W (250 W minus Dynamic Boost's 25 W):
            // a slider that offers more writes an offset the EC ignores.
            sdNvPower.Minimum = min;
            sdNvPower.Maximum = Math.Min(max, NvTuning.MaxPowerLimitWatts);
            max = (int)sdNvPower.Maximum;

            // Clamp current value to new range if above max.
            // The upward bump (max grows, value was at old max) is handled
            // separately in clampSlidersToLimits() which runs after profile loading.
            if (sdNvPower.Value > max) sdNvPower.Value = max;
            if (sdNvPower.Value < min) sdNvPower.Value = min;

            // Hide Dynamic Boost controls when Power boost mode is ON
            bool dbVisible = !boostEnabled;
            DynamicBoostPanel.Visibility = dbVisible ? Visibility.Visible : Visibility.Collapsed;
            tbDbLabel.Visibility = dbVisible ? Visibility.Visible : Visibility.Collapsed;
            tbGpuDb.Visibility = dbVisible ? Visibility.Visible : Visibility.Collapsed;

            UpdatePresetModeLabel(boostEnabled);
        }

        /// <summary>
        /// Updates the preset mode label in the bottom bar and the boost accent stroke.
        /// Shows "Boost" when Power boost mode is ON, hides the label otherwise.
        /// Also shows/hides the bottom bar accent stroke.
        /// </summary>
        private void UpdatePresetModeLabel(bool boostEnabled)
        {
            if (lblPresetMode != null)
            {
                lblPresetMode.Text = boostEnabled ? "Power Boost ON" : "";
                lblPresetMode.Visibility = boostEnabled ? Visibility.Visible : Visibility.Collapsed;
            }

            // Show/hide bottom bar accent stroke
            if (bottomBar != null)
            {
                if (boostEnabled)
                {
                    bottomBar.BorderBrush = (System.Windows.Media.Brush)FindResource("AccentFillColorDefaultBrush");
                    bottomBar.BorderThickness = new Thickness(0, 0, 0, 2);
                }
                else
                {
                    bottomBar.BorderBrush = null;
                    bottomBar.BorderThickness = new Thickness(0);
                }
            }
        }

        private void setProfileTooltips()
        {
            bool hydro = IsHydroConnected;
            string coolingLabel = hydro ? " (water)" : " (air)";
            var limits = GetPowerLimits();
            bool gpuBoostEnabled = Settings.Default.EnablePowerBoostMode;

            // Try to get SKU-specific profile data for tooltips
            Models.SkuProfileEntry? officeProfile = Services.SkuResolver.GetProfile("office");
            Models.SkuProfileEntry? gamingProfile = Services.SkuResolver.GetProfile("gaming");
            Models.SkuProfileEntry? turboProfile = Services.SkuResolver.GetProfile("turbo");

            // Build GPU tooltip text — hide Dynamic Boost mention entirely when Power boost mode is ON
            static string GpuTooltip(Models.SkuProfileEntry? profile, bool boostEnabled)
            {
                if (profile == null)
                    return "TGP: 150W";

                int tgp = profile.GpuTgp;
                bool db = profile.DynamicBoost;
                int dbOffset = profile.DynamicBoostOffset;

                if (boostEnabled)
                {
                    // Merge TGP + DB into pure TGP, no DB mention
                    if (db)
                        tgp += dbOffset;
                    return $"TGP: {tgp}W";
                }

                return db
                    ? $"TGP: {tgp}W | Dynamic Boost: On (+{dbOffset}W)"
                    : $"TGP: {tgp}W | Dynamic Boost: Off";
            }

            if (Family.TYPE == Family.ProcessorType.Amd_Apu)
            {
                int cpuMax = limits?.CpuPl1Sustained ?? 135;

                string officeCpu = officeProfile != null
                    ? $"Sustained: {officeProfile.CpuPl1}W | Boost: {officeProfile.CpuPl2}W"
                    : "Sustained: 45W | Boost: 45W";
                string officeGpu = GpuTooltip(officeProfile, gpuBoostEnabled);

                string gamingCpu = gamingProfile != null
                    ? $"Sustained: {gamingProfile.CpuPl1}W | Boost: {gamingProfile.CpuPl2}W"
                    : "Sustained: 75W | Boost: 85W";
                string gamingGpu = GpuTooltip(gamingProfile, gpuBoostEnabled);

                string turboGpu = GpuTooltip(turboProfile, gpuBoostEnabled);

                btnOffice.ToolTip = CreateTooltip("Office", "CPU Power Limits", officeCpu, "GPU Power Limits", officeGpu);
                btnGaming.ToolTip = CreateTooltip("Gaming", "CPU Power Limits", gamingCpu, "GPU Power Limits", gamingGpu);
                btnTurbo.ToolTip = CreateTooltip("Turbo",
                    "CPU Power Limits", $"Sustained: {cpuMax}W | Boost: {cpuMax}W{coolingLabel}",
                    "GPU Power Limits", turboGpu);

                btnCustom.ToolTip = "Apply your own custom power settings using the sliders below.";
            }
            else if (Family.TYPE == Family.ProcessorType.Intel)
            {
                string officeCpu = officeProfile != null
                    ? $"PL1: {officeProfile.CpuPl1}W | PL2: {officeProfile.CpuPl2}W"
                    : "PL1: 25W | PL2: 35W";
                string officeGpu = GpuTooltip(officeProfile, gpuBoostEnabled);

                string gamingCpu = gamingProfile != null
                    ? $"PL1: {gamingProfile.CpuPl1}W | PL2: {gamingProfile.CpuPl2}W"
                    : "PL1: 45W | PL2: 60W";
                string gamingGpu = GpuTooltip(gamingProfile, gpuBoostEnabled);

                string turboCpu = turboProfile != null
                    ? $"PL1: {turboProfile.CpuPl1}W | PL2: {turboProfile.CpuPl2}W"
                    : "PL1: 160W | PL2: 200W";
                string turboGpu = GpuTooltip(turboProfile, gpuBoostEnabled);

                btnOffice.ToolTip = CreateTooltip("Office", "CPU Power Limits", officeCpu, "GPU Power Limits", officeGpu);
                btnGaming.ToolTip = CreateTooltip("Gaming", "CPU Power Limits", gamingCpu, "GPU Power Limits", gamingGpu);
                btnTurbo.ToolTip = CreateTooltip("Turbo",
                    "CPU Power Limits", turboCpu,
                    "GPU Power Limits", turboGpu);

                btnCustom.ToolTip = "Apply your own custom power settings using the sliders below.";
            }
            else
            {
                btnOffice.ToolTip = "Office power profile — low power, quiet operation";
                btnGaming.ToolTip = "Gaming power profile — balanced performance";
                btnTurbo.ToolTip = "Turbo power profile — maximum performance";
                btnCustom.ToolTip = "Apply your own custom power settings using the sliders below.";
            }
        }

        private static string CreateTooltip(string profile,
            string cpuSection, string cpuValues,
            string gpuSection, string gpuValues)
        {
            return $"{profile}\n\n{cpuSection}\n{cpuValues}\n\n{gpuSection}\n{gpuValues}";
        }

        public void loadSavedProfile()
        {
            // Restore last selected profile.
            // Try the new string property first; fall back to the legacy int property.
            string saved = Settings.Default.presetProfile;
            if (string.IsNullOrEmpty(saved))
            {
                // Legacy premadePreset: 0=Eco(Office), 1=Balanced(Gaming), 2=Performance, 3=Extreme(Turbo)
                int legacy = Settings.Default.premadePreset;
                saved = legacy switch
                {
                    0 => "Office",
                    1 => "Gaming",
                    2 => "Gaming",
                    3 => "Turbo",
                    _ => "Gaming"
                };
            }

            // Handle custom preset names saved by the tray widget (e.g. "Custom 2", "Custom 3 (Boost)")
            if (Services.TrayPresetOptions.IsCustomPreset(saved))
            {
                DebugLog($"loadSavedProfile: saved='{saved}', ActivePresetSlot={ActivePresetSlot}, customPresetSlot={Settings.Default.customPresetSlot}, customPresetSlotBoost={Settings.Default.customPresetSlotBoost}");

                // Parse slot from "Custom N" or "Custom N (Boost)"
                var parts = saved.Replace("(Boost)", "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && int.TryParse(parts[1], out int parsed))
                {
                    int slot = Math.Clamp(parsed - 1, 0, Models.CustomPreset.MaxSlots - 1);
                    // Only overwrite ActivePresetSlot when the saved name contains an explicit slot number.
                    // When saved is just "Custom", the slot is already persisted in customPresetSlot/customPresetSlotBoost.
                    ActivePresetSlot = slot;
                    DebugLog($"loadSavedProfile: Parsed slot {slot} from '{saved}'");
                }

                selectProfile("Custom", apply: false);
            }
            else
            {
                selectProfile(saved, apply: false);
            }
        }

        #region Profile Selection

        private void btnOffice_Click(object sender, RoutedEventArgs e) => selectProfile("Office", apply: true);
        private void btnGaming_Click(object sender, RoutedEventArgs e) => selectProfile("Gaming", apply: true);
        private void btnTurbo_Click(object sender, RoutedEventArgs e) => selectProfile("Turbo", apply: true);
        private void btnCustom_Click(object sender, RoutedEventArgs e)
            {
                selectProfile("Custom", apply: false);
            }

        private async void selectProfile(string profile, bool apply = false)
        {
            string oldProfile = _currentProfile;

            // Save Custom slider values before switching away — setProfileValues below
            // will overwrite the sliders with the new profile's defaults.
            if (oldProfile == "Custom" && profile != "Custom")
            {
                SaveCustomPreset(_customPresetSlot);
            }

            _currentProfile = profile;

            // Update button styles
            foreach (var btn in new[] { btnOffice, btnGaming, btnTurbo, btnCustom })
                btn.Style = FindResource("ProfileButtonStyle") as Style;

            Style? activeStyle = FindResource("ProfileButtonActiveStyle") as Style;
            if (profile == "Office") btnOffice.Style = activeStyle;
            else if (profile == "Gaming") btnGaming.Style = activeStyle;
            else if (profile == "Turbo") btnTurbo.Style = activeStyle;
            else if (profile == "Custom") btnCustom.Style = activeStyle;

            // Show/hide custom panel and bottom bar
            customScroll.Visibility = profile == "Custom" ? Visibility.Visible : Visibility.Collapsed;
            bottomBar.Visibility = profile == "Custom" ? Visibility.Visible : Visibility.Collapsed;

            // Set profile values from SKU database (falls back to hardcoded defaults)
            var limits = GetPowerLimits();
            int cpuMax = limits?.CpuPl1Sustained ?? 135;
            string profileKey = profile.ToLowerInvariant();
            var skuProfile = Services.SkuResolver.GetProfile(profileKey);

            // When Power boost mode is ON, merge TGP + DB offset into a single TGP value
            // and disable Dynamic Boost (the DB UI controls are hidden in boost mode).
            bool gpuBoostEnabled = Settings.Default.EnablePowerBoostMode;

            if (profile == "Office")
            {
                if (skuProfile != null)
                {
                    int tgp = skuProfile.GpuTgp;
                    bool db = skuProfile.DynamicBoost;
                    int dbOffset = skuProfile.DynamicBoostOffset;
                    if (gpuBoostEnabled && db)
                    {
                        tgp += dbOffset;
                        db = false;
                        dbOffset = 0;
                    }
                    setProfileValues(skuProfile.CpuPl1, skuProfile.CpuPl2, 95,
                        tgp, db, dbOffset);
                }
                else
                {
                    setProfileValues(45, 45, 95, 95, false, 0);
                }
                int intelPl1 = skuProfile?.CpuPl1 ?? 25;
                int intelPl2 = skuProfile?.CpuPl2 ?? 35;
                _profileIntelPl1 = intelPl1;
                _profileIntelPl2 = intelPl2;
                sdIntelPl1.Value = intelPl1;
                sdIntelPl2.Value = intelPl2;
            }
            else if (profile == "Gaming")
            {
                if (skuProfile != null)
                {
                    int tgp = skuProfile.GpuTgp;
                    bool db = skuProfile.DynamicBoost;
                    int dbOffset = skuProfile.DynamicBoostOffset;
                    if (gpuBoostEnabled && db)
                    {
                        tgp += dbOffset;
                        db = false;
                        dbOffset = 0;
                    }
                    setProfileValues(skuProfile.CpuPl1, skuProfile.CpuPl2, 95,
                        tgp, db, dbOffset);
                }
                else
                {
                    setProfileValues(75, 85, 95, 150, false, 0);
                }
                int intelPl1 = skuProfile?.CpuPl1 ?? 45;
                int intelPl2 = skuProfile?.CpuPl2 ?? 60;
                _profileIntelPl1 = intelPl1;
                _profileIntelPl2 = intelPl2;
                sdIntelPl1.Value = intelPl1;
                sdIntelPl2.Value = intelPl2;
            }
            else if (profile == "Turbo")
            {
                int gpuTgp = skuProfile?.GpuTgp ?? 150;
                bool db = skuProfile?.DynamicBoost ?? true;
                int dbOffset = skuProfile?.DynamicBoostOffset ?? 25;
                if (gpuBoostEnabled && db)
                {
                    gpuTgp += dbOffset;
                    db = false;
                    dbOffset = 0;
                }

                if (Family.TYPE == Family.ProcessorType.Amd_Apu)
                {
                    setProfileValues(cpuMax, cpuMax, 95, gpuTgp, db, dbOffset);
                }
                else
                {
                    _profileIntelPl1 = cpuMax;
                    _profileIntelPl2 = cpuMax;
                    sdIntelPl1.Value = cpuMax;
                    sdIntelPl2.Value = cpuMax;
                    setProfileValues(cpuMax, cpuMax, 95, gpuTgp, db, dbOffset);
                }
            }
            // Custom: restore from CustomPresetStore slot and update labels
            if (profile == "Custom")
            {
                // Sync slot to the active mode's saved slot before loading.
                // This ensures the correct slot is used even on first navigation
                // (before Page_Loaded runs which also syncs the slot).
                _customPresetSlot = ActivePresetSlot;
                if (_customPresetSlot < 0 || _customPresetSlot >= Models.CustomPreset.MaxSlots)
                    _customPresetSlot = 0;

                DebugLog($"selectProfile Custom: _customPresetSlot={_customPresetSlot}, ActivePresetSlot={ActivePresetSlot}, customPresetSlot={Settings.Default.customPresetSlot}");

                LoadCustomPreset(_customPresetSlot);
                updateAllLabels();

                // Page_Loaded only fires once, so a profile switch — including one
                // driven by the Performance button — has to move the checked slot too.
                var slotButtons = new RadioButton[] { rbProfile1, rbProfile2, rbProfile3, rbProfile4, rbProfile5 };
                for (int i = 0; i < slotButtons.Length; i++)
                    slotButtons[i].IsChecked = i == _customPresetSlot;

                updateRadioButtonStyles();
            }

            // Update status display
            updateStatus();

            // Clamp slider values to current limits after profile values are set.
            // Handles Power Boost mode changes: if boost is ON and a profile value
            // equals the non-boost max, bump to the boost max.
            clampSlidersToLimits();

            // Save selection
            Settings.Default.presetProfile = profile;
            if (profile == "Custom")
            {
                ActivePresetSlot = _customPresetSlot;
            }
            Settings.Default.Save();

            // Only apply when explicitly requested (button click), not on page load
            if (apply)
            {
                DebugLog($"selectProfile: {profile} — apply");

                // Handle fan curve persistence when switching profiles
                if (_deviceApplier is not null)
                {
                    if (profile == "Custom")
                    {
                        // Check if the active custom preset slot has fan settings stored.
                        var customPresets = Models.CustomPresetStore.Load(ActivePresetMode);
                        bool presetHasFanSettings = _customPresetSlot >= 0 &&
                            _customPresetSlot < customPresets.Length &&
                            customPresets[_customPresetSlot].HasFanChanges;

                        if (presetHasFanSettings)
                        {
                            // Apply fan settings from the custom preset slot
                            PresetProfileApplier.ApplyFanSettingsFromPreset(customPresets[_customPresetSlot]);
                            DebugLog($"selectProfile: Applied fan settings from Custom preset slot {_customPresetSlot + 1}");
                        }
                        else if (_savedCustomFanSettings is not null)
                        {
                            // Fallback: restore saved Custom fan settings and apply to EC
                            FanControlSettingsService.Save(_savedCustomFanSettings);
                            DebugLog($"selectProfile: Restored and applying saved Custom fan settings");
                            await ApplyCustomFanCurveFromSettingsAsync(_savedCustomFanSettings).ConfigureAwait(true);
                        }
                        else
                        {
                            // No saved Custom fan settings — apply default Balanced curve
                            DebugLog($"selectProfile: No saved Custom fan settings, applying default Balanced");
                            await ApplyFanCurveFromPresetAsync("Balanced").ConfigureAwait(true);
                        }
                    }
                    else if (profile != "Custom")
                    {
                        // Save current fan settings before switching away from Custom
                        if (oldProfile == "Custom")
                        {
                            _savedCustomFanSettings = FanControlSettingsService.Load();
                            DebugLog($"selectProfile: Saved Custom fan settings before switching to {profile}");
                        }

                        // Stock presets leave fans to the firmware's per-mode table:
                        // ApplyOemStockMode clears 0x727 bit6, which makes the EC ignore
                        // custom fan tables and hands the OEM mode engine back. Writing a
                        // mapped curve here would re-arm manual fan control instead.
                        DebugLog($"selectProfile: {profile} is a stock preset — fan curves left to the firmware");
                    }
                }

                _ = applyProfileAsync();

                // The page's slot-aware apply runs outside the arbiter's pipeline
                // (sliders, fan-slot bookkeeping, V-F state are page-local); record it
                // so the arbiter's debounce and priority bookkeeping stays honest and
                // automation edges racing a user click see the click as the last truth.
                App.GetService<PresetArbiter>()?.RecordApply(PresetIntentSource.UserInterface, profile);
            }
            else
            {
                DebugLog($"selectProfile: {profile} — no apply (page load / Custom)");
            }
        }

        private void setProfileValues(int sustained, int boost, int tjMax, int gpuTgp,
            bool gpuDb, int gpuDbOffset, int coCcd0 = 0, int coCcd1 = 0)
        {
            // Store values for status card (independent of slider UI state)
            _profileSustained = sustained;
            _profileBoost = boost;
            _profileTjMax = tjMax;
            _profileCoCcd0 = coCcd0;
            _profileCoCcd1 = coCcd1;
            _profileGpuTgp = gpuTgp;
            _profileGpuDb = gpuDb;
            _profileGpuDbOffset = gpuDbOffset;

            // SMU values
            sdSustained.Value = sustained;
            sdBoost.Value = boost;
            sdTjMax.Value = tjMax;

            // Curve Optimizer
            sdCoCcd0.Value = coCcd0;
            sdCoCcd1.Value = coCcd1;
            sdCoUnified.Value = 0;

            // GPU
            sdNvPower.Value = gpuTgp;
            togDynamicBoost.IsChecked = gpuDb;
            sdNvDbOffset.Value = Math.Max(0, gpuDbOffset);
            spNvDbOffset.Visibility = gpuDb ? Visibility.Visible : Visibility.Collapsed;

            // Stock presets own no GPU clock offsets: clearing here keeps the Custom
            // preset's core/mem offsets from leaking into a stock apply.
            sdNvCore.Value = 0;
            sdNvMem.Value = 0;

            // Reset the flag when loading profile values — start fresh.
            _userTouchedCoreSlider = false;

            // Update labels
            updateAllLabels();
        }

        private void updateStatus()
        {
            // Guard: UI may not be ready yet (e.g., called from OnNavigatedToAsync before layout)
            if (sdNvidia == null || tbPl1 == null)
                return;

            // Update GPU clock offset display from slider values
            if (sdNvidia.Visibility == Visibility.Visible && Settings.Default.EnableGpuClockControl)
            {
                string coreText;
                if (!Services.PresetProfileApplier.IsBuiltInProfile(_currentProfile)
                    && VfCurveActive && !_userTouchedCoreSlider)
                {
                    coreText = "Curve";
                }
                else
                {
                    coreText = $"{sdNvCore.Value:F0}";
                }
                tbCoreClock.Text = coreText;
                tbMemClock.Text = $"{sdNvMem.Value:F0}";
            }
            else
            {
                tbCoreClock.Text = "--";
                tbMemClock.Text = "--";
            }

            if (Family.TYPE == Family.ProcessorType.Amd_Apu)
            {
                tbPl1.Text = $"{_profileSustained:F0} W";
                tbPl2.Text = $"{_profileBoost:F0} W";
                tbTjMax.Text = $"{_profileTjMax} \u00B0C";
                if (CpuVoltageEnabled)
                {
                    if (rbCoUnified.IsChecked == true)
                    {
                        tbCoOffset.Text = $"{(int)sdCoUnified.Value:+0;-0;0}";
                    }
                    else if (rbCoPerCore.IsChecked == true)
                    {
                        var map = CurrentCoMap();
                        tbCoOffset.Text = map.Length > 0
                            ? $"{map.Min():+0;-0;0}..{map.Max():+0;-0;0}"
                            : $"{_profileCoCcd0:+0;-0;0} | {_profileCoCcd1:+0;-0;0}";
                    }
                    else
                    {
                        tbCoOffset.Text = $"{_profileCoCcd0:+0;-0;0} | {_profileCoCcd1:+0;-0;0}";
                    }
                }
                tbGpuTgp.Text = $"{_profileGpuTgp:F0} W";
                tbGpuDb.Text = _profileGpuDb ? $"+{_profileGpuDbOffset:F0} W" : "Off";
            }
            else if (Family.TYPE == Family.ProcessorType.Intel)
            {
                tbPl1.Text = $"{_profileIntelPl1:F0} W";
                tbPl2.Text = $"{_profileIntelPl2:F0} W";
                tbGpuTgp.Text = $"{_profileGpuTgp:F0} W";
                tbGpuDb.Text = _profileGpuDb ? $"+{_profileGpuDbOffset:F0} W" : "Off";
            }

            ShowEffectiveBatteryLimits();
        }

        /// <summary>
        /// On battery the firmware grants only its own ceiling and the app clamps
        /// its requests to the measured envelope (BatteryPowerPolicy), so the card
        /// must show what will actually be applied - "160 W -> 40 W" - instead of
        /// the preset's AC numbers. Field, 2026-11-02: Turbo showed 160 W SPL/FPL
        /// and 150 W TGP while the hardware sat at ~35/40 W.
        /// </summary>
        private void ShowEffectiveBatteryLimits()
        {
            if (System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus
                != System.Windows.Forms.PowerLineStatus.Offline)
            {
                return;
            }

            bool intel = Family.TYPE == Family.ProcessorType.Intel;
            UCU.Core.Power.BatteryPowerPolicy.Envelope envelope =
                UCU.Core.Power.BatteryPowerPolicy.EnvelopeFor(intel);

            int requestedPl1 = (int)(intel ? _profileIntelPl1 : _profileSustained);
            int requestedPl2 = (int)(intel ? _profileIntelPl2 : _profileBoost);
            UCU.Core.Power.BatteryPowerPolicy.Verdict cpu =
                UCU.Core.Power.BatteryPowerPolicy.ClampCpu(requestedPl1, requestedPl2, onBattery: true, envelope.CpuWatts);
            UCU.Core.Power.BatteryPowerPolicy.GpuVerdict gpu =
                UCU.Core.Power.BatteryPowerPolicy.ClampGpu(
                    (int)_profileGpuTgp,
                    _profileGpuDb ? (int)_profileGpuDbOffset : 0,
                    onBattery: true,
                    envelope.GpuWatts);

            tbPl1.Text = EffectiveWatt(requestedPl1, cpu.Pl1);
            tbPl2.Text = EffectiveWatt(requestedPl2, cpu.Pl2);
            tbGpuTgp.Text = EffectiveWatt((int)_profileGpuTgp, gpu.TgpWatts);
            tbGpuDb.Text = _profileGpuDb
                ? (gpu.DbWatts > 0 ? $"+{gpu.DbWatts} W" : $"+{_profileGpuDbOffset:F0} W \u2192 Off")
                : "Off";
        }

        /// <summary>"40 W" when the request stands, "160 W → 40 W" when clamped.</summary>
        private static string EffectiveWatt(int requested, int applied) =>
            requested == applied ? $"{applied} W" : $"{requested} W \u2192 {applied} W";

        #endregion

        #region Apply

        private async Task applyProfileAsync()
        {
            try
            {
                string command = buildCommandString();
                if (string.IsNullOrWhiteSpace(command))
                {
                    DebugLog($"applyProfile: command is empty, aborting");
                    return;
                }

                DebugLog($"applyProfile: {_currentProfile} profile");
                DebugLog($"  command = {command}");
                _logger.LogInformation("Applying {CurrentProfile} profile: {Command}", _currentProfile, command);

                // TranslateAsync() runs SMU commands on a background thread via Task.Run().
                // We must await it before writing EC registers, otherwise the EC writes
                // complete first and the SMU overwrites the ceiling after.
                await RyzenAdj_To_UCU.TranslateAsync(command, appliedName: _currentProfile + " Profile", localizeAppliedName: false);
                DebugLog($"  TranslateAsync completed");

                // Stock presets are applied the OpenRevo way: assert the OEM mode
                // engine (mode index + trigger + the 0x045B flag) and clear the
                // custom power/profile enable bits, which yields CPU limits, GPU
                // TGP and fans to the firmware's own per-mode factory table. Runs
                // before anything else EC-side so no stale GPU ceiling can be left
                // behind. 0x0741 bit0 stays armed: the EC only raises the
                // AcpiTest_EventULong press echo while manual control is set, which
                // is what keeps the Performance button live under a Custom preset.
                if (PresetProfileApplier.IsBuiltInProfile(_currentProfile))
                {
                    var ecGate = RyzenAdj_To_UCU.EcService;
                    if (ecGate != null)
                    {
                        await ecGate.ApplyOemStockModeAsync(PresetProfileApplier.OemModeIndexFor(_currentProfile)).ConfigureAwait(true);

                        // The contract yields with the values (U-107): the
                        // guardian must not heal custom bytes over a stock
                        // preset that yields power to the firmware.
                        Services.PowerContractStore.ClearAll();
                    }
                }

                // Apply V-F curve offsets only on Custom profile.
                // Stock profiles (Office/Gaming/Turbo) always use the slider values (0 MHz),
                // even if a curve exists on disk from a previous Custom session.
                DebugLog($"  V-F curve check: VfCurveActive={VfCurveActive}, _userTouchedCoreSlider={_userTouchedCoreSlider}, _customPresetSlot={_customPresetSlot}, _currentProfile={_currentProfile}");
                if (_currentProfile == "Custom" && VfCurveActive && !_userTouchedCoreSlider)
                {
                    var customPresets = Models.CustomPresetStore.Load(ActivePresetMode);
                    var offsets = _customPresetSlot >= 0 && _customPresetSlot < customPresets.Length
                        ? customPresets[_customPresetSlot].VfCurveOffsets : new Dictionary<int, int>();
                    DebugLog($"  V-F curve: slot={_customPresetSlot + 1}, offsets.Count={offsets.Count}");
                    if (offsets.Count > 0)
                    {
                        // Apply EC power limit BEFORE the curve write. The NVIDIA driver
                        // on Blackwell laptops may check the EC power ceiling when applying
                        // VF offsets and further reduce power if it sees a low ceiling.
                        int tgp = (int)sdNvPower.Value;
                        bool dbEnabled = togDynamicBoost.IsChecked == true;
                        int dbOffset = dbEnabled ? (int)sdNvDbOffset.Value : 0;
                        NvTuning.SetPowerLimit(tgp + dbOffset, dbOffset);
                        DebugLog($"  V-F curve: pre-apply power limit: {tgp + dbOffset}W, dbOffset={dbOffset}W");

                        int result = NvTuning.ApplyVfCurveOffsets_ClockBoostTable(offsets, MemoryOffsetForCurve());
                        DebugLog($"  V-F curve applied: {(result > 0 ? "OK" : "failed")}");

                        // Also re-apply AFTER in case the driver overrides the EC ceiling.
                        NvTuning.SetPowerLimit(tgp + dbOffset, dbOffset);
                        DebugLog($"  V-F curve: post-apply power limit re-applied: {tgp + dbOffset}W, dbOffset={dbOffset}W");

                        // Post-apply: read live curve to verify GPU actually changed.
                        if (NvTuning.TryGetVfCurve(out var liveCurve))
                        {
                            // Log a few representative points (first, middle, last from editor range).
                            var editorIndices = offsets.Keys.OrderBy(k => k).ToList();
                            if (editorIndices.Count >= 3)
                            {
                                int[] sampleIndices = new[] { editorIndices[0], editorIndices[editorIndices.Count / 2], editorIndices[^1] };
                                foreach (int idx in sampleIndices)
                                {
                                    var point = liveCurve.FirstOrDefault(p => p.Index == idx);
                                    int offset = offsets.GetValueOrDefault(idx, 0);
                                    DebugLog($"  Live check idx={idx}: freq={point.FrequencyMHz}MHz @ {point.VoltageMv}mV, offset={offset}MHz");
                                }
                            }
                        }
                    }
                }

                // Apply EC CPU power limits AFTER SMU commands, for Custom presets
                // only — stock presets yield PL1/PL2/PL4 to the EC's native factory
                // table (OpenRevo's approach).
                if (Family.TYPE == Family.ProcessorType.Amd_Apu && !PresetProfileApplier.IsBuiltInProfile(_currentProfile))
                {
                    var ec = RyzenAdj_To_UCU.EcService;
                    if (ec != null)
                    {
                        int pl1 = (int)sdSustained.Value;
                        int pl2 = (int)sdBoost.Value;
                        DebugLog($"  Applying EC CPU power limits: PL1={pl1}W PL2={pl2}W");
                        await ec.ApplyCpuPowerLimitsAsync(pl1, pl2).ConfigureAwait(true);
                        Services.PowerContractStore.NoteCpuApplied(pl1, pl2);
                        DebugLog($"  EC CPU power limits applied");

                        // TjMax is now handled via SMU path (--tctl-temp) in buildCommandString().
                        // The EC register 0x0786 write was causing a 20W TDP lockup.
                    }
                    else
                    {
                        DebugLog($"  EC service unavailable - CPU power limits may be clamped");
                    }
                }

                if (_currentProfile == "Custom" && _customPresetSlot >= 0)
                {
                    // Name the slot like the other custom-apply paths do ("Custom 2 Applied"),
                    // instead of the generic "Custom Profile Applied".
                    string customTemplate = ActivePresetMode == Models.PresetMode.Boost
                        ? "Custom {0} (Boost) Applied"
                        : "Custom {0} Applied";
                    ToastNotification.ShowToastNotification(LocalizationService.Format(customTemplate, _customPresetSlot + 1),
                        accent: ToastNotification.AccentFor(customTemplate, (_customPresetSlot + 1).ToString()));
                }
                else
                {
                    ToastNotification.ShowToastNotification(LocalizationService.Format("{0} Applied", _currentProfile),
                        accent: _currentProfile);
                }

                // Do NOT save to Settings.Default.CommandString.
                // That field is shared with Custom Presets / Adaptive Mode auto-reapply.
                // Saving here causes the auto-reapply timer to re-apply stale SMU commands
                // that override EC power limits from this page.

                // For Custom profile, update stored values from sliders so the status
                // card reflects what was just applied (not whatever was loaded on startup).
                // Also apply fan control settings stored in the custom preset.
                if (_currentProfile == "Custom")
                {
                    _profileSustained = sdSustained.Value;
                    _profileBoost = sdBoost.Value;
                    _profileTjMax = (int)sdTjMax.Value;
                    _profileCoCcd0 = (int)sdCoCcd0.Value;
                    _profileCoCcd1 = (int)sdCoCcd1.Value;
                    _profileIntelPl1 = sdIntelPl1.Value;
                    _profileIntelPl2 = sdIntelPl2.Value;
                     _profileGpuTgp = sdNvPower.Value;
                    _profileGpuDb = togDynamicBoost.IsChecked == true;
                    _profileGpuDbOffset = sdNvDbOffset.Value;

                    // Apply fan control settings from the saved custom preset slot.
                    var customPresets = Models.CustomPresetStore.Load(ActivePresetMode);
                    if (_customPresetSlot >= 0 && _customPresetSlot < customPresets.Length)
                    {
                        PresetProfileApplier.ApplyFanSettingsFromPreset(customPresets[_customPresetSlot]);
                    }
                }

                // Update status after apply
                updateStatus();
            }
            catch (Exception ex)
            {
                // Broad catch: applyProfileAsync spans SMU commands, EC writes,
                // NVIDIA driver calls, and V-F curve operations — any layer can throw.
                DebugLog($"applyProfile EXCEPTION: {ex.GetType().Name}: {ex.Message}");
                _logger.LogError(ex, "Failed to apply {CurrentProfile} profile", _currentProfile);
            }
        }

        private void SaveCustomValues()
        {
            Settings.Default.customSustained = sdSustained.Value;
            Settings.Default.customBoost = sdBoost.Value;
            Settings.Default.customTjMax = sdTjMax.Value;
            Settings.Default.customIntelPl1 = sdIntelPl1.Value;
            Settings.Default.customIntelPl2 = sdIntelPl2.Value;
            Settings.Default.customIntelVolt = sdIntelVolt.Value;
            Settings.Default.customNvPower = sdNvPower.Value;
            Settings.Default.customNvDb = togDynamicBoost.IsChecked == true;
            Settings.Default.customNvDbOffset = sdNvDbOffset.Value;
            Settings.Default.customNvCore = sdNvCore.Value;
            Settings.Default.customNvMem = sdNvMem.Value;
            Settings.Default.customCoCcd0 = sdCoCcd0.Value;
            Settings.Default.customCoCcd1 = sdCoCcd1.Value;
            Settings.Default.customCoUnified = rbCoUnified.IsChecked == true;
            Settings.Default.customCoUnifiedValue = sdCoUnified.Value;
            Settings.Default.Save();
            DebugLog("Custom slider values saved");
        }

        private void LoadCustomValues()
        {
            sdSustained.Value = Settings.Default.customSustained;
            sdBoost.Value = Settings.Default.customBoost;
            sdTjMax.Value = Settings.Default.customTjMax;
            sdIntelPl1.Value = Settings.Default.customIntelPl1;
            sdIntelPl2.Value = Settings.Default.customIntelPl2;
            sdIntelVolt.Value = Settings.Default.customIntelVolt;
            sdNvPower.Value = Settings.Default.customNvPower;
            togDynamicBoost.IsChecked = Settings.Default.customNvDb;
            sdNvDbOffset.Value = Settings.Default.customNvDbOffset;
            sdNvCore.Value = Settings.Default.customNvCore;
            sdNvMem.Value = Settings.Default.customNvMem;
            sdCoCcd0.Value = Settings.Default.customCoCcd0;
            sdCoCcd1.Value = Settings.Default.customCoCcd1;
            bool coUnified = Settings.Default.customCoUnified;
            rbCoUnified.IsChecked = coUnified;
            rbCoPerCcd.IsChecked = !coUnified;
            sdCoUnified.Value = Settings.Default.customCoUnifiedValue;

            // Apply voltage control visibility from global setting (AMD only — Intel has no voltage control)
            bool voltageEnabled = CpuVoltageEnabled && Family.TYPE != Family.ProcessorType.Intel;
            coControlsPanel?.SetValue(VisibilityProperty, voltageEnabled ? Visibility.Visible : Visibility.Collapsed);

            // Restore stored profile values for status card
            _profileSustained = sdSustained.Value;
            _profileBoost = sdBoost.Value;
            _profileTjMax = (int)sdTjMax.Value;
            _profileCoCcd0 = (int)sdCoCcd0.Value;
            _profileCoCcd1 = (int)sdCoCcd1.Value;
            _profileIntelPl1 = sdIntelPl1.Value;
            _profileIntelPl2 = sdIntelPl2.Value;
            _profileGpuTgp = sdNvPower.Value;
            _profileGpuDb = togDynamicBoost.IsChecked == true;
            _profileGpuDbOffset = sdNvDbOffset.Value;

            // Sync CO visibility after programmatic IsChecked change (events don't fire)
            updateCoVisibility();

            DebugLog($"Custom slider values loaded: sustained={_profileSustained}W boost={_profileBoost}W tjMax={_profileTjMax}");
        }

        #region Custom Preset Slot Management

        /// <summary>
        /// Saves the current slider/control values to the specified Custom preset slot.
        /// Uses the active preset mode (Normal/Boost) to route to the correct storage pool.
        /// </summary>
        private void SaveCustomPreset(int slotIndex)
        {
            var presets = Models.CustomPresetStore.Load(ActivePresetMode);
            if (slotIndex < 0 || slotIndex >= presets.Length) return;

            presets[slotIndex].CpuSustained = sdSustained.Value;
            presets[slotIndex].CpuBoost = sdBoost.Value;
            presets[slotIndex].CpuTjMax = sdTjMax.Value;
            presets[slotIndex].IntelPl1 = sdIntelPl1.Value;
            presets[slotIndex].IntelPl2 = sdIntelPl2.Value;
            presets[slotIndex].IntelVolt = sdIntelVolt.Value;
            presets[slotIndex].GpuTgp = sdNvPower.Value;
            presets[slotIndex].GpuDynamicBoost = togDynamicBoost.IsChecked == true;
            presets[slotIndex].GpuDbOffset = sdNvDbOffset.Value;
            presets[slotIndex].GpuCoreOffset = sdNvCore.Value;
            presets[slotIndex].GpuMemOffset = sdNvMem.Value;
            presets[slotIndex].CpuCoCcd0 = sdCoCcd0.Value;
            presets[slotIndex].CpuCoCcd1 = sdCoCcd1.Value;
            presets[slotIndex].CpuCoUnified = rbCoUnified.IsChecked == true;
            // The mode is the authority; the bool above is the legacy mirror older builds read.
            presets[slotIndex].CpuCoMode = rbCoUnified.IsChecked == true
                ? Models.CpuCoMode.Unified
                : rbCoPerCore.IsChecked == true
                    ? Models.CpuCoMode.PerCore
                    : Models.CpuCoMode.PerCcd;
            presets[slotIndex].CpuCoPerCore = (int[])_coPerCore.Clone();
            presets[slotIndex].CpuCoUnifiedValue = sdCoUnified.Value;

            // Also save V-F curve offsets from editor if active
            if (_vfActivePreset >= 0)
            {
                var vfPresets = Models.VfCurvePresetStore.Load();
                int vfSlot = _vfActivePreset < vfPresets.Length ? _vfActivePreset : 0;
                if (vfPresets[vfSlot].HasChanges)
                {
                    presets[slotIndex].VfCurveOffsets = new Dictionary<int, int>(vfPresets[vfSlot].Offsets);
                }
            }

            // Capture current Fan Control page settings from the global FanControlSettings.
            // This lets each custom preset store its own copy of fan curves, unified/split
            // mode, temp thresholds, and Smart Fan Mode state — but only for slots that
            // carry tuning changes: stamping the global fan state into a slot the user
            // just cleared would make it look saved again right after Clear.
            var p = presets[slotIndex];
            if (p.HasTuningChanges)
            {
                var fanSettings = FanControlSettingsService.Load();
                p.FanUnifiedMode = fanSettings.UnifiedMode;
                p.FanUnifiedPreset = fanSettings.UnifiedPreset;
                p.FanUnifiedDuties = fanSettings.UnifiedDuties?.Clone() as int[];
                p.FanUnifiedTempThresholds = fanSettings.UnifiedTempThresholds?.Clone() as int[];
                p.FanCpuPreset = fanSettings.CpuPreset;
                p.FanCpuDuties = fanSettings.CpuDuties?.Clone() as int[];
                p.FanCpuTempThresholds = fanSettings.CpuTempThresholds?.Clone() as int[];
                p.FanGpuPreset = fanSettings.GpuPreset;
                p.FanGpuDuties = fanSettings.GpuDuties?.Clone() as int[];
                p.FanGpuTempThresholds = fanSettings.GpuTempThresholds?.Clone() as int[];
                p.FanSmartFanMode = fanSettings.EcSmartFanMode;
                p.FanSmartFanTempSource = fanSettings.EcSmartFanTempSource;
            }

            Models.CustomPresetStore.Save(presets, ActivePresetMode);
            DebugLog($"SaveCustomPreset: slot {slotIndex + 1} saved (mode={ActivePresetMode})");
            updateRadioButtonStyles();
        }

        /// <summary>
        /// Loads a Custom preset from the specified slot and populates all controls.
        /// Only applies values if the slot has changes (HasChanges returns true).
        /// Uses the active preset mode (Normal/Boost) to route to the correct storage pool.
        /// </summary>
        private void LoadCustomPreset(int slotIndex)
        {
            var presets = Models.CustomPresetStore.Load(ActivePresetMode);
            if (slotIndex < 0 || slotIndex >= presets.Length) return;

            var preset = presets[slotIndex];
            bool hasData = preset.HasChanges;

            if (hasData)
            {
                sdSustained.Value = preset.CpuSustained;
                sdBoost.Value = preset.CpuBoost;
                sdTjMax.Value = preset.CpuTjMax;
                sdIntelPl1.Value = preset.IntelPl1;
                sdIntelPl2.Value = preset.IntelPl2;
                sdIntelVolt.Value = preset.IntelVolt;
                sdNvPower.Value = preset.GpuTgp;
                togDynamicBoost.IsChecked = preset.GpuDynamicBoost;
                sdNvDbOffset.Value = preset.GpuDbOffset;
                sdNvCore.Value = preset.GpuCoreOffset;
                sdNvMem.Value = preset.GpuMemOffset;
                sdCoCcd0.Value = preset.CpuCoCcd0;
                sdCoCcd1.Value = preset.CpuCoCcd1;
                rbCoUnified.IsChecked = preset.CpuCoMode == Models.CpuCoMode.Unified;
                rbCoPerCcd.IsChecked = preset.CpuCoMode == Models.CpuCoMode.PerCcd;
                rbCoPerCore.IsChecked = preset.CpuCoMode == Models.CpuCoMode.PerCore;
                _coPerCore = preset.CpuCoPerCore ?? Array.Empty<int>();
                sdCoUnified.Value = preset.CpuCoUnifiedValue;

                // Sync CO visibility after programmatic IsChecked change (events don't fire)
                updateCoVisibility();

                // Restore stored profile values for status card
                DebugLog($"LoadCustomPreset slot {slotIndex + 1}: setting sliders Sustained={preset.CpuSustained}, Boost={preset.CpuBoost}, TjMax={preset.CpuTjMax}");
                _profileSustained = preset.CpuSustained;
                _profileBoost = preset.CpuBoost;
                _profileTjMax = (int)preset.CpuTjMax;
                _profileCoCcd0 = (int)preset.CpuCoCcd0;
                _profileCoCcd1 = (int)preset.CpuCoCcd1;
                _profileIntelPl1 = preset.IntelPl1;
                _profileIntelPl2 = preset.IntelPl2;
                _profileGpuTgp = preset.GpuTgp;
                _profileGpuDb = preset.GpuDynamicBoost;
                _profileGpuDbOffset = preset.GpuDbOffset;

                // Load V-F curve offsets if the preset has them
                if (preset.VfCurveOffsets.Count > 0)
                {
                    _vfActivePreset = 0; // Mark curve as active
                    _userTouchedCoreSlider = false;
                    DebugLog($"LoadCustomPreset: slot {slotIndex + 1} loaded with V-F curve ({preset.VfCurveOffsets.Count} points)");
                }
                else
                {
                    _vfActivePreset = -1;
                    _userTouchedCoreSlider = false;
                }

                DebugLog($"LoadCustomPreset: slot {slotIndex + 1} loaded");
            }
            else
            {
                DebugLog($"LoadCustomPreset: slot {slotIndex + 1} is empty, skipping");
            }
        }

        /// <summary>
        /// Updates RadioButton visual style to indicate which slots have saved presets.
        /// Uses the active preset mode (Normal/Boost) to route to the correct storage pool.
        /// </summary>
        private void updateRadioButtonStyles()
        {
            var presets = Models.CustomPresetStore.Load(ActivePresetMode);
            var buttons = new RadioButton[] { rbProfile1, rbProfile2, rbProfile3, rbProfile4, rbProfile5 };

            for (int i = 0; i < buttons.Length; i++)
            {
                bool hasData = i < presets.Length && presets[i].HasChanges;
                // Use bold font weight for slots with saved data
                buttons[i].FontWeight = hasData ? System.Windows.FontWeights.Bold : System.Windows.FontWeights.Normal;
            }
        }

        private void rbProfile_Click(object sender, RoutedEventArgs e)
        {
            var buttons = new RadioButton[] { rbProfile1, rbProfile2, rbProfile3, rbProfile4, rbProfile5 };
            int newSlot = Array.IndexOf(buttons, sender as RadioButton);
            DebugLog($"rbProfile_Click: newSlot={newSlot}, _customPresetSlot={_customPresetSlot}, customPresetSlot={Settings.Default.customPresetSlot}");
            if (newSlot < 0 || newSlot == _customPresetSlot) return;

            // Save current slot before switching (in case user made unsaved changes)
            SaveCustomPreset(_customPresetSlot);

            _customPresetSlot = newSlot;
            ActivePresetSlot = newSlot;

            // The Performance-button cycle keys off presetProfile, so the slot switch
            // has to move that key too or the walk resumes from the old slot.
            Settings.Default.presetProfile = Services.PresetProfileApplier.CustomProfileKey(newSlot, ActivePresetMode);
            Settings.Default.Save();
            DebugLog($"rbProfile_Click: Saved _customPresetSlot={_customPresetSlot}, customPresetSlot={Settings.Default.customPresetSlot}");

            // Load the new slot's preset if it has data
            LoadCustomPreset(newSlot);
            updateAllLabels();
            updateStatus();
            updateRadioButtonStyles();

            // Apply fan settings from the new preset slot if it has any stored.
            var customPresets = Models.CustomPresetStore.Load(ActivePresetMode);
            if (newSlot < customPresets.Length && customPresets[newSlot].HasFanChanges)
            {
                PresetProfileApplier.ApplyFanSettingsFromPreset(customPresets[newSlot]);
                DebugLog($"rbProfile_Click: Applied fan settings from slot {newSlot + 1}");
            }
        }

        private void btnSave_Click(object sender, RoutedEventArgs e)
        {
            SaveCustomPreset(_customPresetSlot);
            string modeSuffix = ActivePresetMode == Models.PresetMode.Boost ? " (Boost)" : "";
            ShowPresetSnackbar($"Preset saved",
                $"Custom preset{modeSuffix} has been saved to slot {_customPresetSlot + 1}!");
        }

        private void btnClear_Click(object sender, RoutedEventArgs e)
        {
            var presets = Models.CustomPresetStore.Load(ActivePresetMode);
            if (_customPresetSlot < 0 || _customPresetSlot >= presets.Length) return;

            // Reset slot to default values (clears all fields)
            presets[_customPresetSlot] = new Models.CustomPreset();
            Models.CustomPresetStore.Save(presets, ActivePresetMode);

            // Clear the slider UI to defaults
            sdSustained.Value = 75;
            sdBoost.Value = 85;
            sdTjMax.Value = 95;
            sdIntelPl1.Value = 45;
            sdIntelPl2.Value = 60;
            sdIntelVolt.Value = 0;
            sdNvPower.Value = 150;
            togDynamicBoost.IsChecked = false;
            sdNvDbOffset.Value = 0;
            sdNvCore.Value = 0;
            sdNvMem.Value = 0;
            sdCoCcd0.Value = 0;
            sdCoCcd1.Value = 0;
            sdCoUnified.Value = 0;
            rbCoUnified.IsChecked = true;
            rbCoPerCcd.IsChecked = false;
            rbCoPerCore.IsChecked = false;
            _coPerCore = Array.Empty<int>();
            updateCoVisibility();

            // Reset V-F curve state
            _vfActivePreset = -1;

            updateAllLabels();
            updateStatus();
            updateRadioButtonStyles();

            string modeSuffix = ActivePresetMode == Models.PresetMode.Boost ? " (Boost)" : "";
            ShowPresetSnackbar($"Profile {_customPresetSlot + 1} Cleared",
                $"Custom preset{modeSuffix} has been cleared from slot {_customPresetSlot + 1}!");
        }

        private void btnApply_Click(object sender, RoutedEventArgs e)
        {
            SaveCustomPreset(_customPresetSlot);
            SaveCustomValues(); // Also save to Settings for backward compatibility (auto-reapply)
            _ = applyProfileAsync();
        }

        #endregion

        /// <summary>
        /// The memory offset a curve apply carries: the V-F curve write rebuilds the pstate
        /// table, so the slider's memory offset has to ride the same call or it is erased
        /// (field, 2026-11-16). 0 when the user disabled GPU clock control.
        /// </summary>
        private int MemoryOffsetForCurve() =>
            Settings.Default.EnableGpuClockControl ? (int)sdNvMem.Value : 0;

        private string buildCommandString()
        {
            var parts = new List<string>();

            if (Family.TYPE == Family.ProcessorType.Amd_Apu)
            {
                // CPU power limits via SMU (slow-limit, fast-limit) + EC (PL1, PL2, PL4).
                // The SMU communicates directly with AMD firmware via PCI config space.
                // The EC writes enforce the hardware ceiling that clamps SMU settings from above.
                // Both paths are required: SMU alone returns OK but the EC ceiling (~130W)
                // silently overrides the SMU values.
                //
                // CRITICAL: Do NOT add --Win-CPU. It calls powercfg /setactive SCHEME_CURRENT
                // which causes Windows to re-apply power scheme defaults, overriding SMU limits.
                // The original UCU does not use --Win-CPU at all.
                //
                // Values are in milliwatts (W * 1000), matching original UCU convention.
                int slowLimit = (int)sdSustained.Value * 1000;   // Sustained â†’ slow-limit
                int fastLimit = (int)sdBoost.Value * 1000;   // Boost â†’ fast-limit

                parts.Add($"--slow-limit={slowLimit}");
                parts.Add($"--fast-limit={fastLimit}");

                // CPU temperature limit via SMU (same path as Adaptive Mode).
                // Previously used ec.SetCpuTempLimit() writing to EC register 0x0786,
                // but 0x0786 is actually a fan curve register (0x0786-0x078A) on NB02.
                // Corrupting fan curve data caused a 20W TDP lockup.
                // tuxedo-drivers has NO CPU temp limit EC support — SMU is the only path.
                int tjMax = (int)sdTjMax.Value;
                parts.Add($"--tctl-temp={tjMax}");
                parts.Add($"--cHTC-temp={tjMax}");

                // Curve Optimizer — one encoder for all three modes (Services/PresetProfileApplier),
                // so the page cannot drift from the preset path: per-core, per-CCD and unified all
                // resolve against the real topology, and the command is decimal (the translator
                // parses decimal; the old hex format here silently dropped half the values).
                // Skip entirely if the user disabled CPU voltage control (BIOS-managed).
                if (CpuVoltageEnabled)
                {
                    var coPreset = new Models.CustomPreset
                    {
                        CpuCoMode = rbCoUnified.IsChecked == true ? Models.CpuCoMode.Unified
                            : rbCoPerCore.IsChecked == true ? Models.CpuCoMode.PerCore
                            : Models.CpuCoMode.PerCcd,
                        CpuCoUnifiedValue = sdCoUnified.Value,
                        CpuCoCcd0 = sdCoCcd0.Value,
                        CpuCoCcd1 = sdCoCcd1.Value,
                        CpuCoPerCore = _coPerCore.Length > 0 ? (int[])_coPerCore.Clone() : Array.Empty<int>()
                    };

                    PresetProfileApplier.AppendCoCommands(parts, coPreset, CpuTopologyReader.Read());
                }
            }
            else if (Family.TYPE == Family.ProcessorType.Intel)
            {
                // Intel MSR commands (no voltage control on Intel — undervolt removed)
                parts.Add($"--intel-pl={sdIntelPl1.Value:F0},{sdIntelPl2.Value:F0}");
            }

            // NVIDIA — GPU power control via EC Dynamic Boost registers (0x0743-0x0746).
            // These GPU-specific EC writes are independent of the CPU power clamp issue.
            if (sdNvidia.Visibility == Visibility.Visible)
            {
                // In curve mode (Custom profile only), skip power limit here — it's applied
                // AFTER the V-F curve in applyProfileAsync to avoid the driver re-negotiating
                // power after the curve write. When the slider is used, apply everything normally.
                // Stock profiles always use the slider path, even if a curve exists on disk.
                bool useCurve = _currentProfile == "Custom" && VfCurveActive && !_userTouchedCoreSlider;
                if (useCurve)
                {
                    // Nothing here — power is applied after the curve.
                }
                else
                {
                    if (PresetProfileApplier.IsBuiltInProfile(_currentProfile))
                    {
                        // Stock presets yield GPU power to the firmware's per-mode
                        // table (OpenRevo's approach) and own no clock offsets.
                        parts.Add("--NVIDIA-Clocks=0-0-0");
                    }
                    // Nothing to write when the EC published no power table: the card is
                    // inert because the offset would be measured from a guessed base.
                    else if (_gpuPowerSupported)
                    {
                        int core = Settings.Default.EnableGpuClockControl ? (int)sdNvCore.Value : 0;
                        int mem = Settings.Default.EnableGpuClockControl ? (int)sdNvMem.Value : 0;
                        int tgp = (int)sdNvPower.Value;
                        bool boostEnabled = Settings.Default.EnablePowerBoostMode;
                        bool dbEnabled = !boostEnabled && togDynamicBoost.IsChecked == true;
                        int dbOffset = dbEnabled ? (int)sdNvDbOffset.Value : 0;
                        int totalWatts = tgp + dbOffset;
                        parts.Add($"--NVIDIA-Clocks=0-{core}-{mem}-{totalWatts}-{dbOffset}");
                    }
                }
            }

            return string.Join(" ", parts) + " ";
        }


        #endregion

        private void SizeSlider_TouchDown(object sender, TouchEventArgs e)
        {
            e.Handled = true;
        }

        #region V-F Curve Editor (popup window)

        /// <summary>
        /// Scans Custom preset slots on disk for any non-zero V-F curve offsets and activates the first one found.
        /// Called on startup so that _vfActivePreset is set even when the curve editor
        /// was last used in a previous app session.
        /// </summary>
        private void detectAndActivateVfCurve()
        {
            if (_vfActivePreset >= 0) return; // Already active from this session.

            // Restore the last active custom preset slot from settings for the active mode
            _customPresetSlot = ActivePresetSlot;
            if (_customPresetSlot < 0 || _customPresetSlot >= Models.CustomPreset.MaxSlots)
                _customPresetSlot = 0;

            var presets = Models.CustomPresetStore.Load(ActivePresetMode);

            // Check the active slot first — if it has a V-F curve, activate it and stop.
            if (_customPresetSlot < presets.Length && presets[_customPresetSlot].VfCurveOffsets.Count > 0)
            {
                _vfActivePreset = 0;
                DebugLog($"V-F curve active on current slot {_customPresetSlot + 1} (mode={ActivePresetMode})");
                return;
            }

            // Only auto-switch to another slot if the current slot is empty (no HasChanges).
            // This avoids stealing the slot away from a preset that has fan/VF settings but no V-F curve.
            if (!presets[_customPresetSlot].HasChanges)
            {
                for (int i = 0; i < presets.Length; i++)
                {
                    if (presets[i].VfCurveOffsets.Count > 0)
                    {
                        _vfActivePreset = 0;
                        _customPresetSlot = i;
                        DebugLog($"V-F curve auto-detected on Custom preset slot {i + 1} (mode={ActivePresetMode})");
                        return;
                    }
                }
            }
        }

        /// <summary> True when the active Custom preset slot has V-F curve offsets. </summary>
        private bool VfCurveActive
        {
            get
            {
                var presets = Models.CustomPresetStore.Load(ActivePresetMode);
                int slot = _customPresetSlot;
                return slot >= 0 && slot < presets.Length && presets[slot].VfCurveOffsets.Count > 0;
            }
        }

        private void btnVfCurve_Click(object sender, RoutedEventArgs e)
        {
            // Only one curve editor window at a time — bring existing to front.
            // Note: _vfEditorWindow is set to null in the Closed handler, so null check is sufficient.
            if (_vfEditorWindow != null)
            {
                _vfEditorWindow.Activate();
                return;
            }

            // Load V-F curve offsets from the active Custom preset slot
            var presets = Models.CustomPresetStore.Load(ActivePresetMode);
            Dictionary<int, int>? offsets = null;
            if (_customPresetSlot >= 0 && _customPresetSlot < presets.Length)
            {
                var vfOffsets = presets[_customPresetSlot].VfCurveOffsets;
                if (vfOffsets.Count > 0)
                    offsets = new Dictionary<int, int>(vfOffsets);
            }

            // Open non-modal curve editor window — user can interact with both windows.
            // The editor auto-saves to the active Custom preset slot.
            _vfEditorWindow = new Views.Windows.VfCurveEditorWindow(offsets, _customPresetSlot, ActivePresetMode);
            _vfEditorWindow.Owner = Application.Current.MainWindow;
            _vfEditorWindow.Closed += (_, _) => _vfEditorWindow = null;
            _vfEditorWindow.CurveChanged += (_, _) =>
                {
                    _userTouchedCoreSlider = false; // User went back to curve — reclaim from slider
                    updateAllLabels();
                };
            _vfEditorWindow.Show();
        }

        #endregion

        #region Fan Curve Mapping

        /// <summary>
        /// Applies a Custom fan curve from saved FanControlSettings to the EC.
        /// Used when switching to the Custom power profile.
        /// </summary>
        private async Task ApplyCustomFanCurveFromSettingsAsync(FanControlSettings settings)
        {
            if (_deviceApplier is null) return;

            if (settings.UnifiedMode)
            {
                // Unified mode — apply the unified curve
                int[]? customDuties = null;
                string presetName = settings.UnifiedPreset;

                if (presetName == "Custom" && settings.UnifiedDuties?.Length == 11)
                {
                    customDuties = settings.UnifiedDuties;
                    presetName = "Custom";
                }

                await _deviceApplier.ApplyEcFanFromPresetAsync(
                    unifiedMode: true,
                    presetName: presetName,
                    customDuties: customDuties,
                    cpuPresetDuties: null,
                    gpuPresetDuties: null,
                    cpuTemps: settings.UnifiedTempThresholds);

                DebugLog($"ApplyCustomFanCurveFromSettings: Unified {presetName} applied");
            }
            else
            {
                // Split mode — apply separate CPU/GPU curves
                int[]? cpuDuties = null;
                int[]? gpuDuties = null;

                if (settings.CpuPreset == "Custom" && settings.CpuDuties?.Length == 11)
                    cpuDuties = settings.CpuDuties;
                if (settings.GpuPreset == "Custom" && settings.GpuDuties?.Length == 11)
                    gpuDuties = settings.GpuDuties;

                // For split mode, use the CPU preset name as the base
                await _deviceApplier.ApplyEcFanFromPresetAsync(
                    unifiedMode: false,
                    presetName: settings.CpuPreset,
                    customDuties: null,
                    cpuPresetDuties: cpuDuties,
                    gpuPresetDuties: gpuDuties,
                    cpuTemps: settings.CpuTempThresholds,
                    gpuTemps: settings.GpuTempThresholds);

                DebugLog($"ApplyCustomFanCurveFromSettings: Split CPU={settings.CpuPreset} GPU={settings.GpuPreset} applied");
            }
        }

        /// <summary>
        /// Applies a fan curve preset to the EC from a power profile switch.
        /// Maps profile names to fan presets: Office→Silent, Gaming→Balanced, Turbo→Performance.
        /// </summary>
        private async Task ApplyFanCurveFromPresetAsync(string fanPresetName)
        {
            if (_deviceApplier is null) return;

            DebugLog($"ApplyFanCurveFromPreset: {fanPresetName}");

            // Update the FanControlSettings so the Fan Control page shows the correct preset
            var settings = FanControlSettingsService.Load();
            settings.UnifiedMode = true;
            settings.UnifiedPreset = fanPresetName;
            FanControlSettingsService.Save(settings);

            // Apply to EC immediately via DeviceApplier
            await _deviceApplier.ApplyEcFanFromPresetAsync(
                unifiedMode: true,
                presetName: fanPresetName,
                customDuties: null,
                cpuPresetDuties: null,
                gpuPresetDuties: null);

            DebugLog($"ApplyFanCurveFromPreset: {fanPresetName} applied to EC");
        }

        #endregion

        #region GPU power ceiling override (Extra GPU power)

        // The state comes from the engine: the display-driver key is HKLM and the
        // adapter restart is a LocalSystem act. Nothing here writes hardware directly.
        private UCU.Contracts.Capabilities.IGpuTuningCapability? _gpuTuning;
        private UCU.Contracts.GpuRomOverrideStateDto? _romOverride;
        private bool _romOverrideBusy;
        private string? _romOverrideError;

        // The platform gate (an NVIDIA driver key has no card without a card), set by the
        // visibility pass and combined with Power boost mode on every render.
        private bool _hasNvidia;

        // The rail currents are a separate, live NVAPI write: their own read, their own
        // apply, their own failure line. Nothing here restarts the GPU.
        private UCU.Contracts.GpuRailLimitsDto? _railLimits;
        private bool _railLimitsBusy;
        private string? _railLimitsError;
        private string? _railLimitsNote;
        private bool _railBinding;

        // The tested tier and the stock rails, the UI's own copy of what the engine ships
        // (GpuPowerTiers.ExtraGpuPower and RomOverrideLayout.Stock*MilliAmps in UCU.Hardware,
        // which this project does not reference). The status row judges a reading against these
        // without asking the engine what "tested" means, and the slider floor is the stock rail.
        private const int TestedTargetMilliwatts = 225_000;
        private const int TestedNvvddMilliamps = 250_000;
        private const int TestedMsvddMilliamps = 70_000;
        private const int TestedBoardInputMilliamps = 14_026;
        private const int StockNvvddMilliamps = 210_000;
        private const int StockMsvddMilliamps = 60_000;
        private const int StockBoardInputMilliamps = 11_053;

        private async Task RefreshRomOverrideAsync()
        {
            _gpuTuning ??= App.GetService<UCU.Contracts.Capabilities.IGpuTuningCapability>();
            _romOverrideError = null;

            if (_gpuTuning == null)
            {
                _romOverride = null;
                RenderRomOverride();
                return;
            }

            try
            {
                _romOverride = await _gpuTuning.GetRomOverrideStateAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                // A stopped engine or a refused verb is a state the card must show,
                // not a page failure.
                DebugLog($"GPU ROM override state unavailable: {ex.Message}");
                _romOverride = null;
            }

            RenderRomOverride();

            // The rails are a second read of the same driver, so they follow the state:
            // the section is only usable while the unlock is applied.
            await RefreshRailLimitsAsync();
        }

        private void RenderRomOverride()
        {
            UCU.Contracts.GpuRomOverrideStateDto? state = _romOverride;
            bool busy = _romOverrideBusy;
            bool unlocked = state?.State == UCU.Contracts.GpuRomOverrideStates.Unlocked;
            bool uncertain = state?.State == UCU.Contracts.GpuRomOverrideStates.Uncertain;
            bool blocked = state?.State == UCU.Contracts.GpuRomOverrideStates.Unsupported;
            bool canUnlock = state != null && !unlocked && !blocked;

            // Both actions stay visible; each is gated separately and dimmed when off.
            bool consent = chkRomConsent.IsChecked == true;
            bool powerBoost = Settings.Default.EnablePowerBoostMode;

            // The card belongs to Power boost mode. With it off the card is gone - except while
            // an override is still live, where hiding it would hide the only way to remove a
            // driver-level change: then it shows one line and Restore stock, and no sliders.
            bool appliedWhileOff = !powerBoost && state?.Enabled == true;
            cardRomOverride.Visibility =
                _hasNvidia && (powerBoost || appliedWhileOff) ? Visibility.Visible : Visibility.Collapsed;
            tbRomPbmOffLine.Visibility = appliedWhileOff ? Visibility.Visible : Visibility.Collapsed;
            btnRomUnlock.Visibility = appliedWhileOff ? Visibility.Collapsed : Visibility.Visible;
            brRailDivider.Visibility = appliedWhileOff ? Visibility.Collapsed : Visibility.Visible;
            spRailSection.Visibility = appliedWhileOff ? Visibility.Collapsed : Visibility.Visible;

            bool unlockEnabled = !busy && canUnlock && powerBoost && consent;
            bool restoreEnabled = !busy && (unlocked || uncertain || appliedWhileOff);

            btnRomUnlock.IsEnabled = unlockEnabled;
            btnRomUnlock.Opacity = unlockEnabled ? 1.0 : 0.5;
            btnRomRestore.IsEnabled = restoreEnabled;
            btnRomRestore.Opacity = restoreEnabled ? 1.0 : 0.5;

            spRomConsentRow.Visibility = canUnlock && !appliedWhileOff ? Visibility.Visible : Visibility.Collapsed;
            chkRomConsent.IsEnabled = !busy;

            spRomBusy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;

            // One line, and only where there is something to explain: a refusal, a partial
            // apply, a failed read, or the reason an action is dimmed. A clean unlock needs no
            // sentence — the values row on the status card is the state, and the buttons moved
            // when it changed. The "still applied with the feature off" state has its own line.
            string? line = busy || appliedWhileOff
                ? null
                : _romOverrideError != null
                    ? WithReason(LocalizationService.Get("The state could not be read back"), _romOverrideError)
                    : state == null
                        ? LocalizationService.Get("The engine service is not available, so the current state could not be read.")
                        : blocked
                            ? WithReason(
                                LocalizationService.Get("The override cannot be used on this machine.") + "\n" + LocalizationService.Get(
                                    "This is refused when the GPU's VBIOS is not the one this build was measured on, or when another tool already holds the override. This card never overwrites an override it did not write: remove it in that tool, then restart the GPU."),
                                state.Reason)
                            : uncertain
                                ? WithReason(LocalizationService.Get("Partly applied: the change could not be verified."), state.Reason)
                                : canUnlock && !powerBoost
                                    ? LocalizationService.Get("Turn on Power boost mode in Settings first.")
                                    : canUnlock && !consent
                                        ? LocalizationService.Get("Tick the statement above first.")
                                        : null;

            tbRomOverrideNote.Text = line ?? string.Empty;
            tbRomOverrideNote.Visibility = string.IsNullOrEmpty(line) ? Visibility.Collapsed : Visibility.Visible;

            // The colour is decoration on a sentence that already says what is wrong.
            tbRomOverrideNote.Foreground = (System.Windows.Media.Brush)FindResource(
                !busy && (_romOverrideError != null || blocked || uncertain)
                    ? "SystemFillColorCautionBrush"
                    : "TextFillColorSecondaryBrush");

            // Both follow this same state, so they re-render with it.
            RenderRomStatusRow();
            RenderRailLimits();
        }

        private static string RomReasonLine(string? reason) =>
            string.IsNullOrWhiteSpace(reason) ? string.Empty : LocalizationService.Format("Reason: {0}", reason);

        private static string WithReason(string text, string? reason) =>
            string.IsNullOrWhiteSpace(reason) ? text : text + "\n" + RomReasonLine(reason);

        /// <summary>
        /// The unlock's numbers as the status card's second row: TGP and the budget, plus the three
        /// rails, each the driver's own read-back rather than the tier's request. The row is keyed to
        /// <see cref="UCU.Contracts.GpuRomOverrideStateDto.Enabled"/>, not to the derived state: it is
        /// absent until the owner has unlocked at least once, and once enabled it renders whatever the
        /// numbers are — including "Not read"/"Unverified" while a start-up re-apply is settling. With
        /// it off, the card above already says what the GPU runs on. It is named in the slider's own
        /// vocabulary - TGP - while the driver's 250 W ceiling is permission, not a target, and is
        /// explained in the section's tooltip instead.
        ///
        /// What the colours mean, stated once here — and every colour is rendered next to its
        /// own word, so no state depends on colour alone:
        ///   Tested (success)   — applied and inside the measured configuration: request at or
        ///                        above the 225 W target, budget at or above the request, and
        ///                        every rail at or below 250 / 70 / 14.026 A.
        ///   Untested (caution) — applied but outside it: budget below the request, request below
        ///                        the target, or a rail above the tested value. Above 14.026 A on
        ///                        the board input that is also the adapter-overload case.
        ///   Unverified         — critical: refused or unverified (Unsupported / Uncertain).
        ///   Not read           — critical: the driver could not be asked for that number, which
        ///                        is not the same as a number that is fine.
        /// </summary>
        private void RenderRomStatusRow()
        {
            UCU.Contracts.GpuRomOverrideStateDto? state = _romOverride;
            bool enabled = state?.Enabled == true;

            spRomStatusRow.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
            if (!enabled)
                return;

            // The numbers are the unlock's own report, and they belong to the feature that owns
            // it: with Power boost mode off the row says why they are gone and what removes the
            // override, instead of measuring a feature the owner has switched off.
            bool powerBoost = Settings.Default.EnablePowerBoostMode;
            spRomStatusValues.Visibility = powerBoost ? Visibility.Visible : Visibility.Collapsed;
            tbRomStatusPbmOff.Visibility = powerBoost ? Visibility.Collapsed : Visibility.Visible;
            if (!powerBoost)
                return;

            // A partial apply is not a verdict: the numbers can read right and still be unverified.
            bool unverified = state!.State != UCU.Contracts.GpuRomOverrideStates.Unlocked;

            int? request = state.RequestedLimitMilliwatts;
            int? budget = state.BudgetMilliwatts;
            int? nvvdd = state.NvvddCurrentMilliamps;
            int? msvdd = state.MsvddCurrentMilliamps;
            int? board = state.BoardInputCurrentMilliamps;

            SetRomValue(tbRomStatRequest, tbRomStatRequestWord, Watts(request),
                request is not null, unverified, request is int r && r >= TestedTargetMilliwatts);

            // The driver enforces min(request, budget), so a budget under the request is the
            // laptop's own cap holding the GPU back, not a fault.
            SetRomValue(tbRomStatBudget, tbRomStatBudgetWord, Watts(budget),
                budget is not null, unverified, budget is int b && b >= (request ?? TestedTargetMilliwatts));

            SetRomValue(tbRomStatNvvdd, tbRomStatNvvddWord, Amps(nvvdd, 0) + " A",
                nvvdd is not null, unverified, nvvdd is int n && n <= TestedNvvddMilliamps);
            SetRomValue(tbRomStatMsvdd, tbRomStatMsvddWord, Amps(msvdd, 0) + " A",
                msvdd is not null, unverified, msvdd is int m && m <= TestedMsvddMilliamps);
            SetRomValue(tbRomStatBoard, tbRomStatBoardWord, Amps(board, 3) + " A",
                board is not null, unverified, board is int i && i <= TestedBoardInputMilliamps);
        }

        private void SetRomValue(
            TextBlock valueText, TextBlock wordText, string text, bool read, bool unverified, bool tested)
        {
            valueText.Text = text;

            (string word, string brush) = !read
                ? ("Not read", "SystemFillColorCriticalBrush")
                : unverified
                    ? ("Unverified", "SystemFillColorCriticalBrush")
                    : tested
                        ? ("Tested", "SystemFillColorSuccessBrush")
                        : ("Untested", "SystemFillColorCautionBrush");

            wordText.Text = LocalizationService.Get(word);
            System.Windows.Media.Brush colour = (System.Windows.Media.Brush)FindResource(brush);
            valueText.Foreground = colour;
            wordText.Foreground = colour;
        }

        private static string Watts(int? milliwatts) =>
            milliwatts is int value ? (value / 1000.0).ToString("0.#") + " W" : "-- W";

        private void chkRomConsent_Changed(object sender, RoutedEventArgs e) => RenderRomOverride();

        /// <summary>
        /// The measurement tooltip's text, built here instead of walked by the page's translation
        /// pass: a tooltip's content is not in the visual tree until it opens, so nothing else
        /// would translate it. Rebuilt on every navigation, as the profile tooltips are.
        /// </summary>
        private void setRomInfoTooltip()
        {
            static string Row(string label, string value) =>
                LocalizationService.Format("{0}: {1}", LocalizationService.Get(label), value);

            tbRomInfo.Text = string.Join("\n",
                Row("Ceiling (driver)", "250 W"),
                Row("Target", "225 W"),
                Row("Rails", "250 A · 70 A · 14.026 A"),
                Row("Stock rails", "210 A · 60 A · 11.053 A"),
                Row("Driver max", "294 A · 84 A · 17.021 A"),
                string.Empty,
                LocalizationService.Get("The EC publishes a 95 W base TGP, so the cTGP offset is 130 and Dynamic Boost is off."),
                string.Empty,
                LocalizationService.Get("With a +233 MHz core offset we measured 214 W board power at 0.975-0.985 V, PerfCap VRel — power was no longer the binding limit. The offset is per card and yours to set on this page, so another unit may take more or crash well below it, and we have not measured a wattage without one. Hotspot 58-63 °C at 215 W with the liquid cooler connected, 81.7 °C without it."),
                string.Empty,
                LocalizationService.Get("Overrides the driver's power policy only: the GPU's firmware is unchanged, and Restore stock undoes it."));
        }

        private async void btnRomUnlock_Click(object sender, RoutedEventArgs e) =>
            await RunRomOverrideAsync(restore: false);

        private async void btnRomRestore_Click(object sender, RoutedEventArgs e) =>
            await RunRomOverrideAsync(restore: true);

        private async Task RunRomOverrideAsync(bool restore)
        {
            if (_romOverrideBusy || _gpuTuning == null || _romOverride == null)
                return;

            bool unlocked = _romOverride.State == UCU.Contracts.GpuRomOverrideStates.Unlocked;
            bool uncertain = _romOverride.State == UCU.Contracts.GpuRomOverrideStates.Uncertain;

            // The buttons are already gated visually; re-check so a stale frame cannot
            // fire an act the card was showing as unavailable.
            if (restore
                ? !(unlocked || uncertain || _romOverride.Enabled)
                : !(Settings.Default.EnablePowerBoostMode && chkRomConsent.IsChecked == true))
            {
                return;
            }

            string question = restore
                ? LocalizationService.Get("Restore the stock GPU power limit?")
                : LocalizationService.Get("Apply the extra GPU power override?");
            string body = restore
                ? LocalizationService.Get("This removes the driver-level override and restarts the GPU. The screen goes blank, and any running game or GPU workload will close.")
                : LocalizationService.Get("This changes a driver-level power setting and restarts the GPU. The screen goes blank, and any running game or GPU workload will close. Restore stock undoes it.");

            Window? owner = Window.GetWindow(this);
            MessageBoxResult answer = owner != null
                ? MessageBox.Show(owner, body, question, MessageBoxButton.YesNo, MessageBoxImage.Warning)
                : MessageBox.Show(body, question, MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (answer != MessageBoxResult.Yes)
                return;

            _romOverrideBusy = true;
            _romOverrideError = null;
            RenderRomOverride();

            try
            {
                // The verb ends in the engine's pnputil restart of the GPU: park hardware
                // monitoring across it (the guard holds the park through the driver
                // cooldown), or a poll lands on a dead NVML handle and kills the process.
                UCU.Contracts.Capabilities.IGpuTuningCapability tuning = _gpuTuning;
                _romOverride = await GpuDeviceRestartGuard.RunAsync(
                    App.GetService<IHardwareMonitoringService>(),
                    () => tuning.SetRomOverrideAsync(!restore)).ConfigureAwait(true);
                DebugLog($"GPU ROM override: asked {(!restore ? "unlock" : "restore")}, engine answered {_romOverride.State}");
            }
            catch (Exception ex)
            {
                // The adapter restart alone may take 60 s, so a transport timeout is
                // not a verdict: report it, then read the real state back.
                DebugLog($"GPU ROM override failed: {ex.Message}");
                _romOverrideError = ex.Message;

                try
                {
                    _romOverride = await _gpuTuning.GetRomOverrideStateAsync().ConfigureAwait(true);
                }
                catch (Exception reread)
                {
                    DebugLog($"GPU ROM override re-read failed: {reread.Message}");
                }
            }
            finally
            {
                _romOverrideBusy = false;
                RenderRomOverride();

                // The unlock rewrote the driver's own policy, and that policy is where the
                // sliders get their bounds: without this read they would keep the stock range
                // and stay inert until the page was reloaded.
                await RefreshRailLimitsAsync();
            }
        }

        #endregion

        #region GPU rail current limits (Extra GPU power, advanced)

        /// <summary>
        /// The sliders' read: the current each rail carries and the policy range the driver
        /// bounds it by. Read separately from the override state because a refusal here must
        /// disable three controls, not a whole card.
        /// </summary>
        private async Task RefreshRailLimitsAsync()
        {
            _gpuTuning ??= App.GetService<UCU.Contracts.Capabilities.IGpuTuningCapability>();
            _railLimitsError = null;

            if (_gpuTuning == null)
            {
                _railLimits = null;
                RenderRailLimits();
                return;
            }

            try
            {
                _railLimits = await _gpuTuning.GetRailLimitsAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                // A transport failure is reported as one, never rendered as "stock".
                DebugLog($"GPU rail limits unavailable: {ex.Message}");
                _railLimits = null;
                _railLimitsError = ex.Message;
            }

            RenderRailLimits();
        }

        private void RenderRailLimits()
        {
            bool applied = _romOverride?.State == UCU.Contracts.GpuRomOverrideStates.Unlocked
                           || _romOverride?.State == UCU.Contracts.GpuRomOverrideStates.Uncertain;

            UCU.Contracts.GpuRailLimitsDto? rails = _railLimits;
            bool bounded;
            _railBinding = true;
            try
            {
                bounded =
                    BindRail(sdRailNvvdd, rails?.NvvddMilliamps, StockNvvddMilliamps, rails?.NvvddMinMilliamps, rails?.NvvddMaxMilliamps) &
                    BindRail(sdRailMsvdd, rails?.MsvddMilliamps, StockMsvddMilliamps, rails?.MsvddMinMilliamps, rails?.MsvddMaxMilliamps) &
                    BindRail(sdRailBoardInput, rails?.BoardInputMilliamps, StockBoardInputMilliamps, rails?.BoardInputMinMilliamps, rails?.BoardInputMaxMilliamps);
            }
            finally
            {
                _railBinding = false;
            }

            // Dimmed as one group, so "not available" never looks like three unrelated
            // controls that happen to be off.
            spRailControls.IsEnabled = applied && bounded && !_railLimitsBusy;

            string? refusal = _railLimitsError ?? rails?.Reason;
            tbRailReason.Text = RomReasonLine(refusal);
            tbRailReason.Visibility = string.IsNullOrEmpty(tbRailReason.Text) ? Visibility.Collapsed : Visibility.Visible;

            tbRailNote.Text = !applied
                ? LocalizationService.Get("Available once Extra GPU power is unlocked.")
                : !bounded
                    ? LocalizationService.Get("The current limits could not be read.")
                    : _railLimitsNote ?? string.Empty;

            UpdateRailReadouts();
            UpdateRailCaution();
            UpdateRailApply(applied && bounded);
        }

        /// <summary>
        /// One rail's slider bounds and position. The floor is the stock rail, not the driver's
        /// own minimum: below stock the unlock cannot take the board past its stock 175 W ROM
        /// ceiling, so those values would change nothing. The driver's policy maximum still caps
        /// the slider (294 / 84 / 17.021 A after an unlock), so the tier's own currents sit
        /// inside the range. False when a number is missing — a guessed range is worse than a
        /// disabled row.
        /// </summary>
        private static bool BindRail(Slider slider, int? current, int stockMilliAmps, int? policyMin, int? policyMax)
        {
            if (current is not int value || policyMin is not int low || policyMax is not int high || high < low)
                return false;

            slider.Minimum = Math.Min(Math.Max(stockMilliAmps, low), high);
            slider.Maximum = high;
            slider.Value = Math.Clamp(value, slider.Minimum, high);
            return true;
        }

        /// <summary>Each rail's value column carries what it draws now; its description carries
        /// the range the slider spans — stock up to the driver's own maximum.</summary>
        private void UpdateRailReadouts()
        {
            UCU.Contracts.GpuRailLimitsDto? rails = _railLimits;
            UpdateRail(lblRailNvvdd, tbRailNvvdd, sdRailNvvdd, rails?.NvvddMinMilliamps, rails?.NvvddMaxMilliamps, StockNvvddMilliamps, 0);
            UpdateRail(lblRailMsvdd, tbRailMsvdd, sdRailMsvdd, rails?.MsvddMinMilliamps, rails?.MsvddMaxMilliamps, StockMsvddMilliamps, 0);
            UpdateRail(lblRailBoardInput, tbRailBoardInput, sdRailBoardInput, rails?.BoardInputMinMilliamps, rails?.BoardInputMaxMilliamps, StockBoardInputMilliamps, 3);
        }

        private static void UpdateRail(TextBlock value, TextBlock range, Slider slider, int? min, int? max, int stock, int decimals)
        {
            string format = AmpsFormat(decimals);
            value.Text = (slider.Value / 1000.0).ToString(format) + " A";

            range.Text = min is int low && max is int high
                ? LocalizationService.Format(
                    "Range {0}-{1} A",
                    (Math.Max(low, stock) / 1000.0).ToString(format),
                    (high / 1000.0).ToString(format))
                : "-- A";
        }

        /// <summary>
        /// Warn, never clamp: above 14 A the GPU alone can exceed what the adapter supplies
        /// once the CPU is loaded, which is the combination that fails on this machine.
        /// </summary>
        private void UpdateRailCaution() =>
            // 14.026 A is the tested value the tier ships; warn only when the user goes above it
            // (which needs a bigger record, i.e. a different policy maximum).
            tbRailCaution.Visibility = sdRailBoardInput.Value > TestedBoardInputMilliamps ? Visibility.Visible : Visibility.Collapsed;

        private void UpdateRailApply(bool canApply)
        {
            UCU.Contracts.GpuRailLimitsDto? rails = _railLimits;
            bool changed = rails != null &&
                (Changed(sdRailNvvdd, rails.NvvddMilliamps, 0) |
                 Changed(sdRailMsvdd, rails.MsvddMilliamps, 0) |
                 Changed(sdRailBoardInput, rails.BoardInputMilliamps, 3));

            // Primary while it would do something, dimmed while it would not: the caption
            // above says why the group can be off, the button says why it is.
            bool enabled = canApply && !_railLimitsBusy && changed;
            btnRailApply.IsEnabled = enabled;
            btnRailApply.Opacity = enabled ? 1.0 : 0.5;
        }

        /// <summary>The value a slider stands for, snapped to the precision it displays.</summary>
        private static int Quantize(double milliAmps, int decimals) => decimals switch
        {
            0 => (int)Math.Round(milliAmps / 1000.0) * 1000,
            1 => (int)Math.Round(milliAmps / 100.0) * 100,
            _ => (int)Math.Round(milliAmps),
        };

        private static bool Changed(Slider slider, int? current, int decimals) =>
            current is int value && Quantize(slider.Value, decimals) != Quantize(value, decimals);

        private static string AmpsFormat(int decimals) => decimals switch
        {
            0 => "0",
            1 => "0.0",
            _ => "0.000",
        };

        private static string Amps(int? milliAmps, int decimals) => milliAmps is int value
            ? (value / 1000.0).ToString(AmpsFormat(decimals))
            : "--";

        private void RailSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            // Bounds are set while the card renders too, so this handler must not react to
            // its own bind: only a user's move clears the note and re-gates the button.
            if (_railBinding || btnRailApply == null)
                return;

            _railLimitsNote = null;
            UpdateRailReadouts();
            UpdateRailCaution();
            UpdateRailApply(_railLimits is { Reason: null } &&
                            (_romOverride?.State == UCU.Contracts.GpuRomOverrideStates.Unlocked ||
                             _romOverride?.State == UCU.Contracts.GpuRomOverrideStates.Uncertain));
        }

        private async void btnRailApply_Click(object sender, RoutedEventArgs e) => await ApplyRailLimitsAsync();

        private async Task ApplyRailLimitsAsync()
        {
            // Live: no restart, no blank screen, and no consent box — the consent belongs to
            // the unlock. The gates the card renders are re-checked so a stale frame cannot
            // fire a write it was showing as unavailable.
            if (_railLimitsBusy || _gpuTuning == null || _railLimits is not { Reason: null } || !spRailControls.IsEnabled)
            {
                return;
            }

            UCU.Contracts.GpuRailLimitsDto rails = _railLimits;

            // Only the rails that moved are sent; null means "leave this one alone".
            int? nvvdd = Changed(sdRailNvvdd, rails.NvvddMilliamps, 0) ? Quantize(sdRailNvvdd.Value, 0) : null;
            int? msvdd = Changed(sdRailMsvdd, rails.MsvddMilliamps, 0) ? Quantize(sdRailMsvdd.Value, 0) : null;
            int? board = Changed(sdRailBoardInput, rails.BoardInputMilliamps, 3) ? Quantize(sdRailBoardInput.Value, 3) : null;

            if (nvvdd == null && msvdd == null && board == null)
                return;

            _railLimitsBusy = true;
            _railLimitsError = null;
            _railLimitsNote = null;
            RenderRailLimits();

            try
            {
                UCU.Contracts.GpuRailLimitsDto answer =
                    await _gpuTuning.SetRailLimitsAsync(nvvdd, msvdd, board).ConfigureAwait(true);

                if (answer.Reason == null)
                {
                    _railLimits = answer;
                    _railLimitsNote = LocalizationService.Format(
                        "Applied: {0} A GPU, {1} A memory, {2} A board input",
                        Amps(answer.NvvddMilliamps, 0), Amps(answer.MsvddMilliamps, 0), Amps(answer.BoardInputMilliamps, 3));
                }
                else
                {
                    // A refusal carries no numbers: keep the ones on screen and say why.
                    _railLimitsError = answer.Reason;
                }
            }
            catch (Exception ex)
            {
                // A transport failure is not a verdict, so read back what the rails carry now
                // and keep the exception on screen either way.
                DebugLog($"GPU rail limits write failed: {ex.Message}");
                _railLimitsError = ex.Message;

                try
                {
                    _railLimits = await _gpuTuning.GetRailLimitsAsync().ConfigureAwait(true);
                }
                catch (Exception reread)
                {
                    DebugLog($"GPU rail limits re-read failed: {reread.Message}");
                }
            }
            finally
            {
                _railLimitsBusy = false;
                RenderRailLimits();
            }

            // The status card shows the rails the driver reports, and it reads them from the
            // override state: refresh it after a successful write, and leave a failure's own
            // message on screen untouched.
            if (_railLimitsError == null)
                await RefreshRomOverrideAsync();
        }

        #endregion
    }
}
