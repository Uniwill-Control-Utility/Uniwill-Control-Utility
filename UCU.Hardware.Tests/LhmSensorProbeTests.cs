using LibreHardwareMonitor.Hardware;
using UCU.Hardware.Metrics;

namespace UCU.Hardware.Tests;

/// <summary>
/// The naming/units rules the engine's sensor probe depends on, pinned by an
/// empirical elevated probe of the field machine (2026-10-29, LHM 0.9.7-pre744):
/// AMD exposes package power as "Package" (Intel "CPU Package"), real core
/// clocks as "Core #N" with a "Cores (Average)" aggregate, the "(Effective)"
/// clocks are utilisation-weighted pseudo-clocks (field: 2161 vs the real
/// 5150), and the SPD data rate lives in the timing "tCKAVGmin" - DDR moves
/// data on both edges, so MT/s = 2000 / tCK(ns) (field: 0.357 ns -> 5602, the
/// WMI ConfiguredClockSpeed of the same machine: 5600).
/// </summary>
public sealed class LhmSensorProbeTests
{
    private static SensorDatum Cpu(SensorType type, string name, float value) =>
        new(HardwareType.Cpu, type, name, value);

    private static SensorDatum Dimm(SensorType type, string name, float value) =>
        new(HardwareType.Memory, type, name, value);

    [Fact]
    public void PackagePower_PrefersCpuPackageThenPackage()
    {
        // Intel-first order; the field machine (AMD) answers on the second name.
        var intel = new[] { Cpu(SensorType.Power, "CPU Package", 65.4f), Cpu(SensorType.Power, "Package", 40f) };
        var amd = new[] { Cpu(SensorType.Power, "Package", 104.64f), Cpu(SensorType.Power, "Core #1 (SMU)", 5.5f) };

        Assert.Equal(65, LhmSensorProbe.Extract(intel).CpuPowerWatts);
        Assert.Equal(105, LhmSensorProbe.Extract(amd).CpuPowerWatts);
    }

    [Fact]
    public void CoreClock_PrefersTheAggregate_ThenAveragesThePerCoreClocks()
    {
        var withAggregate = new[]
        {
            Cpu(SensorType.Clock, "Cores (Average)", 5298f),
            Cpu(SensorType.Clock, "Core #1", 5150f),
            Cpu(SensorType.Clock, "Core #2", 5445f),
        };
        var perCoreOnly = new[]
        {
            Cpu(SensorType.Clock, "Core #1", 4000f),
            Cpu(SensorType.Clock, "Core #2", 4200f),
        };

        Assert.Equal(5298, LhmSensorProbe.Extract(withAggregate).CpuClockMhz);
        Assert.Equal(4100, LhmSensorProbe.Extract(perCoreOnly).CpuClockMhz);
    }

    [Fact]
    public void CoreClock_ExcludesTheEffectiveSensors()
    {
        // The field tree: "Core #1" 5150, "Core #1 (Effective)" 2161. Averaging
        // both in would report a clock that is not the clock.
        var data = new[]
        {
            Cpu(SensorType.Clock, "Core #1", 5150f),
            Cpu(SensorType.Clock, "Core #1 (Effective)", 2161f),
            Cpu(SensorType.Clock, "Core #2", 5450f),
            Cpu(SensorType.Clock, "Core #2 (Effective)", 308f),
            Cpu(SensorType.Clock, "Bus Speed", 99.8f),
        };

        Assert.Equal(5300, LhmSensorProbe.Extract(data).CpuClockMhz);
    }

    [Fact]
    public void MemorySpeed_ComesFromTheFastestTckAvgMin_BothEdgesDdr()
    {
        // Field SPD: tCKAVGmin 0.357 ns -> 2e9/0.357 = 5602 MT/s (WMI: 5600).
        var twoDimms = new[]
        {
            Dimm(SensorType.Timing, "tCKAVGmin (Minimum Cycle Time)", 0.357f),
            Dimm(SensorType.Timing, "tCKAVGmin (Minimum Cycle Time)", 0.370f),
            Dimm(SensorType.Timing, "tCKAVGmax (Maximum Cycle Time)", 1.01f),
        };

        Assert.Equal(5602, LhmSensorProbe.Extract(twoDimms).MemorySpeedMts);
    }

    [Fact]
    public void MemorySpeed_StaysZeroWhenTheTimingSensorIsSilent()
    {
        var noSspd = new[] { Dimm(SensorType.Load, "Memory", 66f) };

        Assert.Equal(0, LhmSensorProbe.Extract(noSspd).MemorySpeedMts);
    }

    [Fact]
    public void DimmTemperatures_RoundTheSpdSensors()
    {
        var data = new[]
        {
            Dimm(SensorType.Temperature, "DIMM #0", 41.5f),
            Dimm(SensorType.Temperature, "DIMM #1", 39.4f),
            Dimm(SensorType.Temperature, "Thermal Sensor High Limit", 55f),
        };

        SensorProbeReading reading = LhmSensorProbe.Extract(data);

        Assert.Equal(42, reading.MemoryTemperature0);
        Assert.Equal(39, reading.MemoryTemperature1);
    }

    [Fact]
    public void Extract_OnEmptyTree_IsZerosWithProbeOpen()
    {
        SensorProbeReading reading = LhmSensorProbe.Extract(Array.Empty<SensorDatum>());

        Assert.True(reading.ProbeOpen);
        Assert.Equal(0, reading.CpuPowerWatts);
        Assert.Equal(0, reading.CpuClockMhz);
        Assert.Equal(0, reading.MemorySpeedMts);
    }
}
