namespace UCU.Core.Ec
{
    /// <summary>
    /// Encodes the EC battery-charging-mode register (0x07A6).
    ///
    /// The mode is a three-bit field (bits 3-5, mask 0x38) whose raw values are not
    /// the mode index: 0x08 / 0x18 / 0x28 for High Capacity (100 %), Balanced (80 %)
    /// and Stationary (60 %). Reads mask the field and fall back to High Capacity for
    /// a value this build does not recognise, which is what the shipped app does and
    /// what the UI relies on to show a mode rather than an error.
    /// </summary>
    public static class EcBatteryCodec
    {
        /// <summary>Field mask (bits 3-5) inside the battery-mode register.</summary>
        public const byte ModeMask = 0x38;

        /// <summary>Number of charging modes the firmware defines.</summary>
        public const int ModeCount = 3;

        /// <summary>Mode index to raw field value, indexed by mode (from the MechControl IDY_7Y profile).</summary>
        public static readonly byte[] ModeRawValues = { 0x08, 0x18, 0x28 };

        /// <summary>User-facing capacity ceiling for each mode index, in percent.</summary>
        public static readonly int[] ModeCapacityCeiling = { 100, 80, 60 };

        /// <summary>
        /// Encodes a mode index to the value written to the register. The write is a
        /// literal (not read-modify-write), matching the OEM tool and the shipped app.
        /// </summary>
        public static byte EncodeMode(int mode)
        {
            ValidateMode(mode);
            return ModeRawValues[mode];
        }

        /// <summary>
        /// Reads the mode index out of a raw register value. An unrecognised field
        /// falls back to <see cref="HighCapacity"/> rather than reporting an error,
        /// which keeps the UI showing a plausible mode on firmware this build does not
        /// know.
        /// </summary>
        public static int DecodeMode(byte raw)
        {
            int index = Array.IndexOf(ModeRawValues, (byte)(raw & ModeMask));
            return index < 0 ? HighCapacity : index;
        }

        /// <summary>Mode index for High Capacity (charge to 100 %).</summary>
        public const int HighCapacity = 0;

        /// <summary>Rejects a mode index the firmware does not define.</summary>
        public static void ValidateMode(int mode)
        {
            if (mode < 0 || mode >= ModeCount)
                throw new ArgumentOutOfRangeException(nameof(mode),
                    $"Battery mode must be 0-{ModeCount - 1}, got {mode}.");
        }

        // ---------------------------------------------------------------------
        // Charge thresholds — the field-verified DERIVED view of the mode
        // (U-103, lab-box probe 2026-10-30):
        //
        // The open-revo chassis registry exposes an explicit threshold pair
        // (0x07C9 up / 0x07D0 down, gate bit 0x0742!bit4, platform override
        // 0x0770). The lab box REFUTES that layout: 0x07C9 reads 0, 0x07D0
        // reads 95 — not a sane percent pair — while 0x07A6 decodes cleanly
        // through the UCU field codec above (docs\acpi\BatteryDiagnostic).
        // So this chassis has NO exposed threshold registers: its thresholds
        // ARE the mode's capacity ceiling, and the pair below is a
        // presentation of the verified mode, not a second control path.
        // Writing 0x07C9/0x07D0 on the strength of a foreign chassis' registry
        // is the evidence-first rule UCU does not bend: an unverified write
        // number against an EC-proprietary layout is exactly the brick vector.
        // ---------------------------------------------------------------------

        /// <summary>Lowest charge threshold the mode table encodes (Stationary), in percent.</summary>
        public const int MinThresholdPercent = 60;

        /// <summary>Highest charge threshold the mode table encodes (High Capacity), in percent.</summary>
        public const int MaxThresholdPercent = 100;

        /// <summary>Re-charge hysteresis below the ceiling, in points (the open-revo driver convention, `limit - 5`).</summary>
        public const int ThresholdHysteresisPoints = 5;

        /// <summary>Lowest re-charge restart point a legal pair carries (the Stationary ceiling minus the hysteresis).</summary>
        public const int MinRestartPercent = MinThresholdPercent - ThresholdHysteresisPoints;

        /// <summary>
        /// The charge-threshold pair implied by a mode: the capacity ceiling is
        /// the charge-UP threshold, and charging restarts at <see
        /// cref="ThresholdHysteresisPoints"/> points below it. Read-only view —
        /// no register carries the pair on this chassis family.
        /// </summary>
        public static (int UpPercent, int DownPercent) ThresholdsForMode(int mode)
        {
            ValidateMode(mode);
            int up = ModeCapacityCeiling[mode];
            return (up, up - ThresholdHysteresisPoints);
        }

        /// <summary>
        /// Validates a threshold pair as the open-revo-style contract (the
        /// ceiling inside 60..100, the restart point inside 55..100, restart
        /// strictly below the ceiling). The codec seam the engine would call
        /// before any hypothetical threshold write; today it validates the
        /// derived view so a codec-level impossibility can never render as a
        /// UI number.
        /// </summary>
        public static void ValidateThresholds(int upPercent, int downPercent)
        {
            if (upPercent < MinThresholdPercent || upPercent > MaxThresholdPercent)
                throw new ArgumentOutOfRangeException(nameof(upPercent),
                    $"Charge-up threshold must be {MinThresholdPercent}-{MaxThresholdPercent} %, got {upPercent}.");

            if (downPercent < MinRestartPercent || downPercent > MaxThresholdPercent)
                throw new ArgumentOutOfRangeException(nameof(downPercent),
                    $"Charge-down threshold must be {MinRestartPercent}-{MaxThresholdPercent} %, got {downPercent}.");

            if (downPercent >= upPercent)
                throw new ArgumentOutOfRangeException(nameof(downPercent),
                    $"Charge-down threshold ({downPercent} %) must be below the charge-up threshold ({upPercent} %).");
        }

        // ---------------------------------------------------------------------
        // Gate status + platform override — read-only EC state.
        // ---------------------------------------------------------------------

        /// <summary>Bit inside the gate-status register (0x0742) marking an active charge gate.</summary>
        public const byte GateActiveBit = 0x10;

        /// <summary>
        /// Decodes the charge-gate bit (0x0742!bit4): true while the EC gates
        /// charging (battery at/above its ceiling). Same register+bit UCU
        /// decodes as FAN_TURBO_SUPPORTED on fan-capability rows — the EC
        /// multiplexes the byte by feature, so each row reads its own bit's
        /// meaning and never the other's.
        /// </summary>
        public static bool IsChargeGateActive(byte gateStatusRaw) => (gateStatusRaw & GateActiveBit) != 0;

        /// <summary>Value of the platform-override register (0x0770) meaning "no override".</summary>
        public const byte PlatformOverrideReset = 0xFF;

        /// <summary>Value the OEM/performance tools pin the override register to while spoofing the platform.</summary>
        public const byte PlatformOverridePinned = 5;

        /// <summary>
        /// True when a platform override is in effect. 0xFF is the documented
        /// reset value (field-verified: the lab box idles at 0xFF); anything
        /// else — the pin value 5 and the legacy 4 — is an override a
        /// firmware-side or third-party tool installed, which changes how the
        /// EC applies charge modes, so the UI must say so.
        /// </summary>
        public static bool IsPlatformOverrideActive(byte overrideRaw) => overrideRaw != PlatformOverrideReset;
    }
}
