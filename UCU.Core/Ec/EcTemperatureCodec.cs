using UCU.Core.Ec.Generated;

namespace UCU.Core.Ec
{
    /// <summary>
    /// RAMFAN2 temperature-threshold encoders.
    ///
    /// The EC fan tables have 16 slots: IDs 0-10 are active, IDs 11-15 are padded
    /// with 255 and read as inactive. The user edits one temperature step per zone;
    /// the EC needs two arrays — <c>temp_up</c> (rising threshold) and
    /// <c>temp_down</c> (falling threshold, i.e. the hysteresis band) — derived from
    /// those steps. Deriving them here keeps the derivation in one place instead of
    /// repeated at every call site.
    ///
    /// The encoders clamp exactly as the historical in-app implementation did, so a
    /// caller that passes out-of-range values gets the same bytes it always got;
    /// <see cref="ValidateTemperatureSteps"/> is the seam the engine uses to reject
    /// them up front instead.
    /// </summary>
    public static class EcTemperatureCodec
    {
        /// <summary>Total slots in one EC fan table (10 active + 1 OFF + 5 reserved).</summary>
        public const int TableLength = EcVault.EC_TABLE_LENGTH;

        /// <summary>Slots the encoders fill from user input: zone 0 (OFF) plus zones 1-10.</summary>
        public const int ActiveZoneCount = 11;

        /// <summary>Value the reserved slots are padded with; the EC reads them as inactive.</summary>
        public const byte InactivePadding = 255;

        /// <summary>Highest temperature threshold the EC encodes (degrees Celsius).</summary>
        public const int MaxThresholdCelsius = 255;

        /// <summary>
        /// Default temperature steps (degrees Celsius), the XMG Control Center
        /// 9-point scale with an OFF zone in front and an overflow zone behind.
        /// Index 0 = fan off, indices 1-9 = 55..95 in 5°C steps, index 10 = 97.
        /// </summary>
        public static int[] DefaultTemperatureSteps { get; } =
            { 0, 55, 60, 65, 70, 75, 80, 85, 90, 95, 97 };

        /// <summary>
        /// Builds the <c>temp_up</c> array: 0 for zone 0, step + 1 for zones 1-10,
        /// padded to 16 with 255. Inputs beyond <see cref="ActiveZoneCount"/> are
        /// ignored, inputs beyond the EC range are clamped.
        /// </summary>
        public static byte[] ToEcTempUp(int[] temps)
        {
            if (temps == null)
                throw new ArgumentNullException(nameof(temps));

            byte[] result = new byte[TableLength];
            Array.Fill(result, InactivePadding);

            int count = Math.Min(temps.Length, ActiveZoneCount);
            for (int i = 0; i < count; i++)
            {
                if (i == 0)
                    result[i] = 0;
                else
                    result[i] = (byte)Math.Clamp(temps[i] + 1, 0, MaxThresholdCelsius);
            }

            return result;
        }

        /// <summary>
        /// Builds the <c>temp_down</c> (hysteresis) array: step - 2 for zone 0
        /// (clamped at 0), step - 1 for zones 1-10, padded to 16 with 255.
        /// </summary>
        public static byte[] ToEcTempDown(int[] temps)
        {
            if (temps == null)
                throw new ArgumentNullException(nameof(temps));

            byte[] result = new byte[TableLength];
            Array.Fill(result, InactivePadding);

            int count = Math.Min(temps.Length, ActiveZoneCount);
            for (int i = 0; i < count; i++)
            {
                if (i == 0)
                    result[i] = (byte)Math.Clamp(temps[i] - 2, 0, MaxThresholdCelsius);
                else
                    result[i] = (byte)Math.Clamp(temps[i] - 1, 0, MaxThresholdCelsius);
            }

            return result;
        }

        /// <summary>
        /// Rejects temperature steps the EC cannot use: no steps, a non-zero OFF zone,
        /// a step outside 0-255, or a zone that is not strictly hotter than the one
        /// before it (the EC walks the tables in ascending order, so a plateau makes
        /// later zones unreachable). The engine calls this before touching hardware.
        /// </summary>
        public static void ValidateTemperatureSteps(int[] temps)
        {
            if (temps == null)
                throw new ArgumentNullException(nameof(temps));

            if (temps.Length == 0)
                throw new ArgumentOutOfRangeException(nameof(temps), "At least one temperature step is required.");

            int limit = Math.Min(temps.Length, ActiveZoneCount);

            if (temps[0] != 0)
                throw new ArgumentOutOfRangeException(nameof(temps),
                    $"Zone 0 is the OFF zone and must be 0, got {temps[0]}.");

            for (int i = 1; i < limit; i++)
            {
                if (temps[i] < 0 || temps[i] > MaxThresholdCelsius)
                    throw new ArgumentOutOfRangeException(nameof(temps),
                        $"Zone {i} temperature {temps[i]} is outside 0-{MaxThresholdCelsius}.");

                if (temps[i] <= temps[i - 1])
                    throw new ArgumentOutOfRangeException(nameof(temps),
                        $"Zone {i} temperature {temps[i]} must exceed zone {i - 1} ({temps[i - 1]}).");
            }
        }
    }
}
