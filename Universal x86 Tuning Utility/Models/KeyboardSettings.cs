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
