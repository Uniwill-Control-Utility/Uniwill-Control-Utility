using UCU.Contracts;
using UCU.Hardware.Gpu;

namespace UCU.Engine.Tests;

/// <summary>
/// The scripted ROM-override store the gpu.get_rom_override_state /
/// gpu.set_rom_override verbs answer with: the wire is exercised without writing the
/// display driver's HKLM key or restarting the adapter.
/// </summary>
public sealed class FakeGpuRomOverrideService : IGpuRomOverrideService
{
    public bool? LastEnabled { get; private set; }

    public GpuRomOverrideStateDto Answer { get; set; } = new()
    {
        State = GpuRomOverrideStates.NotUnlocked,
        TierId = "extra_gpu_power",
        PowerMilliwatts = 250_000,
        NvVddMilliAmps = 294_000,
        MsVddMilliAmps = 84_000,
        BoardInputMilliAmps = 14_026,
        CeilingLimitMilliwatts = 175_000,
        CurrentLimitMilliwatts = 95_000,
        BudgetMilliwatts = 150_000,
        RequestedLimitMilliwatts = null,
    };

    public GpuRomOverrideStateDto GetState() => Answer;

    public GpuRomOverrideStateDto SetEnabled(bool enabled)
    {
        LastEnabled = enabled;
        return Answer;
    }
}
