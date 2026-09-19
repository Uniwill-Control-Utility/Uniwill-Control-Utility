using Microsoft.Win32.TaskScheduler;
using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Uniwill_Control_Utility.Properties;
using Uniwill_Control_Utility.Scripts;
using Uniwill_Control_Utility.Scripts.Misc;
using Uniwill_Control_Utility.Services;
using Wpf.Ui.Abstractions.Controls;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace Uniwill_Control_Utility.Views.Pages
{
    public partial class SettingsPage : INavigableView<ViewModels.SettingsViewModel>
    {
        private const string UpdateGitHubOwner = "jgudec";
        private const string UpdateGitHubRepo = "Uniwill-Control-Utility";
        private const string RootDrivePath = "C:\\";
        private const string InstallerFilePath = "C:\\Uniwill.Control.Utility.msi";

        private const string WmiNamespace = @"root\CIMV2";
        private const string WmiQueryProcessor = "SELECT * FROM Win32_Processor";
        private const string WmiQueryPhysicalMemory = "SELECT * FROM Win32_PhysicalMemory";
        private const string ScheduledTaskName = "UCU";
        private const string InternetConnectivityHost = "8.8.8.8";
        private const string PresetExtension = ".UCUpresets";
        private const string StressTestExeRelativePath = @"Assets\Stress-Test\AVX2 Stress Test.exe";
        private const string LegacyVersionPrefix = "3.";
        private const string MsiExecCommand = "msiexec";
        private const string MsiInstallFlag = "/i ";

        private readonly ILogger<SettingsPage> _logger;
        private bool _languageSelectionReady;

        // Backing fields for async system info
        private string _deviceName = "", _deviceModel = "", _deviceProducer = "";
        private string _cpuName = "", _cpuDescription = "", _cpuManufacturer = "", _cpuCodeName = "", _cpuBaseClock = "", _cpuInstructions = "";
        private int _cpuCores = 0, _cpuLogicalProcessors = 0;
        private double _cpuL2Size = 0, _cpuL3Size = 0;
        private decimal _cpuL1Cache = 0, _cpuL2Cache = 0;
        private double _ramCapacity = 0;
        private int _ramSpeed = 0, _ramType = 0, _ramWidth = 0, _ramSlots = 0;
        private string _ramProducer = "", _ramModel = "";

        // MiniLED Local Dimming
        private const string WmiClassName = "AcpiTest_MULong";
        private const string DefaultInstanceName = @"ACPI\PNP0C14\1_1";
        private const ulong CmdEnableStep1 = 0x50000000002;
        private const ulong CmdEnableStep2 = 0x5000000000E;
        private const ulong CmdDisableStep1 = 0x50000000001;
        private const ulong CmdDisableStep2 = 0x5000000000D;
        private const byte ExpectedEnableStep1 = 0xBB;
        private const byte ExpectedEnableStep2 = 0x0E;
        private const byte ExpectedDisableStep1 = 0xAA;
        private const byte ExpectedDisableStep2 = 0x0D;
        private const ushort EcLocalDimmingRegister = 0x072B;
        private const byte EcLocalDimmingBit = 0x02;
        private bool _localDimmingToggling;
        private UniwillECService? _ecService;

        public ViewModels.SettingsViewModel ViewModel
        {
            get;
        }

        public SettingsPage(ViewModels.SettingsViewModel viewModel, ILogger<SettingsPage> logger)
        {
            ViewModel = viewModel;
            _logger = logger;

            InitializeComponent();

            // Get EC service for Local Dimming readback verification.
            _ecService = App.GetService<UniwillECService>();

            // Show/hide Local Dimming card based on panel type (detected at startup).
            CardLocalDimming.Visibility = App.HasMiniLEDPanel ? Visibility.Visible : Visibility.Collapsed;

            cbxLanguage.ItemsSource = LocalizationService.SupportedLanguages;
            cbxLanguage.SelectedItem = LocalizationService.SupportedLanguages.First(language => language.CultureName == LocalizationService.CurrentCultureName);
            _languageSelectionReady = true;

            cbStartBoot.IsChecked = Settings.Default.StartOnBoot;
            nbStartupDelay.Value = Settings.Default.StartupDelaySeconds;
            bool bootEnabled = Settings.Default.StartOnBoot;
            StartupDelayDivider.Visibility = bootEnabled ? Visibility.Visible : Visibility.Collapsed;
            StartupDelayRow.Visibility = bootEnabled ? Visibility.Visible : Visibility.Collapsed;
            cbStartMini.IsChecked = Settings.Default.StartMini;
            cbMinimizeClose.IsChecked = Settings.Default.MinimizeClose;
            cbAutoCheck.IsChecked = Settings.Default.UpdateCheck;
            cbAdaptive.IsChecked = Settings.Default.isStartAdpative;
            cbGpuPowerBoost.IsChecked = Settings.Default.EnableGpuPowerBoost;
            cbHideFlydigi.IsChecked = Settings.Default.EnableFlydigi;
            cbDisableCpuVoltageControl.IsChecked = Settings.Default.EnableCpuVoltageControl;
            cbDisableGpuClockControl.IsChecked = Settings.Default.EnableGpuClockControl;

            cbxLogLevel.SelectedIndex = Settings.Default.DiagnosticLogLevel;

            tbAppVerion.Text = $"Uniwill Control Utility - {GetAssemblyVersion()}";

            // Show GPU Power Boost toggle only for RTX 5080/5090
            if (Scripts.GPUs.NVIDIA.NvTuning.IsBlackwellHighEnd())
            {
                GpuPowerBoostCard.Visibility = Visibility.Visible;
            }

            _ = checkUpdate();
        }
        private static string GetAssemblyVersion()
        {
            return System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? String.Empty;
        }

        #region System Info

        private void getDeviceInfo()
        {
            _deviceName = GetSystemInfo.SystemName;
            _deviceModel = GetSystemInfo.Product;
            _deviceProducer = GetSystemInfo.Manufacturer;
        }

        private void applyDeviceInfo()
        {
            tbDeviceName.Text = _deviceName;
            tbDeviceModel.Text = _deviceModel;
            tbDeviceProducer.Text = _deviceProducer;
        }

        private void getCPUInfoSync()
        {
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(WmiNamespace, WmiQueryProcessor))
                {
                    foreach (ManagementBaseObject queryObj in searcher.Get())
                    {
                        _cpuName = (queryObj["Name"] as string ?? string.Empty).Trim();
                        _cpuDescription = (queryObj["Description"] as string ?? string.Empty).Trim();
                        _cpuManufacturer = (queryObj["Manufacturer"] as string ?? string.Empty).Trim();
                        _cpuCores = Convert.ToInt32(queryObj["NumberOfCores"]);
                        _cpuLogicalProcessors = Convert.ToInt32(queryObj["NumberOfLogicalProcessors"]);
                        _cpuL2Size = Convert.ToDouble(queryObj["L2CacheSize"]) / 1024;
                        _cpuL3Size = Convert.ToDouble(queryObj["L3CacheSize"]) / 1024;
                        _cpuBaseClock = queryObj["MaxClockSpeed"] as string ?? string.Empty;
                    }
                }

                _cpuCodeName = GetSystemInfo.Codename() ?? string.Empty;

                uint sum = 0u;
                foreach (uint number in GetSystemInfo.GetCacheSizes(GetSystemInfo.CacheLevel.Level1))
                    sum += number;
                _cpuL1Cache = (decimal)(sum / 1024u);

                sum = 0u;
                foreach (uint number in GetSystemInfo.GetCacheSizes(GetSystemInfo.CacheLevel.Level2))
                    sum += number;
                _cpuL2Cache = (decimal)(sum / 1024u);

                _cpuInstructions = GetSystemInfo.InstructionSets();
            }
            catch (ManagementException ex)
            {
                _logger.LogWarning(ex, "An error occurred while querying for WMI data");
            }
        }

        private void applyCPUInfo()
        {
            tbProcessor.Text = _cpuName;
            tbCaption.Text = _cpuDescription;

            if (_cpuCodeName != "")
            {
                tbCodename.Text = _cpuCodeName;
            }
            else
            {
                CodenameRow.Visibility = Visibility.Collapsed;
                CodenameDivider.Visibility = Visibility.Collapsed;
            }

            tbProducer.Text = _cpuManufacturer;
            if (_cpuLogicalProcessors == _cpuCores)
                tbCores.Text = _cpuCores.ToString();
            else
                tbCores.Text = GetSystemInfo.getBigLITTLE(_cpuCores, _cpuL2Size);
            tbThreads.Text = _cpuLogicalProcessors.ToString();
            tbL3Cache.Text = $"{_cpuL3Size.ToString("0.##")} MB";
            tbL1Cache.Text = $"{_cpuL1Cache.ToString("0.##")} MB";
            tbL2Cache.Text = $"{_cpuL2Cache.ToString("0.##")} MB";
            tbBaseClock.Text = $"{_cpuBaseClock} MHz";
            tbInstructions.Text = _cpuInstructions;
            sdCPU.Visibility = Visibility.Visible;
        }

        private void getRAMInfoSync()
        {
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(WmiNamespace, WmiQueryPhysicalMemory))
                {
                    foreach (ManagementBaseObject queryObj in searcher.Get())
                    {
                        string manufacturer = queryObj["Manufacturer"] as string ?? string.Empty;
                        string partNumber = queryObj["PartNumber"] as string ?? string.Empty;

                        if (_ramProducer == "") _ramProducer = manufacturer;
                        else if (!_ramProducer.Contains(manufacturer))
                            _ramProducer = $"{_ramProducer}/{manufacturer}";

                        if (_ramModel == "") _ramModel = partNumber;
                        else if (!_ramModel.Contains(partNumber))
                            _ramModel = $"{_ramModel}/{partNumber}";

                        _ramCapacity = _ramCapacity + Convert.ToDouble(queryObj["Capacity"]);
                        _ramSpeed = Convert.ToInt32(queryObj["ConfiguredClockSpeed"]);
                        _ramType = Convert.ToInt32(queryObj["SMBIOSMemoryType"]);
                        _ramWidth = _ramWidth + Convert.ToInt32(queryObj["DataWidth"]);
                        _ramSlots++;
                    }
                }

                _ramCapacity = _ramCapacity / 1024 / 1024 / 1024;
            }
            catch (Exception ex)
                {
                    // RAM info parsing failed; defaults will be used.
                    // Non-critical: the page renders fine without RAM details.
                    _logger.LogDebug(ex, "Failed to query RAM info via WMI");
                }
        }

        private void applyRAMInfo()
        {
            string DDRType = _ramType switch
            {
                20 => "DDR",
                21 => "DDR2",
                24 => "DDR3",
                26 => "DDR4",
                30 => "LPDDR4",
                34 => "DDR5",
                35 => "LPDDR5",
                _ => $"Unknown ({_ramType})"
            };

            tbRAM.Text = $"{_ramCapacity} GB {DDRType} @ {_ramSpeed} MT/s";
            tbRAMProducer.Text = _ramProducer;
            tbRAMModel.Text = _ramModel.Replace(" ", string.Empty);
            tbWidth.Text = $"{_ramWidth} bit";
            tbSlots.Text = $"{_ramSlots} * {_ramWidth / _ramSlots} bit";
            sdRAM.Visibility = Visibility.Visible;
        }

        #endregion

        #region Toggle handlers

        private void cbStartBoot_Checked(object sender, RoutedEventArgs e)
        {
            StartupDelayDivider.Visibility = Visibility.Visible;
            StartupDelayRow.Visibility = Visibility.Visible;
            UpdateScheduledTask();

            Settings.Default.StartOnBoot = true;
            Settings.Default.Save();
        }

        private void cbStartBoot_Unchecked(object sender, RoutedEventArgs e)
        {
            StartupDelayDivider.Visibility = Visibility.Collapsed;
            StartupDelayRow.Visibility = Visibility.Collapsed;

            using (TaskService ts = new TaskService())
            {
                if (ts.RootFolder.AllTasks.Any(t => t.Name == ScheduledTaskName))
                {
                    ts.RootFolder.DeleteTask(ScheduledTaskName);
                }
            }

            Settings.Default.StartOnBoot = false;
            Settings.Default.Save();
        }

        /// <summary>
        /// Creates or updates the Windows scheduled task with the current startup delay.
        /// </summary>
        private void UpdateScheduledTask()
        {
            using (TaskService ts = new TaskService())
            {
                // Delete existing task so we can recreate with updated delay
                if (ts.RootFolder.AllTasks.Any(t => t.Name == ScheduledTaskName))
                {
                    ts.RootFolder.DeleteTask(ScheduledTaskName);
                }

                TaskDefinition td = ts.NewTask();
                td.Principal.RunLevel = TaskRunLevel.Highest;
                td.RegistrationInfo.Description = "Start UCU";
                td.Settings.DisallowStartIfOnBatteries = false;
                td.Settings.StopIfGoingOnBatteries = false;
                td.Settings.DisallowStartOnRemoteAppSession = false;

                int delaySeconds = (int)(nbStartupDelay.Value ?? 0);
                var trigger = new LogonTrigger()
                {
                    Delay = TimeSpan.FromSeconds(delaySeconds)
                };
                td.Triggers.Add(trigger);

                string path = System.Reflection.Assembly.GetEntryAssembly()?.Location
                    ?? System.Reflection.Assembly.GetExecutingAssembly().Location;
                path = Path.ChangeExtension(path, ".exe");

                td.Actions.Add(path);

                ts.RootFolder.RegisterTaskDefinition(ScheduledTaskName, td);
            }
        }

        private void nbStartupDelay_ValueChanged(object sender, RoutedEventArgs e)
        {
            int seconds = (int)(nbStartupDelay.Value ?? 0);

            Settings.Default.StartupDelaySeconds = seconds;
            Settings.Default.Save();

            // Update the scheduled task with the new delay if Start on boot is ON
            if (cbStartBoot.IsChecked == true)
            {
                UpdateScheduledTask();
            }
        }

        private void cbStartMini_Checked(object sender, RoutedEventArgs e)
        {
            Settings.Default.StartMini = true;
            Settings.Default.Save();
        }

        private void cbStartMini_Unchecked(object sender, RoutedEventArgs e)
        {
            Settings.Default.StartMini = false;
            Settings.Default.Save();
        }

        private void cbMinimizeClose_Checked(object sender, RoutedEventArgs e)
        {
            Settings.Default.MinimizeClose = true;
            Settings.Default.Save();
        }

        private void cbMinimizeClose_Unchecked(object sender, RoutedEventArgs e)
        {
            Settings.Default.MinimizeClose = false;
            Settings.Default.Save();
        }

        private void cbAutoCheck_Checked(object sender, RoutedEventArgs e)
        {
            Settings.Default.UpdateCheck = true;
            Settings.Default.Save();
        }

        private void cbAutoCheck_Unchecked(object sender, RoutedEventArgs e)
        {
            Settings.Default.UpdateCheck = false;
            Settings.Default.Save();
        }

        private void cbAdaptive_Checked(object sender, RoutedEventArgs e)
        {
            Settings.Default.isStartAdpative = true;
            Settings.Default.Save();
        }

        private void cbAdaptive_Unchecked(object sender, RoutedEventArgs e)
        {
            Settings.Default.isStartAdpative = false;
            Settings.Default.Save();
        }

       private void cbGpuPowerBoost_Checked(object sender, RoutedEventArgs e)
        {
            Settings.Default.EnableGpuPowerBoost = true;
            Settings.Default.Save();
        }

        private void cbGpuPowerBoost_Unchecked(object sender, RoutedEventArgs e)
        {
            Settings.Default.EnableGpuPowerBoost = false;
            Settings.Default.Save();
        }

        private void cbHideFlydigi_Checked(object sender, RoutedEventArgs e)
        {
            Settings.Default.EnableFlydigi = true;
            Settings.Default.Save();

            // Resume all Flydigi background services
            try
            {
                var coolerService = App.GetService<FlydigiCoolerHidService>();
                coolerService?.ResumeBackgroundServices();

                var bs1Service = App.GetService<FlydigiCoolerBleService>();
                bs1Service?.ResumeBackgroundServices();
            }
            catch (Exception)
            {
                // Non-critical: resume failures shouldn't block the UI.
            }
        }

        private void cbHideFlydigi_Unchecked(object sender, RoutedEventArgs e)
        {
            Settings.Default.EnableFlydigi = false;
            Settings.Default.Save();

            // Fully suspend all Flydigi background services (fire-and-forget to avoid UI freeze)
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var coolerService = App.GetService<FlydigiCoolerHidService>();
                    coolerService?.SuspendAllServices();

                    var bs1Service = App.GetService<FlydigiCoolerBleService>();
                    bs1Service?.SuspendAllServices();
                }
                catch (Exception)
                    {
                        // Ignore — best-effort cleanup when disabling Flydigi.
                    }
            });
        }

        private void cbDisableCpuVoltageControl_Checked(object sender, RoutedEventArgs e)
        {
            Settings.Default.EnableCpuVoltageControl = true;
            Settings.Default.Save();
        }

        private void cbDisableCpuVoltageControl_Unchecked(object sender, RoutedEventArgs e)
        {
            Settings.Default.EnableCpuVoltageControl = false;
            Settings.Default.Save();
        }

        private void cbDisableGpuClockControl_Checked(object sender, RoutedEventArgs e)
        {
            Settings.Default.EnableGpuClockControl = true;
            Settings.Default.Save();
        }

        private void cbDisableGpuClockControl_Unchecked(object sender, RoutedEventArgs e)
        {
            Settings.Default.EnableGpuClockControl = false;
            Settings.Default.Save();
        }

       #endregion

        private async void btnCheck_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            await checkUpdate(true);
        }

        private async System.Threading.Tasks.Task checkUpdate(bool isUserCheck = false)
        {
            if (IsInternetAvailable())
            {
                var updateManager = new UpdateManager(UpdateGitHubOwner, UpdateGitHubRepo, App.version, RootDrivePath);

                var isUpdateAvailable = await updateManager.IsUpdateAvailable();

                if (isUpdateAvailable)
                {
                    if (updateManager.NewVersion.StartsWith(LegacyVersionPrefix)) tbDownloadMsg.Text = LocalizationService.Get("Head to the Phantom Control Centre GitHub releases page to easily download the latest build!");
                    else {
                        tbDownloadMsg.Text = LocalizationService.Get("An update for Uniwill Control Utility has been found!");
                        btnDownload.Visibility = System.Windows.Visibility.Visible;
                    }
                }
                else if(isUserCheck) tbDownloadMsg.Text = LocalizationService.Get("Uniwill Control Utility is up to date!");
            }
            else if (isUserCheck) tbDownloadMsg.Text = LocalizationService.Get("No internet connection!");
        }

        private async void btnDownload_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            var updateManager = new UpdateManager(UpdateGitHubOwner, UpdateGitHubRepo, App.version, RootDrivePath);

            var isUpdateAvailable = await updateManager.IsUpdateAvailable();

            if (isUpdateAvailable)
            {
                tbDownloadMsg.Text = LocalizationService.Get("Uniwill Control Utility will close and the installer will open when the download is complete");

                await updateManager.DownloadAndInstallUpdate();

                string filePath = InstallerFilePath;

                try
                {
                    // show the MSI and close the main application
                    Process p = new Process();
                    p.StartInfo.UseShellExecute = false;
                    p.StartInfo.FileName = MsiExecCommand;
                    p.StartInfo.Arguments = MsiInstallFlag + filePath;
                    p.Start();
                    System.Windows.Application.Current.Shutdown();
                }
                catch (Exception ex)
                {
                    // log error or display error message to user
                    _logger.LogError(ex, "Failed to launch MSI");
                    System.Windows.MessageBox.Show(LocalizationService.Format("Failed to launch MSI: {0}", ex.Message));
                }
            }
        }

        private static bool IsInternetAvailable()
        {
            try
            {
                using (var ping = new Ping())
                {
                    var result = ping.Send(InternetConnectivityHost, 2000); // ping Google DNS server
                    return result.Status == IPStatus.Success;
                }
            }
            catch (Exception)
            {
                // Internet connectivity check failed; assume offline to avoid false-positive update checks.
                // Suppressing the exception intentionally — transient network issues are common and
                // a failed ping simply means "offline" from the perspective of update checking.
                return false;
            }
        }

        private async void UiPage_Loaded(object sender, RoutedEventArgs e)
        {
            // Load Local Dimming state from settings.
            tsLocalDimming.IsChecked = Settings.Default.DisplayLocalDimming;

            await LoadSystemInfoAsync();
        }

        private async System.Threading.Tasks.Task LoadSystemInfoAsync()
        {
            // Hide expanders immediately so the page renders clean
            sdCPU.Visibility = Visibility.Collapsed;
            sdRAM.Visibility = Visibility.Collapsed;

            // Offload all heavy WMI queries and processing to a background thread
            await System.Threading.Tasks.Task.Run(() =>
            {
                getDeviceInfo();
                getCPUInfoSync();
                getRAMInfoSync();
            });

            // Update UI on the dispatcher thread
            await this.Dispatcher.InvokeAsync(() =>
            {
                applyDeviceInfo();
                applyCPUInfo();
                applyRAMInfo();
            });
        }

        private void btnStressTest_Click(object sender, RoutedEventArgs e)
        {
            var stressTestPath = Settings.Default.Path + StressTestExeRelativePath;
            if (File.Exists(stressTestPath))
            {
                Process process = new Process();
                process.StartInfo.FileName = stressTestPath;
                process.Start();

                process.Dispose();
            }
        }

        private void cbxLogLevel_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (cbxLogLevel == null)
            {
                return;
            }

            Settings.Default.DiagnosticLogLevel = cbxLogLevel.SelectedIndex;
            Settings.Default.Save();
            DiagnosticLogger.ApplySettingsLevel();
        }

        private void cbxLanguage_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (!_languageSelectionReady || cbxLanguage.SelectedItem is not LanguageOption language)
            {
                return;
            }

            Settings.Default.Language = language.CultureName;
            Settings.Default.Save();
            LocalizationService.SetCulture(language.CultureName);
        }

        private void ComboBoxInCardExpander_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            // Prevent CardExpander from collapsing when clicking inside a ComboBox
            e.Handled = true;
        }

        private async void btnBackupPresets_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog
            {
                AddExtension = true,
                DefaultExt = PresetExtension,
                FileName = $"UCU-Presets-{DateTime.Now:yyyy-MM-dd}{PresetExtension}",
                Filter = $"{LocalizationService.Get("UCU backup")} (*{PresetExtension})|*{PresetExtension}"
            };

            if (dialog.ShowDialog() != true)
            {
                return;
            }

            SetPresetBackupBusy(true);
            try
            {
                var result = await PresetBackupService.ExportAsync(Settings.Default.Path, dialog.FileName);
                ShowPresetBackupStatus(
                    LocalizationService.Get("Backup saved"),
                    LocalizationService.Format("Backed up {0} custom presets, {1} adaptive presets, {2} V-F curves, and {3} settings.",
                        result.CustomPresetCount, result.AdaptivePresetCount, result.VfCurveCount, result.SettingsCount),
                    Wpf.Ui.Controls.InfoBarSeverity.Success);
            }
            catch (Exception exception)
            {
                DiagnosticLogger.LogError(exception, "Failed to export backup");
                ShowPresetBackupStatus(
                    LocalizationService.Get("Backup failed"),
                    LocalizationService.Format("The backup could not be exported.\n\n{0}", exception.Message),
                    Wpf.Ui.Controls.InfoBarSeverity.Error);
            }
            finally
            {
                SetPresetBackupBusy(false);
            }
        }

        private async void btnImportPresets_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                CheckFileExists = true,
                DefaultExt = PresetExtension,
                Filter = $"{LocalizationService.Get("UCU backup")} (*{PresetExtension};*.json)|*{PresetExtension};*.json"
            };

            if (dialog.ShowDialog() != true)
            {
                return;
            }

            SetPresetBackupBusy(true);
            try
            {
                var result = await PresetBackupService.ImportAsync(Settings.Default.Path, dialog.FileName);
                ShowPresetBackupStatus(
                    LocalizationService.Get("Import complete"),
                    LocalizationService.Format("Imported {0} custom presets, {1} adaptive presets, {2} V-F curves, and {3} settings.",
                        result.CustomPresetCount, result.AdaptivePresetCount, result.VfCurveCount, result.SettingsCount),
                    Wpf.Ui.Controls.InfoBarSeverity.Success);
            }
            catch (InvalidDataException)
            {
                ShowPresetBackupStatus(
                    LocalizationService.Get("Import failed"),
                    LocalizationService.Get("The selected file is not a valid UCU backup."),
                    Wpf.Ui.Controls.InfoBarSeverity.Error);
            }
            catch (Exception exception)
            {
                DiagnosticLogger.LogError(exception, "Failed to import backup");
                ShowPresetBackupStatus(
                    LocalizationService.Get("Import failed"),
                    LocalizationService.Format("The backup could not be imported.\n\n{0}", exception.Message),
                    Wpf.Ui.Controls.InfoBarSeverity.Error);
            }
            finally
            {
                SetPresetBackupBusy(false);
            }
        }

        private void SetPresetBackupBusy(bool isBusy)
        {
            btnBackupPresets.IsEnabled = !isBusy;
            btnImportPresets.IsEnabled = !isBusy;
        }

        private void ShowPresetBackupStatus(string title, string message, Wpf.Ui.Controls.InfoBarSeverity severity)
        {
            PresetBackupStatus.Title = title;
            PresetBackupStatus.Message = message;
            PresetBackupStatus.Severity = severity;
            PresetBackupStatus.IsOpen = true;
        }

        #region MiniLED Local Dimming

        /// <summary>
        /// Calls GetSetULong via WMI using the correct GetMethodParameters pattern.
        /// This matches the pattern used by MechControl (Heavyfail fork) on Uniwill IDY/Tongfang X6 laptops.
        /// </summary>
        private static byte WmiCall(ManagementObject mo, ulong data)
        {
            using var input = mo.GetMethodParameters("GetSetULong");
            input["Data"] = data;

            using var output = (ManagementBaseObject)mo.InvokeMethod("GetSetULong", input, null);
            return (byte)(uint)output["Return"];
        }

        private void TsLocalDimming_Toggled(object sender, RoutedEventArgs e)
        {
            if (_localDimmingToggling)
                return;

            bool isOn = tsLocalDimming.IsChecked == true;

            _localDimmingToggling = true;
            tsLocalDimming.IsEnabled = false;

            // WMI ManagementObject has apartment threading affinity — must be called on a dedicated thread.
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    bool success = SetLocalDimming(isOn);
                    _logger.LogInformation("[Display] Local Dimming {State}: {Result}",
                        isOn ? "enabled" : "disabled", success ? "success" : "failed");

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
                    _logger.LogError(ex, "[Display] Local Dimming toggle failed");

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
            try
            {
                var instancePath = $@"{WmiClassName}.InstanceName='{DefaultInstanceName}'";
                using var mo = new ManagementObject(@"root\WMI", instancePath, null);

                if (enable)
                {
                    _logger.LogDebug("[Display] Sending enable step 1: 0x{Cmd:X}", CmdEnableStep1);
                    byte result1 = WmiCall(mo, CmdEnableStep1);
                    _logger.LogDebug("[Display] Enable step 1 returned 0x{Result:X2}", result1);

                    if (result1 != ExpectedEnableStep1)
                    {
                        _logger.LogWarning("[Display] Enable step 1 returned 0x{Result:X2} (expected 0x{Expected:X2})",
                            result1, ExpectedEnableStep1);
                        return false;
                    }

                    _logger.LogDebug("[Display] Sending enable step 2: 0x{Cmd:X}", CmdEnableStep2);
                    byte result2 = WmiCall(mo, CmdEnableStep2);
                    _logger.LogDebug("[Display] Enable step 2 returned 0x{Result:X2}", result2);

                    if (result2 != ExpectedEnableStep2)
                    {
                        _logger.LogWarning("[Display] Enable step 2 returned 0x{Result:X2} (expected 0x{Expected:X2})",
                            result2, ExpectedEnableStep2);
                        return false;
                    }
                }
                else
                {
                    _logger.LogDebug("[Display] Sending disable step 1: 0x{Cmd:X}", CmdDisableStep1);
                    byte result1 = WmiCall(mo, CmdDisableStep1);
                    _logger.LogDebug("[Display] Disable step 1 returned 0x{Result:X2}", result1);

                    if (result1 != ExpectedDisableStep1)
                    {
                        _logger.LogWarning("[Display] Disable step 1 returned 0x{Result:X2} (expected 0x{Expected:X2})",
                            result1, ExpectedDisableStep1);
                        return false;
                    }

                    _logger.LogDebug("[Display] Sending disable step 2: 0x{Cmd:X}", CmdDisableStep2);
                    byte result2 = WmiCall(mo, CmdDisableStep2);
                    _logger.LogDebug("[Display] Disable step 2 returned 0x{Result:X2}", result2);

                    if (result2 != ExpectedDisableStep2)
                    {
                        _logger.LogWarning("[Display] Disable step 2 returned 0x{Result:X2} (expected 0x{Expected:X2})",
                            result2, ExpectedDisableStep2);
                        return false;
                    }
                }

                Thread.Sleep(100);

                // Verify via EC readback.
                if (_ecService != null && _ecService.Initialize())
                {
                    byte ecValue = _ecService.ReadECRegister(EcLocalDimmingRegister);
                    bool actualState = (ecValue & EcLocalDimmingBit) != 0;

                    if (actualState != enable)
                    {
                        _logger.LogWarning("[Display] EC 0x72B readback 0x{EcValue:X2} shows {Actual}, expected {Expected} — mismatch",
                            ecValue, actualState ? "ON" : "OFF", enable ? "ON" : "OFF");
                        return false;
                    }

                    _logger.LogInformation("[Display] EC 0x72B readback 0x{EcValue:X2} confirms Local Dimming {State}",
                        ecValue, enable ? "ON" : "OFF");
                }
                else
                {
                    _logger.LogInformation("[Display] EC service unavailable — skipping readback verification");
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Display] WMI/ACPI call failed");
                return false;
            }
        }

        #endregion
    }
}