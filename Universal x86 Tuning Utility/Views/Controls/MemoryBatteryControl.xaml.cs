using System.Windows;
using Universal_x86_Tuning_Utility.Services;

namespace Universal_x86_Tuning_Utility.Views.Controls
{
    public partial class MemoryBatteryControl
    {
        public MemoryBatteryControl()
        {
            InitializeComponent();
        }

        public void UpdateMetrics(HardwareMetricsSnapshot snapshot)
        {
            double totalMem = snapshot.SystemMemoryTotalGb;
            if (totalMem > 0)
            {
                double percent = (snapshot.SystemMemoryUsedGb / totalMem) * 100;
                _memoryBar.Value = percent;
                _memoryLabel.Content = $"{snapshot.SystemMemoryUsedGb:F1}/{totalMem:F1} GB";
            }
            else
            {
                _memoryBar.Value = 0;
                _memoryLabel.Content = "--";
            }

            if (snapshot.HasBattery)
            {
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
