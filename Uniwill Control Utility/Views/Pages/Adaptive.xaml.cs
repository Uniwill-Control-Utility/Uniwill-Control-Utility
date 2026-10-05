using GameLib.Core;
using GameLib;
using LibreHardwareMonitor.Hardware;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Threading;
using Uniwill_Control_Utility.Properties;
using Uniwill_Control_Utility.Scripts;
using Uniwill_Control_Utility.Models;
using Uniwill_Control_Utility.Scripts.Misc;
using Uniwill_Control_Utility.Services;
using Uniwill_Control_Utility.Scripts.GPUs.NVIDIA;
using GameLib.Plugin.Steam.Model;
using Windows.Gaming.Preview.GamesEnumeration;
using System.ComponentModel;
using static Uniwill_Control_Utility.Scripts.Game_Manager;

namespace Uniwill_Control_Utility.Views.Pages
{
    public partial class Adaptive : Page
    {
        private readonly System.Windows.Threading.DispatcherTimer adaptiveMode = new System.Windows.Threading.DispatcherTimer();
        private readonly System.Windows.Threading.DispatcherTimer sensors = new System.Windows.Threading.DispatcherTimer();
        private readonly GpuInventoryService gpuInventory;

        // HydroUI connection tracking
        private HydroUIService? _hydroUIService;
        private bool _hydroConnected;

        public Adaptive(GpuInventoryService gpuInventory)
        {
            this.gpuInventory = gpuInventory;
            InitializeComponent();

            IsVisibleChanged += Page_IsVisibleChanged;
            _ = Tablet.TabletDevices;
            setUp();

            adaptiveMode.Interval = TimeSpan.FromSeconds(5);
            adaptiveMode.Tick += new EventHandler(adaptive_Tick);
            adaptiveMode.Start();

            sensors.Interval = TimeSpan.FromSeconds(5);
            sensors.Tick += new EventHandler(sensors_Tick);
            sensors.Start();

            nudPolling.Value = 5;

            cbAutoSwitch.IsChecked = Settings.Default.autoSwitch;

            // Initialize override checkboxes from persisted settings so they reflect
            // the state from the previous session. This must happen before setUp()
            // calls ToggleAdaptiveMode(), otherwise the checkboxes stay at their
            // XAML default (false) and get captured by loadPreset as "wasEnabled=false".
            cbxBs2ProEnabled.IsChecked = Settings.Default.AdaptiveBs2ProEnabled;
            cbxWcEnabled.IsChecked = Settings.Default.AdaptiveWcEnabled;
            cbxKbEnabled.IsChecked = Settings.Default.AdaptiveKeyboardEnabled;
            cbxEcFanEnabled.IsChecked = Settings.Default.AdaptiveEcFanEnabled;
            cbxCpuEnabled.IsChecked = Settings.Default.AdaptiveCpuEnabled;
            cbxGpuEnabled.IsChecked = Settings.Default.AdaptiveGpuEnabled;
            cbxLbEnabled.IsChecked = Settings.Default.AdaptiveLbEnabled;

            // Probe watercooler connection on a background thread.
            // Always show the card on supported hardware — the toggle and command
            // paths already guard on _hydroConnected, so a race with TryAutoConnectAsync
            // can't cause spurious writes.  Previously the card was hidden when the
            // probe ran before auto-connect completed, then never re-shown because
            // the ConnectionStateChanged subscription was never established.
            _ = Task.Run(() =>
            {
                if (!WaterCoolerHardwareDetector.IsSupportedHardware())
                    return;

                var waterCooler = App.GetService<HydroUIService>();
                bool connected = waterCooler?.IsConnected == true;

                Dispatcher.Invoke(() =>
                {
                    _hydroUIService = waterCooler;
                    _hydroConnected = connected;

                    sdHydroUI.Visibility = Visibility.Visible;
                    _hydroUIService!.ConnectionStateChanged += OnWaterCoolerConnectionStateChanged;
                    _hydroUIService.SmartModeStateChanged += OnSmartModeStateChanged;

                    ApplyFeatureVisibilityGates();
                });
            });

            UpdateFlydigiCardVisibility();

            // Show/hide AMD vs Intel CPU controls
            UpdateAdpCpuUI();

            // Show/hide GPU card based on NVIDIA availability
            bool hasNvidia = !string.IsNullOrEmpty(NvTuning.GetGpuName());
            sdGpuSettings.Visibility = hasNvidia ? Visibility.Visible : Visibility.Collapsed;

            // Hide GPU clock controls when EnableGpuClockControl is OFF
            bool adpGpuClockEnabled = Settings.Default.EnableGpuClockControl;
            AdpGpuClockControlsPanel.Visibility = adpGpuClockEnabled ? Visibility.Visible : Visibility.Collapsed;

            // Show/hide EC Fan card based on Uniwill EC availability.
            // Run the hardware probe on a background thread to avoid blocking
            // the initial page render — EC register reads can take seconds.
            _ = Task.Run(() =>
            {
                bool uniwillAvailable = false;
                var uniwillEc = App.GetService<UniwillECService>();
                if (uniwillEc is not null)
                {
                    try { uniwillAvailable = uniwillEc.Initialize(); }
                    catch (Exception) { /* EC not available */ }
                }

                Dispatcher.Invoke(() =>
                {
                    if (uniwillAvailable)
                        sdEcFan.Visibility = Visibility.Visible;
                });
            });

            // Probe Light Bar availability and open HID device
            _ = Task.Run(() =>
            {
                try
                {
                    _hidService = new KeyboardHidService();
                    bool lbAvailable = _hidService.Open() && _hidService.IsLightBarAvailable;

                    Dispatcher.Invoke(() =>
                    {
                        if (lbAvailable)
                            sdLightBar.Visibility = Visibility.Visible;

                        ApplyFeatureVisibilityGates();
                    });
                }
                catch (Exception) { /* HID not available */ }
            });
        }

        /// <summary>
        /// Updates the Flydigi card visibility and controls based on the currently connected device.
        /// BS1: shows fan controls only (no RGB, no Curve, max 3000 RPM).
        /// BS2+: shows all controls including RGB.
        /// No device: hides the card entirely.
        /// </summary>
        private void UpdateFlydigiCardVisibility()
        {
            // Hide card if Flydigi functionality is disabled in Settings
            if (!Settings.Default.EnableFlydigi)
            {
                sdBs2Pro.Visibility = Visibility.Collapsed;
                // Ensure override is turned off when Flydigi is disabled
                cbxBs2ProEnabled.IsChecked = false;
                if (start && _deviceApplier?.IsFlydigiOverridden == true)
                    _ = _deviceApplier.DisableFlydigiOverrideAsync();
                return;
            }

            var deviceType = FlydigiHardwareDetector.ConnectedDeviceType;

            if (deviceType == ConnectedDeviceType.None)
            {
                sdBs2Pro.Visibility = Visibility.Collapsed;
                return;
            }

            sdBs2Pro.Visibility = Visibility.Visible;

            bool isBs1 = deviceType == ConnectedDeviceType.BS1;
            tbBs2ProTitle.Text = "Flydigi Cooler";

            // BS1 has no RGB — hide the RGB divider and all RGB controls
            if (Bs2ProRgbDivider != null)
                Bs2ProRgbDivider.Visibility = isBs1 ? Visibility.Collapsed : Visibility.Visible;
            if (spBs2ProRgbMode != null)
                spBs2ProRgbMode.Visibility = isBs1 ? Visibility.Collapsed : Visibility.Visible;

            // Hide all RGB sub-controls (they are shown/hidden by UpdateBs2ProRgbUI)
            HideAllBs2ProRgbControls();

            if (isBs1)
            {
                // BS1 supports Curve (Auto) via app-localized FlydigiBs1SmartControl — keep all 3 fan modes.
                nudBs2ProRpm.Maximum = MaxRpmBs1;
                sdBs2ProRpm.Maximum = MaxRpmBs1;
                if (nudBs2ProRpm.Value > MaxRpmBs1)
                    nudBs2ProRpm.Value = MaxRpmBs1;
            }
            else
            {
                nudBs2ProRpm.Maximum = MaxRpmBs2;
                sdBs2ProRpm.Maximum = MaxRpmBs2;
            }
        }

        /// <summary>Hides all RGB sub-controls (color picker, brightness, speed, dividers, info rows).</summary>
        private void HideAllBs2ProRgbControls()
        {
            // Guard: CardExpander content may not be realized yet (IsExpanded=false).
            if (spBs2ProRgbOff is not null)
                spBs2ProRgbOff.Visibility = Visibility.Collapsed;
            if (spBs2ProRgbSmartTemp is not null)
                spBs2ProRgbSmartTemp.Visibility = Visibility.Collapsed;
            if (spBs2ProRgbColorPicker is not null)
                spBs2ProRgbColorPicker.Visibility = Visibility.Collapsed;
            if (Bs2ProRgbSubDivider is not null)
                Bs2ProRgbSubDivider.Visibility = Visibility.Collapsed;
            if (Bs2ProRgbStaticBrightnessDivider is not null)
                Bs2ProRgbStaticBrightnessDivider.Visibility = Visibility.Collapsed;
            if (spBs2ProRgbBrightness is not null)
                spBs2ProRgbBrightness.Visibility = Visibility.Collapsed;
            if (Bs2ProRgbFlowingSpeedDivider is not null)
                Bs2ProRgbFlowingSpeedDivider.Visibility = Visibility.Collapsed;
            if (spBs2ProRgbSpeed is not null)
                spBs2ProRgbSpeed.Visibility = Visibility.Collapsed;
        }

        /// <summary>
        /// Hides Hydro UI and Light Bar override cards when the corresponding
        /// feature toggle is OFF in Settings. Only collapses — hardware availability
        /// controls the initial Visible state, so we never re-show here.
        /// Call from constructor (after hardware probes) and Page_Loaded.
        /// </summary>
        private void ApplyFeatureVisibilityGates()
        {
            if (!Settings.Default.EnableHydroUiNavigation && sdHydroUI.Visibility == Visibility.Visible)
            {
                sdHydroUI.Visibility = Visibility.Collapsed;
                cbxWcEnabled.IsChecked = false;
                Settings.Default.AdaptiveWcEnabled = false;
                Settings.Default.Save();
            }

            if (!Settings.Default.EnableLightBarNavigation && sdLightBar.Visibility == Visibility.Visible)
            {
                sdLightBar.Visibility = Visibility.Collapsed;
                cbxLbEnabled.IsChecked = false;
                Settings.Default.AdaptiveLbEnabled = false;
                Settings.Default.Save();
            }
        }

        // Named constants for repeated magic strings
        private const string AdaptivePresetsFile = "adaptivePresets.json";

        // BS2 Pro fan mode identifiers
        private const string FanModeGear = "Gear";
        private const string FanModeRpm = "Rpm";
        private const string FanModeCurve = "Curve";

        // Curve profile identifiers
        private const string CurveProfileSilent = "Silent";
        private const string CurveProfileQuiet = "Quiet";
        private const string CurveProfileBalanced = "Balanced";
        private const string CurveProfilePerformance = "Performance";
        private const string CurveProfileFreezy = "Freezy";
        private const string CurveProfileCustom = "Custom";

        // BS2 Pro RGB mode identifiers
        private const string RgbModeOff = "Off";
        private const string RgbModeSmartTemp = "SmartTemp";
        private const string RgbModeStatic = "Static";
        private const string RgbModeBreathing = "Breathing";
        private const string RgbModeFlowing = "Flowing";

        // EC Fan preset identifiers
        private const string EcFanPresetSilent = "Silent";
        private const string EcFanPresetBalanced = "Balanced";
        private const string EcFanPresetPerformance = "Performance";
        private const string EcFanPresetFullSpeed = "Full Speed";
        private const string EcFanPresetOff = "Off";
        private const string EcFanPresetCustom = "Custom";

        // Flydigi custom curve settings file
        private const string Bs2ProSettingsFile = "bs2pro_settings.json";

        // RPM clamp bounds
        private const int MinRpm = 1300;
        private const int MaxRpmBs1 = 3000;
        private const int MaxRpmBs2 = 4000;

        // RyzenAdj curve optimizer 20-bit bias base
        private const int CoBiasBase = 0x100000;

        private AdaptivePresetManager adaptivePresetManager = new AdaptivePresetManager(Settings.Default.Path + AdaptivePresetsFile);
        private DeviceApplier? _deviceApplier;
        private FlydigiSmartControl? _bs2ProSmartControl;
        private FlydigiTemperatureProvider? _bs2ProTempProvider;
        private FlydigiBs1SmartControl? _bs1SmartControl;
        private async void setUp()
        {
            try
            {
                // Initialize DeviceApplier for centralized device commands
                _deviceApplier = App.GetService<DeviceApplier>();
                _ = await gpuInventory.GetSnapshotAsync();

                cbExcludeFromSwitching.IsChecked = false;

                await Task.Run(() => Game_Manager.installedGames = Game_Manager.syncGame_Library(true));

                cbxPowerPreset.Items.Add("Default");
                foreach (GameLauncherItem item in Game_Manager.installedGames ?? Enumerable.Empty<GameLauncherItem>()) cbxPowerPreset.Items.Add(item.gameName);

                cbxPowerPreset.SelectedIndex = 0;

                IEnumerable<string> presetNames = adaptivePresetManager.GetPresetNames();

                foreach (GameLauncherItem item in (Game_Manager.installedGames ?? Enumerable.Empty<GameLauncherItem>())
                    .Where(item => !presetNames.Any(name => name.Contains(item.gameName))))
                {

                    {
                        AdaptivePreset preset = new AdaptivePreset
                        {
                            WcEnabled = true,
                            WcPumpVoltage = "V7",
                            WcFanSpeed = "Percent50",
                            WcRgbMode = "Static",
                            WcRgbColor = "Red",
                            Bs2ProEnabled = true,
                            Bs2ProFanMode = "Off",
                            Bs2ProGear = 1,
                            Bs2ProRpm = 2000,
                            Bs2ProCurveProfileId = string.Empty,
                            Bs2ProRgbMode = "Static",
                            Bs2ProRgbR = 0,
                            Bs2ProRgbG = 0,
                            Bs2ProRgbB = 255,
                            Bs2ProBrightness = 100,
                            Bs2ProRgbSpeed = 5,
                            Bs2ProCurvePointsJson = string.Empty,
                            EcFanEnabled = true,
                            EcFanUnifiedMode = false,
                            EcFanPreset = "Balanced",
                            EcFanCpuPreset = "Balanced",
                            EcFanGpuPreset = "Balanced",
                            EcFanCustomDuties = null,
                            EcFanCpuCustomDuties = null,
                            EcFanGpuCustomDuties = null,
                            isAutoSwitch = !(cbExcludeFromSwitching.IsChecked ?? false),
                            // CPU defaults
                            CpuSustained = 75,
                            CpuBoost = 85,
                            CpuTjMax = 95,
                            CpuCoUnified = true,
                            CpuCoUnifiedValue = 0,
                            CpuCoCcd0 = 0,
                            CpuCoCcd1 = 0,
                            CpuPl1 = 45,
                            CpuPl2 = 60,
                            CpuUndervolt = 0,
                            // GPU defaults — mode-aware (GpuNormal and GpuBoost are auto-initialized by the model)
                            // Light Bar defaults
                            LbEnabled = false,
                            LbPower = true,
                            LbColorR = 0,
                            LbColorG = 255,
                            LbColorB = 255,
                            LbBrightness = 100,
                            LbEffect = 0,
                            LbSpeed = 5
                        };
                        adaptivePresetManager.SavePreset(item.gameName, preset);
                    }
                }

                btnStart.IsEnabled = true;
                btnSave.IsEnabled = true;

                if (Settings.Default.isStartAdpative && !start) await ToggleAdaptiveMode();
            }
            catch (Exception ex)
            {
                DiagnosticLogger.LogError(ex, "Failed during adaptive mode setup");
            }
        }

