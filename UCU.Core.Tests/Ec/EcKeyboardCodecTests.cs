using UCU.Core.Ec;

namespace UCU.Core.Tests.Ec;

public sealed class EcKeyboardCodecTests
{
    [Theory]
    [InlineData(255, 255, 255, EcKeyboardCodec.MaxChannelValue)]
    [InlineData(0, 0, 0, EcKeyboardCodec.MinChannelValue)]
    [InlineData(25, 10, 1, 1)]
    public void EncodeColor_ClampsToTheEcChannelScale(int r, int g, int b, int expectedBlue)
    {
        EcRgbColor color = EcKeyboardCodec.EncodeColor((byte)r, (byte)g, (byte)b);

        Assert.Equal((byte)expectedBlue, color.Blue);
        Assert.True(color.Red <= EcKeyboardCodec.MaxChannelValue);
        Assert.True(color.Green <= EcKeyboardCodec.MaxChannelValue);
    }

    [Fact]
    public void EncodeColor_AllZeroIsRaisedBecauseZeroMeansRestoreDefault()
    {
        EcRgbColor color = EcKeyboardCodec.EncodeColor(0, 0, 0);

        Assert.Equal(EcKeyboardCodec.MinChannelValue, color.Red);
        Assert.Equal(EcKeyboardCodec.MinChannelValue, color.Green);
        Assert.Equal(EcKeyboardCodec.MinChannelValue, color.Blue);
    }

    [Fact]
    public void EncodeColor_PassesThroughAColorInsideTheScale()
    {
        Assert.Equal(new EcRgbColor(10, 20, 30), EcKeyboardCodec.EncodeColor(10, 20, 30));
    }

    [Fact]
    public void ValidateColor_RejectsTheRangeTheEncoderClamps()
    {
        EcKeyboardCodec.ValidateColor(0, 0, 0);
        EcKeyboardCodec.ValidateColor(50, 50, 50);

        Assert.Throws<ArgumentOutOfRangeException>(() => EcKeyboardCodec.ValidateColor(51, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => EcKeyboardCodec.ValidateColor(0, 51, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => EcKeyboardCodec.ValidateColor(0, 0, 51));
    }

    [Fact]
    public void EncodeBrightness_PlacesTheLevelInTheHighBits_AndSetsApply()
    {
        for (int level = 0; level <= EcKeyboardCodec.MaxBrightness; level++)
        {
            byte written = EcKeyboardCodec.EncodeBrightness(0x00, level);

            Assert.Equal(level << EcKeyboardCodec.BrightnessShift,
                written & EcKeyboardCodec.BrightnessMask);
            Assert.NotEqual(0, written & EcKeyboardCodec.ApplyBit);
            Assert.Equal(level, EcKeyboardCodec.DecodeBrightness(written));
        }
    }

    [Fact]
    public void EncodeBrightness_PreservesTheOtherStatusBits()
    {
        // Bit 0 (white-only) and bit 1 (power-off) are firmware-reported state that
        // a brightness write must not clear.
        byte written = EcKeyboardCodec.EncodeBrightness(0x03, 4);

        Assert.Equal(0x03, written & 0x03);
        Assert.Equal(4, EcKeyboardCodec.DecodeBrightness(written));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(8)]
    public void EncodeBrightness_ClampsAndValidateRejects(int level)
    {
        int expected = Math.Clamp(level, 0, EcKeyboardCodec.MaxBrightness);
        Assert.Equal(expected, EcKeyboardCodec.DecodeBrightness(EcKeyboardCodec.EncodeBrightness(0, level)));
        Assert.Throws<ArgumentOutOfRangeException>(() => EcKeyboardCodec.ValidateBrightness(level));
    }

    [Fact]
    public void EncodePowerOff_SetsPowerOffAndApply()
    {
        byte written = EcKeyboardCodec.EncodePowerOff(0x00);

        Assert.NotEqual(0, written & EcKeyboardCodec.PowerOffBit);
        Assert.NotEqual(0, written & EcKeyboardCodec.ApplyBit);
    }

    [Fact]
    public void EncodePowerOn_ClearsPowerOffKeepingBrightnessAndApply()
    {
        byte written = EcKeyboardCodec.EncodePowerOn(0x82); // brightness 4 + power off

        Assert.Equal(0, written & EcKeyboardCodec.PowerOffBit);
        Assert.NotEqual(0, written & EcKeyboardCodec.ApplyBit);
        Assert.Equal(4, EcKeyboardCodec.DecodeBrightness(written));
    }

    [Theory]
    [InlineData(0x01, true)]
    [InlineData(0x00, false)]
    [InlineData(0xE3, true)]
    public void IsWhiteOnly_ReadsBitZero(byte status, bool expected)
    {
        Assert.Equal(expected, EcKeyboardCodec.IsWhiteOnly(status));
    }
}
