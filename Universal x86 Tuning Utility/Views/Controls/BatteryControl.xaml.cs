using System.Windows;
using System.Windows.Controls;
using Universal_x86_Tuning_Utility.Services;

namespace Universal_x86_Tuning_Utility.Views.Controls
{
    public partial class BatteryControl : UserControl
    {
        public BatteryControl()
        {
            InitializeComponent();
        }

        public void UpdateMetrics(HardwareMetricsSnapshot snapshot)
        {
            if (snapshot.HasBattery)
            {
                this.Visibility = Visibility.Visible;
                _batteryBar.Value = snapshot.BatteryPercent;
                _batteryPercentLabel.Content = $"{snapshot.BatteryPercent}%";

                string status;
                if (snapshot.IsBatteryFullyCharged)
                    status = "Fully charged";
                else if (snapshot.IsBatteryCharging)
                    status = $"Charging ({snapshot.BatteryPowerWatts:F1}W)";
                else
                    status = $"Discharging ({snapshot.BatteryPowerWatts:F1}W)";

                _batteryStatusLabel.Content = status;
            }
            else
            {
                this.Visibility = Visibility.Collapsed;
            }
        }
    }
}
