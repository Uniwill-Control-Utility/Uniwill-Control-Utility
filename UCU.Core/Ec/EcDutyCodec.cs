namespace UCU.Core.Ec
{
    /// <summary>
    /// EC fan-duty encoders. The EC's duty scale is 0-200 while the UI works in
    /// 0-100 %, and the OEM default curves top out at 180, so the three scales need
    /// one agreed conversion instead of a "* 2" scattered across pages.
    /// </summary>
    public static class EcDutyCodec
    {
        /// <summary>Slots in one duty table (10 active + 1 OFF + 5 padding).</summary>
        public const int TableLength = 16;

        /// <summary>First padding slot; zones from here to 15 repeat the max duty.</summary>
        public const int FirstPaddingZone = 11;

        /// <summary>Highest duty value the EC accepts.</summary>
        public const int MaxEcDuty = 200;

        /// <summary>Top of the UI duty scale (percent).</summary>
        public const int MaxDutyPercent = 100;

        /// <summary>Duty value the OEM default gradient reaches at 100 %.</summary>
        public const int OemGradientMax = 180;

        /// <summary>EC duty units per UI percent point.</summary>
        public const int EcDutyPerPercent = 2;

        /// <summary>Converts a UI duty percentage to the EC 0-200 scale (clamped).</summary>
        public static int PercentToEcDuty(int percent) =>
            Math.Clamp(percent, 0, MaxDutyPercent) * EcDutyPerPercent;

        /// <summary>Converts an EC 0-200 duty value back to a UI percentage.</summary>
        public static int EcDutyToPercent(int duty) =>
            Math.Clamp(duty, 0, MaxEcDuty) / EcDutyPerPercent;

        /// <summary>
        /// Builds the 16-byte duty table from per-zone percentages: zone 0 is always
        /// 0 (fan off below the OFF threshold), zones 1..n are the scaled user duties,
        /// zones 11-15 are padded with the highest duty in the curve.
        /// </summary>
        public static byte[] BuildDutyTable(IReadOnlyList<int> dutyPercentByZone)
        {
            if (dutyPercentByZone == null)
                throw new ArgumentNullException(nameof(dutyPercentByZone));

            byte[] table = new byte[TableLength];

            int count = Math.Min(dutyPercentByZone.Count, TableLength);
            for (int i = 0; i < count; i++)
                table[i] = (byte)(i == 0 ? 0 : PercentToEcDuty(dutyPercentByZone[i]));

            byte maxDuty = 0;
            for (int i = 0; i < count; i++)
                maxDuty = Math.Max(maxDuty, table[i]);

            for (int i = FirstPaddingZone; i < TableLength; i++)
                table[i] = maxDuty;

            return table;
        }

        /// <summary>
        /// Scales an OEM default duty gradient down to a target maximum duty.
        ///
        /// The EC requires duty to be monotonically non-decreasing across zones, and
        /// the OEM gradient it ships has zone 0 at 0 and its top at
        /// <see cref="OemGradientMax"/>, so scaling is proportional to
        /// <c>targetMax / 180</c> with a monotonic repair pass. Padding zones take the
        /// OEM 180 when the target is at or above it, otherwise the target, which
        /// keeps the curve's flat top at the requested ceiling.
        /// </summary>
        public static byte[] ScaleDutyGradient(byte[] defaultDuty, byte targetMax)
        {
            if (defaultDuty == null)
                throw new ArgumentNullException(nameof(defaultDuty));

            byte[] result = new byte[TableLength];
            int activeCount = defaultDuty.Length;

            for (int i = 0; i < activeCount; i++)
            {
                if (i == 0)
                {
                    result[i] = 0;
                }
                else
                {
                    int scaled = (int)Math.Round((double)defaultDuty[i] * targetMax / (double)OemGradientMax);
                    scaled = Math.Max(scaled, result[i - 1]);
                    result[i] = (byte)Math.Clamp(scaled, 0, MaxEcDuty);
                }
            }

            byte padValue = (byte)Math.Min((int)targetMax, OemGradientMax);
            for (int i = activeCount; i < TableLength; i++)
                result[i] = padValue;

            return result;
        }

        /// <summary>
        /// Rejects duty values outside the UI scale. The engine calls this before
        /// touching hardware so a bad preset fails with a reason instead of silently
        /// running the fans at a clamped speed.
        /// </summary>
        public static void ValidateDutyPercent(int percent)
        {
            if (percent < 0 || percent > MaxDutyPercent)
                throw new ArgumentOutOfRangeException(nameof(percent),
                    $"Fan duty must be 0-{MaxDutyPercent} %, got {percent}.");
        }

        /// <summary>
        /// Rejects a duty table that is not monotonically non-decreasing over its
        /// active zones, which the EC documents as a requirement.
        /// </summary>
        public static void ValidateDutyTable(byte[] duty)
        {
            if (duty == null)
                throw new ArgumentNullException(nameof(duty));

            int activeZones = Math.Min(duty.Length, FirstPaddingZone);
            for (int i = 1; i < activeZones; i++)
            {
                if (duty[i] < duty[i - 1])
                    throw new ArgumentOutOfRangeException(nameof(duty),
                        $"Duty must be non-decreasing: zone {i} ({duty[i]}) is below zone {i - 1} ({duty[i - 1]}).");

                if (duty[i] > MaxEcDuty)
                    throw new ArgumentOutOfRangeException(nameof(duty),
                        $"Zone {i} duty {duty[i]} exceeds the EC maximum {MaxEcDuty}.");
            }
        }
    }
}
