using Uniwill_Control_Utility.Models;
using Uniwill_Control_Utility.Services;
using Uniwill_Control_Utility.Scripts.Misc;
using Uniwill_Control_Utility.ViewModels;

namespace Uniwill_Control_Utility.Tests.ViewModels;

/// <summary>
/// The dashboard's merge of the engine's kernel-side readings (field report
/// 2026-10-29: CPU power, CPU clock and RAM speed never appeared on an
/// unelevated install - the sensor drivers' ACLs admit System + Administrators
/// and the WMI speed source was late/zero). The merge fills only the fields the
/// app's own probe left at 0, and the RAM-speed cache publishes the SPD value
/// for a repaint exactly once.
/// </summary>
public sealed class DashboardEngineMetricsMergeTests
{
    [Fact]
    public void ApplyEngineMetrics_FillsZeroFields_KeepsOwnReadings()
    {
        var app = new HardwareMetricsSnapshot
        {
            CpuPowerWatts = 0,
            CpuClockMhz = 5298,
            MemoryTemperature0 = 0,
            MemoryTemperature1 = 44,
            GpuPowerWatts = 0,
            GpuClockMhz = 0,
        };
        var engine = new EngineMetricsSnapshot
        {
            CpuPowerWatts = 104,
            CpuClockMhz = 5150,
            MemoryTemperature0 = 41,
            MemoryTemperature1 = 39,
            ProbeOpen = true,
        };

        HardwareMetricsSnapshot merged = DashboardViewModel.ApplyEngineMetrics(app, engine);

        Assert.Equal(104, merged.CpuPowerWatts);
        Assert.Equal(5298, merged.CpuClockMhz);
        Assert.Equal(41, merged.MemoryTemperature0);
        Assert.Equal(44, merged.MemoryTemperature1);
    }

    [Fact]
    public void ApplyEngineMetrics_LeavesTheGpuFieldsAlone()
    {
        // GPU power/clock stay app-side (NVAPI decision): the engine has no GPU
        // reading, so the merge must not touch them even when they are 0.
        var app = new HardwareMetricsSnapshot { GpuPowerWatts = 0, GpuClockMhz = 0 };
        var engine = new EngineMetricsSnapshot { CpuPowerWatts = 88, ProbeOpen = true };

        HardwareMetricsSnapshot merged = DashboardViewModel.ApplyEngineMetrics(app, engine);

        Assert.Equal(0, merged.GpuPowerWatts);
        Assert.Equal(0, merged.GpuClockMhz);
    }

    [Fact]
    public void PublishRamSpeedIfMissing_FillsOnceAndFiresTheEvent()
    {
        int speedBefore = CachedSystemInfo.RamSpeed;
        try
        {
            CachedSystemInfo.RamSpeed = 0;
            int fired = 0;
            void OnFired() => Interlocked.Increment(ref fired);
            CachedSystemInfo.RamSpeedChanged += OnFired;

            CachedSystemInfo.PublishRamSpeedIfMissing(5602);
            CachedSystemInfo.PublishRamSpeedIfMissing(6400);

            Assert.Equal(5602, CachedSystemInfo.RamSpeed);
            Assert.Equal(1, Volatile.Read(ref fired));
        }
        finally
        {
            CachedSystemInfo.RamSpeed = speedBefore;
        }
    }

    [Fact]
    public void PublishRamSpeedIfMissing_AWmiValueThatLandedWins_NoOverwriteNoEvent()
    {
        // WMI reported the configured speed at startup: the SPD-supported rate
        // must not replace it, and the card must not repaint.
        int speedBefore = CachedSystemInfo.RamSpeed;
        try
        {
            CachedSystemInfo.RamSpeed = 4800;
            bool fired = false;
            CachedSystemInfo.RamSpeedChanged += () => fired = true;

            CachedSystemInfo.PublishRamSpeedIfMissing(5600);

            Assert.Equal(4800, CachedSystemInfo.RamSpeed);
            Assert.False(fired);
        }
        finally
        {
            CachedSystemInfo.RamSpeed = speedBefore;
        }
    }

    [Fact]
    public void PublishRamSpeedIfMissing_IgnoresZero()
    {
        int speedBefore = CachedSystemInfo.RamSpeed;
        try
        {
            CachedSystemInfo.RamSpeed = 0;

            CachedSystemInfo.PublishRamSpeedIfMissing(0);

            Assert.Equal(0, CachedSystemInfo.RamSpeed);
        }
        finally
        {
            CachedSystemInfo.RamSpeed = speedBefore;
        }
    }
}
