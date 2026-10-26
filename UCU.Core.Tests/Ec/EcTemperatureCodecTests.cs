using UCU.Core.Ec;

namespace UCU.Core.Tests.Ec;

public sealed class EcTemperatureCodecTests
{
    [Fact]
    public void ToEcTempUp_DefaultSteps_ZoneZeroIsZero_AndOthersAreStepPlusOne()
    {
        byte[] up = EcTemperatureCodec.ToEcTempUp(EcTemperatureCodec.DefaultTemperatureSteps);

        Assert.Equal(EcTemperatureCodec.TableLength, up.Length);
        Assert.Equal(0, up[0]);
        Assert.Equal(56, up[1]);
        Assert.Equal(61, up[2]);
        Assert.Equal(96, up[9]);
        Assert.Equal(98, up[10]);
    }

    [Fact]
    public void ToEcTempUp_PadsReservedZonesWithInactiveSentinel()
    {
        byte[] up = EcTemperatureCodec.ToEcTempUp(EcTemperatureCodec.DefaultTemperatureSteps);

        Assert.Equal(EcTemperatureCodec.InactivePadding, up[11]);
        Assert.Equal(EcTemperatureCodec.InactivePadding, up[15]);
    }

    [Fact]
    public void ToEcTempDown_ZoneZeroDropsTwo_OffZoneFloorIsZero()
    {
        byte[] down = EcTemperatureCodec.ToEcTempDown(EcTemperatureCodec.DefaultTemperatureSteps);

        Assert.Equal(0, down[0]);

        byte[] fromZero = EcTemperatureCodec.ToEcTempDown(new[] { 1, 55 });
        Assert.Equal(0, down[0]);
        Assert.Equal(0, fromZero[0]);
        Assert.Equal(54, fromZero[1]);
    }

    [Fact]
    public void ToEcTempDown_DefaultSteps_HysteresisIsOneBelowStep()
    {
        byte[] down = EcTemperatureCodec.ToEcTempDown(EcTemperatureCodec.DefaultTemperatureSteps);

        Assert.Equal(54, down[1]);
        Assert.Equal(94, down[9]);
        Assert.Equal(96, down[10]);
    }

    [Fact]
    public void Encoders_ClampAboveTheEcRange_WithoutThrowing()
    {
        // Parity with the historical in-app encoders: the codec clamps, the
        // validator is what rejects. See ValidateTemperatureSteps tests below.
        int[] hot = { 0, 300, 400 };

        Assert.Equal(255, EcTemperatureCodec.ToEcTempUp(hot)[1]);
        Assert.Equal(255, EcTemperatureCodec.ToEcTempDown(hot)[1]);
    }

    [Fact]
    public void Encoders_IgnoreStepsBeyondTheActiveZones()
    {
        int[] longInput = { 0, 55, 60, 65, 70, 75, 80, 85, 90, 95, 97, 99, 101 };

        byte[] up = EcTemperatureCodec.ToEcTempUp(longInput);

        Assert.Equal(98, up[10]);
        Assert.Equal(EcTemperatureCodec.InactivePadding, up[11]);
    }

    [Fact]
    public void Encoders_RejectNull()
    {
        Assert.Throws<ArgumentNullException>(() => EcTemperatureCodec.ToEcTempUp(null!));
        Assert.Throws<ArgumentNullException>(() => EcTemperatureCodec.ToEcTempDown(null!));
        Assert.Throws<ArgumentNullException>(() => EcTemperatureCodec.ValidateTemperatureSteps(null!));
    }

    [Fact]
    public void Validate_DefaultSteps_AreAccepted()
    {
        EcTemperatureCodec.ValidateTemperatureSteps(EcTemperatureCodec.DefaultTemperatureSteps);
    }

    [Fact]
    public void Validate_RejectsEmptyAndNonZeroOffZone()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => EcTemperatureCodec.ValidateTemperatureSteps(Array.Empty<int>()));
        Assert.Throws<ArgumentOutOfRangeException>(() => EcTemperatureCodec.ValidateTemperatureSteps(new[] { 5, 55, 60 }));
    }

    [Fact]
    public void Validate_RejectsOutOfRangeAndNonAscendingZones()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            EcTemperatureCodec.ValidateTemperatureSteps(new[] { 0, 55, 300 }));

        // A plateau makes the remaining zones unreachable for the EC's ascending walk.
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            EcTemperatureCodec.ValidateTemperatureSteps(new[] { 0, 55, 55 }));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            EcTemperatureCodec.ValidateTemperatureSteps(new[] { 0, 60, 55 }));
    }

    [Fact]
    public void DefaultTemperatureSteps_AreTheXmgNinePointScale()
    {
        Assert.Equal(new[] { 0, 55, 60, 65, 70, 75, 80, 85, 90, 95, 97 },
            EcTemperatureCodec.DefaultTemperatureSteps);
    }
}
