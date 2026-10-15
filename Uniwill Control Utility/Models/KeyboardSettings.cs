using System;
using System.Collections.Generic;
using System.Linq;

namespace Uniwill_Control_Utility.Models
{
    /// <summary>
    /// Persisted settings for the Keyboard RGB page.
    /// Stored in %APPDATA%\UCU\keyboard_settings.json
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
        /// Which lighting engine drives the effects. Hardware (default) uses the ITE
        /// controller's built-in firmware MCU effects; BetterRGB renders the same effect
        /// list in software and streams per-key frames to the controller at TargetFps.
        /// Serialized as an int, so JSON written by older versions defaults to Hardware.
        /// </summary>
        public KeyboardEngine Engine { get; set; } = KeyboardEngine.Hardware;

        /// <summary>
        /// Streamer engine frame rate target (1-60). Only used when Engine is BetterRGB.
        /// OpenRevo's BetterRGB advertises 60 FPS; lower values trade smoothness for
        /// less HID traffic (each frame is 6 row announces + 6 output reports).
        /// </summary>
        public int TargetFps { get; set; } = 60;

        /// <summary>
        /// Streamer engine brightness 0-100 — OpenRevo/BetterRGB's g_brightness
        /// slider range (0-100 continuous), applied to the whole rendered frame.
        /// The firmware Brightness field (1-7 levels) stays for the hardware path.
        /// </summary>
        public int BrightnessPercent { get; set; } = 100;

        /// <summary>
        /// Streamer engine speed — the raw OpenRevo/BetterRGB C slider value for the
        /// current effect (breathing 1-40, wave/rainbow/wheel 1-20, others 1-10),
        /// higher = faster. The firmware Speed byte (1-10, 1=fastest) stays for the
        /// hardware path. Serialized as an int, so JSON from older versions defaults
        /// to 10.
        /// </summary>
        public int BetterRgbSpeed { get; set; } = 10;

        #region BetterRGB streamer parameters (reference: BetterRGB-ITE8291-Adaptive
        /// <summary>
        /// The BetterRGB card's effect — the reference's 16 software modes (better_rgb.c
        /// MODE_* 0-15). Independent of the firmware KeyboardEffect enum: the two engines
        /// no longer share controls. Newtonsoft serializes as int; old JSON defaults to
        /// Breathing.
        /// </summary>
        public BetterRgbEffect BetterRgbMode { get; set; } = BetterRgbEffect.Breathing;

        /// <summary>Streamer FPS target 15-60 (reference TARGET_FPS_DEFAULT 30).</summary>
        public int BetterRgbFps { get; set; } = 30;

        /// <summary>Breathing "Speed:" slider 1-40, default 12 (g_breathSpeed).</summary>
        public int BetterRgbBreathSpeed { get; set; } = 12;

        /// <summary>Breathing "Smoothness:" slider 2-150, default 60 (g_breathSmooth levels).</summary>
        public int BetterRgbBreathSmooth { get; set; } = 60;

        /// <summary>Breathing "Random multi-color" checkbox, default off (g_breathRandomMode).</summary>
        public bool BetterRgbBreathRandom { get; set; } = false;

        /// <summary>Breathing color, default RGB(0,120,255) (g_breathColor).</summary>
        public string BetterRgbBreathColor { get; set; } = "#0078FF";

        /// <summary>Wave "Angle:" slider 0-359 degrees, default 0 (g_waveAngle).</summary>
        public int BetterRgbWaveAngle { get; set; } = 0;

        /// <summary>Wave "Speed:" slider 1-20, default 4 (g_waveSpeed).</summary>
        public int BetterRgbWaveSpeed { get; set; } = 4;

        /// <summary>Sparkle "Speed:" slider 1-10, default 5 (g_sparkleSpeed).</summary>
        public int BetterRgbSparkleSpeed { get; set; } = 5;

        /// <summary>Sparkle "Density:" slider 3-20, default 8 (g_sparkleDensity).</summary>
        public int BetterRgbSparkleDensity { get; set; } = 8;

        /// <summary>Reactive "Random" checkbox, default ON (g_reactiveRandomMode).</summary>
        public bool BetterRgbReactiveRandom { get; set; } = true;