        [DllImport("user32.dll", SetLastError = true)]
        static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", SetLastError = true)]
        static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll")]
        static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool EnumWindows(WndEnumProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        static extern IntPtr GetWindowLong(IntPtr hWnd, int nIndex);

        delegate bool WndEnumProc(IntPtr hWnd, IntPtr lParam);

        private const int GWL_STYLE = -16;
        private const int WS_VISIBLE = 0x10000000;

        private const string DefaultProfileName = "Default";

        // Guard flag to prevent cbxPowerPreset_SelectionChanged from calling loadPreset
        // when getRunningGame already called it directly with resetTracking: false.
        private bool _isUpdatingPresetSelection;

        // Voltage control is enabled when EnableCpuVoltageControl setting is True.
        private bool AdpCpuVoltageEnabled => Settings.Default.EnableCpuVoltageControl;

        // Window classes that are NOT games (browsers, notifications, taskbar, etc.)
        private static readonly HashSet<string> _excludedWindowClasses = new(StringComparer.OrdinalIgnoreCase)
        {
            // Browsers
            "Chrome_WidgetWin_1",      // Chrome/Edge/Electron windows
            "MozillaWindowClass",       // Firefox
            "IEFrame",                   // Internet Explorer
            // Editors
            "Notepad",                   // Notepad
            "Notepad++",                 // Notepad++
            "OpusApp",                   // Notepad++ (older)
            // Shell / Explorer
            "Shell_TrayWnd",             // Taskbar
            "Shell_SecondaryTrayWnd",    // Secondary taskbar
            "Shell_HostWindow",          // System tray host
            "Shell_Dialog",              // Shell dialogs
            "Shell_RenderedToolsWindow", // Shell tools
            "Shell_ScopeHost",           // Scope host
            "Shell_SideStrip",           // Explorer sidebar
            "Explorer_ImmersiveModeWindow", // File Explorer
            "CabinetWClass",             // File Explorer (older)
            "Progman",                   // Desktop
            "WorkerW",                   // Desktop worker windows
            // Notifications
            "NotifyIconOverflowWindow",  // System tray overflow
            "ToastContainerWindow",      // Notification toasts
            "WindowsToastContainerWindow", // Windows 11 toasts
            "AmoHost",                   // Action Center host
            // UWP / Modern UI
            "Windows.UI.Core.CoreWindow", // UWP popup windows
            "XamlExplorerHostIslandWindow", // UWP windows
            "ApplicationFrameWindow",    // UWP host window
            // Search / Start
            "SearchUI",                  // Windows Search
            "Start",                     // Start menu
            "ServiceHubStartMenuRoot",   // Start menu root
            // Input
            "MSCTF_UIElementCandidateWindowClassName", // Input method
            "IME",                       // Input method editor
            // Misc system
            "TaskListThumbnailWnd",      // Task view thumbnails
            "DVDDetectionDialog",        // DVD detection dialog
            "DVDDetectionDialogParent",  // DVD detection dialog parent
            "MessageWindow",             // Hidden message windows
            "MS_CursorWindow",           // Cursor windows
            "ForegroundStaging"          // Foreground staging
        };

        private bool start = false;
        private void btnStart_Click(object sender, RoutedEventArgs e)
        {
            _ = ToggleAdaptiveMode();
        }

        private async Task ToggleAdaptiveMode()
        {
            try
            {
                if (start)
                {
                    start = false;
                    btnStart.Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary;
                    btnStart.Icon = new Wpf.Ui.Controls.FontIcon { FontFamily = new FontFamily("Segoe Fluent Icons"), Glyph = "\uE768", FontSize = 16 }; // Play icon
                    btnStart.ToolTip = LocalizationService.Get("Start Adaptive Mode");
                    GetSensor.CloseSensor();
                    Settings.Default.isAdaptiveModeRunning = false;
                    Settings.Default.isStartAdpative = false;

                    // Disable auto-switch when Adaptive Mode stops
                    cbAutoSwitch.IsChecked = false;
                    Settings.Default.autoSwitch = false;
                    Settings.Default.Save();

                    // Stop smart control when Adaptive Mode stops
                    StopBs2ProSmartControl();
                    StopFlydigiBs1SmartControl();

                    // Lift DeviceApplier overrides when Adaptive Mode stops so that
                    // the Flydigi/HydroUI pages regain control and hide their overlays.
                    if (_deviceApplier != null)
                    {
                        _ = _deviceApplier.DisableFlydigiOverrideAsync();
                        _ = _deviceApplier.DisableWatercoolerOverrideAsync();
                        _deviceApplier.DisableEcFanOverride();
                        _deviceApplier.DisableKeyboardOverride();
                        _deviceApplier.DisableLightBarOverride();
                        _ = _deviceApplier.DisableCpuGpuOverrideAsync();
                    }

                }
                else
                {
                    start = true;
                    btnStart.Appearance = Wpf.Ui.Controls.ControlAppearance.Primary;
                    btnStart.Icon = new Wpf.Ui.Controls.FontIcon { FontFamily = new FontFamily("Segoe Fluent Icons"), Glyph = "\uE71A", FontSize = 16 }; // Stop icon
                    btnStart.ToolTip = LocalizationService.Get("Stop Adaptive Mode");
                    // Set synchronously before async work so RestoreAdaptiveButtonState()
                    // can read it if the user navigates away and back before OpenSensor finishes.
                    Settings.Default.isStartAdpative = true;
                    Settings.Default.Save();
                    await Task.Run(() => GetSensor.OpenSensor());
                    Settings.Default.isAdaptiveModeRunning = true;

                    // Enable auto-switch when Adaptive Mode starts
                    cbAutoSwitch.IsChecked = true;
                    Settings.Default.autoSwitch = true;
                    Settings.Default.Save();

                    // Sync DeviceApplier override state to the current checkbox values
                    // when Adaptive Mode starts. This ensures the override state matches
                    // what the user has configured in the active profile.
                    if (_deviceApplier != null)
                    {
                        if (Settings.Default.EnableFlydigi && (cbxBs2ProEnabled.IsChecked ?? false) && !_deviceApplier.IsFlydigiOverridden)
                            _deviceApplier.EnableFlydigiOverride();
                        if (Settings.Default.EnableHydroUiNavigation && _hydroConnected && (cbxWcEnabled.IsChecked ?? false) && !_deviceApplier.IsWatercoolerOverridden)
                            _deviceApplier.EnableWatercoolerOverride();
                        if ((cbxEcFanEnabled.IsChecked ?? false) && !_deviceApplier.IsEcFanOverridden)
                            _deviceApplier.EnableEcFanOverride();
                        if ((cbxKbEnabled.IsChecked ?? false) && !_deviceApplier.IsKeyboardOverridden)
                            _deviceApplier.EnableKeyboardOverride();
                        if (Settings.Default.EnableLightBarNavigation && (cbxLbEnabled.IsChecked ?? false) && sdLightBar.Visibility == Visibility.Visible && !_deviceApplier.IsLightBarOverridden)
                            _deviceApplier.EnableLightBarOverride();
                        if ((cbxCpuEnabled.IsChecked ?? false) || (cbxGpuEnabled.IsChecked ?? false))
                            _deviceApplier.EnableCpuGpuOverride();
                    }
                }
            }
            catch (Exception ex)
            {
                DiagnosticLogger.LogError(ex, "Failed to toggle adaptive mode");
            }
        }

 #pragma warning disable CS0649 // Field is never assigned to — set via XAML bindings
 #pragma warning disable CS0169 // Field is never used — read by XAML bindings
        private static int CPUTemp, CPULoad, CPUClock, CPUPower, GPULoad, GPUClock, GPUMemClock;
#pragma warning restore CS0649
#pragma warning restore CS0169

        /// <summary>
        /// Restores the btnStart ToggleButton icon, text, and IsChecked state
        /// from Settings so the button reflects the actual Adaptive Mode state.
        /// Called from Page_Loaded when navigating back to this page.
        /// 
        /// Checks isStartAdpative as a fallback because isAdaptiveModeRunning is set
        /// asynchronously inside ToggleAdaptiveMode() (after GetSensor.OpenSensor).
        /// When navigating back, setUp()'s async work may not have reached that line
        /// yet, so isAdaptiveModeRunning could still be false even though the user
        /// previously started Adaptive Mode.
        /// </summary>
        private void RestoreAdaptiveButtonState()
        {
            bool wasRunning = Settings.Default.isAdaptiveModeRunning || Settings.Default.isStartAdpative;

            if (wasRunning)
            {
                start = true;
                btnStart.Appearance = Wpf.Ui.Controls.ControlAppearance.Primary;
                btnStart.Icon = new Wpf.Ui.Controls.FontIcon { FontFamily = new FontFamily("Segoe Fluent Icons"), Glyph = "\uE71A", FontSize = 16 };
                btnStart.ToolTip = LocalizationService.Get("Stop Adaptive Mode");
            }
            else
            {
                start = false;
                btnStart.Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary;
                btnStart.Icon = new Wpf.Ui.Controls.FontIcon { FontFamily = new FontFamily("Segoe Fluent Icons"), Glyph = "\uE768", FontSize = 16 };
                btnStart.ToolTip = LocalizationService.Get("Start Adaptive Mode");
            }
        }

        /// <summary>
        /// Applies dynamic maximum to the SPL/FPL sliders based on the resolved CPU SKU
        /// and whether a Hydro UI watercooler is connected. Matches the Presets page behavior.
        /// Also applies GPU TGP slider max based on GPU model and power boost setting.
        /// </summary>
        private void ApplyAdpPowerLimitMax()
        {
            var waterCooler = App.GetService<HydroUIService>();
            bool hydro = waterCooler?.IsConnected == true;
            bool powerBoostMode = Settings.Default.EnablePowerBoostMode;
            var limits = SkuResolver.GetPowerLimits(hydro);

            if (limits == null)
            {
                // Fallback: use reasonable defaults based on CPU vendor
                if (Family.TYPE == Family.ProcessorType.Amd_Apu)
                {
                    AdpSdSustained.Maximum = powerBoostMode ? 255 : (hydro ? 160 : 130);
                    AdpSdBoost.Maximum = powerBoostMode ? 255 : (hydro ? 160 : 135);
                }
                else if (Family.TYPE == Family.ProcessorType.Intel)
                {
                    AdpSdSustained.Maximum = powerBoostMode ? 255 : (hydro ? 200 : 160);
                    AdpSdBoost.Maximum = powerBoostMode ? 255 : (hydro ? 200 : 200);
                }
                return;
            }

            if (Family.TYPE == Family.ProcessorType.Amd_Apu)
            {
                AdpSdSustained.Maximum = powerBoostMode ? 255 : limits.CpuPl1Sustained;
                AdpSdBoost.Maximum = powerBoostMode ? 255 : limits.CpuPl2Boost;
            }
            else if (Family.TYPE == Family.ProcessorType.Intel)
            {
                // Intel uses the same sliders for PL1/PL2
                AdpSdSustained.Maximum = powerBoostMode ? 255 : limits.CpuPl1Sustained;
                AdpSdBoost.Maximum = powerBoostMode ? 255 : limits.CpuPl2Boost;
            }

            // Clamp current values if they exceed the new maximum
            if (AdpSdSustained.Value > AdpSdSustained.Maximum)
                AdpSdSustained.Value = AdpSdSustained.Maximum;
            if (AdpSdBoost.Value > AdpSdBoost.Maximum)
                AdpSdBoost.Value = AdpSdBoost.Maximum;

            // GPU TGP slider — set per-GPU max range (matches Presets page)
            if (sdGpuSettings.Visibility == Visibility.Visible)
            {
                string gpuName = NvTuning.GetGpuName()?.ToLowerInvariant() ?? string.Empty;
                bool boostEnabled = Settings.Default.EnablePowerBoostMode;
                int gpuMax = gpuName switch
                {
                    var n when n.Contains("rtx 5090") => boostEnabled ? 250 : 150,
                    var n when n.Contains("rtx 5080") => boostEnabled ? 250 : 150,
                    var n when n.Contains("rtx 5070") => boostEnabled ? 250 : 115,
                    _ => limits.GpuTgpBaseline
                };
                AdpSdNvPower.Maximum = gpuMax;
                if (AdpSdNvPower.Value > gpuMax)
                    AdpSdNvPower.Value = gpuMax;

                // Hide Dynamic Boost controls when Power boost mode is ON
                bool dbVisible = !boostEnabled;
                AdpDynamicBoostPanel.Visibility = dbVisible ? Visibility.Visible : Visibility.Collapsed;

                // Update bottom bar boost indicator
                UpdateAdpPresetModeLabel(boostEnabled);
            }
        }

        /// <summary>
        /// Updates the preset mode label in the bottom bar and the boost accent stroke.
        /// </summary>
        private void UpdateAdpPresetModeLabel(bool boostEnabled)
        {
            if (lblAdpPresetMode != null)
            {
                lblAdpPresetMode.Text = boostEnabled ? "Power Boost ON" : "";
                lblAdpPresetMode.Visibility = boostEnabled ? Visibility.Visible : Visibility.Collapsed;
            }

            // Show/hide bottom bar accent stroke
            if (AdpBottomBar != null)
            {
                if (boostEnabled)
                {
                    AdpBottomBar.BorderBrush = (System.Windows.Media.Brush)FindResource("AccentFillColorDefaultBrush");
                    AdpBottomBar.BorderThickness = new Thickness(0, 0, 0, 2);
                }
                else
                {
                    AdpBottomBar.BorderBrush = null;
                    AdpBottomBar.BorderThickness = new Thickness(0);
                }
            }
        }

        private async void adaptive_Tick(object? sender, EventArgs e)
        {
            if (start)
            {
                update();
            }
        }

        private void cbxPowerPreset_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Skip if getRunningGame already called loadPreset directly.
            if (_isUpdatingPresetSelection)
                return;

            string? presetName = (sender as ComboBox)?.SelectedItem as string;
            if (presetName == null)
                return;
            loadPreset(presetName);
        }

        private void loadPreset(string presetName)
        {
            loadPreset(presetName, resetTracking: true);
        }

        #region Preset load helpers

        private void LoadWatercoolerToUI(AdaptivePreset preset)
        {
            cbxWcPumpVoltage.SelectedIndex = GetPumpVoltageIndex(preset.WcPumpVoltage);
            cbxWcFanSpeed.SelectedIndex = GetFanSpeedIndex(preset.WcFanSpeed);
            if (Enum.TryParse<RgbState>(preset.WcRgbMode, true, out var pvLoad))
            {
                cbxWcRgbMode.SelectedIndex = GetRgbModeIndex(pvLoad);
                spWcRgbColor.Visibility = pvLoad switch
                {
                    RgbState.Static or RgbState.Breathe => Visibility.Visible,
                    _ => Visibility.Collapsed
                };
                WcRgbColorDivider.Visibility = spWcRgbColor.Visibility;
            }
            if (Enum.TryParse<RgbColor>(preset.WcRgbColor, true, out var rcLoad))
                cbxWcRgbColor.SelectedIndex = GetRgbColorIndex(rcLoad);
        }

        private void LoadBs2ProToUI(AdaptivePreset preset)
        {
            // Guard: CardExpander content is null until expanded
            if (cbxBs2ProFanMode is not null)
            {
                cbxBs2ProFanMode.SelectedIndex = GetBs2ProFanModeIndex(preset.Bs2ProFanMode);
                UpdateBs2ProModeUI();
                cbxBs2ProGear.SelectedIndex = Math.Clamp(preset.Bs2ProGear - 1, 0, 3);
                nudBs2ProRpm.Value = Math.Clamp((int)preset.Bs2ProRpm, MinRpm, MaxRpmBs2);
                cbxBs2ProCurve.SelectedIndex = GetBs2ProCurveIndex(preset.Bs2ProCurveProfileId);

                // Gap 3: Restore Flydigi custom curve points per-game
                ApplyBs2ProCurvePoints(preset.Bs2ProCurvePointsJson);

                cbxBs2ProRgbMode.SelectedIndex = GetBs2ProRgbModeIndex(preset.Bs2ProRgbMode);
                UpdateBs2ProRgbUI();
                nudBs2ProRgbR.Value = preset.Bs2ProRgbR;
                nudBs2ProRgbG.Value = preset.Bs2ProRgbG;
                nudBs2ProRgbB.Value = preset.Bs2ProRgbB;
                Bs2ProRgbColorPicker.SelectedColor = Color.FromRgb(preset.Bs2ProRgbR, preset.Bs2ProRgbG, preset.Bs2ProRgbB);
                sliderBs2ProBrightness.Value = preset.Bs2ProBrightness;
                Bs2ProRgbBrightnessValueText.Text = preset.Bs2ProBrightness.ToString();
                // Gap 2: Restore Flydigi RGB speed per-game
                if (sliderBs2ProRgbSpeed != null)
                    sliderBs2ProRgbSpeed.Value = preset.Bs2ProRgbSpeed;
                int rpm = Math.Clamp((int)preset.Bs2ProRpm, MinRpm, MaxRpmBs2);
                Bs2ProRpmValueText.Text = rpm.ToString();
                sdBs2ProRpm.Value = rpm;
            }
        }

        private void LoadEcFanToUI(AdaptivePreset preset, bool resetTracking = true)
        {
            if (sdEcFan.Visibility == Visibility.Visible)
            {
                // Only restore the enabled toggle from the saved preset on full reloads.
                // When resetTracking is false (Page_Loaded navigation-back, game detection),
                // keep the current UI toggle state so unsaved enable/disable changes persist.
                if (resetTracking)
                    cbxEcFanEnabled.IsChecked = preset.EcFanEnabled;

                cbxEcFanMode.SelectedIndex = preset.EcFanUnifiedMode ? 0 : 1;
                cbxEcFanPreset.SelectedIndex = GetEcFanPresetIndex(preset.EcFanPreset);
                cbxEcFanCpuPreset.SelectedIndex = GetEcFanPresetIndex(preset.EcFanCpuPreset);
                cbxEcFanGpuPreset.SelectedIndex = GetEcFanPresetIndex(preset.EcFanGpuPreset);
                // Gap 1: Restore EC Fan custom curves per-game
                ApplyEcFanCustomDuties(preset.EcFanPreset, preset.EcFanCustomDuties,
                    preset.EcFanCpuPreset, preset.EcFanCpuCustomDuties,
                    preset.EcFanGpuPreset, preset.EcFanGpuCustomDuties);
                UpdateEcFanModeUI();
            }
        }

        private void LoadKeyboardToUI(AdaptivePreset preset)
        {
            cbxKbMode.SelectedIndex = preset.KbPerKeyMode ? 1 : 0;
            UpdateKbModeUI();
            int brightness = Math.Clamp(preset.KbBrightness, 1, 7);
            sdKbBrightness.Value = brightness;
            tbKbBrightnessValue.Text = brightness.ToString();
            cbxKbEffect.SelectedIndex = GetKbEffectIndex(preset.KbEffectMode);
            // Explicitly update effect UI after setting the index — the SelectionChanged
            // event may not fire if the index already matches, leaving controls hidden.
            UpdateKbEffectUi();
            KbColorPicker.SelectedColor = Color.FromRgb(preset.KbColorR, preset.KbColorG, preset.KbColorB);
            int speed = Math.Clamp((int)preset.KbEffectSpeed, 1, 10);
            sdKbSpeed.Value = speed;
            tbKbSpeedValue.Text = speed.ToString();
            _kbDirection = preset.KbDirection;
            _kbPerKeyColors = preset.KbPerKeyColors ?? "";
            UpdateKbDirectionButtons();

            // Restore multi-colors if saved
            if (!string.IsNullOrEmpty(preset.KbMultiColors))
            {
                var colors = ParseColorString(preset.KbMultiColors);
                if (colors.Count > 0)
                {
                    int count = preset.KbEffectMode == "GamingMode" ? 4 : 7;
                    KbMultiColorPicker.SetColors(colors, count);
                }
            }
        }

        private void LoadCpuToUI(AdaptivePreset preset)
        {
            if (Family.TYPE == Family.ProcessorType.Amd_Apu)
            {
                AdpSdSustained.Value = Math.Clamp(preset.CpuSustained, 5, 255);
                AdpSdBoost.Value = Math.Clamp(preset.CpuBoost, 5, 255);
                AdpSdTjMax.Value = Math.Clamp(preset.CpuTjMax, 70, 105);
                AdpRbCoUnified.IsChecked = preset.CpuCoUnified;
                AdpRbCoPerCcd.IsChecked = !preset.CpuCoUnified;
                AdpSdCoUnified.Value = Math.Clamp(preset.CpuCoUnifiedValue, -30, 0);
                AdpSdCoCcd0.Value = Math.Clamp(preset.CpuCoCcd0, -30, 0);
                AdpSdCoCcd1.Value = Math.Clamp(preset.CpuCoCcd1, -30, 0);
                updateAdpCoVisibility();
                AdpLblCoUnified.Text = $"{AdpSdCoUnified.Value:+0;-0;0}";
                AdpLblCoCcd0.Text = $"{AdpSdCoCcd0.Value:+0;-0;0}";
                AdpLblCoCcd1.Text = $"{AdpSdCoCcd1.Value:+0;-0;0}";
            }
            else if (Family.TYPE == Family.ProcessorType.Intel)
            {
                AdpSdIntelPl1.Value = Math.Clamp(preset.CpuPl1, 5, 400);
                AdpSdIntelPl2.Value = Math.Clamp(preset.CpuPl2, 5, 400);
                AdpSdIntelUndervolt.Value = Math.Clamp(preset.CpuUndervolt, -150, 0);
            }
        }

