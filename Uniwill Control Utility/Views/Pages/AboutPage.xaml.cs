using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
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

        private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
        {
            var appPath = Path.GetDirectoryName(Assembly.GetEntryAssembly()?.Location ?? AppDomain.CurrentDomain.BaseDirectory) ?? ".";
            var logFolder = Path.Combine(appPath, "logs");

            if (Directory.Exists(logFolder))
            {
                Process.Start(new ProcessStartInfo(logFolder) { UseShellExecute = true });
            }

            e.Handled = true;
        }
    }
}
