using System;
using Uniwill_Control_Utility.Services;

namespace Uniwill_Control_Utility.Tests.Services;

/// <summary>
/// GPU power comes from LibreHardwareMonitor, whose Intel iGPU "GPU Power" is a
/// RAPL energy-counter derivative with an unchecked 32-bit subtraction, fed a 0
/// whenever a PawnIO read fails. That produced "22 W, 190286 W, 22 W" on the
/// Intel tester laptop on battery (field, 2026-11-02). The producer drops values
/// no laptop GPU can draw, which also renders as "--" instead of overflowing the
/// fixed-width OSD column.
/// </summary>
public sealed class GpuPowerPlausibilityTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(22)]
    [InlineData(115)]
    [InlineData(600)]
    public void PlausibleDraws_PassThrough(double watts)
    {
        Assert.Equal((int)Math.Round(watts), HardwareMonitoringService.PlausibleGpuPowerWatts(watts, ActiveGpuType.Nvidia, null));
    }

    [Theory]
    [InlineData(190286)]
    [InlineData(4294967)]
    [InlineData(601)]
    public void ImplausibleDraws_AreDroppedToUnavailable(double watts)
    {
        Assert.Equal(0, HardwareMonitoringService.PlausibleGpuPowerWatts(watts, ActiveGpuType.Intel, null));
    }
}
