namespace UCU.Core.Ec
{
    /// <summary>
    /// Plausibility rules for raw EC sensor bytes.
    ///
    /// The EC answers every read with a byte — including when it is reinitialising, when
    /// a channel is unpopulated, or when the read races the firmware's own update. Those
    /// bytes reach the UI as temperatures and fan speeds, and an unbounded garbage value
    /// reads as a real one: 0xFA becomes 250 °C on the dashboard and in the RTSS overlay.
    ///
    /// The rules are deliberately asymmetric. A too-high value is always wrong and is
    /// rejected; 0 is treated as "no reading" for temperature (a GPU the PnP power-saving
    /// switch has disabled answers 0, which is not a measurement) but is a valid fan speed
    /// (a stopped fan really is 0).
    ///
    /// 110 °C is above every junction limit these ECs expose (Intel 105, AMD TjMax 105 →
    /// 110 leaves margin for the firmware's own offset).
    /// </summary>
    public static class EcSensorPlausibility
    {
        /// <summary>Sentinel the verbs return instead of an implausible reading.</summary>
        /// <remarks>
        /// Every consumer in the app already treats a non-positive temperature or speed as
        /// "unknown" (dashboards print "--", the RTSS overlay omits the cell), so this is
        /// the value that needs no downstream change.
        /// </remarks>
        public const int Unknown = -1;

        /// <summary>Highest temperature the EC can legitimately report, in degrees Celsius.</summary>
        public const int MaxTemperatureCelsius = 110;

        /// <summary>Highest fan speed the EC can legitimately report, in RPM.</summary>
        public const int MaxRpm = 8000;

        /// <summary>Returns <paramref name="raw"/> when it is a plausible temperature, else -1.</summary>
        public static int Temperature(int raw) =>
            raw is >= 1 and <= MaxTemperatureCelsius ? raw : Unknown;

        /// <summary>Returns <paramref name="rpm"/> when it is a plausible fan speed, else -1.</summary>
        public static int Rpm(int rpm) =>
            rpm is >= 0 and <= MaxRpm ? rpm : Unknown;
    }
}
