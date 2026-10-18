using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Media;
using Uniwill_Control_Utility.Models;

namespace Uniwill_Control_Utility.Models
{
    /// <summary>
    /// Software frame renderer for the BetterRGB streamer engine — a 1:1 port of the
    /// open-source BetterRGB effect engine (BetterRGB-ITE8291-Adaptive, source\better_rgb.c).
    ///
    /// Frame format: one 512-byte interleaved frame (the reference's BUF_SIZE), each
    /// key an RGB triple at its KEYMAP offset (better_rgb.c:227-253) with AUX mirror
    /// slots for the wide Shift/Enter keys (set_key, 1755-1766). The streamer sends it
    /// as 1 feature report + 8 x 65-byte output writes on the UsagePage 0xFF03 / MI_01
    /// interface — the reference's send_frame (1708-1745).
    ///
    /// Geometry: KEYMAP row/col are HID slots, not physical positions; get_key_x/get_key_y
    /// (1918-1962) give physical key-centre coordinates in one-key pitches (numpad
    /// columns, wide-key centres, row stagger). Effects sample those coordinates.
    ///
    /// Statefulness: the reference keeps per-effect state in its effect thread (phosphor
    /// EMA, spawn lists, heat arrays). BetterRgbFrameState carries the same state; the
    /// streamer keeps it across reconnects ("resuming animation without timeline reset").
    ///
    /// Randomness: the reference seeds C rand() once per process (srand(time)). UCU uses a
    /// splitmix64 stream in the state — deterministic per session, continues across
    /// reconnects, and testable. Same role as rand(): spawn positions/colors only.
    ///
    /// Brightness: NOT applied inside the effect math — the reference multiplies the
    /// finished 512-byte frame by g_brightness inside send_frame (1711-1715); ApplyBrightness
    /// here mirrors that, and the streamer service calls it after Render.
    /// </summary>
    public sealed class BetterRgbFrameRenderer
    {
        public const int FrameSize = 512;

        // ---- KEYMAP (better_rgb.c:227-253) --------------------------------------
        // {name, offset, row, col}. row 0 = top (Esc), row 6 = bottom arrow cluster.
        // Slot names follow the NEO A25 variant (matches KeyboardZone.cs): 333=PrtSc,
        // 357=Ins, 289=CtrlR — the reference's FnLockCam/CopilotKey names are not
        // present on this hardware.
        // Built via List (tuple types cannot appear in new-expressions).
        private static readonly (string Name, int Offset, int Row, int Col)[] KeyMap = BuildKeyMap();

        public static readonly int KeyCount = KeyMap.Length; // 102 = KEYMAP_COUNT

        // AUX mirror slots for wide keys (set_key, better_rgb.c:1755-1766).
        private const int OffsetShiftLeft = 5;
        private const int OffsetShiftLeftAux = 29;
        private const int OffsetEnter = 321;
        private const int OffsetEnterAux = 345;

        private static (string Name, int Offset, int Row, int Col)[] BuildKeyMap()
        {
            var list = new List<(string, int, int, int)>();
            list.Add(("Esc",21,0,0)); list.Add(("F1",45,0,1)); list.Add(("F2",69,0,2));
            list.Add(("F3",93,0,3)); list.Add(("F4",117,0,4)); list.Add(("F5",141,0,5));
            list.Add(("F6",165,0,6)); list.Add(("F7",189,0,7)); list.Add(("F8",213,0,8));
            list.Add(("F9",237,0,9)); list.Add(("F10",261,0,10)); list.Add(("F11",285,0,11));
            list.Add(("F12",309,0,12)); list.Add(("PrtSc",333,0,13)); list.Add(("Ins",357,0,14));
            list.Add(("Del",381,0,15)); list.Add(("Home",405,0,16)); list.Add(("PgUp",429,0,17));
            list.Add(("PgDn",453,0,18)); list.Add(("End",477,0,19));
            list.Add(("Grave",17,1,0)); list.Add(("1",41,1,1)); list.Add(("2",65,1,2));
            list.Add(("3",89,1,3)); list.Add(("4",113,1,4)); list.Add(("5",137,1,5));
            list.Add(("6",161,1,6)); list.Add(("7",185,1,7)); list.Add(("8",209,1,8));
            list.Add(("9",233,1,9)); list.Add(("0",257,1,10)); list.Add(("Minus",281,1,11));
            list.Add(("Equals",305,1,12)); list.Add(("Backspace",353,1,13)); list.Add(("NumLock",377,1,14));
            list.Add(("NumpadDiv",401,1,15)); list.Add(("NumpadMul",425,1,16)); list.Add(("NumpadMinus",449,1,17));
            list.Add(("Tab",13,2,0)); list.Add(("Q",61,2,1)); list.Add(("W",85,2,2));
            list.Add(("E",109,2,3)); list.Add(("R",133,2,4)); list.Add(("T",157,2,5));
            list.Add(("Y",181,2,6)); list.Add(("U",205,2,7)); list.Add(("I",229,2,8));
            list.Add(("O",253,2,9)); list.Add(("P",277,2,10)); list.Add(("BracketL",301,2,11));
            list.Add(("BracketR",325,2,12)); list.Add(("Backslash",349,2,13)); list.Add(("Numpad7",373,2,14));
            list.Add(("Numpad8",397,2,15)); list.Add(("Numpad9",421,2,16)); list.Add(("NumpadPlus",445,2,17));
            list.Add(("CapsLock",9,3,0)); list.Add(("A",57,3,1)); list.Add(("S",81,3,2));
            list.Add(("D",105,3,3)); list.Add(("F",129,3,4)); list.Add(("G",153,3,5));
            list.Add(("H",177,3,6)); list.Add(("J",201,3,7)); list.Add(("K",225,3,8));
            list.Add(("L",249,3,9)); list.Add(("Semicolon",273,3,10)); list.Add(("Quote",297,3,11));
            list.Add(("Enter",321,3,12)); list.Add(("Numpad4",369,3,13)); list.Add(("Numpad5",393,3,14));
            list.Add(("Numpad6",417,3,15));
            list.Add(("ShiftL",5,4,0)); list.Add(("ISO_Backslash",53,4,1)); list.Add(("Z",77,4,2));
            list.Add(("X",101,4,3)); list.Add(("C",125,4,4)); list.Add(("V",149,4,5));
            list.Add(("B",173,4,6)); list.Add(("N",197,4,7)); list.Add(("M",221,4,8));
            list.Add(("Comma",245,4,9)); list.Add(("Period",269,4,10)); list.Add(("Slash",293,4,11));
            list.Add(("ShiftR",341,4,12)); list.Add(("Numpad1",365,4,13)); list.Add(("Numpad2",389,4,14));
            list.Add(("Numpad3",413,4,15)); list.Add(("NumpadEnter",437,4,16));
            list.Add(("CtrlL",1,5,0)); list.Add(("Fn",49,5,1)); list.Add(("Windows",73,5,2));
            list.Add(("AltL",97,5,3)); list.Add(("Space",169,5,4)); list.Add(("AltGr",241,5,5));
            list.Add(("CtrlR",289,5,6)); list.Add(("ArrowUp",337,5,7)); list.Add(("Numpad0",385,5,8));
            list.Add(("NumpadDecimal",409,5,9));
            list.Add(("ArrowLeft",313,6,6)); list.Add(("ArrowDown",433,6,7)); list.Add(("ArrowRight",361,6,8));
            return list.ToArray();
        }

