using UCU.Core.Power;

namespace UCU.Core.Tests.Power;

/// <summary>
/// The battery CPU-envelope clamp (U-206 follow-up, field-measured
/// 2026-11-02). On DC the firmware caps package power at the Office level and
/// punishes a higher request by dropping below it (160 W requested -> 30 W
/// actual, vs 40 W at the Office preset). The policy therefore clamps the
/// request on battery and leaves AC untouched.
/// </summary>
public sealed class BatteryPowerPolicyTests
{
    [Fact]
    public void OnAc_RequestPassesThrough()
    {
        var verdict = BatteryPowerPolicy.ClampForBattery(160, 200, onBattery: false, officePl1: 45, officePl2: 45);

        Assert.False(verdict.Clamped);
        Assert.Equal(160, verdict.Pl1);
        Assert.Equal(200, verdict.Pl2);
    }

    [Fact]
    public void OnBattery_HighRequestIsClampedToTheOfficeEnvelope()
    {
        var verdict = BatteryPowerPolicy.ClampForBattery(160, 200, onBattery: true, officePl1: 40, officePl2: 40);

        Assert.True(verdict.Clamped);
        Assert.Equal(40, verdict.Pl1);
        Assert.Equal(40, verdict.Pl2);
        Assert.Equal(160, verdict.RequestedPl1);
        Assert.Equal(200, verdict.RequestedPl2);
    }

    [Fact]
    public void OnBattery_RequestBelowTheEnvelopeIsKept()
    {
        var verdict = BatteryPowerPolicy.ClampForBattery(30, 35, onBattery: true, officePl1: 40, officePl2: 40);

        Assert.False(verdict.Clamped);
        Assert.Equal(30, verdict.Pl1);
        Assert.Equal(35, verdict.Pl2);
    }

    [Fact]
    public void OnBattery_MixedRequestClampsOnlyWhatExceeds()
    {
        var verdict = BatteryPowerPolicy.ClampForBattery(35, 120, onBattery: true, officePl1: 40, officePl2: 40);

        Assert.True(verdict.Clamped);
        Assert.Equal(35, verdict.Pl1);
        Assert.Equal(40, verdict.Pl2);
    }

    [Fact]
    public void UnknownSkuEnvelope_FallsBackToTheDefaults()
    {
        var verdict = BatteryPowerPolicy.ClampForBattery(160, 160, onBattery: true, officePl1: 0, officePl2: 0);

        Assert.Equal(BatteryPowerPolicy.DefaultOfficePl1, verdict.Pl1);
        Assert.Equal(BatteryPowerPolicy.DefaultOfficePl2, verdict.Pl2);
    }
}
