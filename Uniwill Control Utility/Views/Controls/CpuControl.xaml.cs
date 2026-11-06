using System.Windows.Controls;
using Uniwill_Control_Utility.Services;

namespace Uniwill_Control_Utility.Views.Controls
{
    public partial class CpuControl : UserControl
    {
        public CpuControl()
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
            _cpuUsageHero.Text = $"{snapshot.CpuUsage}%";

            _cpuTempHero.Text = snapshot.CpuTemperature > 0 ? $"{snapshot.CpuTemperature}°C" : "--";
            _temperatureSeries.AddSample(snapshot.CpuTemperature);

            // Where the number came from is worth saying: the EC reading is the fallback
            // used when the CPU's own sensor is unavailable, and it is a different sensor
            // (the EC's fan-curve input), so a graph should not be ambiguous about it.
            _cpuTempHero.ToolTip = snapshot.CpuTemperatureSource switch
            {
                CpuTemperatureSource.CpuSensor => "CPU core sensor (model-specific registers)",
                CpuTemperatureSource.EcSensor => "Embedded controller sensor (the EC fan-curve input)",
                _ => "No CPU temperature source available",
            };

            _cpuPowerBar.Value = snapshot.CpuPowerWatts;
            _cpuPowerLabel.Text = snapshot.CpuPowerWatts > 0 ? $"{snapshot.CpuPowerWatts}W" : "--";

            _cpuClockBar.Value = snapshot.CpuClockMhz;
            _cpuClockLabel.Text = snapshot.CpuClockMhz > 0 ? $"{snapshot.CpuClockMhz}MHz" : "--";

            _cpuFanBar.Value = snapshot.CpuFanSpeed;
            _cpuFanLabel.Text = snapshot.CpuFanSpeed > 0 ? $"{snapshot.CpuFanSpeed}%" : "--";
        }
    }
}
