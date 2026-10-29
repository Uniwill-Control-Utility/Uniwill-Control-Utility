using LibreHardwareMonitor.Hardware;
using System;
using Uniwill_Control_Utility.Models;
using Uniwill_Control_Utility.Services;

namespace Uniwill_Control_Utility.Tests.Services;

/// <summary>
/// Producer-serving rules when a probe group stops answering (field reports
/// 2026-10-29: in-game the LHM pass parks behind the foreground title that owns
/// the GPU - NVAPI/storage calls under AV filter drivers; the cache's
/// single-flight refresh parks with it, and the RTSS OSD kept serving the last
/// completed snapshot as live - values froze, updating only for 1-2 s around
/// alt-tab. Separately, the measured SMART park - `device update Storage
/// 21044ms` - sat in the ONE gate the single Computer used, so a quasi-static
/// disk temperature blanked the CPU/GPU fields on every consumer).
///
/// The producer runs two groups (fast: CPU/GPU/battery; slow: DIMM SPD, SMART,
/// SuperIO system devices), each with its own age. Past ITS budget a group's
/// fields are no readings, never statues; the fresh sources - OS, EC, engine -
/// refill their fields through the merge beside them.
/// </summary>
public sealed class HardwareMonitoringServiceServingTests
{
    [Theory]
    [InlineData(4, false)] // healthy cadence: a pass completes every ~1 s
    [InlineData(6, true)]  // no completed pass for 6 cycles: the refresh is parked
    public void ProducerIsStale_BudgetsTheOneSecondRefreshCadence(int seconds, bool expected)
    {
        Assert.Equal(
            expected,
            HardwareMonitoringService.ProducerIsStale(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void ProducerIsStale_NeverCompleted_IsStale()
    {
        // An entry that never refreshed carries DateTimeOffset.MinValue: the
        // age is astronomical, so the startup path (first pass parked on the
        // gate) is the stale path too - the same honest serving.
        Assert.True(HardwareMonitoringService.ProducerIsStale(TimeSpan.FromDays(365_000)));
    }

    [Fact]
    public void ClearFastStaleFields_ZeroesTheFastGroupReadingsOnly()
    {
        // Ownership is the split's contract: the fast clear zeroes the fields
        // the CPU/GPU/battery subsystems feed, and never the slow group's
        // (DIMM temperatures, storage temperature) - those go stale on their
        // own group's age.
        var last = new HardwareMetricsSnapshot
        {
            CpuTemperature = 61,
            CpuTemperatureSource = CpuTemperatureSource.CpuSensor,
            CpuUsage = 47,
            CpuPowerWatts = 88,
            CpuClockMhz = 5150,
            GpuTemperature = 55,
            GpuUsage = 63,
            GpuPowerWatts = 210,
            GpuClockMhz = 1800,
            GpuMemoryUsedGb = 4.2,
            GpuMemoryTotalGb = 16,
            BatteryPowerWatts = 8.5,
            MemoryTemperature0 = 41,
            MemoryTemperature1 = 39,
            StorageTemperatureCelsius = 44,
        };

        HardwareMetricsSnapshot cleared = HardwareMonitoringService.ClearFastStaleFields(last);

        Assert.Equal(0, cleared.CpuTemperature);
        Assert.Equal(CpuTemperatureSource.None, cleared.CpuTemperatureSource);
        Assert.Equal(0, cleared.CpuUsage);
        Assert.Equal(0, cleared.CpuPowerWatts);
        Assert.Equal(0, cleared.CpuClockMhz);
        Assert.Equal(0, cleared.GpuTemperature);
        Assert.Equal(0, cleared.GpuUsage);
        Assert.Equal(0, cleared.GpuPowerWatts);
        Assert.Equal(0, cleared.GpuClockMhz);
        Assert.Equal(0, cleared.GpuMemoryUsedGb);
        Assert.Equal(0, cleared.BatteryPowerWatts);

        // The slow group's readings are untouched: its own pass is alive.
        Assert.Equal(41, cleared.MemoryTemperature0);
        Assert.Equal(39, cleared.MemoryTemperature1);
        Assert.Equal(44, cleared.StorageTemperatureCelsius);
    }

    [Fact]
    public void ClearSlowStaleFields_ZeroesTheSlowGroupReadingsOnly_TheSmartParkCase()
    {
        // The measured hazard: the SMART DeviceIoControl parks 21 s inside the
        // slow group's gate, so the slow group ages past its budget while the
        // fast group keeps answering. The storage/DIMM cells go "--" and the
        // CPU usage and GPU row keep streaming - not the whole dashboard.
        var last = new HardwareMetricsSnapshot
        {
            CpuUsage = 47,
            GpuTemperature = 55,
            GpuPowerWatts = 210,
            GpuClockMhz = 1800,
            MemoryTemperature0 = 41,
            MemoryTemperature1 = 39,
            StorageTemperatureCelsius = 44,
        };

        HardwareMetricsSnapshot cleared = HardwareMonitoringService.ClearSlowStaleFields(last);

        Assert.Equal(0, cleared.MemoryTemperature0);
        Assert.Equal(0, cleared.MemoryTemperature1);
        Assert.Equal(0, cleared.StorageTemperatureCelsius);
        Assert.Equal(47, cleared.CpuUsage);
        Assert.Equal(55, cleared.GpuTemperature);
        Assert.Equal(210, cleared.GpuPowerWatts);
        Assert.Equal(1800, cleared.GpuClockMhz);
    }

    [Fact]
    public void ClearFastStaleFields_KeepsOsStateEcFieldsAndTheGpuSelection()
    {
        // The fields with a live source beside the producer survive: OS state
        // (memory, battery percent/charging - rebuilt fresh there), the EC-fed
        // fields (fan speeds, battery health/temperature - the EC cache answers
        // on its own cadence), and ActiveGpuType (a selection, not a reading).
        var last = new HardwareMetricsSnapshot
        {
            SystemMemoryUsedGb = 15.1,
            HasBattery = true,
            BatteryPercent = 73,
            IsBatteryCharging = false,
            BatteryTimeRemainingMinutes = 122,
            BatteryHealthPercent = 96,
            BatteryTemperatureC = 31,
            ActiveGpuType = ActiveGpuType.Nvidia,
            CpuFanSpeed = 45,
        };

        HardwareMetricsSnapshot kept = HardwareMonitoringService.ClearFastStaleFields(last);

        Assert.Equal(15.1, kept.SystemMemoryUsedGb);
        Assert.True(kept.HasBattery);
        Assert.Equal(73, kept.BatteryPercent);
        Assert.Equal(122, kept.BatteryTimeRemainingMinutes);
        Assert.Equal(96, kept.BatteryHealthPercent);
        Assert.Equal(31, kept.BatteryTemperatureC);
        Assert.Equal(ActiveGpuType.Nvidia, kept.ActiveGpuType);
        Assert.Equal(45, kept.CpuFanSpeed);
    }

    [Fact]
    public void MergeGroupSnapshots_BothGroupsFresh_TakesEveryFieldFromItsOwnGroup()
    {
        var fast = new HardwareMetricsSnapshot
        {
            CpuUsage = 33,
            GpuTemperature = 58,
            GpuPowerWatts = 174,
            ActiveGpuType = ActiveGpuType.Nvidia,
        };
        var slow = new HardwareMetricsSnapshot
        {
            MemoryTemperature0 = 42,
            MemoryTemperature1 = 38,
            StorageTemperatureCelsius = 45,
        };

        HardwareMetricsSnapshot merged =
            HardwareMonitoringService.MergeGroupSnapshots(fast, fastStale: false, slow, slowStale: false);

        Assert.Equal(33, merged.CpuUsage);
        Assert.Equal(58, merged.GpuTemperature);
        Assert.Equal(174, merged.GpuPowerWatts);
        Assert.Equal(ActiveGpuType.Nvidia, merged.ActiveGpuType);
        Assert.Equal(42, merged.MemoryTemperature0);
        Assert.Equal(38, merged.MemoryTemperature1);
        Assert.Equal(45, merged.StorageTemperatureCelsius);
    }

    [Fact]
    public void MergeGroupSnapshots_SlowGroupParked_FastFieldsStayLive()
    {
        // The regression the split exists to fix: with one Computer and one
        // gate, the 21 s SMART park aged the whole producer and the CPU/GPU
        // cells blanked on dashboard, tray, FanControl and OSD.
        var fast = new HardwareMetricsSnapshot { CpuUsage = 33, GpuPowerWatts = 174, GpuClockMhz = 1800 };
        var slow = new HardwareMetricsSnapshot { MemoryTemperature0 = 42, StorageTemperatureCelsius = 45 };

        HardwareMetricsSnapshot merged =
            HardwareMonitoringService.MergeGroupSnapshots(fast, fastStale: false, slow, slowStale: true);

        Assert.Equal(33, merged.CpuUsage);
        Assert.Equal(174, merged.GpuPowerWatts);
        Assert.Equal(1800, merged.GpuClockMhz);
        Assert.Equal(0, merged.MemoryTemperature0);
        Assert.Equal(0, merged.StorageTemperatureCelsius);
    }

    [Fact]
    public void MergeGroupSnapshots_FastGroupParked_SlowFieldsStayLive()
    {
        // The rule is symmetric: the NVML/NVAPI park the stack sampler caught
        // (NvidiaGpu.Update -> NvmlDeviceGetPcieThroughput) ages the FAST
        // group, and the storage/DIMM cells keep answering from the slow one.
        var fast = new HardwareMetricsSnapshot { CpuUsage = 33, GpuPowerWatts = 174 };
        var slow = new HardwareMetricsSnapshot { MemoryTemperature0 = 42, StorageTemperatureCelsius = 45 };

        HardwareMetricsSnapshot merged =
            HardwareMonitoringService.MergeGroupSnapshots(fast, fastStale: true, slow, slowStale: false);

        Assert.Equal(0, merged.CpuUsage);
        Assert.Equal(0, merged.GpuPowerWatts);
        Assert.Equal(42, merged.MemoryTemperature0);
        Assert.Equal(45, merged.StorageTemperatureCelsius);
    }

    [Fact]
    public void WedgedProducer_FreshSourcesRefillTheirFields_NothingFrozenRemains()
    {
        // The full serve chain for a producer where BOTH groups are parked:
        // clear each group's fields, then the EC CPU temperature and the
        // engine's live Ring0/SPD readings merge beside the rebuilt OS fields.
        // The GPU keeps no source (app-side decision) and stays 0 - "--", not
        // a 210 W statue.
        var last = new HardwareMetricsSnapshot
        {
            CpuTemperature = 61,
            CpuTemperatureSource = CpuTemperatureSource.CpuSensor,
            CpuPowerWatts = 104,
            CpuClockMhz = 5298,
            MemoryTemperature0 = 41,
            GpuPowerWatts = 210,
            GpuClockMhz = 1800,
        };
        var engine = new EngineMetricsSnapshot
        {
            CpuPowerWatts = 96,
            CpuClockMhz = 4800,
            MemoryTemperature0 = 43,
            ProbeOpen = true,
        };

        HardwareMetricsSnapshot served = HardwareMonitoringService.ApplyEngineSensorSource(
            HardwareMonitoringService.ApplyCpuTemperatureSource(
                HardwareMonitoringService.ClearSlowStaleFields(
                    HardwareMonitoringService.ClearFastStaleFields(last)), 58),
            engine);

        Assert.Equal(58, served.CpuTemperature);
        Assert.Equal(CpuTemperatureSource.EcSensor, served.CpuTemperatureSource);
        Assert.Equal(96, served.CpuPowerWatts);
        Assert.Equal(4800, served.CpuClockMhz);
        Assert.Equal(43, served.MemoryTemperature0);
        Assert.Equal(0, served.GpuPowerWatts);
        Assert.Equal(0, served.GpuClockMhz);
    }

    [Theory]
    [InlineData(HardwareType.Cpu, true)]
    [InlineData(HardwareType.GpuNvidia, true)]
    [InlineData(HardwareType.GpuAmd, true)]
    [InlineData(HardwareType.GpuIntel, true)]
    [InlineData(HardwareType.Battery, true)]
    [InlineData(HardwareType.Motherboard, false)]
    [InlineData(HardwareType.Storage, false)]
    [InlineData(HardwareType.EmbeddedController, false)]
    [InlineData(HardwareType.Memory, false)]
    public void IsFastHardware_RoutesEverySensorReadToTheGroupThatOwnsIt(HardwareType type, bool isFast)
    {
        // The fan loop feeds on ReadSensor: motherboard/controller SuperIO fans
        // are the slow group's, CPU/GPU fans are the fast group's. A read
        // routed to the wrong group would go 0 on the other group's park.
        Assert.Equal(isFast, HardwareMonitoringService.IsFastHardware(type));
    }

    [Fact]
    public void StorageUpdateDue_DefersTheFirstUpdateAndThenRunsOnceAMinute()
    {
        // The field park (21 s SMART behind the AV storage filter) must not run
        // on the 250 ms pass cadence even inside its own group: the clock
        // starts at construction, so the first update is a minute out and the
        // first snapshot never queues behind SMART.
        DateTime construction = new(2026, 10, 29, 21, 0, 0, DateTimeKind.Utc);

        Assert.False(HardwareMonitoringService.StorageUpdateDue(construction, construction));
        Assert.False(HardwareMonitoringService.StorageUpdateDue(
            construction.AddSeconds(59), construction));
        Assert.True(HardwareMonitoringService.StorageUpdateDue(
            construction.AddSeconds(60), construction));
        Assert.True(HardwareMonitoringService.StorageUpdateDue(
            construction.AddMinutes(30), construction));
    }
}
