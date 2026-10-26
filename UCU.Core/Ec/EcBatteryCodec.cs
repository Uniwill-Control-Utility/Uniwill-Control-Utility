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
    }
}
