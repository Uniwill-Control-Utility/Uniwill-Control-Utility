using LibreHardwareMonitor.Hardware;
using System;
using Uniwill_Control_Utility.Services;

namespace Uniwill_Control_Utility.Tests.Services;

/// <summary>
/// The CPU clock cell's extraction, pinned by the two field sensor dumps
/// (LHM 0.9.7-pre744, elevated): AMD 2026-10-29 (Ryzen 9 9955HX3D) and Intel
/// 2026-11-01 (Core Ultra 9 275HX). The old ReadAverage(cpu, Clock, "Core")
/// Contains-matched everything with "Core" in the name: the "(Effective)"
/// utilisation-weighted pseudo-clocks (AMD: 52-2329 beside the real 5150) and
/// the iGPU sub-hardware's "GPU Core" clock (Intel: 120 MHz idle). The dev box
/// displayed 2929 MHz while the aggregate sensor said 5300 - a diluted clock
/// is not a blank one, so the card "looked alive" while showing a number no
/// core ran at. Same field rules the engine probe pins (LhmSensorProbe).
/// </summary>
public sealed class HardwareMonitoringCoreClockTests
{
    private static (SensorType Type, string Name, float? Value) Clock(string name, float value) =>
        (SensorType.Clock, name, value);

    [Fact]
    public void AmdAggregateSensorWinsOverThePerCoreNoise()
    {
        // The dev-box tree: aggregate 5300, real cores 5150-5450, Effective
        // pseudo-clocks 52-2329, iGPU idle 120, bus 99.8. Averaging that set
        // (the old rule) produced ~2900; the truth is 5300.
        var tree = new List<(SensorType Type, string Name, float? Value)>
        {
            Clock("Cores (Average)", 5300f),
            Clock("Cores (Average Effective)", 604f),
        };
        for (int n = 1; n <= 24; n++)
        {
            tree.Add(Clock($"Core #{n}", n <= 8 ? 5150f : 5450f));
            tree.Add(Clock($"Core #{n} (Effective)", 100f + n * 40f));
        }
        tree.Add(Clock("GPU Core", 120f));
        tree.Add(Clock("GPU Memory", 1600f));
        tree.Add(Clock("Bus Speed", 99.81f));

        Assert.Equal(5300, HardwareMonitoringService.CoreClockMhzFrom(tree));
    }

    [Fact]
    public void AmdPerCoreAveragesExcludeTheEffectiveSensors()
    {
        // No aggregate answer: mean of the real cores only (the AMD box's
        // "#1"=5150, "#2"=5450 pair -> 5300; the Effective entries beside
        // them would drag it to ~1500).
        var tree = new[]
        {
            Clock("Core #1", 5150f),
            Clock("Core #1 (Effective)", 1599f),
            Clock("Core #2", 5450f),
            Clock("Core #2 (Effective)", 516f),
            Clock("Bus Speed", 99.81f),
        };

        Assert.Equal(5300, HardwareMonitoringService.CoreClockMhzFrom(tree));
    }

    [Fact]
    public void IntelHybridAveragesPCoreAndECoreSensors()
    {
        // The Intel field tree: NO "Core #N", no aggregate - "P-Core #N" and
        // "E-Core #N" only. Mean of the dump's 8 P + 16 E values = 2647 MHz.
        // The iGPU's "GPU Core" (120 idle) and "Bus Speed" (99.10) must stay
        // out: pulling the iGPU clock in gives 2563.
        var tree = new List<(SensorType Type, string Name, float? Value)>
        {
            Clock("P-Core #1", 5153.42f),
            Clock("P-Core #2", 5153.42f),
            Clock("P-Core #3", 2180.29f),
            Clock("P-Core #4", 1783.87f),
            Clock("P-Core #5", 2180.29f),
            Clock("P-Core #6", 5252.52f),
            Clock("P-Core #7", 2180.29f),
            Clock("P-Core #8", 4757.00f),
        };
        for (int e = 1; e <= 16; e++)
            tree.Add(Clock($"E-Core #{e}", 2180.29f));
        tree.Add(Clock("GPU Core", 120f));
        tree.Add(Clock("GPU Memory", 14001f));
        tree.Add(Clock("Bus Speed", 99.10f));

        Assert.Equal(2647, HardwareMonitoringService.CoreClockMhzFrom(tree));
    }

    [Fact]
    public void SilentTreeAnswersZeroForTheEngineOverlay()
    {
        // No Ring0 on the app's side: the real core sensors answer null, so
        // the mean has no dividend and the cell must be 0 = unavailable,
        // letting the engine's fill-zeros overlay supply its elevated
        // reading. The bus clock (99.10) alone must not become a "clock".
        var tree = new List<(SensorType Type, string Name, float? Value)>
        {
            (SensorType.Clock, "Core #1", null),
            (SensorType.Clock, "P-Core #1", null),
            (SensorType.Clock, "Bus Speed", 99.1f),
        };

        Assert.Equal(0, HardwareMonitoringService.CoreClockMhzFrom(tree));
    }
}
