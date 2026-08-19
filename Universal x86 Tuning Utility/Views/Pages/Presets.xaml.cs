using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Universal_x86_Tuning_Utility.Models;
using Universal_x86_Tuning_Utility.Properties;
using Universal_x86_Tuning_Utility.Scripts;
using Universal_x86_Tuning_Utility.Scripts.GPUs.NVIDIA;
using Universal_x86_Tuning_Utility.Scripts.Intel_Backend;
using Universal_x86_Tuning_Utility.Scripts.Misc;
using Universal_x86_Tuning_Utility.Services;
using Wpf.Ui.Abstractions.Controls;

namespace Universal_x86_Tuning_Utility.Views.Pages
{
    public partial class Presets : INavigationAware
    {
        private readonly ILogger<Presets> _logger;
        private readonly GpuInventoryService? _gpuInventory;
        private readonly WaterCoolerService? _waterCooler;
        private string _currentProfile = "Gaming";

        // V-F curve preset state
        private VfCurvePreset[] _vfPresets = Array.Empty<VfCurvePreset>();
        private int _vfActivePreset = -1;
        private Views.Windows.VfCurveEditorWindow? _vfEditorWindow;

        // DEBUG: on-page log viewer
        private readonly List<string> _debugLines = new();
        private void DebugLog(string message)
        {
            var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}";
            _debugLines.Add(line);
            Application.Current.Dispatcher.Invoke(DispatcherPriority.Background, new Action(() =>
            {
                txtDebugLog.Text = string.Join("", _debugLines);
                var parent = VisualTreeHelper.GetParent(txtDebugLog) as ScrollViewer;
                if (parent != null) parent.ScrollToEnd();
            }));
        }

        private void btnDumpGpuEc_Click(object sender, RoutedEventArgs e)
        {
            var ec = RyzenAdj_To_UXTU.EcService;
            if (ec == null)
            {
                DebugLog("Dump GPU EC: EC service unavailable");
                return;
            }

            // Dump the full GPU power control register range and surrounding area
            DebugLog("=== GPU EC Register Dump ===");

            // Key GPU power registers
            ushort[] keyRegs =
            {
                0x0727, // CUSTOM_PROFILE
                0x0743, // CTGP_DB_ENABLE
                0x0744, // CTGP_DB_CTGP_OFFSET
                0x0745, // CTGP_DB_TPP_OFFSET
                0x0746, // CTGP_DB_DB_OFFSET
                0x0747, // unknown (fan-curve overlap)
            };

            foreach (var reg in keyRegs)
            {
                try
                {
                    byte val = ec.ReadECRegister(reg);
                    DebugLog($"  0x{reg:X3} = 0x{val:X2} ({val})");
                }
                catch (Exception ex)
                {
                    DebugLog($"  0x{reg:X3} = READ FAILED: {ex.Message}");
                }
            }

            // Sweep 0x0740-0x075F for anything interesting
            DebugLog("  Sweep 0x0740-0x075F:");
            for (ushort reg = 0x0740; reg <= 0x075F; reg++)
            {
                try
                {
                    byte val = ec.ReadECRegister(reg);
                    DebugLog($"    0x{reg:X3} = 0x{val:X2}");
                }
                catch
                {
                    // skip unreadable
                }
            }

            // Also sweep 0x0720-0x073F (CUSTOM_PROFILE area)
            DebugLog("  Sweep 0x0720-0x073F:");
            for (ushort reg = 0x0720; reg <= 0x073F; reg++)
            {
                try
                {
                    byte val = ec.ReadECRegister(reg);
                    DebugLog($"    0x{reg:X3} = 0x{val:X2}");
                }
                catch
                {
                    // skip unreadable
                }
            }

            DebugLog("=== End Dump ===");
        }

