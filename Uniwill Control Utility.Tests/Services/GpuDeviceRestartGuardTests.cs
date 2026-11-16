using LibreHardwareMonitor.Hardware;
using System;
using System.Threading.Tasks;
using Uniwill_Control_Utility.Services;

namespace Uniwill_Control_Utility.Tests.Services;

/// <summary>
/// The GPU unlock restarts the device, which kills every NVML handle the monitoring
/// service holds; the next call on one is an access violation in native code (field
/// 2026-11-16: the app and HWiNFO64 both faulted in nvml.dll in the same second as a
/// pnputil restart). So the restart verb must only ever run with monitoring parked,
/// and the park must be released on both the success and the failure path.
/// </summary>
public sealed class GpuDeviceRestartGuardTests
{
    [Fact]
    public async Task RestartVerb_RunsOnlyWhileMonitoringsParked_AndReleasesAfter()
    {
        var monitoring = new RecordingMonitoringService();
        bool parkedDuringVerb = false;

        int answer = await GpuDeviceRestartGuard.RunAsync(monitoring, () =>
        {
            parkedDuringVerb = monitoring.IsParked;
            return Task.FromResult(210);
        });

        Assert.Equal(210, answer);
        Assert.True(parkedDuringVerb);
        Assert.False(monitoring.IsParked);
    }

    [Fact]
    public async Task RestartVerb_Throws_StillReleasesThePark()
    {
        // A transport failure is not a verdict: the device may still have restarted,
        // so the handles stay parked-and-cooling exactly as on the success path.
        var monitoring = new RecordingMonitoringService();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            GpuDeviceRestartGuard.RunAsync<int>(monitoring, () => throw new InvalidOperationException("transport timeout")));

        Assert.False(monitoring.IsParked);
    }

    /// <summary>Counts parks so the guard's contract is observable.</summary>
    private sealed class RecordingMonitoringService : IHardwareMonitoringService
    {
        private int _parks;

        public bool IsParked => Volatile.Read(ref _parks) > 0;

        public IDisposable Acquire(HardwareMonitoringCategory categories) => throw new NotSupportedException();

        public void AcquirePersistent(HardwareMonitoringCategory categories) => throw new NotSupportedException();

        public HardwareMetricsSnapshot ReadSnapshot() => new();

        public float ReadSensor(HardwareType hardwareType, SensorType sensorType, string sensorName) => 0;

        public void ResetComputer() { }

        public IDisposable Suspend()
        {
            System.Threading.Interlocked.Increment(ref _parks);
            return new Park(this);
        }

        private sealed class Park : IDisposable
        {
            private readonly RecordingMonitoringService _owner;
            private int _disposed;

            public Park(RecordingMonitoringService owner) => _owner = owner;

            public void Dispose()
            {
                if (System.Threading.Interlocked.Exchange(ref _disposed, 1) == 0)
                    System.Threading.Interlocked.Decrement(ref _owner._parks);
            }
        }
    }
}
