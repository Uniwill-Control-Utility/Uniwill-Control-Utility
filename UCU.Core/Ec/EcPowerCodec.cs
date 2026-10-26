namespace UCU.Core.Ec
{
    /// <summary>
    /// Encodes the EC power-mode registers: the CPU power limits, the TCC offset, the
    /// OEM performance-mode index, and the literal sequences the OEM "stock" presets
    /// write.
    ///
    /// Several of these registers are read-modify-write: 0x07AB keeps three
    /// firmware-owned high bits plus a low bit, and bit 4 is the OEM engine's arm
    /// bit, so writing a bare mode value disarms the performance-button engine.
    /// Every encoder here takes the previously read byte and returns the byte to
    /// write, which keeps the read-modify-write sequencing in the caller (the engine,
    /// under the EC lock) while making the bit math itself pure and testable.
    /// </summary>
    public static class EcPowerCodec
    {
        /// <summary>Lowest power limit the EC encodes, in watts.</summary>
        public const int MinPowerWatts = 0;

        /// <summary>Highest power limit the EC encodes (single byte), in watts.</summary>
        public const int MaxPowerWatts = 255;

        /// <summary>Highest TCC offset the EC encodes (degrees below the default junction temp).</summary>
        public const int MaxTccOffset = 255;

        /// <summary>Number of OEM performance modes: quiet, balanced, performance, battery saver.</summary>
        public const int PerformanceModeCount = 4;

        /// <summary>Bit 4 of the mode-index register: arms the OEM performance-button engine.</summary>
        public const byte PerformanceModeArmBit = 0x10;

        /// <summary>Firmware-owned high bits of the mode-index register (bits 5-7).</summary>
        public const byte ModeIndexHighBitsMask = 0xE0;

        /// <summary>Low bit of the mode-index register, preserved across writes.</summary>
        public const byte ModeIndexLowBit = 0x01;

        /// <summary>Mask of the mode-index register's low nibble (the mode index itself).</summary>
        public const byte ModeIndexMask = 0x0F;

        /// <summary>Bit 7 of 0x0726: custom power-management gate. Stock presets clear it.</summary>
        public const byte PowerManagementGateBit = 0x80;

        /// <summary>Bit 6 of 0x0727: custom-profile enable. Stock presets clear it so the firmware's factory table applies.</summary>
        public const byte CustomProfileEnableBit = 0x40;

        /// <summary>Bit 4 of the trigger register, preserved by the stock-preset write.</summary>
        public const byte TriggerPreservedBit = 0x10;

        /// <summary>Bit 5 of the trigger register: apply color.</summary>
        public const byte TriggerApplyColorBit = 0x20;

        /// <summary>Bit 3 of the trigger register, set by the Office stock preset only.</summary>
        public const byte TriggerOfficeBit = 0x08;

        /// <summary>Bit 4 of the fan-control register: turbo. Forced by the Beast stock preset.</summary>
        public const byte FanTurboBit = 0x10;

        /// <summary>OEM stock mode index for Office (quiet).</summary>
        public const int StockModeOffice = 0;

        /// <summary>OEM stock mode index for Balance.</summary>
        public const int StockModeBalance = 1;

        /// <summary>OEM stock mode index for Beast (performance).</summary>
        public const int StockModeBeast = 2;

        /// <summary>Highest accepted OEM stock mode index.</summary>
        public const int MaxStockMode = StockModeBeast;

        /// <summary>
        /// Literal the OEM writes to the fan-control register for Office
        /// (USER | HIGH). This is what recolors the Q-key LED in a stock preset.
        /// </summary>
        public const byte FanProfileOffice = 0xA0;

        /// <summary>Literal the OEM writes to the fan-control register for Balance (profile bits cleared).</summary>
        public const byte FanProfileBalance = 0x00;

        /// <summary>Literal the OEM writes to the fan-control register for Beast (TURBO).</summary>
        public const byte FanProfileBeast = FanTurboBit;

        /// <summary>
        /// Commit value the OEM writes last in every stock-preset body (and never in a
        /// custom profile): 0x83.
        /// </summary>
        public const byte StockModeCommit = 0x83;

        /// <summary>Encodes a CPU power limit (PL1/PL2/PL4/PL5) in watts to its EC byte.</summary>
        public static byte EncodePowerLimit(int watts) => (byte)Math.Clamp(watts, MinPowerWatts, MaxPowerWatts);

        /// <summary>Encodes a TCC offset to its EC byte.</summary>
        public static byte EncodeTccOffset(int offset) => (byte)Math.Clamp(offset, 0, MaxTccOffset);

        /// <summary>
        /// Rejects a power limit the EC cannot represent. The engine calls this before
        /// the write so an out-of-range preset is reported, not clamped into the CPU.
        /// </summary>
        public static void ValidatePowerLimit(int watts)
        {
            if (watts < MinPowerWatts || watts > MaxPowerWatts)
                throw new ArgumentOutOfRangeException(nameof(watts),
                    $"Power limit must be {MinPowerWatts}-{MaxPowerWatts} W, got {watts}.");
        }

        /// <summary>Rejects a TCC offset the EC cannot represent.</summary>
        public static void ValidateTccOffset(int offset)
        {
            if (offset < 0 || offset > MaxTccOffset)
                throw new ArgumentOutOfRangeException(nameof(offset),
                    $"TCC offset must be 0-{MaxTccOffset}, got {offset}.");
        }

        /// <summary>
        /// Encodes a performance-mode write. The low nibble is the mode index, bit 4
        /// is the OEM engine's arm bit and bits 5-7 belong to the firmware, so the
        /// current register byte must be preserved: a bare mode value disarms the
        /// performance-button engine.
        /// </summary>
        /// <param name="previousRaw">The value last read from the mode-index register.</param>
        /// <param name="mode">Mode index: 0 quiet, 1 balanced, 2 performance, 3 battery saver.</param>
        public static byte EncodePerformanceMode(byte previousRaw, int mode)
        {
            int clamped = Math.Clamp(mode, 0, PerformanceModeCount - 1);
            return (byte)((previousRaw & ModeIndexHighBitsMask) | clamped | PerformanceModeArmBit);
        }

        /// <summary>Masks the mode index out of a mode-index register read.</summary>
        public static int DecodePerformanceMode(byte raw) => raw & ModeIndexMask;

        /// <summary>Rejects a performance-mode index outside the OEM set.</summary>
        public static void ValidatePerformanceMode(int mode)
        {
            if (mode < 0 || mode >= PerformanceModeCount)
                throw new ArgumentOutOfRangeException(nameof(mode),
                    $"Performance mode must be 0-{PerformanceModeCount - 1}, got {mode}.");
        }

        /// <summary>Rejects an OEM stock-preset index outside Office/Balance/Beast.</summary>
        public static void ValidateStockMode(int modeCase)
        {
            if (modeCase < StockModeOffice || modeCase > MaxStockMode)
                throw new ArgumentOutOfRangeException(nameof(modeCase),
                    $"Stock mode must be {StockModeOffice}-{MaxStockMode}, got {modeCase}.");
        }

        /// <summary>Clears the custom power-management gate (stock presets yield power values to the firmware table).</summary>
        public static byte EncodeStockPowerGate(byte previousRaw) =>
            (byte)(previousRaw & ~PowerManagementGateBit);

        /// <summary>Clears the custom-profile enable bit — the discriminator that makes the firmware apply its factory table.</summary>
        public static byte EncodeStockCustomProfile(byte previousRaw) =>
            (byte)(previousRaw & ~CustomProfileEnableBit);

        /// <summary>The literal fan-control byte for a stock mode.</summary>
        public static byte EncodeStockFanProfile(int modeCase) => modeCase switch
        {
            StockModeOffice => FanProfileOffice,
            StockModeBalance => FanProfileBalance,
            _ => FanProfileBeast,
        };

        /// <summary>
        /// Encodes the stock-preset mode-index write: high bits and low bit preserved,
        /// turbo (bit 4) forced for Beast only.
        /// </summary>
        public static byte EncodeStockModeIndex(byte previousRaw, int modeCase)
        {
            byte value = (byte)((previousRaw & ModeIndexHighBitsMask) | (previousRaw & ModeIndexLowBit));
            if (modeCase >= StockModeBeast)
                value |= FanTurboBit;
            return value;
        }

        /// <summary>
        /// Encodes the stock-preset trigger write: bit 4 preserved, apply-color set,
        /// plus bit 3 for Office only.
        /// </summary>
        public static byte EncodeStockTrigger(byte previousRaw, int modeCase) =>
            (byte)((previousRaw & TriggerPreservedBit) | TriggerApplyColorBit |
                   (modeCase == StockModeOffice ? TriggerOfficeBit : 0));

        /// <summary>The 0x045B OEM mode flag: 1 for Office, 0 for Balance and Beast.</summary>
        public static byte EncodeStockModeFlag(int modeCase) =>
            (byte)(modeCase == StockModeOffice ? 1 : 0);
    }
}