        private void LoadGpuToUI(AdaptivePreset preset)
        {
            if (sdGpuSettings.Visibility == Visibility.Visible)
            {
                var gpu = GetActiveGpuSettings(preset);
                AdpSdNvPower.Value = Math.Clamp(gpu.Tgp, 0, NvTuning.MaxPowerLimitWatts);
                AdpTogDynamicBoost.IsChecked = gpu.DynamicBoost;
                AdpSdNvDbOffset.Value = Math.Clamp(gpu.DbOffset, 0, 25);
                AdpSdNvCore.Value = Math.Clamp(gpu.CoreClock, -1000, 1000);
                AdpSdNvMem.Value = Math.Clamp(gpu.MemClock, -1000, 3000);
                // Show/hide Dynamic Boost offset based on toggle
                AdpSpNvDbOffset.Visibility = gpu.DynamicBoost ? Visibility.Visible : Visibility.Collapsed;
                // Reset user-touched flag when loading preset — start fresh
                _adpUserTouchedCoreSlider = false;
                // Gap 4: Load per-game V-F curve offsets
                _adpVfCurveOffsets = new Dictionary<int, int>(gpu.VfCurveOffsets);
                updateAdpCoreLabel();
            }
        }

        /// <summary>
        /// Returns the GPU settings for the currently active mode (Normal or Boost).
        /// </summary>
        private static AdaptiveGpuSettings GetActiveGpuSettings(AdaptivePreset preset)
        {
            bool boostEnabled = Settings.Default.EnablePowerBoostMode;
            return boostEnabled ? preset.GpuBoost : preset.GpuNormal;
        }

        /// <summary>
        /// Captures the current UI GPU slider values into the active mode's settings.
        /// </summary>
        private void CaptureGpuToPreset(AdaptivePreset preset)
        {
            bool boostEnabled = Settings.Default.EnablePowerBoostMode;
            var gpu = boostEnabled ? preset.GpuBoost : preset.GpuNormal;
            gpu.Tgp = (int)AdpSdNvPower.Value;
            gpu.DynamicBoost = AdpTogDynamicBoost.IsChecked == true;
            gpu.DbOffset = (int)AdpSdNvDbOffset.Value;
            gpu.CoreClock = (int)AdpSdNvCore.Value;
            gpu.MemClock = (int)AdpSdNvMem.Value;
            gpu.VfCurveOffsets = new Dictionary<int, int>(_adpVfCurveOffsets);
        }

        private void LoadLightBarToUI(AdaptivePreset preset)
        {
            if (sdLightBar.Visibility == Visibility.Visible)
            {
                AdpTsLightBarPower.IsChecked = preset.LbPower;
                AdpLbColorPicker.SelectedColor = Color.FromRgb(preset.LbColorR, preset.LbColorG, preset.LbColorB);
                AdpLbBrightnessSlider.Value = Math.Clamp(preset.LbBrightness, 0, 100);
                AdpLbBrightnessValueText.Text = Math.Clamp(preset.LbBrightness, 0, 100).ToString();
                AdpLbEffect.SelectedIndex = Math.Clamp(preset.LbEffect, 0, 5);
                AdpLbSpeedSlider.Value = Math.Clamp(preset.LbSpeed, 0, 11);
                AdpLbSpeedValueText.Text = Math.Clamp(preset.LbSpeed, 0, 11).ToString();
                // Show/hide speed control (hidden for Static)
                AdpSpLbSpeed.Visibility = preset.LbEffect != 0 ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void SyncOverridesFromPreset(AdaptivePreset preset)
        {
            // Apply the preset's per-profile override flags only when resetTracking
            // is true (profile switch). When resetTracking is false (Page_Loaded,
            // game detection without profile change), preserve the user's current
            // checkbox edits so they aren't overwritten by the saved preset.
            if (Settings.Default.EnableFlydigi && cbxBs2ProEnabled.IsChecked != preset.Bs2ProEnabled)
                cbxBs2ProEnabled.IsChecked = preset.Bs2ProEnabled;
            if (cbxWcEnabled.IsChecked != preset.WcEnabled)
                cbxWcEnabled.IsChecked = preset.WcEnabled;
            if (cbxKbEnabled.IsChecked != preset.KbEnabled)
                cbxKbEnabled.IsChecked = preset.KbEnabled;
            if (cbxCpuEnabled.IsChecked != preset.CpuEnabled)
                cbxCpuEnabled.IsChecked = preset.CpuEnabled;
            if (cbxGpuEnabled.IsChecked != preset.GpuEnabled)
                cbxGpuEnabled.IsChecked = preset.GpuEnabled;
            if (cbxLbEnabled.IsChecked != preset.LbEnabled)
                cbxLbEnabled.IsChecked = preset.LbEnabled;

            // Sync global settings to the active preset's override state so that
            // App.xaml.cs startup restoration reads the correct values.
            Settings.Default.AdaptiveBs2ProEnabled = preset.Bs2ProEnabled;
            Settings.Default.AdaptiveWcEnabled = preset.WcEnabled;
            Settings.Default.AdaptiveKeyboardEnabled = preset.KbEnabled;
            Settings.Default.AdaptiveCpuEnabled = preset.CpuEnabled;
            Settings.Default.AdaptiveGpuEnabled = preset.GpuEnabled;
            Settings.Default.AdaptiveLbEnabled = preset.LbEnabled;
            Settings.Default.Save();
        }

        private void ResetDiffTracking()
        {
            // Reset tracking so the next update() tick re-applies everything.
            lastBs2ProFanModeText = "";
            lastBs2ProGear = 0;
            lastBs2ProRpm = 0;
            lastBs2ProRgbMode = "";
            lastBs2ProRgbR = 0;
            lastBs2ProRgbG = 0;
            lastBs2ProRgbB = 0;
            lastBs2ProBrightness = 0;

            lastWcPump = PumpVoltage.Off;
            lastWcFan = FanSpeed.Off;
            lastWcRgbMode = RgbState.Off;
            lastWcRgbColor = RgbColor.Red;

            lastEcFanUnifiedMode = false;
            lastEcFanPreset = "";
            lastEcFanCpuPreset = "";
            lastEcFanGpuPreset = "";

            lastKbPerKeyMode = false;
            lastKbBrightness = 0;
            lastKbEffectMode = "";
            lastKbEffectSpeed = 0;
            lastKbColorR = 0;
            lastKbColorG = 0;
            lastKbColorB = 0;
            lastKbMultiColors = "";
            lastKbDirection = "";

            // CPU tracking
            lastCpuSustained = 0;
            lastCpuBoost = 0;
            lastCpuTjMax = 0;
            lastCpuPl1 = 0;
            lastCpuPl2 = 0;
            lastCpuUndervolt = 0;

            // GPU tracking
            lastGpuTgp = 0;
            lastGpuDynamicBoost = false;
            lastGpuDbOffset = 0;
            lastGpuCoreClock = 0;
            lastGpuMemClock = 0;
            lastGpuVfCurveCount = -1;

            // Light Bar tracking
            lastLbPower = false;
            lastLbColorR = 0;
            lastLbColorG = 0;
            lastLbColorB = 0;
            lastLbBrightness = 0;
            lastLbEffect = 0;
            lastLbSpeed = 0;
        }

        #endregion

        /// <summary>
        /// Loads a preset into the UI controls.
        /// </summary>
        /// <param name="presetName">Preset to load.</param>
        /// <param name="resetTracking">If true, resets diff-tracking variables so the next update() tick re-applies everything.
        /// Set to false when loading from Page_Loaded or game detection to avoid re-sending unchanged device commands.</param>
        private void loadPreset(string presetName, bool resetTracking)
        {
            try
            {
                adaptivePresetManager = new AdaptivePresetManager(Settings.Default.Path + "adaptivePresets.json");
                AdaptivePreset? preset = adaptivePresetManager.GetPreset(presetName);

                if (preset is null)
                    return;

                cbExcludeFromSwitching.IsChecked = !preset.isAutoSwitch;

                LoadWatercoolerToUI(preset);
                LoadBs2ProToUI(preset);
                LoadEcFanToUI(preset, resetTracking);
                LoadKeyboardToUI(preset);
                LoadCpuToUI(preset);
                LoadGpuToUI(preset);
                LoadLightBarToUI(preset);

                if (resetTracking)
                {
                    SyncOverridesFromPreset(preset);
                    ResetDiffTracking();
                }
                // When resetTracking is false (Page_Loaded, game detection), leave
                // tracking vars unchanged so update() won't re-send device commands
                // for values that are already applied.

                // Stop any existing smart control (curve mode) so it restarts with the new profile
                StopBs2ProSmartControl();
                StopFlydigiBs1SmartControl();
            }
            catch (Exception ex)
            {
                DiagnosticLogger.LogError(ex, "Failed to load adaptive preset");
            }
        }

        private void savePreset(string presetName)
        {
            try
            {
                // Load existing preset to preserve the other mode's GPU settings
                AdaptivePreset? existing = adaptivePresetManager.GetPreset(presetName);

                AdaptivePreset preset = new AdaptivePreset
                {
                    WcEnabled = cbxWcEnabled.IsChecked ?? false,
                    WcPumpVoltage = GetPumpVoltageFromIndex(cbxWcPumpVoltage.SelectedIndex).ToString(),
                    WcFanSpeed = GetFanSpeedFromIndex(cbxWcFanSpeed.SelectedIndex).ToString(),
                    WcRgbMode = GetRgbModeFromIndex(cbxWcRgbMode.SelectedIndex).ToString(),
                    WcRgbColor = GetRgbColorFromIndex(cbxWcRgbColor.SelectedIndex).ToString(),
                    Bs2ProEnabled = cbxBs2ProEnabled.IsChecked ?? false,
                    Bs2ProFanMode = GetBs2ProFanModeFromIndex(cbxBs2ProFanMode.SelectedIndex),
                    Bs2ProGear = cbxBs2ProGear.SelectedIndex + 1,
                    Bs2ProRpm = (ushort)Math.Clamp((int)(nudBs2ProRpm.Value ?? 0), MinRpm, MaxRpmBs2),
                    Bs2ProCurveProfileId = GetBs2ProCurveProfileId(cbxBs2ProCurve.SelectedIndex),
                    // Gap 3: Save Flydigi custom curve points per-game
                    Bs2ProCurvePointsJson = GetBs2ProCurvePointsJson(),
                    Bs2ProRgbMode = GetBs2ProRgbModeFromIndex(cbxBs2ProRgbMode.SelectedIndex),
                    Bs2ProRgbR = (byte)(nudBs2ProRgbR.Value ?? 0),
                    Bs2ProRgbG = (byte)(nudBs2ProRgbG.Value ?? 0),
                    Bs2ProRgbB = (byte)(nudBs2ProRgbB.Value ?? 0),
                    Bs2ProBrightness = (byte)sliderBs2ProBrightness.Value,
                    // Gap 2: Save Flydigi RGB speed per-game
                    Bs2ProRgbSpeed = (byte)sliderBs2ProRgbSpeed.Value,
                    EcFanEnabled = sdEcFan.Visibility == Visibility.Visible && (cbxEcFanEnabled.IsChecked ?? false),
                    EcFanUnifiedMode = sdEcFan.Visibility == Visibility.Visible && cbxEcFanMode.SelectedIndex == 0,
                    EcFanPreset = sdEcFan.Visibility == Visibility.Visible ? GetEcFanPresetFromIndex(cbxEcFanPreset.SelectedIndex) : EcFanPresetBalanced,
                    EcFanCpuPreset = sdEcFan.Visibility == Visibility.Visible ? GetEcFanPresetFromIndex(cbxEcFanCpuPreset.SelectedIndex) : EcFanPresetBalanced,
                    EcFanGpuPreset = sdEcFan.Visibility == Visibility.Visible ? GetEcFanPresetFromIndex(cbxEcFanGpuPreset.SelectedIndex) : EcFanPresetBalanced,
                    // Gap 1: Save EC Fan custom curves per-game
                    EcFanCustomDuties = sdEcFan.Visibility == Visibility.Visible && GetEcFanPresetFromIndex(cbxEcFanPreset.SelectedIndex) == EcFanPresetCustom
                        ? FanControlSettingsService.Load().UnifiedDuties?.ToArray() : null,
                    EcFanCpuCustomDuties = sdEcFan.Visibility == Visibility.Visible && GetEcFanPresetFromIndex(cbxEcFanCpuPreset.SelectedIndex) == EcFanPresetCustom
                        ? FanControlSettingsService.Load().CpuDuties?.ToArray() : null,
                    EcFanGpuCustomDuties = sdEcFan.Visibility == Visibility.Visible && GetEcFanPresetFromIndex(cbxEcFanGpuPreset.SelectedIndex) == EcFanPresetCustom
                        ? FanControlSettingsService.Load().GpuDuties?.ToArray() : null,
                    KbEnabled = cbxKbEnabled.IsChecked ?? false,
                    KbPerKeyMode = cbxKbMode.SelectedIndex == 1,
                    KbBrightness = (int)sdKbBrightness.Value,
                    KbIdleTimerEnabled = false,
                    KbIdleTimerMinutes = 0,
                    KbEffectMode = GetKbEffectFromIndex(cbxKbEffect.SelectedIndex),
                    KbEffectSpeed = (byte)Math.Clamp((int)sdKbSpeed.Value, 1, 10),
                    KbColorR = KbColorPicker.SelectedColor.R,
                    KbColorG = KbColorPicker.SelectedColor.G,
                    KbColorB = KbColorPicker.SelectedColor.B,
                    KbMultiColors = SerializeColors(KbMultiColorPicker.GetColors()),
                    KbPerKeyColors = _kbPerKeyColors,
                    KbDirection = _kbDirection,
                    isAutoSwitch = !(cbExcludeFromSwitching.IsChecked ?? false),
                    // CPU
                    CpuEnabled = cbxCpuEnabled.IsChecked ?? false,
                    CpuSustained = (int)AdpSdSustained.Value,
                    CpuBoost = (int)AdpSdBoost.Value,
                    CpuTjMax = (int)AdpSdTjMax.Value,
                    CpuCoUnified = AdpRbCoUnified.IsChecked == true,
                    CpuCoUnifiedValue = (int)AdpSdCoUnified.Value,
                    CpuCoCcd0 = (int)AdpSdCoCcd0.Value,
                    CpuCoCcd1 = (int)AdpSdCoCcd1.Value,
                    CpuPl1 = (int)AdpSdIntelPl1.Value,
                    CpuPl2 = (int)AdpSdIntelPl2.Value,
                    CpuUndervolt = (int)AdpSdIntelUndervolt.Value,
                    // GPU — enabled flag only; mode-specific settings captured below
                    GpuEnabled = sdGpuSettings.Visibility == Visibility.Visible && (cbxGpuEnabled.IsChecked ?? false),
                    // Preserve both mode variants from existing preset, then overwrite the active one
                    GpuNormal = existing?.GpuNormal ?? new AdaptiveGpuSettings(),
                    GpuBoost = existing?.GpuBoost ?? new AdaptiveGpuSettings(boostDefaults: true),
                    // Light Bar
                    LbEnabled = sdLightBar.Visibility == Visibility.Visible && (cbxLbEnabled.IsChecked ?? false),
                    LbPower = AdpTsLightBarPower.IsChecked == true,
                    LbColorR = AdpLbColorPicker.SelectedColor.R,
                    LbColorG = AdpLbColorPicker.SelectedColor.G,
                    LbColorB = AdpLbColorPicker.SelectedColor.B,
                    LbBrightness = (int)AdpLbBrightnessSlider.Value,
                    LbEffect = AdpLbEffect.SelectedIndex,
                    LbSpeed = (int)AdpLbSpeedSlider.Value
                };

                // Capture current UI GPU values into the active mode's settings
                if (sdGpuSettings.Visibility == Visibility.Visible)
                    CaptureGpuToPreset(preset);

                adaptivePresetManager.SavePreset(presetName, preset);
            }
            catch (Exception ex)
            {
                DiagnosticLogger.LogError(ex, "Failed to save adaptive preset");
            }
        }

        private async void btnReloadApps_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                cbxPowerPreset.ItemsSource = new List<string>();
                await Task.Run(() => Game_Manager.installedGames = Game_Manager.syncGame_Library(true));
                cbxPowerPreset.Items.Clear();
                cbxPowerPreset.Items.Add("Default");
                foreach (GameLauncherItem item in Game_Manager.installedGames ?? Enumerable.Empty<GameLauncherItem>()) cbxPowerPreset.Items.Add(item.gameName);
                cbxPowerPreset.SelectedIndex = 0;
            }
            catch (Exception ex)
            {
                DiagnosticLogger.LogError(ex, "Failed to reload game apps");
            }
        }

        private void btnSave_Click(object sender, RoutedEventArgs e)
        {
            string presetName = cbxPowerPreset.SelectedItem?.ToString() ?? DefaultProfileName;
            savePreset(presetName);

            // Sync active settings so other pages can detect the change immediately
            Settings.Default.AdaptiveBs2ProEnabled = cbxBs2ProEnabled.IsChecked ?? false;
            Settings.Default.AdaptiveWcEnabled = cbxWcEnabled.IsChecked ?? false;
            Settings.Default.AdaptiveEcFanEnabled = cbxEcFanEnabled.IsChecked ?? false;
            Settings.Default.AdaptiveCpuEnabled = cbxCpuEnabled.IsChecked ?? false;
            Settings.Default.AdaptiveGpuEnabled = cbxGpuEnabled.IsChecked ?? false;
            Settings.Default.AdaptiveLbEnabled = cbxLbEnabled.IsChecked ?? false;
            Settings.Default.Save();
        }

        private void cbxBs2ProEnabled_Toggled(object sender, RoutedEventArgs e)
        {
            // Guard: don't allow enabling override when Flydigi is disabled in Settings
            if (!Settings.Default.EnableFlydigi)
            {
                cbxBs2ProEnabled.IsChecked = false;
                return;
            }

            bool enabled = cbxBs2ProEnabled.IsChecked ?? false;
            Settings.Default.AdaptiveBs2ProEnabled = enabled;
            Settings.Default.Save();

            // Only apply override when Adaptive Mode is running
            if (!start) return;

            // Route through DeviceApplier so subscribed pages get the event notification
            if (enabled)
            {
                _deviceApplier?.EnableFlydigiOverride();
                // Reset tracking so the next update() tick force-applies the profile to the device.
                lastBs2ProFanModeText = "";
                lastBs2ProRgbMode = "";
            }
            else
                _ = _deviceApplier!.DisableFlydigiOverrideAsync();
        }

