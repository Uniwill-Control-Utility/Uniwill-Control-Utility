using UCU.Core.Ec.Generated;

namespace UCU.Core.Ec
{
    /// <summary>
    /// Encodes the EC keyboard/backlight registers.
    ///
    /// Two quirks are encoded here because they are firmware facts, not UI choices:
    /// each RGB channel is 0-50 (not 0-255), and an all-zero color means "restore
    /// default" to the EC, so an all-zero request is raised to the dimmest
    /// non-zero color. Brightness, power-off and apply are bits of one status
    /// register, so every write is read-modify-write and takes the previous byte.
    /// </summary>
    public static class EcKeyboardCodec
    {
        /// <summary>Highest value of one RGB channel (the EC scale, not 0-255).</summary>
        public const int MaxChannelValue = 50;

        /// <summary>Lowest value the EC accepts on every channel of one color write.</summary>
        public const int MinChannelValue = 1;

        /// <summary>Highest backlight brightness level.</summary>
        public const int MaxBrightness = 7;

        /// <summary>Brightness bits of the keyboard status register (bits 5-7).</summary>
        public const byte BrightnessMask = EcVault.KBD_BRIGHTNESS_MASK;

        /// <summary>Shift from a brightness level to its position in the status register.</summary>
        public const int BrightnessShift = 5;

        /// <summary>Apply bit (bit 4): tells the EC to commit the status write.</summary>
        public const byte ApplyBit = EcVault.KBD_APPLY;

        /// <summary>Power-off bit (bit 1).</summary>
        public const byte PowerOffBit = EcVault.KBD_POWER_OFF;

        /// <summary>White-only keyboard bit (bit 0) of the status register.</summary>
        public const byte WhiteOnlyBit = EcVault.KBD_WHITE_ONLY;

        /// <summary>
        /// Clamps an RGB color to the EC scale and raises an all-zero color to the
        /// dimmest non-zero value, which is how the EC is told "set this color"
        /// without being told to restore its default.
        /// </summary>
        public static EcRgbColor EncodeColor(byte red, byte green, byte blue)
        {
            byte r = (byte)Math.Clamp((int)red, 0, MaxChannelValue);
            byte g = (byte)Math.Clamp((int)green, 0, MaxChannelValue);
            byte b = (byte)Math.Clamp((int)blue, 0, MaxChannelValue);

            if (r == 0 && g == 0 && b == 0)
            {
                r = MinChannelValue;
                g = MinChannelValue;
                b = MinChannelValue;
            }

            return new EcRgbColor(r, g, b);
        }

        /// <summary>Rejects a color outside the EC channel scale.</summary>
        public static void ValidateColor(byte red, byte green, byte blue)
        {
            if (red > MaxChannelValue || green > MaxChannelValue || blue > MaxChannelValue)
                throw new ArgumentOutOfRangeException(nameof(red),
                    $"RGB channels must be 0-{MaxChannelValue}, got ({red}, {green}, {blue}).");
        }

        /// <summary>Encodes a brightness write, preserving the other status bits and setting apply.</summary>
        public static byte EncodeBrightness(byte previousStatus, int brightness)
        {
            int clamped = Math.Clamp(brightness, 0, MaxBrightness);
            byte status = (byte)(previousStatus & ~BrightnessMask);
            return (byte)((status | (byte)(clamped << BrightnessShift)) | ApplyBit);
        }

        /// <summary>Reads the brightness level out of a status register value.</summary>
        public static int DecodeBrightness(byte status) => (status & BrightnessMask) >> BrightnessShift;

        /// <summary>Encodes a backlight-off write (power-off plus apply).</summary>
        public static byte EncodePowerOff(byte previousStatus) =>
            (byte)(previousStatus | PowerOffBit | ApplyBit);

        /// <summary>Encodes a backlight-on write (clear power-off, set apply).</summary>
        public static byte EncodePowerOn(byte previousStatus) =>
            (byte)((previousStatus & ~PowerOffBit) | ApplyBit);

        /// <summary>True when the status register reports a white-only (non-RGB) keyboard.</summary>
        public static bool IsWhiteOnly(byte status) => (status & WhiteOnlyBit) != 0;

        /// <summary>Rejects a brightness level outside the OEM range.</summary>
        public static void ValidateBrightness(int brightness)
        {
            if (brightness < 0 || brightness > MaxBrightness)
                throw new ArgumentOutOfRangeException(nameof(brightness),
                    $"Keyboard brightness must be 0-{MaxBrightness}, got {brightness}.");
        }
    }

    /// <summary>An EC keyboard color on the 0-50 per-channel scale.</summary>
    public readonly record struct EcRgbColor(byte Red, byte Green, byte Blue);
}
