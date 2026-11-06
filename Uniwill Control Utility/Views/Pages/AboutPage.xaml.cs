using Serilog;
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Uniwill_Control_Utility.Services;

namespace Uniwill_Control_Utility.Views.Pages
{
    public partial class AboutPage : Page
    {
        private static string VersionText
        {
            get
            {
                var version = Assembly.GetEntryAssembly()?.GetName().Version;
                if (version is null)
                    return string.Empty;
                return version.ToString();
            }
        }

        private static string CopyrightText
        {
            get
            {
                var location = Assembly.GetEntryAssembly()?.Location;
                if (location is null)
                    return string.Empty;
                return FileVersionInfo.GetVersionInfo(location).LegalCopyright ?? string.Empty;
            }
        }

        public AboutPage()
        {
            InitializeComponent();

            tbVersion.Text = LocalizationService.Format("Version {0}", VersionText);
            tbCopyright.Text = CopyrightText;

            // Velopack updates only work for installed (MSI) layouts; on a
            // portable zip the check/download controls have nothing to act on.
            var updateService = App.GetService<VelopackUpdateService>();
            if (updateService is { IsInstalled: false })
            {
                btnCheck.Visibility = Visibility.Collapsed;
                btnDownload.Visibility = Visibility.Collapsed;
                tbDownloadMsg.Text = LocalizationService.Get("Updates are not available for this installation type (portable)");
            }
            else if (updateService is not null)
            {
                // Reflect a package staged in a previous session.
                ShowPendingUpdate(updateService);
            }
        }

        private void ShowPendingUpdate(VelopackUpdateService updateService)
        {
            if (updateService.State == UpdateState.PendingApply)
            {
                tbDownloadMsg.Text = LocalizationService.Get("Update downloaded");
                btnDownload.Visibility = Visibility.Visible;
                btnDownloadText.Text = LocalizationService.Get("Update and restart");
                btnDownloadIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.ArrowSync20;
            }
        }

        private async void btnCheck_Click(object sender, RoutedEventArgs e)
        {
            var updateService = App.GetService<VelopackUpdateService>();
            if (updateService is null || !updateService.IsInstalled)
                return;

            if (!App.IsInternetAvailable())
            {
                tbDownloadMsg.Text = LocalizationService.Get("No internet connection!");
                return;
            }

            tbDownloadMsg.Text = LocalizationService.Get("Checking for updates");
            btnCheck.IsEnabled = false;
            try
            {
                var info = await updateService.CheckForUpdatesAsync();
                if (info is not null)
                {
                    tbDownloadMsg.Text = LocalizationService.Get("An update for Uniwill Control Utility has been found!");
                    btnDownload.Visibility = Visibility.Visible;
                    btnDownloadText.Text = LocalizationService.Get("Download Update");
                    btnDownloadIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.ArrowDownload20;
                }
                else
                {
                    tbDownloadMsg.Text = LocalizationService.Get("Uniwill Control Utility is up to date!");
                }
            }
            catch (Exception ex)
            {
                tbDownloadMsg.Text = LocalizationService.Get("Failed to check for updates");
                Log.Error(ex, "[Updates] Manual update check failed");
            }
            finally
            {
                btnCheck.IsEnabled = true;
            }
        }

        private async void btnDownload_Click(object sender, RoutedEventArgs e)
        {
            var updateService = App.GetService<VelopackUpdateService>();
            if (updateService is null || !updateService.IsInstalled)
                return;

            // Second click (package already staged): apply and restart now.
            if (updateService.State == UpdateState.PendingApply)
            {
                updateService.ApplyAndRestart();
                return;
            }

            tbDownloadMsg.Text = LocalizationService.Format("Downloading update: {0}%", 0);
            var progress = new Progress<int>(value =>
                tbDownloadMsg.Text = LocalizationService.Format("Downloading update: {0}%", value));
            btnDownload.IsEnabled = false;
            try
            {
                await updateService.DownloadAsync(progress);

                // Staged: applies automatically on graceful exit; offer now.
                tbDownloadMsg.Text = LocalizationService.Get("Update downloaded");
                btnDownloadText.Text = LocalizationService.Get("Update and restart");
                btnDownloadIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.ArrowSync20;
            }
            catch (Exception ex)
            {
                tbDownloadMsg.Text = LocalizationService.Get("Failed to download update");
                Log.Error(ex, "[Updates] Update download failed");
            }
            finally
            {
                btnDownload.IsEnabled = true;
            }
        }

        private void OpenLink_Click(object sender, RoutedEventArgs e)
        {
            if (e.Source is System.Windows.Controls.Button button && button.Tag is string uri)
            {
                Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
                e.Handled = true;
            }
        }

        private void OpenAppDataFolder_Click(object sender, RoutedEventArgs e)
        {
            var appDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UCU");

            if (Directory.Exists(appDataFolder))
            {
                Process.Start(new ProcessStartInfo(appDataFolder) { UseShellExecute = true });
            }

            e.Handled = true;
        }

        private async void ExportDiagnostics_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                AddExtension = true,
                DefaultExt = ".zip",
                FileName = $"UCU-Diagnostics-{DateTime.Now:yyyy-MM-dd-HHmm}.zip",
                Filter = LocalizationService.Get("Diagnostics bundle (*.zip)|*.zip"),
            };

            if (dialog.ShowDialog() != true)
                return;

            btnExportDiagnostics.IsEnabled = false;
            try
            {
                DiagnosticsExportService? service = App.GetService<DiagnosticsExportService>();
                if (service is null)
                    return;

                await service.ExportAsync(dialog.FileName);
                Scripts.Misc.ToastNotification.ShowToastNotification(
                    LocalizationService.Get("Diagnostics"),
                    LocalizationService.Get("Diagnostics bundle exported."));
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[About] diagnostics export failed");
                Scripts.Misc.ToastNotification.ShowToastNotification(
                    LocalizationService.Get("Diagnostics"),
                    LocalizationService.Get("The diagnostics bundle could not be written."));
            }
            finally
            {
                btnExportDiagnostics.IsEnabled = true;
            }
        }

        private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
        {
            // Both logs, because a field report is rarely answered by one of them:
            // the app log holds the preset/fan decisions, the engine log holds what
            // reached the EC. The app folder is per-user (App.LOGS_FOLDER) - in a
            // per-machine install the exe's own directory is not writable at all -
            // and the engine's lives under ProgramData, readable by an admin.
            string engineFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "UCU",
                "engine");

            if (Directory.Exists(App.LOGS_FOLDER))
                Process.Start(new ProcessStartInfo(App.LOGS_FOLDER) { UseShellExecute = true });

            if (Directory.Exists(engineFolder))
                Process.Start(new ProcessStartInfo(engineFolder) { UseShellExecute = true });

            e.Handled = true;
        }
    }
}
