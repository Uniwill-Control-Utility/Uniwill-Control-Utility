using UCU.Core.Supervision;

namespace UCU.Core.Tests.Supervision;

/// <summary>
/// The thermal failsafe rule (US-11). Both directions cost something real: an eager trip
/// silently cuts performance, an eager release lets the GPU cycle between full and
/// minimum power every few seconds.
/// </summary>
public sealed class ThermalFailsafePolicyTests
{
    private const int Threshold = 88;

    [Theory]
    [InlineData(88, ThermalFailsafeDecision.Trip)]
    [InlineData(95, ThermalFailsafeDecision.Trip)]
    [InlineData(87, ThermalFailsafeDecision.None)]
    [InlineData(40, ThermalFailsafeDecision.None)]
    public void NotYetTripped_TripsAtOrAboveTheThreshold(int temperature, ThermalFailsafeDecision expected)
    {
        Assert.Equal(expected, ThermalFailsafePolicy.Decide(temperature, Threshold, tripped: false));
    }

    [Theory]
    [InlineData(83, ThermalFailsafeDecision.Clear)]  // exactly threshold - 5
    [InlineData(70, ThermalFailsafeDecision.Clear)]
    [InlineData(84, ThermalFailsafeDecision.None)]   // one degree too warm to release
    [InlineData(88, ThermalFailsafeDecision.None)]
    [InlineData(95, ThermalFailsafeDecision.None)]   // still hot: stay held, never re-trip
    public void Tripped_ReleasesOnlyAfterTheHysteresisBand(int temperature, ThermalFailsafeDecision expected)
    {
        Assert.Equal(expected, ThermalFailsafePolicy.Decide(temperature, Threshold, tripped: true));
    }

    [Fact]
    public void UnknownTemperature_NeverActs()
    {
        Assert.Equal(ThermalFailsafeDecision.None, ThermalFailsafePolicy.Decide(null, Threshold, tripped: false));
        Assert.Equal(ThermalFailsafeDecision.None, ThermalFailsafePolicy.Decide(null, Threshold, tripped: true));
    }

    [Theory]
    [InlineData(0)]   // a disabled channel
    [InlineData(110)] // an impossible reading that slipped past the plausibility gate
    public void AnonymousButOutOfBandThreshold_NeverActs(int temperature)
    {
        Assert.Equal(ThermalFailsafeDecision.None, ThermalFailsafePolicy.Decide(temperature, threshold: 0, tripped: false));
        Assert.Equal(ThermalFailsafeDecision.None, ThermalFailsafePolicy.Decide(temperature, threshold: 200, tripped: false));
    }

    [Theory]
    [InlineData(70, true)]
    [InlineData(88, true)]
    [InlineData(95, true)]
    [InlineData(69, false)]
    [InlineData(96, false)]
    public void Thresholds_AreBoundedToTheUsableBand(int threshold, bool valid)
    {
        Assert.Equal(valid, ThermalFailsafePolicy.IsValidThreshold(threshold));
    }

    [Fact]
    public void TheDefaultThresholdIsInsideTheBand()
    {
        Assert.True(ThermalFailsafePolicy.IsValidThreshold(ThermalFailsafePolicy.DefaultThresholdCelsius));
        Assert.True(ThermalFailsafePolicy.DefaultThresholdCelsius < 105); // below the junction limit
    }
}
