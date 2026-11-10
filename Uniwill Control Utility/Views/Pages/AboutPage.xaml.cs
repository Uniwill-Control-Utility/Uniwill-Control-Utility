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

            // No in-app updater exists, so nothing here downloads or applies a
            // package: the button only asks GitHub whether a newer release exists,
            // and a newer one offers the release page where the installer
            // (Uniwill-Control-Utility-win.msi) is downloaded and run by hand. The
            // sentence and the button labels live here rather than in XAML so all
            // user-visible text travels through the catalogues.
            tbDownloadMsg.Text = LocalizationService.Get("Updates ship as Uniwill-Control-Utility-win.msi.");
            btnCheckText.Text = LocalizationService.Get("Check for updates");
            btnDownloadText.Text = LocalizationService.Get("Download installer");
        }

        private string? _releasePageUrl;

        private async void btnCheck_Click(object sender, RoutedEventArgs e)
        {
            btnCheck.IsEnabled = false;
            btnDownload.Visibility = Visibility.Collapsed;
            tbDownloadMsg.Text = LocalizationService.Get("Checking for updates...");

            try
            {
                UpdateChecker.ReleaseInfo? latest = await UpdateChecker.GetLatestAsync();
                if (latest is null)
                {
                    tbDownloadMsg.Text = LocalizationService.Get("Could not check for updates.");
                    return;
                }

                Version? current = Assembly.GetEntryAssembly()?.GetName().Version;
                if (current is null || !UpdateChecker.IsNewer(current, latest.Version))
                {
                    tbDownloadMsg.Text = LocalizationService.Format(
                        "You're on the latest release (v{0}).", latest.Version.ToString(3));
                    return;
                }

                tbDownloadMsg.Text = LocalizationService.Format(
                    "v{0} is available. Download and run the installer.", latest.Version.ToString(3));
                _releasePageUrl = latest.HtmlUrl;
                btnDownload.Visibility = Visibility.Visible;
            }
            finally
            {
                btnCheck.IsEnabled = true;
            }
        }

        private void btnDownload_Click(object sender, RoutedEventArgs e)
        {
            Process.Start(new ProcessStartInfo(_releasePageUrl ?? UpdateChecker.ReleasesPageUrl)
            {
                UseShellExecute = true,
            });
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
