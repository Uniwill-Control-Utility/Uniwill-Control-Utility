using System.Windows;
using System.Windows.Controls;
using Uniwill_Control_Utility.Services;
using Wpf.Ui;

namespace Uniwill_Control_Utility.Views.Controls
{
    public partial class HydroUiControl : UserControl
    {
        private const string ConnectedColor = "#4BD879";

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

                // Connection status pill
                _statusDot.Fill = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(ConnectedColor)!;
                _statusText.Text = "Connected";

                // Smart mode pill
                _smartModePill.Visibility = snapshot.HydroUiSmartModeActive ? Visibility.Visible : Visibility.Collapsed;

                // Pump
                string pumpText = snapshot.HydroUiPumpVoltage switch
                {
                    0 => "Off",
                    7 => "7V",
                    8 => "8V",
                    11 => "11V",
                    _ => $"Unknown ({snapshot.HydroUiPumpVoltage})"
                };
                _pumpLabel.Text = pumpText;
                _pumpBar.Value = snapshot.HydroUiPumpVoltage;

                // Fan
                if (snapshot.HydroUiFanRpm > 0)
                {
                    _fanBar.Value = 100;
                    _fanLabel.Text = $"{snapshot.HydroUiFanRpm} RPM";
                }
                else if (snapshot.HydroUiFanSpeed > 0)
                {
                    _fanBar.Value = snapshot.HydroUiFanSpeed;
                    _fanLabel.Text = $"{snapshot.HydroUiFanSpeed}%";
                }
                else
                {
                    _fanBar.Value = 0;
                    _fanLabel.Text = "Off";
                }
            }
            else
            {
                // Hide the card entirely when not connected so the Dashboard
                // can center the remaining card or hide the row altogether.
                this.Visibility = Visibility.Collapsed;
            }
        }
    }
}
