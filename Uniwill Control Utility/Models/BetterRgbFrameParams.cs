using System.Windows.Media;
using Uniwill_Control_Utility.Models;

namespace Uniwill_Control_Utility.Models
{
    /// <summary>
    /// One immutable-by-convention render-parameter snapshot for the BetterRGB
    /// streamer — the C engine's g_* globals (better_rgb.c), one field per global.
    /// The service builds a fresh instance per SetParams; the render loop reads it
    /// once per frame, so slider changes never tear mid-frame.
    ///
    /// Ranges/defaults follow the reference's UI (docs\RGB_PROTOCOL__EN.md control
    /// table); the service clamps, the renderer re-guards exactly like the C
    /// effect threads (defensive Math.Clamp at the point of use).
    /// </summary>
    public sealed class BetterRgbFrameParams
    {
        public BetterRgbEffect Mode = BetterRgbEffect.Breathing;

        // Breathing: g_breathSpeed 1-40 def 12, g_breathSmooth 2-150 def 60,
        // g_breathColor, g_breathRandomMode.
        public int BreathSpeed = 12;
        public int BreathSmooth = 60;
        public bool BreathRandom;
        public Color BreathColor = Color.FromRgb(0x00, 0x78, 0xFF);

        // Wave: g_waveAngle 0-359 def 0, g_waveSpeed 1-20 def 4.
        public int WaveAngle;
        public int WaveSpeed = 4;

        // Sparkle: g_sparkleSpeed 1-10 def 5, g_sparkleDensity 3-20 def 8.
        public int SparkleSpeed = 5;
        public int SparkleDensity = 8;

        // Reactive: g_reactiveDuration 10/20/45 def 20, g_reactiveColor,
        // g_reactiveRandomMode (random per-channel bytes, NOT hsv — 2840).
        public int ReactiveDuration = 20;
        public bool ReactiveRandom = true;
        public Color ReactiveColor = Color.FromRgb(0xFF, 0xFF, 0xFF);

        // Wheel: g_wheelSpeed 1-20 def 4, g_wheelReverse.
        public int WheelSpeed = 4;
        public bool WheelReverse;

        // Lightning: g_lightningSpeed 1-10 def 5, g_lightningSmooth 1-20 def 10,
        // g_lightningWidth 1-4 def 2, g_lightningConcurrent 1-4 def 1.
        public int LightningSpeed = 5;
        public int LightningSmooth = 10;
        public int LightningWidth = 2;
        public int LightningConcurrent = 1;

        // Flame: g_flameSpeed 1-10 def 5, g_flameSmooth 1-20 def 8.
        public int FlameSpeed = 5;
        public int FlameSmooth = 8;

        // Rain: g_rainSpeed 1-10 def 5, g_rainDensityTarget 1-5 def 3,
        // g_rainColorCount 1-5 def 1, g_rainColors[5].
        public int RainSpeed = 5;
        public int RainDensity = 3;
        public int RainColorCount = 1;
        public Color[] RainColors = new Color[5];

        // Matrix: g_matrixStyle 0-3 def 0, g_matrixSpeed 1-10 def 5,
        // g_matrixDensityTarget 1-5 def 2, g_matrixColorCount 1-3 def 1,
        // g_matrixColors[3].
        public int MatrixStyle;
        public int MatrixSpeed = 5;
        public int MatrixDensity = 2;
        public int MatrixColorCount = 1;
        public Color[] MatrixColors = new Color[3];

        // Static: g_staticLayout 0-1, g_staticZoneCount 1-6/1-10 def 1,
        // g_staticColors[10].
        public int StaticLayout;
        public int StaticZones = 1;
        public Color[] StaticZoneColors = new Color[10];

        // Ripple: g_rippleSpeed 1-10 def 5, g_rippleWidth 1-4 def 2,
        // g_rippleColor def RGB(0,180,255), g_rippleRandomMode def ON.
        public int RippleSpeed = 5;
        public int RippleWidth = 2;
        public bool RippleRandom = true;
        public Color RippleColor = Color.FromRgb(0x00, 0xB4, 0xFF);

        // Rainbow: g_rainbowSpeed 1-20 def 6.
        public int RainbowSpeed = 6;

        // Quicksand: g_quicksandSpeed 1-10 def 5, g_quicksandScale 1-10 def 5,
        // g_quicksandColors[3] def red/green/blue.
        public int QuicksandSpeed = 5;
        public int QuicksandScale = 5;
        public Color[] QuicksandColors = new Color[3];

        // Current: g_currentSpeed 1-10 def 5, g_currentWidth 2-8 def 4,
        // g_currentColors[3].
        public int CurrentSpeed = 5;
        public int CurrentWidth = 4;
        public Color[] CurrentColors = new Color[3];

        // Touch Current: g_touchCurrentSpeed 1-10 def 5, g_touchCurrentWidth 1-5
        // def 2, g_touchCurrentColors[3].
        public int TouchCurrentSpeed = 5;
        public int TouchCurrentWidth = 2;
        public Color[] TouchCurrentColors = new Color[3];

        // Screen Ambient: g_ambientBlur 1-8 def 3, g_ambientResponse 1-10 def 5.
        public int AmbientBlur = 3;
        public int AmbientResponse = 5;
    }
}
