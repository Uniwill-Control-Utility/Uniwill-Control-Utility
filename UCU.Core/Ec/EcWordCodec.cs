using UCU.Core.Ec.Generated;

namespace UCU.Core.Ec
{
    /// <summary>
    /// Decodes the multi-register EC values.
    ///
    /// Byte order is not uniform across the register map: the main fan RPM is
    /// big-endian across its pair, the secondary fan's high byte sits at the higher
    /// address of the pair named after it, and the battery block is little-endian.
    /// These helpers exist so callers name the byte order they are reading instead of
    /// inlining a shift that reads the same either way, and so the derived battery
    /// math (health, Kelvin) has one tested implementation.
    /// </summary>
    public static class EcWordCodec
    {
        /// <summary>Combines a big-endian byte pair (high byte first) into a 16-bit value.</summary>
        public static int CombineBigEndian(byte highByte, byte lowByte) => (highByte << 8) | lowByte;

        /// <summary>Combines a little-endian byte pair (low byte first) into a 16-bit value.</summary>
        public static int CombineLittleEndian(byte lowByte, byte highByte) => (highByte << 8) | lowByte;

        /// <summary>
        /// Converts an EC battery temperature (Kelvin × 10) to whole degrees Celsius.
        /// Truncates toward zero, matching how the dashboard has always displayed it.
        /// </summary>
        public static int KelvinTenthsToCelsius(int kelvinTenths) => (int)(kelvinTenths / 10.0 - 273.15);

        /// <summary>
        /// Battery health as a percentage of design capacity. Returns 0 when the
        /// design capacity is unreadable (0), which is how the EC reports "no
        /// battery data" and is rendered as unknown by the UI.
        /// </summary>
        public static int ComputeHealthPercent(int fullChargeCapacityMah, int designCapacityMah) =>
            designCapacityMah > 0 ? (int)((long)fullChargeCapacityMah * 100 / designCapacityMah) : 0;

        /// <summary>True when bit 0 of the power-source register reports AC power.</summary>
        public static bool IsAcPowered(byte powerSourceRegister) => (powerSourceRegister & EcVault.BATTERY_AC_CONNECTED_BIT) != 0;

        /// <summary>
        /// Charge rate in milliamps. The EC reports a magnitude in a 16-bit register;
        /// the sign convention (positive charging, negative discharging) is applied by
        /// the caller from the power-source register, so this stays a plain read.
        /// </summary>
        public static int ChargeRateMa(int rawChargeRate) => rawChargeRate;
    }
}
