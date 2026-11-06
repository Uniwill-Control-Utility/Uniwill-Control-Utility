using UCU.Core.Ec;

namespace UCU.Core.Tests.Ec;

public sealed class EcDutyCodecTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(50, 100)]
    [InlineData(100, 200)]
    [InlineData(-5, 0)]
    [InlineData(120, 200)]
    public void PercentToEcDuty_ScalesOntoTheEcRange(int percent, int expected)
    {
        Assert.Equal(expected, EcDutyCodec.PercentToEcDuty(percent));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(100, 50)]
    [InlineData(200, 100)]
    [InlineData(255, 100)]
    public void EcDutyToPercent_IsTheInverseOfPercentToEcDuty(int duty, int expected)
    {
        Assert.Equal(expected, EcDutyCodec.EcDutyToPercent(duty));
    }

    [Fact]
    public void BuildDutyTable_ZoneZeroIsAlwaysOff()
    {
        byte[] table = EcDutyCodec.BuildDutyTable(new[] { 100, 100, 100 });

        Assert.Equal(0, table[0]);
        Assert.Equal(200, table[1]);
        Assert.Equal(200, table[2]);
    }

    [Fact]
    public void BuildDutyTable_PadsTrailingZonesWithTheCurveMax()
    {
        byte[] table = EcDutyCodec.BuildDutyTable(new List<int> { 0, 20, 25, 30, 38, 43, 48, 53, 58, 58, 58 });

        Assert.Equal(116, table[10]);
        for (int zone = EcDutyCodec.FirstPaddingZone; zone < EcDutyCodec.TableLength; zone++)
            Assert.Equal(116, table[zone]);
    }

    [Fact]
    public void BuildDutyTable_MatchesTheAppModelConversion()
    {
        // The app's EcFanCurve.ToEcDutyTable() is the behaviour being centralised;
        // the parity test in the app suite pins them together, this one pins the
        // documented shape (11 zones + 5 padding, 0-200 scale).
        byte[] table = EcDutyCodec.BuildDutyTable(new[] { 0, 30, 32, 36, 40, 47, 55, 62, 72, 84, 100 });

        Assert.Equal(EcDutyCodec.TableLength, table.Length);
        Assert.Equal(new byte[] { 0, 60, 64, 72, 80, 94, 110, 124, 144, 168, 200 },
            table[..EcDutyCodec.FirstPaddingZone]);
    }

    [Fact]
    public void BuildDutyTable_RejectsNull()
    {
        Assert.Throws<ArgumentNullException>(() => EcDutyCodec.BuildDutyTable(null!));
    }

    [Fact]
    public void ScaleDutyGradient_ScalesProportionallyToTheOemGradientMax()
    {
        byte[] oem = { 0, 20, 40, 60, 80, 100, 120, 140, 160, 180 };

        byte[] full = EcDutyCodec.ScaleDutyGradient(oem, 180);
        byte[] half = EcDutyCodec.ScaleDutyGradient(oem, 90);

        Assert.Equal(0, full[0]);
        Assert.Equal(180, full[9]);
        Assert.Equal(0, half[0]);
        Assert.Equal(90, half[9]);
        Assert.Equal(40, half[4]);
    }

    [Fact]
    public void ScaleDutyGradient_KeepsMonotonicallyNonDecreasing()
    {
        // Aggressive target on a flat gradient: rounding must not dip below the
        // previous zone, which the EC documents as a requirement.
        byte[] oem = { 0, 180, 1, 1, 1, 1, 1, 1, 1, 180 };

        byte[] scaled = EcDutyCodec.ScaleDutyGradient(oem, 100);

        for (int i = 1; i < EcDutyCodec.FirstPaddingZone; i++)
            Assert.True(scaled[i] >= scaled[i - 1], $"zone {i} dipped below zone {i - 1}");
    }

    [Fact]
    public void ScaleDutyGradient_PadsWithTargetWhenBelowTheOemMax()
    {
        byte[] oem = { 0, 20, 40, 60, 80, 100, 120, 140, 160, 180 };

        byte[] scaled = EcDutyCodec.ScaleDutyGradient(oem, 120);

        Assert.Equal(120, scaled[EcDutyCodec.FirstPaddingZone]);
        Assert.Equal(120, scaled[15]);
    }

    [Fact]
    public void ScaleDutyGradient_PadsWithTheOemMaxWhenTargetIsAtOrAboveIt()
    {
        byte[] oem = { 0, 20, 40, 60, 80, 100, 120, 140, 160, 180 };

        byte[] scaled = EcDutyCodec.ScaleDutyGradient(oem, 200);

        Assert.Equal(EcDutyCodec.OemGradientMax, scaled[EcDutyCodec.FirstPaddingZone]);
    }

    [Fact]
    public void ScaleDutyGradient_RejectsNull()
    {
        Assert.Throws<ArgumentNullException>(() => EcDutyCodec.ScaleDutyGradient(null!, 100));
    }

    [Fact]
    public void ValidateDutyPercent_RejectsOutsideTheUiScale()
    {
        EcDutyCodec.ValidateDutyPercent(0);
        EcDutyCodec.ValidateDutyPercent(100);

        Assert.Throws<ArgumentOutOfRangeException>(() => EcDutyCodec.ValidateDutyPercent(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => EcDutyCodec.ValidateDutyPercent(101));
    }

    [Fact]
    public void ValidateDutyTable_RejectsNonMonotonicAndOverRange()
    {
        EcDutyCodec.ValidateDutyTable(new byte[] { 0, 50, 50, 100, 100, 100, 100, 100, 100, 100, 200 });

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            EcDutyCodec.ValidateDutyTable(new byte[] { 0, 100, 50 }));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            EcDutyCodec.ValidateDutyTable(new byte[] { 0, 201 }));

        Assert.Throws<ArgumentNullException>(() => EcDutyCodec.ValidateDutyTable(null!));
    }
}
