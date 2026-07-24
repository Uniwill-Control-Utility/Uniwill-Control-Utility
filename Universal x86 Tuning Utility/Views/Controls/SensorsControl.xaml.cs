using System.Windows.Controls;
using Universal_x86_Tuning_Utility.Services;

namespace Universal_x86_Tuning_Utility.Views.Controls
{
    public partial class SensorsControl : UserControl
    {
        public SensorsControl()
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

            _gpuUsageBar.Value = snapshot.GpuUsage;
            _gpuUsageLabel.Content = snapshot.GpuUsage >= 0
                ? (snapshot.GpuUsage < 1 ? $"{snapshot.GpuUsage:F1}%" : $"{snapshot.GpuUsage}%")
                : "--";

            _gpuTempBar.Value = snapshot.GpuTemperature;
            _gpuTempLabel.Content = snapshot.GpuTemperature > 0 ? $"{snapshot.GpuTemperature}°C" : "--";

            _gpuPowerBar.Value = snapshot.GpuPowerWatts;
            _gpuPowerLabel.Content = snapshot.GpuPowerWatts > 0 ? $"{snapshot.GpuPowerWatts}W" : "--";

            _gpuClockBar.Value = snapshot.GpuClockMhz;
            _gpuClockLabel.Content = snapshot.GpuClockMhz > 0 ? $"{snapshot.GpuClockMhz}MHz" : "--";
        }
    }
}
