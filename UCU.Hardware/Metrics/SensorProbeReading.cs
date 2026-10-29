using LibreHardwareMonitor.Hardware;

namespace UCU.Hardware.Metrics
{
    /// <summary>
    /// One flat sensor datum out of the probe's hardware tree. The extraction is
    /// kept behind this shape so the naming/units logic is a pure function the
    /// tests can feed synthetic trees to, and the LHM walk stays thin.
    /// </summary>
    public readonly record struct SensorDatum(
        HardwareType Hardware,
        SensorType Sensor,
        string Name,
        float Value);

    /// <summary>
    /// What the kernel-side probe read this cycle. 0 = no reading (the UI shows
    /// "--", the snapshot's zero-is-unavailable convention). ProbeOpen distinguishes
    /// "the sensor stack is not up" from "it is up and this field stayed silent".
    /// </summary>
    public sealed record SensorProbeReading
    {
        /// <summary>CPU package power in watts (Ring0 MSR via PawnIO).</summary>
        public int CpuPowerWatts { get; init; }

        /// <summary>Average CPU core clock in MHz (Ring0 MSR via PawnIO).</summary>
        public int CpuClockMhz { get; init; }

        /// <summary>Memory module data rate in MT/s (SPD via the SMBus).</summary>
        public int MemorySpeedMts { get; init; }

        /// <summary>DIMM 0 temperature in Celsius (SPD thermal sensor).</summary>
        public int MemoryTemperature0 { get; init; }

        /// <summary>DIMM 1 temperature in Celsius (SPD thermal sensor).</summary>
        public int MemoryTemperature1 { get; init; }

        /// <summary>The probe's Computer is open and sensors answered this cycle.</summary>
        public bool ProbeOpen { get; init; }
    }
}
