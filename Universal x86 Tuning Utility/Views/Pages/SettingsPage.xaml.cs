using Microsoft.Win32.TaskScheduler;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Universal_x86_Tuning_Utility.Properties;
using Universal_x86_Tuning_Utility.Scripts;
using Universal_x86_Tuning_Utility.Scripts.Misc;
using Universal_x86_Tuning_Utility.Services;
using Wpf.Ui.Abstractions.Controls;
using System.Diagnostics.Eventing.Reader;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace Universal_x86_Tuning_Utility.Views.Pages
{
    public partial class SettingsPage : INavigableView<ViewModels.SettingsViewModel>
    {
        private readonly ILogger<SettingsPage> _logger;
        private bool _languageSelectionReady;

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
            cbAutoReapply.IsChecked = Settings.Default.AutoReapply;
            nudAutoReapply.Value = Settings.Default.AutoReapplyTime;
            nudAutoReapply.Text = Convert.ToString(Settings.Default.AutoReapplyTime);
            cbAutoCheck.IsChecked = Settings.Default.UpdateCheck;
            cbAdaptive.IsChecked = Settings.Default.isStartAdpative;
            cbTrack.IsChecked = Settings.Default.isTrack;

            cbxLogLevel.SelectedIndex = Settings.Default.DiagnosticLogLevel;

            tbAppVerion.Text = $"Universal x86 Tuning Utility - {GetAssemblyVersion()}";

            // Initialize system info
            getDeviceInfo();
            getCPUInfo();
            getRAMInfo();
            if (SystemInformation.PowerStatus.BatteryChargeStatus != BatteryChargeStatus.NoSystemBattery)
            {
                getBatteryInfo();
            }
            else
            {
                sdBattery.Visibility = Visibility.Collapsed;
            }

            checkUpdate();
        }
        private string GetAssemblyVersion()
        {
            return System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? String.Empty;
        }

        #region System Info

        private void getDeviceInfo()
        {
            tbDeviceName.Text = GetSystemInfo.SystemName;
            tbDeviceModel.Text = GetSystemInfo.Product;
            tbDeviceProducer.Text = GetSystemInfo.Manufacturer;
        }

        private async void getCPUInfo()
        {
            try
            {
                sdCPU.Visibility = Visibility.Collapsed;
                ManagementObjectSearcher searcher = new ManagementObjectSearcher("root\\CIMV2", "SELECT * FROM Win32_Processor");

                string name = "";
                string description = "";
                string manufacturer = "";
                int numberOfCores = 0;
                int numberOfLogicalProcessors = 0;
                double l2Size = 0;
                double l3Size = 0;
                string baseClock = "";

                await System.Threading.Tasks.Task.Run(() =>
                {
                    foreach (ManagementObject queryObj in searcher.Get())
                    {
                        name = queryObj["Name"].ToString();
                        description = queryObj["Description"].ToString();
                        manufacturer = queryObj["Manufacturer"].ToString();
                        numberOfCores = Convert.ToInt32(queryObj["NumberOfCores"]);
                        numberOfLogicalProcessors = Convert.ToInt32(queryObj["NumberOfLogicalProcessors"]);
                        l2Size = Convert.ToDouble(queryObj["L2CacheSize"]) / 1024;
                        l3Size = Convert.ToDouble(queryObj["L3CacheSize"]) / 1024;
                        baseClock = queryObj["MaxClockSpeed"].ToString();
                    }
                });

                tbProcessor.Text = name;
                tbCaption.Text = description;
                string codeName = GetSystemInfo.Codename();
                if (codeName != "")
                {
                    tbCodename.Text = codeName;
                }
                else
                {
                    tbCode.Visibility = Visibility.Collapsed;
                    tbCodename.Visibility = Visibility.Collapsed;
                }

                tbProducer.Text = manufacturer;
                if (numberOfLogicalProcessors == numberOfCores)
                    tbCores.Text = numberOfCores.ToString();
                else
                    tbCores.Text = GetSystemInfo.getBigLITTLE(numberOfCores, l2Size);
                tbThreads.Text = numberOfLogicalProcessors.ToString();
                tbL3Cache.Text = $"{l3Size.ToString("0.##")} MB";

                uint sum = 0;
                foreach (uint number in GetSystemInfo.GetCacheSizes(GetSystemInfo.CacheLevel.Level1))
                    sum += number;
                decimal total = sum / 1024;
                tbL1Cache.Text = $"{total.ToString("0.##")} MB";

                sum = 0;
                foreach (uint number in GetSystemInfo.GetCacheSizes(GetSystemInfo.CacheLevel.Level2))
                    sum += number;
                total = sum / 1024;
                tbL2Cache.Text = $"{total.ToString("0.##")} MB";

                tbBaseClock.Text = $"{baseClock} MHz";
                tbInstructions.Text = GetSystemInfo.InstructionSets();
                sdCPU.Visibility = Visibility.Visible;
            }
            catch (ManagementException ex)
            {
                Console.WriteLine("An error occurred while querying for WMI data: " + ex.Message);
            }
        }

        private async void getRAMInfo()
        {
            try
            {
                sdRAM.Visibility = Visibility.Collapsed;
                double capacity = 0;
                int speed = 0;
                int type = 0;
                int width = 0;
                string producer = "";
                string model = "";
                int slots = 0;

                ManagementObjectSearcher searcher = new ManagementObjectSearcher("root\\CIMV2", "SELECT * FROM Win32_PhysicalMemory");

                await System.Threading.Tasks.Task.Run(() =>
                {
                    foreach (ManagementObject queryObj in searcher.Get())
                    {
                        if (producer == "") producer = queryObj["Manufacturer"].ToString();
                        else if (!producer.Contains(queryObj["Manufacturer"].ToString()))
                            producer = $"{producer}/{queryObj["Manufacturer"]}";

                        if (model == "") model = queryObj["PartNumber"].ToString();
                        else if (!model.Contains(queryObj["PartNumber"].ToString()))
                            model = $"{model}/{queryObj["PartNumber"]}";

                        capacity = capacity + Convert.ToDouble(queryObj["Capacity"]);
                        speed = Convert.ToInt32(queryObj["ConfiguredClockSpeed"]);
                        type = Convert.ToInt32(queryObj["SMBIOSMemoryType"]);
                        width = width + Convert.ToInt32(queryObj["DataWidth"]);
                        slots++;
                    }
                });

                if (width > 128 && Family.FAM == Family.RyzenFamily.StrixHalo)
                    if (width > 256) width = 256;
                    else if (width > 64 && Family.FAM == Family.RyzenFamily.Mendocino) width = 64;
                    else if (width > 128 && Family.FAM < Family.RyzenFamily.FireRange && Family.TYPE != Family.ProcessorType.Intel)
                        width = 128;

                capacity = capacity / 1024 / 1024 / 1024;

                string DDRType = type switch
                {
                    20 => "DDR",
                    21 => "DDR2",
                    24 => "DDR3",
                    26 => "DDR4",
                    30 => "LPDDR4",
                    34 => "DDR5",
                    35 => "LPDDR5",
                    _ => $"Unknown ({type})"
                };

                tbRAM.Text = $"{capacity} GB {DDRType} @ {speed} MT/s";
                tbRAMProducer.Text = producer;
                tbRAMModel.Text = model.Replace(" ", null!);
                tbWidth.Text = $"{width} bit";
                tbSlots.Text = $"{slots} * {width / slots} bit";

                sdRAM.Visibility = Visibility.Visible;
            }
            catch { }
        }

        private void getBatteryInfo()
        {
            try
            {
                tbHealth.Text = $"{(GetSystemInfo.GetBatteryHealth() * 100).ToString("0.##")}%";
                tbCycle.Text = $"{GetSystemInfo.GetBatteryCycle()}";
                tbCapcity.Text = LocalizationService.Format("Full Charge: {0} mAh | Design: {1} mAh",
                    GetSystemInfo.ReadFullChargeCapacity(), GetSystemInfo.ReadDesignCapacity());

                tbChargeRate.Text = $"{(GetSystemInfo.GetBatteryRate() / 1000).ToString("0.##")}W";

                DispatcherTimer bat = new DispatcherTimer();
                bat.Interval = TimeSpan.FromSeconds(2);
                bat.Tick += Bat_Tick;
                bat.Start();
            }
            catch
            {
                sdBattery.Visibility = Visibility.Collapsed;
            }
        }

        private async void Bat_Tick(object sender, EventArgs e)
        {
            decimal batRate = 0;
            await System.Threading.Tasks.Task.Run(() => batRate = GetSystemInfo.GetBatteryRate() / 1000);
            tbChargeRate.Text = $"{batRate.ToString("0.##")}W";
        }

        #endregion

        private void cbStartBoot_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            using (TaskService ts = new TaskService())
            {
                if (ts.RootFolder.AllTasks.Any(t => t.Name == "UXTU"))
                {
                    // Remove the task we just created
                    ts.RootFolder.DeleteTask("UXTU");
                }
            }

            if (cbStartBoot.IsChecked == true)
            {
                // Get the service on the local machine
                using (TaskService ts = new TaskService())
                {
                    if (!ts.RootFolder.AllTasks.Any(t => t.Name == "UXTU"))
                    {
                        // Create a new task definition and assign properties
                        TaskDefinition td = ts.NewTask();
                        td.Principal.RunLevel = TaskRunLevel.Highest;
                        td.RegistrationInfo.Description = "Start UXTU";
                        td.Settings.DisallowStartIfOnBatteries = false;
                        td.Settings.StopIfGoingOnBatteries = false;
                        td.Settings.DisallowStartOnRemoteAppSession = false;

                        // Create a trigger that will fire the task at this time every other day
                        td.Triggers.Add(new LogonTrigger());

                        string path = System.Reflection.Assembly.GetEntryAssembly().Location;
                        path = path.Replace("Universal x86 Tuning Utility.dll", "Universal x86 Tuning Utility.exe");

                        // Create an action that will launch Notepad whenever the trigger fires
                        td.Actions.Add(path);

                        // Register the task in the root folder
                        ts.RootFolder.RegisterTaskDefinition(@"UXTU", td);
                    }
                }
            }

            Settings.Default.StartOnBoot = (bool)cbStartBoot.IsChecked;
            Settings.Default.Save();
        }

        private void cbStartMini_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            Settings.Default.StartMini = (bool)cbStartMini.IsChecked;
            Settings.Default.Save();
        }

        private void cbMinimizeClose_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            Settings.Default.MinimizeClose = (bool)cbMinimizeClose.IsChecked;
            Settings.Default.Save();
        }

        private void cbAutoReapply_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            Settings.Default.AutoReapply = (bool)cbAutoReapply.IsChecked;
            Settings.Default.AutoReapplyTime = (int)nudAutoReapply.Value;
            Settings.Default.Save();
        }

        private void nudAutoReapply_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            Settings.Default.AutoReapplyTime = (int)nudAutoReapply.Value;
            Settings.Default.Save();
        }

        private void cbApplyStart_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            Settings.Default.ApplyOnStart = (bool)cbApplyStart.IsChecked;
            Settings.Default.Save();
        }

        private async void btnCheck_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            checkUpdate(true);
        }

        private async void checkUpdate(bool isUserCheck = false)
        {
            if (IsInternetAvailable())
            {
                var updateManager = new UpdateManager("JamesCJ60", "Universal-x86-Tuning-Utility", App.version, "C:\\");

                var isUpdateAvailable = await updateManager.IsUpdateAvailable();

                if (isUpdateAvailable)
                {
                    if (updateManager._newVersion.StartsWith("3.")) tbDownloadMsg.Text = LocalizationService.Get("Head to the Phantom Control Centre GitHub releases page to easily download the latest build!");
                    else {
                        tbDownloadMsg.Text = LocalizationService.Get("An update for Universal x86 Tuning Utility has been found!");
                        btnDownload.Visibility = System.Windows.Visibility.Visible;
                    }
                }
                else if(isUserCheck) tbDownloadMsg.Text = LocalizationService.Get("Universal x86 Tuning Utility is up to date!");
            }
            else if (isUserCheck) tbDownloadMsg.Text = LocalizationService.Get("No internet connection!");
        }

        private async void btnDownload_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            var updateManager = new UpdateManager("JamesCJ60", "Universal-x86-Tuning-Utility", App.version, "C:\\");

            var isUpdateAvailable = await updateManager.IsUpdateAvailable();

            if (isUpdateAvailable)
            {
                tbDownloadMsg.Text = LocalizationService.Get("Universal x86 Tuning Utility will close and the installer will open when the download is complete");

                await updateManager.DownloadAndInstallUpdate();

                string filePath = "C:\\Universal.x86.Tuning.Utility.msi";

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

        private void cbAutoCheck_Click(object sender, RoutedEventArgs e)
        {
            Settings.Default.UpdateCheck = (bool)cbAutoCheck.IsChecked;
            Settings.Default.Save();
        }

        private void StackPanel_Loaded(object sender, RoutedEventArgs e)
        {

        }

        private void UiPage_Loaded(object sender, RoutedEventArgs e)
        {
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

        private void cbAdaptive_Click(object sender, RoutedEventArgs e)
        {
            Settings.Default.isStartAdpative = (bool)cbAdaptive.IsChecked;
            Settings.Default.Save();
        }

        private void cbTrack_Click(object sender, RoutedEventArgs e)
        {
            Settings.Default.isTrack = (bool)cbTrack.IsChecked;
            Settings.Default.Save();
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

        private void nudAutoReapply_ValueChanged(object sender, RoutedEventArgs e)
        {
            if (nudAutoReapply != null && nudAutoReapply.Value != null)
            {
                Settings.Default.AutoReapplyTime = (int)nudAutoReapply.Value;
                Settings.Default.Save();
            }
        }

        private async void btnBackupPresets_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog
            {
                AddExtension = true,
                DefaultExt = ".uxtupresets",
                FileName = $"UXTU-Presets-{DateTime.Now:yyyy-MM-dd}.uxtupresets",
                Filter = $"{LocalizationService.Get("UXTU preset backup")} (*.uxtupresets)|*.uxtupresets"
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
                DefaultExt = ".uxtupresets",
                Filter = $"{LocalizationService.Get("UXTU preset backup")} (*.uxtupresets;*.json)|*.uxtupresets;*.json"
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
                    LocalizationService.Get("The selected file is not a valid UXTU preset backup."),
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
