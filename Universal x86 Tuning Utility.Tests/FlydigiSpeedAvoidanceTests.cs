using Universal_x86_Tuning_Utility.Models;

namespace Universal_x86_Tuning_Utility.Tests;

public class FlydigiSpeedAvoidanceTests
{
    [Fact]
    public void Disabled_ReturnsTargetUnchanged()
    {
        var result = FlydigiSpeedAvoidance.Apply(2200, enabled: false, 2000, 2500, 50.0);
        Assert.Equal(2200, result);
    }

    [Fact]
    public void Enabled_TargetBelowRange_ReturnsUnchanged()
    {
        var result = FlydigiSpeedAvoidance.Apply(1500, enabled: true, 2000, 2500, 50.0);
        Assert.Equal(1500, result);
    }

    [Fact]
    public void Enabled_TargetInRange_JumpsToEndPlus100()
    {
        var result = FlydigiSpeedAvoidance.Apply(2200, enabled: true, 2000, 2500, 50.0);
        Assert.Equal(2600, result);
    }

    [Fact]
    public void Enabled_TargetAboveRange_ReturnsUnchanged()
    {
        var result = FlydigiSpeedAvoidance.Apply(3000, enabled: true, 2000, 2500, 50.0);
        Assert.Equal(3000, result);
    }

    [Fact]
    public void EmergencyBypass_At85C_ReturnsTarget()
    {
        var result = FlydigiSpeedAvoidance.Apply(2200, enabled: true, 2000, 2500, 85.0);
        Assert.Equal(2200, result);
    }

    [Fact]
    public void EmergencyBypass_At90C_ReturnsTarget()
    {
        var result = FlydigiSpeedAvoidance.Apply(2200, enabled: true, 2000, 2500, 90.0);
        Assert.Equal(2200, result);
    }

    [Fact]
    public void NoTemperature_Null_StillAppliesAvoidance()
    {
        var result = FlydigiSpeedAvoidance.Apply(2200, enabled: true, 2000, 2500, null);
        Assert.Equal(2600, result);
    }

    [Fact]
    public void EndPlus100_ClampedTo4000()
    {
        var result = FlydigiSpeedAvoidance.Apply(3950, enabled: true, 3900, 3950, 50.0);
        Assert.Equal(4000, result);
    }
}
