using System.Windows;
using System.Windows.Controls;
using Uniwill_Control_Utility.Services;
using Wpf.Ui;

namespace Uniwill_Control_Utility.Views.Controls
{
    public partial class FlydigiControl : UserControl
    {
        private INavigationService? _navigationService;

        public FlydigiControl()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Sets the navigation service so the "Connect" button can navigate to the Flydigi page.
        /// </summary>
        public void SetNavigationService(INavigationService navigationService)
        {
            _navigationService = navigationService;
        }

        public void UpdateMetrics(DeviceMetricsSnapshot snapshot)
        {
            if (snapshot.FlydigiConnected)
            {
                this.Visibility = Visibility.Visible;
                spConnected.Visibility = Visibility.Visible;
                spDisconnected.Visibility = Visibility.Collapsed;

                // Update title with model name
                if (!string.IsNullOrEmpty(snapshot.FlydigiModelName))
                {
                    _titleText.Text = $"Flydigi {snapshot.FlydigiModelName}";
                }

                // BS1 has max 3000 RPM; BS2/BS2 Pro/BS3/BS3 Pro have max 4000 RPM
                int maxRpm = snapshot.FlydigiIsBs1 ? 3000 : 4000;
                _fanBar.Maximum = maxRpm;

                if (snapshot.FlydigiFanRpm > 0)
                {
                    _fanLabel.Content = $"{snapshot.FlydigiFanRpm} RPM";
                    _fanBar.Value = snapshot.FlydigiFanRpm;
                }
                else
                {
                    _fanLabel.Content = "Off";
                    _fanBar.Value = _fanBar.Minimum;
                }

                _rgbLabel.Content = string.IsNullOrEmpty(snapshot.FlydigiRgbMode)
                    ? "--"
                    : snapshot.FlydigiRgbMode;
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
            _navigationService?.Navigate("flydigicooler");
        }
    }
}