        // ---- Light bar sync sampling --------------------------------------------
        // The front strip is a single HID zone (zone 2) with no slots in the 512-byte
        // RGB frame, so sync derives its one color from the rendered keyboard frame's
        // physical bottom row — KEYMAP Row >= 5 (modifier/space row plus the arrow
        // cluster at row 6), the keys nearest the strip. Mirror semantics (strip
        // coincident with the bottom row) beat an appended virtual row: most effects
        // are per-key painters that would never paint an extra row, and the physical
        // gap is far under one key pitch.
        //
        // The sampled color is the width-weighted mean of the MIDDLE THIRD of the row
        // (physical x-extent from get_key_x geometry; keys whose centre falls in the
        // middle third — Space/AltGr/CtrlR on this layout), not the whole row: a
        // whole-row mean lets every hue vote, so a multi-hue bottom row (blue+red+
        // green+yellow+purple) collapses toward muddy near-white, while the middle
        // third keeps the strip reading as the color under the strip's own physical
        // midpoint — what a user means when they say "mirror the bottom row". Width-
        // weighting (Space ~5 pitches, Numpad0 ~2, else 1) makes the mean reflect
        // real physical coverage instead of equal votes; a mean of the middle third
        // still rotates continuously as the Wave sweeps, so the strip cycles smoothly
        // rather than snapping. (A brightest-key term was tried and rejected: it
        // chases a discrete argmax, jarring even eased.)
        public static bool SampleBottomRowColor(byte[] frame512, out byte r, out byte g, out byte b)
        {
            // Pass 1: physical extent of the sampled row (centre +/- half width).
            double left = double.MaxValue, right = double.MinValue;
            for (int i = 0; i < KeyMap.Length; i++)
            {
                if (KeyMap[i].Row < 5)
                    continue;
                double hw = SampleWidth(KeyMap[i].Name) / 2.0;
                if (KeyX[i] - hw < left) left = KeyX[i] - hw;
                if (KeyX[i] + hw > right) right = KeyX[i] + hw;
            }
            double t0 = left + (right - left) / 3.0;
            double t1 = left + 2.0 * (right - left) / 3.0;

            // Pass 2: width-weighted mean of the middle-third keys; the whole-row
            // mean is kept alongside as a defensive fallback (the static geometry
            // always yields at least Space, so it never fires in practice).
            int rs = 0, gs = 0, bs = 0, wsum = 0;
            int rsAll = 0, gsAll = 0, bsAll = 0, wsumAll = 0;
            int n = 0, nAll = 0;
            for (int i = 0; i < KeyMap.Length; i++)
            {
                if (KeyMap[i].Row < 5)
                    continue;
                int off = KeyMap[i].Offset;
                int w = SampleWidth(KeyMap[i].Name);
                rsAll += frame512[off] * w;
                gsAll += frame512[off + 1] * w;
                bsAll += frame512[off + 2] * w;
                wsumAll += w;
                nAll++;
                if (KeyX[i] < t0 || KeyX[i] > t1)
                    continue;
                rs += frame512[off] * w;
                gs += frame512[off + 1] * w;
                bs += frame512[off + 2] * w;
                wsum += w;
                n++;
            }
            if (nAll == 0 || wsumAll == 0)
            {
                r = g = b = 0;
                return false;
            }
            if (n == 0)
            {
                rs = rsAll; gs = gsAll; bs = bsAll; wsum = wsumAll;
            }
            r = (byte)Math.Round((double)rs / wsum);
            g = (byte)Math.Round((double)gs / wsum);
            b = (byte)Math.Round((double)bs / wsum);
            return true;
        }

        // Approximate physical pitch width of a sampled bottom-row key (get_key_x
        // geometry): Space spans ~5 columns, Numpad0 ~2, everything else ~1.
        private static int SampleWidth(string name)
        {
            if (name == "Space")
                return 5;
            if (name == "Numpad0")
                return 2;
            return 1;
        }

        // ---- Precomputed geometry (get_key_x/get_y, effect_visual_row) ---------
        private static readonly double[] KeyX = ComputeKeyX();
        private static readonly double[] KeyY = ComputeKeyY();
        private static readonly int[] KeyVisualRow = ComputeVisualRow();
        private static readonly double WheelCx = Mean(KeyX);
        private static readonly double WheelCy = Mean(KeyY);
        private static readonly int[] XRank = ComputeXRank();

        // AA sample offsets (better_rgb.c:2178-2180)
        private static readonly double[] SampleX = { -0.28, 0.28, -0.28, 0.28 };
        private static readonly double[] SampleY = { -0.22, -0.22, 0.22, 0.22 };
        private static readonly double[] CurrentSampleX = { -0.44, -0.31, -0.19, -0.06, 0.06, 0.19, 0.31, 0.44 };

        // ---- Keypress hook (GetAsyncKeyState, like the reference's effect threads)
        // GetAsyncKeyState is exported by user32.dll, NOT kernel32.dll — a wrong DLL
        // name makes the lazy DllImport binding throw on first call (only Reactive/
        // Ripple call it), which trips the streamer watchdog and abandons BetterRGB.
        [DllImport("user32.dll")]
        private static extern int GetAsyncKeyState(int virtualKey);

        // VK -> KEYMAP index (vk_to_keymap_index, better_rgb.c:2778-2805)
        private static readonly int[] VkToKeyMapIndex = BuildVkMap();
        // Tracked VK list (fill_tracked_vk_list, 2807-2821)
        private static readonly int[] TrackedVkList = BuildTrackedVkList();

        /// <summary>
        /// Renders one frame into a 512-byte interleaved buffer. The buffer is
        /// cleared here first, like the C effect threads (unsigned char buf[BUF_SIZE]
        /// = {0}). Brightness is applied by the caller via ApplyBrightness.
        /// </summary>
        public static void Render(BetterRgbEffect mode, BetterRgbFrameParams p,
            BetterRgbFrameState st, long frameIndex, double elapsedMs, byte[] frame512)
        {
            if (frame512 == null || frame512.Length != FrameSize)
                return;

            Array.Fill(frame512, (byte)0);

            switch (mode)
            {
                case BetterRgbEffect.Breathing: RenderBreathing(p, st, frame512); break;
                case BetterRgbEffect.Wave: RenderWave(p, st, frame512); break;
                case BetterRgbEffect.Sparkle: RenderSparkle(p, st, frame512); break;
                case BetterRgbEffect.Reactive: RenderReactive(p, st, frame512); break;
                case BetterRgbEffect.Wheel: RenderWheel(p, st, frame512); break;
                case BetterRgbEffect.Lightning: RenderLightning(p, st, frame512); break;
                case BetterRgbEffect.Flame: RenderFlame(p, st, frame512); break;
                case BetterRgbEffect.Raindrop: RenderRain(p, st, frame512); break;
                case BetterRgbEffect.Matrix: RenderMatrix(p, st, frame512); break;
                case BetterRgbEffect.Static: RenderStatic(p, frame512); break;
                case BetterRgbEffect.Ripple: RenderRipple(p, st, elapsedMs, frame512); break;
                case BetterRgbEffect.Rainbow: RenderRainbow(p, st, elapsedMs, frame512); break;
                case BetterRgbEffect.Quicksand: RenderQuicksand(p, st, elapsedMs, frame512); break;
                case BetterRgbEffect.Current: RenderCurrent(p, st, elapsedMs, frame512); break;
            }
        }

        /// <summary>C send_frame brightness step (1711-1715): scale the finished
        /// frame by g_brightness/100 — never sent to the device, software only.</summary>
        public static void ApplyBrightness(byte[] frame512, int percent)
        {
            if (percent >= 100 || frame512 == null)
                return;
            if (percent < 0)
                percent = 0;
            for (int i = 0; i < frame512.Length; i++)
                frame512[i] = (byte)((int)frame512[i] * percent / 100);
        }

