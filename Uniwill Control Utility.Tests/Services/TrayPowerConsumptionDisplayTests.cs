using Uniwill_Control_Utility.Services;

namespace Uniwill_Control_Utility.Tests.Services;

public sealed class TrayPowerConsumptionDisplayTests
{
    [Fact]
    public void Create_FormatsLiveCpuAndGpuConsumption()
    {
        TrayPowerConsumptionDisplay display = TrayPowerConsumptionDisplay.Create(
            cpuPowerWatts: 13,
            gpuPowerWatts: 21);

        Assert.Equal("13 W", display.CpuTdp);
        Assert.Equal("21 W", display.GpuTgp);
    }

    [Fact]
    public void Create_ShowsUnavailableLiveConsumptionWithoutInventingValues()
    {
        TrayPowerConsumptionDisplay display = TrayPowerConsumptionDisplay.Create(
            cpuPowerWatts: 0,
            gpuPowerWatts: 0);

        Assert.Equal("-- W", display.CpuTdp);
        Assert.Equal("-- W", display.GpuTgp);
    }
}
