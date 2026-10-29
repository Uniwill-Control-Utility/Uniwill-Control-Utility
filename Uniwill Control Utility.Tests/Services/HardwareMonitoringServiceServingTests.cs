using System;
using Uniwill_Control_Utility.Models;
using Uniwill_Control_Utility.Services;

namespace Uniwill_Control_Utility.Tests.Services;

/// <summary>
/// Producer-serving rules when the probe loop stops answering (field report
/// 2026-10-29: in-game the LHM pass parks behind the foreground title that
/// owns the GPU - NVAPI/storage calls under AV filter drivers; the cache's
/// single-flight refresh parks with it, and the RTSS OSD kept serving the
/// last completed snapshot as live - values froze, updating only for 1-2 s
/// around alt-tab when the pass briefly unblocked). Past the staleness
/// budget the LHM-only fields are no readings, never statues; the fresh
/// sources - OS, EC, engine - refill their fields through the producer
/// merge beside them.
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
    public void ClearStaleSensorFields_ZeroesTheLhmOnlyReadings()
    {
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
            MemoryTemperature0 = 41,
            MemoryTemperature1 = 39,
            StorageTemperatureCelsius = 44,
            BatteryPowerWatts = 8.5,
        };

        HardwareMetricsSnapshot cleared = HardwareMonitoringService.ClearStaleSensorFields(last);

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
        Assert.Equal(0, cleared.MemoryTemperature0);
        Assert.Equal(0, cleared.MemoryTemperature1);
        Assert.Equal(0, cleared.StorageTemperatureCelsius);
        Assert.Equal(0, cleared.BatteryPowerWatts);
    }

    [Fact]
    public void ClearStaleSensorFields_KeepsOsStateEcFieldsAndTheGpuSelection()
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

        HardwareMetricsSnapshot kept = HardwareMonitoringService.ClearStaleSensorFields(last);

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
    public void WedgedProducer_FreshSourcesRefillTheirFields_NothingFrozenRemains()
    {
        // The full serve chain for a wedged producer: clear, then the EC CPU
        // temperature and the engine's live Ring0/SPD readings merge beside
        // the rebuilt OS fields. The GPU keeps no source (app-side decision)
        // and stays 0 - "--", not a 210 W statue.
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
                HardwareMonitoringService.ClearStaleSensorFields(last), 58),
            engine);

        Assert.Equal(58, served.CpuTemperature);
        Assert.Equal(CpuTemperatureSource.EcSensor, served.CpuTemperatureSource);
        Assert.Equal(96, served.CpuPowerWatts);
        Assert.Equal(4800, served.CpuClockMhz);
        Assert.Equal(43, served.MemoryTemperature0);
        Assert.Equal(0, served.GpuPowerWatts);
        Assert.Equal(0, served.GpuClockMhz);
    }
}