        // ---- Effect renderers (1:1 with the C effect_* threads) -----------------

        /// <summary>C effect_breathing (2117-2151): raw=(sin(t)+1)/2, quantized to
        /// g_breathSmooth levels; t += g_breathSpeed/100 PER FRAME (state keeps t
        /// across reconnects, unlike the C thread restart).</summary>
        private static void RenderBreathing(BetterRgbFrameParams p, BetterRgbFrameState st,
            byte[] frame512)
        {
            double raw = (Math.Sin(st.BreathT) + 1.0) / 2.0;
            Color cycleColor;
            if (p.BreathRandom)
            {
                if (raw > st.BreathPrevRaw && st.BreathPrevRaw < 0.03)
                    st.BreathCycleColor = RandomHsvColor(st);
                cycleColor = st.BreathCycleColor;
            }
            else
            {
                cycleColor = p.BreathColor;
            }
            st.BreathPrevRaw = raw;

            int levels = p.BreathSmooth;
            if (levels < 2) levels = 2;
            double bright = Math.Floor(raw * levels) / (double)levels;
            var r = (byte)(cycleColor.R * bright);
            var g = (byte)(cycleColor.G * bright);
            var b = (byte)(cycleColor.B * bright);
            for (int i = 0; i < KeyCount; i++)
                SetKey(frame512, KeyMap[i].Offset, r, g, b);

            st.BreathT += p.BreathSpeed / 100.0;
        }

        /// <summary>C effect_wave (2283-2304): hue = t + (x*cosA + y*sinA)*15,
        /// t += g_waveSpeed per frame.</summary>
        private static void RenderWave(BetterRgbFrameParams p, BetterRgbFrameState st,
            byte[] frame512)
        {
            double angleRad = p.WaveAngle * Math.PI / 180.0;
            double dirX = Math.Cos(angleRad), dirY = Math.Sin(angleRad);
            double t = st.WaveT;
            for (int i = 0; i < KeyCount; i++)
            {
                double proj = KeyX[i] * dirX + KeyY[i] * dirY;
                var (r, g, b) = HsvToRgb(t + proj * 15.0);
                SetKey(frame512, KeyMap[i].Offset, r, g, b);
            }
            st.WaveT += p.WaveSpeed;
        }

        /// <summary>C effect_sparkle (2726-2776): age/duration per key, sin^2
        /// envelope, random HSV color, spawn to keep `density` alive.</summary>
        private static void RenderSparkle(BetterRgbFrameParams p, BetterRgbFrameState st,
            byte[] frame512)
        {
            int target = p.SparkleDensity;
            int lifeDuration = 70 - p.SparkleSpeed * 5;
            if (lifeDuration < 18) lifeDuration = 18;

            int alive = 0;
            for (int i = 0; i < KeyCount; i++)
                if (st.SparkleAge[i] > 0)
                    alive++;
            int deficit = target - alive;

            for (int n = 0; n < deficit; n++)
            {
                int idx = -1;
                for (int attempt = 0; attempt < 20; attempt++)
                {
                    int c = st.RandInt(KeyCount);
                    if (st.SparkleAge[c] == 0) { idx = c; break; }
                }
                if (idx < 0) break;
                st.SparkleAge[idx] = 1;
                st.SparkleDuration[idx] = lifeDuration + st.RandInt(12);
                var (hr, hg, hb) = HsvToRgb(st.RandInt(360));
                st.SparkleR[idx] = hr; st.SparkleG[idx] = hg; st.SparkleB[idx] = hb;
            }

            for (int i = 0; i < KeyCount; i++)
            {
                if (st.SparkleAge[i] <= 0)
                    continue;
                double progress = (double)st.SparkleAge[i] / (double)st.SparkleDuration[i];
                double envelope = Math.Sin(Math.PI * progress);
                envelope *= envelope;
                SetKeyFromLinear(frame512, i,
                    SrgbToLinear(st.SparkleR[i]) * envelope,
                    SrgbToLinear(st.SparkleG[i]) * envelope,
                    SrgbToLinear(st.SparkleB[i]) * envelope);
                st.SparkleAge[i]++;
                if (st.SparkleAge[i] >= st.SparkleDuration[i])
                    st.SparkleAge[i] = 0;
            }
        }

        /// <summary>C effect_reactive (2823-2865): GetAsyncKeyState over the tracked
        /// VK list; held keys full color, released keys fade over g_reactiveDuration.
        /// Random spawn color is per-channel rand()%256 (2840), NOT hsv.</summary>
        private static void RenderReactive(BetterRgbFrameParams p, BetterRgbFrameState st,
            byte[] frame512)
        {
            PollFreshPresses(st);

            int duration = p.ReactiveDuration;
            if (duration < 1) duration = 1;

            for (int e = 0; e < st.EventCount; e++)
            {
                int idx = st.EventIdx[e];
                if (p.ReactiveRandom)
                {
                    st.ReactiveR[idx] = (byte)st.RandInt(256);
                    st.ReactiveG[idx] = (byte)st.RandInt(256);
                    st.ReactiveB[idx] = (byte)st.RandInt(256);
                }
                else
                {
                    st.ReactiveR[idx] = p.ReactiveColor.R;
                    st.ReactiveG[idx] = p.ReactiveColor.G;
                    st.ReactiveB[idx] = p.ReactiveColor.B;
                }
            }

            for (int i = 0; i < KeyCount; i++)
            {
                if (st.WasDown[i] != 0)
                {
                    SetKey(frame512, KeyMap[i].Offset, st.ReactiveR[i], st.ReactiveG[i], st.ReactiveB[i]);
                    st.ReactiveFadeLife[i] = duration; // C: fadeLife set every held frame
                }
                else if (st.ReactiveFadeLife[i] > 0)
                {
                    double fade = st.ReactiveFadeLife[i] / (double)duration;
                    SetKey(frame512, KeyMap[i].Offset,
                        (byte)(st.ReactiveR[i] * fade), (byte)(st.ReactiveG[i] * fade), (byte)(st.ReactiveB[i] * fade));
                    st.ReactiveFadeLife[i]--;
                }
            }
        }

        /// <summary>C effect_wheel (2306-2330): hue = t*dirMul + atan2 around the
        /// KEYMAP centroid; t += g_wheelSpeed per frame.</summary>
        private static void RenderWheel(BetterRgbFrameParams p, BetterRgbFrameState st,
            byte[] frame512)
        {
            double dirMul = p.WheelReverse ? -1.0 : 1.0;
            double t = st.WheelT;
            for (int i = 0; i < KeyCount; i++)
            {
                double x = KeyX[i] - WheelCx;
                double y = KeyY[i] - WheelCy;
                double angleDeg = Math.Atan2(y, x) * 180.0 / Math.PI;
                var (r, g, b) = HsvToRgb(t * dirMul + angleDeg);
                SetKey(frame512, KeyMap[i].Offset, r, g, b);
            }
            st.WheelT += p.WheelSpeed;
        }

