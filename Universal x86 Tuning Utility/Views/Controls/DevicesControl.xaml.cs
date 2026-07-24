using System.Windows;
using System.Windows.Controls;
using Universal_x86_Tuning_Utility.Services;

namespace Universal_x86_Tuning_Utility.Views.Controls
{
    public partial class DevicesControl : UserControl
    {
        public DevicesControl()
        {
            InitializeComponent();
        }

        public void UpdateMetrics(DeviceMetricsSnapshot snapshot)
        {
            bool hydroVisible = snapshot.HydroUiConnected;
            bool flydigiVisible = snapshot.FlydigiConnected;

            // Hide entire card if nothing connected
            this.Visibility = (hydroVisible || flydigiVisible)
                ? Visibility.Visible
                : Visibility.Collapsed;

            // Hydro UI section
            _hydroTitle.Visibility = hydroVisible ? Visibility.Visible : Visibility.Collapsed;
            _hydroMetrics.Visibility = hydroVisible ? Visibility.Visible : Visibility.Collapsed;
            _hydroFanRow.Visibility = hydroVisible ? Visibility.Visible : Visibility.Collapsed;

            if (hydroVisible)
            {
                // Pump: 0-11V
                _pumpBar.Value = snapshot.HydroUiPumpVoltage;
                _pumpLabel.Content = snapshot.HydroUiPumpVoltage > 0
                    ? $"{snapshot.HydroUiPumpVoltage}V"
                    : "Off";

                // Fan: 0-100%
                int fanPercent = snapshot.HydroUiFanRpm > 0 ? 100 : snapshot.HydroUiFanSpeed;
                _fanBar.Value = fanPercent;
                _fanLabel.Content = snapshot.HydroUiFanRpm > 0
                    ? $"{snapshot.HydroUiFanRpm} RPM"
                    : (snapshot.HydroUiFanSpeed > 0 ? $"{snapshot.HydroUiFanSpeed}%" : "Off");
            }

            // Flydigi section
            _flydigiSection.Visibility = flydigiVisible ? Visibility.Visible : Visibility.Collapsed;

            if (flydigiVisible)
            {
                int rpm = snapshot.FlydigiFanRpm;

                // Clamp RPM to progress bar range
                if (rpm > 0)
                {
                    _flydigiFanBar.Value = rpm;
                    _flydigiFanLabel.Content = $"{rpm} RPM";
                }
                else
                {
                    _flydigiFanBar.Value = 1300; // minimum
                    _flydigiFanLabel.Content = "Off";
                }

                _flydigiRgbLabel.Content = string.IsNullOrEmpty(snapshot.FlydigiRgbMode)
                    ? "--"
                    : snapshot.FlydigiRgbMode;
            }
        }
    }
}
