using System.Windows.Controls;
using Uniwill_Control_Utility.Services;

namespace Uniwill_Control_Utility.Views.Controls
{
    public partial class GpuControl : UserControl
    {
        public GpuControl()
        {
            InitializeComponent();
        }

        public string HardwareName
        {
            get => _hardwareName.Text;
            set
            {
                _hardwareName.Text = value;
                _hardwareName.ToolTip = value;
            }
        }

        public void UpdateMetrics(HardwareMetricsSnapshot snapshot)
        {
            _gpuUsageHero.Text = snapshot.GpuUsage >= 0
                ? (snapshot.GpuUsage < 1 ? $"{snapshot.GpuUsage:F1}%" : $"{snapshot.GpuUsage}%")
                : "--";

            _gpuTempHero.Text = snapshot.GpuTemperature > 0 ? $"{snapshot.GpuTemperature}°C" : "--";
            _temperatureSeries.AddSample(snapshot.GpuTemperature);

            _gpuPowerBar.Value = snapshot.GpuPowerWatts;
            _gpuPowerLabel.Text = snapshot.GpuPowerWatts > 0 ? $"{snapshot.GpuPowerWatts}W" : "--";

            _gpuClockBar.Value = snapshot.GpuClockMhz;
            _gpuClockLabel.Text = snapshot.GpuClockMhz > 0 ? $"{snapshot.GpuClockMhz}MHz" : "--";

            _gpuFanBar.Value = snapshot.GpuFanSpeed;
            _gpuFanLabel.Text = snapshot.GpuFanSpeed > 0 ? $"{snapshot.GpuFanSpeed}%" : "--";
        }
    }
}