        /// <summary>C effect_lightning (2333-2403): vertical bolts, sinusoidal bend,
        /// smoothstep front, exponential tail, gaussian horizontal, phosphor EMA
        /// (attack/release from g_lightningSmooth), countdown respawns.</summary>
        private static void RenderLightning(BetterRgbFrameParams p, BetterRgbFrameState st,
            byte[] frame512)
        {
            double speed = 0.05 + p.LightningSpeed * 0.08;
            double bandWidth = 0.4 + p.LightningSmooth * 0.15;
            double tailLen = bandWidth * 2.5;
            double finishPos = 6.0 + tailLen + 0.5;
            double xSigma = 0.18 + p.LightningWidth * 0.10;
            int concurrent = p.LightningConcurrent;
            if (concurrent < 1) concurrent = 1;
            if (concurrent > BetterRgbFrameState.MaxStrikes) concurrent = BetterRgbFrameState.MaxStrikes;

            for (int i = 0; i < KeyCount; i++)
                st.LightningBright[i] = 0;

            for (int s = 0; s < concurrent; s++)
            {
                if (st.LightningHeadPos[s] > finishPos)
                {
                    st.LightningCountdown[s]--;
                    if (st.LightningCountdown[s] <= 0)
                    {
                        st.LightningHeadPos[s] = -2.0;
                        st.LightningBoltX[s] = st.RandInt(1900) / 100.0;
                        st.LightningBendSeed[s] = st.RandInt(628) / 100.0;
                        st.LightningCountdown[s] = 20 + st.RandInt(40);
                    }
                }
                if (st.LightningHeadPos[s] <= finishPos)
                {
                    for (int i = 0; i < KeyCount; i++)
                    {
                        double y = KeyY[i];
                        double dv = st.LightningHeadPos[s] - y;
                        double centerX = st.LightningBoltX[s] + Math.Sin(y * 2.35 + st.LightningBendSeed[s]) * 0.32;
                        double dh = KeyX[i] - centerX;
                        double front = dv < 0.0 ? SmoothUnit((dv + 0.85) / 0.85) : 1.0;
                        double tail = dv > 0.0 ? Math.Exp(-dv / (tailLen * 0.52)) : 1.0;
                        double horizontal = Math.Exp(-0.5 * (dh / xSigma) * (dh / xSigma));
                        if (horizontal < 0.02) continue;
                        double b = front * tail * horizontal;
                        if (b > st.LightningBright[i]) st.LightningBright[i] = b;
                    }
                    st.LightningHeadPos[s] += speed;
                }
            }

            double smoothValue = p.LightningSmooth;
            double attack = 0.62 / (1.0 + smoothValue * 0.065);
            double release = 0.24 / (1.0 + smoothValue * 0.055);
            for (int i = 0; i < KeyCount; i++)
            {
                double target = st.LightningBright[i] < 0.002 ? 0.0 : st.LightningBright[i];
                double coefficient = target > st.LightningPrevious[i] ? attack : release;
                st.LightningPrevious[i] += (target - st.LightningPrevious[i]) * coefficient;
                double glow = st.LightningPrevious[i];
                SetKeyFromLinear(frame512, i,
                    0.0012 + glow * 0.90,
                    0.0012 + glow * 0.90,
                    0.0044 + glow);
            }
        }

        /// <summary>C effect_flame (2405-2444): per-key heat EMA, quantized updates
        /// every 11-speed frames, fire gradient ramp.</summary>
        private static void RenderFlame(BetterRgbFrameParams p, BetterRgbFrameState st,
            byte[] frame512)
        {
            double speed = p.FlameSpeed;
            double smooth = p.FlameSmooth;
            double alpha = 1.0 / (1.0 + smooth * 0.6);
            double volatility = 40.0 + speed * 18.0;
            int updateInterval = 11 - (int)speed;
            if (updateInterval < 1) updateInterval = 1;

            st.FlameCounter++;
            if (st.FlameCounter % updateInterval == 0)
            {
                for (int i = 0; i < KeyCount; i++)
                {
                    double rowFrac = KeyMap[i].Row / 6.0;
                    double bias = 40.0 * rowFrac;
                    double target = 90.0 + bias + (st.RandInt((int)Math.Max(1, volatility)) - volatility / 2.0);
                    st.FlameHeat[i] = st.FlameHeat[i] * (1.0 - alpha) + target * alpha;
                    st.FlameHeat[i] = Math.Clamp(st.FlameHeat[i], 0.0, 255.0);
                }
            }

            for (int i = 0; i < KeyCount; i++)
            {
                int h = (int)st.FlameHeat[i];
                byte r, g, b;
                if (h < 85) { r = (byte)(h * 3); g = 0; b = 0; }
                else if (h < 170) { r = 255; g = (byte)((h - 85) * 3); b = 0; }
                else { r = 255; g = 255; b = (byte)((h - 170) * 3); }
                SetKey(frame512, KeyMap[i].Offset, r, g, b);
            }
        }

        /// <summary>C effect_rain (2447-2513): falling gaussian drops with
        /// exponential tails, EMA 0.42 rising / 0.19 falling.</summary>
        private static void RenderRain(BetterRgbFrameParams p, BetterRgbFrameState st,
            byte[] frame512)
        {
            double speed = 0.05 + p.RainSpeed * 0.03;
            int colorCount = p.RainColorCount;
            if (colorCount < 1) colorCount = 1;
            if (colorCount > 5) colorCount = 5;

            int target = p.RainDensity;
            if (target < 1) target = 1;
            if (target > 5) target = 5;
            int activeDrops = target * 3;
            if (activeDrops > BetterRgbFrameState.MaxRainDrops) activeDrops = BetterRgbFrameState.MaxRainDrops;

            for (int i = 0; i < KeyCount; i++)
            {
                st.RainTargetR[i] = 0; st.RainTargetG[i] = 0; st.RainTargetB[i] = 0;
            }

            for (int d = 0; d < activeDrops; d++)
            {
                var col = p.RainColors[st.RainDropColorIdx[d] % colorCount];
                double cr = SrgbToLinear(col.R), cg = SrgbToLinear(col.G), cb = SrgbToLinear(col.B);
                for (int i = 0; i < KeyCount; i++)
                {
                    double dx = (KeyX[i] - st.RainDropX[d]) / 0.46;
                    double dy = st.RainDropRow[d] - KeyY[i];
                    double horizontal = Math.Exp(-0.5 * dx * dx);
                    double head = Math.Exp(-0.5 * (dy / 0.34) * (dy / 0.34));
                    double tail = dy > 0.0 ? 0.52 * Math.Exp(-dy / 1.15) : 0.0;
                    double intensity = horizontal * (head + tail);
                    st.RainTargetR[i] += cr * intensity;
                    st.RainTargetG[i] += cg * intensity;
                    st.RainTargetB[i] += cb * intensity;
                }
                st.RainDropRow[d] += speed;
                if (st.RainDropRow[d] > 8)
                {
                    st.RainDropRow[d] = -(double)st.RandInt(5);
                    st.RainDropX[d] = st.RandInt(20);
                    st.RainDropColorIdx[d] = st.RandInt(colorCount);
                }
            }

            for (int i = 0; i < KeyCount; i++)
            {
                double targetLevel = st.RainTargetR[i] + st.RainTargetG[i] + st.RainTargetB[i];
                double previousLevel = st.RainPrevR[i] + st.RainPrevG[i] + st.RainPrevB[i];
                double coefficient = targetLevel > previousLevel ? 0.42 : 0.19;
                st.RainPrevR[i] += (st.RainTargetR[i] - st.RainPrevR[i]) * coefficient;
                st.RainPrevG[i] += (st.RainTargetG[i] - st.RainPrevG[i]) * coefficient;
                st.RainPrevB[i] += (st.RainTargetB[i] - st.RainPrevB[i]) * coefficient;
                SetKeyFromLinear(frame512, i, st.RainPrevR[i], st.RainPrevG[i], st.RainPrevB[i]);
            }
        }

