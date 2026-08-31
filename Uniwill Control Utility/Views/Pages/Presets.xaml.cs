using Microsoft.Extensions.Logging;
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
        private readonly WaterCoolerService? _waterCooler;
        private readonly DeviceApplier? _deviceApplier;
        private string _currentProfile = "Gaming";

        // V-F curve preset state
        private VfCurvePreset[] _vfPresets = Array.Empty<VfCurvePreset>();
        private int _vfActivePreset = -1;
        private Views.Windows.VfCurveEditorWindow? _vfEditorWindow;
        private bool _userTouchedCoreSlider; // true when user manually changes the core clock slider

        // Custom profile fan curve persistence
        private FanControlSettings? _savedCustomFanSettings;

        // Diagnostic logging (visible in Visual Studio Output / debug console)
        private void DebugLog(string message)
        {
            Debug.WriteLine($"[Presets] {message}");
        }

        // Stored profile values for status card (independent of slider UI state).
        private double _profileSustained = 75;
        private double _profileBoost = 85;
        private int _profileTjMax = 95;
        private int _profileCoAll = 0;
        private double _profileGpuTgp = 150;
        private bool _profileGpuDb = false;
        private double _profileGpuDbOffset = 0;
        private double _profileIntelPl1 = 45;
        private double _profileIntelPl2 = 60;
 
        public Presets(ILogger<Presets> logger, GpuInventoryService? gpuInventory = null, WaterCoolerService? waterCooler = null, DeviceApplier? deviceApplier = null)
        {
            _logger = logger;
            _gpuInventory = gpuInventory;
            _waterCooler = waterCooler;
            _deviceApplier = deviceApplier;
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
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to navigate to Presets page");
            }

            return Task.CompletedTask;
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
                line.Contains("[VfCurveEditor."))
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

            // For Custom profile, restore saved slider values from settings.
            if (_currentProfile == "Custom")
            {
                LoadCustomValues();
            }

            // Initialize slider defaults from stored profile values so the status card
            // shows correct values even when the custom panel is collapsed.
            sdSustained.Value = _profileSustained;
            sdBoost.Value = _profileBoost;
            sdTjMax.Value = _profileTjMax;
            sdCoAll.Value = 0;

            sdNvPower.Value = _profileGpuTgp;
            togDynamicBoost.IsChecked = _profileGpuDb;
            sdNvDbOffset.Value = _profileGpuDbOffset;
            spNvDbOffset.Visibility = _profileGpuDb ? Visibility.Visible : Visibility.Collapsed;

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
            updateStatus(_currentProfile);

            // Update labels if custom panel is visible
            if (customScroll.Visibility == Visibility.Visible)
            {
                updateAllLabels();
            }
        }

        private void Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (sender == sdNvCore)
                _userTouchedCoreSlider = true;
            updateAllLabels();
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

            lblCoAll.Text = $"{sdCoAll.Value:F0}";

            lblIntelPl1.Text = $"{sdIntelPl1.Value:F0} W";
            lblIntelPl2.Text = $"{sdIntelPl2.Value:F0} W";
            lblIntelVolt.Text = $"{sdIntelVolt.Value:F0} mV";
 
            lblNvPower.Text = $"{sdNvPower.Value:F0} W";
            lblNvDbOffset.Text = $"{sdNvDbOffset.Value:F0} W";
            // Show "Curve" only if curve is active AND user hasn't touched the slider.
            // If the user manually changes the slider, the curve is ignored for Apply.
            lblNvCore.Text = (VfCurveActive && !_userTouchedCoreSlider) ? "Curve" : $"{sdNvCore.Value:F0} MHz";
            lblNvMem.Text = $"{sdNvMem.Value:F0} MHz";
        }

        private void applyPlatformVisibility()
        {
            // Hide AMD-specific controls on Intel
            if (Family.TYPE == Family.ProcessorType.Intel)
            {
                sdAmdCoAll.Visibility = Visibility.Collapsed;
                gridStatusTjMax.Visibility = Visibility.Collapsed;
                gridCustomTjMax.Visibility = Visibility.Collapsed;
                dividerAfterTjMax.Visibility = Visibility.Collapsed;
            }
            // Hide Intel-specific controls on AMD
            else
            {
                sdIntelTuning.Visibility = Visibility.Collapsed;
            }

            // NVIDIA visibility â€” check GPU inventory or fall back to NvTuning probe
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
                    // Fall through to NvTuning probe
                }
            }
            if (!hasNvidia)
            {
                hasNvidia = !string.IsNullOrEmpty(NvTuning.GetGpuName());
            }
            sdNvidia.Visibility = hasNvidia ? Visibility.Visible : Visibility.Collapsed;
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

        private void applyPowerLimits()
        {
            var limits = GetPowerLimits();
            if (limits == null)
                return;

            var pl = limits;

            if (Family.TYPE == Family.ProcessorType.Amd_Apu)
            {
                sdSustained.Maximum = pl.CpuPl1Sustained;
                sdBoost.Maximum = pl.CpuPl2Boost;
            }
            else if (Family.TYPE == Family.ProcessorType.Intel)
            {
                sdIntelPl1.Maximum = pl.CpuPl1Sustained;
                sdIntelPl2.Maximum = pl.CpuPl2Boost;
            }

            // GPU TGP slider â€” limit to the GPU's baseline TGP
            sdNvPower.Maximum = pl.GpuTgpBaseline;

            // Dynamic Boost offset â€” max is the DB max from SKU config
            sdNvDbOffset.Maximum = pl.DynamicBoostMax;
        }

        private void setProfileTooltips()
        {
            bool hydro = IsHydroConnected;
            string coolingLabel = hydro ? " (water)" : " (air)";
            var limits = GetPowerLimits();

            // Try to get SKU-specific profile data for tooltips
            Models.SkuProfileEntry? officeProfile = Services.SkuResolver.GetProfile("office");
            Models.SkuProfileEntry? gamingProfile = Services.SkuResolver.GetProfile("gaming");
            Models.SkuProfileEntry? turboProfile = Services.SkuResolver.GetProfile("turbo");

            if (Family.TYPE == Family.ProcessorType.Amd_Apu)
            {
                int cpuMax = limits?.CpuPl1Sustained ?? 135;

                string officeCpu = officeProfile != null
                    ? $"Sustained: {officeProfile.CpuPl1}W | Boost: {officeProfile.CpuPl2}W"
                    : "Sustained: 45W | Boost: 45W";
                string officeGpu = officeProfile != null
                    ? $"TGP: {officeProfile.GpuTgp}W | Dynamic Boost: Off"
                    : "TGP: 95W | Dynamic Boost: Off";

                string gamingCpu = gamingProfile != null
                    ? $"Sustained: {gamingProfile.CpuPl1}W | Boost: {gamingProfile.CpuPl2}W"
                    : "Sustained: 75W | Boost: 85W";
                string gamingGpu = gamingProfile != null
                    ? $"TGP: {gamingProfile.GpuTgp}W | Dynamic Boost: Off"
                    : "TGP: 150W | Dynamic Boost: Off";

                string turboGpu = turboProfile != null
                    ? $"TGP: {turboProfile.GpuTgp}W | Dynamic Boost: On (+{turboProfile.DynamicBoostOffset}W)"
                    : "TGP: 150W | Dynamic Boost: On (+25W)";

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
                string officeGpu = officeProfile != null
                    ? $"TGP: {officeProfile.GpuTgp}W | Dynamic Boost: Off"
                    : "TGP: 95W | Dynamic Boost: Off";

                string gamingCpu = gamingProfile != null
                    ? $"PL1: {gamingProfile.CpuPl1}W | PL2: {gamingProfile.CpuPl2}W"
                    : "PL1: 45W | PL2: 60W";
                string gamingGpu = gamingProfile != null
                    ? $"TGP: {gamingProfile.GpuTgp}W | Dynamic Boost: Off"
                    : "TGP: 150W | Dynamic Boost: Off";

                string turboCpu = turboProfile != null
                    ? $"PL1: {turboProfile.CpuPl1}W | PL2: {turboProfile.CpuPl2}W"
                    : "PL1: 160W | PL2: 200W";
                string turboGpu = turboProfile != null
                    ? $"TGP: {turboProfile.GpuTgp}W | Dynamic Boost: On (+{turboProfile.DynamicBoostOffset}W)"
                    : "TGP: 150W | Dynamic Boost: On (+25W)";

                btnOffice.ToolTip = CreateTooltip("Office", "CPU Power Limits", officeCpu, "GPU Power Limits", officeGpu);
                btnGaming.ToolTip = CreateTooltip("Gaming", "CPU Power Limits", gamingCpu, "GPU Power Limits", gamingGpu);
                btnTurbo.ToolTip = CreateTooltip("Turbo",
                    "CPU Power Limits", turboCpu,
                    "GPU Power Limits", turboGpu);

                btnCustom.ToolTip = "Apply your own custom power settings using the sliders below.";
            }
            else
            {
                btnOffice.ToolTip = "Office power profile â€” low power, quiet operation";
                btnGaming.ToolTip = "Gaming power profile â€” balanced performance";
                btnTurbo.ToolTip = "Turbo power profile â€” maximum performance";
                btnCustom.ToolTip = "Apply your own custom power settings using the sliders below.";
            }
        }

        private static string CreateTooltip(string profile,
            string cpuSection, string cpuValues,
            string gpuSection, string gpuValues)
        {
            return $"{profile}\n\n{cpuSection}\n{cpuValues}\n\n{gpuSection}\n{gpuValues}";
        }

        private void loadSavedProfile()
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

            selectProfile(saved, apply: false);
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
                SaveCustomValues();
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

            // Show/hide custom panel
            customScroll.Visibility = profile == "Custom" ? Visibility.Visible : Visibility.Collapsed;

            // Set profile values from SKU database (falls back to hardcoded defaults)
            var limits = GetPowerLimits();
            int cpuMax = limits?.CpuPl1Sustained ?? 135;
            string profileKey = profile.ToLowerInvariant();
            var skuProfile = Services.SkuResolver.GetProfile(profileKey);

            if (profile == "Office")
            {
                if (skuProfile != null)
                {
                    setProfileValues(skuProfile.CpuPl1, skuProfile.CpuPl2, 95,
                        skuProfile.GpuTgp, skuProfile.DynamicBoost, skuProfile.DynamicBoostOffset, 0);
                }
                else
                {
                    setProfileValues(45, 45, 95, 95, false, 0, 0);
                }
                _profileIntelPl1 = skuProfile?.CpuPl1 ?? 25;
                _profileIntelPl2 = skuProfile?.CpuPl2 ?? 35;
            }
            else if (profile == "Gaming")
            {
                if (skuProfile != null)
                {
                    setProfileValues(skuProfile.CpuPl1, skuProfile.CpuPl2, 95,
                        skuProfile.GpuTgp, skuProfile.DynamicBoost, skuProfile.DynamicBoostOffset, 0);
                }
                else
                {
                    setProfileValues(75, 85, 95, 150, false, 0, 0);
                }
                _profileIntelPl1 = skuProfile?.CpuPl1 ?? 45;
                _profileIntelPl2 = skuProfile?.CpuPl2 ?? 60;
            }
            else if (profile == "Turbo")
            {
                if (Family.TYPE == Family.ProcessorType.Amd_Apu)
                {
                    int gpuTgp = skuProfile?.GpuTgp ?? 150;
                    bool db = skuProfile?.DynamicBoost ?? true;
                    int dbOffset = skuProfile?.DynamicBoostOffset ?? 25;
                    setProfileValues(cpuMax, cpuMax, 95, gpuTgp, db, dbOffset, 0);
                }
                else
                {
                    _profileIntelPl1 = cpuMax;
                    _profileIntelPl2 = cpuMax;
                }
            }
            // Custom: restore saved slider values and update labels
            if (profile == "Custom")
            {
                LoadCustomValues();
                updateAllLabels();
            }

            // Update status display
            updateStatus(profile);

            // Save selection
            Settings.Default.presetProfile = profile;
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
                        if (_savedCustomFanSettings is not null)
                        {
                            // Restore Custom fan settings and apply to EC
                            FanControlSettingsService.Save(_savedCustomFanSettings);
                            DebugLog($"selectProfile: Restored and applying Custom fan settings");
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

                        // Apply mapped fan curve for default profiles
                        string fanPreset = profile switch
                        {
                            "Office" => "Silent",
                            "Gaming" => "Balanced",
                            "Turbo" => "Performance",
                            _ => "Balanced"
                        };
                        ApplyFanCurveFromPreset(fanPreset);
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
            bool gpuDb, int gpuDbOffset, int coAll)
        {
            // Store values for status card (independent of slider UI state)
            _profileSustained = sustained;
            _profileBoost = boost;
            _profileTjMax = tjMax;
            _profileGpuTgp = gpuTgp;
            _profileGpuDb = gpuDb;
            _profileGpuDbOffset = gpuDbOffset;
            _profileCoAll = coAll;

            // SMU values
            sdSustained.Value = sustained;
            sdBoost.Value = boost;
            sdTjMax.Value = tjMax;

            // Curve Optimizer
            sdCoAll.Value = coAll;

            // GPU
            sdNvPower.Value = gpuTgp;
            togDynamicBoost.IsChecked = gpuDb;
            sdNvDbOffset.Value = Math.Max(0, gpuDbOffset);
            spNvDbOffset.Visibility = gpuDb ? Visibility.Visible : Visibility.Collapsed;

            // Reset the flag when loading profile values — start fresh.
            _userTouchedCoreSlider = false;

            // Update labels
            updateAllLabels();
        }

        private void updateStatus(string profile)
        {
            tbActiveProfile.Text = profile;

            if (Family.TYPE == Family.ProcessorType.Amd_Apu)
            {
                tbPl1.Text = $"{_profileSustained:F0} W";
                tbPl2.Text = $"{_profileBoost:F0} W";
                tbTjMax.Text = $"{_profileTjMax} \u00B0C";
                tbCoAll.Text = $"{_profileCoAll:+0;-0;0}";
                tbGpuTgp.Text = $"{_profileGpuTgp:F0} W";
                tbGpuDb.Text = _profileGpuDb ? $"+{_profileGpuDbOffset:F0} W" : "Off";
            }
            else if (Family.TYPE == Family.ProcessorType.Intel)
            {
                tbPl1.Text = $"{_profileIntelPl1:F0} W";
                tbPl2.Text = $"{_profileIntelPl2:F0} W";
                tbTjMax.Text = $"{_profileTjMax} \u00B0C";
                tbCoAll.Text = "--";
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
                _logger.LogInformation($"Applying {_currentProfile} profile: {command}");

                // TranslateAsync() runs SMU commands on a background thread via Task.Run().
                // We must await it before writing EC registers, otherwise the EC writes
                // complete first and the SMU overwrites the ceiling after.
                await RyzenAdj_To_UCU.TranslateAsync(command, appliedName: _currentProfile + " Profile", localizeAppliedName: false);
                DebugLog($"  TranslateAsync completed");

                // Apply V-F curve offsets only on Custom profile.
                // Stock profiles (Office/Gaming/Turbo) always use the slider values (0 MHz),
                // even if a curve exists on disk from a previous Custom session.
                DebugLog($"  V-F curve check: VfCurveActive={VfCurveActive}, _userTouchedCoreSlider={_userTouchedCoreSlider}, _vfActivePreset={_vfActivePreset}, _currentProfile={_currentProfile}");
                if (_currentProfile == "Custom" && VfCurveActive && !_userTouchedCoreSlider)
                {
                    var fresh = VfCurvePresetStore.Load();
                    int activeSlot = _vfActivePreset >= 0 ? _vfActivePreset : 0;
                    var offsets = activeSlot < fresh.Length && fresh[activeSlot].HasChanges
                        ? fresh[activeSlot].Offsets : new Dictionary<int, int>();
                    DebugLog($"  V-F curve: activeSlot={activeSlot}, offsets.Count={offsets.Count}");
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
                                    if (point != null)
                                    {
                                        DebugLog($"  Live check idx={idx}: freq={point.FrequencyMHz}MHz @ {point.VoltageMv}mV, offset={offset}MHz");
                                    }
                                }
                            }
                        }
                    }
                }

                // Apply EC CPU power limits AFTER SMU commands. The EC enforces a hardware
                // ceiling that clamps SMU settings from above. Without these
                // EC writes, the CPU is capped at ~130W regardless of SMU power limit values.
                if (Family.TYPE == Family.ProcessorType.Amd_Apu)
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

                ToastNotification.ShowToastNotification($"{_currentProfile} Profile Applied",
                    $"The {_currentProfile.ToLower()} power profile has been applied!");

                // Do NOT save to Settings.Default.CommandString.
                // That field is shared with Custom Presets / Adaptive Mode auto-reapply.
                // Saving here causes the auto-reapply timer to re-apply stale SMU commands
                // that override EC power limits from this page.

                // For Custom profile, update stored values from sliders so the status
                // card reflects what was just applied (not whatever was loaded on startup).
                if (_currentProfile == "Custom")
                {
                    _profileSustained = sdSustained.Value;
                    _profileBoost = sdBoost.Value;
                    _profileTjMax = (int)sdTjMax.Value;
                    _profileCoAll = (int)sdCoAll.Value;
                    _profileIntelPl1 = sdIntelPl1.Value;
                    _profileIntelPl2 = sdIntelPl2.Value;
                     _profileGpuTgp = sdNvPower.Value;
                    _profileGpuDb = togDynamicBoost.IsChecked == true;
                    _profileGpuDbOffset = sdNvDbOffset.Value;
                }

                // Update status after apply
                updateStatus(_currentProfile);
            }
            catch (Exception ex)
            {
                DebugLog($"applyProfile EXCEPTION: {ex.GetType().Name}: {ex.Message}");
                _logger.LogError(ex, $"Failed to apply {_currentProfile} profile");
            }
        }

        private void ReadBackEcValues()
        {
            // CPU power limits are now set via SMU + EC writes.
            // Log the stored profile values so the user can verify what was sent.
            if (Family.TYPE == Family.ProcessorType.Amd_Apu)
            {
                DebugLog($"SMU applied: slow-limit={(int)_profileSustained}W fast-limit={(int)_profileBoost}W");
            }
            else if (Family.TYPE == Family.ProcessorType.Intel)
            {
                DebugLog($"MSR applied: PL1={_profileIntelPl1}W PL2={_profileIntelPl2}W");
            }
        }

        private void SaveCustomValues()
        {
            Settings.Default.customSustained = sdSustained.Value;
            Settings.Default.customBoost = sdBoost.Value;
            Settings.Default.customTjMax = sdTjMax.Value;
            Settings.Default.customCoAll = sdCoAll.Value;
            Settings.Default.customIntelPl1 = sdIntelPl1.Value;
            Settings.Default.customIntelPl2 = sdIntelPl2.Value;
            Settings.Default.customIntelVolt = sdIntelVolt.Value;
            Settings.Default.customNvPower = sdNvPower.Value;
            Settings.Default.customNvDb = togDynamicBoost.IsChecked == true;
            Settings.Default.customNvDbOffset = sdNvDbOffset.Value;
            Settings.Default.customNvCore = sdNvCore.Value;
            Settings.Default.customNvMem = sdNvMem.Value;
            Settings.Default.Save();
            DebugLog("Custom slider values saved");
        }

        private void LoadCustomValues()
        {
            sdSustained.Value = Settings.Default.customSustained;
            sdBoost.Value = Settings.Default.customBoost;
            sdTjMax.Value = Settings.Default.customTjMax;
            sdCoAll.Value = Settings.Default.customCoAll;
            sdIntelPl1.Value = Settings.Default.customIntelPl1;
            sdIntelPl2.Value = Settings.Default.customIntelPl2;
            sdIntelVolt.Value = Settings.Default.customIntelVolt;
            sdNvPower.Value = Settings.Default.customNvPower;
            togDynamicBoost.IsChecked = Settings.Default.customNvDb;
            sdNvDbOffset.Value = Settings.Default.customNvDbOffset;
            sdNvCore.Value = Settings.Default.customNvCore;
            sdNvMem.Value = Settings.Default.customNvMem;

            // Restore stored profile values for status card
            _profileSustained = sdSustained.Value;
            _profileBoost = sdBoost.Value;
            _profileTjMax = (int)sdTjMax.Value;
            _profileCoAll = (int)sdCoAll.Value;
            _profileIntelPl1 = sdIntelPl1.Value;
            _profileIntelPl2 = sdIntelPl2.Value;
            _profileGpuTgp = sdNvPower.Value;
            _profileGpuDb = togDynamicBoost.IsChecked == true;
            _profileGpuDbOffset = sdNvDbOffset.Value;

            DebugLog($"Custom slider values loaded: sustained={_profileSustained}W boost={_profileBoost}W tjMax={_profileTjMax}");
        }

        private void btnApply_Click(object sender, RoutedEventArgs e)
        {
            SaveCustomValues();
            _ = applyProfileAsync();
        }

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

                // Curve Optimizer
                if (sdCoAll.Value > 0)
                    parts.Add($"--set-coall={(byte)sdCoAll.Value}");
            }
            else if (Family.TYPE == Family.ProcessorType.Intel)
            {
                // Intel MSR commands
                parts.Add($"--intel-pl={sdIntelPl1.Value:F0},{sdIntelPl2.Value:F0}");
                if (sdIntelVolt.Value < 0)
                    parts.Add($"--intel-volt-cpu={sdIntelVolt.Value:F0}");
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
                    int core = (int)sdNvCore.Value;
                    int mem = (int)sdNvMem.Value;
                    int tgp = (int)sdNvPower.Value;
                    bool dbEnabled = togDynamicBoost.IsChecked == true;
                    int dbOffset = dbEnabled ? (int)sdNvDbOffset.Value : 0;
                    int totalWatts = tgp + dbOffset;
                    parts.Add($"--NVIDIA-Clocks=0-{core}-{mem}-{totalWatts}-{dbOffset}");
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
        /// Scans preset slots on disk for any non-zero offsets and activates the first one found.
        /// Called on startup so that _vfActivePreset is set even when the curve editor
        /// was last used in a previous app session.
        /// </summary>
        private void detectAndActivateVfCurve()
        {
            if (_vfActivePreset >= 0) return; // Already active from this session.
            var presets = VfCurvePresetStore.Load();
            for (int i = 0; i < presets.Length; i++)
            {
                if (presets[i].HasChanges)
                {
                    _vfActivePreset = i;
                    DebugLog($"V-F curve auto-detected on slot {i}");
                    return;
                }
            }
        }

        /// <summary> True when a V-F curve preset has non-zero offsets applied. Reloads from file for freshness. </summary>
        private bool VfCurveActive
        {
            get
            {
                if (_vfActivePreset < 0) return false;
                var fresh = VfCurvePresetStore.Load();
                int slot = _vfActivePreset >= 0 ? _vfActivePreset : 0;
                return slot < fresh.Length && fresh[slot].HasChanges;
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

            // Always reload from disk — the editor window auto-saves independently,
            // so the in-memory copy may be stale after a previous edit session.
            _vfPresets = VfCurvePresetStore.Load();

            // Default to preset slot 0 if not yet set.
            if (_vfActivePreset < 0) _vfActivePreset = 0;

            // Load the curve editor (with active preset if one is selected)
            Dictionary<int, int>? offsets = null;
            if (_vfActivePreset < _vfPresets.Length && _vfPresets[_vfActivePreset].HasChanges)
                offsets = _vfPresets[_vfActivePreset].Offsets;

            // Open non-modal curve editor window — user can interact with both windows.
            // The editor auto-saves to file, so Apply reads fresh data on each click.
            _vfEditorWindow = new Views.Windows.VfCurveEditorWindow(offsets);
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