        private void cbxWcEnabled_Toggled(object sender, RoutedEventArgs e)
        {
            // Prevent enabling if Hydro UI navigation is disabled in Settings.
            if ((cbxWcEnabled.IsChecked ?? false) && !Settings.Default.EnableHydroUiNavigation)
            {
                cbxWcEnabled.IsChecked = false;
                return;
            }

            // Prevent enabling if no watercooler is connected.
            if ((cbxWcEnabled.IsChecked ?? false) && !_hydroConnected)
            {
                cbxWcEnabled.IsChecked = false;
                return;
            }

            bool enabled = cbxWcEnabled.IsChecked ?? false;
            Settings.Default.AdaptiveWcEnabled = enabled;
            Settings.Default.Save();

            // Only apply override when Adaptive Mode is running
            if (!start) return;

            // Route through DeviceApplier so subscribed pages get the event notification
            if (enabled)
            {
                _deviceApplier?.EnableWatercoolerOverride();
                // Reset tracking so the next update() tick force-applies the profile to the device.
                lastWcPump = PumpVoltage.Off;
                lastWcFan = FanSpeed.Off;
                lastWcRgbMode = RgbState.Off;
                lastWcRgbColor = RgbColor.Red;
            }
            else
                _ = _deviceApplier!.DisableWatercoolerOverrideAsync();
        }

        private void cbxEcFanEnabled_Toggled(object sender, RoutedEventArgs e)
        {
            bool enabled = cbxEcFanEnabled.IsChecked ?? false;
            Settings.Default.AdaptiveEcFanEnabled = enabled;
            Settings.Default.Save();

            // Only apply override when Adaptive Mode is running
            if (!start) return;

            if (enabled)
            {
                _deviceApplier?.EnableEcFanOverride();
                // Reset tracking so the next update() tick force-applies
                lastEcFanPreset = "";
                lastEcFanCpuPreset = "";
                lastEcFanGpuPreset = "";
            }
            else
            {
                _deviceApplier?.DisableEcFanOverride();
            }
        }

        private void cbxEcFanMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateEcFanModeUI();
        }

        private void UpdateEcFanModeUI()
        {
            if (cbxEcFanMode == null || spEcFanUnified == null || spEcFanSplit == null)
                return;

            bool unified = cbxEcFanMode.SelectedIndex == 0;
            spEcFanUnified.Visibility = unified ? Visibility.Visible : Visibility.Collapsed;
            spEcFanSplit.Visibility = unified ? Visibility.Collapsed : Visibility.Visible;
        }

        private async void sensors_Tick(object? sender, EventArgs e)
        {
            try
            {
                if (start)
                {
                    await Task.Run(() =>
                    {
                        isGameRunning();
                    });

                    if (cbxPowerPreset.Items.Count > 0 && cbAutoSwitch.IsChecked == true)
                    {
                        // Only auto-switch when the detected game changed from the previous tick.
                        // Compare against _lastDetectedGame instead of the combobox selection so that
                        // manual combobox changes don't get overwritten by stale detection results.
                        if (_lastDetectedGame != runningGameName)
                        {
                            _lastDetectedGame = runningGameName;

                            await Dispatcher.InvokeAsync(() =>
                            {
                                getRunningGame(runningGameName);
                            });
                        }
                    }

                }
            }
            catch (Exception ex)
            {
                DiagnosticLogger.LogError(ex, "Failed during sensor tick");
            }
        }

        private PumpVoltage lastWcPump = PumpVoltage.Off;
        private FanSpeed lastWcFan = FanSpeed.Off;
        private RgbState lastWcRgbMode = RgbState.Off;
        private RgbColor lastWcRgbColor = RgbColor.Red;
        private string lastBs2ProFanModeText = "";
        private byte? lastBs2ProGear = 0;
        private ushort? lastBs2ProRpm = 0;
        private string lastBs2ProRgbMode = "";
        private byte lastBs2ProRgbR = 0;
        private byte lastBs2ProRgbG = 0;
        private byte lastBs2ProRgbB = 0;
        private byte lastBs2ProBrightness = 0;
        private string lastBs2ProSpeed = "Medium";
        // EC Fan diff-tracking
        private bool lastEcFanUnifiedMode = false;
        private string lastEcFanPreset = "";
        private string lastEcFanCpuPreset = "";
        private string lastEcFanGpuPreset = "";
        // Keyboard RGB diff-tracking
        private bool lastKbPerKeyMode = false;
        private int lastKbBrightness = 0;
        private string lastKbEffectMode = "";
        private byte lastKbEffectSpeed = 0;
        private byte lastKbColorR = 0;
        private byte lastKbColorG = 0;
        private byte lastKbColorB = 0;
        private string lastKbMultiColors = "";
        private string _kbDirection = "LeftRight";
        private string _kbPerKeyColors = "";
        private string lastKbDirection = "";
        // CPU diff-tracking
        private int lastCpuSustained = 0;
        private int lastCpuBoost = 0;
        private int lastCpuTjMax = 0;
        private bool lastCpuCoUnified = true;
        private int lastCpuCoUnifiedValue = 0;
        private int lastCpuCoCcd0 = 0;
        private int lastCpuCoCcd1 = 0;
        private int lastCpuPl1 = 0;
        private int lastCpuPl2 = 0;
        private int lastCpuUndervolt = 0;
        // GPU diff-tracking
        private int lastGpuTgp = 0;
        private bool lastGpuDynamicBoost = false;
        private int lastGpuDbOffset = 0;
        private int lastGpuCoreClock = 0;
        private int lastGpuMemClock = 0;
        private int lastGpuVfCurveCount = -1;
        // Light Bar diff-tracking
        private bool lastLbPower = false;
        private byte lastLbColorR = 0;
        private byte lastLbColorG = 0;
        private byte lastLbColorB = 0;
        private int lastLbBrightness = 0;
        private byte lastLbEffect = 0;
        private byte lastLbSpeed = 0;
        // V-F Curve editor window (singleton, like Presets page)
        private Views.Windows.VfCurveEditorWindow? _vfEditorWindow;
        // True when user manually changes the core clock slider — curve label is suppressed
        private bool _adpUserTouchedCoreSlider;
        // Per-game V-F curve offsets: pointIndex → frequencyOffsetMHz
        private Dictionary<int, int> _adpVfCurveOffsets = new();
        // KeyboardHidService for Light Bar control
        private KeyboardHidService? _hidService;
        private SemaphoreSlim _updateSemaphore = new(1, 1);
        private async void update()
        {
            // Prevent concurrent executions from adaptive_Tick and sensors_Tick
            if (!await _updateSemaphore.WaitAsync(TimeSpan.Zero))
                return;

            try
            {
                if (start)
                {
                    await ApplyWatercoolerIfNeeded();
                    await ApplyBs2ProIfNeeded();
                    ApplyEcFanIfNeeded();
                    ApplyKeyboardIfNeeded();
                    ApplyCpuIfNeeded();
                    ApplyGpuIfNeeded();
                    ApplyLightBarIfNeeded();
                }
            }
            catch (Exception ex)
            {
                DiagnosticLogger.LogError(ex, "Failed during adaptive mode update");
            }
            finally
            {
                _updateSemaphore.Release();
            }
        }

        #region Adaptive update helpers

        private async Task ApplyWatercoolerIfNeeded()
        {
            if (!Settings.Default.EnableHydroUiNavigation || !_hydroConnected || !(cbxWcEnabled.IsChecked ?? false))
                return;

            bool smartModeActive = _hydroUIService?.IsSmartModeActive == true;
            // Also check if Smart Mode is configured in the service settings.
            // At startup there is a gap between BLE connection and Smart Mode timer
            // starting — during that window IsSmartModeActive is false even though
            // Smart Mode will take over pump/fan control shortly. Checking the
            // configured flag prevents Adaptive from applying a one-shot preset
            // pump voltage that Smart Mode would immediately overwrite.
            bool smartModeConfigured = _hydroUIService?.GetSettings().SmartModeEnabled == true;
            bool skipPumpFan = smartModeActive || smartModeConfigured;
            // Also skip RGB when Smart Mode temperature-based RGB is active
            bool skipRgb = smartModeActive && (_hydroUIService?.GetSettings().SmartModeTemperatureRgb == true);

            // When Smart Mode is active or configured, skip pump voltage and fan speed overrides
            // — Smart Mode controls them internally. RGB still applies unless temperature-based RGB is active.
            PumpVoltage? curPump = skipPumpFan ? null : (PumpVoltage?)GetPumpVoltageFromIndex(cbxWcPumpVoltage.SelectedIndex);
            FanSpeed? curFan = skipPumpFan ? null : GetFanSpeedFromIndex(cbxWcFanSpeed.SelectedIndex);
            RgbState curRgbMode = skipRgb ? lastWcRgbMode : GetRgbModeFromIndex(cbxWcRgbMode.SelectedIndex);
            RgbColor curRgbColor = skipRgb ? lastWcRgbColor : GetRgbColorFromIndex(cbxWcRgbColor.SelectedIndex);

            if (curPump != lastWcPump || curFan != lastWcFan ||
                curRgbMode != lastWcRgbMode || curRgbColor != lastWcRgbColor)
            {
                await _deviceApplier.ApplyWatercoolerAsync(curPump, curFan, curRgbMode, curRgbColor);
                lastWcPump = curPump ?? default;
                lastWcFan = curFan ?? default;
                lastWcRgbMode = curRgbMode;
                lastWcRgbColor = curRgbColor;
            }
        }

        private async Task ApplyBs2ProIfNeeded()
        {
            if (!Settings.Default.EnableFlydigi || !(cbxBs2ProEnabled.IsChecked ?? false))
                return;

            string bs2Mode = GetBs2ProFanModeFromIndex(cbxBs2ProFanMode.SelectedIndex);
            string bs2RgbMode = GetBs2ProRgbModeFromIndex(cbxBs2ProRgbMode.SelectedIndex);
            byte bs2R = (byte)(nudBs2ProRgbR.Value ?? 0);
            byte bs2G = (byte)(nudBs2ProRgbG.Value ?? 0);
            byte bs2B = (byte)(nudBs2ProRgbB.Value ?? 0);
            byte bs2Brightness = (byte)sliderBs2ProBrightness.Value;
            string bs2Speed = GetRgbSpeedNameFromSlider((int)sliderBs2ProRgbSpeed.Value);

            // --- Fan control (differs by mode) ---
            if (bs2Mode == "Curve")
            {
                await ApplyBs2ProCurveMode();
            }
            else
            {
                // For non-curve modes, stop smart control if active
                if (_bs2ProSmartControl != null)
                    StopBs2ProSmartControl();
                if (_bs1SmartControl != null)
                    StopFlydigiBs1SmartControl();

                byte? gear = null;
                ushort? rpm = null;
                if (bs2Mode == "Gear")
                    gear = (byte)(cbxBs2ProGear.SelectedIndex + 1);
                else if (bs2Mode == "Rpm")
                    rpm = (ushort)Math.Clamp((int)(nudBs2ProRpm.Value ?? 0), MinRpm,
                        (FlydigiHardwareDetector.ConnectedDeviceType == ConnectedDeviceType.BS1 ? MaxRpmBs1 : MaxRpmBs2));

                bool fanChanged = bs2Mode != lastBs2ProFanModeText ||
                    (bs2Mode == "Gear" && gear != lastBs2ProGear) ||
                    (bs2Mode == "Rpm" && rpm != lastBs2ProRpm);

                if (fanChanged)
                {
                    await _deviceApplier.ApplyFlydigiFanAsync(bs2Mode, gear, rpm);
                    lastBs2ProFanModeText = bs2Mode;
                    lastBs2ProGear = gear ?? lastBs2ProGear;
                    lastBs2ProRpm = rpm ?? lastBs2ProRpm;
                }
            }

            // --- RGB control (independent of fan mode) ---
            bool rgbChanged = bs2RgbMode != lastBs2ProRgbMode ||
                bs2R != lastBs2ProRgbR || bs2G != lastBs2ProRgbG ||
                bs2B != lastBs2ProRgbB || bs2Brightness != lastBs2ProBrightness ||
                bs2Speed != lastBs2ProSpeed;

            if (rgbChanged)
            {
                await _deviceApplier.ApplyFlydigiRgbAsync(bs2RgbMode, bs2R, bs2G, bs2B, bs2Brightness);

                // Fire preset-applied event so the Flydigi page can sync its UI.
                byte? fanGear = bs2Mode == "Gear" ? (byte?)(cbxBs2ProGear.SelectedIndex + 1) : null;
                ushort? fanRpm = bs2Mode == "Rpm"
                    ? (ushort?)Math.Clamp((int)(nudBs2ProRpm.Value ?? 0), MinRpm,
                        (FlydigiHardwareDetector.ConnectedDeviceType == ConnectedDeviceType.BS1 ? MaxRpmBs1 : MaxRpmBs2))
                    : null;
                _deviceApplier.RaiseFlydigiPresetApplied(bs2Mode, fanGear, fanRpm,
                    bs2RgbMode, bs2R, bs2G, bs2B, bs2Brightness);

                lastBs2ProRgbMode = bs2RgbMode;
                lastBs2ProRgbR = bs2R;
                lastBs2ProRgbG = bs2G;
                lastBs2ProRgbB = bs2B;
                lastBs2ProBrightness = bs2Brightness;
                lastBs2ProSpeed = bs2Speed;
            }
        }

        private async Task ApplyBs2ProCurveMode()
        {
            bool isBs1Curve = FlydigiHardwareDetector.ConnectedDeviceType == ConnectedDeviceType.BS1;

            if (isBs1Curve)
            {
                var bs1Service = App.GetService<FlydigiCoolerBleService>();
                if (bs1Service?.IsConnected != true)
                    return;

                FlydigiFanCurveProfile curveProfile = GetBs1CurveProfile(bs1Service);

                if (_bs1SmartControl == null)
                {
                    var tempProvider = new FlydigiTemperatureProvider();
                    _bs1SmartControl = new FlydigiBs1SmartControl(bs1Service, tempProvider);
                    _bs1SmartControl.ActiveProfile = curveProfile;
                    _bs1SmartControl.Settings = bs1Service.GetSettings();
                    _bs1SmartControl.TempSource = bs1Service.GetSettings().TempSource;
                    _bs1SmartControl.Start();
                }
                else
                {
                    _bs1SmartControl.ActiveProfile = curveProfile;
                }
            }
            else
            {
                var flydigiService = App.GetService<FlydigiCoolerHidService>();
                if (flydigiService?.IsConnected != true)
                    return;

                FlydigiFanCurveProfile curveProfile = GetBs2CurveProfile(flydigiService);

                if (_bs2ProSmartControl == null)
                {
                    _bs2ProTempProvider = new FlydigiTemperatureProvider();
                    _bs2ProSmartControl = new FlydigiSmartControl(flydigiService, _bs2ProTempProvider);
                    _bs2ProSmartControl.ActiveProfile = curveProfile;
                    _bs2ProSmartControl.Start();
                }
                else
                {
                    _bs2ProSmartControl.ActiveProfile = curveProfile;
                }
            }
        }

        private FlydigiFanCurveProfile GetBs1CurveProfile(FlydigiCoolerBleService bs1Service)
        {
            string curveProfileId = GetBs2ProCurveProfileId(cbxBs2ProCurve.SelectedIndex);
            return curveProfileId switch
            {
                CurveProfileSilent => FlydigiFanCurveProfile.CreateSilent(),
                CurveProfileQuiet => FlydigiFanCurveProfile.CreateQuiet(),
                CurveProfilePerformance => FlydigiFanCurveProfile.CreatePerformance(),
                CurveProfileFreezy => FlydigiFanCurveProfile.CreateFreezy(),
                CurveProfileCustom => LoadBs1CustomCurveProfile(bs1Service),
                _ => FlydigiFanCurveProfile.CreateBalanced()
            };
        }

        private FlydigiFanCurveProfile GetBs2CurveProfile(FlydigiCoolerHidService flydigiService)
        {
            string curveProfileId = GetBs2ProCurveProfileId(cbxBs2ProCurve.SelectedIndex);
            return curveProfileId switch
            {
                CurveProfileSilent => FlydigiFanCurveProfile.CreateSilent(),
                CurveProfileQuiet => FlydigiFanCurveProfile.CreateQuiet(),
                CurveProfilePerformance => FlydigiFanCurveProfile.CreatePerformance(),
                CurveProfileFreezy => FlydigiFanCurveProfile.CreateFreezy(),
                CurveProfileCustom => LoadCustomCurveProfile(flydigiService),
                _ => FlydigiFanCurveProfile.CreateBalanced()
            };
        }

        private void ApplyEcFanIfNeeded()
        {
            if (!(cbxEcFanEnabled.IsChecked ?? false) || _deviceApplier == null)
                return;

            bool unified = cbxEcFanMode.SelectedIndex == 0;
            string unifiedPreset = GetEcFanPresetFromIndex(cbxEcFanPreset.SelectedIndex);
            string cpuPreset = GetEcFanPresetFromIndex(cbxEcFanCpuPreset.SelectedIndex);
            string gpuPreset = GetEcFanPresetFromIndex(cbxEcFanGpuPreset.SelectedIndex);

            bool changed = unified != lastEcFanUnifiedMode ||
                (unified && unifiedPreset != lastEcFanPreset) ||
                (!unified && (cpuPreset != lastEcFanCpuPreset || gpuPreset != lastEcFanGpuPreset));

            if (!changed)
                return;

            if (unified)
            {
                var cpuCurve = GetEcFanPresetCurve(unifiedPreset);
                var cpuTemps = Uniwill_Control_Utility.Models.EcFanCurve.CpuTemperatures;

                _deviceApplier.ApplyEcFanFromPreset(true, unifiedPreset,
                    cpuCurve.Duties.ToArray(), null, null,
                    cpuTemps, cpuTemps);

                lastEcFanPreset = unifiedPreset;
            }
            else
            {
                var cpuCurve = GetEcFanPresetCurve(cpuPreset);
                var gpuCurve = GetEcFanPresetCurve(gpuPreset);
                var cpuTemps = Uniwill_Control_Utility.Models.EcFanCurve.CpuTemperatures;
                var gpuTemps = Uniwill_Control_Utility.Models.EcFanCurve.GpuTemperatures;

                _deviceApplier.ApplyEcFanFromPreset(false, cpuPreset,
                    null, cpuCurve.Duties.ToArray(), gpuCurve.Duties.ToArray(),
                    cpuTemps, gpuTemps);

                lastEcFanCpuPreset = cpuPreset;
                lastEcFanGpuPreset = gpuPreset;
            }
            lastEcFanUnifiedMode = unified;
        }

