using UCU.Contracts;
using UCU.Hardware.Metrics;

namespace UCU.Engine
{
    /// <summary>
    /// The published sensor readings the telemetry.get_metrics_snapshot verb
    /// serves. The probe service writes it; the verb reads it. The verb performs
    /// zero hardware I/O by construction, so the sensor probe - the thing that
    /// parks on a guarded machine (the app's own LHM probe parks at startup in
    /// the field) - can never queue behind, delay, or wedge a pipe call.
    ///
    /// Freshness travels with the answer, not with the cache: the probe loop is
    /// deliberately OUTSIDE the watchdog (a wedged sensor open must not FailFast
    /// an engine whose EC queues are healthy, and the watchdog only sees serial
    /// queues), so a wedged loop keeps publishing NOTHING and the only way the
    /// app can tell a live reading from a frozen one is the age the verb stamps
    /// at serve time. The app budgets ~6 cycles; older is rendered "--".
    /// </summary>
    public sealed class EngineMetricsSnapshotSource
    {
        /// <summary>Serves "no reading ever taken": ProbeOpen false, age beyond
        /// any budget the app can have.</summary>
        public static MetricsSnapshotDto NeverPublished { get; } = new()
        {
            ProbeOpen = false,
            ReadingAgeSeconds = int.MaxValue,
        };

        private volatile MetricsSnapshotDto? _current;
        private DateTime _publishedUtc = DateTime.MinValue;

        /// <summary>The latest published reading, age unstamped. Verb callers
        /// must use <see cref="Served"/>; this is the raw cache view.</summary>
        public MetricsSnapshotDto Current => _current ?? NeverPublished;

        public void Publish(SensorProbeReading reading)
        {
            _current = new MetricsSnapshotDto
            {
                CpuPowerWatts = reading.CpuPowerWatts,
                CpuClockMhz = reading.CpuClockMhz,
                MemorySpeedMts = reading.MemorySpeedMts,
                MemoryTemperature0 = reading.MemoryTemperature0,
                MemoryTemperature1 = reading.MemoryTemperature1,
                ProbeOpen = reading.ProbeOpen,
            };
            _publishedUtc = DateTime.UtcNow;
        }

        /// <summary>The cache stamped with its serve-time age: seconds since the
        /// probe last published. A wedged loop (publishing nothing) produces a
        /// monotonically rising age, which is the app's "--" signal.</summary>
        public MetricsSnapshotDto Served()
        {
            MetricsSnapshotDto current = Current;
            if (ReferenceEquals(current, NeverPublished))
                return current;

            long ageSeconds = (long)(DateTime.UtcNow - _publishedUtc).TotalSeconds;
            return current with
            {
                ReadingAgeSeconds = (int)Math.Min(ageSeconds, int.MaxValue),
            };
        }
    }
}
