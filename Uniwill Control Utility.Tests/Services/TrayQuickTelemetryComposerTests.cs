using Uniwill_Control_Utility.Services;

namespace Uniwill_Control_Utility.Tests.Services;

public sealed class TrayQuickTelemetryComposerTests
{
    [Fact]
    public void Compose_UsesCachedHardwareMetricsAndEcFanDuty()
    {
        var hardware = new HardwareMetricsSnapshot
        {
            CpuTemperature = 41,
            CpuUsage = 8,
            CpuPowerWatts = 12,
            CpuClockMhz = 2758,
            GpuTemperature = 37,
            GpuUsage = 6.4,
            GpuPowerWatts = 21,
            GpuClockMhz = 1875
        };
        var ec = new EcFanDutySnapshot(CpuFanDuty: 60, GpuFanDuty: 50);

        TrayQuickTelemetrySnapshot result = TrayQuickTelemetryComposer.Compose(hardware, ec);

        Assert.Equal(41, result.CpuTemperature);
        Assert.Equal(8, result.CpuUsage);
        Assert.Equal(12, result.CpuPowerWatts);
        Assert.Equal(2758, result.CpuClockMhz);
        Assert.Equal(30, result.CpuFanPercent);
        Assert.Equal(37, result.GpuTemperature);
        Assert.Equal(6.4, result.GpuUsage);
        Assert.Equal(21, result.GpuPowerWatts);
        Assert.Equal(1875, result.GpuClockMhz);
        Assert.Equal(25, result.GpuFanPercent);
    }

    [Fact]
    public void Compose_ClampsUnavailableNegativeValuesToZero()
    {
        var hardware = new HardwareMetricsSnapshot
        {
            CpuTemperature = -1,
            CpuUsage = -4,
            GpuTemperature = -1,
            GpuUsage = -2
        };

        TrayQuickTelemetrySnapshot result = TrayQuickTelemetryComposer.Compose(
            hardware,
            new EcFanDutySnapshot(-20, -10));

        Assert.Equal(0, result.CpuTemperature);
        Assert.Equal(0, result.CpuUsage);
        Assert.Equal(0, result.GpuTemperature);
        Assert.Equal(0, result.GpuUsage);
        Assert.Equal(0, result.CpuFanPercent);
        Assert.Equal(0, result.GpuFanPercent);
    }
}
