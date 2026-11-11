namespace UCU.Core.Supervision
{
    /// <summary>What the thermal failsafe wants to do with this sample.</summary>
    public enum ThermalFailsafeDecision
    {
        /// <summary>Nothing to change (including "no valid reading").</summary>
        None = 0,
        /// <summary>Hold the GPU at minimum power.</summary>
        Trip = 1,
        /// <summary>The GPU has cooled; restore the contract captured at the trip.</summary>
        Clear = 2,
    }

    /// <summary>
    /// The decision rule for the thermal failsafe (US-11): hold the GPU at minimum power
    /// when it stays too hot, and release only once it is comfortably below the threshold.
    ///
    /// Pure so the two failure directions are pinned by tests. Tripping too eagerly
    /// silently costs performance; releasing too eagerly lets a hot GPU cycle between
    /// full and minimum power every few seconds.
    ///
    /// The caller counts consecutive identical decisions (three samples at ~1 s), so one
    /// stray reading - including the 0/255 bytes the EC emits while it reinitialises -
    /// can neither trip nor release anything. Unknown readings always decide None.
    /// </summary>
    public static class ThermalFailsafePolicy
    {
        /// <summary>Below this a "threshold" is inside normal load temperatures; refuse it.</summary>
        public const int MinThresholdCelsius = 70;

        /// <summary>Above this the trip point sits past the junction limit; refuse it.</summary>
        public const int MaxThresholdCelsius = 95;

        /// <summary>
        /// CPU trip band. CPUs run hotter than GPUs under sustained load (a 9955HX3D sits
        /// in the high 80s in a game), so the CPU needs its own, higher trip point - a
        /// shared one would hold CPU power down permanently.
        /// </summary>
        public const int MinCpuThresholdCelsius = 85;

        public const int MaxCpuThresholdCelsius = 100;

        public const int DefaultCpuThresholdCelsius = 95;
        /// <summary>Default trip point. Well below TjMax (105) so the hold lands before throttling.</summary>
        public const int DefaultThresholdCelsius = 88;

        /// <summary>Samples that must agree before acting.</summary>
        public const int SamplesRequired = 3;

        /// <summary>
        /// How far below the threshold the GPU must get before release. Larger than the
        /// fan-curve hysteresis on purpose: a trip is a power cut, not a fan step.
        /// </summary>
        public const int ClearHysteresisCelsius = 5;

        /// <summary>True when the value is a threshold the policy can use.</summary>
        public static bool IsValidThreshold(int threshold) =>
            threshold is >= MinThresholdCelsius and <= MaxThresholdCelsius;

        /// <summary>True when the value is a CPU threshold the policy can use.</summary>
        public static bool IsValidCpuThreshold(int threshold) =>
            threshold is >= MinCpuThresholdCelsius and <= MaxCpuThresholdCelsius;

        /// <summary>
        /// One sample's verdict. <paramref name="tripped"/> is the persisted state, so a
        /// restart cannot re-trip an already-held GPU or release one early.
        /// </summary>
        public static ThermalFailsafeDecision Decide(int? temperatureCelsius, int threshold, bool tripped)
        {
            if (temperatureCelsius is null || !IsValidThreshold(threshold))
                return ThermalFailsafeDecision.None;

            if (!tripped)
                return temperatureCelsius >= threshold
                    ? ThermalFailsafeDecision.Trip
                    : ThermalFailsafeDecision.None;

            return temperatureCelsius <= threshold - ClearHysteresisCelsius
                ? ThermalFailsafeDecision.Clear
                : ThermalFailsafeDecision.None;
        }
    }
}
