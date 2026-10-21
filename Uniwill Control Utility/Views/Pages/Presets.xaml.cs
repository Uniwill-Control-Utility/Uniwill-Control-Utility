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

        public Task OnNavigatedToAsync()
        {
            try
            {
                // Subscribe to DiagnosticLogger events so deep code (EC, NVML) posts to our UI log
                DiagnosticLogger.OnMessage += DiagnosticLogger_OnMessage;

                applyPlatformVisibility();
                applyPowerLimits();
                setProfileTooltips();
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
            }
            catch (Exception ex)
            {
                // Broad catch: OnNavigatedToAsync can fail from any UI control
                // initialization issue (missing resources, null controls, etc.).
                _logger.LogError(ex, "Failed to navigate to Presets page");
            }

            return Task.CompletedTask;
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
                line.Contains("[UniwillECService.") ||
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
                cbApplyOnStartup.IsChecked = Settings.Default.ApplyOnStart;
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

        private void rbCoMode_Click(object sender, RoutedEventArgs e) => updateCoVisibility();

        private void updateCoVisibility()
        {
            bool unified = rbCoUnified.IsChecked == true;
            if (unified)
            {
                dividerCoPerCcd.Visibility = Visibility.Collapsed;
                gridCustomCoUnifiedSlider.Visibility = Visibility.Visible;
                gridCustomCoCcd0.Visibility = Visibility.Collapsed;
                gridCustomCoCcd1.Visibility = Visibility.Collapsed;
            }
            else
            {
                dividerCoPerCcd.Visibility = Visibility.Visible;
                gridCustomCoUnifiedSlider.Visibility = Visibility.Collapsed;
                gridCustomCoCcd0.Visibility = Visibility.Visible;
                gridCustomCoCcd1.Visibility = Visibility.Visible;
            }
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

        private void applyPlatformVisibility()
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
                    var snapshot = _gpuInventory.GetSnapshotAsync().GetAwaiter().GetResult();
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

            // Hide GPU clock controls when EnableGpuClockControl is OFF
            bool gpuClockEnabled = Settings.Default.EnableGpuClockControl;
            GpuClockControlsPanel.Visibility = gpuClockEnabled ? Visibility.Visible : Visibility.Collapsed;
            tbCoreLabel.Visibility = gpuClockEnabled ? Visibility.Visible : Visibility.Collapsed;
            tbCoreClock.Visibility = gpuClockEnabled ? Visibility.Visible : Visibility.Collapsed;
            tbMemLabel.Visibility = gpuClockEnabled ? Visibility.Visible : Visibility.Collapsed;
            tbMemClock.Visibility = gpuClockEnabled ? Visibility.Visible : Visibility.Collapsed;
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

            sdNvPower.Minimum = min;
            sdNvPower.Maximum = max;

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

        private void selectProfile(string profile, bool apply = false)
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

            // Stock presets always apply on startup - hide the checkbox for them.
            bool isBuiltIn = Services.PresetProfileApplier.IsBuiltInProfile(profile);
            cbApplyOnStartup.Visibility = isBuiltIn ? Visibility.Collapsed : Visibility.Visible;

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
                            ApplyCustomFanCurveFromSettings(_savedCustomFanSettings);
                        }
                        else
                        {
                            // No saved Custom fan settings — apply default Balanced curve
                            DebugLog($"selectProfile: No saved Custom fan settings, applying default Balanced");
                            ApplyFanCurveFromPreset("Balanced");
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
                    bool coUnified = rbCoUnified.IsChecked == true;
                    if (coUnified)
                    {
                        tbCoOffset.Text = $"{(int)sdCoUnified.Value:+0;-0;0}";
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
        }

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
                        ecGate.ApplyOemStockMode(PresetProfileApplier.OemModeIndexFor(_currentProfile));
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

                        int result = NvTuning.ApplyVfCurveOffsets_ClockBoostTable(offsets);
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
                        ec.ApplyCpuPowerLimits(pl1, pl2);
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
                rbCoUnified.IsChecked = preset.CpuCoUnified;
                rbCoPerCcd.IsChecked = !preset.CpuCoUnified;
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

        private void cbApplyOnStartup_Click(object sender, RoutedEventArgs e)
        {
            Settings.Default.ApplyOnStart = cbApplyOnStartup.IsChecked == true;
            Settings.Default.Save();
        }

        private void btnApply_Click(object sender, RoutedEventArgs e)
        {
            SaveCustomPreset(_customPresetSlot);
            SaveCustomValues(); // Also save to Settings for backward compatibility (auto-reapply)
            _ = applyProfileAsync();
        }

        #endregion

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

                // Curve Optimizer — unified mode uses set-coall, per-CCD uses set-coper.
                // Skip entirely if the user disabled CPU voltage control (BIOS-managed).
                if (CpuVoltageEnabled)
                {
                    bool coUnified = rbCoUnified.IsChecked == true;
                    if (coUnified)
                    {
                        int coValue = (int)sdCoUnified.Value;
                        uint coEncoded = coValue < 0
                            ? (uint)(0x100000 - Math.Abs(coValue))
                            : (uint)coValue;
                        parts.Add($"--set-coall={coEncoded}");
                    }
                    else
                    {
                        // Per-CCD mode: use set-coper for each core.
                        // Encoding (Renoir+/AM5): ((ccd << 8) | core_in_ccd) << 20 | offset20
                        // offset20: 20-bit biased — negative = 0x100000 - |offset|
                        // CCD0 = cores 0-7, CCD1 = cores 8-15
                        int coCcd0 = (int)sdCoCcd0.Value;
                        int coCcd1 = (int)sdCoCcd1.Value;
                        for (int ccd = 0; ccd < 2; ccd++)
                        {
                            int offset = ccd == 0 ? coCcd0 : coCcd1;
                            for (int core = 0; core < 8; core++)
                            {
                                uint encoded = BuildCoperArg(ccd, core, offset);
                                parts.Add($"--set-coper={encoded}");
                            }
                        }
                    }
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
                    else
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

        /// <summary>
        /// Encodes a per-core Curve Optimizer argument (Renoir+/AM5 format).
        /// Layout: ((ccd << 8) | core) << 20 | offset20
        /// offset20: 20-bit biased — negative = 0x100000 - |offset|
        /// </summary>
        private static uint BuildCoperArg(int ccd, int core, int offset)
        {
            int magnitude = Math.Min(Math.Abs(offset), 0xFFFFF);
            uint encoded20 = offset < 0
                ? (uint)((0x100000 - magnitude) & 0xFFFFF)
                : (uint)(magnitude & 0xFFFFF);
            uint prefix = (uint)(((ccd << 8) | (core & 7)) << 20);
            return prefix | encoded20;
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
        private void ApplyCustomFanCurveFromSettings(FanControlSettings settings)
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

                _deviceApplier.ApplyEcFanFromPreset(
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
                _deviceApplier.ApplyEcFanFromPreset(
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
        private void ApplyFanCurveFromPreset(string fanPresetName)
        {
            if (_deviceApplier is null) return;

            DebugLog($"ApplyFanCurveFromPreset: {fanPresetName}");

            // Update the FanControlSettings so the Fan Control page shows the correct preset
            var settings = FanControlSettingsService.Load();
            settings.UnifiedMode = true;
            settings.UnifiedPreset = fanPresetName;
            FanControlSettingsService.Save(settings);

            // Apply to EC immediately via DeviceApplier
            _deviceApplier.ApplyEcFanFromPreset(
                unifiedMode: true,
                presetName: fanPresetName,
                customDuties: null,
                cpuPresetDuties: null,
                gpuPresetDuties: null);

            DebugLog($"ApplyFanCurveFromPreset: {fanPresetName} applied to EC");
        }

        #endregion
    }
}