        private void ApplyKeyboardIfNeeded()
        {
            if (!(cbxKbEnabled.IsChecked ?? false) || _deviceApplier == null)
                return;

            bool perKeyMode = cbxKbMode.SelectedIndex == 1;
            int brightness = (int)sdKbBrightness.Value;
            string effectMode = GetKbEffectFromIndex(cbxKbEffect.SelectedIndex);
            byte effectSpeed = (byte)(10 - Math.Clamp((int)sdKbSpeed.Value, 1, 10));
            byte colorR = KbColorPicker.SelectedColor.R;
            byte colorG = KbColorPicker.SelectedColor.G;
            byte colorB = KbColorPicker.SelectedColor.B;
            string multiColors = SerializeColors(KbMultiColorPicker.GetColors());

            bool changed = perKeyMode != lastKbPerKeyMode ||
                brightness != lastKbBrightness ||
                effectMode != lastKbEffectMode ||
                effectSpeed != lastKbEffectSpeed ||
                _kbDirection != lastKbDirection ||
                colorR != lastKbColorR ||
                colorG != lastKbColorG ||
                colorB != lastKbColorB ||
                multiColors != lastKbMultiColors;

            if (!changed)
                return;

            // In per-key mode, pass the current per-key colors from the field
            // (set by LoadKeyboardToUI or btnKbEditPerKey_Click)
            string? perKeyColorsStr = perKeyMode
                ? _kbPerKeyColors
                : null;

            _deviceApplier.ApplyKeyboardFromPreset(
                perKeyMode, brightness, false, 0,
                effectMode, effectSpeed, _kbDirection,
                colorR, colorG, colorB,
                multiColors, perKeyColorsStr);

            lastKbPerKeyMode = perKeyMode;
            lastKbBrightness = brightness;
            lastKbEffectMode = effectMode;
            lastKbEffectSpeed = effectSpeed;
            lastKbDirection = _kbDirection;
            lastKbColorR = colorR;
            lastKbColorG = colorG;
            lastKbColorB = colorB;
            lastKbMultiColors = multiColors;
        }

        private void ApplyCpuIfNeeded()
        {
            if (!(cbxCpuEnabled.IsChecked ?? false))
                return;

            if (Family.TYPE == Family.ProcessorType.Amd_Apu)
            {
                ApplyAmdCpuIfNeeded();
            }
            else if (Family.TYPE == Family.ProcessorType.Intel)
            {
                ApplyIntelCpuIfNeeded();
            }
        }

        private void ApplyAmdCpuIfNeeded()
        {
            int sustained = (int)AdpSdSustained.Value;
            int boost = (int)AdpSdBoost.Value;
            int tjMax = (int)AdpSdTjMax.Value;

            int coUnifiedValue = (int)AdpSdCoUnified.Value;
            int coCcd0 = (int)AdpSdCoCcd0.Value;
            int coCcd1 = (int)AdpSdCoCcd1.Value;
            bool coUnified = AdpRbCoUnified.IsChecked == true;

            if (sustained != lastCpuSustained || boost != lastCpuBoost ||
                tjMax != lastCpuTjMax ||
                coUnified != lastCpuCoUnified ||
                coUnifiedValue != lastCpuCoUnifiedValue ||
                coCcd0 != lastCpuCoCcd0 ||
                coCcd1 != lastCpuCoCcd1)
            {
                try
                {
                    string cmd = BuildAmdCpuCommand(sustained, boost, tjMax,
                        AdpCpuVoltageEnabled, coUnified, coUnifiedValue, coCcd0, coCcd1);
                    _ = RyzenAdj_To_UCU.TranslateAsync(cmd);

                    // Also write EC CPU power limits
                    var ec = RyzenAdj_To_UCU.EcService;
                    if (ec != null)
                        ec.ApplyCpuPowerLimits(sustained, boost);
                }
                catch (Exception ex)
                {
                    DiagnosticLogger.LogError(ex, "Failed to apply CPU settings in Adaptive Mode");
                }

                lastCpuSustained = sustained;
                lastCpuBoost = boost;
                lastCpuTjMax = tjMax;
                lastCpuCoUnified = coUnified;
                lastCpuCoUnifiedValue = coUnifiedValue;
                lastCpuCoCcd0 = coCcd0;
                lastCpuCoCcd1 = coCcd1;
            }
        }

        private static string BuildAmdCpuCommand(int sustained, int boost, int tjMax,
            bool coEnabled, bool coUnified, int coUnifiedValue, int coCcd0, int coCcd1)
        {
            var parts = new System.Collections.Generic.List<string>();
            parts.Add($"--slow-limit={sustained * 1000}");
            parts.Add($"--fast-limit={boost * 1000}");
            parts.Add($"--tctl-temp={tjMax}");
            parts.Add($"--cHTC-temp={tjMax}");

            if (coEnabled)
            {
                if (coUnified)
                {
                    int biased = CoBiasBase - Math.Abs(coUnifiedValue);
                    parts.Add($"--set-coall={biased:X}");
                }
                else
                {
                    int[,] ccdMap = { { 0, 1, 2, 3, 4, 5, 6, 7 }, { 8, 9, 10, 11, 12, 13, 14, 15 } };
                    for (int ccd = 0; ccd < 2; ccd++)
                    {
                        int offset = ccd == 0 ? coCcd0 : coCcd1;
                        int biased = CoBiasBase - Math.Abs(offset);
                        for (int core = 0; core < 8; core++)
                        {
                            parts.Add($"--set-coper={BuildCoperArg(ccd, ccdMap[ccd, core], biased):X}");
                        }
                    }
                }
            }

            return string.Join(" ", parts);
        }

        private void ApplyIntelCpuIfNeeded()
        {
            int pl1 = (int)AdpSdIntelPl1.Value;
            int pl2 = (int)AdpSdIntelPl2.Value;
            int uv = (int)AdpSdIntelUndervolt.Value;

            if (pl1 != lastCpuPl1 || pl2 != lastCpuPl2 || uv != lastCpuUndervolt)
            {
                try
                {
                    var parts = new System.Collections.Generic.List<string>();
                    parts.Add($"--intel-pl={pl1:F0},{pl2:F0}");
                    if (AdpCpuVoltageEnabled && uv < 0)
                        parts.Add($"--intel-volt-cpu={uv:F0}");

                    string cmd = string.Join(" ", parts);
                    _ = RyzenAdj_To_UCU.TranslateAsync(cmd);
                }
                catch (Exception ex)
                {
                    DiagnosticLogger.LogError(ex, "Failed to apply Intel CPU settings in Adaptive Mode");
                }

                lastCpuPl1 = pl1;
                lastCpuPl2 = pl2;
                lastCpuUndervolt = uv;
            }
        }

        private void ApplyGpuIfNeeded()
        {
            if (!(cbxGpuEnabled.IsChecked ?? false) || sdGpuSettings.Visibility != Visibility.Visible)
                return;

            int tgp = (int)AdpSdNvPower.Value;
            bool boostEnabled = Settings.Default.EnablePowerBoostMode;
            bool dbEnabled = !boostEnabled && AdpTogDynamicBoost.IsChecked == true;
            int dbOffset = dbEnabled ? (int)AdpSdNvDbOffset.Value : 0;
            bool gpuClockDisabled = !Settings.Default.EnableGpuClockControl;
            int memClock = gpuClockDisabled ? 0 : (int)AdpSdNvMem.Value;

            // When a V-F curve is active and user hasn't touched the slider,
            // skip the core clock offset — the curve handles per-point frequency.
            int coreClock = gpuClockDisabled
                ? 0
                : (AdpVfCurveActive && !_adpUserTouchedCoreSlider)
                    ? 0 : (int)AdpSdNvCore.Value;

            bool changed = tgp != lastGpuTgp ||
                dbEnabled != lastGpuDynamicBoost ||
                dbOffset != lastGpuDbOffset ||
                coreClock != lastGpuCoreClock ||
                memClock != lastGpuMemClock;

            if (changed)
            {
                try
                {
                    int totalWatts = tgp + dbOffset;
                    string nvCmd = $"--NVIDIA-Clocks=0-{coreClock}-{memClock}-{totalWatts}-{dbOffset}";
                    _ = RyzenAdj_To_UCU.TranslateAsync(nvCmd);
                }
                catch (Exception ex)
                {
                    DiagnosticLogger.LogError(ex, "Failed to apply GPU settings in Adaptive Mode");
                }

                lastGpuTgp = tgp;
                lastGpuDynamicBoost = dbEnabled;
                lastGpuDbOffset = dbOffset;
                lastGpuCoreClock = coreClock;
                lastGpuMemClock = memClock;
            }

            // Apply V-F curve if it changed (only when Adaptive Mode running + GPU enabled)
            int curveCount = _adpVfCurveOffsets.Count;
            if (curveCount != lastGpuVfCurveCount)
            {
                ApplyActiveVfCurveToGpu();
                lastGpuVfCurveCount = curveCount;
            }
        }

        private void ApplyLightBarIfNeeded()
        {
            if (!Settings.Default.EnableLightBarNavigation ||
                !(cbxLbEnabled.IsChecked ?? false) ||
                sdLightBar.Visibility != Visibility.Visible ||
                _hidService == null)
                return;

            bool lbPower = AdpTsLightBarPower.IsChecked == true;
            byte lbR = AdpLbColorPicker.SelectedColor.R;
            byte lbG = AdpLbColorPicker.SelectedColor.G;
            byte lbB = AdpLbColorPicker.SelectedColor.B;
            int lbBrightness = (int)AdpLbBrightnessSlider.Value;
            byte lbEffect = GetLbEffectByte(AdpLbEffect.SelectedIndex);
            byte lbSpeed = GetLbHidSpeed((int)AdpLbSpeedSlider.Value);

            bool lbChanged = lbPower != lastLbPower ||
                lbR != lastLbColorR || lbG != lastLbColorG || lbB != lastLbColorB ||
                lbBrightness != lastLbBrightness ||
                lbEffect != lastLbEffect || lbSpeed != lastLbSpeed;

            if (!lbChanged)
                return;

            try
            {
                if (lbPower)
                {
                    _hidService.LightBarSetColor(lbR, lbG, lbB, lbBrightness);
                    _hidService.LightBarSetEffect(lbEffect, lbSpeed, lbBrightness);
                }
                else
                {
                    _hidService.LightBarTurnOff();
                }
            }
            catch (Exception ex)
            {
                DiagnosticLogger.LogError(ex, "Failed to apply Light Bar settings in Adaptive Mode");
            }

            lastLbPower = lbPower;
            lastLbColorR = lbR;
            lastLbColorG = lbG;
            lastLbColorB = lbB;
            lastLbBrightness = lbBrightness;
            lastLbEffect = lbEffect;
            lastLbSpeed = lbSpeed;
        }

        #endregion

        private void cbAutoSwitch_Click(object sender, RoutedEventArgs e)
        {
            Settings.Default.autoSwitch = cbAutoSwitch.IsChecked ?? false;
            Settings.Default.Save();
        }

