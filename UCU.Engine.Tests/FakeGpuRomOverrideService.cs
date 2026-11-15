using UCU.Contracts;
using UCU.Hardware.Gpu;

namespace UCU.Engine.Tests;

/// <summary>
/// The scripted ROM-override store the gpu.get_rom_override_state /
/// gpu.set_rom_override / gpu.get_rail_limits / gpu.set_rail_limits verbs answer with:
/// the wire is exercised without writing the display driver's HKLM key, restarting the
/// adapter or touching nvapi64.
/// </summary>
public sealed class FakeGpuRomOverrideService : IGpuRomOverrideService
{
    public bool? LastEnabled { get; private set; }

    public (int? Nvvdd, int? Msvdd, int? BoardInput)? LastRailSet { get; private set; }

    public GpuRomOverrideStateDto Answer { get; set; } = new()
    {
        State = GpuRomOverrideStates.NotUnlocked,
        TierId = "extra_gpu_power",
        PowerMilliwatts = 250_000,
        NvVddMilliAmps = 294_000,
        MsVddMilliAmps = 84_000,
        BoardInputMilliAmps = 14_026,
        NvvddCurrentMilliamps = 210_000,
        MsvddCurrentMilliamps = 60_000,
        BoardInputCurrentMilliamps = 11_053,
        CeilingLimitMilliwatts = 175_000,
        CurrentLimitMilliwatts = 95_000,
        BudgetMilliwatts = 150_000,
        RequestedLimitMilliwatts = null,
    };

    public GpuRailLimitsDto RailAnswer { get; set; } = new()
    {
        NvvddMilliamps = 250_000,
        NvvddMinMilliamps = 210_000,
        NvvddMaxMilliamps = 294_000,
        MsvddMilliamps = 70_000,
        MsvddMinMilliamps = 60_000,
        MsvddMaxMilliamps = 84_000,
        BoardInputMilliamps = 14_026,
        BoardInputMinMilliamps = 11_053,
        BoardInputMaxMilliamps = 14_026,
    };

    public GpuRomOverrideStateDto GetState() => Answer;

    public GpuRomOverrideStateDto SetEnabled(bool enabled)
    {
        LastEnabled = enabled;
        return Answer;
    }

    public GpuRailLimitsDto GetRailLimits() => RailAnswer;

    public GpuRailLimitsDto SetRailLimits(int? nvvddMilliamps, int? msvddMilliamps, int? boardInputMilliamps)
    {
        LastRailSet = (nvvddMilliamps, msvddMilliamps, boardInputMilliamps);
        return RailAnswer;
    }
}
