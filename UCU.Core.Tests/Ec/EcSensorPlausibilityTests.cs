using UCU.Core.Ec;

namespace UCU.Core.Tests.Ec;

/// <summary>
/// Rules for raw EC sensor bytes. 0xFF arrives while the EC reinitialises, 0 arrives for
/// a channel it has switched off, and either would otherwise read as a real measurement
/// (250 °C on the dashboard, a phantom 65 535 rpm).
/// </summary>
public sealed class EcSensorPlausibilityTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(45, 45)]
    [InlineData(110, 110)]
    [InlineData(0, -1)]     // no reading (channel switched off), not "0 °C"
    [InlineData(111, -1)]   // above every junction limit these ECs expose
    [InlineData(250, -1)]
    [InlineData(255, -1)]   // the reinit sentinel
    public void Temperature_RejectsEverythingOutsideThePlausibleBand(int raw, int expected)
    {
        Assert.Equal(expected, EcSensorPlausibility.Temperature(raw));
    }

    [Theory]
    [InlineData(0, 0)]      // a stopped fan is a real reading
    [InlineData(1300, 1300)]
    [InlineData(8000, 8000)]
    [InlineData(8001, -1)]
    [InlineData(65535, -1)] // 0xFFFF from a torn tach read
    [InlineData(-5, -1)]
    public void Rpm_AcceptsZeroButRejectsTornOrImpossibleValues(int rpm, int expected)
    {
        Assert.Equal(expected, EcSensorPlausibility.Rpm(rpm));
    }

    [Fact]
    public void TheUnknownSentinelIsNonPositive_SoEveryConsumerRendersItAsMissing()
    {
        Assert.True(EcSensorPlausibility.Unknown <= 0);
    }
}
