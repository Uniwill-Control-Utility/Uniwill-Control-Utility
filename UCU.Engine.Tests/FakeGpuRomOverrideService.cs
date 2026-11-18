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

    /// <summary>The target TGP the verb carried (mW), or null when the request had none.</summary>
    public int? LastTargetTgpMilliwatts { get; private set; }

    /// <summary>How many times the engine's start-up re-apply reached this fake.</summary>
    public int ReapplyCalls { get; private set; }

    /// <summary>The next start-up attempts' verdicts, in order; then <see cref="Verdict"/>.</summary>
    public Queue<GpuReapplyVerdict> Verdicts { get; } = new();

    /// <summary>What an attempt answers once the scripted ones are used up.</summary>
    public GpuReapplyVerdict Verdict { get; set; } = GpuReapplyVerdict.NoIntent;

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

    public GpuRomOverrideStateDto SetEnabled(bool enabled, int? targetTgpMilliwatts = null)
    {
        LastEnabled = enabled;
        LastTargetTgpMilliwatts = targetTgpMilliwatts;
        return Answer;
    }

    public GpuReapplyResult ReapplyAtStartup()
    {
        ReapplyCalls++;
        GpuReapplyVerdict verdict = Verdicts.Count > 0 ? Verdicts.Dequeue() : Verdict;
        return new(verdict, verdict == GpuReapplyVerdict.NoIntent ? null : Answer);
    }

    /// <summary>How many settle passes reached this fake.</summary>
    public int SettleCalls { get; private set; }

    /// <summary>The next settle passes, in order; then <see cref="SettleAnswer"/>.</summary>
    public Queue<GpuSettleResult> Settles { get; } = new();

    /// <summary>What a settle pass answers once the scripted ones are used up.</summary>
    public GpuSettleResult SettleAnswer { get; set; } = new(GpuSettleVerdict.Settled, null, null);

    public GpuRailLimitsDto GetRailLimits() => RailAnswer;

    public GpuSettleResult SettleAtStartup()
    {
        SettleCalls++;
        return Settles.Count > 0 ? Settles.Dequeue() : SettleAnswer;
    }

    /// <summary>How many drift-watch ticks reached this fake.</summary>
    public int WatchCalls { get; private set; }

    /// <summary>The next watch ticks, in order; then <see cref="WatchAnswer"/>.</summary>
    public Queue<GpuBudgetWatchResult> Watches { get; } = new();

    /// <summary>
    /// What a watch tick answers once the scripted ones are used up. Stopped by default so a
    /// test that is not about the watch ends it on its first tick.
    /// </summary>
    public GpuBudgetWatchResult WatchAnswer { get; set; } = new(GpuBudgetWatchVerdict.Stopped, null);

    public GpuBudgetWatchResult WatchBudget()
    {
        WatchCalls++;
        return Watches.Count > 0 ? Watches.Dequeue() : WatchAnswer;
    }

    /// <summary>Never raised by this fake: the engine starts its watch from StartAsync as well.</summary>
    public event Action? Unlocked
    {
        add { }
        remove { }
    }

    public GpuRailLimitsDto SetRailLimits(int? nvvddMilliamps, int? msvddMilliamps, int? boardInputMilliamps)
    {
        LastRailSet = (nvvddMilliamps, msvddMilliamps, boardInputMilliamps);
        return RailAnswer;
    }
}
