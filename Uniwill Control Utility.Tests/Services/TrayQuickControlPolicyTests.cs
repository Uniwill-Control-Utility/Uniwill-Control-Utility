using Uniwill_Control_Utility.Services;

namespace Uniwill_Control_Utility.Tests.Services;

public sealed class TrayQuickControlPolicyTests
{
    [Fact]
    public void Create_HidesOptionalHardwareAndKeepsIndependentControlsAvailable()
    {
        var policy = TrayQuickControlPolicy.Create(
            hasNvidiaGpu: false,
            hasSupportedWaterCooler: false,
            waterCoolerConnected: false,
            adaptiveCpuEnabled: false,
            adaptiveGpuEnabled: false,
            adaptiveFanEnabled: false,
            adaptiveWaterCoolerEnabled: false);

        Assert.False(policy.ShowTgp);
        Assert.False(policy.ShowHydro);
        Assert.True(policy.PresetEnabled);
        Assert.True(policy.TdpEnabled);
        Assert.True(policy.LocalDimmingEnabled);
    }

    [Fact]
    public void Create_KeepsLocalDimmingAvailableAsADisplayToggle()
    {
        var policy = TrayQuickControlPolicy.Create(
            hasNvidiaGpu: true,
            hasSupportedWaterCooler: true,
            waterCoolerConnected: true,
            adaptiveCpuEnabled: true,
            adaptiveGpuEnabled: false,
            adaptiveFanEnabled: true,
            adaptiveWaterCoolerEnabled: true);

        Assert.True(policy.ShowTgp);
        Assert.True(policy.ShowHydro);
        Assert.False(policy.PresetEnabled);
        Assert.False(policy.TdpEnabled);
        Assert.True(policy.TgpEnabled);
        Assert.True(policy.LocalDimmingEnabled);
        Assert.False(policy.HydroEnabled);
    }
}
