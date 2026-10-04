using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Uniwill_Control_Utility.Services;
using Uniwill_Control_Utility.Services.Performance;
using Wpf.Ui.Controls;

namespace Uniwill_Control_Utility.Views.Windows
{
    public partial class PrerequisiteDialog : FluentWindow
    {
        private const string PawnIoUrl = "https://pawnio.eu/";
        private const string RtssUrl = "https://www.guru3d.com/download/rtss-rivatuner-statistics-server-download/";
        private const string TuxedoControlCenterUrl = "https://mytuxedo.de/public.php/dav/files/ZeB8FTf8CrpEtJr/Stellaris_Series/Stellaris_Gen7_AMD/ControlCenter_5.x_2025.zip";

        public PrerequisiteDialog()
        {
            InitializeComponent();
            UpdateStatuses();
        }

        private void UpdateStatuses()
        {
            UpdateEcDriverStatus();
            UpdatePawnIoStatus();
            UpdateRtssStatus();
        }

        private void UpdateEcDriverStatus()
        {
            bool installed = EcDriverDetectionService.IsInstalled();

            EcDriverStatusIcon.Symbol = installed ? SymbolRegular.CheckmarkCircle20 : SymbolRegular.Warning20;
            EcDriverStatusIcon.Foreground = FindResource("SystemFillColor" + (installed ? "Success" : "Caution") + "Brush") as Brush;

            if (installed)
            {
                UpdateButtonContent(EcDriverButton, true, "Installed", "Download");
                EcDriverDescription.Text = "Kernel driver required for EC communication (fan control, battery, power limits). This driver is installed and ready to use.";
            }
            else
            {
                // Determine what to show based on detected laptop brand
                var model = LaptopDetector.Detected;
                string? manufacturer = model?.SystemManufacturer;

                if (manufacturer != null && (manufacturer.Equals("SchenkerTechnologiesGmbH", StringComparison.OrdinalIgnoreCase) ||
                                            manufacturer.Equals("PCSpecialist", StringComparison.OrdinalIgnoreCase)))
                {
                    // XMG or PCSpecialist — link to official driver page
                    EcDriverDescription.Text = "Kernel driver required for EC communication (fan control, battery, power limits). Install your manufacturer's Control Center, then uninstall it — the driver persists on disk.";
                    UpdateButtonContent(EcDriverButton, false, "Installed", "Driver Page");
                }
                else
                {
                    // Other brands — generic instructions with Tuxedo fallback
                    EcDriverDescription.Text = "Kernel driver required for EC communication (fan control, battery, power limits). Download your manufacturer's Control Center software. If unavailable, try the Tuxedo Control Center (at your own risk).";
                    UpdateButtonContent(EcDriverButton, false, "Installed", "Download");
                }
            }
        }

        private void UpdatePawnIoStatus()
        {
            bool installed = PawnIoDetectionService.IsInstalled();

            PawnIoStatusIcon.Symbol = installed ? SymbolRegular.CheckmarkCircle20 : SymbolRegular.Warning20;
            PawnIoStatusIcon.Foreground = FindResource("SystemFillColor" + (installed ? "Success" : "Caution") + "Brush") as Brush;

            UpdateButtonContent(PawnIoButton, installed, "Installed", "Install");
        }

        private void UpdateRtssStatus()
        {
            bool running = RtssReader.IsRunning();

            RtssStatusIcon.Symbol = running ? SymbolRegular.CheckmarkCircle20 : SymbolRegular.Warning20;
            RtssStatusIcon.Foreground = FindResource("SystemFillColor" + (running ? "Success" : "Caution") + "Brush") as Brush;

            UpdateButtonContent(RtssButton, running, "Running", "Download");
        }

      private static void UpdateButtonContent(Wpf.Ui.Controls.Button button, bool isReady, string readyText, string actionText)
        {
            button.Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new SymbolIcon
                    {
                        Symbol = isReady ? SymbolRegular.Checkmark20 : SymbolRegular.ArrowDownload20,
                        Margin = new Thickness(0, 0, 6, 0)
                    },
                    new System.Windows.Controls.TextBlock { Text = isReady ? readyText : actionText }
                }
            };

            button.IsEnabled = !isReady;
            button.Appearance = isReady ? Wpf.Ui.Controls.ControlAppearance.Transparent : Wpf.Ui.Controls.ControlAppearance.Primary;
        }

        private void EcDriverButton_Click(object sender, RoutedEventArgs e)
        {
            var model = LaptopDetector.Detected;
            string? manufacturer = model?.SystemManufacturer;

            string url = manufacturer switch
            {
                _ when manufacturer.Equals("SchenkerTechnologiesGmbH", StringComparison.OrdinalIgnoreCase) => LaptopDetector.GetDriverUrl()!,
                _ when manufacturer.Equals("PCSpecialist", StringComparison.OrdinalIgnoreCase) => LaptopDetector.GetDriverUrl()!,
                _ => TuxedoControlCenterUrl
            };

            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }

        private void PawnIoButton_Click(object sender, RoutedEventArgs e)
        {
            new InstallpawnIo().Show();
        }

        private void RtssButton_Click(object sender, RoutedEventArgs e)
        {
            Process.Start(new ProcessStartInfo(RtssUrl) { UseShellExecute = true });
        }

        private void ContinueButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
