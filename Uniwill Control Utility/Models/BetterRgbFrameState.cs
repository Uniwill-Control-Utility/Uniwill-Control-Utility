using System;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Windows.Media;
using Uniwill_Control_Utility.Models;

namespace Uniwill_Control_Utility.Models
{
    /// <summary>
    /// Per-stream animation state for the BetterRGB engine — the C effect threads'
    /// locals (better_rgb.c effect_* thread bodies). The streamer owns one instance
    /// and keeps it across in-place reconnects ("resuming animation without timeline
    /// reset"), unlike C which restarts the thread and re-inits.
    ///
    /// Thread-start inits mirror the C thread prologues exactly (lightning headPos
    /// 100/boltX 10/countdown 10+rand%30, flame heat 100+rand%60, rain/matrix spawn
    /// seeds) so the first frames look like the reference's.
    ///
    /// Randomness: C seeds srand(time(NULL)) once per process and calls rand(); UCU
    /// uses a splitmix64 stream seeded from the clock — same role (spawn positions/
    /// colors only), deterministic per session and testable.
    /// </summary>
    public sealed class BetterRgbFrameState
    {
        // ---- PRNG stream (splitmix64; role of C rand()) -------------------------
        private ulong _seed;

        public int RandInt(int n)
        {
            if (n <= 0)
                return 0;
            _seed += 0x9E3779B97F4A7C15UL;
            var z = _seed;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            z ^= z >> 31;
            return (int)(z % (ulong)n);
        }

        // ---- Breathing (effect_breathing locals) --------------------------------
        public double BreathT;
        public double BreathPrevRaw = 0.5;
        public Color BreathCycleColor;

        // ---- Wave / Wheel (t += speed per frame) --------------------------------
        public double WaveT;
        public double WheelT;

        // ---- Rainbow (wall-clock hue accumulation) ------------------------------
        public double RainbowHue;
        public double RainbowPrevElapsedSec;

        // ---- Sparkle (age/duration/color per key) -------------------------------
        public const int KeyCount = 102; // KEYMAP_COUNT (better_rgb.c:222-254)
        public int[] SparkleAge = new int[KeyCount];
        public int[] SparkleDuration = new int[KeyCount];
        public byte[] SparkleR = new byte[KeyCount];
        public byte[] SparkleG = new byte[KeyCount];
        public byte[] SparkleB = new byte[KeyCount];

        // ---- Keypress hook (GetAsyncKeyState over the tracked VK list) ----------
        // Shared wasDown + fresh-press event buffer; Reactive/Ripple
        // consume it each frame, exactly like their per-thread polling loops.
        public int[] WasDown = new int[KeyCount];
        public int EventCount;
        public double[] EventX = new double[128];
        public double[] EventY = new double[128];
        public int[] EventIdx = new int[128];

        // ---- Reactive (wasDown + fadeLife + spawn color) ------------------------
        public int[] ReactiveFadeLife = new int[KeyCount];
        public byte[] ReactiveR = new byte[KeyCount];
        public byte[] ReactiveG = new byte[KeyCount];
        public byte[] ReactiveB = new byte[KeyCount];

        // ---- Lightning (MAX_STRIKES 4 bolts + phosphor EMA) ---------------------
        public const int MaxStrikes = 4;
        public double[] LightningHeadPos = new double[MaxStrikes];
        public double[] LightningBoltX = new double[MaxStrikes];
        public double[] LightningBendSeed = new double[MaxStrikes];
        public int[] LightningCountdown = new int[MaxStrikes];
        public double[] LightningPrevious = new double[KeyCount];
        public double[] LightningBright = new double[KeyCount];

        // ---- Flame (heat EMA + quantized update interval) -----------------------
        public double[] FlameHeat = new double[KeyCount];
        public int FlameCounter;

        // ---- Rain (MAX_RAIN_DROPS 20 falling splats + EMA) ----------------------
        public const int MaxRainDrops = 20;
        public double[] RainDropX = new double[MaxRainDrops];
        public double[] RainDropRow = new double[MaxRainDrops];
        public int[] RainDropColorIdx = new int[MaxRainDrops];
        public double[] RainPrevR = new double[KeyCount];
        public double[] RainPrevG = new double[KeyCount];
        public double[] RainPrevB = new double[KeyCount];
        public double[] RainTargetR = new double[KeyCount];
        public double[] RainTargetG = new double[KeyCount];
        public double[] RainTargetB = new double[KeyCount];

