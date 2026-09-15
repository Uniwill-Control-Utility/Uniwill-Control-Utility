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

        public PrerequisiteDialog()
        {
            InitializeComponent();
            UpdateStatuses();
        }

        private void UpdateStatuses()
        {
            UpdatePawnIoStatus();
            UpdateRtssStatus();
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
