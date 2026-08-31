using System.Collections.Generic;

namespace Uniwill_Control_Utility.Models
{
    /// <summary>
    /// JSON-serializable settings for the Flydigi BS2 Pro cooling pad.
    /// Persisted to %APPDATA%\UCU\bs2pro_settings.json.
    /// </summary>
    public class Bs2ProSettings
    {
        /// <summary>Whether to automatically reconnect on app startup.</summary>
        public bool AutoConnect { get; set; }

        /// <summary>Cached HID device path for auto-reconnect.</summary>
        public string LastDevicePath { get; set; } = string.Empty;

        /// <summary>Manual gear level (1-4: Quiet/Standard/Strong/Overclock). 0 = not set.</summary>
        public int ManualGear { get; set; }

        /// <summary>Manual gear sub-level (0=Low, 1=Medium, 2=High).</summary>
        public int ManualGearSubLevel { get; set; }

        /// <summary>Power-On Start toggle.</summary>
        public bool PowerOnStart { get; set; }

        /// <summary>Smart Start/Stop mode (0=Off, 1=Immediate, 2=Delayed).</summary>
        public int SmartStartStopMode { get; set; }

        /// <summary>Gear indicator light toggle.</summary>
        public bool GearLightEnabled { get; set; } = true;

        /// <summary>RGB mode name (Off, SmartTemp, Static, Rotation, Flowing, Breathing).</summary>
        public string RgbMode { get; set; } = "Off";

        /// <summary>RGB red channel (0-255).</summary>
        public byte R { get; set; } = 255;

        /// <summary>RGB green channel (0-255).</summary>
        public byte G { get; set; } = 0;

        /// <summary>RGB blue channel (0-255).</summary>
        public byte B { get; set; } = 0;

        /// <summary>RGB animation speed (Fast, Medium, Slow).</summary>
        public string RgbSpeed { get; set; } = "Medium";

        /// <summary>RGB brightness (0-100).</summary>
        public byte Brightness { get; set; } = 100;

        /// <summary>Rotation mode speed (Fast, Medium, Slow).</summary>
        public string RotationSpeed { get; set; } = "Medium";

        /// <summary>Rotation mode brightness (0-100).</summary>
        public byte RotationBrightness { get; set; } = 100;

        /// <summary>Serialized rotation colors as comma-separated hex strings (e.g., "#FF0000,#00FF00").</summary>
        public string RotationColors { get; set; } = string.Empty;

        /// <summary>Manual RPM override (1300-4000). 0 = use defaults.</summary>
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

        /// <summary>Start of RPM avoidance range (within 1300-4000).</summary>
        public ushort AvoidanceStartRpm { get; set; } = 2000;

        /// <summary>End of RPM avoidance range (within 1300-4000).</summary>
        public ushort AvoidanceEndRpm { get; set; } = 2500;

        /// <summary>Whether time-based curve scheduling is enabled.</summary>
        public bool TimeScheduleEnabled { get; set; }

        /// <summary>List of time windows mapped to curve profiles.</summary>
        public List<TimeCurveEntry> TimeScheduleEntries { get; set; } = new();

        /// <summary>Whether learning-based offset adjustment is enabled.</summary>
        public bool LearningEnabled { get; set; }

        /// <summary>Learning bias mode: balanced, cooling, or quiet.</summary>
        public string LearningBias { get; set; } = "balanced";
    }

    /// <summary>
    /// A single time window entry in the curve schedule.
    /// When the current time falls within StartTime–EndTime, the assigned curve profile becomes active.
    /// </summary>
    public class TimeCurveEntry
    {
        /// <summary>Start time of this window (e.g. "06:00" for 6 AM).</summary>
        public string StartTime { get; set; } = string.Empty;

        /// <summary>End time of this window (e.g. "22:00" for 10 PM).</summary>
        public string EndTime { get; set; } = string.Empty;

        /// <summary>Name of the curve profile to activate (Silent, Balanced, Performance).</summary>
        public string ProfileName { get; set; } = "Balanced";
    }
}