        /// <summary>C effect_matrix (2517-2667): 4 styles — diagonal streaks,
        /// falling columns, laser scan, expanding rings.</summary>
        private static void RenderMatrix(BetterRgbFrameParams p, BetterRgbFrameState st,
            byte[] frame512)
        {
            int colorCount = p.MatrixColorCount;
            if (colorCount < 1) colorCount = 1;
            if (colorCount > 3) colorCount = 3;
            int style = p.MatrixStyle;
            double speedMul = 0.3 + p.MatrixSpeed * 0.15;

            if (style == 0)
            {
                if (!st.MatrixInitDone)
                {
                    st.MatrixActiveStreaks = Math.Clamp(p.MatrixDensity, 1, 5);
                    st.MatrixInitDone = true;
                }
                st.MatrixRerollCounter++;
                if (st.MatrixRerollCounter >= 60)
                {
                    st.MatrixActiveStreaks = ComputeProbCount(st, Math.Clamp(p.MatrixDensity, 1, 5), 1, 5);
                    st.MatrixRerollCounter = 0;
                }
                for (int i = 0; i < KeyCount; i++)
                {
                    double x = KeyX[i], y = KeyY[i];
                    double bestBright = 0; int bestColor = 0;
                    for (int s = 0; s < st.MatrixActiveStreaks; s++)
                    {
                        double dirX = Math.Cos(st.MatrixAngle[s]), dirY = Math.Sin(st.MatrixAngle[s]);
                        double proj = x * dirX + y * dirY;
                        double dist = Math.Abs(proj - st.MatrixPos[s]);
                        if (dist < 1.5)
                        {
                            double b = 1.0 - dist / 1.5;
                            if (b > bestBright) { bestBright = b; bestColor = st.MatrixStreakColorIdx[s] % colorCount; }
                        }
                    }
                    if (bestBright > 1) bestBright = 1;
                    var col = p.MatrixColors[bestColor];
                    SetKey(frame512, KeyMap[i].Offset,
                        (byte)(col.R * bestBright), (byte)(col.G * bestBright), (byte)(col.B * bestBright));
                }
                for (int s = 0; s < st.MatrixActiveStreaks; s++)
                {
                    st.MatrixPos[s] += st.MatrixSpd[s] * speedMul;
                    if (st.MatrixPos[s] > 30)
                    {
                        st.MatrixPos[s] = -30;
                        st.MatrixAngle[s] = st.RandInt(360) * Math.PI / 180.0;
                        st.MatrixSpd[s] = 0.2 + st.RandInt(100) / 100.0;
                        st.MatrixStreakColorIdx[s] = st.RandInt(colorCount);
                    }
                }
            }
            else if (style == 1)
            {
                for (int i = 0; i < KeyCount; i++)
                {
                    double x = KeyX[i];
                    int col = (int)(x + 0.5);
                    if (col < 0) col = 0;
                    if (col >= BetterRgbFrameState.MatrixColumns) col = BetterRgbFrameState.MatrixColumns - 1;
                    double dv = st.MatrixColPos[col] - KeyMap[i].Row;
                    double bright = 0;
                    if (dv >= -0.3 && dv < 4.0)
                        bright = dv < 0 ? 1.0 : 1.0 - dv / 4.0;
                    if (bright < 0) bright = 0;
                    var cc = p.MatrixColors[st.MatrixColColorIdx[col] % colorCount];
                    SetKey(frame512, KeyMap[i].Offset,
                        (byte)(cc.R * bright), (byte)(cc.G * bright), (byte)(cc.B * bright));
                }
                for (int i = 0; i < BetterRgbFrameState.MatrixColumns; i++)
                {
                    st.MatrixColPos[i] += st.MatrixColSpeed[i] * speedMul;
                    if (st.MatrixColPos[i] > 10)
                    {
                        st.MatrixColPos[i] = -(double)st.RandInt(6);
                        st.MatrixColSpeed[i] = 0.05 + st.RandInt(100) / 300.0;
                        st.MatrixColColorIdx[i] = st.RandInt(colorCount);
                    }
                }
            }
            else if (style == 2)
            {
                int laserCount = Math.Clamp(p.MatrixDensity, 1, 5) >= 4 ? 3 : 2;
                for (int i = 0; i < KeyCount; i++)
                {
                    double x = KeyX[i], y = KeyY[i];
                    double bestBright = 0; int bestColor = 0;
                    for (int s = 0; s < laserCount; s++)
                    {
                        double dirX = Math.Cos(st.MatrixLaserAngle[s]), dirY = Math.Sin(st.MatrixLaserAngle[s]);
                        double proj = x * dirX + y * dirY;
                        double dist = Math.Abs(proj - st.MatrixLaserPos[s]);
                        if (dist < 0.6)
                        {
                            double b = 1.0 - dist / 0.6;
                            if (b > bestBright) { bestBright = b; bestColor = st.MatrixLaserColorIdx[s] % colorCount; }
                        }
                    }
                    if (bestBright > 1) bestBright = 1;
                    var col = p.MatrixColors[bestColor];
                    SetKey(frame512, KeyMap[i].Offset,
                        (byte)(col.R * bestBright), (byte)(col.G * bestBright), (byte)(col.B * bestBright));
                }
                for (int s = 0; s < laserCount; s++)
                {
                    st.MatrixLaserPos[s] += st.MatrixLaserSpd[s] * speedMul * 1.5;
                    if (st.MatrixLaserPos[s] > 30)
                    {
                        st.MatrixLaserPos[s] = -30.0 - st.RandInt(20);
                        st.MatrixLaserAngle[s] = st.RandInt(4) * (Math.PI / 2.0);
                        st.MatrixLaserSpd[s] = 0.6 + st.RandInt(100) / 100.0;
                        st.MatrixLaserColorIdx[s] = st.RandInt(colorCount);
                    }
                }
            }
            else
            {
                for (int i = 0; i < KeyCount; i++)
                {
                    double x = KeyX[i], y = KeyY[i];
                    double bestBright = 0; int bestColor = 0;
                    for (int s = 0; s < BetterRgbFrameState.MaxRipples; s++)
                    {
                        double dx = x - st.MatrixRippleX[s], dy = y - st.MatrixRippleY[s];
                        double dist = Math.Sqrt(dx * dx + dy * dy);
                        double ringDist = Math.Abs(dist - st.MatrixRippleR[s]);
                        if (ringDist < 1.2)
                        {
                            double b = 1.0 - ringDist / 1.2;
                            if (b > bestBright) { bestBright = b; bestColor = st.MatrixRippleColorIdx[s] % colorCount; }
                        }
                    }
                    if (bestBright > 1) bestBright = 1;
                    var col = p.MatrixColors[bestColor];
                    SetKey(frame512, KeyMap[i].Offset,
                        (byte)(col.R * bestBright), (byte)(col.G * bestBright), (byte)(col.B * bestBright));
                }
                for (int s = 0; s < BetterRgbFrameState.MaxRipples; s++)
                {
                    st.MatrixRippleR[s] += speedMul * 0.25;
                    if (st.MatrixRippleR[s] > 24)
                    {
                        st.MatrixRippleX[s] = st.RandInt(20);
                        st.MatrixRippleY[s] = st.RandInt(7);
                        st.MatrixRippleR[s] = 0;
                        st.MatrixRippleColorIdx[s] = st.RandInt(colorCount);
                    }
                }
            }
        }