        // ---- Matrix (streaks 5 / columns 24 / lasers 3 / rings 3) ---------------
        public const int MatrixStreaks = 5;
        public const int MatrixColumns = 24;
        public const int MaxLasers = 3;
        public const int MaxRipples = 3;
        public double[] MatrixAngle = new double[MatrixStreaks];
        public double[] MatrixPos = new double[MatrixStreaks];
        public double[] MatrixSpd = new double[MatrixStreaks];
        public int[] MatrixStreakColorIdx = new int[MatrixStreaks];
        public int MatrixActiveStreaks = 2;
        public bool MatrixInitDone;
        public int MatrixRerollCounter;
        public double[] MatrixColPos = new double[MatrixColumns];
        public double[] MatrixColSpeed = new double[MatrixColumns];
        public int[] MatrixColColorIdx = new int[MatrixColumns];
        public double[] MatrixLaserAngle = new double[MaxLasers];
        public double[] MatrixLaserPos = new double[MaxLasers];
        public double[] MatrixLaserSpd = new double[MaxLasers];
        public int[] MatrixLaserColorIdx = new int[MaxLasers];
        public double[] MatrixRippleX = new double[MaxRipples];
        public double[] MatrixRippleY = new double[MaxRipples];
        public double[] MatrixRippleR = new double[MaxRipples];
        public int[] MatrixRippleColorIdx = new int[MaxRipples];

        // ---- Ripple (MAX_TOUCH_RIPPLES 20 keypress-spawned rings) ---------------
        public const int MaxTouchRipples = 20;
        public bool[] RippleActive = new bool[MaxTouchRipples];
        public double[] RippleX = new double[MaxTouchRipples];
        public double[] RippleY = new double[MaxTouchRipples];
        public double[] RippleStartedAtMs = new double[MaxTouchRipples];
        public byte[] RippleR = new byte[MaxTouchRipples];
        public byte[] RippleG = new byte[MaxTouchRipples];
        public byte[] RippleB = new byte[MaxTouchRipples];
        public int RippleNextSlot;

        // ---- Current (per-row comet EMA) ----------------------------------------
        public double[] CurrentPrevR = new double[KeyCount];
        public double[] CurrentPrevG = new double[KeyCount];
        public double[] CurrentPrevB = new double[KeyCount];

        // ---- Thread-prologue inits (C thread bodies) ----------------------------
        public BetterRgbFrameState()
        {
            _seed = (ulong)DateTime.Now.Ticks * 0x2545F4914F6CDD1DUL | 0x9E3779B97F4A7C15UL;

            BreathCycleColor = RandomHsvColor();

            for (int i = 0; i < MaxStrikes; i++)
            {
                LightningHeadPos[i] = 100.0;
                LightningBoltX[i] = 10.0;
                LightningBendSeed[i] = RandInt(628) / 100.0;
                LightningCountdown[i] = 10 + RandInt(30);
            }

            for (int i = 0; i < KeyCount; i++)
                FlameHeat[i] = 100.0 + RandInt(60);

            for (int i = 0; i < MaxRainDrops; i++)
            {
                RainDropX[i] = RandInt(20);
                RainDropRow[i] = -(double)RandInt(8);
                RainDropColorIdx[i] = RandInt(5);
            }

            for (int i = 0; i < MatrixStreaks; i++)
            {
                MatrixAngle[i] = RandInt(360) * Math.PI / 180.0;
                MatrixPos[i] = -30.0;
                MatrixSpd[i] = 0.2 + RandInt(100) / 100.0;
                MatrixStreakColorIdx[i] = RandInt(3);
            }
            for (int i = 0; i < MatrixColumns; i++)
            {
                MatrixColPos[i] = -(double)RandInt(8);
                MatrixColSpeed[i] = 0.05 + RandInt(100) / 300.0;
                MatrixColColorIdx[i] = RandInt(3);
            }
            for (int i = 0; i < MaxLasers; i++)
            {
                MatrixLaserAngle[i] = RandInt(4) * (Math.PI / 2.0);
                MatrixLaserPos[i] = -30.0 - i * 8.0;
                MatrixLaserSpd[i] = 0.6 + RandInt(100) / 100.0;
                MatrixLaserColorIdx[i] = RandInt(3);
            }
            for (int i = 0; i < MaxRipples; i++)
            {
                MatrixRippleX[i] = RandInt(20);
                MatrixRippleY[i] = RandInt(7);
                MatrixRippleR[i] = i * 8.0;
                MatrixRippleColorIdx[i] = RandInt(3);
            }
        }

        // hsv_to_rgb at a random hue (C: hsv_to_rgb(rand()%360) at thread start).
        private Color RandomHsvColor()
        {
            double h = RandInt(360);
            if (h < 0) h += 360.0;
            double c = 1.0, x = c * (1.0 - Math.Abs(h / 60.0 % 2.0 - 1.0));
            double rr, gg, bb;
            if (h < 60) { rr = c; gg = x; bb = 0; }
            else if (h < 120) { rr = x; gg = c; bb = 0; }
            else if (h < 180) { rr = 0; gg = c; bb = x; }
            else if (h < 240) { rr = 0; gg = x; bb = c; }
            else if (h < 300) { rr = x; gg = 0; bb = c; }
            else { rr = c; gg = 0; bb = x; }
            return Color.FromRgb((byte)(rr * 255), (byte)(gg * 255), (byte)(bb * 255));
        }
    }
}
