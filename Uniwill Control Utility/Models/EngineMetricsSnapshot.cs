namespace Uniwill_Control_Utility.Models
{
    /// <summary>
    /// The engine's kernel-side sensor readings (CPU Ring0 power/core clock, DIMM
    /// SPD speed and temperatures), as the app consumes them: the dashboard merges
    /// these into its own snapshot for the fields the in-process LHM probe cannot
    /// read unelevated - the sensor drivers' device ACLs admit System +
    /// Administrators, so an asInvoker app installed per-machine gets access
    /// denied. 0 = no reading, the snapshot's zero-is-unavailable convention.
    ///
    /// A UI model, deliberately not the wire DTO (see EcFanDutySnapshot).
    /// </summary>
    public readonly record struct EngineMetricsSnapshot
    {
        public static EngineMetricsSnapshot Empty { get; } = new();

        /// <summary>CPU package power in watts.</summary>
        public int CpuPowerWatts { get; init; }

        /// <summary>Average CPU core clock in MHz.</summary>
        public int CpuClockMhz { get; init; }

        /// <summary>Memory module data rate in MT/s (SPD).</summary>
        public int MemorySpeedMts { get; init; }

        /// <summary>DIMM 0 temperature in Celsius.</summary>
        public int MemoryTemperature0 { get; init; }

        /// <summary>DIMM 1 temperature in Celsius.</summary>
        public int MemoryTemperature1 { get; init; }

        /// <summary>The engine's sensor probe is open and answering.</summary>
        public bool ProbeOpen { get; init; }

        /// <summary>
        /// Seconds between the engine's probe publishing the reading and the
        /// verb serving it. The engine's probe loop runs outside the watchdog
        /// on purpose, so freshness travels with the data; the service gate
        /// (<see cref="Services.IEcEngineMetricsSource"/>) turns over-budget
        /// readings into Empty, and consumers never see this field as a
        /// number they must judge.
        /// </summary>
        public int ReadingAgeSeconds { get; init; }
    }
}
