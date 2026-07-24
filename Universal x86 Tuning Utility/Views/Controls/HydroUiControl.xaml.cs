using System.Windows;
using System.Windows.Controls;
using Universal_x86_Tuning_Utility.Services;

namespace Universal_x86_Tuning_Utility.Views.Controls
{
    public partial class HydroUiControl : UserControl
    {
        public HydroUiControl()
        {
            InitializeComponent();
        }

        public void UpdateMetrics(DeviceMetricsSnapshot snapshot)
        {
            if (snapshot.HydroUiConnected)
            {
                this.Visibility = Visibility.Visible;

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
                    _fanLabel.Content = $"{snapshot.HydroUiFanRpm} RPM";
                else if (snapshot.HydroUiFanSpeed > 0)
                    _fanLabel.Content = $"{snapshot.HydroUiFanSpeed}%";
                else
                    _fanLabel.Content = "Off";
            }
            else
            {
                this.Visibility = Visibility.Collapsed;
            }
        }
    }
}