        /// <summary>Reactive response color, default white (g_reactiveColor).</summary>
        public string BetterRgbReactiveColor { get; set; } = "#FFFFFF";

        /// <summary>Reactive "Duration:" radio Short/Medium/Long -> 10/20/45 frames, default 20 (g_reactiveDuration).</summary>
        public int BetterRgbReactiveDuration { get; set; } = 20;

        /// <summary>Wheel "Speed:" slider 1-20, default 4 (g_wheelSpeed).</summary>
        public int BetterRgbWheelSpeed { get; set; } = 4;

        /// <summary>Wheel "Reverse rotation" checkbox, default off (g_wheelReverse).</summary>
        public bool BetterRgbWheelReverse { get; set; } = false;

        /// <summary>Lightning "Speed:" slider 1-10, default 5 (g_lightningSpeed).</summary>
        public int BetterRgbLightningSpeed { get; set; } = 5;

        /// <summary>Lightning "Smoothness:" slider 1-20, default 10 (g_lightningSmooth).</summary>
        public int BetterRgbLightningSmooth { get; set; } = 10;

        /// <summary>Lightning "Lightning width:" slider 1-4, default 2 (g_lightningWidth).</summary>
        public int BetterRgbLightningWidth { get; set; } = 2;

        /// <summary>Lightning "Concurrent count:" slider 1-4, default 1 (g_lightningConcurrent).</summary>
        public int BetterRgbLightningConcurrent { get; set; } = 1;

        /// <summary>Flame "Flicker speed:" slider 1-10, default 5 (g_flameSpeed).</summary>
        public int BetterRgbFlameSpeed { get; set; } = 5;

        /// <summary>Flame "Smoothness:" slider 1-20, default 8 (g_flameSmooth).</summary>
        public int BetterRgbFlameSmooth { get; set; } = 8;

        /// <summary>Raindrop "Falling speed:" slider 1-10, default 5 (g_rainSpeed).</summary>
        public int BetterRgbRainSpeed { get; set; } = 5;

        /// <summary>Raindrop density buttons 1-5, default 3 (g_rainDensityTarget).</summary>
        public int BetterRgbRainDensity { get; set; } = 3;

        /// <summary>Raindrop "Number of colors:" buttons 1-5, default 1 (g_rainColorCount).</summary>
        public int BetterRgbRainColorCount { get; set; } = 1;

        /// <summary>Raindrop 5 color buttons (g_rainColors defaults).</summary>
        public string BetterRgbRainColors { get; set; } = "#508CFF,#7850FF,#50DCFF,#FFFFFF,#50FFB4";

        /// <summary>Matrix "Speed:" slider 1-10, default 5 (g_matrixSpeed).</summary>
        public int BetterRgbMatrixSpeed { get; set; } = 5;

        /// <summary>Matrix "Style:" combo 0=Diagonal bands / 1=Number rain / 2=Laser scan / 3=Ripple, default 0 (g_matrixStyle).</summary>
        public int BetterRgbMatrixStyle { get; set; } = 0;

        /// <summary>Matrix density buttons 1-5, default 2 (g_matrixDensityTarget).</summary>
        public int BetterRgbMatrixDensity { get; set; } = 2;

        /// <summary>Matrix "Number of colors:" buttons 1-3, default 1 (g_matrixColorCount).</summary>
        public int BetterRgbMatrixColorCount { get; set; } = 1;

        /// <summary>Matrix 3 color buttons (g_matrixColors defaults).</summary>
        public string BetterRgbMatrixColors { get; set; } = "#00FF3C,#00B4FF,#FF0078";

        /// <summary>Static "Layout:" 0=Horizontal zones / 1=Vertical zones (g_staticLayout).</summary>
        public int BetterRgbStaticLayout { get; set; } = 0;

        /// <summary>Static "Number of zones:" slider 1-6 (horizontal) / 1-10 (vertical), default 1 (g_staticZoneCount).</summary>
        public int BetterRgbStaticZones { get; set; } = 1;

        /// <summary>Static 10 zone colors (g_staticColors defaults).</summary>
        public string BetterRgbStaticZoneColors { get; set; } =
            "#FFFFFF,#FF0000,#00FF00,#0000FF,#FFFF00,#FF00FF,#00FFFF,#FF8C00,#8C00FF,#008C8C";

