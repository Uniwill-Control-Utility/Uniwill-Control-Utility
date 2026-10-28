using Uniwill_Control_Utility.Services;

namespace Uniwill_Control_Utility.Tests.Services;

/// <summary>
/// CPU temperature source precedence (HardwareMonitoringService.ApplyCpuTemperatureSource).
/// The CPU's own MSR-derived sensor wins; the embedded controller's reading replaces it only
/// when the MSR path is unavailable - an asInvoker app cannot open PawnIO, whose device ACL
/// admits System and Administrators, which is exactly the installed (per-machine) app. Neither
/// source answering stays 0/None, which the UI renders as "--".
/// </summary>
public sealed class CpuTemperatureSourceTests
{
    [Fact]
    public void CpuSensor_Wins_WhenItAnswered()
    {
        var metrics = new HardwareMetricsSnapshot { CpuTemperature = 61 };

        HardwareMetricsSnapshot result =
            HardwareMonitoringService.ApplyCpuTemperatureSource(metrics, ecCpuTemperature: 54);

        Assert.Equal(61, result.CpuTemperature);
        Assert.Equal(CpuTemperatureSource.CpuSensor, result.CpuTemperatureSource);
    }

    [Fact]
    public void EcSensor_Replaces_TheMissingCpuSensor()
    {
        var metrics = new HardwareMetricsSnapshot { CpuTemperature = 0 };

        HardwareMetricsSnapshot result =
            HardwareMonitoringService.ApplyCpuTemperatureSource(metrics, ecCpuTemperature: 54);

        Assert.Equal(54, result.CpuTemperature);
        Assert.Equal(CpuTemperatureSource.EcSensor, result.CpuTemperatureSource);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NeitherSource_LeavesTheMetricUnavailable(int ecTemperature)
    {
        var metrics = new HardwareMetricsSnapshot { CpuTemperature = 0 };

        HardwareMetricsSnapshot result =
            HardwareMonitoringService.ApplyCpuTemperatureSource(metrics, ecCpuTemperature: ecTemperature);

        Assert.Equal(0, result.CpuTemperature);
        Assert.Equal(CpuTemperatureSource.None, result.CpuTemperatureSource);
    }

    [Fact]
    public void Fallback_Preserves_TheOtherMetrics()
    {
        var metrics = new HardwareMetricsSnapshot
        {
            CpuTemperature = 0,
            CpuUsage = 42,
            CpuFanSpeed = 30,
            GpuTemperature = 66
        };

        HardwareMetricsSnapshot result =
            HardwareMonitoringService.ApplyCpuTemperatureSource(metrics, ecCpuTemperature: 54);

        Assert.Equal(42, result.CpuUsage);
        Assert.Equal(30, result.CpuFanSpeed);
        Assert.Equal(66, result.GpuTemperature);
    }

    [Fact]
    public void Fallback_IsSourcedFromTheEngineVerbSurface()
    {
        // The fallback's whole point is that it needs no privilege, so it must be reachable
        // through the app's engine capability, not through any in-process driver handle.
        Type sourceType = typeof(IEcCpuTemperatureSource);
        Assert.NotNull(sourceType.GetMethod(nameof(IEcCpuTemperatureSource.ReadCpuTemperature)));
        Assert.True(typeof(IEcCpuTemperatureSource).IsAssignableFrom(typeof(EcTelemetryService)));
    }
}
