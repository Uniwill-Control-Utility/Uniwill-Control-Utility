using System;
using System.Collections.Generic;
using System.Linq;

namespace Universal_x86_Tuning_Utility.Models
{
    /// <summary>
    /// Persisted settings for the Keyboard RGB page.
    /// Stored in %APPDATA%\UXTU\keyboard_settings.json
    /// </summary>
    public class KeyboardSettings
    {
        /// <summary>
        /// Whether the keyboard backlight is powered on.
        /// </summary>
        public bool PowerOn { get; set; } = true;

        /// <summary>
        /// Red channel 0-255 for static color mode.
        /// </summary>
        public byte ColorR { get; set; } = 0;

        /// <summary>
        /// Green channel 0-255 for static color mode.
        /// </summary>
        public byte ColorG { get; set; } = 255;

        /// <summary>
        /// Blue channel 0-255 for static color mode.
        /// </summary>
        public byte ColorB { get; set; } = 255;

        /// <summary>
        /// Brightness level 0-7.
        /// </summary>
        public int Brightness { get; set; } = 5;

        /// <summary>
        /// The active keyboard lighting effect. Default is Static (0x01).
        /// </summary>
        public KeyboardEffect EffectMode { get; set; } = KeyboardEffect.Static;

        /// <summary>
        /// Animation speed 0-11 (0x00-0x0B). Default 5.
        /// 0x00 = fastest, 0x0B = frozen (effect active but no movement).
        /// Higher value = slower animation (inverted scale).
        /// Stored as byte[4] in CMD_MODE_BRIGHTNESS HID reports.
        /// </summary>
        public byte Speed { get; set; } = 5;

        /// <summary>
        /// Animation direction for effects that support it (Wave, Music).
        /// 0 = off/left, 1 = right. Default 1.
        /// </summary>
        public byte Direction { get; set; } = 1;

        /// <summary>
        /// Multi-color palette for effects that support it (Breathing, Wave, Ripple, Music).
        /// Stored as comma-separated hex strings, e.g. "#FF0000,#00FF00,#0000FF".
        /// Max 7 colors.
        /// </summary>
        public string MultiColors { get; set; } = "";

        /// <summary>
        /// Whether the idle timer is enabled. When true, the keyboard backlight
        /// automatically turns off after a period of keyboard/mouse inactivity.
        /// </summary>
        public bool IdleTimerEnabled { get; set; } = false;

        /// <summary>
        /// Idle timer duration in minutes. Range 5-60. Default 10 (matches XMG CC).
        /// </summary>
        public int IdleTimerMinutes { get; set; } = 10;

        /// <summary>
        /// Per-key color override mode. When true, the keyboard uses per-key colors
        /// instead of effects. Mutually exclusive with effect modes.
        /// </summary>
        public bool PerKeyMode { get; set; } = false;

        /// <summary>
        /// Per-key colors for all 126 zones. Stored as "R,G,B" per zone, pipe-separated.
        /// e.g., "0,255,255|255,0,0|0,0,255|..." (126 entries)
        /// Null or empty means all black (default).
        /// </summary>
        public string? PerKeyColors { get; set; }

        /// <summary>
        /// Deserializes PerKeyColors string into a dictionary of zone index to RGB tuple.
        /// Returns all-black defaults if the string is null or empty.
        /// </summary>
        public Dictionary<int, (byte R, byte G, byte B)> GetPerKeyColors()
        {
            var result = new Dictionary<int, (byte, byte, byte)>();
            if (string.IsNullOrWhiteSpace(PerKeyColors))
            {
                for (int i = 0; i < 126; i++)
                    result[i] = (0, 0, 0);
                return result;
            }

            var entries = PerKeyColors.Split('|');
            for (int i = 0; i < 126; i++)
            {
                if (i < entries.Length)
                {
                    var parts = entries[i].Split(',');
                    if (parts.Length >= 3 && byte.TryParse(parts[0], out var r)
                        && byte.TryParse(parts[1], out var g) && byte.TryParse(parts[2], out var b))
                    {
                        result[i] = (r, g, b);
                    }
                    else
                    {
                        result[i] = (0, 0, 0);
                    }
                }
                else
                {
                    result[i] = (0, 0, 0);
                }
            }
            return result;
        }

        /// <summary>
        /// Serializes a dictionary of zone colors to the PerKeyColors string format.
        /// </summary>
        public void SetPerKeyColors(Dictionary<int, (byte R, byte G, byte B)> colors)
        {
            var entries = new List<string>();
            for (int i = 0; i < 126; i++)
            {
                (byte R, byte G, byte B) c = colors.TryGetValue(i, out var val) ? val : ((byte)0, (byte)0, (byte)0);
                entries.Add($"{c.R},{c.G},{c.B}");
            }
            PerKeyColors = string.Join("|", entries);
        }
    }

    /// <summary>
    /// Keyboard lighting effects discovered from the ITE HID controller protocol.
    /// Each value maps to byte[3] in CMD_MODE_BRIGHTNESS (0x08) reports.
    /// Format: 00 08 02 [effect] 01 [brightness] 08 00 00
    /// </summary>
    public enum KeyboardEffect : byte
    {
        /// <summary>Single solid color.</summary>
        Static = 0x01,

        /// <summary>Breathing between multiple colors.</summary>
        Breathing = 0x02,

        /// <summary>Wave traveling across the keyboard.</summary>
        Wave = 0x03,

        /// <summary>Random keys lighting up spontaneously (XCC: "Reactive").</summary>
        Reactive = 0x04,

        /// <summary>Static rainbow gradient across all keys.</summary>
        Rainbow = 0x05,

        /// <summary>Ripple animation across the keyboard.</summary>
        Ripple = 0x06,

        /// <summary>Ripple triggered by keypress (touch a key to send ripples).</summary>
        TouchRipple = 0x07,

        /// <summary>Marquee-style sequential lighting.</summary>
        Marquee = 0x09,

        /// <summary>Backlight off, random keys breathe in multiple colors (XCC: "Raindrop").</summary>
        Raindrop = 0x0A,

        /// <summary>Faster variant of Raindrop with different key transitions.</summary>
        RaindropFast = 0x0B,

        /// <summary>Rows light up from random positions, spreading across (XCC: "Aurora").</summary>
        Aurora = 0x0E,

        /// <summary>Aurora effect triggered by keypress instead of random.</summary>
        TouchAurora = 0x0F,

        /// <summary>Spark effect triggered by keypress.</summary>
        TouchSpark = 0x10,

        /// <summary>Spark effect running randomly without keypress (XCC: "Spark").</summary>
        Spark = 0x11,

        /// <summary>Only WASD and arrow keys lit, rest dark (XCC: "Gaming Mode").</summary>
        GamingMode = 0x15,

        /// <summary>Gaming mode WASD/arrows + static color on rest.</summary>
        GamingModeFull = 0x20,

        /// <summary>Music-reactive effect (XCC: "Music").</summary>
        Music = 0x33,
    }
}
