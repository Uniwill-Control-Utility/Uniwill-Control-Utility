using System;

namespace UCU.Hardware.Metrics
{
    /// <summary>
    /// The kernel-side sensor probe: read cycle, reset for retry, teardown.
    /// <see cref="LhmSensorProbe"/> is the production implementation; the seam
    /// exists so the engine host can run with a scripted probe in tests - the
    /// same reason the EC transport is overridable: the real probe opens real
    /// sensor drivers, which has no place inside a test process.
    /// </summary>
    public interface ISensorProbe : IDisposable
    {
        SensorProbeReading Read();

        void Reset();
    }
}
