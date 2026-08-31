using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;

namespace Uniwill_Control_Utility.Models
{
    /// <summary>
    /// Generates the 30-frame animation data and header (f0) for each RGB mode.
    /// Mirrors the frame-generation logic in THRM's rgb.go.
    /// </summary>
    public static class Bs2ProRgbFrameGenerator
    {
        /* ------------------------------------------------------------------ */
        /*  Public API                                                         */
        /* ------------------------------------------------------------------ */

        /// <summary>
        /// Generates the header frame (f0) and 30 animation frames for the specified mode.
        /// Returns a LightUploadData struct with the pre-computed frames.
        /// </summary>
        public static LightUploadData Generate(
            string mode, byte r, byte g, byte b, byte speed, byte brightness)
        {
            // Scale RGB by brightness factor
            double scale = brightness / 100.0;
            byte sr = (byte)Math.Min(255, Math.Round(r * scale));
            byte sg = (byte)Math.Min(255, Math.Round(g * scale));
            byte sb = (byte)Math.Min(255, Math.Round(b * scale));

            var colors = new[] { new RgbColor(sr, sg, sb) };

            return mode switch
            {
                "static" => GenerateStatic(colors, brightness),
                "rotation" => GenerateRotation(colors, brightness, speed),
                "flowing" => GenerateFlowing(brightness),
                "breathing" => GenerateBreathing(colors, brightness),
                _ => GenerateStatic(colors, brightness) // fallback
            };
        }

        /// <summary>
        /// Generates rotation animation with multiple colors (1-6).
        /// Colors are brightness-scaled internally.
        /// </summary>
        public static LightUploadData GenerateRotation(
            List<Color> colors, byte speed, byte brightness)
        {
            if (colors.Count == 0)
                colors.Add(Colors.White);

            double scale = brightness / 100.0;
            var rgbColors = colors.Take(6).Select(c =>
                new RgbColor(
                    (byte)Math.Min(255, Math.Round(c.R * scale)),
                    (byte)Math.Min(255, Math.Round(c.G * scale)),
                    (byte)Math.Min(255, Math.Round(c.B * scale)))
            ).ToArray();

            return GenerateRotation(rgbColors, brightness, speed);
        }

        /* ------------------------------------------------------------------ */
        /*  Header frame (f0) builder                                          */
        /* ------------------------------------------------------------------ */

        /// <summary>
        /// Builds the 10-byte header frame f0.
        /// Layout: [00, 02, 00, mode, speed, brightness, R, G, B, 00]
        /// </summary>
        private static byte[] BuildF0(byte modeCode, byte speed, byte brightness, byte r, byte g, byte b)
        {
            return new byte[]
            {
                0x00, 0x02, 0x00,
                modeCode,
                speed,
                brightness,
                r, g, b,
                0x00
            };
        }

        /* ------------------------------------------------------------------ */
        /*  Mode: Static                                                       */
        /* ------------------------------------------------------------------ */

        /// <summary>
        /// Static mode: frames at indices 2,5,8,11,14 get RGB values.
        /// All other frames are zero.  Mode code = 0x00.
        /// </summary>
        private static LightUploadData GenerateStatic(RgbColor[] colors, byte brightness)
        {
            var frames = new byte[30, 10];
            var targetIndices = new[] { 2, 5, 8, 11, 14 };

            for (int i = 0; i < targetIndices.Length; i++)
            {
                int fi = targetIndices[i];
                var color = colors[i % colors.Length];
                frames[fi, 6] = color.R;
                frames[fi, 7] = color.G;
                frames[fi, 8] = color.B;
            }

            var f0 = BuildF0(0x00, Bs2ProRgbSpeed.Medium, brightness, colors[0].R, colors[0].G, colors[0].B);
            return new LightUploadData { Header = f0, Frames = frames };
        }

        /* ------------------------------------------------------------------ */
        /*  Mode: Rotation                                                     */
        /* ------------------------------------------------------------------ */

        /// <summary>
        /// Rotation mode: builds a 304-byte color stream from 6 chunks of 10 positions,
        /// then distributes first 4 bytes to f0[6..9] and remaining 300 bytes to frames.
        /// Mode code = 0x05.
        /// </summary>
        private static LightUploadData GenerateRotation(RgbColor[] colors, byte brightness, byte speed)
        {
            int numColors = colors.Length;
            var stream = new byte[304];

            // Build 6 chunks × 10 positions × 3 bytes (RGB)
            for (int chunkIdx = 0; chunkIdx < 6; chunkIdx++)
            {
                int chunkStart = chunkIdx * 30;
                for (int p = 0; p < 10; p++)
                {
                    int colorIdx = (p + chunkIdx) % 6;
                    if (colorIdx < numColors)
                    {
                        stream[chunkStart + p * 3] = colors[colorIdx].R;
                        stream[chunkStart + p * 3 + 1] = colors[colorIdx].G;
                        stream[chunkStart + p * 3 + 2] = colors[colorIdx].B;
                    }
                }
            }

            // Distribute: first 4 bytes → f0[6..9], rest → frames[0..29][0..9]
            var f0 = BuildF0(0x05, speed, brightness, stream[0], stream[1], stream[2]);
            f0[6] = stream[0];
            f0[7] = stream[1];
            f0[8] = stream[2];
            f0[9] = stream[3];

            var frames = new byte[30, 10];
            int streamOffset = 4;
            for (int fi = 0; fi < 30; fi++)
            {
                for (int j = 0; j < 10; j++)
                {
                    frames[fi, j] = stream[streamOffset++];
                }
            }

            return new LightUploadData { Header = f0, Frames = frames };
        }

