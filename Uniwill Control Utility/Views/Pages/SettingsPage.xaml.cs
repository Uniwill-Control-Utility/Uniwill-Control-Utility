using Microsoft.Win32.TaskScheduler;
using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Uniwill_Control_Utility.Properties;
using Uniwill_Control_Utility.Scripts;
using Uniwill_Control_Utility.Scripts.Misc;
using Uniwill_Control_Utility.Services;
using Wpf.Ui.Abstractions.Controls;
using System.Diagnostics.Eventing.Reader;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace Uniwill_Control_Utility.Views.Pages
{
    public partial class SettingsPage : INavigableView<ViewModels.SettingsViewModel>
    {
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

        public ViewModels.SettingsViewModel ViewModel
        {
            get;
        }

        public SettingsPage(ViewModels.SettingsViewModel viewModel, ILogger<SettingsPage> logger)
        {
            ViewModel = viewModel;
            _logger = logger;

            InitializeComponent();

            cbxLanguage.ItemsSource = LocalizationService.SupportedLanguages;
            cbxLanguage.SelectedItem = LocalizationService.SupportedLanguages.First(language => language.CultureName == LocalizationService.CurrentCultureName);
            _languageSelectionReady = true;

            cbStartBoot.IsChecked = Settings.Default.StartOnBoot;
            cbStartMini.IsChecked = Settings.Default.StartMini;
            cbMinimizeClose.IsChecked = Settings.Default.MinimizeClose;
            cbApplyStart.IsChecked = Settings.Default.ApplyOnStart;
            nudAutoReapply.Value = Settings.Default.AutoReapplyTime;
            nudAutoReapply.Text = Convert.ToString(Settings.Default.AutoReapplyTime);
            cbAutoCheck.IsChecked = Settings.Default.UpdateCheck;
            cbAdaptive.IsChecked = Settings.Default.isStartAdpative;
            cbTrack.IsChecked = Settings.Default.isTrack;
            cbHideFlydigi.IsChecked = Settings.Default.HideFlydigi;

            cbxLogLevel.SelectedIndex = Settings.Default.DiagnosticLogLevel;

            tbAppVerion.Text = $"Uniwill Control Utility - {GetAssemblyVersion()}";

            checkUpdate();
        }
        private string GetAssemblyVersion()
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
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("root\\CIMV2", "SELECT * FROM Win32_Processor"))
                {
                    foreach (ManagementObject queryObj in searcher.Get())
                    {
                        _cpuName = queryObj["Name"].ToString().Trim();
                        _cpuDescription = queryObj["Description"].ToString().Trim();
                        _cpuManufacturer = queryObj["Manufacturer"].ToString().Trim();
                        _cpuCores = Convert.ToInt32(queryObj["NumberOfCores"]);
                        _cpuLogicalProcessors = Convert.ToInt32(queryObj["NumberOfLogicalProcessors"]);
                        _cpuL2Size = Convert.ToDouble(queryObj["L2CacheSize"]) / 1024;
                        _cpuL3Size = Convert.ToDouble(queryObj["L3CacheSize"]) / 1024;
                        _cpuBaseClock = queryObj["MaxClockSpeed"].ToString();
                    }
                }

                _cpuCodeName = GetSystemInfo.Codename();

                uint sum = 0;
                foreach (uint number in GetSystemInfo.GetCacheSizes(GetSystemInfo.CacheLevel.Level1))
                    sum += number;
                _cpuL1Cache = sum / 1024;

                sum = 0;
                foreach (uint number in GetSystemInfo.GetCacheSizes(GetSystemInfo.CacheLevel.Level2))
                    sum += number;
                _cpuL2Cache = sum / 1024;

                _cpuInstructions = GetSystemInfo.InstructionSets();
            }
            catch (ManagementException ex)
            {
                Console.WriteLine("An error occurred while querying for WMI data: " + ex.Message);
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
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("root\\CIMV2", "SELECT * FROM Win32_PhysicalMemory"))
                {
                    foreach (ManagementObject queryObj in searcher.Get())
                    {
                        if (_ramProducer == "") _ramProducer = queryObj["Manufacturer"].ToString();
                        else if (!_ramProducer.Contains(queryObj["Manufacturer"].ToString()))
                            _ramProducer = $"{_ramProducer}/{queryObj["Manufacturer"]}";

                        if (_ramModel == "") _ramModel = queryObj["PartNumber"].ToString();
                        else if (!_ramModel.Contains(queryObj["PartNumber"].ToString()))
                            _ramModel = $"{_ramModel}/{queryObj["PartNumber"]}";

                        _ramCapacity = _ramCapacity + Convert.ToDouble(queryObj["Capacity"]);
                        _ramSpeed = Convert.ToInt32(queryObj["ConfiguredClockSpeed"]);
                        _ramType = Convert.ToInt32(queryObj["SMBIOSMemoryType"]);
                        _ramWidth = _ramWidth + Convert.ToInt32(queryObj["DataWidth"]);
                        _ramSlots++;
                    }
                }

                if (_ramWidth > 128 && Family.FAM == Family.RyzenFamily.StrixHalo)
                    if (_ramWidth > 256) _ramWidth = 256;
                    else if (_ramWidth > 64 && Family.FAM == Family.RyzenFamily.Mendocino) _ramWidth = 64;
                    else if (_ramWidth > 128 && Family.FAM < Family.RyzenFamily.FireRange && Family.TYPE != Family.ProcessorType.Intel)
                        _ramWidth = 128;

                _ramCapacity = _ramCapacity / 1024 / 1024 / 1024;
            }
            catch { }
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
            tbRAMModel.Text = _ramModel.Replace(" ", null!);
            tbWidth.Text = $"{_ramWidth} bit";
            tbSlots.Text = $"{_ramSlots} * {_ramWidth / _ramSlots} bit";
            sdRAM.Visibility = Visibility.Visible;
        }

        #endregion

        #region Toggle handlers

        private void cbStartBoot_Checked(object sender, RoutedEventArgs e)
        {
            // Create the scheduled task
            using (TaskService ts = new TaskService())
            {
                if (!ts.RootFolder.AllTasks.Any(t => t.Name == "UCU"))
                {
                    TaskDefinition td = ts.NewTask();
                    td.Principal.RunLevel = TaskRunLevel.Highest;
                    td.RegistrationInfo.Description = "Start UCU";
                    td.Settings.DisallowStartIfOnBatteries = false;
                    td.Settings.StopIfGoingOnBatteries = false;
                    td.Settings.DisallowStartOnRemoteAppSession = false;

                    td.Triggers.Add(new LogonTrigger());

                    string path = System.Reflection.Assembly.GetEntryAssembly().Location;
                    path = path.Replace("Uniwill Control Utility.dll", "Uniwill Control Utility.exe");

                    td.Actions.Add(path);

                    ts.RootFolder.RegisterTaskDefinition(@"UCU", td);
                }
            }

            Settings.Default.StartOnBoot = true;
            Settings.Default.Save();
        }

        private void cbStartBoot_Unchecked(object sender, RoutedEventArgs e)
        {
            using (TaskService ts = new TaskService())
            {
                if (ts.RootFolder.AllTasks.Any(t => t.Name == "UCU"))
                {
                    ts.RootFolder.DeleteTask("UCU");
                }
            }

            Settings.Default.StartOnBoot = false;
            Settings.Default.Save();
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

        private void cbApplyStart_Checked(object sender, RoutedEventArgs e)
        {
            Settings.Default.ApplyOnStart = true;
            Settings.Default.Save();
        }

        private void cbApplyStart_Unchecked(object sender, RoutedEventArgs e)
        {
            Settings.Default.ApplyOnStart = false;
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

        private void cbTrack_Checked(object sender, RoutedEventArgs e)
        {
            Settings.Default.isTrack = true;
            Settings.Default.Save();
        }

        private void cbTrack_Unchecked(object sender, RoutedEventArgs e)
        {
            Settings.Default.isTrack = false;
            Settings.Default.Save();
        }

        private void cbHideFlydigi_Checked(object sender, RoutedEventArgs e)
        {
            Settings.Default.HideFlydigi = true;
            Settings.Default.Save();

            // Disconnect the Flydigi cooler if connected (fire-and-forget to avoid UI freeze)
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var coolerService = App.GetService<FlydigiCoolerService>();
                    coolerService?.DisconnectAsync();

                    var bs1Service = App.GetService<Bs1Service>();
                    bs1Service?.DisconnectAsync().Wait();
                }
                catch { /* ignore disconnect errors */ }
            });
        }

        private void cbHideFlydigi_Unchecked(object sender, RoutedEventArgs e)
        {
            Settings.Default.HideFlydigi = false;
            Settings.Default.Save();
        }

        #endregion

        private void nudAutoReapply_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            Settings.Default.AutoReapplyTime = (int)nudAutoReapply.Value;
            Settings.Default.Save();
        }

        private void nudAutoReapply_ValueChanged(object sender, RoutedEventArgs e)
        {
            if (nudAutoReapply != null && nudAutoReapply.Value != null)
            {
                Settings.Default.AutoReapplyTime = (int)nudAutoReapply.Value;
                Settings.Default.Save();
            }
        }

        private async void btnCheck_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            checkUpdate(true);
        }

        private async void checkUpdate(bool isUserCheck = false)
        {
            if (IsInternetAvailable())
            {
                var updateManager = new UpdateManager("jgudec", "Uniwill-Control-Utility", App.version, "C:\\");

                var isUpdateAvailable = await updateManager.IsUpdateAvailable();

                if (isUpdateAvailable)
                {
                    if (updateManager._newVersion.StartsWith("3.")) tbDownloadMsg.Text = LocalizationService.Get("Head to the Phantom Control Centre GitHub releases page to easily download the latest build!");
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
            var updateManager = new UpdateManager("jgudec", "Uniwill-Control-Utility", App.version, "C:\\");

            var isUpdateAvailable = await updateManager.IsUpdateAvailable();

            if (isUpdateAvailable)
            {
                tbDownloadMsg.Text = LocalizationService.Get("Uniwill Control Utility will close and the installer will open when the download is complete");

                await updateManager.DownloadAndInstallUpdate();

                string filePath = "C:\\Uniwill.Control.Utility.msi";

                try
                {
                    // show the MSI and close the main application
                    Process p = new Process();
                    p.StartInfo.FileName = "msiexec";
                    p.StartInfo.Arguments = $"/i {filePath}";
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
                    var result = ping.Send("8.8.8.8", 2000); // ping Google DNS server
                    return result.Status == IPStatus.Success;
                }
            }
            catch
            {
                return false;
            }
        }

        private void StackPanel_Loaded(object sender, RoutedEventArgs e)
        {

        }

        private async void UiPage_Loaded(object sender, RoutedEventArgs e)
        {
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
            this.Dispatcher.Invoke(() =>
            {
                applyDeviceInfo();
                applyCPUInfo();
                applyRAMInfo();
            });
        }

        private void btnStressTest_Click(object sender, RoutedEventArgs e)
        {
            if (File.Exists(Settings.Default.Path + @"\Assets\Stress-Test\AVX2 Stress Test.exe"))
            {
                Process process = new Process();
                process.StartInfo.FileName = Settings.Default.Path + @"\Assets\Stress-Test\AVX2 Stress Test.exe";
                process.Start();

                process.Dispose();
                process = null;
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
                DefaultExt = ".UCUpresets",
                FileName = $"UCU-Presets-{DateTime.Now:yyyy-MM-dd}.UCUpresets",
                Filter = $"{LocalizationService.Get("UCU preset backup")} (*.UCUpresets)|*.UCUpresets"
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
                    LocalizationService.Get("Preset backup saved"),
                    LocalizationService.Format("Backed up {0} custom presets and {1} adaptive mode presets.", result.CustomPresetCount, result.AdaptivePresetCount),
                    Wpf.Ui.Controls.InfoBarSeverity.Success);
            }
            catch (Exception exception)
            {
                DiagnosticLogger.LogError(exception, "Failed to back up presets");
                ShowPresetBackupStatus(
                    LocalizationService.Get("Preset backup failed"),
                    LocalizationService.Format("The presets could not be backed up.\n\n{0}", exception.Message),
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
                DefaultExt = ".UCUpresets",
                Filter = $"{LocalizationService.Get("UCU preset backup")} (*.UCUpresets;*.json)|*.UCUpresets;*.json"
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
                    LocalizationService.Get("Preset import complete"),
                    LocalizationService.Format("Imported {0} custom presets and {1} adaptive mode presets.", result.CustomPresetCount, result.AdaptivePresetCount),
                    Wpf.Ui.Controls.InfoBarSeverity.Success);
            }
            catch (InvalidDataException)
            {
                ShowPresetBackupStatus(
                    LocalizationService.Get("Preset import failed"),
                    LocalizationService.Get("The selected file is not a valid UCU preset backup."),
                    Wpf.Ui.Controls.InfoBarSeverity.Error);
            }
            catch (Exception exception)
            {
                DiagnosticLogger.LogError(exception, "Failed to import presets");
                ShowPresetBackupStatus(
                    LocalizationService.Get("Preset import failed"),
                    LocalizationService.Format("The presets could not be imported.\n\n{0}", exception.Message),
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
    }
}