        /// <summary>C effect_static (2681-2724): horizontal row zones or vertical
        /// x-rank zones.</summary>
        private static void RenderStatic(BetterRgbFrameParams p, byte[] frame512)
        {
            int layout = p.StaticLayout;
            int maxZones = layout == 0 ? 6 : 10;
            int zoneCount = p.StaticZones;
            if (zoneCount < 1) zoneCount = 1;
            if (zoneCount > maxZones) zoneCount = maxZones;

            for (int i = 0; i < KeyCount; i++)
            {
                int zone = 0;
                if (zoneCount > 1)
                {
                    if (layout == 0)
                        zone = HorizontalZoneForRow(KeyMap[i].Row, zoneCount);
                    else
                    {
                        zone = (XRank[i] * zoneCount) / KeyCount;
                        if (zone >= zoneCount) zone = zoneCount - 1;
                    }
                }
                var c = p.StaticZoneColors[zone];
                SetKey(frame512, KeyMap[i].Offset, c.R, c.G, c.B);
            }
        }

        /// <summary>C effect_ripple (2878-2947): keypress spawns rings; wall-clock
        /// radius, band width, linear fade over lifetime.</summary>
        private static void RenderRipple(BetterRgbFrameParams p, BetterRgbFrameState st,
            double elapsedMs, byte[] frame512)
        {
            PollFreshPresses(st);

            for (int e = 0; e < st.EventCount; e++)
            {
                int slot = st.RippleNextSlot;
                st.RippleNextSlot = (st.RippleNextSlot + 1) % BetterRgbFrameState.MaxTouchRipples;
                st.RippleActive[slot] = true;
                st.RippleX[slot] = st.EventX[e];
                st.RippleY[slot] = st.EventY[e];
                st.RippleStartedAtMs[slot] = elapsedMs;
                if (p.RippleRandom)
                {
                    var (hr, hg, hb) = HsvToRgb(st.RandInt(360));
                    st.RippleR[slot] = hr; st.RippleG[slot] = hg; st.RippleB[slot] = hb;
                }
                else
                {
                    st.RippleR[slot] = p.RippleColor.R;
                    st.RippleG[slot] = p.RippleColor.G;
                    st.RippleB[slot] = p.RippleColor.B;
                }
            }

            double speed = 0.006 + p.RippleSpeed * 0.0015;
            double width = 0.35 + p.RippleWidth * 0.25;
            double lifetime = 2300.0 - p.RippleSpeed * 120.0;

            for (int i = 0; i < KeyCount; i++)
            {
                double outR = 0, outG = 0, outB = 0;
                double keyX = KeyX[i], keyY = KeyY[i];
                for (int r = 0; r < BetterRgbFrameState.MaxTouchRipples; r++)
                {
                    if (!st.RippleActive[r]) continue;
                    double elapsed = elapsedMs - st.RippleStartedAtMs[r];
                    if (elapsed >= lifetime)
                    {
                        st.RippleActive[r] = false;
                        continue;
                    }
                    double dx = keyX - st.RippleX[r];
                    double dy = keyY - st.RippleY[r];
                    double distance = Math.Sqrt(dx * dx + dy * dy);
                    double radius = elapsed * speed;
                    double band = 1.0 - Math.Abs(distance - radius) / width;
                    if (band <= 0.0) continue;
                    double fade = 1.0 - elapsed / lifetime;
                    double intensity = band * fade;
                    outR += st.RippleR[r] * intensity;
                    outG += st.RippleG[r] * intensity;
                    outB += st.RippleB[r] * intensity;
                }
                if (outR > 255.0) outR = 255.0;
                if (outG > 255.0) outG = 255.0;
                if (outB > 255.0) outB = 255.0;
                SetKey(frame512, KeyMap[i].Offset, (byte)outR, (byte)outG, (byte)outB);
            }
        }

        /// <summary>C effect_rainbow (2153-2173): one hue for all keys, accumulating
        /// (4 + g_rainbowSpeed*2) deg/s of wall-clock time.</summary>
        private static void RenderRainbow(BetterRgbFrameParams p, BetterRgbFrameState st,
            double elapsedMs, byte[] frame512)
        {
            double elapsedSec = elapsedMs / 1000.0;
            double degreesPerSecond = 4.0 + p.RainbowSpeed * 2.0;
            st.RainbowHue = (st.RainbowHue + (elapsedSec - st.RainbowPrevElapsedSec) * degreesPerSecond) % 360.0;
            if (st.RainbowHue < 0) st.RainbowHue += 360.0;
            st.RainbowPrevElapsedSec = elapsedSec;

            var (r, g, b) = HsvToRgb(st.RainbowHue);
            for (int i = 0; i < KeyCount; i++)
                SetKey(frame512, KeyMap[i].Offset, r, g, b);
        }

        /// <summary>C effect_quicksand (2192-2227): flowing curtain with sinusoidal
        /// warp, 4-sample AA, palette sampled in linear light.</summary>
        private static void RenderQuicksand(BetterRgbFrameParams p, BetterRgbFrameState st,
            double elapsedMs, byte[] frame512)
        {
            double seconds = elapsedMs / 1000.0;
            double speed = 0.22 + p.QuicksandSpeed * 0.16;
            double wavelength = 1.15 + p.QuicksandScale * 0.42;
            double flow = seconds * speed;

            for (int i = 0; i < KeyCount; i++)
            {
                double sumR = 0, sumG = 0, sumB = 0;
                for (int sample = 0; sample < 4; sample++)
                {
                    double x = KeyX[i] + SampleX[sample];
                    double y = KeyY[i] + SampleY[sample];
                    double warpedY = y - flow +
                        0.24 * Math.Sin(x * 0.68 + flow * 0.55) +
                        0.10 * Math.Sin(x * 1.73 - flow * 0.31);
                    double band = warpedY / wavelength;
                    double phase = band + x * 0.035;
                    double curtain = (Math.Sin(band * 2.0 * Math.PI) + 1.0) * 0.5;
                    double intensity = 0.08 + 0.92 * SmoothUnit(curtain);
                    double grain = 0.90 + 0.10 * Math.Sin(x * 2.41 + y * 3.17 + flow * 1.2);
                    var (lr, lg, lb) = SamplePaletteLinear(p.QuicksandColors, phase);
                    sumR += lr * intensity * grain;
                    sumG += lg * intensity * grain;
                    sumB += lb * intensity * grain;
                }
                SetKeyFromLinear(frame512, i, sumR / 4.0, sumG / 4.0, sumB / 4.0);
            }
        }

        /// <summary>C effect_current (2229-2281): per-row comet with exponential
        /// decay tail, 8-sample AA, EMA 0.30 rising / 0.14 falling.</summary>
        private static void RenderCurrent(BetterRgbFrameParams p, BetterRgbFrameState st,
            double elapsedMs, byte[] frame512)
        {
            double seconds = elapsedMs / 1000.0;
            double travelSpeed = 0.50 + p.CurrentSpeed * 0.40;
            double width = p.CurrentWidth;
            double cycle = 22.0 + width;

            for (int i = 0; i < KeyCount; i++)
            {
                int row = KeyVisualRow[i];
                double sumR = 0, sumG = 0, sumB = 0;
                for (int sample = 0; sample < 8; sample++)
                {
                    double x = KeyX[i] + CurrentSampleX[sample];
                    double travelX = (row & 1) == 1 ? 19.0 - x : x;
                    double head = Frac(seconds * travelSpeed + row * 3.35, cycle) - width;
                    double distanceBehind = head - travelX;
                    double intensity = 0.0;
                    if (distanceBehind > -0.75 && distanceBehind < width + 0.75)
                    {
                        double headCover = distanceBehind < 0.0
                            ? SmoothUnit((distanceBehind + 0.75) / 0.75) : 1.0;
                        double tailCover = distanceBehind > width
                            ? SmoothUnit((width + 0.75 - distanceBehind) / 0.75) : 1.0;
                        double decayDistance = distanceBehind > 0.0 ? distanceBehind : 0.0;
                        intensity = Math.Exp(-decayDistance / (0.42 * width + 0.45)) * headCover * tailCover;
                    }
                    double phase = (double)row / 7.0 + travelX * 0.045 + seconds * 0.075;
                    var (r, g, b) = SamplePaletteLinear(p.CurrentColors, phase);
                    sumR += r * intensity;
                    sumG += g * intensity;
                    sumB += b * intensity;
                }
                double targetR = sumR / 8.0, targetG = sumG / 8.0, targetB = sumB / 8.0;
                double targetLevel = targetR + targetG + targetB;
                double previousLevel = st.CurrentPrevR[i] + st.CurrentPrevG[i] + st.CurrentPrevB[i];
                double smoothing = targetLevel > previousLevel ? 0.30 : 0.14;
                st.CurrentPrevR[i] += (targetR - st.CurrentPrevR[i]) * smoothing;
                st.CurrentPrevG[i] += (targetG - st.CurrentPrevG[i]) * smoothing;
                st.CurrentPrevB[i] += (targetB - st.CurrentPrevB[i]) * smoothing;
                SetKeyFromLinear(frame512, i, st.CurrentPrevR[i], st.CurrentPrevG[i], st.CurrentPrevB[i]);
            }
        }

