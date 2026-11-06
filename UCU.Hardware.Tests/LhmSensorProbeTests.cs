using LibreHardwareMonitor.Hardware;
using UCU.Hardware.Metrics;

namespace UCU.Hardware.Tests;

/// <summary>
/// The naming/units rules the engine's sensor probe depends on, pinned by
/// empirical elevated probes of the field machines (LHM 0.9.7-pre744):
/// AMD exposes package power as "Package" (Intel "CPU Package"), real core
/// clocks as "Core #N" with a "Cores (Average)" aggregate, the "(Effective)"
/// clocks are utilisation-weighted pseudo-clocks (field: 2161 vs the real
/// 5150), and the SPD data rate lives in the timing "tCKAVGmin" - DDR moves
/// data on both edges, so MT/s = 2000 / tCK(ns) (field: 0.357 ns -> 5602, the
/// WMI ConfiguredClockSpeed of the same machine: 5600). The Intel hybrid
/// field tree (2026-11-01, Core Ultra 9 275HX) has no "Core #N" and no
/// aggregate at all - only "P-Core #N"/"E-Core #N" plus "Bus Speed" and the
/// Gpu-node iGPU clocks, all of which the matcher below pins.
/// </summary>
public sealed class LhmSensorProbeTests
{
    private static SensorDatum Cpu(SensorType type, string name, float value) =>
        new(HardwareType.Cpu, type, name, value);

    private static SensorDatum Dimm(SensorType type, string name, float value) =>
        new(HardwareType.Memory, type, name, value);

    private static SensorDatum Gpu(SensorType type, string name, float value) =>
        new(HardwareType.GpuIntel, type, name, value);

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
    public void CoreClock_AveragesIntelHybridPCoreAndECoreSensors()
    {
        // FIELD DUMP 2026-11-01 (PCSpecialist tester, Core Ultra 9 275HX):
        // Intel hybrid exposes NO "Core #N" and no "Cores (Average)" - only
        // "P-Core #N"/"E-Core #N", so the AMD-only matcher left the clock
        // blank in the card AND the OSD. Mean of the 8 P + 16 E values of
        // the real dump = 2647 MHz; "Bus Speed" 99.10 must stay out of the
        // average (pulling it in gives 2572), and the iGPU clocks live on a
        // Gpu-typed node (Gpu helper) that the Cpu filter drops.
        var intel = new List<SensorDatum>
        {
            Cpu(SensorType.Clock, "P-Core #1", 5153.42f),
            Cpu(SensorType.Clock, "P-Core #2", 5153.42f),
            Cpu(SensorType.Clock, "P-Core #3", 2180.29f),
            Cpu(SensorType.Clock, "P-Core #4", 1783.87f),
            Cpu(SensorType.Clock, "P-Core #5", 2180.29f),
            Cpu(SensorType.Clock, "P-Core #6", 5252.52f),
            Cpu(SensorType.Clock, "P-Core #7", 2180.29f),
            Cpu(SensorType.Clock, "P-Core #8", 4757.00f),
        };
        for (int e = 1; e <= 16; e++)
            intel.Add(Cpu(SensorType.Clock, $"E-Core #{e}", 2180.29f));
        intel.Add(Cpu(SensorType.Clock, "Bus Speed", 99.10f));
        intel.Add(Gpu(SensorType.Clock, "GPU Core", 120f));
        intel.Add(Gpu(SensorType.Clock, "GPU Memory", 14001f));
        intel.Add(Cpu(SensorType.Power, "CPU Package", 13.99f));

        SensorProbeReading reading = LhmSensorProbe.Extract(intel);

        Assert.Equal(2647, reading.CpuClockMhz);
        Assert.Equal(14, reading.CpuPowerWatts);
    }

    [Fact]
    public void MemoryTemperatures_PairTheDimmFamilyByDiscoveryOrder()
    {
        // The Intel field board (2026-11-01) is a 4-slot layout answering
        // "DIMM #0" + "DIMM #2" - the exact "#0"/"#1" pair dropped the
        // second stick. The "DIMM #N" family is assigned to the card's two
        // slots in tree order, same rule the app's reader pins (its comment
        // cites this machine). Limit sensors are not DIMM readings.
        var intel = new[]
        {
            Dimm(SensorType.Temperature, "DIMM #0", 45.75f),
            Dimm(SensorType.Temperature, "Thermal Sensor High Limit", 55f),
            Dimm(SensorType.Temperature, "Thermal Sensor Critical High Limit", 85f),
            Dimm(SensorType.Temperature, "DIMM #2", 41.75f),
        };
        var amd = new[]
        {
            Dimm(SensorType.Temperature, "DIMM #0", 37.5f),
            Dimm(SensorType.Temperature, "DIMM #1", 36.25f),
        };

        SensorProbeReading intelReading = LhmSensorProbe.Extract(intel);
        Assert.Equal(46, intelReading.MemoryTemperature0);
        Assert.Equal(42, intelReading.MemoryTemperature1);

        SensorProbeReading amdReading = LhmSensorProbe.Extract(amd);
        Assert.Equal(38, amdReading.MemoryTemperature0);
        Assert.Equal(36, amdReading.MemoryTemperature1);
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