        private void btnDumpCpuEc_Click(object sender, RoutedEventArgs e)
        {
            var ec = RyzenAdj_To_UXTU.EcService;
            if (ec == null)
            {
                DebugLog("Dump CPU EC: EC service unavailable");
                return;
            }

            DebugLog("=== CPU EC Register Dump ===");

            // Key CPU power limit registers
            DebugLog("  Power Limits (0x0780-0x078F):");
            var cpuPowerRegs = new (ushort Reg, string Label)[]
            {
                (0x0782, "BIOS_OEM_2"),
                (0x0783, "PL1_SETTING"),
                (0x0784, "PL2_SETTING"),
                (0x0785, "PL4_SETTING"),
                (0x0786, "TCC_OFFSET"),
                (0x0787, "FAN_SWITCH_SPEED"),
                (0x0788, "PL5_SETTING"),
                (0x0789, "unknown"),
                (0x078A, "unknown"),
                (0x078B, "unknown"),
                (0x078C, "KBD_STATUS"),
            };

            foreach (var (reg, label) in cpuPowerRegs)
            {
                try
                {
                    byte val = ec.ReadECRegister(reg);
                    DebugLog($"    0x{reg:X3} ({label,-18}) = 0x{val:X2} ({val})");
                }
                catch (Exception ex)
                {
                    DebugLog($"    0x{reg:X3} ({label,-18}) = FAILED: {ex.Message}");
                }
            }

            // Power management activation (0x0720-0x072F)
            DebugLog("  Power Mgmt Activation (0x0720-0x072F):");
            var pmRegs = new (ushort Reg, string Label)[]
            {
                (0x0726, "power_mgmt_1"),
                (0x0727, "CUSTOM_PROFILE"),
                (0x0741, "MANUAL_MODE"),
                (0x0751, "MANUAL_FAN_CTRL"),
            };
            foreach (var (reg, label) in pmRegs)
            {
                try
                {
                    byte val = ec.ReadECRegister(reg);
                    DebugLog($"    0x{reg:X3} ({label,-18}) = 0x{val:X2} (0b{val:B8})");
                }
                catch (Exception ex)
                {
                    DebugLog($"    0x{reg:X3} ({label,-18}) = FAILED: {ex.Message}");
                }
            }

            // MODE_INDEX and surrounding
            DebugLog("  Mode/Index (0x07A0-0x07AF):");
            for (ushort reg = 0x07A0; reg <= 0x07AF; reg++)
            {
                try
                {
                    byte val = ec.ReadECRegister(reg);
                    DebugLog($"    0x{reg:X3} = 0x{val:X2}");
                }
                catch { /* skip unreadable */ }
            }

            // Universal fan control area (0x07C0-0x07CF)
            DebugLog("  Fan/Universal Ctrl (0x07C0-0x07CF):");
            for (ushort reg = 0x07C0; reg <= 0x07CF; reg++)
            {
                try
                {
                    byte val = ec.ReadECRegister(reg);
                    DebugLog($"    0x{reg:X3} = 0x{val:X2}");
                }
                catch { /* skip unreadable */ }
            }

            DebugLog("=== End CPU Dump ===");
        }

        private void btnCopyDebugLog_Click(object sender, RoutedEventArgs e)
        {
            Clipboard.SetText(string.Join("", _debugLines));
        }