        /// <summary>Ripple "Spread speed:" slider 1-10, default 5 (g_rippleSpeed).</summary>
        public int BetterRgbRippleSpeed { get; set; } = 5;

        /// <summary>Ripple "Wave width:" slider 1-4, default 2 (g_rippleWidth).</summary>
        public int BetterRgbRippleWidth { get; set; } = 2;

        /// <summary>Ripple "Random" checkbox, default ON (g_rippleRandomMode).</summary>
        public bool BetterRgbRippleRandom { get; set; } = true;

        /// <summary>Ripple color, default RGB(0,180,255) (g_rippleColor).</summary>
        public string BetterRgbRippleColor { get; set; } = "#00B4FF";

        /// <summary>Rainbow "Color-change speed:" slider 1-20, default 6 (g_rainbowSpeed).</summary>
        public int BetterRgbRainbowSpeed { get; set; } = 6;

        /// <summary>Quicksand "Falling speed:" slider 1-10, default 5 (g_quicksandSpeed).</summary>
        public int BetterRgbQuicksandSpeed { get; set; } = 5;

        /// <summary>Quicksand "Sand band width:" slider 1-10, default 5 (g_quicksandScale).</summary>
        public int BetterRgbQuicksandScale { get; set; } = 5;

        /// <summary>Quicksand 3 gradient colors, default red/green/blue (g_quicksandColors).</summary>
        public string BetterRgbQuicksandColors { get; set; } = "#FF0000,#00FF00,#0000FF";

        /// <summary>Current "Flow speed:" slider 1-10, default 5 (g_currentSpeed).</summary>
        public int BetterRgbCurrentSpeed { get; set; } = 5;

        /// <summary>Current "Current length:" slider 2-8, default 4 (g_currentWidth).</summary>
        public int BetterRgbCurrentWidth { get; set; } = 4;

        /// <summary>Current 3 gradient colors, default red/green/blue (g_currentColors).</summary>
        public string BetterRgbCurrentColors { get; set; } = "#FF0000,#00FF00,#0000FF";

        #endregion

        /// <summary>
        /// Animation speed 0-11 (0x00-0x0B). Default 5.
        /// 0x00 = fastest, 0x0B = frozen (effect active but no movement).
        /// Higher value = slower animation (inverted scale).
        /// Stored as byte[4] in CMD_MODE_BRIGHTNESS HID reports.
        /// </summary>
        public byte Speed { get; set; } = 5;

        /// <summary>
        /// Animation direction for effects that support it (verified on Wave; other effects TBD).
        /// Stored as byte[7] in CMD_MODE_BRIGHTNESS HID reports.
        /// </summary>
        public KeyboardDirection Direction { get; set; } = KeyboardDirection.LeftRight;

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
        /// Auto-power policy: when true, the keyboard backlight is turned off while
        /// the laptop is unplugged (on battery) and restored to the user's saved
        /// PowerOn intent when plugged in. The PowerOn field stays the user's intent.
        /// </summary>
        public bool AutoPowerOffOnBattery { get; set; } = false;

        /// <summary>
        /// Whether touch-reactive mode is enabled for effects that support it
        /// (Random, Ripple, Aurora, Spark). When true, the effect triggers on
        /// keypress instead of running automatically. Stored as byte[7]=0x01
        /// in CMD_MODE_BRIGHTNESS HID reports.
        /// </summary>
        public bool TouchReactive { get; set; } = false;

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
        /// The currently selected color in the per-key color picker.
        /// Stored as "R,G,B". Default is white (255,255,255).
        /// </summary>
        public string PerKeyPickerColor { get; set; } = "255,255,255";

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

        /// <summary>Random keys lighting up spontaneously (non-touch, XCC: "Reactive"). Use TouchReactive setting for keypress-reactive variant.</summary>
        Random = 0x04,

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

        // --- Software-only effects (BetterRGB streamer engine; the ITE firmware
        // MCU has no equivalent byte). Offered in the ComboBox only while the
        // engine is on; persisted as Hardware if saved while per-key/hardware. ---

