namespace Uniwill_Control_Utility.Models
{
    /// <summary>
    /// Holds the current device settings queried from the cooling pad.
    /// Populated by QueryDeviceSettingsAsync on connect or on demand.
    /// </summary>
    public class Bs2ProDeviceSettings
    {
        /// <summary>Whether the fan starts on power-on.</summary>
        public bool PowerOnStart { get; set; }

        /// <summary>Smart Start/Stop mode: 0=Off, 1=Immediate, 2=Delayed.</summary>
        public int SmartStartStopMode { get; set; }

        /// <summary>Whether the gear indicator light is enabled.</summary>
        public bool GearLightEnabled { get; set; } = true;

        /// <summary>
        /// Current gear RPM table from the device (4 entries: gear 0-3).
        /// Each entry is the RPM for that gear level.
        /// </summary>
        public ushort[]? GearRpmTable { get; set; }

        /// <summary>Current work mode byte (e.g. 0x04=manual, 0x05=realtime).</summary>
        public byte WorkMode { get; set; }

        /// <summary>Human-readable work mode name.</summary>
        public string WorkModeName => (WorkMode & 0x01) == 1 ? "Realtime" : "Manual";
    }
}
