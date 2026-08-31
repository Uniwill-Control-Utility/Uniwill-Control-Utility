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
using Universal_x86_Tuning_Utility.Properties;
using Universal_x86_Tuning_Utility.Scripts;
using Universal_x86_Tuning_Utility.Models;
using Universal_x86_Tuning_Utility.Scripts.Misc;
using Universal_x86_Tuning_Utility.Services;
using Universal_x86_Tuning_Utility.Scripts.GPUs.NVIDIA;
using GameLib.Plugin.Steam.Model;
using Windows.Gaming.Preview.GamesEnumeration;
using System.Management;
using System.ComponentModel;
using static Universal_x86_Tuning_Utility.Scripts.Game_Manager;

namespace Universal_x86_Tuning_Utility.Views.Pages
{
    public partial class Adaptive : Page
    {
        System.Windows.Threading.DispatcherTimer adaptiveMode = new System.Windows.Threading.DispatcherTimer();
        System.Windows.Threading.DispatcherTimer sensors = new System.Windows.Threading.DispatcherTimer();
        private static int coreCount = 0;
        private readonly GpuInventoryService gpuInventory;
        private int radeonGpuCount;
        private int nvidiaGpuCount;

        public Adaptive(GpuInventoryService gpuInventory)
        {
            this.gpuInventory = gpuInventory;
            InitializeComponent();

            _ = Tablet.TabletDevices;
            setUp();

            adaptiveMode.Interval = TimeSpan.FromSeconds(2);
            adaptiveMode.Tick += new EventHandler(adaptive_Tick);
            adaptiveMode.Start();

            sensors.Interval = TimeSpan.FromSeconds(2);
            sensors.Tick += new EventHandler(sensors_Tick);
            sensors.Start();

            nudPolling.Value = Settings.Default.polling;

            cbAutoSwitch.IsChecked = Settings.Default.autoSwitch;

            // Initialize override checkboxes from persisted settings so they reflect
            // the state from the previous session. This must happen before setUp()
            // calls ToggleAdaptiveMode(), otherwise the checkboxes stay at their
            // XAML default (false) and get captured by loadPreset as "wasEnabled=false".
            cbxBs2ProEnabled.IsChecked = Settings.Default.AdaptiveBs2ProEnabled;
            cbxWcEnabled.IsChecked = Settings.Default.AdaptiveWcEnabled;
            cbxKbEnabled.IsChecked = Settings.Default.AdaptiveKeyboardEnabled;
            cbxCpuEnabled.IsChecked = Settings.Default.AdaptiveCpuEnabled;
            cbxGpuEnabled.IsChecked = Settings.Default.AdaptiveGpuEnabled;
            cbxLbEnabled.IsChecked = Settings.Default.AdaptiveLbEnabled;

            sdHydroUI.Visibility = WaterCoolerHardwareDetector.IsSupportedHardware() ? Visibility.Visible : Visibility.Collapsed;
            UpdateFlydigiCardVisibility();

            // Show/hide AMD vs Intel CPU controls
            UpdateAdpCpuUI();

            // Show/hide GPU card based on NVIDIA availability
            bool hasNvidia = !string.IsNullOrEmpty(NvTuning.GetGpuName());
            sdGpuSettings.Visibility = hasNvidia ? Visibility.Visible : Visibility.Collapsed;

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
                    catch { /* not available */ }
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
                    });
                }
                catch { /* HID not available */ }
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
                // BS1 supports Curve (Auto) via app-localized Bs1SmartControl — keep all 3 fan modes.
                // BS1 max RPM is 3000
                nudBs2ProRpm.Maximum = 3000;
                sdBs2ProRpm.Maximum = 3000;
                if (nudBs2ProRpm.Value > 3000)
                    nudBs2ProRpm.Value = 3000;
            }
            else
            {
                nudBs2ProRpm.Maximum = 4000;
                sdBs2ProRpm.Maximum = 4000;
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

        private static AdaptivePresetManager adaptivePresetManager = new AdaptivePresetManager(Settings.Default.Path + "adaptivePresets.json");
        private static DeviceApplier? _deviceApplier;
        private static FlydigiSmartControl? _bs2ProSmartControl;
        private static FlydigiTemperatureProvider? _bs2ProTempProvider;
        private static Bs1SmartControl? _bs1SmartControl;
        private async void setUp()
        {
            try
            {
                // Initialize DeviceApplier for centralized device commands
                _deviceApplier = App.GetService<DeviceApplier>();
                GpuInventorySnapshot inventory = await gpuInventory.GetSnapshotAsync();
                radeonGpuCount = inventory.RadeonCount;
                nvidiaGpuCount = inventory.NvidiaCount;

                tsAutoSwitch.IsChecked = true;

                await Task.Run(() => Game_Manager.installedGames = Game_Manager.syncGame_Library(true));

                cbxPowerPreset.Items.Add("Default");
                foreach (GameLauncherItem item in Game_Manager.installedGames) cbxPowerPreset.Items.Add(item.gameName);

                cbxPowerPreset.SelectedIndex = 0;

                IEnumerable<string> presetNames = adaptivePresetManager.GetPresetNames();

                foreach (GameLauncherItem item in Game_Manager.installedGames)
                {
                    bool containsName = false;

                    foreach (string names in presetNames)
                    {
                        if (names.Contains(item.gameName)) containsName = true;
                    }

                    if (containsName == false)
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
                            isAutoSwitch = (bool)tsAutoSwitch.IsChecked,
                            // CPU defaults
                            CpuSustained = 75,
                            CpuBoost = 85,
                            CpuTjMax = 95,
                            CpuCo = 0,
                            CpuPl1 = 45,
                            CpuPl2 = 60,
                            CpuUndervolt = 0,
                            // GPU defaults
                            GpuTgp = 150,
                            GpuDynamicBoost = false,
                            GpuDbOffset = 5,
                            GpuCoreClock = 0,
                            GpuMemClock = 0,
                            GpuVfCurveName = string.Empty,
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

                foreach (var item in new System.Management.ManagementObjectSearcher("Select * from Win32_Processor").Get()) coreCount += int.Parse(item["NumberOfCores"].ToString());

                btnStart.IsEnabled = true;
                btnSave.IsEnabled = true;

                if (Settings.Default.isStartAdpative) ToggleAdaptiveMode();
            }
            catch (Exception ex)
            {
                DiagnosticLogger.LogError(ex, "Failed during adaptive mode setup");
            }
        }

        [DllImport("user32.dll")]
        static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

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

        delegate bool WndEnumProc(IntPtr hWnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        struct LASTINPUTINFO
        {
            public uint cbSize;
            public uint dwTime;
        }

        private const string DefaultProfileName = "Default";

        // Guard flag to prevent cbxPowerPreset_SelectionChanged from calling loadPreset
        // when getRunningGame already called it directly with resetTracking: false.
        private bool _isUpdatingPresetSelection;

        // Window classes that are NOT games (browsers, notifications, taskbar, etc.)
        private static readonly HashSet<string> ExcludedWindowClasses = new(StringComparer.OrdinalIgnoreCase)
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

        bool start = false;
        private void btnStart_Click(object sender, RoutedEventArgs e)
        {
            ToggleAdaptiveMode();
        }

        private async void ToggleAdaptiveMode()
        {
            try
            {
                if (start)
                {
                    start = false;
                    siStartIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.Play20;
                    tbxStartText.Text = LocalizationService.Get("Start Adaptive Mode");
                    GetSensor.CloseSensor();
                    Settings.Default.isAdaptiveModeRunning = false;
                    Settings.Default.isStartAdpative = false;
                    Settings.Default.Save();

                    // Stop smart control when Adaptive Mode stops
                    StopBs2ProSmartControl();
                    StopBs1SmartControl();

                    // Lift DeviceApplier overrides when Adaptive Mode stops so that
                    // the Flydigi/Watercooler pages regain control and hide their overlays.
                    if (_deviceApplier != null)
                    {
                        _ = _deviceApplier.DisableFlydigiOverrideAsync();
                        _ = _deviceApplier.DisableWatercoolerOverrideAsync();
                        _deviceApplier.DisableEcFanOverride();
                        _deviceApplier.DisableKeyboardOverride();
                    }

                }
                else
                {
                    start = true;
                    siStartIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.Stop20;
                    tbxStartText.Text = LocalizationService.Get("Stop Adaptive Mode");
                    await Task.Run(() => GetSensor.OpenSensor());
                    Settings.Default.isAdaptiveModeRunning = true;
                    Settings.Default.isStartAdpative = true;
                    Settings.Default.Save();

                    // Sync DeviceApplier override state to the current checkbox values
                    // when Adaptive Mode starts. This ensures the override state matches
                    // what the user has configured in the active profile.
                    if (_deviceApplier != null)
                    {
                        if ((bool)cbxBs2ProEnabled.IsChecked && !_deviceApplier.IsFlydigiOverridden)
                            _deviceApplier.EnableFlydigiOverride();
                        if ((bool)cbxWcEnabled.IsChecked && !_deviceApplier.IsWatercoolerOverridden)
                            _deviceApplier.EnableWatercoolerOverride();
                        if ((bool)cbxEcFanEnabled.IsChecked && !_deviceApplier.IsEcFanOverridden)
                            _deviceApplier.EnableEcFanOverride();
                        if ((bool)cbxKbEnabled.IsChecked && !_deviceApplier.IsKeyboardOverridden)
                            _deviceApplier.EnableKeyboardOverride();
                    }
                }
            }
            catch (Exception ex)
            {
                DiagnosticLogger.LogError(ex, "Failed to toggle adaptive mode");
            }
        }

        public static int CPUTemp, CPULoad, CPUClock, CPUPower, GPULoad, GPUClock, GPUMemClock;

        private void mainScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (IsScrollBarVisible(mainScroll)) mainCon.Margin = new Thickness(0, 0, -12, 0);
            else mainCon.Margin = new Thickness(0, 0, 0, 0);
        }

        private async void adaptive_Tick(object sender, EventArgs e)
        {
            if (start == true)
            {
                update();
            }
            if (Settings.Default.polling != nudPolling.Value)
            {
                Settings.Default.polling = (double)nudPolling.Value;
                Settings.Default.Save();
            }

            if (adaptiveMode.Interval != TimeSpan.FromSeconds((double)nudPolling.Value))
            {
                adaptiveMode.Stop();
                adaptiveMode.Interval = TimeSpan.FromSeconds((double)nudPolling.Value);
                adaptiveMode.Start();
            }
            if (sensors.Interval != TimeSpan.FromSeconds((double)nudPolling.Value))
            {
                sensors.Stop();
                sensors.Interval = TimeSpan.FromSeconds((double)nudPolling.Value);
                sensors.Start();
            }
        }

        private void cbxPowerPreset_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Skip if getRunningGame already called loadPreset directly.
            if (_isUpdatingPresetSelection)
                return;

            string presetName = (sender as ComboBox).SelectedItem as string;
            loadPreset(presetName);
        }

        private void loadPreset(string presetName)
        {
            loadPreset(presetName, resetTracking: true);
        }

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
                AdaptivePreset myPreset = adaptivePresetManager.GetPreset(presetName);

                if (myPreset != null)
                {
                    tsAutoSwitch.IsChecked = myPreset.isAutoSwitch;

                    // Watercooler
                    cbxWcPumpVoltage.SelectedIndex = GetPumpVoltageIndex(myPreset.WcPumpVoltage);
                    cbxWcFanSpeed.SelectedIndex = GetFanSpeedIndex(myPreset.WcFanSpeed);
                    if (Enum.TryParse<RgbState>(myPreset.WcRgbMode, true, out var pvLoad))
                    {
                        cbxWcRgbMode.SelectedIndex = GetRgbModeIndex(pvLoad);
                        spWcRgbColor.Visibility = pvLoad switch
                        {
                            RgbState.Static or RgbState.Breathe => Visibility.Visible,
                            _ => Visibility.Collapsed
                        };
                        WcRgbColorDivider.Visibility = spWcRgbColor.Visibility;
                    }
                    if (Enum.TryParse<RgbColor>(myPreset.WcRgbColor, true, out var rcLoad))
                        cbxWcRgbColor.SelectedIndex = GetRgbColorIndex(rcLoad);

                    // BS2 Pro — guard: CardExpander content is null until expanded
                    if (cbxBs2ProFanMode is not null)
                    {
                        cbxBs2ProFanMode.SelectedIndex = GetBs2ProFanModeIndex(myPreset.Bs2ProFanMode);
                        UpdateBs2ProModeUI();
                        cbxBs2ProGear.SelectedIndex = Math.Clamp(myPreset.Bs2ProGear - 1, 0, 3);
                        nudBs2ProRpm.Value = Math.Clamp((int)myPreset.Bs2ProRpm, 1300, 4000);
                        cbxBs2ProCurve.SelectedIndex = GetBs2ProCurveIndex(myPreset.Bs2ProCurveProfileId);

                        // Gap 3: Restore Flydigi custom curve points per-game
                        ApplyBs2ProCurvePoints(myPreset.Bs2ProCurvePointsJson);

                        cbxBs2ProRgbMode.SelectedIndex = GetBs2ProRgbModeIndex(myPreset.Bs2ProRgbMode);
                        UpdateBs2ProRgbUI();
                        nudBs2ProRgbR.Value = myPreset.Bs2ProRgbR;
                        nudBs2ProRgbG.Value = myPreset.Bs2ProRgbG;
                        nudBs2ProRgbB.Value = myPreset.Bs2ProRgbB;
                        Bs2ProRgbColorPicker.SelectedColor = Color.FromRgb(myPreset.Bs2ProRgbR, myPreset.Bs2ProRgbG, myPreset.Bs2ProRgbB);
                        sliderBs2ProBrightness.Value = myPreset.Bs2ProBrightness;
                        Bs2ProRgbBrightnessValueText.Text = myPreset.Bs2ProBrightness.ToString();
                        // Gap 2: Restore Flydigi RGB speed per-game
                        if (sliderBs2ProRgbSpeed != null)
                            sliderBs2ProRgbSpeed.Value = myPreset.Bs2ProRgbSpeed;
                        int rpm = Math.Clamp((int)myPreset.Bs2ProRpm, 1300, 4000);
                        Bs2ProRpmValueText.Text = rpm.ToString();
                        sdBs2ProRpm.Value = rpm;
                    }

                    // EC Fan
                    if (sdEcFan.Visibility == Visibility.Visible)
                    {
                        cbxEcFanEnabled.IsChecked = myPreset.EcFanEnabled;
                        cbxEcFanMode.SelectedIndex = myPreset.EcFanUnifiedMode ? 0 : 1;
                        cbxEcFanPreset.SelectedIndex = GetEcFanPresetIndex(myPreset.EcFanPreset);
                        cbxEcFanCpuPreset.SelectedIndex = GetEcFanPresetIndex(myPreset.EcFanCpuPreset);
                        cbxEcFanGpuPreset.SelectedIndex = GetEcFanPresetIndex(myPreset.EcFanGpuPreset);
                        // Gap 1: Restore EC Fan custom curves per-game
                        ApplyEcFanCustomDuties(myPreset.EcFanPreset, myPreset.EcFanCustomDuties,
                            myPreset.EcFanCpuPreset, myPreset.EcFanCpuCustomDuties,
                            myPreset.EcFanGpuPreset, myPreset.EcFanGpuCustomDuties);
                        UpdateEcFanModeUI();
                    }

                    // Keyboard RGB
                    cbxKbMode.SelectedIndex = myPreset.KbPerKeyMode ? 1 : 0;
                    UpdateKbModeUI();
                    int brightness = Math.Clamp(myPreset.KbBrightness, 1, 7);
                    sdKbBrightness.Value = brightness;
                    tbKbBrightnessValue.Text = brightness.ToString();
                    cbxKbEffect.SelectedIndex = GetKbEffectIndex(myPreset.KbEffectMode);
                    KbColorPicker.SelectedColor = Color.FromRgb(myPreset.KbColorR, myPreset.KbColorG, myPreset.KbColorB);
                    KbRestColorPicker.SelectedColor = Color.FromRgb(myPreset.KbRestColorR, myPreset.KbRestColorG, myPreset.KbRestColorB);
                    int speed = Math.Clamp((int)myPreset.KbEffectSpeed, 1, 10);
                    sdKbSpeed.Value = speed;
                    tbKbSpeedValue.Text = speed.ToString();
                    _kbDirection = myPreset.KbDirection;
                    UpdateKbDirectionButtons();

                    // Restore multi-colors if saved
                    if (!string.IsNullOrEmpty(myPreset.KbMultiColors))
                    {
                        var colors = ParseColorString(myPreset.KbMultiColors);
                        if (colors.Count > 0)
                        {
                            int count = myPreset.KbEffectMode == "GamingModeFull" || myPreset.KbEffectMode == "GamingMode" ? 4 : 7;
                            KbMultiColorPicker.SetColors(colors, count);
                        }
                    }

                    // Push the preset's per-key colors to the global keyboard settings
                    // so that update() picks them up when in per-key mode. Only do this
                    // when keyboard override is active — don't overwrite the user's
                    // own per-key colors with stale preset data.
                    if (!string.IsNullOrEmpty(myPreset.KbPerKeyColors) && _deviceApplier?.IsKeyboardOverridden == true)
                    {
                        var kbSettings = KeyboardSettingsService.Load();
                        kbSettings.PerKeyColors = myPreset.KbPerKeyColors;
                        KeyboardSettingsService.Save(kbSettings);
                    }

                    // CPU Settings
                    if (Family.TYPE == Family.ProcessorType.Amd_Apu)
                    {
                        AdpSdSustained.Value = Math.Clamp(myPreset.CpuSustained, 5, 255);
                        AdpSdBoost.Value = Math.Clamp(myPreset.CpuBoost, 5, 255);
                        AdpSdTjMax.Value = Math.Clamp(myPreset.CpuTjMax, 70, 105);
                        AdpSdCoAll.Value = Math.Clamp(myPreset.CpuCo, -50, 0);
                    }
                    else if (Family.TYPE == Family.ProcessorType.Intel)
                    {
                        AdpSdIntelPl1.Value = Math.Clamp(myPreset.CpuPl1, 5, 400);
                        AdpSdIntelPl2.Value = Math.Clamp(myPreset.CpuPl2, 5, 400);
                        AdpSdIntelUndervolt.Value = Math.Clamp(myPreset.CpuUndervolt, -150, 0);
                    }

                    // GPU Settings
                    if (sdGpuSettings.Visibility == Visibility.Visible)
                    {
                        AdpSdNvPower.Value = Math.Clamp(myPreset.GpuTgp, 0, 150);
                        AdpTogDynamicBoost.IsChecked = myPreset.GpuDynamicBoost;
                        AdpSdNvDbOffset.Value = Math.Clamp(myPreset.GpuDbOffset, 0, 25);
                        AdpSdNvCore.Value = Math.Clamp(myPreset.GpuCoreClock, -1000, 1000);
                        AdpSdNvMem.Value = Math.Clamp(myPreset.GpuMemClock, -1000, 2000);
                        // Show/hide Dynamic Boost offset based on toggle
                        AdpSpNvDbOffset.Visibility = myPreset.GpuDynamicBoost ? Visibility.Visible : Visibility.Collapsed;
                        // Reset user-touched flag when loading preset — start fresh
                        _adpUserTouchedCoreSlider = false;
                        // Gap 4: Track which curve this profile wants (apply deferred to update() loop)
                        _adpActiveCurveName = myPreset.GpuVfCurveName;
                        updateAdpCoreLabel();
                    }

                    // Light Bar Settings
                    if (sdLightBar.Visibility == Visibility.Visible)
                    {
                        AdpTsLightBarPower.IsChecked = myPreset.LbPower;
                        AdpLbColorPicker.SelectedColor = Color.FromRgb(myPreset.LbColorR, myPreset.LbColorG, myPreset.LbColorB);
                        AdpLbBrightnessSlider.Value = Math.Clamp(myPreset.LbBrightness, 0, 100);
                        AdpLbBrightnessValueText.Text = Math.Clamp(myPreset.LbBrightness, 0, 100).ToString();
                        AdpLbEffect.SelectedIndex = Math.Clamp(myPreset.LbEffect, 0, 5);
                        AdpLbSpeedSlider.Value = Math.Clamp(myPreset.LbSpeed, 0, 11);
                        AdpLbSpeedValueText.Text = Math.Clamp(myPreset.LbSpeed, 0, 11).ToString();
                        // Show/hide speed control (hidden for Static)
                        AdpSpLbSpeed.Visibility = myPreset.LbEffect != 0 ? Visibility.Visible : Visibility.Collapsed;
                    }

                    // Apply the preset's per-profile override flags only when resetTracking
                    // is true (profile switch). When resetTracking is false (Page_Loaded,
                    // game detection without profile change), preserve the user's current
                    // checkbox edits so they aren't overwritten by the saved preset.
                    if (resetTracking)
                    {
                        if (cbxBs2ProEnabled.IsChecked != myPreset.Bs2ProEnabled)
                            cbxBs2ProEnabled.IsChecked = myPreset.Bs2ProEnabled;
                        if (cbxWcEnabled.IsChecked != myPreset.WcEnabled)
                            cbxWcEnabled.IsChecked = myPreset.WcEnabled;
                        if (cbxKbEnabled.IsChecked != myPreset.KbEnabled)
                            cbxKbEnabled.IsChecked = myPreset.KbEnabled;
                        if (cbxCpuEnabled.IsChecked != myPreset.CpuEnabled)
                            cbxCpuEnabled.IsChecked = myPreset.CpuEnabled;
                        if (cbxGpuEnabled.IsChecked != myPreset.GpuEnabled)
                            cbxGpuEnabled.IsChecked = myPreset.GpuEnabled;
                        if (cbxLbEnabled.IsChecked != myPreset.LbEnabled)
                            cbxLbEnabled.IsChecked = myPreset.LbEnabled;

                        // Sync global settings to the active preset's override state so that
                        // App.xaml.cs startup restoration reads the correct values.
                        Settings.Default.AdaptiveBs2ProEnabled = myPreset.Bs2ProEnabled;
                        Settings.Default.AdaptiveWcEnabled = myPreset.WcEnabled;
                        Settings.Default.AdaptiveKeyboardEnabled = myPreset.KbEnabled;
                        Settings.Default.AdaptiveCpuEnabled = myPreset.CpuEnabled;
                        Settings.Default.AdaptiveGpuEnabled = myPreset.GpuEnabled;
                        Settings.Default.AdaptiveLbEnabled = myPreset.LbEnabled;
                        Settings.Default.Save();
                    }

                    if (resetTracking)
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
                        lastKbRestColorR = 0;
                        lastKbRestColorG = 0;
                        lastKbRestColorB = 0;
                        lastKbMultiColors = "";
                        lastKbDirection = "";

                        // CPU tracking
                        lastCpuSustained = 0;
                        lastCpuBoost = 0;
                        lastCpuTjMax = 0;
                        lastCpuCo = 0;
                        lastCpuPl1 = 0;
                        lastCpuPl2 = 0;
                        lastCpuUndervolt = 0;

                        // GPU tracking
                        lastGpuTgp = 0;
                        lastGpuDynamicBoost = false;
                        lastGpuDbOffset = 0;
                        lastGpuCoreClock = 0;
                        lastGpuMemClock = 0;
                        lastGpuVfCurveName = string.Empty;

                        // Light Bar tracking
                        lastLbPower = false;
                        lastLbColorR = 0;
                        lastLbColorG = 0;
                        lastLbColorB = 0;
                        lastLbBrightness = 0;
                        lastLbEffect = 0;
                        lastLbSpeed = 0;
                    }
                    // When resetTracking is false (Page_Loaded, game detection), leave
                    // tracking vars unchanged so update() won't re-send device commands
                    // for values that are already applied.

                    // Stop any existing smart control (curve mode) so it restarts with the new profile
                    StopBs2ProSmartControl();
                    StopBs1SmartControl();
                }
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
                AdaptivePreset preset = new AdaptivePreset
                {
                    WcEnabled = (bool)cbxWcEnabled.IsChecked,
                    WcPumpVoltage = GetPumpVoltageFromIndex(cbxWcPumpVoltage.SelectedIndex).ToString(),
                    WcFanSpeed = GetFanSpeedFromIndex(cbxWcFanSpeed.SelectedIndex).ToString(),
                    WcRgbMode = GetRgbModeFromIndex(cbxWcRgbMode.SelectedIndex).ToString(),
                    WcRgbColor = GetRgbColorFromIndex(cbxWcRgbColor.SelectedIndex).ToString(),
                    Bs2ProEnabled = (bool)cbxBs2ProEnabled.IsChecked,
                    Bs2ProFanMode = GetBs2ProFanModeFromIndex(cbxBs2ProFanMode.SelectedIndex),
                    Bs2ProGear = cbxBs2ProGear.SelectedIndex + 1,
                    Bs2ProRpm = (ushort)Math.Clamp((int)nudBs2ProRpm.Value, 1300, 4000),
                    Bs2ProCurveProfileId = GetBs2ProCurveProfileId(cbxBs2ProCurve.SelectedIndex),
                    // Gap 3: Save Flydigi custom curve points per-game
                    Bs2ProCurvePointsJson = GetBs2ProCurvePointsJson(),
                    Bs2ProRgbMode = GetBs2ProRgbModeFromIndex(cbxBs2ProRgbMode.SelectedIndex),
                    Bs2ProRgbR = (byte)nudBs2ProRgbR.Value,
                    Bs2ProRgbG = (byte)nudBs2ProRgbG.Value,
                    Bs2ProRgbB = (byte)nudBs2ProRgbB.Value,
                    Bs2ProBrightness = (byte)sliderBs2ProBrightness.Value,
                    // Gap 2: Save Flydigi RGB speed per-game
                    Bs2ProRgbSpeed = (byte)sliderBs2ProRgbSpeed.Value,
                    EcFanEnabled = sdEcFan.Visibility == Visibility.Visible ? (bool)cbxEcFanEnabled.IsChecked : false,
                    EcFanUnifiedMode = sdEcFan.Visibility == Visibility.Visible ? cbxEcFanMode.SelectedIndex == 0 : false,
                    EcFanPreset = sdEcFan.Visibility == Visibility.Visible ? GetEcFanPresetFromIndex(cbxEcFanPreset.SelectedIndex) : "Balanced",
                    EcFanCpuPreset = sdEcFan.Visibility == Visibility.Visible ? GetEcFanPresetFromIndex(cbxEcFanCpuPreset.SelectedIndex) : "Balanced",
                    EcFanGpuPreset = sdEcFan.Visibility == Visibility.Visible ? GetEcFanPresetFromIndex(cbxEcFanGpuPreset.SelectedIndex) : "Balanced",
                    // Gap 1: Save EC Fan custom curves per-game
                    EcFanCustomDuties = sdEcFan.Visibility == Visibility.Visible && GetEcFanPresetFromIndex(cbxEcFanPreset.SelectedIndex) == "Custom"
                        ? FanControlSettingsService.Load().UnifiedDuties?.ToArray() : null,
                    EcFanCpuCustomDuties = sdEcFan.Visibility == Visibility.Visible && GetEcFanPresetFromIndex(cbxEcFanCpuPreset.SelectedIndex) == "Custom"
                        ? FanControlSettingsService.Load().CpuDuties?.ToArray() : null,
                    EcFanGpuCustomDuties = sdEcFan.Visibility == Visibility.Visible && GetEcFanPresetFromIndex(cbxEcFanGpuPreset.SelectedIndex) == "Custom"
                        ? FanControlSettingsService.Load().GpuDuties?.ToArray() : null,
                    KbEnabled = (bool)cbxKbEnabled.IsChecked,
                    KbPerKeyMode = cbxKbMode.SelectedIndex == 1,
                    KbBrightness = (int)sdKbBrightness.Value,
                    KbIdleTimerEnabled = false,
                    KbIdleTimerMinutes = 0,
                    KbEffectMode = GetKbEffectFromIndex(cbxKbEffect.SelectedIndex),
                    KbEffectSpeed = (byte)Math.Clamp((int)sdKbSpeed.Value, 1, 10),
                    KbColorR = KbColorPicker.SelectedColor.R,
                    KbColorG = KbColorPicker.SelectedColor.G,
                    KbColorB = KbColorPicker.SelectedColor.B,
                    KbRestColorR = KbRestColorPicker.SelectedColor.R,
                    KbRestColorG = KbRestColorPicker.SelectedColor.G,
                    KbRestColorB = KbRestColorPicker.SelectedColor.B,
                    KbMultiColors = SerializeColors(KbMultiColorPicker.Colors),
                    KbPerKeyColors = KeyboardSettingsService.Load().PerKeyColors,
                    KbDirection = _kbDirection,
                    isAutoSwitch = (bool)tsAutoSwitch.IsChecked,
                    // CPU
                    CpuEnabled = (bool)cbxCpuEnabled.IsChecked,
                    CpuSustained = (int)AdpSdSustained.Value,
                    CpuBoost = (int)AdpSdBoost.Value,
                    CpuTjMax = (int)AdpSdTjMax.Value,
                    CpuCo = (int)AdpSdCoAll.Value,
                    CpuPl1 = (int)AdpSdIntelPl1.Value,
                    CpuPl2 = (int)AdpSdIntelPl2.Value,
                    CpuUndervolt = (int)AdpSdIntelUndervolt.Value,
                    // GPU
                    GpuEnabled = sdGpuSettings.Visibility == Visibility.Visible ? (bool)cbxGpuEnabled.IsChecked : false,
                    GpuTgp = (int)AdpSdNvPower.Value,
                    GpuDynamicBoost = AdpTogDynamicBoost.IsChecked == true,
                    GpuDbOffset = (int)AdpSdNvDbOffset.Value,
                    GpuCoreClock = (int)AdpSdNvCore.Value,
                    GpuMemClock = (int)AdpSdNvMem.Value,
                    // Gap 4: Save GPU V-F curve preset name per-game
                    GpuVfCurveName = GetActiveVfCurveName(),
                    // Light Bar
                    LbEnabled = sdLightBar.Visibility == Visibility.Visible ? (bool)cbxLbEnabled.IsChecked : false,
                    LbPower = AdpTsLightBarPower.IsChecked == true,
                    LbColorR = AdpLbColorPicker.SelectedColor.R,
                    LbColorG = AdpLbColorPicker.SelectedColor.G,
                    LbColorB = AdpLbColorPicker.SelectedColor.B,
                    LbBrightness = (int)AdpLbBrightnessSlider.Value,
                    LbEffect = AdpLbEffect.SelectedIndex,
                    LbSpeed = (int)AdpLbSpeedSlider.Value
                };
                adaptivePresetManager.SavePreset(presetName, preset);
            }
            catch (Exception ex)
            {
                DiagnosticLogger.LogError(ex, "Failed to save adaptive preset");
            }
        }

        private static LASTINPUTINFO lastInput = new LASTINPUTINFO();

        private async void btnReloadApps_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                cbxPowerPreset.ItemsSource = new List<string>();
                await Task.Run(() => Game_Manager.installedGames = Game_Manager.syncGame_Library(true));
                cbxPowerPreset.Items.Clear();
                cbxPowerPreset.Items.Add("Default");
                foreach (GameLauncherItem item in Game_Manager.installedGames) cbxPowerPreset.Items.Add(item.gameName);
                cbxPowerPreset.SelectedIndex = 0;
            }
            catch (Exception ex)
            {
                DiagnosticLogger.LogError(ex, "Failed to reload game apps");
            }
        }

        private void btnSave_Click(object sender, RoutedEventArgs e)
        {
            string presetName = cbxPowerPreset.SelectedItem.ToString();
            savePreset(presetName);

            // Sync active settings so other pages can detect the change immediately
            Settings.Default.AdaptiveBs2ProEnabled = (bool)cbxBs2ProEnabled.IsChecked;
            Settings.Default.AdaptiveWcEnabled = (bool)cbxWcEnabled.IsChecked;
            Settings.Default.AdaptiveCpuEnabled = (bool)cbxCpuEnabled.IsChecked;
            Settings.Default.AdaptiveGpuEnabled = (bool)cbxGpuEnabled.IsChecked;
            Settings.Default.AdaptiveLbEnabled = (bool)cbxLbEnabled.IsChecked;
            Settings.Default.Save();
        }

        private void cbxBs2ProEnabled_Toggled(object sender, RoutedEventArgs e)
        {
            bool enabled = (bool)cbxBs2ProEnabled.IsChecked;
            Settings.Default.AdaptiveBs2ProEnabled = enabled;
            Settings.Default.Save();

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
            bool enabled = (bool)cbxWcEnabled.IsChecked;
            Settings.Default.AdaptiveWcEnabled = enabled;
            Settings.Default.Save();

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
            bool enabled = (bool)cbxEcFanEnabled.IsChecked;

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

        private async void sensors_Tick(object sender, EventArgs e)
        {
            try
            {
                if (start == true)
                {
                    await Task.Run(() =>
                    {
                        isGameRunning();
                    });

                    if (cbxPowerPreset.Items.Count > 0 && cbAutoSwitch.IsChecked == true)
                    {
                        string selectedGameName = string.Empty;

                        Dispatcher.Invoke(() =>
                        {
                            selectedGameName = cbxPowerPreset.SelectedItem.ToString();
                        });

                        if (selectedGameName != runningGameName)
                        {
                            Dispatcher.Invoke(() =>
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

        PumpVoltage lastWcPump = PumpVoltage.Off;
        FanSpeed lastWcFan = FanSpeed.Off;
        RgbState lastWcRgbMode = RgbState.Off;
        RgbColor lastWcRgbColor = RgbColor.Red;
        string lastBs2ProFanModeText = "";
        byte? lastBs2ProGear = 0;
        ushort? lastBs2ProRpm = 0;
        string lastBs2ProRgbMode = "";
        byte lastBs2ProRgbR = 0;
        byte lastBs2ProRgbG = 0;
        byte lastBs2ProRgbB = 0;
        byte lastBs2ProBrightness = 0;
        string lastBs2ProSpeed = "Medium";
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
        private byte lastKbRestColorR = 0;
        private byte lastKbRestColorG = 0;
        private byte lastKbRestColorB = 0;
        private string lastKbMultiColors = "";
        private string _kbDirection = "LeftRight";
        private string lastKbDirection = "";
        // CPU diff-tracking
        private int lastCpuSustained = 0;
        private int lastCpuBoost = 0;
        private int lastCpuTjMax = 0;
        private int lastCpuCo = 0;
        private int lastCpuPl1 = 0;
        private int lastCpuPl2 = 0;
        private int lastCpuUndervolt = 0;
        // GPU diff-tracking
        private int lastGpuTgp = 0;
        private bool lastGpuDynamicBoost = false;
        private int lastGpuDbOffset = 0;
        private int lastGpuCoreClock = 0;
        private int lastGpuMemClock = 0;
        private string lastGpuVfCurveName = string.Empty;
        // Light Bar diff-tracking
        private bool lastLbPower = false;
        private byte lastLbColorR = 0;
        private byte lastLbColorG = 0;
        private byte lastLbColorB = 0;
        private int lastLbBrightness = 0;
        private int lastLbEffect = 0;
        private int lastLbSpeed = 0;
        // V-F Curve editor window (singleton, like Presets page)
        private Views.Windows.VfCurveEditorWindow? _vfEditorWindow;
        // True when user manually changes the core clock slider — curve label is suppressed
        private bool _adpUserTouchedCoreSlider;
        // Name of the V-F curve loaded by the current per-game profile (empty = no curve)
        private string _adpActiveCurveName = string.Empty;
        // KeyboardHidService for Light Bar control
        private KeyboardHidService? _hidService;
        private SemaphoreSlim _updateSemaphore = new(1, 1);
        private async void update()
        {
            // Prevent concurrent executions from adaptive_Tick and sensors_Tick
            if (!_updateSemaphore.Wait(0))
                return;

            try
            {
                if (start == true)
                {
                    // Apply watercooler settings if enabled
                        if ((bool)cbxWcEnabled.IsChecked)
                        {
                            PumpVoltage curPump = GetPumpVoltageFromIndex(cbxWcPumpVoltage.SelectedIndex);
                            FanSpeed curFan = GetFanSpeedFromIndex(cbxWcFanSpeed.SelectedIndex);
                            RgbState curRgbMode = GetRgbModeFromIndex(cbxWcRgbMode.SelectedIndex);
                            RgbColor curRgbColor = GetRgbColorFromIndex(cbxWcRgbColor.SelectedIndex);

                            if (curPump != lastWcPump || curFan != lastWcFan ||
                                curRgbMode != lastWcRgbMode || curRgbColor != lastWcRgbColor)
                            {
                                await _deviceApplier!.ApplyWatercoolerFromPresetAsync(curPump, curFan, curRgbMode, curRgbColor);
                                lastWcPump = curPump;
                                lastWcFan = curFan;
                                lastWcRgbMode = curRgbMode;
                                lastWcRgbColor = curRgbColor;
                            }
                        }

                        // Apply BS2 Pro settings if enabled
                        if ((bool)cbxBs2ProEnabled.IsChecked)
                        {
                            string bs2Mode = GetBs2ProFanModeFromIndex(cbxBs2ProFanMode.SelectedIndex);
                            string bs2RgbMode = GetBs2ProRgbModeFromIndex(cbxBs2ProRgbMode.SelectedIndex);
                            byte bs2R = (byte)nudBs2ProRgbR.Value;
                            byte bs2G = (byte)nudBs2ProRgbG.Value;
                            byte bs2B = (byte)nudBs2ProRgbB.Value;
                            byte bs2Brightness = (byte)sliderBs2ProBrightness.Value;
                            string bs2Speed = GetRgbSpeedNameFromSlider((int)sliderBs2ProRgbSpeed.Value);

                            // --- Fan control (differs by mode) ---
                            if (bs2Mode == "Curve")
                            {
                                // Handle Curve mode via smart control (stateful, can't route through DeviceApplier)
                                bool isBs1Curve = FlydigiHardwareDetector.ConnectedDeviceType == ConnectedDeviceType.BS1;

                                if (isBs1Curve)
                                {
                                    // BS1 Curve mode via app-localized Bs1SmartControl
                                    var bs1Service = App.GetService<Bs1Service>();
                                    if (bs1Service?.IsConnected == true)
                                    {
                                        string curveProfileId = GetBs2ProCurveProfileId(cbxBs2ProCurve.SelectedIndex);
                                        FlydigiFanCurveProfile curveProfile = curveProfileId switch
                                        {
                                            "Silent" => FlydigiFanCurveProfile.CreateSilent(),
                                            "Performance" => FlydigiFanCurveProfile.CreatePerformance(),
                                            "Custom" => LoadBs1CustomCurveProfile(bs1Service),
                                            _ => FlydigiFanCurveProfile.CreateBalanced()
                                        };

                                        if (_bs1SmartControl == null)
                                        {
                                            var tempProvider = new FlydigiTemperatureProvider();
                                            _bs1SmartControl = new Bs1SmartControl(bs1Service, tempProvider);
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
                                }
                                else
                                {
                                    // BS2+ Curve mode via FlydigiSmartControl
                                    var flydigiService = App.GetService<FlydigiCoolerService>();
                                    if (flydigiService?.IsConnected == true)
                                    {
                                        string curveProfileId = GetBs2ProCurveProfileId(cbxBs2ProCurve.SelectedIndex);
                                        FlydigiFanCurveProfile curveProfile = curveProfileId switch
                                        {
                                            "Silent" => FlydigiFanCurveProfile.CreateSilent(),
                                            "Performance" => FlydigiFanCurveProfile.CreatePerformance(),
                                            "Custom" => LoadCustomCurveProfile(flydigiService),
                                            _ => FlydigiFanCurveProfile.CreateBalanced()
                                        };

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
                            }
                            else
                            {
                                // For non-curve modes, stop smart control if active
                                if (_bs2ProSmartControl != null)
                                    StopBs2ProSmartControl();
                                if (_bs1SmartControl != null)
                                    StopBs1SmartControl();

                                byte? gear = null;
                                ushort? rpm = null;
                                if (bs2Mode == "Gear")
                                    gear = (byte)(cbxBs2ProGear.SelectedIndex + 1);
                                else if (bs2Mode == "Rpm")
                                    rpm = (ushort)Math.Clamp((int)nudBs2ProRpm.Value, 1300, (FlydigiHardwareDetector.ConnectedDeviceType == ConnectedDeviceType.BS1 ? 3000 : 4000));

                                // Use DeviceApplier for fan (diff-based to avoid redundant writes)
                                bool fanChanged = bs2Mode != lastBs2ProFanModeText ||
                                    (bs2Mode == "Gear" && gear != lastBs2ProGear) ||
                                    (bs2Mode == "Rpm" && rpm != lastBs2ProRpm);

                                if (fanChanged)
                                {
                                    await _deviceApplier!.ApplyFlydigiFanAsync(bs2Mode, gear, rpm);
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
                                await _deviceApplier!.ApplyFlydigiRgbAsync(bs2RgbMode, bs2R, bs2G, bs2B, bs2Brightness);

                                // Fire preset-applied event so the Flydigi page can sync its UI.
                                byte? fanGear = bs2Mode == "Gear" ? (byte?)(cbxBs2ProGear.SelectedIndex + 1) : null;
                                ushort? fanRpm = bs2Mode == "Rpm" ? (ushort?)Math.Clamp((int)nudBs2ProRpm.Value, 1300, (FlydigiHardwareDetector.ConnectedDeviceType == ConnectedDeviceType.BS1 ? 3000 : 4000)) : null;
                                _deviceApplier.RaiseFlydigiPresetApplied(bs2Mode, fanGear, fanRpm, bs2RgbMode, bs2R, bs2G, bs2B, bs2Brightness);

                                lastBs2ProRgbMode = bs2RgbMode;
                                lastBs2ProRgbR = bs2R;
                                lastBs2ProRgbG = bs2G;
                                lastBs2ProRgbB = bs2B;
                                lastBs2ProBrightness = bs2Brightness;
                                lastBs2ProSpeed = bs2Speed;
                            }
                        }

                        // Apply EC Fan settings if enabled
                        if ((bool)cbxEcFanEnabled.IsChecked && _deviceApplier != null)
                        {
                            bool unified = cbxEcFanMode.SelectedIndex == 0;
                            string unifiedPreset = GetEcFanPresetFromIndex(cbxEcFanPreset.SelectedIndex);
                            string cpuPreset = GetEcFanPresetFromIndex(cbxEcFanCpuPreset.SelectedIndex);
                            string gpuPreset = GetEcFanPresetFromIndex(cbxEcFanGpuPreset.SelectedIndex);

                            bool changed = unified != lastEcFanUnifiedMode ||
                                (unified && unifiedPreset != lastEcFanPreset) ||
                                (!unified && (cpuPreset != lastEcFanCpuPreset || gpuPreset != lastEcFanGpuPreset));

                            if (changed)
                            {
                                if (unified)
                                {
                                    var cpuCurve = GetEcFanPresetCurve(unifiedPreset);
                                    var cpuTemps = Universal_x86_Tuning_Utility.Models.EcFanCurve.CpuTemperatures;

                                    _deviceApplier.ApplyEcFanFromPreset(true, unifiedPreset,
                                        cpuCurve.Duties.ToArray(), null, null,
                                        cpuTemps, cpuTemps);

                                    lastEcFanPreset = unifiedPreset;
                                }
                                else
                                {
                                    var cpuCurve = GetEcFanPresetCurve(cpuPreset);
                                    var gpuCurve = GetEcFanPresetCurve(gpuPreset);
                                    var cpuTemps = Universal_x86_Tuning_Utility.Models.EcFanCurve.CpuTemperatures;
                                    var gpuTemps = Universal_x86_Tuning_Utility.Models.EcFanCurve.GpuTemperatures;

                                    _deviceApplier.ApplyEcFanFromPreset(false, cpuPreset,
                                        null, cpuCurve.Duties.ToArray(), gpuCurve.Duties.ToArray(),
                                        cpuTemps, gpuTemps);

                                    lastEcFanCpuPreset = cpuPreset;
                                    lastEcFanGpuPreset = gpuPreset;
                                }
                                lastEcFanUnifiedMode = unified;
                            }
                        }

                        // Apply Keyboard RGB settings if enabled
                        if ((bool)cbxKbEnabled.IsChecked && _deviceApplier != null)
                        {
                            bool perKeyMode = cbxKbMode.SelectedIndex == 1;
                            int brightness = (int)sdKbBrightness.Value;
                            string effectMode = GetKbEffectFromIndex(cbxKbEffect.SelectedIndex);
                            byte effectSpeed = (byte)(10 - Math.Clamp((int)sdKbSpeed.Value, 1, 10));
                            byte colorR = KbColorPicker.SelectedColor.R;
                            byte colorG = KbColorPicker.SelectedColor.G;
                            byte colorB = KbColorPicker.SelectedColor.B;
                            byte restColorR = KbRestColorPicker.SelectedColor.R;
                            byte restColorG = KbRestColorPicker.SelectedColor.G;
                            byte restColorB = KbRestColorPicker.SelectedColor.B;
                            string multiColors = SerializeColors(KbMultiColorPicker.Colors);

                            bool changed = perKeyMode != lastKbPerKeyMode ||
                                brightness != lastKbBrightness ||
                                effectMode != lastKbEffectMode ||
                                effectSpeed != lastKbEffectSpeed ||
                                _kbDirection != lastKbDirection ||
                                colorR != lastKbColorR ||
                                colorG != lastKbColorG ||
                                colorB != lastKbColorB ||
                                restColorR != lastKbRestColorR ||
                                restColorG != lastKbRestColorG ||
                                restColorB != lastKbRestColorB ||
                                multiColors != lastKbMultiColors;

                            if (changed)
                            {
                                // In per-key mode, pass the current per-key colors from settings
                                string? perKeyColorsStr = perKeyMode
                                    ? KeyboardSettingsService.Load().PerKeyColors
                                    : null;

                                _deviceApplier.ApplyKeyboardFromPreset(
                                    perKeyMode, brightness, false, 0,
                                    effectMode, effectSpeed, _kbDirection,
                                    colorR, colorG, colorB,
                                    multiColors, perKeyColorsStr, restColorR, restColorG, restColorB);

                                lastKbPerKeyMode = perKeyMode;
                                lastKbBrightness = brightness;
                                lastKbEffectMode = effectMode;
                                lastKbEffectSpeed = effectSpeed;
                                lastKbDirection = _kbDirection;
                                lastKbColorR = colorR;
                                lastKbColorG = colorG;
                                lastKbColorB = colorB;
                                lastKbRestColorR = restColorR;
                                lastKbRestColorG = restColorG;
                                lastKbRestColorB = restColorB;
                                lastKbMultiColors = multiColors;
                            }
                        }

                        // Apply CPU settings if enabled
                        if ((bool)cbxCpuEnabled.IsChecked)
                        {
                            if (Family.TYPE == Family.ProcessorType.Amd_Apu)
                            {
                                int sustained = (int)AdpSdSustained.Value;
                                int boost = (int)AdpSdBoost.Value;
                                int tjMax = (int)AdpSdTjMax.Value;
                                int co = (int)AdpSdCoAll.Value;

                                if (sustained != lastCpuSustained || boost != lastCpuBoost ||
                                    tjMax != lastCpuTjMax || co != lastCpuCo)
                                {
                                    try
                                    {
                                        // Build SMU command string (same format as Presets page)
                                        var parts = new System.Collections.Generic.List<string>();
                                        parts.Add($"--slow-limit={sustained * 1000}");
                                        parts.Add($"--fast-limit={boost * 1000}");
                                        parts.Add($"--tctl-temp={tjMax}");
                                        parts.Add($"--cHTC-temp={tjMax}");
                                        if (co > 0)
                                            parts.Add($"--set-coall={(byte)co}");

                                        string cmd = string.Join(" ", parts);
                                        _ = RyzenAdj_To_UXTU.TranslateAsync(cmd);

                                        // Also write EC CPU power limits
                                        var ec = RyzenAdj_To_UXTU.EcService;
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
                                    lastCpuCo = co;
                                }
                            }
                            else if (Family.TYPE == Family.ProcessorType.Intel)
                            {
                                int pl1 = (int)AdpSdIntelPl1.Value;
                                int pl2 = (int)AdpSdIntelPl2.Value;
                                int uv = (int)AdpSdIntelUndervolt.Value;

                                if (pl1 != lastCpuPl1 || pl2 != lastCpuPl2 || uv != lastCpuUndervolt)
                                {
                                    try
                                    {
                                        // Build Intel MSR command string (same format as Presets page)
                                        var parts = new System.Collections.Generic.List<string>();
                                        parts.Add($"--intel-pl={pl1:F0},{pl2:F0}");
                                        if (uv < 0)
                                            parts.Add($"--intel-volt-cpu={uv:F0}");

                                        string cmd = string.Join(" ", parts);
                                        _ = RyzenAdj_To_UXTU.TranslateAsync(cmd);
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
                        }

                        // Apply GPU settings if enabled
                        if ((bool)cbxGpuEnabled.IsChecked && sdGpuSettings.Visibility == Visibility.Visible)
                        {
                            int tgp = (int)AdpSdNvPower.Value;
                            bool dbEnabled = AdpTogDynamicBoost.IsChecked == true;
                            int dbOffset = dbEnabled ? (int)AdpSdNvDbOffset.Value : 0;
                            int memClock = (int)AdpSdNvMem.Value;

                            // When a V-F curve is active and user hasn't touched the slider,
                            // skip the core clock offset — the curve handles per-point frequency.
                            int coreClock = (AdpVfCurveActive && !_adpUserTouchedCoreSlider) ? 0 : (int)AdpSdNvCore.Value;

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
                                    // Apply via the same NVIDIA command format as Presets page
                                    string nvCmd = $"--NVIDIA-Clocks=0-{coreClock}-{memClock}-{totalWatts}-{dbOffset}";
                                    _ = RyzenAdj_To_UXTU.TranslateAsync(nvCmd);
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
                            if (_adpActiveCurveName != lastGpuVfCurveName)
                            {
                                ApplyActiveVfCurveToGpu();
                                lastGpuVfCurveName = _adpActiveCurveName;
                            }
                        }

                        // Apply Light Bar settings if enabled
                        if ((bool)cbxLbEnabled.IsChecked && sdLightBar.Visibility == Visibility.Visible && _hidService != null)
                        {
                            bool lbPower = AdpTsLightBarPower.IsChecked == true;
                            byte lbR = AdpLbColorPicker.SelectedColor.R;
                            byte lbG = AdpLbColorPicker.SelectedColor.G;
                            byte lbB = AdpLbColorPicker.SelectedColor.B;
                            int lbBrightness = (int)AdpLbBrightnessSlider.Value;
                            int lbEffect = AdpLbEffect.SelectedIndex;
                            int lbSpeed = (int)AdpLbSpeedSlider.Value;

                            bool lbChanged = lbPower != lastLbPower ||
                                lbR != lastLbColorR || lbG != lastLbColorG || lbB != lastLbColorB ||
                                lbBrightness != lastLbBrightness ||
                                lbEffect != lastLbEffect || lbSpeed != lastLbSpeed;

                            if (lbChanged)
                            {
                                try
                                {
                                    if (lbPower)
                                    {
                                        _hidService.LightBarSetColor(lbR, lbG, lbB, lbBrightness);
                                        _hidService.LightBarSetEffect((byte)lbEffect, (byte)lbSpeed, lbBrightness);
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
                        }
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

        public bool IsScrollBarVisible(ScrollViewer scrollViewer)
        {
            if (scrollViewer == null) throw new ArgumentNullException(nameof(scrollViewer));

            return scrollViewer.ExtentHeight > scrollViewer.ViewportHeight;
        }

        private void cbAutoSwitch_Click(object sender, RoutedEventArgs e)
        {
            Settings.Default.autoSwitch = (bool)cbAutoSwitch.IsChecked;
            Settings.Default.Save();
        }

        private static LauncherManager launcherManager = new LauncherManager(new LauncherOptions() { QueryOnlineData = true });

        private void Page_Loaded(object sender, RoutedEventArgs e)
        {
            // Recheck Flydigi card visibility on navigation (device may have connected/disconnected)
            UpdateFlydigiCardVisibility();

            // Defer preset loading to after the first frame renders so the page
            // appears immediately and control updates happen in a second pass.
            if (cbxPowerPreset.SelectedItem is string presetName)
            {
                Dispatcher.InvokeAsync(() =>
                {
                    adaptivePresetManager = new AdaptivePresetManager(Settings.Default.Path + "adaptivePresets.json");
                    // Don't reset tracking on navigation — just load the preset UI values
                    loadPreset(presetName, resetTracking: false);
                    // Ensure Keyboard RGB effect controls visibility matches the mode selector.
                    // Needed because loadPreset skips UI updates when myPreset is null
                    // (first-time load with no saved presets).
                    UpdateKbModeUI();
                }, DispatcherPriority.Loaded);
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
                        existing.KbRestColorR = KbRestColorPicker.SelectedColor.R;
                        existing.KbRestColorG = KbRestColorPicker.SelectedColor.G;
                        existing.KbRestColorB = KbRestColorPicker.SelectedColor.B;
                        existing.KbMultiColors = SerializeColors(KbMultiColorPicker.Colors);
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
        }


        string runningGameName = DefaultProfileName;
        string lastConfirmedGame = DefaultProfileName;
        int gameMissingCount = 0;
        const int maxGameMisses = 2;

        private void isGameRunning()
        {
            string detectedGame = DefaultProfileName;

            // --- Pass 1: Process-based detection ---
            foreach (GameLauncherItem item in installedGames)
            {
                int i = 0;
                do
                {
                    Process[] processes = Process.GetProcesses();

                    foreach (Process process in processes)
                    {
                        try
                        {
                            string executablePath = process.MainModule.FileName;

                            if (executablePath.Contains(item.path))
                            {
                                bool autoSwitch = true;
                                AdaptivePreset preset = adaptivePresetManager.GetPreset(item.gameName);
                                if (preset != null)
                                {
                                    autoSwitch = preset.isAutoSwitch;
                                }
                                if (!autoSwitch)
                                {
                                    continue;
                                }

                                detectedGame = item.gameName;
                                break;
                            }
                        }
                        catch (Exception ex)
                        {
                            DiagnosticLogger.LogError(ex, "Failed to check running game process");
                        }
                    }

                    if (detectedGame != DefaultProfileName)
                    {
                        break;
                    }

                    i++;
                } while (i < 2);

                if (detectedGame != DefaultProfileName)
                {
                    break;
                }
            }

            // --- Pass 2: Window-title fallback for fullscreen/elevated games ---
            // Some games run elevated or in a protected context after launch, so MainModule.FileName
            // throws an exception.  Enumerate visible windows and match titles against game names.
            if (detectedGame == DefaultProfileName)
            {
                var matchedGameNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var classNameSb = new StringBuilder(256);
                var titleSb = new StringBuilder(256);

                EnumWindows((hWnd, lParam) =>
                {
                    if (!IsWindowVisible(hWnd))
                        return true; // continue enumeration

                    // Get window class to filter out non-game windows
                    classNameSb.Clear();
                    GetClassName(hWnd, classNameSb, classNameSb.Capacity);
                    if (ExcludedWindowClasses.Contains(classNameSb.ToString()))
                        return true;

                    int length = GetWindowTextLength(hWnd);
                    if (length == 0)
                        return true;

                    // Resize buffer if the title is longer than our default capacity
                    if (length + 1 > titleSb.Capacity)
                        titleSb.Capacity = length + 1;

                    titleSb.Clear();
                    GetWindowText(hWnd, titleSb, titleSb.Capacity);
                    string windowTitle = titleSb.ToString().Trim();

                    foreach (GameLauncherItem item in installedGames)
                    {
                        if (windowTitle.Contains(item.gameName, StringComparison.OrdinalIgnoreCase))
                        {
                            bool autoSwitch = true;
                            AdaptivePreset preset = adaptivePresetManager.GetPreset(item.gameName);
                            if (preset != null)
                                autoSwitch = preset.isAutoSwitch;

                            if (autoSwitch)
                                matchedGameNames.Add(item.gameName);
                        }
                    }

                    return true; // continue enumeration
                }, IntPtr.Zero);

                if (matchedGameNames.Count > 0)
                {
                    // Prefer the longest match (most specific game name).
                    detectedGame = matchedGameNames.OrderByDescending(n => n.Length).First();
                }
            }

            // Commit detection immediately; only delay the revert.
            if (detectedGame != DefaultProfileName)
            {
                lastConfirmedGame = detectedGame;
                runningGameName = detectedGame;
                gameMissingCount = 0;
            }
            else
            {
                // No game detected this poll.
                if (lastConfirmedGame != DefaultProfileName)
                {
                    // Game was previously detected but is now missing — count misses.
                    gameMissingCount++;
                    if (gameMissingCount >= maxGameMisses)
                    {
                        runningGameName = DefaultProfileName;
                        lastConfirmedGame = DefaultProfileName;
                        gameMissingCount = 0;
                    }
                }
                // If already at Default, nothing to do.
            }
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
                1 => "Gear",
                2 => "Rpm",
                _ => "Curve"
            };
        }

        private static int GetBs2ProFanModeIndex(string mode)
        {
            return mode switch
            {
                "Gear" => 0,
                "Rpm" => 1,
                "Curve" => 2,
                _ => 0
            };
        }

        private static string GetBs2ProCurveProfileId(int index)
        {
            return index switch
            {
                0 => "Silent",
                1 => "Balanced",
                2 => "Performance",
                3 => "Custom",
                _ => "Silent"
            };
        }

        private static int GetBs2ProCurveIndex(string profileId)
        {
            return profileId switch
            {
                "Silent" => 0,
                "Balanced" => 1,
                "Performance" => 2,
                "Custom" => 3,
                _ => 0
            };
        }

        /// <summary>Stops and disposes the BS2 Pro smart control (curve mode) if active.</summary>
        private static void StopBs2ProSmartControl()
        {
            if (_bs2ProSmartControl != null)
            {
                try { _bs2ProSmartControl.Stop(); } catch { /* ignore */ }
                try { _bs2ProSmartControl.Dispose(); } catch { /* ignore */ }
                _bs2ProSmartControl = null;
            }
            if (_bs2ProTempProvider != null)
            {
                try { _bs2ProTempProvider.Dispose(); } catch { /* ignore */ }
                _bs2ProTempProvider = null;
            }
        }

        private static void StopBs1SmartControl()
        {
            if (_bs1SmartControl != null)
            {
                try { _bs1SmartControl.Stop(); } catch { /* ignore */ }
                try { _bs1SmartControl.Dispose(); } catch { /* ignore */ }
                _bs1SmartControl = null;
            }
        }

        /// <summary>Loads the custom curve profile from Bs1 settings, falling back to Balanced.</summary>
        private static FlydigiFanCurveProfile LoadBs1CustomCurveProfile(Bs1Service bs1Service)
        {
            try
            {
                var bs1Settings = bs1Service.GetSettings();
                if (!string.IsNullOrEmpty(bs1Settings.CustomCurveJson))
                {
                    return FlydigiFanCurveProfile.FromJSON(bs1Settings.CustomCurveJson);
                }
            }
            catch
            {
                // Corrupted custom curve JSON — fall back to Balanced
            }
            return FlydigiFanCurveProfile.CreateBalanced();
        }

        /// <summary>Loads the custom curve profile from Bs2Pro settings, falling back to Balanced.</summary>
        private static FlydigiFanCurveProfile LoadCustomCurveProfile(FlydigiCoolerService flydigiService)
        {
            try
            {
                var bs2Settings = flydigiService.GetSettings();
                if (!string.IsNullOrEmpty(bs2Settings.CustomCurveJson))
                {
                    return FlydigiFanCurveProfile.FromJSON(bs2Settings.CustomCurveJson);
                }
            }
            catch
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
            else if (Bs2ProCurveEditor is not null && cbxBs2ProCurve?.SelectedIndex == 3)
            {
                // Re-entering Curve mode with Custom selected — re-show editor
                Bs2ProCurveEditorDivider.Visibility = Visibility.Visible;
                Bs2ProCurveEditor.Visibility = Visibility.Visible;
                try
                {
                    var flydigiService = App.GetService<FlydigiCoolerService>();
                    Bs2ProCurveEditor.SetCurve(LoadCustomCurveProfile(flydigiService));
                }
                catch
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

            bool isCustom = cbxBs2ProCurve.SelectedIndex == 3;
            Bs2ProCurveEditorDivider.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;
            Bs2ProCurveEditor.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;

            if (isCustom)
            {
                // Load custom curve or fall back to Balanced default
                try
                {
                    var flydigiService = App.GetService<FlydigiCoolerService>();
                    Bs2ProCurveEditor.SetCurve(LoadCustomCurveProfile(flydigiService));
                }
                catch
                {
                    Bs2ProCurveEditor.SetCurve(FlydigiFanCurveProfile.CreateBalanced());
                }
            }
        }

        private void btnBs2ProEditCurve_Click(object sender, RoutedEventArgs e)
        {
            // Load the current custom curve from Bs2ProSettings if it exists
            var bs2SettingsPath = System.IO.Path.Combine(Settings.Default.Path, "bs2pro_settings.json");
            FlydigiFanCurveProfile? seedProfile = FlydigiFanCurveProfile.CreateBalanced();

            if (System.IO.File.Exists(bs2SettingsPath))
            {
                try
                {
                    var bs2Settings = Newtonsoft.Json.JsonConvert.DeserializeObject<
                        Universal_x86_Tuning_Utility.Models.Bs2ProSettings>(
                        System.IO.File.ReadAllText(bs2SettingsPath));
                    if (bs2Settings != null && !string.IsNullOrEmpty(bs2Settings.CustomCurveJson))
                    {
                        seedProfile = Universal_x86_Tuning_Utility.Models.FlydigiFanCurveProfile.FromJSON(bs2Settings.CustomCurveJson);
                    }
                }
                catch { /* use default balanced */ }
            }

            var dialog = new Views.Windows.FlydigiCurveEditorWindow(seedProfile);
            if (dialog.ShowDialog() != true || dialog.EditedProfile == null)
                return;

            // Persist the custom curve to bs2pro_settings.json
            try
            {
                var bs2Settings = new Universal_x86_Tuning_Utility.Models.Bs2ProSettings();
                if (System.IO.File.Exists(bs2SettingsPath))
                {
                    bs2Settings = Newtonsoft.Json.JsonConvert.DeserializeObject<
                        Universal_x86_Tuning_Utility.Models.Bs2ProSettings>(
                        System.IO.File.ReadAllText(bs2SettingsPath)) ?? bs2Settings;
                }
                bs2Settings.CustomCurveJson = dialog.EditedProfile.ToJSON();
                bs2Settings.SelectedCurveProfile = "Custom";
                System.IO.File.WriteAllText(bs2SettingsPath,
                    Newtonsoft.Json.JsonConvert.SerializeObject(bs2Settings, Newtonsoft.Json.Formatting.Indented));
            }
            catch (Exception ex)
            {
                DiagnosticLogger.LogError(ex, "Failed to save custom curve from Adaptive Mode");
            }

            // Update the Flydigi page's smart control if it's running
            try
            {
                var flydigiPage = App.GetService<Views.Pages.FlydigiCooler>();
                // The Flydigi page will pick up the new curve on its next smart control tick
                // because it reads from the same settings file.
            }
            catch { /* Flydigi page may not be loaded */ }
        }

        // RGB helpers

        private static string GetBs2ProRgbModeFromIndex(int index)
        {
            return (index + 1) switch
            {
                1 => "Off",
                2 => "SmartTemp",
                3 => "Static",
                4 => "Breathing",
                5 => "Flowing",
                _ => "Static"
            };
        }

        private static int GetBs2ProRgbModeIndex(string mode)
        {
            return mode switch
            {
                "Off" => 0,
                "SmartTemp" => 1,
                "Static" => 2,
                "Breathing" => 3,
                "Flowing" => 4,
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
            "Silent" => 0,
            "Performance" => 2,
            "Full Speed" => 3,
            "Off" => 4,
            "Custom" => 5,
            _ => 1 // Balanced
        };

        private static string GetEcFanPresetFromIndex(int index) => index switch
        {
            0 => "Silent",
            2 => "Performance",
            3 => "Full Speed",
            4 => "Off",
            5 => "Custom",
            _ => "Balanced"
        };

        private static Universal_x86_Tuning_Utility.Models.EcFanCurve GetEcFanPresetCurve(string? name) => name switch
        {
            "Silent" => Universal_x86_Tuning_Utility.Models.EcFanCurve.CreateSilent(),
            "Balanced" => Universal_x86_Tuning_Utility.Models.EcFanCurve.CreateBalanced(),
            "Performance" => Universal_x86_Tuning_Utility.Models.EcFanCurve.CreatePerformance(),
            "Full Speed" => Universal_x86_Tuning_Utility.Models.EcFanCurve.CreateFullSpeed(),
            "Off" => Universal_x86_Tuning_Utility.Models.EcFanCurve.CreateOff(),
            _ => Universal_x86_Tuning_Utility.Models.EcFanCurve.CreateBalanced()
        };

        #endregion

        #region Keyboard RGB helpers

        private void cbxKbEnabled_Toggled(object sender, RoutedEventArgs e)
        {
            bool enabled = (bool)cbxKbEnabled.IsChecked;
            Settings.Default.AdaptiveKeyboardEnabled = enabled;
            Settings.Default.Save();

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
            if (spKbColor == null || spKbMultiColor == null || spKbGamingRest == null || spKbSpeed == null)
                return;

            if (cbxKbEffect.SelectedItem is not ComboBoxItem item)
            {
                sepKbColor.Visibility = Visibility.Collapsed;
                spKbColor.Visibility = Visibility.Collapsed;
                spKbMultiColor.Visibility = Visibility.Collapsed;
                spKbGamingRest.Visibility = Visibility.Collapsed;
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
                spKbGamingRest.Visibility = Visibility.Collapsed;
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
                spKbGamingRest.Visibility = Visibility.Collapsed;
                tbKbMultiColorTitle.Text = "Effect Colors";
                tbKbMultiColorSubtitle.Text = "7 colors required for this effect. Click a swatch to edit.";
                KbMultiColorPicker.SetColors(KbMultiColorPicker.Colors, 7);
            }
            else if (Views.Pages.Keyboard.IsMultiColor4Effect(ParseKbEffect(effect)))
            {
                spKbColor.Visibility = Visibility.Collapsed;
                spKbMultiColor.Visibility = Visibility.Visible;
                spKbGamingRest.Visibility = Visibility.Collapsed;
                tbKbMultiColorTitle.Text = "WASD & Arrow Keys";
                tbKbMultiColorSubtitle.Text = "4 colors for gaming keys. Click a swatch to edit.";
                KbMultiColorPicker.SetColors(KbMultiColorPicker.Colors, 4);
            }
            else if (Views.Pages.Keyboard.IsMultiColor4Plus1Effect(ParseKbEffect(effect)))
            {
                spKbColor.Visibility = Visibility.Collapsed;
                spKbMultiColor.Visibility = Visibility.Visible;
                spKbGamingRest.Visibility = Visibility.Visible;
                tbKbMultiColorTitle.Text = "WASD & Arrow Keys";
                tbKbMultiColorSubtitle.Text = "4 colors for gaming keys. Click a swatch to edit.";
                KbMultiColorPicker.SetColors(KbMultiColorPicker.Colors, 4);
            }
            else
            {
                // Single-color effects (Static, Rainbow, etc.)
                spKbColor.Visibility = Visibility.Visible;
                spKbMultiColor.Visibility = Visibility.Collapsed;
                spKbGamingRest.Visibility = Visibility.Collapsed;
            }

            // Speed: animated effects only (not Static, Rainbow, GamingMode, GamingModeFull)
            bool showSpeed = ParseKbEffect(effect) is KeyboardEffect effectEnum
                && effectEnum != KeyboardEffect.Static
                && effectEnum != KeyboardEffect.Rainbow
                && effectEnum != KeyboardEffect.GamingMode
                && effectEnum != KeyboardEffect.GamingModeFull;
            sepKbSpeed.Visibility = showSpeed ? Visibility.Visible : Visibility.Collapsed;
            spKbSpeed.Visibility = showSpeed ? Visibility.Visible : Visibility.Collapsed;
            // Direction: Wave effect only (other effects use different byte flags to be probed later)
            bool showDirection = effectEnum == KeyboardEffect.Wave;
            sepKbDirection.Visibility = showDirection ? Visibility.Visible : Visibility.Collapsed;
            grKbDirection.Visibility = showDirection ? Visibility.Visible : Visibility.Collapsed;

            // sepKbColor: show if any color control (Color, MultiColor, GamingRest) is visible.
            bool anyColorVisible = spKbColor.Visibility == Visibility.Visible
                || spKbMultiColor.Visibility == Visibility.Visible
                || spKbGamingRest.Visibility == Visibility.Visible;
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

        private void KbRestColorPicker_ColorChangedDelayed(object sender, EventArgs e)
        {
            // Rest color changed — will be picked up by update() loop's diff tracking
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
            "GamingModeFull" => KeyboardEffect.GamingModeFull,
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

        private static int GetKbIdleTimerIndex(int minutes) => minutes switch
        {
            <= 5 => 0,
            <= 10 => 1,
            <= 15 => 2,
            <= 30 => 3,
            <= 60 => 4,
            <= 120 => 5,
            _ => 6
        };

        private static int GetKbIdleTimerMinutes(int index) => index switch
        {
            0 => 5,
            1 => 10,
            2 => 15,
            3 => 30,
            4 => 60,
            5 => 120,
            _ => 180
        };

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
            "GamingModeFull" => 14,
            "Music" => 15,
            _ => 0
        };

        private static string GetKbEffectFromIndex(int index) => index switch
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
            14 => "GamingModeFull",
            15 => "Music",
            _ => "Static"
        };

        private void btnKbEditPerKey_Click(object sender, RoutedEventArgs e)
        {
            // Load current settings from KeyboardSettingsService (the source of truth)
            var settings = KeyboardSettingsService.Load();

            var dialog = new Views.Windows.KeyboardPerKeyDialog(settings);
            if (dialog.ShowDialog() == true && dialog.Applied && dialog.ResultColors != null)
            {
                // Save the updated settings
                KeyboardSettingsService.Save(settings);

                // Apply to device if override is active
                if (_deviceApplier != null && _deviceApplier.IsKeyboardOverridden)
                {
                    // Re-apply the current preset with the new per-key colors
                    bool perKeyMode = cbxKbMode.SelectedIndex == 1;
                    int brightness = (int)sdKbBrightness.Value;
                    string effectMode = GetKbEffectFromIndex(cbxKbEffect.SelectedIndex);
                    byte effectSpeed = (byte)(10 - Math.Clamp((int)sdKbSpeed.Value, 1, 10));
                    byte colorR = KbColorPicker.SelectedColor.R;
                    byte colorG = KbColorPicker.SelectedColor.G;
                    byte colorB = KbColorPicker.SelectedColor.B;

                    // Serialize per-key colors to string
                    var sb = new System.Text.StringBuilder();
                    var colors = dialog.ResultColors;
                    for (int i = 0; i < 126; i++)
                    {
                        if (i > 0) sb.Append('|');
                        (byte R, byte G, byte B) c = colors.TryGetValue(i, out var val) ? val : ((byte)0, (byte)0, (byte)0);
                        sb.Append($"{c.R},{c.G},{c.B}");
                    }
                    string perKeyColorsStr = sb.ToString();

                    _deviceApplier.ApplyKeyboardFromPreset(
                        perKeyMode, brightness, false, 0,
                        effectMode, effectSpeed, _kbDirection,
                        colorR, colorG, colorB, "", perKeyColorsStr,
                        KbRestColorPicker.SelectedColor.R, KbRestColorPicker.SelectedColor.G, KbRestColorPicker.SelectedColor.B);

                    // Update tracking
                    lastKbPerKeyMode = perKeyMode;
                    lastKbBrightness = brightness;
                    lastKbEffectMode = effectMode;
                    lastKbEffectSpeed = effectSpeed;
                    lastKbDirection = _kbDirection;
                    lastKbColorR = colorR;
                    lastKbColorG = colorG;
                    lastKbColorB = colorB;
                }
            }
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

        #region CPU / GPU / Light Bar event handlers

        /// <summary>
        /// Shows/hide AMD vs Intel CPU controls based on the detected CPU type.
        /// </summary>
        private void UpdateAdpCpuUI()
        {
            if (Family.TYPE == Family.ProcessorType.Intel)
            {
                // Hide AMD-specific controls
                AdpGridTjMax.Visibility = Visibility.Collapsed;
                AdpDividerAfterTjMax.Visibility = Visibility.Collapsed;
                AdpGridCoAll.Visibility = Visibility.Collapsed;
                AdpDividerAfterCoAll.Visibility = Visibility.Collapsed;
            }
            else
            {
                // Hide Intel-specific controls
                AdpGridIntelPl1.Visibility = Visibility.Collapsed;
                AdpDividerAfterIntelPl1.Visibility = Visibility.Collapsed;
                AdpGridIntelPl2.Visibility = Visibility.Collapsed;
                AdpDividerAfterIntelPl2.Visibility = Visibility.Collapsed;
                AdpGridIntelUndervolt.Visibility = Visibility.Collapsed;
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
                case "AdpSdCoAll":
                    AdpLblCoAll.Text = $"{slider.Value:F0}";
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
            bool enabled = (bool)cbxCpuEnabled.IsChecked;
            Settings.Default.AdaptiveCpuEnabled = enabled;
            Settings.Default.Save();

            // Reset tracking so the next update() tick force-applies CPU settings
            if (enabled)
            {
                lastCpuSustained = 0;
                lastCpuBoost = 0;
                lastCpuTjMax = 0;
                lastCpuCo = 0;
                lastCpuPl1 = 0;
                lastCpuPl2 = 0;
                lastCpuUndervolt = 0;
            }
        }

        private void cbxGpuEnabled_Toggled(object sender, RoutedEventArgs e)
        {
            bool enabled = (bool)cbxGpuEnabled.IsChecked;
            Settings.Default.AdaptiveGpuEnabled = enabled;
            Settings.Default.Save();

            // Reset tracking so the next update() tick force-applies GPU settings
            if (enabled)
            {
                lastGpuTgp = 0;
                lastGpuDynamicBoost = false;
                lastGpuDbOffset = 0;
                lastGpuCoreClock = 0;
                lastGpuMemClock = 0;
                lastGpuVfCurveName = string.Empty;
            }
        }

        private void cbxLbEnabled_Toggled(object sender, RoutedEventArgs e)
        {
            bool enabled = (bool)cbxLbEnabled.IsChecked;
            Settings.Default.AdaptiveLbEnabled = enabled;
            Settings.Default.Save();

            // Reset tracking so the next update() tick force-applies Light Bar settings
            if (enabled)
            {
                lastLbPower = false;
                lastLbColorR = 0;
                lastLbColorG = 0;
                lastLbColorB = 0;
                lastLbBrightness = 0;
                lastLbEffect = 0;
                lastLbSpeed = 0;
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

            // Reload presets from disk — editor auto-saves independently
            var presets = Universal_x86_Tuning_Utility.Models.VfCurvePresetStore.Load();

            // Find active preset (first with changes)
            Dictionary<int, int>? offsets = null;
            foreach (var preset in presets)
            {
                if (!string.IsNullOrEmpty(preset.Name) && preset.HasChanges)
                {
                    offsets = preset.Offsets;
                    break;
                }
            }

            // Open non-modal curve editor window
            _vfEditorWindow = new Views.Windows.VfCurveEditorWindow(offsets);
            _vfEditorWindow.Owner = Application.Current.MainWindow;
            _vfEditorWindow.Closed += (_, _) => _vfEditorWindow = null;
            _vfEditorWindow.CurveChanged += (_, _) =>
            {
                _adpUserTouchedCoreSlider = false; // User went back to curve — reclaim from slider
                updateAdpCoreLabel();
                // Only apply to hardware if Adaptive Mode is running AND GPU Settings is enabled
                if (start && (bool)cbxGpuEnabled.IsChecked)
                    ApplyActiveVfCurveToGpu();
            };
            _vfEditorWindow.Show();
        }

        private void AdpTsLightBarPower_Toggled(object sender, RoutedEventArgs e)
        {
            bool isOn = AdpTsLightBarPower.IsChecked == true;
            if (_hidService == null)
                return;

            byte r = AdpLbColorPicker.SelectedColor.R;
            byte g = AdpLbColorPicker.SelectedColor.G;
            byte b = AdpLbColorPicker.SelectedColor.B;
            int brightness = (int)AdpLbBrightnessSlider.Value;
            int effect = AdpLbEffect.SelectedIndex;
            int speed = (int)AdpLbSpeedSlider.Value;

            try
            {
                if (isOn)
                {
                    _hidService.LightBarTurnOn(r, g, b, brightness);
                    _hidService.LightBarSetEffect((byte)effect, (byte)speed, brightness);
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
            // Will be picked up by update() loop diff tracking
        }

        private void AdpLbBrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (AdpLbBrightnessValueText != null)
                AdpLbBrightnessValueText.Text = ((int)AdpLbBrightnessSlider.Value).ToString();
        }

        private void AdpLbEffect_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Guard against calls before XAML fields are loaded (e.g. constructor, Page_Loaded).
            if (AdpSpLbSpeed == null || AdpLbEffect == null)
                return;

            // Show/hide speed control (hidden for Static = index 0)
            AdpSpLbSpeed.Visibility = AdpLbEffect.SelectedIndex != 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void AdpLbSpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (AdpLbSpeedValueText != null)
                AdpLbSpeedValueText.Text = ((int)AdpLbSpeedSlider.Value).ToString();
        }

        #endregion

        #region Per-game profile helpers (gaps)

        /// <summary>True when the currently loaded per-game profile has a V-F curve with changes.</summary>
        private bool AdpVfCurveActive
        {
            get
            {
                if (string.IsNullOrEmpty(_adpActiveCurveName))
                    return false;
                try
                {
                    var presets = Universal_x86_Tuning_Utility.Models.VfCurvePresetStore.Load();
                    var target = presets.FirstOrDefault(p => p.Name == _adpActiveCurveName);
                    return target != null && target.HasChanges;
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>Updates the core clock label to show "Curve" when active, or slider value otherwise.</summary>
        private void updateAdpCoreLabel()
        {
            AdpLblNvCore.Text = (AdpVfCurveActive && !_adpUserTouchedCoreSlider)
                ? "Curve"
                : $"{AdpSdNvCore.Value:F0} MHz";
        }

        /// <summary>Applies the active V-F curve preset to the GPU hardware. Used by Adaptive page (no Apply button).</summary>
        private void ApplyActiveVfCurveToGpu()
        {
            try
            {
                if (string.IsNullOrEmpty(_adpActiveCurveName))
                    return;

                var presets = Universal_x86_Tuning_Utility.Models.VfCurvePresetStore.Load();
                var target = presets.FirstOrDefault(p => p.Name == _adpActiveCurveName);
                if (target == null || !target.HasChanges)
                    return;

                // Apply EC power limit BEFORE the curve write (same as Presets page).
                // The NVIDIA driver on Blackwell laptops may check the EC power ceiling
                // when applying VF offsets and further reduce power if it sees a low ceiling.
                int tgp = (int)AdpSdNvPower.Value;
                bool dbEnabled = AdpTogDynamicBoost.IsChecked == true;
                int dbOffset = dbEnabled ? (int)AdpSdNvDbOffset.Value : 0;
                NvTuning.SetPowerLimit(tgp + dbOffset, dbOffset);

                NvTuning.ApplyVfCurveOffsets_ClockBoostTable(target.Offsets);

                // Re-apply power limit after in case the driver overrides the EC ceiling.
                NvTuning.SetPowerLimit(tgp + dbOffset, dbOffset);
            }
            catch (Exception ex)
            {
                DiagnosticLogger.LogError(ex, "Failed to apply V-F curve to GPU in Adaptive Mode");
            }
        }

        /// <summary>Gap 1: Restores EC Fan custom duties from per-game profile into FanControlSettings.</summary>
        private void ApplyEcFanCustomDuties(string unifiedPreset, int[]? unifiedDuties,
            string cpuPreset, int[]? cpuDuties,
            string gpuPreset, int[]? gpuDuties)
        {
            try
            {
                var settings = FanControlSettingsService.Load();

                if (unifiedPreset == "Custom" && unifiedDuties != null)
                    settings.UnifiedDuties = unifiedDuties.ToArray();

                if (cpuPreset == "Custom" && cpuDuties != null)
                    settings.CpuDuties = cpuDuties.ToArray();

                if (gpuPreset == "Custom" && gpuDuties != null)
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
                if (GetBs2ProCurveProfileId(cbxBs2ProCurve.SelectedIndex) != "Custom")
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

        /// <summary>Gap 4: Returns the name of the currently active V-F curve preset, or empty string if none.</summary>
        private static string GetActiveVfCurveName()
        {
            try
            {
                var presets = Universal_x86_Tuning_Utility.Models.VfCurvePresetStore.Load();
                foreach (var preset in presets)
                {
                    if (!string.IsNullOrEmpty(preset.Name) && preset.HasChanges)
                        return preset.Name;
                }
            }
            catch (Exception ex)
            {
                DiagnosticLogger.LogError(ex, "Failed to get active V-F curve name");
            }
            return string.Empty;
        }

        /// <summary>Gap 4: Applies the named V-F curve preset to the GPU hardware, or resets to stock if name is empty.</summary>
        private static void ApplyVfCurveByName(string curveName)
        {
            try
            {
                if (string.IsNullOrEmpty(curveName))
                {
                    // Profile has no curve — reset GPU to stock to clear any previous game's curve
                    NvTuning.ResetVfCurveOffsets();
                    return;
                }

                var presets = Universal_x86_Tuning_Utility.Models.VfCurvePresetStore.Load();
                var target = presets.FirstOrDefault(p => p.Name == curveName);
                if (target == null || !target.HasChanges)
                {
                    // Named curve not found — reset to stock
                    NvTuning.ResetVfCurveOffsets();
                    return;
                }

                // Apply EC power limit BEFORE the curve write (same as Presets page).
                NvTuning.ApplyVfCurveOffsets_ClockBoostTable(target.Offsets);
            }
            catch (Exception ex)
            {
                DiagnosticLogger.LogError(ex, "Failed to apply V-F curve from preset");
            }
        }

        #endregion
    }
}
