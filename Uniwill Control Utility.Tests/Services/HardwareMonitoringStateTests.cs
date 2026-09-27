using LibreHardwareMonitor.Hardware;
using Uniwill_Control_Utility.Services;

namespace Uniwill_Control_Utility.Tests.Services;

public sealed class HardwareMonitoringStateTests
{
    [Fact]
    public void ReadSensor_matches_hardware_type_sensor_type_and_name_without_case_sensitivity()
    {
        var state = new HardwareMonitoringState(
            new HardwareMetricsSnapshot(),
            new[]
            {
                new HardwareSensorReading(HardwareType.Cpu, SensorType.Temperature, "CPU Package", 71.5f),
                new HardwareSensorReading(HardwareType.GpuNvidia, SensorType.Temperature, "GPU Core", 62f),
                new HardwareSensorReading(HardwareType.Cpu, SensorType.Load, "CPU Total", 35f)
            });

        Assert.Equal(71.5f, state.ReadSensor(HardwareType.Cpu, SensorType.Temperature, "package"));
        Assert.Equal(0f, state.ReadSensor(HardwareType.GpuAmd, SensorType.Temperature, "GPU Core"));
        Assert.Equal(0f, state.ReadSensor(HardwareType.Cpu, SensorType.Power, "Package"));
    }
}