        /// <summary>Digital-matrix falling code columns (BetterRGB "Digital Matrix").</summary>
        Matrix = 0x20,

        /// <summary>Lightning strikes sweeping down the keyboard (BetterRGB "Lightning").</summary>
        Lightning = 0x21,

        /// <summary>Rolling flames with per-key heat noise (BetterRGB "Rolling Flames").</summary>
        Flame = 0x22,

        /// <summary>Color wheel rotating around the keyboard center (BetterRGB "Wheel").</summary>
        Wheel = 0x23,
    }

    /// <summary>
    /// Lighting engines for the keyboard. Hardware sends the effect byte to the ITE
    /// controller's firmware MCU (the original path); BetterRGB is the software
    /// streamer engine (port of OpenRevo's BetterRGB): frames are rendered per key
    /// in software and streamed through UserMode (0x33) at <c>TargetFps</c>.
    /// </summary>
    public enum KeyboardEngine : byte
    {
        /// <summary>ITE firmware MCU effects via CMD_MODE_BRIGHTNESS reports (default).</summary>
        Hardware = 0,

        /// <summary>Software-rendered frames streamed to the controller at up to 60 FPS.</summary>
        BetterRGB = 1,
    }

    /// <summary>
    /// The BetterRGB streamer card's effect list — the reference's 16 software modes
    /// in MODE_* order (better_rgb.c:99-115). Completely separate from the firmware
    /// KeyboardEffect enum: the two engines no longer share controls or effect lists.
    /// </summary>
    public enum BetterRgbEffect : byte
    {
        /// <summary>Sine breathing with smooth-level quantization (MODE_BREATH).</summary>
        Breathing = 0,

        /// <summary>HSV wave traveling along an adjustable angle (MODE_WAVE).</summary>
        Wave = 1,

        /// <summary>Random keys sparkle with sin^2 envelopes (MODE_SPARKLE).</summary>
        Sparkle = 2,

        /// <summary>Keys light while held, fade on release (MODE_REACTIVE).</summary>
        Reactive = 3,

        /// <summary>HSV color wheel around the keybed centroid (MODE_WHEEL).</summary>
        Wheel = 4,

        /// <summary>Vertical lightning strikes with phosphor decay (MODE_LIGHTNING).</summary>
        Lightning = 5,

        /// <summary>Rolling flames with per-key heat EMA (MODE_FLAME).</summary>
        Flame = 6,

        /// <summary>Falling gaussian rain drops (MODE_RAIN).</summary>
        Raindrop = 7,

        /// <summary>Digital matrix: 4 styles (bands/rain/lasers/rings) (MODE_MATRIX).</summary>
        Matrix = 8,

        /// <summary>Static color zones (horizontal rows / vertical ranks) (MODE_STATIC).</summary>
        Static = 9,

        /// <summary>Keypress ripple rings (MODE_RIPPLE).</summary>
        Ripple = 10,

        /// <summary>Full-color gradient rotating at 4+2*speed deg/s (MODE_RAINBOW).</summary>
        Rainbow = 11,

        /// <summary>Flowing quicksand curtain, 3-color palette (MODE_QUICKSAND).</summary>
        Quicksand = 12,

        /// <summary>Per-row comet currents with exponential tails (MODE_CURRENT).</summary>
        Current = 13,
    }

    /// <summary>
    /// Animation direction for effects that support directional control.
    /// Maps to byte[7] in CMD_MODE_BRIGHTNESS HID reports.
    /// Verified on Wave (0x03); behavior on other effects is yet to be confirmed.
    /// </summary>
    public enum KeyboardDirection : byte
    {
        /// <summary>Left → Right (default).</summary>
        LeftRight = 0x00,

        /// <summary>Right → Left.</summary>
        RightLeft = 0x02,

        /// <summary>Down → Up.</summary>
        DownUp = 0x03,

        /// <summary>Up → Down.</summary>
        UpDown = 0x04,

        /// <summary>Diagonal: bottom-right → top-left.</summary>
        DiagonalBottomRightToTopLeft = 0x05,

        /// <summary>Diagonal: bottom-left → top-right.</summary>
        DiagonalBottomLeftToTopRight = 0x06,
    }
}
