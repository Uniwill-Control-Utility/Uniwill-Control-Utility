using System.Windows.Controls;
using Universal_x86_Tuning_Utility.Services;

namespace Universal_x86_Tuning_Utility.Views.Controls
{
    public partial class GpuControl : UserControl
    {
        public GpuControl()
        {
            InitializeComponent();
        }

        public void UpdateMetrics(HardwareMetricsSnapshot snapshot)
        {
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

            _gpuFanBar.Value = snapshot.GpuFanSpeed;
            _gpuFanLabel.Content = snapshot.GpuFanSpeed > 0 ? $"{snapshot.GpuFanSpeed}%" : "--";
        }
    }
}