        // ---- Frame writing (set_key + AUX mirror, 1755-1766) --------------------
        private static void SetKey(byte[] frame, int offset, byte r, byte g, byte b)
        {
            if (offset < 0 || offset + 2 >= FrameSize) return;
            frame[offset] = r; frame[offset + 1] = g; frame[offset + 2] = b;
            int aux = -1;
            if (offset == OffsetShiftLeft) aux = OffsetShiftLeftAux;
            else if (offset == OffsetEnter) aux = OffsetEnterAux;
            if (aux >= 0 && aux + 2 < FrameSize)
            {
                frame[aux] = r; frame[aux + 1] = g; frame[aux + 2] = b;
            }
        }

        private static void SetKeyFromLinear(byte[] frame, int keyIndex, double r, double g, double b)
        {
            SetKey(frame, KeyMap[keyIndex].Offset, LinearToSrgb(r), LinearToSrgb(g), LinearToSrgb(b));
        }

        // ---- Shared helpers (better_rgb.c:1768-1844) ----------------------------
        private static (byte R, byte G, byte B) HsvToRgb(double hueDeg)
        {
            double h = hueDeg % 360.0;
            if (h < 0) h += 360.0;
            double c = 1.0, x = c * (1 - Math.Abs(h / 60.0 % 2 - 1));
            double rr, gg, bb;
            if (h < 60) { rr = c; gg = x; bb = 0; }
            else if (h < 120) { rr = x; gg = c; bb = 0; }
            else if (h < 180) { rr = 0; gg = c; bb = x; }
            else if (h < 240) { rr = 0; gg = x; bb = c; }
            else if (h < 300) { rr = x; gg = 0; bb = c; }
            else { rr = c; gg = 0; bb = x; }
            return ((byte)(rr * 255), (byte)(gg * 255), (byte)(bb * 255));
        }

        private static Color HsvColor(double hueDeg)
        {
            var (r, g, b) = HsvToRgb(hueDeg);
            return Color.FromRgb(r, g, b);
        }

        private static Color RandomHsvColor(BetterRgbFrameState st)
        {
            return HsvColor(st.RandInt(360));
        }

        private static double WrapUnit(double v)
        {
            double f = v % 1.0;
            return f < 0 ? f + 1.0 : f;
        }

        private static double SmoothUnit(double v)
        {
            if (v <= 0.0) return 0.0;
            if (v >= 1.0) return 1.0;
            return v * v * (3.0 - 2.0 * v);
        }

        private static double Frac(double v, double period)
        {
            double f = v % period;
            return f < 0 ? f + period : f;
        }

