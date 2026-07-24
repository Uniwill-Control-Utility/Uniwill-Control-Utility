using System.Windows.Controls;
using Universal_x86_Tuning_Utility.Services;

namespace Universal_x86_Tuning_Utility.Views.Controls
{
    public partial class CpuControl : UserControl
    {
        public CpuControl()
        {
            InitializeComponent();
        }

        public void UpdateMetrics(HardwareMetricsSnapshot snapshot)
        {
            _cpuUsageBar.Value = snapshot.CpuUsage;
            _cpuUsageLabel.Content = $"{snapshot.CpuUsage}%";

            _cpuTempBar.Value = snapshot.CpuTemperature;
            _cpuTempLabel.Content = snapshot.CpuTemperature > 0 ? $"{snapshot.CpuTemperature}°C" : "--";

            _cpuPowerBar.Value = snapshot.CpuPowerWatts;
            _cpuPowerLabel.Content = snapshot.CpuPowerWatts > 0 ? $"{snapshot.CpuPowerWatts}W" : "--";

            _cpuClockBar.Value = snapshot.CpuClockMhz;
            _cpuClockLabel.Content = snapshot.CpuClockMhz > 0 ? $"{snapshot.CpuClockMhz}MHz" : "--";
        }
    }
}