        /// <summary>
        /// Restores the btnStart button state when the page becomes visible.
        /// Page.Loaded only fires once (wpfui reuses the same scoped instance),
        /// so we use IsVisibleChanged which fires on every navigation back to this page.
        ///
        /// Deferred to ContextIdle so it runs AFTER LocalizationService.ApplyTree()
        /// has finished re-applying translations (which fires at Loaded priority and
        /// would otherwise overwrite our button text).
        /// </summary>
        private void Page_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if ((bool)e.NewValue)
            {
                Dispatcher.InvokeAsync(new Action(RestoreAdaptiveButtonState), DispatcherPriority.Normal);
            }
        }

        private void Page_Loaded(object sender, RoutedEventArgs e)
        {
            // Recheck Flydigi card visibility on navigation (device may have connected/disconnected)
            UpdateFlydigiCardVisibility();

            // Apply Smart Mode state on navigation
            bool smartModeActive = _hydroUIService?.IsSmartModeActive == true;
            if (smartModeActive)
            {
                spWcPumpFan.Visibility = Visibility.Collapsed;
                spWcSmartModeBadge.Visibility = Visibility.Visible;
                bool tempRgb = _hydroUIService?.GetSettings().SmartModeTemperatureRgb == true;
                spWcRgb.Visibility = tempRgb ? Visibility.Collapsed : Visibility.Visible;
                tbWcSmartModeBadgeText.Text = "Smart Mode ON";
            }
            else
            {
                spWcPumpFan.Visibility = Visibility.Visible;
                spWcSmartModeBadge.Visibility = Visibility.Collapsed;
                spWcRgb.Visibility = Visibility.Visible;
            }

            // Refresh CPU voltage control visibility so setting changes from Settings page take effect
            UpdateAdpCpuUI();

            // Refresh GPU clock control visibility so setting changes from Settings page take effect
            AdpGpuClockControlsPanel.Visibility = Settings.Default.EnableGpuClockControl
                ? Visibility.Visible : Visibility.Collapsed;

            // Re-apply feature visibility gates so cards hide when Settings toggle is OFF
            ApplyFeatureVisibilityGates();

            // Apply dynamic power limit max and update GPU boost indicator
            ApplyAdpPowerLimitMax();

            // Update bottom bar boost mode label
            UpdateAdpPresetModeLabel(Settings.Default.EnablePowerBoostMode);

            // Defer preset loading to after the first frame renders so the page
            // appears immediately and control updates happen in a second pass.
            if (cbxPowerPreset.SelectedItem is string presetName)
            {
                // Sync _lastDetectedGame with the current combobox selection so that
                // sensors_Tick doesn't trigger a spurious switch on first navigation.
                _lastDetectedGame = presetName;

                Dispatcher.InvokeAsync(() =>
                {
                    adaptivePresetManager = new AdaptivePresetManager(Settings.Default.Path + "adaptivePresets.json");
                    // Don't reset tracking on navigation — just load the preset UI values
                    loadPreset(presetName, resetTracking: false);
                    // Ensure Keyboard RGB effect controls visibility matches the mode selector.
                    // Needed because loadPreset skips UI updates when preset is null
                    // (first-time load with no saved presets).
                    UpdateKbModeUI();
                }, DispatcherPriority.Render);
            }
        }

        private void Page_Unloaded(object sender, RoutedEventArgs e)
        {
            // Save current UI values back to the active preset so that
            // unsaved changes (e.g., speed, brightness, color) persist
            // across navigation. Without this, Page_Loaded reloads the
            // preset from disk and overwrites in-flight edits.
            if (cbxPowerPreset.SelectedItem is string presetName && adaptivePresetManager != null)
            {
                try
                {
                    var existing = adaptivePresetManager.GetPreset(presetName);
                    if (existing != null)
                    {
                        // Patch only the keyboard RGB fields that can be edited
                        // without clicking Save Preset.
                        existing.KbEffectSpeed = (byte)Math.Clamp((int)sdKbSpeed.Value, 1, 10);
                        existing.KbBrightness = (int)sdKbBrightness.Value;
                        existing.KbEffectMode = GetKbEffectFromIndex(cbxKbEffect.SelectedIndex);
                        existing.KbColorR = KbColorPicker.SelectedColor.R;
                        existing.KbColorG = KbColorPicker.SelectedColor.G;
                        existing.KbColorB = KbColorPicker.SelectedColor.B;
                        existing.KbMultiColors = SerializeColors(KbMultiColorPicker.GetColors());
                        existing.KbPerKeyMode = cbxKbMode.SelectedIndex == 1;
                        existing.KbDirection = _kbDirection;

                        adaptivePresetManager.SavePreset(presetName, existing);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Adaptive] Page_Unloaded save failed: {ex.Message}");
                }
            }

            // Unsubscribe from HydroUIService events to allow this scoped instance to be GC'd.
            if (_hydroUIService != null)
            {
                _hydroUIService.ConnectionStateChanged -= OnWaterCoolerConnectionStateChanged;
                _hydroUIService.SmartModeStateChanged -= OnSmartModeStateChanged;
            }
        }


        private string runningGameName = DefaultProfileName;
        private string lastConfirmedGame = DefaultProfileName;
        private string _lastDetectedGame = DefaultProfileName;
        private int gameMissingCount = 0;
        private const int maxGameMisses = 2;

        private bool AutoSwitchEnabled(GameLauncherItem item)
        {
            var preset = adaptivePresetManager.GetPreset(item.gameName);
            return preset?.isAutoSwitch ?? true;
        }

        /// <summary>Pass 1: iterate games and processes to match executable paths (same structure as UXTU original).</summary>
        private string? DetectGameByProcess()
        {
            var games = installedGames ?? Enumerable.Empty<GameLauncherItem>();

            foreach (GameLauncherItem item in games)
            {
                if (string.IsNullOrEmpty(item.path))
                    continue;

                for (int attempt = 0; attempt < 2; attempt++)
                {
                    foreach (Process process in Process.GetProcesses())
                    {
                        try
                        {
                            var mainModule = process.MainModule;
                            if (mainModule == null)
                                continue;

                            string fileName = mainModule.FileName;

                            if (fileName.Contains(item.path, StringComparison.OrdinalIgnoreCase)
                                && AutoSwitchEnabled(item))
                            {
                                return item.gameName;
                            }

                            if (!string.IsNullOrEmpty(item.exe) &&
                                process.ProcessName.Contains(item.exe, StringComparison.OrdinalIgnoreCase)
                                && AutoSwitchEnabled(item))
                            {
                                return item.gameName;
                            }
                        }
                        catch (Exception)
                        {
                            // Process.MainModule throws for elevated/protected processes - skip silently
                        }
                    }
                }
            }
            return null;
        }

        /// <summary>Pass 2: EnumWindows fallback for fullscreen/elevated/minimized games.</summary>
        private string? DetectGameByWindowTitle()
        {
            var matchedGameNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var classNameSb = new StringBuilder(256);
            var titleSb = new StringBuilder(256);

            EnumWindows((hWnd, lParam) =>
            {
                // IsWindowVisible returns false for minimized windows, but we still want
                // to detect games that are minimized. A minimized window is still "visible"
                // in the sense that it's not destroyed or hidden (WS_VISIBLE is still set).
                // Check WS_VISIBLE flag directly instead of IsWindowVisible.
                IntPtr style = GetWindowLong(hWnd, GWL_STYLE);
                if ((style & (IntPtr)WS_VISIBLE) == IntPtr.Zero)
                    return true;

                classNameSb.Clear();
                _ = GetClassName(hWnd, classNameSb, classNameSb.Capacity);
                if (_excludedWindowClasses.Contains(classNameSb.ToString()))
                    return true;

                int length = GetWindowTextLength(hWnd);
                if (length == 0)
                    return true;

                if (length + 1 > titleSb.Capacity)
                    titleSb.Capacity = length + 1;

                titleSb.Clear();
                _ = GetWindowText(hWnd, titleSb, titleSb.Capacity);
                string windowTitle = titleSb.ToString().Trim();

                foreach (var gameName in (installedGames ?? Enumerable.Empty<GameLauncherItem>())
                    .Where(item => windowTitle.Contains(item.gameName, StringComparison.OrdinalIgnoreCase)
                        && AutoSwitchEnabled(item))
                    .Select(item => item.gameName))
                {
                    matchedGameNames.Add(gameName);
                }

                return true;
            }, IntPtr.Zero);

            if (matchedGameNames.Count > 0)
                return matchedGameNames.OrderByDescending(n => n.Length).First();

            return null;
        }

        /// <summary>Commit detection: apply game preset or revert to default with delay.</summary>
        private void CommitGameDetection(string? detected)
        {
            if (detected != null)
            {
                lastConfirmedGame = detected;
                runningGameName = detected;
                gameMissingCount = 0;
            }
            else if (lastConfirmedGame != DefaultProfileName)
            {
                gameMissingCount++;
                if (gameMissingCount >= maxGameMisses)
                {
                    runningGameName = DefaultProfileName;
                    lastConfirmedGame = DefaultProfileName;
                    gameMissingCount = 0;
                }
            }
        }

        private void isGameRunning()
        {
            string? detected = DetectGameByProcess() ?? DetectGameByWindowTitle();
            CommitGameDetection(detected);
        }


        private void getRunningGame(string presetName)
        {
            foreach (var item in cbxPowerPreset.Items)
            {
                if (item.ToString() == presetName)
                {
                    // Update the combo box selection to reflect the detected game.
                    _isUpdatingPresetSelection = true;
                    cbxPowerPreset.SelectedItem = item;
                    _isUpdatingPresetSelection = false;

                    // Load the new preset's UI values. The device override flags being
                    // active does not prevent profile switching — the new profile may
                    // have different override settings (ON vs OFF) that need to apply.
                    // Use resetTracking: true so the next update() tick applies the
                    // new preset's settings to the device and CPU/GPU controls.
                    loadPreset(presetName, resetTracking: true);
                    return;
                }
            }

            // Fallback to Default if the preset wasn't found.
            _isUpdatingPresetSelection = true;
            cbxPowerPreset.SelectedIndex = 0;
            _isUpdatingPresetSelection = false;

             // Use resetTracking: true so the next update() tick applies the Default preset.
            loadPreset(DefaultProfileName, resetTracking: true);
        }

        #region Watercooler Helpers

        /// <summary>
        /// Handles watercooler connection/disconnection events at runtime.
        /// Shows or hides the Hydro UI card and disables the checkbox when disconnected.
        /// </summary>
        private void OnWaterCoolerConnectionStateChanged(object? sender, HydroUIService.WatercoolerConnectionState state)
        {
            Dispatcher.Invoke(() =>
            {
                bool wasConnected = _hydroConnected;
                _hydroConnected = state == HydroUIService.WatercoolerConnectionState.Connected;

                if (_hydroConnected && !wasConnected)
                {
                    // Cooler just connected — show the card (respecting Settings gate).
                    sdHydroUI.Visibility = Visibility.Visible;
                    ApplyFeatureVisibilityGates();
                }
                else if (!_hydroConnected && wasConnected)
                {
                    // Cooler just disconnected — hide card, disable checkbox, stop override.
                    sdHydroUI.Visibility = Visibility.Collapsed;
                    cbxWcEnabled.IsChecked = false;
                    Settings.Default.AdaptiveWcEnabled = false;
                    Settings.Default.Save();

                    if (start && _deviceApplier?.IsWatercoolerOverridden == true)
                        _ = _deviceApplier.DisableWatercoolerOverrideAsync();
                }
            });
        }

        private void OnSmartModeStateChanged(object? sender, bool isActive)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                if (isActive)
                {
                    spWcPumpFan.Visibility = Visibility.Collapsed;
                    spWcSmartModeBadge.Visibility = Visibility.Visible;
                    tbWcSmartModeBadgeText.Text = "Smart Mode ON";

                    // Hide RGB controls when Smart Mode temperature-based RGB is active
                    bool tempRgb = _hydroUIService?.GetSettings().SmartModeTemperatureRgb == true;
                    spWcRgb.Visibility = tempRgb ? Visibility.Collapsed : Visibility.Visible;
                }
                else
                {
                    spWcPumpFan.Visibility = Visibility.Visible;
                    spWcSmartModeBadge.Visibility = Visibility.Collapsed;
                    spWcRgb.Visibility = Visibility.Visible;
                }

                // Reset pump/fan tracking so the next update() tick picks up the
                // correct state (null when Smart Mode is active, profile values
                // when inactive).
                lastWcPump = PumpVoltage.Off;
                lastWcFan = FanSpeed.Off;
            });
        }

        private static PumpVoltage GetPumpVoltageFromIndex(int index)
        {
            return (index + 1) switch
            {
                1 => PumpVoltage.Off,
                2 => PumpVoltage.V7,
                3 => PumpVoltage.V8,
                _ => PumpVoltage.V11
            };
        }

        private static int GetPumpVoltageIndex(string voltageText)
        {
            if (Enum.TryParse<PumpVoltage>(voltageText, true, out var voltage))
                return GetPumpVoltageIndex(voltage);
            return 0;
        }

        private static int GetPumpVoltageIndex(PumpVoltage voltage)
        {
            return voltage switch
            {
                PumpVoltage.Off => 0,
                PumpVoltage.V7 => 1,
                PumpVoltage.V8 => 2,
                PumpVoltage.V11 => 3,
                _ => 0
            };
        }

        private static FanSpeed GetFanSpeedFromIndex(int index)
        {
            return (index + 1) switch
            {
                1 => FanSpeed.Off,
                2 => FanSpeed.Percent25,
                3 => FanSpeed.Percent50,
                4 => FanSpeed.Percent75,
                5 => FanSpeed.Percent90,
                6 => FanSpeed.Percent95,
                _ => FanSpeed.Percent100
            };
        }

        private static int GetFanSpeedIndex(string speedText)
        {
            if (Enum.TryParse<FanSpeed>(speedText, true, out var speed))
                return GetFanSpeedIndex(speed);
            return 0;
        }

        private static int GetFanSpeedIndex(FanSpeed speed)
        {
            return speed switch
            {
                FanSpeed.Off => 0,
                FanSpeed.Percent25 => 1,
                FanSpeed.Percent50 => 2,
                FanSpeed.Percent75 => 3,
                FanSpeed.Percent90 => 4,
                FanSpeed.Percent95 => 5,
                FanSpeed.Percent100 => 6,
                _ => 0
            };
        }

        private static RgbState GetRgbModeFromIndex(int index)
        {
            return (index + 1) switch
            {
                1 => RgbState.Off,
                2 => RgbState.Static,
                3 => RgbState.Breathe,
                4 => RgbState.Colorful,
                _ => RgbState.BreatheColor
            };
        }

        private static int GetRgbModeIndex(RgbState mode)
        {
            return mode switch
            {
                RgbState.Off => 0,
                RgbState.Static => 1,
                RgbState.Breathe => 2,
                RgbState.Colorful => 3,
                RgbState.BreatheColor => 4,
                _ => 0
            };
        }

        private static RgbColor GetRgbColorFromIndex(int index)
        {
            return (index + 1) switch
            {
                1 => RgbColor.Red,
                2 => RgbColor.Green,
                3 => RgbColor.Blue,
                _ => RgbColor.White
            };
        }

        private static int GetRgbColorIndex(RgbColor color)
        {
            return color switch
            {
                RgbColor.Red => 0,
                RgbColor.Green => 1,
                RgbColor.Blue => 2,
                RgbColor.White => 3,
                _ => 0
            };
        }

        private void cbxWcRgbMode_SelectionChanged(object sender, RoutedEventArgs e)
        {
            if (spWcRgbColor == null) return;

            var mode = GetRgbModeFromIndex(cbxWcRgbMode.SelectedIndex);
            var visibility = mode switch
            {
                RgbState.Static or RgbState.Breathe => Visibility.Visible,
                _ => Visibility.Collapsed
            };
            spWcRgbColor.Visibility = visibility;
            WcRgbColorDivider.Visibility = visibility;
        }

        #endregion

        #region BS2 Pro Helpers

        private static string GetBs2ProFanModeFromIndex(int index)
        {
            return (index + 1) switch
            {
                1 => FanModeGear,
                2 => FanModeRpm,
                _ => FanModeCurve
            };
        }

        private static int GetBs2ProFanModeIndex(string mode)
        {
            return mode switch
            {
                FanModeGear => 0,
                FanModeRpm => 1,
                FanModeCurve => 2,
                _ => 0
            };
        }

        private static string GetBs2ProCurveProfileId(int index)
        {
            return index switch
            {
                0 => CurveProfileSilent,
                1 => CurveProfileQuiet,
                2 => CurveProfileBalanced,
                3 => CurveProfilePerformance,
                4 => CurveProfileFreezy,
                5 => CurveProfileCustom,
                _ => CurveProfileSilent
            };
        }

        private static int GetBs2ProCurveIndex(string profileId)
        {
            return profileId switch
            {
                CurveProfileSilent => 0,
                CurveProfileQuiet => 1,
                CurveProfileBalanced => 2,
                CurveProfilePerformance => 3,
                CurveProfileFreezy => 4,
                CurveProfileCustom => 5,
                _ => 0
            };
        }

        /// <summary>Stops and disposes the BS2 Pro smart control (curve mode) if active.</summary>
        private void StopBs2ProSmartControl()
        {
            if (_bs2ProSmartControl != null)
            {
                try { _bs2ProSmartControl.Stop(); } catch (Exception) { /* ignore */ }
                try { _bs2ProSmartControl.Dispose(); } catch (Exception) { /* ignore */ }
                _bs2ProSmartControl = null;
            }
            if (_bs2ProTempProvider != null)
            {
                try { _bs2ProTempProvider.Dispose(); } catch (Exception) { /* ignore */ }
                _bs2ProTempProvider = null;
            }
        }

        private void StopFlydigiBs1SmartControl()
        {
            if (_bs1SmartControl != null)
            {
                try { _bs1SmartControl.Stop(); } catch (Exception) { /* ignore */ }
                try { _bs1SmartControl.Dispose(); } catch (Exception) { /* ignore */ }
                _bs1SmartControl = null;
            }
        }

        /// <summary>Loads the custom curve profile from Bs1 settings, falling back to Balanced.</summary>
        private static FlydigiFanCurveProfile LoadBs1CustomCurveProfile(FlydigiCoolerBleService bs1Service)
        {
            try
            {
                var bs1Settings = bs1Service.GetSettings();
                if (!string.IsNullOrEmpty(bs1Settings.CustomCurveJson))
                {
                    return FlydigiFanCurveProfile.FromJSON(bs1Settings.CustomCurveJson);
                }
            }
            catch (Exception)
            {
                // Corrupted custom curve JSON — fall back to Balanced
            }
            return FlydigiFanCurveProfile.CreateBalanced();
        }

        /// <summary>Loads the custom curve profile from Bs2Pro settings, falling back to Balanced.</summary>
        private static FlydigiFanCurveProfile LoadCustomCurveProfile(FlydigiCoolerHidService? flydigiService)
        {
            try
            {
                if (flydigiService == null)
                    return FlydigiFanCurveProfile.CreateBalanced();
                var bs2Settings = flydigiService.GetSettings();
                if (!string.IsNullOrEmpty(bs2Settings.CustomCurveJson))
                {
                    return FlydigiFanCurveProfile.FromJSON(bs2Settings.CustomCurveJson);
                }
            }
            catch (Exception)
            {
                // Corrupted custom curve JSON — fall back to Balanced
            }
            return FlydigiFanCurveProfile.CreateBalanced();
        }

        private void cbxBs2ProFanMode_SelectionChanged(object sender, EventArgs e)
        {
            UpdateBs2ProModeUI();
        }

        private void UpdateBs2ProModeUI()
        {
            // Guard: controls may be null if sdBs2Pro is still collapsed (hardware not detected yet)
            if (spBs2ProGear == null)
                return;

            var mode = cbxBs2ProFanMode.SelectedIndex;
            Bs2ProFanSubDivider.Visibility = Visibility.Visible;
            spBs2ProGear.Visibility = mode == 0 ? Visibility.Visible : Visibility.Collapsed;
            spBs2ProRpm.Visibility = mode == 1 ? Visibility.Visible : Visibility.Collapsed;
            spBs2ProCurve.Visibility = mode == 2 ? Visibility.Visible : Visibility.Collapsed;

            // Hide curve editor when leaving Curve (Auto) mode — it only makes sense
            // inside the Curve mode sub-pane. Re-show if already in Curve + Custom.
            if (mode != 2)
            {
                Bs2ProCurveEditorDivider.Visibility = Visibility.Collapsed;
                Bs2ProCurveEditor.Visibility = Visibility.Collapsed;
            }
            else if (Bs2ProCurveEditor is not null && cbxBs2ProCurve?.SelectedIndex == 5)
            {
                // Re-entering Curve mode with Custom selected — re-show editor
                Bs2ProCurveEditorDivider.Visibility = Visibility.Visible;
                Bs2ProCurveEditor.Visibility = Visibility.Visible;
                try
                {
                    var flydigiService = App.GetService<FlydigiCoolerHidService>();
                    Bs2ProCurveEditor.SetCurve(LoadCustomCurveProfile(flydigiService));
                }
                catch (Exception)
                {
                    Bs2ProCurveEditor.SetCurve(FlydigiFanCurveProfile.CreateBalanced());
                }
            }
        }

        private void cbxBs2ProCurve_SelectionChanged(object sender, EventArgs e)
        {
            // Guard: controls may be null if CardExpander content not realized
            if (cbxBs2ProCurve is null || Bs2ProCurveEditor is null)
                return;

            bool isCustom = cbxBs2ProCurve.SelectedIndex == 5;
            Bs2ProCurveEditorDivider.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;
            Bs2ProCurveEditor.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;

            if (isCustom)
            {
                // Load custom curve or fall back to Balanced default
                try
                {
                    var flydigiService = App.GetService<FlydigiCoolerHidService>();
                    Bs2ProCurveEditor.SetCurve(LoadCustomCurveProfile(flydigiService));
                }
                catch (Exception)
                {
                    Bs2ProCurveEditor.SetCurve(FlydigiFanCurveProfile.CreateBalanced());
                }
            }
        }

        private void btnBs2ProEditCurve_Click(object sender, RoutedEventArgs e)
        {
            // Load the current custom curve from Bs2ProSettings if it exists
            var bs2SettingsPath = System.IO.Path.Combine(Settings.Default.Path, Bs2ProSettingsFile);
            FlydigiFanCurveProfile? seedProfile = FlydigiFanCurveProfile.CreateBalanced();

            if (System.IO.File.Exists(bs2SettingsPath))
            {
                try
                {
                    var bs2Settings = Newtonsoft.Json.JsonConvert.DeserializeObject<
                        Uniwill_Control_Utility.Models.Bs2ProSettings>(
                        System.IO.File.ReadAllText(bs2SettingsPath));
                    if (bs2Settings != null && !string.IsNullOrEmpty(bs2Settings.CustomCurveJson))
                    {
                        seedProfile = Uniwill_Control_Utility.Models.FlydigiFanCurveProfile.FromJSON(bs2Settings.CustomCurveJson);
                    }
                }
                catch (Exception) { /* use default balanced */ }
            }

            var dialog = new Views.Windows.FlydigiCurveEditorWindow(seedProfile);
            if (dialog.ShowDialog() != true || dialog.EditedProfile == null)
                return;

            // Persist the custom curve to bs2pro_settings.json
            try
            {
                var bs2Settings = new Uniwill_Control_Utility.Models.Bs2ProSettings();
                if (System.IO.File.Exists(bs2SettingsPath))
                {
                    bs2Settings = Newtonsoft.Json.JsonConvert.DeserializeObject<
                        Uniwill_Control_Utility.Models.Bs2ProSettings>(
                        System.IO.File.ReadAllText(bs2SettingsPath)) ?? bs2Settings;
                }
                bs2Settings.CustomCurveJson = dialog.EditedProfile.ToJSON();
                bs2Settings.SelectedCurveProfile = CurveProfileCustom;
                System.IO.File.WriteAllText(bs2SettingsPath,
                    Newtonsoft.Json.JsonConvert.SerializeObject(bs2Settings, Newtonsoft.Json.Formatting.Indented));
            }
            catch (Exception ex)
            {
                DiagnosticLogger.LogError(ex, "Failed to save custom curve from Adaptive Mode");
            }

            // The Flydigi page will pick up the new curve on its next smart control tick
            // because it reads from the same settings file.
        }

        // RGB helpers

        private static string GetBs2ProRgbModeFromIndex(int index)
        {
            return (index + 1) switch
            {
                1 => RgbModeOff,
                2 => RgbModeSmartTemp,
                3 => RgbModeStatic,
                4 => RgbModeBreathing,
                5 => RgbModeFlowing,
                _ => RgbModeStatic
            };
        }

        private static int GetBs2ProRgbModeIndex(string mode)
        {
            return mode switch
            {
                RgbModeOff => 0,
                RgbModeSmartTemp => 1,
                RgbModeStatic => 2,
                RgbModeBreathing => 3,
                RgbModeFlowing => 4,
                _ => 2
            };
        }

        private void cbxBs2ProRgbMode_SelectionChanged(object sender, EventArgs e)
        {
            UpdateBs2ProRgbUI();
        }

        private void UpdateBs2ProRgbUI()
        {
            // Guard: CardExpander content may not be realized yet (IsExpanded=false).
            // All content controls are null until the user expands the card.
            if (spBs2ProRgbOff is null)
                return;

            // Hide all first, then show what's needed for the selected mode
            HideAllBs2ProRgbControls();

            var mode = cbxBs2ProRgbMode.SelectedIndex;

            switch (mode)
            {
                case 0: // Off
                    Bs2ProRgbSubDivider.Visibility = Visibility.Visible;
                    spBs2ProRgbOff.Visibility = Visibility.Visible;
                    break;
                case 1: // Smart-Temp
                    Bs2ProRgbSubDivider.Visibility = Visibility.Visible;
                    spBs2ProRgbSmartTemp.Visibility = Visibility.Visible;
                    break;
                case 2: // Static
                    Bs2ProRgbSubDivider.Visibility = Visibility.Visible;
                    spBs2ProRgbColorPicker.Visibility = Visibility.Visible;
                    Bs2ProRgbStaticBrightnessDivider.Visibility = Visibility.Visible;
                    spBs2ProRgbBrightness.Visibility = Visibility.Visible;
                    break;
                case 3: // Breathing
                    Bs2ProRgbSubDivider.Visibility = Visibility.Visible;
                    spBs2ProRgbColorPicker.Visibility = Visibility.Visible;
                    Bs2ProRgbStaticBrightnessDivider.Visibility = Visibility.Visible;
                    spBs2ProRgbBrightness.Visibility = Visibility.Visible;
                    break;
                case 4: // Flowing — no color picker, so skip the sub divider to avoid a bare divider above Brightness
                    Bs2ProRgbStaticBrightnessDivider.Visibility = Visibility.Visible;
                    spBs2ProRgbBrightness.Visibility = Visibility.Visible;
                    Bs2ProRgbFlowingSpeedDivider.Visibility = Visibility.Visible;
                    spBs2ProRgbSpeed.Visibility = Visibility.Visible;
                    break;
            }
        }

          /* ------------------------------------------------------------------ */
        /*  Bs2Pro: slider value text sync + color picker                     */
        /* ------------------------------------------------------------------ */

        private void Bs2ProRpmSlider_ValueChanged(object? sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (Bs2ProRpmValueText is null || nudBs2ProRpm is null)
                return;
            Bs2ProRpmValueText.Text = ((int)sdBs2ProRpm.Value).ToString();
            nudBs2ProRpm.Value = sdBs2ProRpm.Value;
        }

        private void Bs2ProBrightnessSlider_ValueChanged(object? sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (Bs2ProRgbBrightnessValueText is null)
                return;
            Bs2ProRgbBrightnessValueText.Text = ((int)sliderBs2ProBrightness.Value).ToString();
        }

        private void Bs2ProRgbSpeedSlider_ValueChanged(object? sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (Bs2ProRgbSpeedValueText is null)
                return;
            Bs2ProRgbSpeedValueText.Text = GetRgbSpeedNameFromSlider((int)sliderBs2ProRgbSpeed.Value);
        }

        private void Bs2ProRgbColorPicker_ColorChangedDelayed(object? sender, EventArgs e)
        {
            var color = Bs2ProRgbColorPicker.SelectedColor;
            nudBs2ProRgbR.Value = color.R;
            nudBs2ProRgbG.Value = color.G;
            nudBs2ProRgbB.Value = color.B;
        }

        /* ------------------------------------------------------------------ */
        /*  Bs2Pro: speed name mapping (same as Flydigi page)                  */
        /* ------------------------------------------------------------------ */

        private static string GetRgbSpeedNameFromSlider(int value) => value switch
        {
            0 => "Slow",
            1 => "Medium",
            2 => "Fast",
            _ => "Medium"
        };

        private static int GetRgbSpeedIndex(string? name) => name?.ToLowerInvariant() switch
        {
            "slow" => 0,
            "medium" => 1,
            "fast" => 2,
            _ => 1
        };

        #endregion

        #region EC Fan Helpers

        private static int GetEcFanPresetIndex(string? presetName) => presetName switch
        {
            EcFanPresetSilent => 0,
            EcFanPresetPerformance => 2,
            EcFanPresetFullSpeed => 3,
            EcFanPresetOff => 4,
            EcFanPresetCustom => 5,
            _ => 1 // Balanced
        };

        private static string GetEcFanPresetFromIndex(int index) => index switch
        {
            0 => EcFanPresetSilent,
            2 => EcFanPresetPerformance,
            3 => EcFanPresetFullSpeed,
            4 => EcFanPresetOff,
            5 => EcFanPresetCustom,
            _ => EcFanPresetBalanced
        };

        private static Uniwill_Control_Utility.Models.EcFanCurve GetEcFanPresetCurve(string? name) => name switch
        {
            EcFanPresetSilent => Uniwill_Control_Utility.Models.EcFanCurve.CreateSilent(),
            EcFanPresetBalanced => Uniwill_Control_Utility.Models.EcFanCurve.CreateBalanced(),
            EcFanPresetPerformance => Uniwill_Control_Utility.Models.EcFanCurve.CreatePerformance(),
            EcFanPresetFullSpeed => Uniwill_Control_Utility.Models.EcFanCurve.CreateFullSpeed(),
            EcFanPresetOff => Uniwill_Control_Utility.Models.EcFanCurve.CreateOff(),
            _ => Uniwill_Control_Utility.Models.EcFanCurve.CreateBalanced()
        };

        #endregion

        #region Keyboard RGB helpers

        private void cbxKbEnabled_Toggled(object sender, RoutedEventArgs e)
        {
            bool enabled = cbxKbEnabled.IsChecked ?? false;
            Settings.Default.AdaptiveKeyboardEnabled = enabled;
            Settings.Default.Save();

            // Only apply override when Adaptive Mode is running
            if (!start) return;

            if (enabled)
            {
                _deviceApplier?.EnableKeyboardOverride();
                // Reset tracking so the next update() tick force-applies
                lastKbPerKeyMode = false;
                lastKbBrightness = 0;
                lastKbEffectMode = "";
            }
            else
            {
                _deviceApplier?.DisableKeyboardOverride();
            }
        }

        private void cbxKbMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateKbModeUI();
        }

        private void sdKeyboard_Expanded(object sender, RoutedEventArgs e)
        {
            // When the card is expanded, ensure the effect controls match the
            // current mode selection. This fixes the issue where spKbEffectControls
            // stays Collapsed on first expand because Page_Loaded's deferred call
            // may have run before the ComboBox items were measured.
            UpdateKbModeUI();
        }

        private void cbxKbEffect_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateKbEffectUi();
        }

        private void KbDirectionButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Wpf.Ui.Controls.Button btn && btn.Tag is string tag)
                _kbDirection = tag;
            UpdateKbDirectionButtons();
        }

        private void UpdateKbDirectionButtons()
        {
            void SetActive(Wpf.Ui.Controls.Button btn, string tag)
            {
                btn.Appearance = _kbDirection == tag ? Wpf.Ui.Controls.ControlAppearance.Primary : Wpf.Ui.Controls.ControlAppearance.Transparent;
            }
            SetActive(btnKbDirLeftRight, "LeftRight");
            SetActive(btnKbDirRightLeft, "RightLeft");
            SetActive(btnKbDirDownUp, "DownUp");
            SetActive(btnKbDirUpDown, "UpDown");
            SetActive(btnKbDiagBRTL, "DiagonalBottomRightToTopLeft");
            SetActive(btnKbDiagBLTR, "DiagonalBottomLeftToTopRight");
        }

        private void UpdateKbModeUI()
        {
            if (spKbEffectControls == null || spKbPerKeyNote == null)
                return;

            bool perKey = cbxKbMode.SelectedIndex == 1;
            bool showEffects = !perKey;
            spKbEffectControls.Visibility = showEffects ? Visibility.Visible : Visibility.Collapsed;
            spKbPerKeyNote.Visibility = perKey ? Visibility.Visible : Visibility.Collapsed;
            sepKbPerKey.Visibility = perKey ? Visibility.Visible : Visibility.Collapsed;

            if (!perKey)
                UpdateKbEffectUi();
        }

        /// <summary>
        /// Shows/hides Color, Multi-Color, Rest color, and Speed controls based on the
        /// selected effect — mirroring the Keyboard page's UpdateEffectUi().
        /// </summary>
        private void UpdateKbEffectUi()
        {
            // Guard against calls before XAML fields are loaded (e.g. constructor, Page_Loaded).
            if (spKbColor == null || spKbMultiColor == null || spKbSpeed == null)
                return;

            if (cbxKbEffect.SelectedItem is not ComboBoxItem)
            {
                sepKbColor.Visibility = Visibility.Collapsed;
                spKbColor.Visibility = Visibility.Collapsed;
                spKbMultiColor.Visibility = Visibility.Collapsed;
                sepKbSpeed.Visibility = Visibility.Collapsed;
                spKbSpeed.Visibility = Visibility.Collapsed;
                sepKbDirection.Visibility = Visibility.Collapsed;
                grKbDirection.Visibility = Visibility.Collapsed;
                return;
            }

            string effect = GetKbEffectFromIndex(cbxKbEffect.SelectedIndex);

            // Rainbow has fixed colors — no color controls, no speed.
            if (ParseKbEffect(effect) == KeyboardEffect.Rainbow)
            {
                sepKbColor.Visibility = Visibility.Collapsed;
                spKbColor.Visibility = Visibility.Collapsed;
                spKbMultiColor.Visibility = Visibility.Collapsed;
                sepKbSpeed.Visibility = Visibility.Collapsed;
                spKbSpeed.Visibility = Visibility.Collapsed;
                sepKbDirection.Visibility = Visibility.Collapsed;
                grKbDirection.Visibility = Visibility.Collapsed;
                return;
            }

            if (Views.Pages.Keyboard.IsMultiColor7Effect(ParseKbEffect(effect)))
            {
                spKbColor.Visibility = Visibility.Collapsed;
                spKbMultiColor.Visibility = Visibility.Visible;
                tbKbMultiColorTitle.Text = "Effect Colors";
                tbKbMultiColorSubtitle.Text = "7 colors required for this effect. Click a swatch to edit.";
                KbMultiColorPicker.SetColors(KbMultiColorPicker.GetColors(), 7);
            }
            else if (Views.Pages.Keyboard.IsMultiColor4Effect(ParseKbEffect(effect)))
            {
                spKbColor.Visibility = Visibility.Collapsed;
                spKbMultiColor.Visibility = Visibility.Visible;
                tbKbMultiColorTitle.Text = "WASD & Arrow Keys";
                tbKbMultiColorSubtitle.Text = "4 colors for gaming keys. Click a swatch to edit.";
                KbMultiColorPicker.SetColors(KbMultiColorPicker.GetColors(), 4);
            }
            else
            {
                // Single-color effects (Static, Rainbow, etc.)
                spKbColor.Visibility = Visibility.Visible;
                spKbMultiColor.Visibility = Visibility.Collapsed;
            }

            // Speed: animated effects only (not Static, Rainbow, GamingMode)
            bool showSpeed = ParseKbEffect(effect) is KeyboardEffect effectEnum
                && effectEnum != KeyboardEffect.Static
                && effectEnum != KeyboardEffect.Rainbow
                && effectEnum != KeyboardEffect.GamingMode;
            sepKbSpeed.Visibility = showSpeed ? Visibility.Visible : Visibility.Collapsed;
            spKbSpeed.Visibility = showSpeed ? Visibility.Visible : Visibility.Collapsed;
            // Direction: Wave effect only (other effects use different byte flags to be probed later)
            bool showDirection = effectEnum == KeyboardEffect.Wave;
            sepKbDirection.Visibility = showDirection ? Visibility.Visible : Visibility.Collapsed;
            grKbDirection.Visibility = showDirection ? Visibility.Visible : Visibility.Collapsed;

            // sepKbColor: show if any color control (Color, MultiColor) is visible.
            bool anyColorVisible = spKbColor.Visibility == Visibility.Visible
                || spKbMultiColor.Visibility == Visibility.Visible;
            sepKbColor.Visibility = anyColorVisible ? Visibility.Visible : Visibility.Collapsed;

            UpdateKbDirectionButtons();
        }

        private void KbMultiColorPicker_ColorsChanged(object sender, EventArgs e)
        {
            // Multi-color colors changed — will be picked up by update() loop's diff tracking
        }

        private void KbColorPicker_ColorChangedDelayed(object sender, EventArgs e)
        {
            // Color changed — will be picked up by update() loop's diff tracking
        }

        /// <summary>
        /// Parses an effect name string to KeyboardEffect enum, matching the Adaptive page's index mapping.
        /// </summary>
        private static KeyboardEffect ParseKbEffect(string name) => name switch
        {
            "Static" => KeyboardEffect.Static,
            "Breathing" => KeyboardEffect.Breathing,
            "Wave" => KeyboardEffect.Wave,
            "Reactive" => KeyboardEffect.Random,
            "Random" => KeyboardEffect.Random,
            "Rainbow" => KeyboardEffect.Rainbow,
            "Ripple" => KeyboardEffect.Ripple,
            "TouchRipple" => KeyboardEffect.TouchRipple,
            "Marquee" => KeyboardEffect.Marquee,
            "Raindrop" => KeyboardEffect.Raindrop,
            "Aurora" => KeyboardEffect.Aurora,
            "TouchAurora" => KeyboardEffect.TouchAurora,
            "TouchSpark" => KeyboardEffect.TouchSpark,
            "Spark" => KeyboardEffect.Spark,
            "GamingMode" => KeyboardEffect.GamingMode,
            "Music" => KeyboardEffect.Static,
            _ => KeyboardEffect.Static,
        };

        private void sdKbBrightness_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            tbKbBrightnessValue.Text = ((int)sdKbBrightness.Value).ToString();
            // Force the next update() tick to detect the change.
            lastKbBrightness = int.MinValue;
        }

        private void sdKbSpeed_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            tbKbSpeedValue.Text = ((int)sdKbSpeed.Value).ToString();
            // Force the next update() tick to detect the change.
            lastKbEffectSpeed = byte.MaxValue;
        }

        private static int GetKbEffectIndex(string effectMode) => effectMode switch
        {
            "Static" => 0,
            "Breathing" => 1,
            "Wave" => 2,
            "Reactive" => 3,
            "Random" => 3,
            "Rainbow" => 4,
            "Ripple" => 5,
            "TouchRipple" => 6,
            "Marquee" => 7,
            "Raindrop" => 8,
            "Aurora" => 9,
            "TouchAurora" => 10,
            "TouchSpark" => 11,
            "Spark" => 12,
            "GamingMode" => 13,
            _ => 0
        };

        private static string GetKbEffectFromIndex(int index)
        {
            return index switch
            {
                0 => "Static",
                1 => "Breathing",
                2 => "Wave",
                3 => "Reactive",
                4 => "Rainbow",
                5 => "Ripple",
                6 => "TouchRipple",
                7 => "Marquee",
                8 => "Raindrop",
                9 => "Aurora",
                10 => "TouchAurora",
                11 => "TouchSpark",
                12 => "Spark",
                13 => "GamingMode",
                _ => "Static"
            };
        }

        private void btnKbEditPerKey_Click(object sender, RoutedEventArgs e)
        {
            // Load per-key colors: from field (which holds preset state or
            // a previous dialog edit), falling back to global keyboard settings.
            Dictionary<int, (byte R, byte G, byte B)> initialColors = new();
            if (!string.IsNullOrEmpty(_kbPerKeyColors))
            {
                initialColors = ParsePerKeyColors(_kbPerKeyColors);
            }
            else
            {
                var kbSettings = KeyboardSettingsService.Load();
                initialColors = kbSettings.GetPerKeyColors();
            }

            var dialog = new Views.Windows.KeyboardPerKeyDialog(initialColors);
            dialog.ShowDialog();

            // Always persist the colors back to the field so that:
            // 1) re-opening the dialog shows the edits made this session
            // 2) Page_Unloaded saves them (it reads from this field, not from the preset)
            if (dialog.ResultColors != null)
            {
                var sb = new System.Text.StringBuilder();
                var colors = dialog.ResultColors;
                for (int i = 0; i < 126; i++)
                {
                    if (i > 0) sb.Append('|');
                    (byte R, byte G, byte B) c = colors.TryGetValue(i, out var val) ? val : ((byte)0, (byte)0, (byte)0);
                    sb.Append($"{c.R},{c.G},{c.B}");
                }
                _kbPerKeyColors = sb.ToString();

                // Apply to device only when Adaptive Mode keyboard override is active
                // and the user confirmed with OK.
                if (dialog.Applied && _deviceApplier != null && _deviceApplier.IsKeyboardOverridden)
                {
                    int brightness = (int)sdKbBrightness.Value;

                    _deviceApplier.ApplyKeyboardFromPreset(
                        true, brightness, false, 0,
                        "Static", (byte)5, _kbDirection,
                        0, 0, 0, "", _kbPerKeyColors);

                    // Update tracking
                    lastKbPerKeyMode = true;
                    lastKbBrightness = brightness;
                    lastKbEffectMode = "Static";
                    lastKbEffectSpeed = 5;
                    lastKbDirection = _kbDirection;
                    lastKbColorR = 0;
                    lastKbColorG = 0;
                    lastKbColorB = 0;
                }
            }
        }

        private static Dictionary<int, (byte R, byte G, byte B)> ParsePerKeyColors(string data)
        {
            var result = new Dictionary<int, (byte R, byte G, byte B)>();
            var entries = data.Split('|', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < Math.Min(entries.Length, 126); i++)
            {
                var parts = entries[i].Split(',');
                if (parts.Length >= 3 &&
                    byte.TryParse(parts[0], out var r) &&
                    byte.TryParse(parts[1], out var g) &&
                    byte.TryParse(parts[2], out var b))
                {
                    result[i] = (r, g, b);
                }
            }
            return result;
        }

        #endregion

        #region Color serialization helpers

        private static string SerializeColors(System.Collections.Generic.List<Color> colors)
        {
            return string.Join(",", colors.Select(c => $"#{c.R:X2}{c.G:X2}{c.B:X2}"));
        }

        private static System.Collections.Generic.List<Color> ParseColorString(string data)
        {
            var result = new System.Collections.Generic.List<Color>();
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
                catch (FormatException) { /* Invalid hex color entry skipped */ }
            }
            return result;
        }

        #endregion

        #region CPU / GPU / Light Bar event handlers

        /// <summary>
        /// Maps the Light Bar ComboBox index to the HID effect byte value.
        /// Static=0x01, Breathing=0x02, Wave=0x03, Loading=0x04, Marquee=0x05, Meteor=0x06.
        /// </summary>
        private static byte GetLbEffectByte(int selectedIndex)
        {
            return (byte)(selectedIndex + 1);
        }

        /// <summary>
        /// Converts the UI speed value (0-11, slow?fast) to HID speed (0-11, fast?slow).
        /// </summary>
        private static byte GetLbHidSpeed(int uiSpeed)
        {
            return (byte)(11 - uiSpeed);
        }

        /// <summary>
        /// Shows/hide AMD vs Intel CPU controls based on the detected CPU type.
        /// </summary>
        private void UpdateAdpCpuUI()
        {
            // Apply voltage control visibility from global setting
            bool voltageEnabled = AdpCpuVoltageEnabled;

            if (Family.TYPE == Family.ProcessorType.Intel)
            {
                // Hide AMD-specific controls
                AdpGridSustained.Visibility = Visibility.Collapsed;
                AdpDividerAfterSustained.Visibility = Visibility.Collapsed;
                AdpGridBoost.Visibility = Visibility.Collapsed;
                AdpDividerAfterBoost.Visibility = Visibility.Collapsed;
                AdpGridTjMax.Visibility = Visibility.Collapsed;
                AdpCoControlsPanel.Visibility = Visibility.Collapsed;
                // Intel undervolt visibility gated by voltage control setting
                AdpGridIntelUndervolt.Visibility = voltageEnabled ? Visibility.Visible : Visibility.Collapsed;
                AdpDividerAfterIntelPl2.Visibility = voltageEnabled ? Visibility.Visible : Visibility.Collapsed;
            }
            else
            {
                // Hide Intel-specific controls
                AdpGridIntelPl1.Visibility = Visibility.Collapsed;
                AdpDividerAfterIntelPl1.Visibility = Visibility.Collapsed;
                AdpGridIntelPl2.Visibility = Visibility.Collapsed;
                AdpDividerAfterIntelPl2.Visibility = Visibility.Collapsed;
                AdpGridIntelUndervolt.Visibility = Visibility.Collapsed;

                // AMD CO panel visibility gated by voltage control setting.
                AdpCoControlsPanel.Visibility = voltageEnabled ? Visibility.Visible : Visibility.Collapsed;
                updateAdpCoVisibility();
            }
        }

        /// <summary>
        /// Shared ValueChanged handler for all Adaptive sliders.
        /// Updates the corresponding label text based on the sender.
        /// </summary>
        private void AdpSlider_ValueChanged(object? sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (sender is not Slider slider)
                return;

            switch (slider.Name)
            {
                case "AdpSdSustained":
                    AdpLblSustained.Text = $"{slider.Value:F0} W";
                    break;
                case "AdpSdBoost":
                    AdpLblBoost.Text = $"{slider.Value:F0} W";
                    break;
                case "AdpSdTjMax":
                    AdpLblTjMax.Text = $"{slider.Value:F0} °C";
                    break;
                case "AdpSdCoUnified":
                    AdpLblCoUnified.Text = $"{slider.Value:+0;-0;0}";
                    break;
                case "AdpSdCoCcd0":
                    AdpLblCoCcd0.Text = $"{slider.Value:+0;-0;0}";
                    break;
                case "AdpSdCoCcd1":
                    AdpLblCoCcd1.Text = $"{slider.Value:+0;-0;0}";
                    break;
                case "AdpSdIntelPl1":
                    AdpLblIntelPl1.Text = $"{slider.Value:F0} W";
                    break;
                case "AdpSdIntelPl2":
                    AdpLblIntelPl2.Text = $"{slider.Value:F0} W";
                    break;
                case "AdpSdIntelUndervolt":
                    AdpLblIntelUndervolt.Text = $"{slider.Value:F0} mV";
                    break;
                case "AdpSdNvPower":
                    AdpLblNvPower.Text = $"{slider.Value:F0} W";
                    break;
                case "AdpSdNvDbOffset":
                    AdpLblNvDbOffset.Text = $"{slider.Value:F0} W";
                    break;
                case "AdpSdNvCore":
                    _adpUserTouchedCoreSlider = true;
                    updateAdpCoreLabel();
                    break;
                case "AdpSdNvMem":
                    AdpLblNvMem.Text = $"{slider.Value:F0} MHz";
                    break;
            }
        }

        private void cbxCpuEnabled_Toggled(object sender, RoutedEventArgs e)
        {
            bool enabled = cbxCpuEnabled.IsChecked ?? false;
            Settings.Default.AdaptiveCpuEnabled = enabled;
            Settings.Default.Save();

            // Only reset tracking when Adaptive Mode is running
            if (!start) return;

            if (enabled)
            {
                _deviceApplier?.EnableCpuGpuOverride();
                // Reset tracking so the next update() tick force-applies CPU settings
                lastCpuSustained = 0;
                lastCpuBoost = 0;
                lastCpuTjMax = 0;
                lastCpuCoUnified = true;
                lastCpuCoUnifiedValue = 0;
                lastCpuCoCcd0 = 0;
                lastCpuCoCcd1 = 0;
                lastCpuPl1 = 0;
                lastCpuPl2 = 0;
                lastCpuUndervolt = 0;
            }
            else
            {
                _ = _deviceApplier?.DisableCpuGpuOverrideAsync();
            }
        }

        private void AdpRbCoMode_Click(object sender, RoutedEventArgs e) => updateAdpCoVisibility();

        private void updateAdpCoVisibility()
        {
            bool unified = AdpRbCoUnified.IsChecked == true;
            if (unified)
            {
                AdpDividerCoPerCcd.Visibility = Visibility.Collapsed;
                AdpGridCoUnified.Visibility = Visibility.Visible;
                AdpGridCoCcd0.Visibility = Visibility.Collapsed;
                AdpGridCoCcd1.Visibility = Visibility.Collapsed;
                AdpDividerCoAfterCcd1.Visibility = Visibility.Collapsed;
            }
            else
            {
                AdpDividerCoPerCcd.Visibility = Visibility.Visible;
                AdpGridCoUnified.Visibility = Visibility.Collapsed;
                AdpGridCoCcd0.Visibility = Visibility.Visible;
                AdpGridCoCcd1.Visibility = Visibility.Visible;
                AdpDividerCoAfterCcd1.Visibility = Visibility.Visible;
            }
        }

        private void cbxGpuEnabled_Toggled(object sender, RoutedEventArgs e)
        {
            bool enabled = cbxGpuEnabled.IsChecked ?? false;
            Settings.Default.AdaptiveGpuEnabled = enabled;
            Settings.Default.Save();

            // Only reset tracking when Adaptive Mode is running
            if (!start) return;

            if (enabled)
            {
                _deviceApplier?.EnableCpuGpuOverride();
                // Reset tracking so the next update() tick force-applies GPU settings
                lastGpuTgp = 0;
                lastGpuDynamicBoost = false;
                lastGpuDbOffset = 0;
                lastGpuCoreClock = 0;
                lastGpuMemClock = 0;
                lastGpuVfCurveCount = -1;
            }
            else
            {
                _ = _deviceApplier?.DisableCpuGpuOverrideAsync();
            }
        }

        private void cbxLbEnabled_Toggled(object sender, RoutedEventArgs e)
        {
            // Prevent enabling if Light Bar navigation is disabled in Settings.
            if ((cbxLbEnabled.IsChecked ?? false) && !Settings.Default.EnableLightBarNavigation)
            {
                cbxLbEnabled.IsChecked = false;
                return;
            }

            bool enabled = cbxLbEnabled.IsChecked ?? false;
            Settings.Default.AdaptiveLbEnabled = enabled;
            Settings.Default.Save();

            // Only apply override when Adaptive Mode is running
            if (!start) return;

            if (enabled)
            {
                _deviceApplier?.EnableLightBarOverride();
                // Reset tracking so the next update() tick force-applies Light Bar settings
                lastLbPower = false;
                lastLbColorR = 0;
                lastLbColorG = 0;
                lastLbColorB = 0;
                lastLbBrightness = 0;
                lastLbEffect = 0;
                lastLbSpeed = 0;
            }
            else
            {
                _deviceApplier?.DisableLightBarOverride();
            }
        }

        private void AdpTogDynamicBoost_Checked(object sender, RoutedEventArgs e)
        {
            if (AdpSpNvDbOffset == null) return;
            AdpSpNvDbOffset.Visibility = Visibility.Visible;
        }

        private void AdpTogDynamicBoost_Unchecked(object sender, RoutedEventArgs e)
        {
            if (AdpSpNvDbOffset == null) return;
            AdpSpNvDbOffset.Visibility = Visibility.Collapsed;
        }

        private void AdpBtnVfCurve_Click(object sender, RoutedEventArgs e)
        {
            // Reuse existing window if already open
            if (_vfEditorWindow != null)
            {
                _vfEditorWindow.Activate();
                return;
            }

            // Load per-game curve offsets
            var offsets = _adpVfCurveOffsets.Count > 0 ? new Dictionary<int, int>(_adpVfCurveOffsets) : null;

            // Open non-modal curve editor window
            _vfEditorWindow = new Views.Windows.VfCurveEditorWindow(offsets);
            _vfEditorWindow.Owner = Application.Current.MainWindow;
            _vfEditorWindow.Closed += (_, _) =>
            {
                // Capture current offsets from editor back to per-game profile before closing
                _adpVfCurveOffsets = new Dictionary<int, int>(_vfEditorWindow!.GetOffsets());
                _vfEditorWindow = null;
                _adpUserTouchedCoreSlider = false;
                updateAdpCoreLabel();
            };
            _vfEditorWindow.CurveChanged += (_, _) =>
            {
                // Capture live offsets from editor to per-game profile
                _adpVfCurveOffsets = new Dictionary<int, int>(_vfEditorWindow!.GetOffsets());
                _adpUserTouchedCoreSlider = false; // User went back to curve — reclaim from slider
                updateAdpCoreLabel();
                // Only apply to hardware if Adaptive Mode is running AND GPU Settings is enabled
                if (start && (cbxGpuEnabled.IsChecked ?? false))
                    ApplyActiveVfCurveToGpu();
            };
            _vfEditorWindow.Show();
        }

        private void AdpTsLightBarPower_Toggled(object sender, RoutedEventArgs e)
        {
            // Only apply when Adaptive Mode is running AND Light Bar override is enabled.
            if (!start || !(cbxLbEnabled.IsChecked ?? false))
                return;

            bool isOn = AdpTsLightBarPower.IsChecked == true;
            if (_hidService == null)
                return;

            byte r = AdpLbColorPicker.SelectedColor.R;
            byte g = AdpLbColorPicker.SelectedColor.G;
            byte b = AdpLbColorPicker.SelectedColor.B;
            int brightness = (int)AdpLbBrightnessSlider.Value;
            byte effect = GetLbEffectByte(AdpLbEffect.SelectedIndex);
            byte speed = GetLbHidSpeed((int)AdpLbSpeedSlider.Value);

            try
            {
                if (isOn)
                {
                    _hidService.LightBarTurnOn(r, g, b, brightness);
                    _hidService.LightBarSetEffect(effect, speed, brightness);
                }
                else
                {
                    _hidService.LightBarTurnOff();
                }
            }
            catch (Exception ex)
            {
                DiagnosticLogger.LogError(ex, "Failed to toggle Light Bar power in Adaptive Mode");
            }
        }

        private void AdpLbColorPicker_ColorChangedDelayed(object sender, EventArgs e)
        {
            ApplyLbOverrideImmediate();
        }

        private void AdpLbBrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (AdpLbBrightnessValueText != null)
                AdpLbBrightnessValueText.Text = ((int)AdpLbBrightnessSlider.Value).ToString();

            ApplyLbOverrideImmediate();
        }

        private void AdpLbEffect_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Guard against calls before XAML fields are loaded (e.g. constructor, Page_Loaded).
            if (AdpSpLbSpeed == null || AdpLbEffect == null)
                return;

            // Show/hide speed control (hidden for Static = index 0)
            AdpSpLbSpeed.Visibility = AdpLbEffect.SelectedIndex != 0 ? Visibility.Visible : Visibility.Collapsed;

            ApplyLbOverrideImmediate();
        }

        private void AdpLbSpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (AdpLbSpeedValueText != null)
                AdpLbSpeedValueText.Text = ((int)AdpLbSpeedSlider.Value).ToString();

            ApplyLbOverrideImmediate();
        }

        /// <summary>
        /// Immediately applies the current Light Bar override settings to the HID device.
        /// Only applies when Adaptive Mode is running AND Light Bar override is enabled.
        /// Resets the relevant last* tracking variables so update() doesn't re-apply.
        /// </summary>
        private void ApplyLbOverrideImmediate()
        {
            // Only apply when Adaptive Mode is running AND Light Bar override is enabled.
            if (!start || !(cbxLbEnabled.IsChecked ?? false))
                return;

            if (_hidService == null)
                return;

            if (!(AdpTsLightBarPower.IsChecked ?? true))
                return;

            var color = AdpLbColorPicker.SelectedColor;
            int brightness = (int)AdpLbBrightnessSlider.Value;
            byte effect = GetLbEffectByte(AdpLbEffect.SelectedIndex);
            byte speed = GetLbHidSpeed((int)AdpLbSpeedSlider.Value);

            _ = Task.Run(() =>
            {
                try
                {
                    Thread.Sleep(50);
                    _hidService?.LightBarSetColor(color.R, color.G, color.B, brightness);
                    _hidService?.LightBarSetEffect(effect, speed, brightness);

                    // Reset tracking variables so update() doesn't duplicate the write
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        lastLbColorR = color.R;
                        lastLbColorG = color.G;
                        lastLbColorB = color.B;
                        lastLbBrightness = brightness;
                        lastLbEffect = effect;
                        lastLbSpeed = speed;
                    });
                }
                catch (Exception ex)
                {
                    DiagnosticLogger.LogError(ex, "Failed to apply Light Bar override settings immediately");
                }
            });
        }

        #endregion

        #region Per-game profile helpers (gaps)

        /// <summary>True when the currently loaded per-game profile has a V-F curve with changes.</summary>
        private bool AdpVfCurveActive => _adpVfCurveOffsets.Count > 0;

        /// <summary>Updates the core clock label to show "Curve" when active, or slider value otherwise.</summary>
        private void updateAdpCoreLabel()
        {
            AdpLblNvCore.Text = (AdpVfCurveActive && !_adpUserTouchedCoreSlider)
                ? "Curve"
                : $"{AdpSdNvCore.Value:F0} MHz";
        }

        /// <summary>Applies the active per-game V-F curve to the GPU hardware. Used by Adaptive page (no Apply button).</summary>
        private void ApplyActiveVfCurveToGpu()
        {
            try
            {
                if (_adpVfCurveOffsets.Count == 0)
                    return;

                // Apply EC power limit BEFORE the curve write (same as Presets page).
                // The NVIDIA driver on Blackwell laptops may check the EC power ceiling
                // when applying VF offsets and further reduce power if it sees a low ceiling.
                int tgp = (int)AdpSdNvPower.Value;
                bool dbEnabled = AdpTogDynamicBoost.IsChecked == true;
                int dbOffset = dbEnabled ? (int)AdpSdNvDbOffset.Value : 0;
                NvTuning.SetPowerLimit(tgp + dbOffset, dbOffset);

                NvTuning.ApplyVfCurveOffsets_ClockBoostTable(_adpVfCurveOffsets);

                // Re-apply power limit after in case the driver overrides the EC ceiling.
                NvTuning.SetPowerLimit(tgp + dbOffset, dbOffset);
            }
            catch (Exception ex)
            {
                DiagnosticLogger.LogError(ex, "Failed to apply V-F curve to GPU in Adaptive Mode");
            }
        }

        /// <summary>Gap 1: Restores EC Fan custom duties from per-game profile into FanControlSettings.</summary>
        private static void ApplyEcFanCustomDuties(string unifiedPreset, int[]? unifiedDuties,
            string cpuPreset, int[]? cpuDuties,
            string gpuPreset, int[]? gpuDuties)
        {
            try
            {
                var settings = FanControlSettingsService.Load();

                if (unifiedPreset == EcFanPresetCustom && unifiedDuties != null)
                    settings.UnifiedDuties = unifiedDuties.ToArray();

                if (cpuPreset == EcFanPresetCustom && cpuDuties != null)
                    settings.CpuDuties = cpuDuties.ToArray();

                if (gpuPreset == EcFanPresetCustom && gpuDuties != null)
                    settings.GpuDuties = gpuDuties.ToArray();

                FanControlSettingsService.Save(settings);
            }
            catch (Exception ex)
            {
                DiagnosticLogger.LogError(ex, "Failed to apply EC Fan custom duties from preset");
            }
        }

        /// <summary>Gap 3: Serializes the current Flydigi custom curve points to JSON for per-game save.</summary>
        private string GetBs2ProCurvePointsJson()
        {
            try
            {
                // Only save custom curve points when Custom is selected
                if (GetBs2ProCurveProfileId(cbxBs2ProCurve.SelectedIndex) != CurveProfileCustom)
                    return string.Empty;

                if (Bs2ProCurveEditor is not null)
                {
                    var curve = Bs2ProCurveEditor.GetCurve();
                    return curve.ToJSON();
                }
            }
            catch (Exception ex)
            {
                DiagnosticLogger.LogError(ex, "Failed to get Flydigi custom curve points");
            }
            return string.Empty;
        }

        /// <summary>Gap 3: Restores Flydigi custom curve points from per-game profile into the editor.</summary>
        private void ApplyBs2ProCurvePoints(string curvePointsJson)
        {
            try
            {
                if (string.IsNullOrEmpty(curvePointsJson))
                    return;

                var profile = FlydigiFanCurveProfile.FromJSON(curvePointsJson);
                if (Bs2ProCurveEditor is not null)
                    Bs2ProCurveEditor.SetCurve(profile);
            }
            catch (Exception ex)
            {
                DiagnosticLogger.LogError(ex, "Failed to apply Flydigi custom curve points from preset");
            }
        }

     #endregion

        private static uint BuildCoperArg(int ccd, int core, int offset20)
        {
            return ((uint)((ccd << 8) | core) << 20) | (uint)offset20;
        }

        /// <summary>
        /// Resets all diff-tracking variables so the next update() tick will
        /// force-apply everything. Call after system resume from sleep/hibernation
        /// because the hardware reverts to BIOS defaults while our cached values
        /// still believe they're applied.
        /// </summary>
        public void InvalidateAllLastValues()
        {
            ResetGpuLastValues();
            // Reset CPU last values
            lastCpuSustained = 0;
            lastCpuBoost = 0;
            lastCpuTjMax = 0;
            lastCpuCoUnified = true;
            lastCpuCoUnifiedValue = 0;
            lastCpuCoCcd0 = 0;
            lastCpuCoCcd1 = 0;
            lastCpuPl1 = 0;
            lastCpuPl2 = 0;
            lastCpuUndervolt = 0;
            // Reset watercooler last values
            lastWcPump = PumpVoltage.Off;
            lastWcFan = FanSpeed.Off;
            lastWcRgbMode = RgbState.Off;
            lastWcRgbColor = RgbColor.Red;
            // Reset BS2 Pro last values
            lastBs2ProFanModeText = string.Empty;
            lastBs2ProGear = null;
            lastBs2ProRpm = null;
            lastBs2ProRgbMode = string.Empty;
            lastBs2ProRgbR = 0;
            lastBs2ProRgbG = 0;
            lastBs2ProRgbB = 0;
            lastBs2ProBrightness = 0;
            lastBs2ProSpeed = "Medium";
            // Reset keyboard last values
            lastKbPerKeyMode = false;
            lastKbBrightness = 0;
            lastKbEffectMode = string.Empty;
            lastKbEffectSpeed = 0;
            lastKbColorR = 0;
            lastKbColorG = 0;
            lastKbColorB = 0;
            lastKbMultiColors = string.Empty;
            lastKbDirection = string.Empty;
            // Reset light bar last values
            lastLbPower = false;
            lastLbColorR = 0;
            lastLbColorG = 0;
            lastLbColorB = 0;
            lastLbBrightness = 0;
            lastLbEffect = 0;
            lastLbSpeed = 0;
            // Reset EC fan last values
            lastEcFanUnifiedMode = false;
            lastEcFanPreset = string.Empty;
            lastEcFanCpuPreset = string.Empty;
            lastEcFanGpuPreset = string.Empty;
        }

        private void ResetGpuLastValues()
        {
            lastGpuTgp = 0;
            lastGpuDynamicBoost = false;
            lastGpuDbOffset = 0;
            lastGpuCoreClock = 0;
            lastGpuMemClock = 0;
            lastGpuVfCurveCount = -1;
        }
    }
}
