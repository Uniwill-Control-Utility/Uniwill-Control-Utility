using UCU.Contracts;
using UCU.Hardware.Metrics;

namespace UCU.Engine.Tests;

/// <summary>
/// The serve-time freshness stamp of the cached sensor snapshot. The probe loop
/// runs outside the watchdog on purpose, so a wedged loop keeps publishing
/// NOTHING and the age is the app's only signal that the numbers are frozen -
/// these tests pin the stamp itself.
/// </summary>
public sealed class EngineMetricsSnapshotSourceTests
{
    [Fact]
    public void Served_BeforeAnyPublish_IsProbeClosedWithAnAgeBeyondEveryBudget()
    {
        var source = new EngineMetricsSnapshotSource();

        MetricsSnapshotDto served = source.Served();

        Assert.False(served.ProbeOpen);
        Assert.Equal(int.MaxValue, served.ReadingAgeSeconds);
    }

    [Fact]
    public void Served_ArightAfterPublish_IsZeroAged()
    {
        var source = new EngineMetricsSnapshotSource();
        source.Publish(new SensorProbeReading { CpuPowerWatts = 104, ProbeOpen = true });

        MetricsSnapshotDto served = source.Served();

        Assert.True(served.ProbeOpen);
        Assert.Equal(104, served.CpuPowerWatts);
        Assert.InRange(served.ReadingAgeSeconds, 0, 1);
    }

    [Fact]
    public void Served_AgesWithRealTime_WhenTheLoopStopsPublishing()
    {
        // The wedge simulation is the loop simply not publishing: the stamp is
        // measured against the publish time, so time alone ages the reading.
        var source = new EngineMetricsSnapshotSource();
        source.Publish(new SensorProbeReading { CpuClockMhz = 5298, ProbeOpen = true });

        Thread.Sleep(1100);
        MetricsSnapshotDto served = source.Served();

        Assert.True(served.ReadingAgeSeconds >= 1);
        Assert.Equal(5298, served.CpuClockMhz); // the value persists; only age grows
    }

    [Fact]
    public void Publish_RefreshesTheAge()
    {
        var source = new EngineMetricsSnapshotSource();
        source.Publish(new SensorProbeReading { ProbeOpen = true });
        Thread.Sleep(1100);
        source.Publish(new SensorProbeReading { CpuPowerWatts = 50, ProbeOpen = true });

        Assert.InRange(source.Served().ReadingAgeSeconds, 0, 1);
    }
}
