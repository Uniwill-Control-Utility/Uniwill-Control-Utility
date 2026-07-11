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
            sdBs2Pro.Visibility = FlydigiHardwareDetector.IsDeviceAvailable() ? Visibility.Visible : Visibility.Collapsed;
            tbBs2ProTitle.Text = FlydigiHardwareDetector.GetDetectedModelName();
        }
        private static AdaptivePresetManager adaptivePresetManager = new AdaptivePresetManager(Settings.Default.Path + "adaptivePresets.json");
        private static WaterCoolerService? _waterCoolerService;
        private static FlydigiCoolerService? _bs2ProService;
        private async void setUp()
        {
            try
            {
                GpuInventorySnapshot inventory = await gpuInventory.GetSnapshotAsync();
                radeonGpuCount = inventory.RadeonCount;
                nvidiaGpuCount = inventory.NvidiaCount;

                if (radeonGpuCount <= 0)
                {
                    sdTBOiGPU.Visibility = Visibility.Collapsed;
                    sdADLX.Visibility = Visibility.Collapsed;
                }

                if (nvidiaGpuCount < 1) sdNVIDIA.Visibility = Visibility.Collapsed;

                if (Family.TYPE == Family.ProcessorType.Amd_Desktop_Cpu || Family.FAM == Family.RyzenFamily.DragonRange) nudPowerLimit.Value = 86;
                else nudPowerLimit.Value = 28;
                nudMaxGfxClk.Value = 1900;
                nudMinGfxClk.Value = 400;
                nudTemp.Value = 95;
                nudMinCpuClk.Value = 1500;
                nudNVMaxCore.Value = 4000;
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
                    tbxStartText.Text = "Start Adaptive Mode";
                    GetSensor.CloseSensor();
                    Settings.Default.isAdaptiveModeRunning = false;
                    Settings.Default.Save();

                }
                else
                {
                    start = true;
                    siStartIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.Stop20;
                    tbxStartText.Text = "Stop Adaptive Mode";
                    await Task.Run(() => GetSensor.OpenSensor());
                    Settings.Default.isAdaptiveModeRunning = true;
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
            string presetName = (sender as ComboBox).SelectedItem as string;
            loadPreset(presetName);
        }

        private void loadPreset(string presetName)
        {
            try
            {
                adaptivePresetManager = new AdaptivePresetManager(Settings.Default.Path + "adaptivePresets.json");
                AdaptivePreset myPreset = adaptivePresetManager.GetPreset(presetName);

                if (myPreset != null)
                {
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

                    tsUXTUSR.IsChecked = myPreset.isMag;
                    cbVSync.IsChecked = myPreset.isVsync;
                    cbAutoCap.IsChecked = myPreset.isRecap;
                    nudSharp.Value = myPreset.Sharpness;
                    cbxResScale.SelectedIndex = myPreset.ResScaleIndex;

                    // Watercooler
                    cbxWcEnabled.IsChecked = myPreset.WcEnabled;
                    if (Enum.TryParse<PumpVoltage>(myPreset.WcPumpVoltage, true, out var pv))
                        cbxWcPumpVoltage.SelectedIndex = GetPumpVoltageIndex(pv);
                    if (Enum.TryParse<FanSpeed>(myPreset.WcFanSpeed, true, out var fs))
                        cbxWcFanSpeed.SelectedIndex = GetFanSpeedIndex(fs);
                    if (Enum.TryParse<RgbState>(myPreset.WcRgbMode, true, out var rm))
                        cbxWcRgbMode.SelectedIndex = GetRgbModeIndex(rm);
                    if (Enum.TryParse<RgbColor>(myPreset.WcRgbColor, true, out var rc))
                        cbxWcRgbColor.SelectedIndex = GetRgbColorIndex(rc);

                    // BS2 Pro
                    cbxBs2ProEnabled.IsChecked = myPreset.Bs2ProEnabled;
                    Settings.Default.AdaptiveBs2ProEnabled = myPreset.Bs2ProEnabled;
                    Settings.Default.Save();
                    cbxBs2ProFanMode.SelectedIndex = GetBs2ProFanModeIndex(myPreset.Bs2ProFanMode);
                    UpdateBs2ProModeUI();
                    cbxBs2ProGear.SelectedIndex = Math.Clamp(myPreset.Bs2ProGear - 1, 0, 3);
                    nudBs2ProRpm.Value = Math.Clamp((int)myPreset.Bs2ProRpm, 1300, 4000);
                    cbxBs2ProCurve.SelectedIndex = GetBs2ProCurveIndex(myPreset.Bs2ProCurveProfileId);
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
            savePreset(cbxPowerPreset.SelectedItem.ToString());
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
        byte lastBs2ProGear = 0;
        ushort lastBs2ProRpm = 0;
        private async void update()
        {
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

                        // Apply watercooler settings if enabled, hardware is supported and connected
                        if ((bool)cbxWcEnabled.IsChecked)
                        {
                            if (_waterCoolerService == null && WaterCoolerHardwareDetector.IsSupportedHardware())
                                _waterCoolerService = App.GetService<WaterCoolerService>();

                            if (_waterCoolerService != null && _waterCoolerService.IsConnected)
                            {
                                PumpVoltage curPump = GetPumpVoltageFromIndex(cbxWcPumpVoltage.SelectedIndex);
                                FanSpeed curFan = GetFanSpeedFromIndex(cbxWcFanSpeed.SelectedIndex);
                                RgbState curRgbMode = GetRgbModeFromIndex(cbxWcRgbMode.SelectedIndex);
                                RgbColor curRgbColor = GetRgbColorFromIndex(cbxWcRgbColor.SelectedIndex);

                                if (curPump != lastWcPump)
                                {
                                    await _waterCoolerService.WritePumpModeAsync(curPump);
                                    lastWcPump = curPump;
                                }

                                if (curFan != lastWcFan)
                                {
                                    await _waterCoolerService.WriteFanModeAsync(curFan);
                                    lastWcFan = curFan;
                                }

                                if (curRgbMode != lastWcRgbMode || curRgbColor != lastWcRgbColor)
                                {
                                    await _waterCoolerService.WriteRgbModeAsync(curRgbMode, curRgbColor);
                                    lastWcRgbMode = curRgbMode;
                                    lastWcRgbColor = curRgbColor;
                                }
                            }
                        }

                        // Apply BS2 Pro settings if enabled, hardware is supported and connected
                        if ((bool)cbxBs2ProEnabled.IsChecked)
                        {
                            if (_bs2ProService == null && FlydigiHardwareDetector.IsDeviceAvailable())
                                _bs2ProService = App.GetService<FlydigiCoolerService>();

                            if (_bs2ProService != null && _bs2ProService.IsConnected)
                            {
                                string bs2Mode = GetBs2ProFanModeFromIndex(cbxBs2ProFanMode.SelectedIndex);

                                if (bs2Mode == "Off")
                                {
                                    await _bs2ProService.WriteRealtimeRpmAsync(0);
                                }
                                else if (bs2Mode == "Gear")
                                {
                                    byte gear = (byte)(cbxBs2ProGear.SelectedIndex + 1);
                                    if (gear != lastBs2ProGear)
                                    {
                                        await _bs2ProService.WriteGearAsync(gear);
                                        lastBs2ProGear = gear;
                                    }
                                }
                                else if (bs2Mode == "Rpm")
                                {
                                    ushort rpm = (ushort)Math.Clamp((int)nudBs2ProRpm.Value, 1300, 4000);
                                    if (rpm != lastBs2ProRpm)
                                    {
                                        await _bs2ProService.WriteRealtimeRpmAsync(rpm);
                                        lastBs2ProRpm = rpm;
                                    }
                                }
                                // "Curve" mode is handled by FlydigiSmartControl on the FlydigiCooler page,
                                // not by the adaptive tick loop. Auto-control toggle is saved for future use.
                            }
                        }

                        if (commandString != null && commandString != "") await Task.Run(() => RyzenAdj_To_UXTU.Translate(commandString));
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
                    cbxPowerPreset.SelectedItem = item;
                    return;
                }
            }

            // Fallback to Default if the preset wasn't found.
            cbxPowerPreset.SelectedIndex = 0;
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

        #endregion

        #region BS2 Pro Helpers

        private static string GetBs2ProFanModeFromIndex(int index)
        {
            return (index + 1) switch
            {
                1 => "Off",
                2 => "Gear",
                3 => "Rpm",
                _ => "Curve"
            };
        }

        private static int GetBs2ProFanModeIndex(string mode)
        {
            return mode switch
            {
                "Off" => 0,
                "Gear" => 1,
                "Rpm" => 2,
                "Curve" => 3,
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
            spBs2ProGear.Visibility = mode == 1 ? Visibility.Visible : Visibility.Collapsed;
            spBs2ProRpm.Visibility = mode == 2 ? Visibility.Visible : Visibility.Collapsed;
            spBs2ProCurve.Visibility = mode == 3 ? Visibility.Visible : Visibility.Collapsed;
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

        #endregion
    }
}
