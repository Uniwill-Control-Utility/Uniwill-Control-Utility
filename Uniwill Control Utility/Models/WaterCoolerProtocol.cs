namespace Uniwill_Control_Utility.Models
{
    /// <summary>
    /// RGB lighting animation modes for LCT water coolers.
    /// </summary>
    public enum RgbState : byte
    {
        Off = 0xFF,
        Static = 0x00,
        Breathe = 0x01,
        Colorful = 0x02,
        BreatheColor = 0x03
    }

    /// <summary>
    /// Flow meter status reported by the device.
    /// </summary>
    public enum FlowStatus
    {
        /// <summary>Pump is off — no flow expected.</summary>
        Off,
        /// <summary>No data yet — pump state unknown.</summary>
        Unknown,
        /// <summary>Pump just started — grace period before fault detection.</summary>
        Starting,
        /// <summary>Coolant flow detected — normal operation.</summary>
        Ok,
        /// <summary>Pump running but no flow — possible leak or blockage.</summary>
        Fault
    }

    /// <summary>
    /// Pump voltage settings for LCT water coolers.
    /// </summary>
    public enum PumpVoltage : byte
    {
        Off = 0xFF,
        V7 = 0x02,
        V8 = 0x03,
        V11 = 0x00
    }

    /// <summary>
    /// Fan speed duty cycle presets for LCT water coolers.
    /// </summary>
    public enum FanSpeed : byte
    {
        Off = 0xFF,
        Percent25 = 25,
        Percent50 = 50,
        Percent75 = 75,
        Percent90 = 90,
        Percent95 = 95,
        Percent100 = 100
    }

    /// <summary>
    /// RGB color presets for LCT water coolers.
    /// </summary>
    public enum RgbColor : byte
    {
        Red = 0x01,
        Green = 0x02,
        Blue = 0x03,
        White = 0x04
    }

    /// <summary>
    /// Supported LCT watercooler device model identifiers.
    /// </summary>
    public static class LctDeviceModel
    {
        public const string LCT21001 = "LCT21001";
        public const string LCT22002 = "LCT22002";
    }

    /// <summary>
    /// BLE GATT command byte identifiers used by the watercooler protocol.
    /// Each command is wrapped in a fixed 7-byte frame: FE [cmd] [enable] [paramA] [paramB] [paramC] EF
    /// No checksum — the device parses fixed byte offsets within the frame.
    /// </summary>
    public static class WaterCoolerCommand
    {
        public const byte Reset = 0x19;
        public const byte Status = 0x1A;
        public const byte Fan = 0x1B;
        public const byte Pump = 0x1C;
        public const byte Rgb = 0x1E;
        /// <summary>Device-initiated flow meter notification (inbound only).</summary>
        public const byte MeterPush = 0x31;
        /// <summary>Poll device for flow meter status.</summary>
        public const byte QueryMeter = 0x32;
        /// <summary>OEM line-off command.</summary>
        public const byte LineOff = 0x38;

        /// <summary>Frame start marker</summary>
        public const byte FrameStart = 0xFE;

        /// <summary>Frame end marker</summary>
        public const byte FrameEnd = 0xEF;

        /// <summary>Command enabled</summary>
        public const byte Enabled = 0x01;

        /// <summary>Command disabled (off/reset)</summary>
        public const byte Disabled = 0x00;
    }

    /// <summary>
    /// Nordic nRF52 UART service and characteristic UUIDs.
    /// All LCT water coolers use the standard Nordic UART profile.
    /// </summary>
    public static class NordicUart
    {
        public const string ServiceUuid = "6E400001-B5A3-F393-E0A9-E50E24DCCA9E";

        public const string CharacteristicTx = "6E400002-B5A3-F393-E0A9-E50E24DCCA9E";

        public const string CharacteristicRx = "6E400003-B5A3-F393-E0A9-E50E24DCCA9E";
    }
}
