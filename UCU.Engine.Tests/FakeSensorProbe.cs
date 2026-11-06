using UCU.Hardware.Metrics;

namespace UCU.Engine.Tests;

/// <summary>
/// Sensor probe that answers every cycle with a fixed reading (the default: no
/// readings). The production host's LHM probe opens real sensor drivers
/// (Ring0 MSRs, SMBus SPD) - a side effect a test process must never cause, so
/// the fixture injects this through EngineRuntimeOptions.SensorProbeOverride,
/// the same seam as the fake EC transport.
/// </summary>
internal sealed class FakeSensorProbe : ISensorProbe
{
    private readonly SensorProbeReading _reading;

    public FakeSensorProbe(SensorProbeReading? reading = null) => _reading = reading ?? new SensorProbeReading();

    public int ReadCount { get; private set; }

    public SensorProbeReading Read()
    {
        ReadCount++;
        return _reading;
    }

    public void Reset()
    {
    }

    public void Dispose()
    {
    }
}