        /* ------------------------------------------------------------------ */
        /*  Mode: Flowing                                                      */
        /* ------------------------------------------------------------------ */

        /// <summary>
        /// Flowing mode: uses a hardcoded 9-frame template repeated across 30 frames.
        /// Each byte 0-8 is brightness-scaled; byte 9 is copied verbatim.
        /// f0 base color is green (0x00, 0xFF, 0x00).  Mode code = 0x05.
        /// </summary>
        private static LightUploadData GenerateFlowing(byte brightness)
        {
            double scale = brightness / 100.0;

            // 9 base templates from THRM rgb.go
            byte[][] flowingBase = new byte[][]
            {
                new byte[] { 0x7F, 0x7F, 0x00, 0xFF, 0x00, 0x7F, 0x7F, 0x00, 0xFF, 0x00 },
                new byte[] { 0x00, 0x7F, 0x00, 0x7F, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 },
                new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x7F, 0x7F, 0x00 },
                new byte[] { 0x00, 0x00, 0x00, 0xFF, 0x00, 0x7F, 0x7F, 0x00, 0xFF, 0x00 },
                new byte[] { 0x7F, 0x00, 0x00, 0xFF, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 },
                new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xFF, 0x00, 0x7F },
                new byte[] { 0x7F, 0x00, 0xFF, 0x00, 0x00, 0x7F, 0x00, 0x7F, 0x00, 0x00 },
                new byte[] { 0xFF, 0x00, 0x7F, 0x7F, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 },
                new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xFF, 0x00 },
            };

            var frames = new byte[30, 10];
            for (int fi = 0; fi < 30; fi++)
            {
                byte[] template = flowingBase[fi % 9];
                for (int j = 0; j < 10; j++)
                {
                    if (j < 9)
                        frames[fi, j] = (byte)Math.Min(255, Math.Round(template[j] * scale));
                    else
                        frames[fi, j] = template[j]; // byte 9 copied verbatim
                }
            }

            // f0 uses green base color
            var f0 = BuildF0(0x05, Bs2ProRgbSpeed.Medium, brightness, 0x00, 0xFF, 0x00);
            return new LightUploadData { Header = f0, Frames = frames };
        }

        /* ------------------------------------------------------------------ */
        /*  Mode: Breathing                                                    */
        /* ------------------------------------------------------------------ */

        /// <summary>
        /// Breathing mode: builds a 30-byte pattern from colors (each color = 6 bytes:
        /// R,G,B + 3 padding zeros), then repeats to fill a 304-byte stream.
        /// Distribution: first 4 bytes → f0[6..9], remaining 300 → frames.
        /// Mode code = numColors*2 - 1 (1 color → 0x01).
        /// </summary>
        private static LightUploadData GenerateBreathing(RgbColor[] colors, byte brightness)
        {
            int numColors = colors.Length;
            byte modeCode = (byte)(numColors * 2 - 1);

            // Build 30-byte pattern: each color = 6 bytes (R,G,B,0,0,0)
            var pattern = new byte[30];
            int pOffset = 0;
            for (int ci = 0; ci < numColors && pOffset + 6 <= 30; ci++)
            {
                pattern[pOffset++] = colors[ci].R;
                pattern[pOffset++] = colors[ci].G;
                pattern[pOffset++] = colors[ci].B;
                pattern[pOffset++] = 0;
                pattern[pOffset++] = 0;
                pattern[pOffset++] = 0;
            }

            // Fill 304-byte stream by repeating pattern
            var stream = new byte[304];
            for (int k = 0; k < 304; k++)
                stream[k] = pattern[k % 30];

            // Distribute
            var f0 = BuildF0(modeCode, Bs2ProRgbSpeed.Medium, brightness, stream[0], stream[1], stream[2]);
            f0[6] = stream[0];
            f0[7] = stream[1];
            f0[8] = stream[2];
            f0[9] = stream[3];

            var frames = new byte[30, 10];
            int streamOffset = 4;
            for (int fi = 0; fi < 30; fi++)
            {
                for (int j = 0; j < 10; j++)
                {
                    frames[fi, j] = stream[streamOffset++];
                }
            }

            return new LightUploadData { Header = f0, Frames = frames };
        }

        /* ------------------------------------------------------------------ */
        /*  Helper types                                                       */
        /* ------------------------------------------------------------------ */

        private readonly struct RgbColor
        {
            public byte R { get; }
            public byte G { get; }
            public byte B { get; }
            public RgbColor(byte r, byte g, byte b) { R = r; G = g; B = b; }
        }
    }

    /// <summary>
    /// Pre-computed light upload data: one header frame (f0) and 30 animation frames.
    /// </summary>
    public readonly struct LightUploadData
    {
        /// <summary>10-byte header frame (f0).</summary>
        public byte[] Header { get; init; }

        /// <summary>30 animation frames, each 10 bytes. Index 0..29 maps to frame indices 1..30.</summary>
        public byte[,] Frames { get; init; }
    }
}
