using System.Windows;
using System.Windows.Controls;
using Universal_x86_Tuning_Utility.Services;
using Wpf.Ui;

namespace Universal_x86_Tuning_Utility.Views.Controls
{
    public partial class HydroUiControl : UserControl
    {
        private INavigationService? _navigationService;

        public HydroUiControl()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Sets the navigation service so the "Connect" button can navigate to the Watercooler page.
        /// </summary>
        public void SetNavigationService(INavigationService navigationService)
        {
            _navigationService = navigationService;
        }

        public void UpdateMetrics(DeviceMetricsSnapshot snapshot)
        {
            if (snapshot.HydroUiConnected)
            {
                this.Visibility = Visibility.Visible;
                spConnected.Visibility = Visibility.Visible;
                spDisconnected.Visibility = Visibility.Collapsed;

                string pumpText = snapshot.HydroUiPumpVoltage switch
                {
                    0 => "Off",
                    7 => "7V",
                    8 => "8V",
                    11 => "11V",
                    _ => $"Unknown ({snapshot.HydroUiPumpVoltage})"
                };
                _pumpLabel.Content = pumpText;

                if (snapshot.HydroUiFanRpm > 0)
                {
                    _fanBar.Value = 100;
                    _fanLabel.Content = $"{snapshot.HydroUiFanRpm} RPM";
                }
                else if (snapshot.HydroUiFanSpeed > 0)
                {
                    _fanBar.Value = snapshot.HydroUiFanSpeed;
                    _fanLabel.Content = $"{snapshot.HydroUiFanSpeed}%";
                }
                else
                {
                    _fanBar.Value = 0;
                    _fanLabel.Content = "Off";
                }
            }
            else
            {
                // Show the control but display the disconnected state
                this.Visibility = Visibility.Visible;
                spConnected.Visibility = Visibility.Collapsed;
                spDisconnected.Visibility = Visibility.Visible;
            }
        }

        private void OnConnectClick(object sender, RoutedEventArgs e)
        {
            _navigationService?.Navigate("watercooler");
        }
    }
}