        private static double SrgbToLinear(byte v)
        {
            double c = v / 255.0;
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        private static byte LinearToSrgb(double v)
        {
            if (v <= 0.0) return 0;
            if (v >= 1.0) return 255;
            double c = v <= 0.0031308 ? v * 12.92 : 1.055 * Math.Pow(v, 1.0 / 2.4) - 0.055;
            int result = (int)(c * 255.0 + 0.5);
            return (byte)Math.Clamp(result, 0, 255);
        }

        /// <summary>C sample_palette_linear (1830-1844): 3-color closed-loop ramp
        /// mixed in linear light.</summary>
        private static (double R, double G, double B) SamplePaletteLinear(Color[] colors3, double phase)
        {
            double scaled = WrapUnit(phase) * 3.0;
            int first = Math.Min((int)Math.Floor(scaled), 2);
            int second = (first + 1) % 3;
            double mix = SmoothUnit(scaled - Math.Floor(scaled));
            var a = colors3[first];
            var n = colors3[second];
            return (SrgbToLinear(a.R) * (1.0 - mix) + SrgbToLinear(n.R) * mix,
                    SrgbToLinear(a.G) * (1.0 - mix) + SrgbToLinear(n.G) * mix,
                    SrgbToLinear(a.B) * (1.0 - mix) + SrgbToLinear(n.B) * mix);
        }

        // ---- Geometry tables ----------------------------------------------------
        private static double[] ComputeKeyX()
        {
            var xs = new double[KeyCount];
            for (int i = 0; i < KeyCount; i++)
            {
                var (name, _, row, col) = KeyMap[i];
                xs[i] = name switch
                {
                    "NumLock" or "Numpad7" or "Numpad4" or "Numpad1" => 15.5,
                    "NumpadDiv" or "Numpad8" or "Numpad5" or "Numpad2" => 16.5,
                    "NumpadMul" or "Numpad9" or "Numpad6" or "Numpad3" or "NumpadDecimal" => 17.5,
                    "NumpadMinus" or "NumpadPlus" or "NumpadEnter" => 18.5,
                    "Numpad0" => 16.0,
                    "Backspace" or "Enter" => 13.5,
                    "ShiftL" or "CtrlL" => 0.125,
                    "ShiftR" => 13.375,
                    "Fn" => 1.25,
                    "Windows" => 2.25,
                    "AltL" => 3.25,
                    "Space" => 6.25,
                    "AltGr" => 9.5,
                    "CtrlR" => 10.75,
                    "ArrowUp" or "ArrowDown" => 12.75,
                    "ArrowLeft" => 12.25,
                    "ArrowRight" => 13.25,
                    _ => row switch
                    {
                        2 => col + 0.25,
                        3 => col + 0.375,
                        4 => col + 0.25,
                        _ => (double)col,
                    },
                };
            }
            return xs;
        }

        private static double[] ComputeKeyY()
        {
            var ys = new double[KeyCount];
            for (int i = 0; i < KeyCount; i++)
            {
                var name = KeyMap[i].Name;
                var row = KeyMap[i].Row;
                ys[i] = name switch
                {
                    "Enter" or "NumpadPlus" => 2.5,
                    "NumpadEnter" => 4.5,
                    "ArrowUp" => 4.75,
                    "ArrowLeft" or "ArrowDown" or "ArrowRight" => 5.25,
                    _ => (double)row,
                };
            }
            return ys;
        }

        private static int[] ComputeVisualRow()
        {
            // effect_visual_row (2182-2190)
            var rows = new int[KeyCount];
            for (int i = 0; i < KeyCount; i++)
            {
                var name = KeyMap[i].Name;
                var row = KeyMap[i].Row;
                rows[i] = name switch
                {
                    "Enter" => 3,
                    "Backslash" => 2,
                    "ArrowLeft" or "ArrowDown" or "ArrowRight" => 6,
                    _ => row,
                };
            }
            return rows;
        }

        private static double Mean(double[] xs)
        {
            double s = 0;
            foreach (var v in xs) s += v;
            return s / xs.Length;
        }

        private static int[] ComputeXRank()
        {
            var idx = new int[KeyCount];
            for (int i = 0; i < KeyCount; i++) idx[i] = i;
            for (int i = 1; i < KeyCount; i++)
            {
                int cur = idx[i];
                double curX = KeyX[cur];
                int j = i;
                while (j > 0 && KeyX[idx[j - 1]] > curX) { idx[j] = idx[j - 1]; j--; }
                idx[j] = cur;
            }
            var rank = new int[KeyCount];
            for (int i = 0; i < KeyCount; i++) rank[idx[i]] = i;
            return rank;
        }

        private static int HorizontalZoneForRow(int row, int zoneCount)
        {
            // horizontal_zone_for_row (2669-2679)
            return zoneCount switch
            {
                1 => 0,
                2 => row <= 3 ? 0 : 1,
                3 => row <= 1 ? 0 : row <= 4 ? 1 : 2,
                4 => row == 0 ? 0 : row <= 2 ? 1 : row <= 4 ? 2 : 3,
                5 => row == 0 ? 0 : row == 1 ? 1 : row == 2 ? 2 : row <= 4 ? 3 : 4,
                6 => row switch { 0 => 0, 1 => 1, 2 => 2, 3 => 3, 4 => 4, _ => 5 },
                _ => 0,
            };
        }

        private static int ComputeProbCount(BetterRgbFrameState st, int target, int minCount, int maxCount)
        {
            // compute_prob_count (1909-1915) — uses the state PRNG stream.
            if (target >= maxCount) return maxCount;
            int r = st.RandInt(100);
            if (r < 25)
            {
                int v = target - 1;
                return v < minCount ? minCount : v;
            }
            if (r < 75) return target;
            int w = target + 1;
            return w > maxCount ? maxCount : w;
        }

        // ---- Keypress hook ------------------------------------------------------
        private static (int Vk, string Name)[] BuildVkTable()
        {
            // vk_to_keymap_index table (better_rgb.c:2783-2797); VK_* values from
            // winuser.h (llvm-mingw clone). Built via List (no new-expressions for
            // tuple types).
            var list = new List<(int, string)>();
            list.Add((0x20, "Space")); list.Add((0x0D, "Enter")); list.Add((0x08, "Backspace")); list.Add((0x09, "Tab"));
            list.Add((0xA0, "ShiftL")); list.Add((0xA1, "ShiftR")); list.Add((0x14, "CapsLock")); list.Add((0xA2, "CtrlL"));
            list.Add((0xA3, "CtrlR"));
            list.Add((0xA4, "AltL")); list.Add((0xA5, "AltGr")); list.Add((0x25, "ArrowLeft")); list.Add((0x27, "ArrowRight"));
            list.Add((0x26, "ArrowUp")); list.Add((0x28, "ArrowDown")); list.Add((0xE2, "ISO_Backslash")); list.Add((0x5B, "Windows"));
            list.Add((0xDB, "BracketL")); list.Add((0xDD, "BracketR")); list.Add((0xBA, "Semicolon")); list.Add((0xDE, "Quote"));
            list.Add((0xDC, "Backslash")); list.Add((0xBC, "Comma")); list.Add((0xBE, "Period")); list.Add((0xBF, "Slash"));
            list.Add((0xBD, "Minus")); list.Add((0xBB, "Equals")); list.Add((0xC0, "Grave"));
            list.Add((0x1B, "Esc")); list.Add((0x70, "F1")); list.Add((0x71, "F2")); list.Add((0x72, "F3"));
            list.Add((0x73, "F4")); list.Add((0x74, "F5")); list.Add((0x75, "F6")); list.Add((0x76, "F7"));
            list.Add((0x77, "F8")); list.Add((0x78, "F9")); list.Add((0x79, "F10")); list.Add((0x7A, "F11")); list.Add((0x7B, "F12"));
            list.Add((0x2C, "PrtSc")); list.Add((0x2D, "Ins")); list.Add((0x2E, "Del")); list.Add((0x24, "Home")); list.Add((0x21, "PgUp"));
            list.Add((0x22, "PgDn")); list.Add((0x23, "End"));
            list.Add((0x60, "Numpad0")); list.Add((0x61, "Numpad1")); list.Add((0x62, "Numpad2")); list.Add((0x63, "Numpad3"));
            list.Add((0x64, "Numpad4")); list.Add((0x65, "Numpad5")); list.Add((0x66, "Numpad6")); list.Add((0x67, "Numpad7"));
            list.Add((0x68, "Numpad8")); list.Add((0x69, "Numpad9")); list.Add((0x6E, "NumpadDecimal")); list.Add((0x6B, "NumpadPlus"));
            list.Add((0x6D, "NumpadMinus")); list.Add((0x6A, "NumpadMul")); list.Add((0x6F, "NumpadDiv"));
            return list.ToArray();
        }

        private static int[] BuildVkMap()
        {
            // vk_to_keymap_index: VK code -> KEYMAP index (-1 unmapped). Letters/digits
            // map by single-char name (2781), specials by the table above.
            var map = new int[256];
            Array.Fill(map, -1);
            foreach (var (vk, name) in BuildVkTable())
            {
                for (int i = 0; i < KeyCount; i++)
                    if (KeyMap[i].Name == name) { map[vk] = i; break; }
            }
            for (int vk = 'A'; vk <= 'Z'; vk++)
            {
                for (int i = 0; i < KeyCount; i++)
                    if (KeyMap[i].Name.Length == 1 && KeyMap[i].Name[0] == (char)vk) { map[vk] = i; break; }
            }
            for (int vk = '0'; vk <= '9'; vk++)
            {
                for (int i = 0; i < KeyCount; i++)
                    if (KeyMap[i].Name.Length == 1 && KeyMap[i].Name[0] == (char)vk) { map[vk] = i; break; }
            }
            return map;
        }

        private static int[] BuildTrackedVkList()
        {
            // fill_tracked_vk_list (2807-2821): A-Z, 0-9, then the specials in table
            // order.
            var list = new List<int>();
            for (int vk = 'A'; vk <= 'Z'; vk++) list.Add(vk);
            for (int vk = '0'; vk <= '9'; vk++) list.Add(vk);
            foreach (var (vk, _) in BuildVkTable()) list.Add(vk);
            return list.ToArray();
        }

        /// <summary>
        /// Polls the tracked VK list with GetAsyncKeyState (system-wide, exactly like
        /// the reference's effect threads) and records freshly-pressed keys into the
        /// state event buffer. Reactive/Ripple call this once per frame.
        /// </summary>
        private static void PollFreshPresses(BetterRgbFrameState st)
        {
            st.EventCount = 0;
            foreach (var vk in TrackedVkList)
            {
                int idx = VkToKeyMapIndex[vk];
                if (idx < 0) continue;
                bool down = (GetAsyncKeyState(vk) & 0x8000) != 0;
                if (down && st.WasDown[idx] == 0 && st.EventCount < st.EventX.Length)
                {
                    st.EventX[st.EventCount] = KeyX[idx];
                    st.EventY[st.EventCount] = KeyY[idx];
                    st.EventIdx[st.EventCount] = idx;
                    st.EventCount++;
                }
                st.WasDown[idx] = down ? 1 : 0;
            }
        }
    }
}
