using GameLib.Core;
using GameLib;
using LibreHardwareMonitor.Hardware;
using RTSSSharedMemoryNET;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.DirectoryServices.ActiveDirectory;
using System.Linq;
using System.Linq.Expressions;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Universal_x86_Tuning_Utility.Properties;
using Universal_x86_Tuning_Utility.Scripts;
using Universal_x86_Tuning_Utility.Scripts.Adaptive;
using Universal_x86_Tuning_Utility.Models;
using Universal_x86_Tuning_Utility.Scripts.Misc;
using Universal_x86_Tuning_Utility.Services;
using static System.Net.Mime.MediaTypeNames;
using GameLib.Plugin.Steam.Model;
using Windows.ApplicationModel.Search;
using Windows.Gaming.Preview.GamesEnumeration;
using System.Management;
using RyzenSmu;
using Universal_x86_Tuning_Utility.Scripts.GPUs.AMD;
using static Universal_x86_Tuning_Utility.Scripts.Game_Manager;
using System.ComponentModel;

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

            if (!Settings.Default.isASUS) sdAsusPower.Visibility = Visibility.Collapsed;

            sdHydroUI.Visibility = WaterCoolerHardwareDetector.IsSupportedHardware() ? Visibility.Visible : Visibility.Collapsed;
            UpdateFlydigiCardVisibility();
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

            // BS1 has no RGB — hide the RGB mode selector and RGB color panels
            if (spBs2ProRgbMode != null)
                spBs2ProRgbMode.Visibility = isBs1 ? Visibility.Collapsed : Visibility.Visible;
            if (spBs2ProRgb != null)
                spBs2ProRgb.Visibility = isBs1 ? Visibility.Collapsed : Visibility.Visible;

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

                if (radeonGpuCount <= 0)
                {
                    sdTBOiGPU.Visibility = Visibility.Collapsed;
                    sdADLX.Visibility = Visibility.Collapsed;
                }

                if (nvidiaGpuCount < 1) sdNVIDIA.Visibility = Visibility.Collapsed;

                bool watercoolerActive = App.GetService<WaterCoolerService>().IsConnected
                    || App.GetService<FlydigiCoolerService>().IsConnected;
                nudPowerLimit.Value = Family.GetRecommendedPowerLimit(watercoolerActive);
                nudMaxGfxClk.Value = 1900;
                nudMinGfxClk.Value = 400;
                nudTemp.Value = 95;
                nudMinCpuClk.Value = 1500;
                nudNVMaxCore.Value = 4000;
                nudWindowsMinState.Value = 5;
                nudWindowsMaxState.Value = 100;
                nudWindowsMaxFrequency.Value = 5000;
                nudWindowsEpp.Value = 50;
                nudWindowsCoreParking.Value = 100;
                nudWindowsMaxUnparkedCores.Value = 100;
                cbxWindowsBoostMode.SelectedIndex = 0;
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
                            Temp = (int)nudTemp.Value,
                            Power = (int)nudPowerLimit.Value,
                            CO = (int)nudCurve.Value,
                            minGFX = (int)nudMinGfxClk.Value,
                            MaxGFX = (int)nudMaxGfxClk.Value,
                            minCPU = (int)nudMinCpuClk.Value,
                            isCO = (bool)cbCurve.IsChecked,
                            isGFX = (bool)tsTBOiGPU.IsChecked,
                            rsr = (int)nudRSR.Value,
                            boost = (int)nudBoost.Value,
                            imageSharp = (int)nudImageSharp.Value,
                            isRadeonGraphics = (bool)tsRadeonGraph.IsChecked,
                            isRSR = (bool)cbRSR.IsChecked,
                            isBoost = (bool)cbBoost.IsChecked,
                            isAntiLag = (bool)cbAntiLag.IsChecked,
                            isImageSharp = (bool)cbImageSharp.IsChecked,
                            isSync = (bool)cbSync.IsChecked,
                            isNVIDIA = (bool)tsNV.IsChecked,
                            nvMaxCoreClk = (int)nudNVMaxCore.Value,
                            nvCoreClk = (int)nudNVCore.Value,
                            nvMemClk = (int)nudNVMem.Value,
                            asusPowerProfile = (int)cbxAsusPower.SelectedIndex,
                            windowsBoostMode = cbxWindowsBoostMode.SelectedIndex,
                            isWindowsMinState = (bool)cbWindowsMinState.IsChecked,
                            windowsMinState = (int)nudWindowsMinState.Value,
                            isWindowsMaxState = (bool)cbWindowsMaxState.IsChecked,
                            windowsMaxState = (int)nudWindowsMaxState.Value,
                            isWindowsMaxFrequency = (bool)cbWindowsMaxFrequency.IsChecked,
                            windowsMaxFrequency = (int)nudWindowsMaxFrequency.Value,
                            isWindowsEpp = (bool)cbWindowsEpp.IsChecked,
                            windowsEpp = (int)nudWindowsEpp.Value,
                            isWindowsCoreParking = (bool)cbWindowsCoreParking.IsChecked,
                            windowsCoreParking = (int)nudWindowsCoreParking.Value,
                            isWindowsMaxUnparkedCores = (bool)cbWindowsMaxUnparkedCores.IsChecked,
                            windowsMaxUnparkedCores = (int)nudWindowsMaxUnparkedCores.Value,
                            isMag = (bool)tsUXTUSR.IsChecked,
                            isVsync = (bool)cbVSync.IsChecked,
                            isRecap = (bool)cbAutoCap.IsChecked,
                            Sharpness = (int)nudSharp.Value,
                            ResScaleIndex = (int)cbxResScale.SelectedIndex,
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
                            isAutoSwitch = (bool)tsAutoSwitch.IsChecked
                        };
                        adaptivePresetManager.SavePreset(item.gameName, preset);
                    }

                    if (Family.TYPE == Family.ProcessorType.Intel)
                    {
                        spCO.Visibility = Visibility.Collapsed;
                        sdTBOiGPU.Visibility = Visibility.Collapsed;
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

        private void SizeSlider_TouchDown(object sender, TouchEventArgs e)
        {
            // Mark event as handled
            e.Handled = true;
        }
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
        int i = 0;

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
                    // Preserve user's explicit override checkbox state so that setting
                    // IsChecked below doesn't fire Toggled and unexpectedly enable/disable
                    // the DeviceApplier override.
                    bool wasBs2ProEnabled = (bool)cbxBs2ProEnabled.IsChecked;
                    bool wasWcEnabled = (bool)cbxWcEnabled.IsChecked;
                    tsAutoSwitch.IsChecked = myPreset.isAutoSwitch;

                    nudTemp.Value = myPreset.Temp;
                    nudPowerLimit.Value = myPreset.Power;
                    nudCurve.Value = myPreset.CO;
                    nudMaxGfxClk.Value = myPreset.MaxGFX;
                    nudMinGfxClk.Value = myPreset.minGFX;
                    nudMinCpuClk.Value = myPreset.minCPU;

                    cbCurve.IsChecked = myPreset.isCO;
                    tsTBOiGPU.IsChecked = myPreset.isGFX;

                    tsRadeonGraph.IsChecked = myPreset.isRadeonGraphics;
                    cbAntiLag.IsChecked = myPreset.isAntiLag;
                    cbRSR.IsChecked = myPreset.isRSR;
                    cbBoost.IsChecked = myPreset.isBoost;
                    cbImageSharp.IsChecked = myPreset.isImageSharp;
                    cbSync.IsChecked = myPreset.isSync;
                    nudRSR.Value = myPreset.rsr;
                    nudBoost.Value = myPreset.boost;
                    nudImageSharp.Value = myPreset.imageSharp;

                    tsNV.IsChecked = myPreset.isNVIDIA;
                    nudNVMaxCore.Value = myPreset.nvMaxCoreClk;
                    nudNVCore.Value = myPreset.nvCoreClk;
                    nudNVMem.Value = myPreset.nvMemClk;

                    cbxAsusPower.SelectedIndex = myPreset.asusPowerProfile;
                    cbxWindowsBoostMode.SelectedIndex = myPreset.windowsBoostMode;
                    cbWindowsMinState.IsChecked = myPreset.isWindowsMinState;
                    nudWindowsMinState.Value = myPreset.windowsMinState;
                    cbWindowsMaxState.IsChecked = myPreset.isWindowsMaxState;
                    nudWindowsMaxState.Value = myPreset.windowsMaxState;
                    cbWindowsMaxFrequency.IsChecked = myPreset.isWindowsMaxFrequency;
                    nudWindowsMaxFrequency.Value = myPreset.windowsMaxFrequency;
                    cbWindowsEpp.IsChecked = myPreset.isWindowsEpp;
                    nudWindowsEpp.Value = myPreset.windowsEpp;
                    cbWindowsCoreParking.IsChecked = myPreset.isWindowsCoreParking;
                    nudWindowsCoreParking.Value = myPreset.windowsCoreParking;
                    cbWindowsMaxUnparkedCores.IsChecked = myPreset.isWindowsMaxUnparkedCores;
                    nudWindowsMaxUnparkedCores.Value = myPreset.windowsMaxUnparkedCores;

                    tsUXTUSR.IsChecked = myPreset.isMag;
                    cbVSync.IsChecked = myPreset.isVsync;
                    cbAutoCap.IsChecked = myPreset.isRecap;
                    nudSharp.Value = myPreset.Sharpness;
                    cbxResScale.SelectedIndex = myPreset.ResScaleIndex;

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
                    }
                    if (Enum.TryParse<RgbColor>(myPreset.WcRgbColor, true, out var rcLoad))
                        cbxWcRgbColor.SelectedIndex = GetRgbColorIndex(rcLoad);

                    // BS2 Pro
                    cbxBs2ProFanMode.SelectedIndex = GetBs2ProFanModeIndex(myPreset.Bs2ProFanMode);
                    UpdateBs2ProModeUI();
                    cbxBs2ProGear.SelectedIndex = Math.Clamp(myPreset.Bs2ProGear - 1, 0, 3);
                    nudBs2ProRpm.Value = Math.Clamp((int)myPreset.Bs2ProRpm, 1300, 4000);
                    cbxBs2ProCurve.SelectedIndex = GetBs2ProCurveIndex(myPreset.Bs2ProCurveProfileId);

                    // BS2 Pro RGB
                    cbxBs2ProRgbMode.SelectedIndex = GetBs2ProRgbModeIndex(myPreset.Bs2ProRgbMode);
                    UpdateBs2ProRgbUI();
                    nudBs2ProRgbR.Value = myPreset.Bs2ProRgbR;
                    nudBs2ProRgbG.Value = myPreset.Bs2ProRgbG;
                    nudBs2ProRgbB.Value = myPreset.Bs2ProRgbB;
                    nudBs2ProBrightness.Value = myPreset.Bs2ProBrightness;

                    // Restore user's override checkbox state (don't let preset override it)
                    cbxWcEnabled.IsChecked = wasWcEnabled;
                    cbxBs2ProEnabled.IsChecked = wasBs2ProEnabled;

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
                    Temp = (int)nudTemp.Value,
                    Power = (int)nudPowerLimit.Value,
                    CO = (int)nudCurve.Value,
                    minGFX = (int)nudMinGfxClk.Value,
                    MaxGFX = (int)nudMaxGfxClk.Value,
                    minCPU = (int)nudMinCpuClk.Value,
                    isCO = (bool)cbCurve.IsChecked,
                    isGFX = (bool)tsTBOiGPU.IsChecked,
                    rsr = (int)nudRSR.Value,
                    boost = (int)nudBoost.Value,
                    imageSharp = (int)nudImageSharp.Value,
                    isRadeonGraphics = (bool)tsRadeonGraph.IsChecked,
                    isRSR = (bool)cbRSR.IsChecked,
                    isBoost = (bool)cbBoost.IsChecked,
                    isAntiLag = (bool)cbAntiLag.IsChecked,
                    isImageSharp = (bool)cbImageSharp.IsChecked,
                    isSync = (bool)cbSync.IsChecked,
                    isNVIDIA = (bool)tsNV.IsChecked,
                    nvMaxCoreClk = (int)nudNVMaxCore.Value,
                    nvCoreClk = (int)nudNVCore.Value,
                    nvMemClk = (int)nudNVMem.Value,
                    asusPowerProfile = (int)cbxAsusPower.SelectedIndex,
                    windowsBoostMode = cbxWindowsBoostMode.SelectedIndex,
                    isWindowsMinState = (bool)cbWindowsMinState.IsChecked,
                    windowsMinState = (int)nudWindowsMinState.Value,
                    isWindowsMaxState = (bool)cbWindowsMaxState.IsChecked,
                    windowsMaxState = (int)nudWindowsMaxState.Value,
                    isWindowsMaxFrequency = (bool)cbWindowsMaxFrequency.IsChecked,
                    windowsMaxFrequency = (int)nudWindowsMaxFrequency.Value,
                    isWindowsEpp = (bool)cbWindowsEpp.IsChecked,
                    windowsEpp = (int)nudWindowsEpp.Value,
                    isWindowsCoreParking = (bool)cbWindowsCoreParking.IsChecked,
                    windowsCoreParking = (int)nudWindowsCoreParking.Value,
                    isWindowsMaxUnparkedCores = (bool)cbWindowsMaxUnparkedCores.IsChecked,
                    windowsMaxUnparkedCores = (int)nudWindowsMaxUnparkedCores.Value,
                    isMag = (bool)tsUXTUSR.IsChecked,
                    isVsync = (bool)cbVSync.IsChecked,
                    isRecap = (bool)cbAutoCap.IsChecked,
                    Sharpness = (int)nudSharp.Value,
                    ResScaleIndex = (int)cbxResScale.SelectedIndex,
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
                    Bs2ProRgbMode = GetBs2ProRgbModeFromIndex(cbxBs2ProRgbMode.SelectedIndex),
                    Bs2ProRgbR = (byte)nudBs2ProRgbR.Value,
                    Bs2ProRgbG = (byte)nudBs2ProRgbG.Value,
                    Bs2ProRgbB = (byte)nudBs2ProRgbB.Value,
                    Bs2ProBrightness = (byte)nudBs2ProBrightness.Value,
                    isAutoSwitch = (bool)tsAutoSwitch.IsChecked
                };
                adaptivePresetManager.SavePreset(presetName, preset);
            }
            catch (Exception ex)
            {
                DiagnosticLogger.LogError(ex, "Failed to save adaptive preset");
            }
        }

        private static LASTINPUTINFO lastInput = new LASTINPUTINFO();

        private static int minCPUClock = 1440;

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

        private static int newMinCPUClock = 1440;
        private async void sensors_Tick(object sender, EventArgs e)
        {
            try
            {
                if (start == true)
                {
                    await Task.Run(() =>
                    {
                        if (Family.TYPE == Family.ProcessorType.Intel) CPUTemp = (int)GetSensor.GetCPUInfo(SensorType.Temperature, "Package");
                        else CPUTemp = (int)GetSensor.GetCPUInfo(SensorType.Temperature, "Core");
                        CPULoad = (int)GetSensor.GetCPUInfo(SensorType.Load, "Total");

                        int clockTotal = 0;
                        int clockSamples = 0;
                        for (int core = 1; core <= coreCount; core++)
                        {
                            int clock = (int)GetSensor.GetCPUInfo(SensorType.Clock, $"Core #{core}");
                            if (clock <= 0)
                                continue;
                            clockTotal += clock;
                            clockSamples++;
                        }

                        CPUClock = clockSamples > 0 ? clockTotal / clockSamples : 0;

                        //CPUPower = (int)GetSensor.getCPUInfo(SensorType.Power, "Package");

                        if (radeonGpuCount > 0)
                        {
                            GPULoad = ADLXBackend.GetGPUMetrics(0, 7);
                            GPUClock = ADLXBackend.GetGPUMetrics(0, 0);
                            GPUMemClock = ADLXBackend.GetGPUMetrics(0, 1);
                        }

                        isGameRunning();
                    });

                    if (nvidiaGpuCount < 1) sdNVIDIA.Visibility = Visibility.Collapsed;

                    minCPUClock = Convert.ToInt32(nudMinCpuClk.Value);
                    if (CPULoad < (100 / coreCount) + 5) newMinCPUClock = minCPUClock + 500;
                    else newMinCPUClock = minCPUClock;


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

        string lastCPU = "";
        string lastCO = "";
        string lastiGPU = "";
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
        string lastWindowsProcessorPower = "";
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
                    if (i < 2)
                    {
                        CPUControl.UpdatePowerLimit(CPUTemp, CPULoad, (int)nudPowerLimit.Value, (int)nudPowerLimit.Value - 5, (int)nudTemp.Value);
                        CPUControl.UpdatePowerLimit(CPUTemp, CPULoad, (int)nudPowerLimit.Value, (int)nudPowerLimit.Value - 5, (int)nudTemp.Value);
                        CPUControl.UpdatePowerLimit(CPUTemp, CPULoad, (int)nudPowerLimit.Value, (int)nudPowerLimit.Value - 5, (int)nudTemp.Value);
                        i++;
                    }
                    else
                    {
                        CPUControl.UpdatePowerLimit(CPUTemp, CPULoad, (int)nudPowerLimit.Value, 8, (int)nudTemp.Value);

                        if (cbCurve.IsChecked == true) CPUControl.CurveOptimiserLimit(CPULoad, (int)nudCurve.Value);

                        if (tsTBOiGPU.IsChecked == true) iGPUControl.UpdateiGPUClock((int)nudMaxGfxClk.Value, (int)nudMinGfxClk.Value, (int)nudTemp.Value, CPUPower, CPUTemp, GPUClock, GPULoad, GPUMemClock, CPUClock, minCPUClock);

                        string commandString = "";

                        commandString = commandString + $"--UXTUSR={tsUXTUSR.IsChecked}-{cbVSync.IsChecked}-{nudSharp.Value / 100}-{cbxResScale.SelectedIndex}-{cbAutoCap.IsChecked} ";

                        if (Settings.Default.isASUS)
                        {
                            if (cbxAsusPower.SelectedIndex > 0) commandString = commandString + $"--ASUS-Power={cbxAsusPower.SelectedIndex} ";
                        }

                        var windowsProcessorPower = GetWindowsProcessorPowerCommand();
                        if (windowsProcessorPower != lastWindowsProcessorPower)
                        {
                            commandString = commandString + windowsProcessorPower;
                            lastWindowsProcessorPower = windowsProcessorPower;
                        }

                        if (CPUControl.cpuCommand != lastCPU)
                        {
                            commandString = commandString + CPUControl.cpuCommand;
                            lastCPU = CPUControl.cpuCommand;
                        }

                        if (CPUControl.coCommand != null && CPUControl.coCommand != "" && cbCurve.IsChecked == true && CPUControl.coCommand != lastCO)
                        {
                            commandString = commandString + CPUControl.coCommand;
                            lastCO = CPUControl.coCommand;
                        }

                        if (iGPUControl.commmand != null && iGPUControl.commmand != "" && tsTBOiGPU.IsChecked == true && iGPUControl.commmand != lastiGPU)
                        {
                            commandString = commandString + iGPUControl.commmand;
                            lastiGPU = iGPUControl.commmand;
                        }

                        if (tsRadeonGraph.IsChecked == true)
                        {
                            if (cbAntiLag.IsChecked == true) commandString = commandString + $"--ADLX-Lag=0-true --ADLX-Lag=1-true ";
                            else commandString = commandString + $"--ADLX-Lag=0-false --ADLX-Lag=1-false ";

                            if (cbRSR.IsChecked == true) commandString = commandString + $"--ADLX-RSR=true-{(int)nudRSR.Value} ";
                            else commandString = commandString + $"--ADLX-RSR=false-{(int)nudRSR.Value} ";

                            if (cbBoost.IsChecked == true) commandString = commandString + $"--ADLX-Boost=0-true-{(int)nudBoost.Value} --ADLX-Boost=1-true-{(int)nudBoost.Value} ";
                            else commandString = commandString + $"--ADLX-Boost=0-false-{(int)nudBoost.Value} --ADLX-Boost=1-false-{(int)nudBoost.Value} ";

                            if (cbImageSharp.IsChecked == true) commandString = commandString + $"--ADLX-ImageSharp=0-true-{(int)nudImageSharp.Value} --ADLX-ImageSharp=1-true-{(int)nudImageSharp.Value} ";
                            else commandString = commandString + $"--ADLX-ImageSharp=0-false-{(int)nudImageSharp.Value} --ADLX-ImageSharp=1-false-{(int)nudImageSharp.Value} ";

                            if (cbSync.IsChecked == true) commandString = commandString + $"--ADLX-Sync=0-true --ADLX-Sync=1-true ";
                            else commandString = commandString + $"--ADLX-Sync=0-false --ADLX-Sync=1-false ";
                        }

                        if (tsNV.IsChecked == true)
                        {
                            commandString = commandString + $"--NVIDIA-Clocks={nudNVMaxCore.Value}-{nudNVCore.Value}-{nudNVMem.Value} ";
                        }

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
                            byte bs2Brightness = (byte)nudBs2ProBrightness.Value;

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

                                // Use DeviceApplier for fan + RGB (diff-based to avoid redundant writes)
                                // Only compare gear/rpm when they're relevant for the current mode.
                                bool fanChanged = bs2Mode != lastBs2ProFanModeText ||
                                    (bs2Mode == "Gear" && gear != lastBs2ProGear) ||
                                    (bs2Mode == "Rpm" && rpm != lastBs2ProRpm);
                                bool rgbChanged = bs2RgbMode != lastBs2ProRgbMode ||
                                    bs2R != lastBs2ProRgbR || bs2G != lastBs2ProRgbG ||
                                    bs2B != lastBs2ProRgbB || bs2Brightness != lastBs2ProBrightness;

                                if (fanChanged || rgbChanged)
                                {
                                    // Only send commands for the portion that actually changed to avoid
                                    // unnecessary re-transmits that can briefly interrupt the device's RGB effect.
                                    if (fanChanged)
                                        await _deviceApplier!.ApplyFlydigiFanAsync(bs2Mode, gear, rpm);
                                    if (rgbChanged)
                                        await _deviceApplier!.ApplyFlydigiRgbAsync(bs2RgbMode, bs2R, bs2G, bs2B, bs2Brightness);

                                    // Fire preset-applied event so the Flydigi page can sync its UI.
                                    _deviceApplier.RaiseFlydigiPresetApplied(bs2Mode, gear, rpm, bs2RgbMode, bs2R, bs2G, bs2B, bs2Brightness);

                                    lastBs2ProFanModeText = bs2Mode;
                                    lastBs2ProGear = gear ?? lastBs2ProGear;
                                    lastBs2ProRpm = rpm ?? lastBs2ProRpm;
                                    lastBs2ProRgbMode = bs2RgbMode;
                                    lastBs2ProRgbR = bs2R;
                                    lastBs2ProRgbG = bs2G;
                                    lastBs2ProRgbB = bs2B;
                                    lastBs2ProBrightness = bs2Brightness;
                                }
                            }
                        }

                        if (commandString != null && commandString != "") await RyzenAdj_To_UXTU.TranslateAsync(commandString, appliedName: "Adaptive Mode", localizeAppliedName: true);
                    }

                    if (RTSS.RTSSRunning() && tsRTSS.IsChecked == true) RTSS.setRTSSFPSLimit((int)nudRTSS.Value);
                    

                    //if (RTSS.RTSSRunning())
                    //{
                    //    int i = 0;
                    //    bool found = false;
                    //    do
                    //    {
                    //        AppFlags appFlag = RunningGames.appFlags[i];
                    //        var appEntries = OSD.GetAppEntries(appFlag);
                    //        foreach (var app in appEntries)
                    //        {
                    //            found = true;
                    //            osd.Update($"{RunningGames.appFlags[i]} {app.InstantaneousFrames}FPS {app.InstantaneousFrameTime.Milliseconds}ms");
                    //        }
                    //        i++;
                    //    } while (i < RunningGames.appFlags.Count && found == false);
                    //}
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

        private string GetWindowsProcessorPowerCommand()
        {
            var boostMode = cbxWindowsBoostMode.SelectedIndex > 0 ? cbxWindowsBoostMode.SelectedIndex - 1 : -1;
            var minimumState = cbWindowsMinState.IsChecked == true ? (int)nudWindowsMinState.Value : -1;
            var maximumState = cbWindowsMaxState.IsChecked == true ? (int)nudWindowsMaxState.Value : -1;
            var maximumFrequency = cbWindowsMaxFrequency.IsChecked == true ? (int)nudWindowsMaxFrequency.Value : -1;
            var energyPreference = cbWindowsEpp.IsChecked == true ? (int)nudWindowsEpp.Value : -1;
            var minimumUnparkedCores = cbWindowsCoreParking.IsChecked == true ? (int)nudWindowsCoreParking.Value : -1;
            var maximumUnparkedCores = cbWindowsMaxUnparkedCores.IsChecked == true ? (int)nudWindowsMaxUnparkedCores.Value : -1;
            return boostMode >= 0 || minimumState >= 0 || maximumState >= 0 || maximumFrequency >= 0 || energyPreference >= 0 || minimumUnparkedCores >= 0 || maximumUnparkedCores >= 0
                ? $"--Win-CPU={boostMode},{maximumState},{maximumFrequency},{energyPreference},{minimumState},{minimumUnparkedCores},{maximumUnparkedCores} "
                : string.Empty;
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
            adaptivePresetManager = new AdaptivePresetManager(Settings.Default.Path + "adaptivePresets.json");
            if (cbxPowerPreset.SelectedItem is string presetName)
            {
                // Don't reset tracking on navigation — just load the preset UI values
                loadPreset(presetName, resetTracking: false);
            }

            // Recheck Flydigi card visibility on navigation (device may have connected/disconnected)
            UpdateFlydigiCardVisibility();
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

                    // Only load preset UI values when no device override is active.
                    // When Flydigi/Watercooler override is enabled, the profile owns
                    // the device settings and game detection should not overwrite the
                    // UI controls (which would cause update() to re-send commands).
                    if ((bool)cbxBs2ProEnabled.IsChecked || (bool)cbxWcEnabled.IsChecked)
                        return;

                    loadPreset(presetName, resetTracking: false);
                    return;
                }
            }

            // Fallback to Default if the preset wasn't found.
            _isUpdatingPresetSelection = true;
            cbxPowerPreset.SelectedIndex = 0;
            _isUpdatingPresetSelection = false;

            if ((bool)cbxBs2ProEnabled.IsChecked || (bool)cbxWcEnabled.IsChecked)
                return;

            loadPreset(DefaultProfileName, resetTracking: false);
        }

        private void cb_Checked(object sender, RoutedEventArgs e)
        {
            System.Windows.Controls.CheckBox checkBox = (System.Windows.Controls.CheckBox)sender;
            if (checkBox == cbBoost)
            {
                cbRSR.IsChecked = false;
                cbAntiLag.IsChecked = false;
            }

            if (checkBox == cbAntiLag)
            {
                cbBoost.IsChecked = false;
            }

            if (checkBox == cbRSR)
            {
                cbBoost.IsChecked = false;
                cbImageSharp.IsChecked = false;
            }

            if (checkBox == cbImageSharp) cbRSR.IsChecked = false;
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
            spWcRgbColor.Visibility = mode switch
            {
                RgbState.Static or RgbState.Breathe => Visibility.Visible,
                _ => Visibility.Collapsed
            };
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
            spBs2ProGear.Visibility = mode == 0 ? Visibility.Visible : Visibility.Collapsed;
            spBs2ProRpm.Visibility = mode == 1 ? Visibility.Visible : Visibility.Collapsed;
            spBs2ProCurve.Visibility = mode == 2 ? Visibility.Visible : Visibility.Collapsed;
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
            if (spBs2ProRgb == null)
                return;

            var mode = cbxBs2ProRgbMode.SelectedIndex;
            // Show RGB color controls only for Static (2) and Breathing (3)
            spBs2ProRgb.Visibility = mode is 2 or 3 ? Visibility.Visible : Visibility.Collapsed;
        }

        #endregion
    }
}
