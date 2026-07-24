using System;
using System.Windows;
using System.Windows.Controls;
using Universal_x86_Tuning_Utility.Services;

namespace Universal_x86_Tuning_Utility.Views.Controls
{
    public partial class SystemControl : UserControl
    {
        public SystemControl()
        {
            InitializeComponent();
        }

        public void UpdateMetrics(HardwareMetricsSnapshot snapshot)
        {
            // Memory
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

            // Committed
            double pagefileTotal = snapshot.SystemMemoryPagefileTotalGb;
            if (pagefileTotal > 0)
            {
                double percent = (snapshot.SystemMemoryCommittedGb / pagefileTotal) * 100;
                _committedBar.Value = Math.Min(percent, 100);
                _committedLabel.Content = $"{snapshot.SystemMemoryCommittedGb:F1}/{pagefileTotal:F1} GB";
            }
            else
            {
                _committedBar.Value = 0;
                _committedLabel.Content = $"{snapshot.SystemMemoryCommittedGb:F1} GB";
            }

            // Battery
            if (snapshot.HasBattery)
            {
                _batterySection.Visibility = Visibility.Visible;
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
                _batterySection.Visibility = Visibility.Collapsed;
            }
        }
    }
}
