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
    /// </summary>
    public sealed class EngineMetricsSnapshotSource
    {
        private volatile MetricsSnapshotDto? _current;

        /// <summary>The latest published reading, or the all-zero/ProbeOpen=false
        /// default while the probe has not produced one. The verb never returns
        /// null: an app that asks gets the shape, with zeros meaning "no
        /// reading", the same convention the dashboard's own snapshot uses.</summary>
        public MetricsSnapshotDto Current => _current ?? Default;

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
        }

        private static MetricsSnapshotDto Default { get; } = new();
    }
}
