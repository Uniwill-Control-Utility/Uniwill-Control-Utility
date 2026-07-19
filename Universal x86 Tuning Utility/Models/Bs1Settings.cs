namespace Universal_x86_Tuning_Utility.Models
{
    /// <summary>
    /// JSON-serializable settings for the Flydigi BS1 cooling pad.
    /// Persisted to %APPDATA%\UXTU\bs1_settings.json.
    ///
    /// Simplified compared to Bs2ProSettings: no RGB, no sub-gear levels,
    /// no device settings (Power-On Start, Smart Start/Stop, Gear Light).
    /// </summary>
    public class Bs1Settings
    {
        /// <summary>Whether to automatically reconnect on app startup.</summary>
        public bool AutoConnect { get; set; }

        /// <summary>Cached BLE address for auto-reconnect.</summary>
        public string LastDeviceAddress { get; set; } = string.Empty;

        /// <summary>Manual gear level (1-4: Quiet/Standard/Strong/Overclock). 0 = not set.</summary>
        public int ManualGear { get; set; }

        /// <summary>Manual RPM override (1300-3000). 0 = use defaults.</summary>
        public ushort ManualRpm { get; set; }

        /// <summary>Fan control mode (0=Manual, 1=Gear Presets, 2=Auto Curve).</summary>
        public int FanMode { get; set; }

        /// <summary>Selected curve profile name (Silent, Balanced, Performance, Custom).</summary>
        public string SelectedCurveProfile { get; set; } = "Balanced";

        /// <summary>Serialized custom curve profile JSON. Empty string = no custom curve.</summary>
        public string CustomCurveJson { get; set; } = string.Empty;

        /// <summary>Whether to turn off the fan when the system suspends.</summary>
        public bool SuspendFanOff { get; set; } = true;

        /// <summary>Temperature source for smart control (max, cpu, gpu).</summary>
        public string TempSource { get; set; } = "max";

        /// <summary>Whether speed avoidance zones are enabled.</summary>
        public bool AvoidanceEnabled { get; set; }

        /// <summary>Start of RPM avoidance range (within 1300-3000).</summary>
        public ushort AvoidanceStartRpm { get; set; } = 2000;

        /// <summary>End of RPM avoidance range (within 1300-3000).</summary>
        public ushort AvoidanceEndRpm { get; set; } = 2500;
    }
}