        private void btnClearDebugLog_Click(object sender, RoutedEventArgs e)
        {
            _debugLines.Clear();
            txtDebugLog.Text = "";
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

        public Presets(ILogger<Presets> logger, GpuInventoryService? gpuInventory = null, WaterCoolerService? waterCooler = null)
        {
            _logger = logger;
            _gpuInventory = gpuInventory;
            _waterCooler = waterCooler;
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
            if (line.Contains("[RyzenAdj_To_UXTU.") ||
                line.Contains("[NvTuning.") ||
                line.Contains("[UniwillECService.") ||
                line.Contains("[Presets.") ||
                line.Contains("[Translate") ||
                line.Contains("[WinPower]"))
            {
                DebugLog(line);
            }
        }

        private void Page_Loaded(object sender, RoutedEventArgs e)
        {
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
            lblTjMax.Text = $"{sdTjMax.Value:F0} Â°C";

            lblCoAll.Text = $"{sdCoAll.Value:F0}";

            lblIntelPl1.Text = $"{sdIntelPl1.Value:F0} W";
            lblIntelPl2.Text = $"{sdIntelPl2.Value:F0} W";
            lblIntelVolt.Text = $"{sdIntelVolt.Value:F0} mV";

            lblNvPower.Text = $"{sdNvPower.Value:F0} W";
            lblNvDbOffset.Text = $"{sdNvDbOffset.Value:F0} W";
            lblNvCore.Text = VfCurveActive ? "Curve" : $"{sdNvCore.Value:F0} MHz";
            lblNvMem.Text = $"{sdNvMem.Value:F0} MHz";
        }

        private void applyPlatformVisibility()
        {
            // Hide AMD-specific controls on Intel
            if (Family.TYPE == Family.ProcessorType.Intel)
            {
                sdAmdCoAll.Visibility = Visibility.Collapsed;
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
                int cpuMax = limits?.CpuPl1Sustained ?? 160;

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

                string turboGpu = turboProfile != null
                    ? $"TGP: {turboProfile.GpuTgp}W | Dynamic Boost: On (+{turboProfile.DynamicBoostOffset}W)"
                    : "TGP: 150W | Dynamic Boost: On (+25W)";

                btnOffice.ToolTip = CreateTooltip("Office", "CPU Power Limits", officeCpu, "GPU Power Limits", officeGpu);
                btnGaming.ToolTip = CreateTooltip("Gaming", "CPU Power Limits", gamingCpu, "GPU Power Limits", gamingGpu);
                btnTurbo.ToolTip = CreateTooltip("Turbo",
                    "CPU Power Limits", $"PL1: {cpuMax}W | PL2: {cpuMax}W{coolingLabel}",
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
                SaveCustomValues();
                selectProfile("Custom", apply: false);
            }

        private void selectProfile(string profile, bool apply = false)
        {
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
            // Custom: update labels if custom panel is visible
            if (profile == "Custom")
            {
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
                DebugLog($"selectProfile: {profile} â€” apply");
                _ = applyProfileAsync();
            }
            else
            {
                DebugLog($"selectProfile: {profile} â€” no apply (page load / Custom)");
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
                tbTjMax.Text = "-- \u00B0C";
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
                await RyzenAdj_To_UXTU.TranslateAsync(command, appliedName: _currentProfile + " Profile", localizeAppliedName: false);
                DebugLog($"  TranslateAsync completed");

                // Apply V-F curve offsets (if curve mode is active) after the uniform
                // clock offset has been applied via TranslateAsync.
                // Reload from file because the editor window auto-saves independently.
                if (VfCurveActive)
                {
                    var fresh = VfCurvePresetStore.Load();
                    int activeSlot = _vfActivePreset >= 0 ? _vfActivePreset : 0;
                    var offsets = activeSlot < fresh.Length && fresh[activeSlot].HasChanges
                        ? fresh[activeSlot].Offsets : new Dictionary<int, int>();
                    if (offsets.Count > 0)
                    {
                        int written = NvTuning.ApplyVfCurveOffsets(offsets);
                        DebugLog($"  V-F curve applied: {written}/{offsets.Count} points written");
                    }
                }

                // Apply EC CPU power limits AFTER SMU commands. The EC enforces a hardware
                // ceiling that clamps SMU settings from above. Without these
                // EC writes, the CPU is capped at ~130W regardless of SMU power limit values.
                if (Family.TYPE == Family.ProcessorType.Amd_Apu)
                {
                    var ec = RyzenAdj_To_UXTU.EcService;
                    if (ec != null)
                    {
                        int pl1 = (int)sdSustained.Value;
                        int pl2 = (int)sdBoost.Value;
                        DebugLog($"  Applying EC CPU power limits: PL1={pl1}W PL2={pl2}W");
                        ec.ApplyCpuPowerLimits(pl1, pl2);
                        DebugLog($"  EC CPU power limits applied");
                    }
                    else
                    {
                        DebugLog($"  EC service unavailable â€” CPU power limits may be clamped");
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
                // The original UXTU does not use --Win-CPU at all.
                //
                // Values are in milliwatts (W * 1000), matching original UXTU convention.
                int slowLimit = (int)sdSustained.Value * 1000;   // Sustained â†’ slow-limit
                int fastLimit = (int)sdBoost.Value * 1000;   // Boost â†’ fast-limit

                parts.Add($"--slow-limit={slowLimit}");
                parts.Add($"--fast-limit={fastLimit}");

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

            // NVIDIA â€” GPU power control via EC Dynamic Boost registers (0x0743-0x0746).
            // These GPU-specific EC writes are independent of the CPU power clamp issue.
            if (sdNvidia.Visibility == Visibility.Visible)
            {
                // In curve mode, core offset is 0 (per-point offsets are applied separately via ApplyVfCurveOffsets).
                int core = VfCurveActive ? 0 : (int)sdNvCore.Value;
                int mem = (int)sdNvMem.Value;
                int tgp = (int)sdNvPower.Value;
                bool dbEnabled = togDynamicBoost.IsChecked == true;
                int dbOffset = dbEnabled ? (int)sdNvDbOffset.Value : 0;
                int totalWatts = tgp + dbOffset;
                parts.Add($"--NVIDIA-Clocks=0-{core}-{mem}-{totalWatts}-{dbOffset}");
            }

            return string.Join(" ", parts) + " ";
        }

        #endregion

        private void SizeSlider_TouchDown(object sender, TouchEventArgs e)
        {
            e.Handled = true;
        }

        #region V-F Curve Editor (popup window)

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

            // Load presets if not already loaded
            if (_vfPresets.Length == 0)
                _vfPresets = VfCurvePresetStore.Load();

            // Load the curve editor (with active preset if one is selected)
            Dictionary<int, int>? offsets = null;
            if (_vfActivePreset >= 0 && _vfActivePreset < _vfPresets.Length && _vfPresets[_vfActivePreset].HasChanges)
                offsets = _vfPresets[_vfActivePreset].Offsets;

            // Open non-modal curve editor window — user can interact with both windows.
            // The editor auto-saves to file, so Apply reads fresh data on each click.
            _vfEditorWindow = new Views.Windows.VfCurveEditorWindow(offsets);
            _vfEditorWindow.Owner = Application.Current.MainWindow;
            _vfEditorWindow.Closed += (_, _) => _vfEditorWindow = null;
            _vfEditorWindow.CurveChanged += (_, _) => updateAllLabels();
            _vfEditorWindow.Show();

            // Track active preset slot
            if (_vfActivePreset < 0) _vfActivePreset = 0;
        }

        #endregion

    }
}
