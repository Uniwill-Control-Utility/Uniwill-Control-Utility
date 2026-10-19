using Uniwill_Control_Utility.Services;
using Uniwill_Control_Utility.ViewModels;

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
            adaptiveWaterCoolerEnabled: false,
            adaptiveKeyboardEnabled: false,
            adaptiveLightBarEnabled: false,
            lightBarSyncActive: false,
            adaptiveFlydigiEnabled: false,
            hasMiniLedPanel: false,
            flydigiDeviceAvailable: false,
            flydigiEnabled: false,
            refreshRateSwitchable: false,
            features: new FeatureNavigationPreferences(false, false, false, false, false));

        Assert.False(policy.ShowTgp);
        Assert.False(policy.ShowRefreshRate);
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
            adaptiveWaterCoolerEnabled: true,
            adaptiveKeyboardEnabled: false,
            adaptiveLightBarEnabled: false,
            lightBarSyncActive: false,
            adaptiveFlydigiEnabled: false,
            hasMiniLedPanel: false,
            flydigiDeviceAvailable: false,
            flydigiEnabled: false,
            refreshRateSwitchable: true,
            features: new FeatureNavigationPreferences(false, false, false, false, false));

        Assert.True(policy.ShowTgp);
        Assert.True(policy.ShowRefreshRate);
        Assert.True(policy.ShowHydro);
        Assert.False(policy.PresetEnabled);
        Assert.False(policy.TdpEnabled);
        Assert.True(policy.TgpEnabled);
        Assert.True(policy.LocalDimmingEnabled);
        Assert.False(policy.HydroEnabled);
    }

    [Fact]
    public void Create_DisablesLightBarWhileBetterRgbLightBarSyncOwnsTheStrip()
    {
        var policy = TrayQuickControlPolicy.Create(
            hasNvidiaGpu: true,
            hasSupportedWaterCooler: false,
            waterCoolerConnected: false,
            adaptiveCpuEnabled: false,
            adaptiveGpuEnabled: false,
            adaptiveFanEnabled: false,
            adaptiveWaterCoolerEnabled: false,
            adaptiveKeyboardEnabled: false,
            adaptiveLightBarEnabled: false,
            lightBarSyncActive: true,
            adaptiveFlydigiEnabled: false,
            hasMiniLedPanel: false,
            flydigiDeviceAvailable: false,
            flydigiEnabled: false,
            refreshRateSwitchable: false,
            features: new FeatureNavigationPreferences(false, false, false, false, false));

        Assert.True(policy.DisableLightBar);
    }
}
