using UCU.Core.Ec;

namespace UCU.Core.Tests.Ec;

public sealed class EcWordCodecTests
{
    [Fact]
    public void CombineBigEndian_FirstByteIsTheHighByte()
    {
        // Main fan RPM: the register named "_LO" (0x0464) carries the high byte,
        // which is why the pairing helper is named by byte order, not by address.
        Assert.Equal(0x1234, EcWordCodec.CombineBigEndian(0x12, 0x34));
    }

    [Fact]
    public void CombineLittleEndian_SecondByteIsTheHighByte()
    {
        // Battery block is little-endian: low byte at the base register.
        Assert.Equal(0x3412, EcWordCodec.CombineLittleEndian(0x12, 0x34));
    }

    [Theory]
    [InlineData(2981, 24)]   // 298.1 K = 24.95 °C, truncated toward zero
    [InlineData(2731, 0)]    // 273.1 K = -0.05 °C, truncated toward zero
    [InlineData(0, -273)]    // unreadable sensor reports 0; UI renders "unknown"
    [InlineData(3731, 99)]   // 373.1 K = 99.95 °C
    [InlineData(3001, 26)]   // 300.1 K = 26.95 °C
    public void KelvinTenthsToCelsius_TruncatesLikeTheDashboard(int kelvinTenths, int expected)
    {
        Assert.Equal(expected, EcWordCodec.KelvinTenthsToCelsius(kelvinTenths));
    }

    [Theory]
    [InlineData(5000, 5000, 100)]
    [InlineData(2500, 5000, 50)]
    [InlineData(5200, 5000, 104)]
    [InlineData(4999, 5000, 99)]
    public void ComputeHealthPercent_IsFullOverDesign(int fullMah, int designMah, int expected)
    {
        Assert.Equal(expected, EcWordCodec.ComputeHealthPercent(fullMah, designMah));
    }

    [Fact]
    public void ComputeHealthPercent_ZeroDesignCapacityMeansNoData()
    {
        Assert.Equal(0, EcWordCodec.ComputeHealthPercent(4000, 0));
    }

    [Fact]
    public void ComputeHealthPercent_UsesLongMathSoLargeCapacitiesDoNotOverflow()
    {
        int health = EcWordCodec.ComputeHealthPercent(20_000_000, 20_000_000);

        Assert.Equal(100, health);
    }

    [Theory]
    [InlineData(0x01, true)]
    [InlineData(0x00, false)]
    [InlineData(0x03, true)]
    [InlineData(0x02, false)]
    public void IsAcPowered_ReadsBitZero(byte powerSource, bool expected)
    {
        Assert.Equal(expected, EcWordCodec.IsAcPowered(powerSource));
    }
}
