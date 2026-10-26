using UCU.Core.Ec;

namespace UCU.Core.Tests.Ec;

public sealed class EcPowerCodecTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(125, 125)]
    [InlineData(255, 255)]
    [InlineData(-10, 0)]
    [InlineData(400, 255)]
    public void EncodePowerLimit_MapsWattsOntoTheEcByte(int watts, int expected)
    {
        Assert.Equal((byte)expected, EcPowerCodec.EncodePowerLimit(watts));
    }

    [Fact]
    public void ValidatePowerLimit_RejectsWhatTheEncoderWouldClamp()
    {
        EcPowerCodec.ValidatePowerLimit(0);
        EcPowerCodec.ValidatePowerLimit(255);

        Assert.Throws<ArgumentOutOfRangeException>(() => EcPowerCodec.ValidatePowerLimit(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => EcPowerCodec.ValidatePowerLimit(256));
    }

    [Fact]
    public void ValidateTccOffset_BoundsAtTheEcRange()
    {
        EcPowerCodec.ValidateTccOffset(0);
        EcPowerCodec.ValidateTccOffset(255);

        Assert.Throws<ArgumentOutOfRangeException>(() => EcPowerCodec.ValidateTccOffset(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => EcPowerCodec.ValidateTccOffset(256));
    }

    [Fact]
    public void EncodePerformanceMode_PreservesFirmwareBits_AndArmsTheEngine()
    {
        // 0b1010_0001: high bits 0xA0 belong to the firmware.
        const byte previous = 0xA1;

        for (int mode = 0; mode < EcPowerCodec.PerformanceModeCount; mode++)
        {
            byte written = EcPowerCodec.EncodePerformanceMode(previous, mode);

            Assert.Equal(previous & EcPowerCodec.ModeIndexHighBitsMask, written & EcPowerCodec.ModeIndexHighBitsMask);
            Assert.Equal(mode, written & 0x0F);

            // Writing a bare mode value disarms the performance-button engine, so
            // every write has to carry the arm bit.
            Assert.NotEqual(0, written & EcPowerCodec.PerformanceModeArmBit);
        }
    }

    [Fact]
    public void EncodePerformanceMode_IndexOverwritesTheLowBit()
    {
        // The mode index owns bits 0-1, so the register's low bit is consumed here —
        // unlike the stock-preset write (EncodeStockModeIndex), which carries it over.
        // Pinned so the two encoders are not accidentally merged.
        Assert.Equal(0x00, EcPowerCodec.EncodePerformanceMode(0xA1, 0) & EcPowerCodec.ModeIndexLowBit);
        Assert.Equal(0x01, EcPowerCodec.EncodeStockModeIndex(0xA1, EcPowerCodec.StockModeBalance) & EcPowerCodec.ModeIndexLowBit);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(4, 3)]
    [InlineData(99, 3)]
    public void EncodePerformanceMode_ClampsToTheOemModeSet(int mode, int expected)
    {
        Assert.Equal(expected, EcPowerCodec.EncodePerformanceMode(0x00, mode) & 0x0F);
    }

    [Fact]
    public void ValidatePerformanceMode_RejectsIndicesTheFirmwareDoesNotDefine()
    {
        for (int mode = 0; mode < EcPowerCodec.PerformanceModeCount; mode++)
            EcPowerCodec.ValidatePerformanceMode(mode);

        Assert.Throws<ArgumentOutOfRangeException>(() => EcPowerCodec.ValidatePerformanceMode(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => EcPowerCodec.ValidatePerformanceMode(4));
    }

    [Fact]
    public void DecodePerformanceMode_MasksTheModeIndex()
    {
        Assert.Equal(2, EcPowerCodec.DecodePerformanceMode(0xA2));
        Assert.Equal(1, EcPowerCodec.DecodePerformanceMode(0x11));
    }

    [Fact]
    public void StockModeEncoders_ClearTheCustomProfileDiscriminators()
    {
        // Stock presets own no custom profile: with those bits set, the firmware
        // keeps the user's table and the stock preset silently does nothing.
        Assert.Equal(0x00, EcPowerCodec.EncodeStockPowerGate(0x80));
        Assert.Equal(0x00, EcPowerCodec.EncodeStockCustomProfile(0x40));
        Assert.Equal(0x01, EcPowerCodec.EncodeStockPowerGate(0x81));
    }

    [Theory]
    [InlineData(EcPowerCodec.StockModeOffice, EcPowerCodec.FanProfileOffice)]
    [InlineData(EcPowerCodec.StockModeBalance, EcPowerCodec.FanProfileBalance)]
    [InlineData(EcPowerCodec.StockModeBeast, EcPowerCodec.FanProfileBeast)]
    public void EncodeStockFanProfile_WritesTheOemLiteral(int modeCase, byte expected)
    {
        Assert.Equal(expected, EcPowerCodec.EncodeStockFanProfile(modeCase));
    }

    [Theory]
    [InlineData(EcPowerCodec.StockModeOffice, 0xA0, false)]
    [InlineData(EcPowerCodec.StockModeBalance, 0xA0, false)]
    [InlineData(EcPowerCodec.StockModeBeast, 0xA0, true)]
    public void EncodeStockModeIndex_ForcesTurboForBeastOnly(int modeCase, byte previousRaw, bool expectTurbo)
    {
        byte written = EcPowerCodec.EncodeStockModeIndex(previousRaw, modeCase);

        Assert.Equal(expectTurbo, (written & EcPowerCodec.FanTurboBit) != 0);
        Assert.Equal(previousRaw & EcPowerCodec.ModeIndexHighBitsMask,
            written & EcPowerCodec.ModeIndexHighBitsMask);
    }

    [Fact]
    public void EncodeStockModeIndex_PreservesTheLowBit()
    {
        Assert.Equal(0x01, EcPowerCodec.EncodeStockModeIndex(0x01, EcPowerCodec.StockModeBalance));
        Assert.Equal(0x11, EcPowerCodec.EncodeStockModeIndex(0x11, EcPowerCodec.StockModeBeast));
    }

    [Theory]
    [InlineData(EcPowerCodec.StockModeOffice, 0x28)]
    [InlineData(EcPowerCodec.StockModeBalance, 0x20)]
    [InlineData(EcPowerCodec.StockModeBeast, 0x20)]
    public void EncodeStockTrigger_MatchesTheOemLiteral(int modeCase, int expected)
    {
        // The OEM keeps bit 4 of the previous trigger read; tests use 0 and 0x10.
        Assert.Equal(expected, EcPowerCodec.EncodeStockTrigger(0x00, modeCase));
        Assert.Equal(expected | 0x10, EcPowerCodec.EncodeStockTrigger(0x10, modeCase));
    }

    [Theory]
    [InlineData(EcPowerCodec.StockModeOffice, 1)]
    [InlineData(EcPowerCodec.StockModeBalance, 0)]
    [InlineData(EcPowerCodec.StockModeBeast, 0)]
    public void EncodeStockModeFlag_OfficeIsTheOnlyOneThatSetsIt(int modeCase, int expected)
    {
        Assert.Equal((byte)expected, EcPowerCodec.EncodeStockModeFlag(modeCase));
    }

    [Fact]
    public void ValidateStockMode_RejectsCustomAndUnknownIndices()
    {
        EcPowerCodec.ValidateStockMode(EcPowerCodec.StockModeOffice);
        EcPowerCodec.ValidateStockMode(EcPowerCodec.StockModeBalance);
        EcPowerCodec.ValidateStockMode(EcPowerCodec.StockModeBeast);

        Assert.Throws<ArgumentOutOfRangeException>(() => EcPowerCodec.ValidateStockMode(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => EcPowerCodec.ValidateStockMode(3));
    }

    [Fact]
    public void StockModeCommit_IsTheLastWriteOfEveryOemBody()
    {
        Assert.Equal(0x83, EcPowerCodec.StockModeCommit);
    }
}
