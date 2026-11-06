using UCU.Core.Supervision;

namespace UCU.Core.Tests.Supervision;

/// <summary>
/// The watcher hub's pure decision core (U-201): every rule-shape / observation
/// pair the runtime can produce gets a pinned answer here. The storm floors are
/// the story's hard requirements (1 s tick, 5 s diff poll, 5 s default cooldown,
/// rule cap), and the unavailable-reading refusal is UCU's hardening over the
/// reference (open-revo fires telemetry rules on stale zero readings).
/// </summary>
public sealed class WatcherPolicyTests
{
    private static WatcherRule Telemetry(
        int? cpuGt = null, int? gpuGt = null, int? fpsGt = null,
        bool? onAc = null, string? exe = null, int cooldown = 5) =>
        new()
        {
            Name = "t",
            Kind = WatcherConditionKind.Telemetry,
            CpuTempGt = cpuGt,
            GpuTempGt = gpuGt,
            FpsGt = fpsGt,
            OnAc = onAc,
            ForegroundExe = exe,
            CooldownSeconds = cooldown,
        };

    private static WatcherTelemetry Obs(
        int cpuTemp = 0, int gpuTemp = 0, int fps = 0, bool onAc = true,
        string exe = "", bool live = true, int cpuUse = 0, int gpuUse = 0) =>
        new(live, cpuTemp, gpuTemp, cpuUse, gpuUse, fps, onAc, exe);

    [Fact]
    public void Thresholds_AreAndGates_OnLiveReadings()
    {
        var rule = Telemetry(cpuGt: 80, gpuGt: 85);

        Assert.True(WatcherPolicy.TelemetryMatches(rule, Obs(cpuTemp: 91, gpuTemp: 88)));
        Assert.False(WatcherPolicy.TelemetryMatches(rule, Obs(cpuTemp: 91, gpuTemp: 80))); // one gate fails
        Assert.False(WatcherPolicy.TelemetryMatches(rule, Obs(cpuTemp: 80, gpuTemp: 90))); // gt is strict
    }

    [Fact]
    public void UnavailableReadings_NeverMatch_TheSensorOutageGuard()
    {
        // The snapshot convention: 0 = unavailable. "cpu < 30" on a dead
        // sensor (0) must NOT fire - open-revo's raw compare fires there.
        var lt = new WatcherRule { Name = "l", Kind = WatcherConditionKind.Telemetry, CpuTempLt = 30 };
        Assert.False(WatcherPolicy.TelemetryMatches(lt, Obs(cpuTemp: 0)));
        Assert.True(WatcherPolicy.TelemetryMatches(lt, Obs(cpuTemp: 25)));
    }

    [Fact]
    public void UsageGates_RefuseAParkedSnapshot_ZeroIdleIsNotAMatch()
    {
        var rule = new WatcherRule { Name = "u", Kind = WatcherConditionKind.Telemetry, CpuUsageGt = 0 };

        Assert.False(WatcherPolicy.TelemetryMatches(rule, Obs(live: false, cpuUse: 0)));
        Assert.True(WatcherPolicy.TelemetryMatches(rule, Obs(live: true, cpuUse: 1)));
    }

    [Fact]
    public void FpsGate_ZeroFpsNoGame_IsFalse()
    {
        var rule = Telemetry(fpsGt: 30);

        Assert.False(WatcherPolicy.TelemetryMatches(rule, Obs(fps: 0)));
        Assert.False(WatcherPolicy.TelemetryMatches(rule, Obs(fps: 30)));
        Assert.True(WatcherPolicy.TelemetryMatches(rule, Obs(fps: 31)));
    }

    [Fact]
    public void ForegroundExeGate_SubstringCaseInsensitive()
    {
        var rule = Telemetry(gpuGt: 85, exe: "CYBERPUNK");

        Assert.True(WatcherPolicy.TelemetryMatches(rule, Obs(gpuTemp: 90, exe: "Cyberpunk2077.exe")));
        Assert.False(WatcherPolicy.TelemetryMatches(rule, Obs(gpuTemp: 90, exe: "notepad.exe")));
        Assert.False(WatcherPolicy.TelemetryMatches(rule, Obs(gpuTemp: 80, exe: "Cyberpunk2077.exe")));
    }

    [Fact]
    public void OnAcGate_DesktopsAnswerTrue()
    {
        Assert.True(WatcherPolicy.TelemetryMatches(Telemetry(onAc: true), Obs(onAc: true)));
        Assert.False(WatcherPolicy.TelemetryMatches(Telemetry(onAc: false), Obs(onAc: true)));
    }

    [Fact]
    public void Cooldown_UsesTheRulesSeconds_AndTheFirstFireIsFree()
    {
        var t0 = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);

        Assert.True(WatcherPolicy.CooldownElapsed(null, TimeSpan.FromSeconds(5), t0));
        Assert.False(WatcherPolicy.CooldownElapsed(t0, TimeSpan.FromSeconds(5), t0 + TimeSpan.FromSeconds(4)));
        Assert.True(WatcherPolicy.CooldownElapsed(t0, TimeSpan.FromSeconds(5), t0 + TimeSpan.FromSeconds(5)));
        Assert.Equal(1, (int)WatcherPolicy.MinCooldown.TotalSeconds); // storm floor
    }

    [Fact]
    public void CooldownSeconds_AreClampedToTheOneSecondFloor()
    {
        Assert.Equal(TimeSpan.FromSeconds(1), WatcherPolicy.CooldownOf(new WatcherRule { CooldownSeconds = 0 }));
        Assert.Equal(TimeSpan.FromSeconds(5), WatcherPolicy.CooldownOf(new WatcherRule())); // default 5
    }

    [Fact]
    public void ForegroundEdges_EnterExitBoth_OnNameTransitions()
    {
        var rule = new WatcherRule
        {
            Name = "f",
            Kind = WatcherConditionKind.ForegroundProcess,
            ForegroundTargets = new[] { "game.exe" },
        };

        Assert.True(WatcherPolicy.ForegroundFires(rule with { EdgeKind = WatcherEdgeKind.Enter }, "notepad", "game"));
        Assert.False(WatcherPolicy.ForegroundFires(rule with { EdgeKind = WatcherEdgeKind.Enter }, "other", "game2"));
        Assert.True(WatcherPolicy.ForegroundFires(rule with { EdgeKind = WatcherEdgeKind.Exit }, "game", "notepad"));
        Assert.False(WatcherPolicy.ForegroundFires(rule with { EdgeKind = WatcherEdgeKind.Exit }, "notepad", "game"));
        Assert.True(WatcherPolicy.ForegroundFires(rule with { EdgeKind = WatcherEdgeKind.Both }, "notepad", "game"));
        Assert.True(WatcherPolicy.ForegroundFires(rule with { EdgeKind = WatcherEdgeKind.Both }, "game", "notepad"));

        // No edge (same normalized name, .exe suffix included): never fires.
        Assert.False(WatcherPolicy.ForegroundFires(rule with { EdgeKind = WatcherEdgeKind.Both }, "game.exe", "game"));
        Assert.False(WatcherPolicy.ForegroundFires(rule with { EdgeKind = WatcherEdgeKind.Both }, "other", "other"));
    }

    [Fact]
    public void ValueChange_FirstReadSeeds_DifferentReadbackFires()
    {
        Assert.False(WatcherPolicy.ValueChanged(null, "42"));   // seeding
        Assert.False(WatcherPolicy.ValueChanged("42", "42"));
        Assert.True(WatcherPolicy.ValueChanged("42", "43"));
    }

    [Fact]
    public void Validate_RejectsEveryShapelessRule_AndAcceptsTheStorysFlagship()
    {
        Assert.NotNull(WatcherPolicy.Validate(new WatcherRule { Kind = WatcherConditionKind.Telemetry, Action = WatcherActionKind.Notify, NotifyMessage = "m" }));  // no gate
        Assert.NotNull(WatcherPolicy.Validate(new WatcherRule { Name = "n", Kind = WatcherConditionKind.Telemetry, GpuTempGt = 85, Action = WatcherActionKind.ApplyPreset }));  // no profile
        Assert.NotNull(WatcherPolicy.Validate(new WatcherRule { Name = "n", Kind = WatcherConditionKind.ForegroundProcess, Action = WatcherActionKind.Notify, NotifyMessage = "m" }));  // no targets
        Assert.NotNull(WatcherPolicy.Validate(new WatcherRule { Name = "n", Kind = WatcherConditionKind.ValueChange, Action = WatcherActionKind.Notify, NotifyMessage = "m" }));  // no source

        // "Fan boost when GPU > 85 C in game X": the flagship one rule, valid.
        Assert.Null(WatcherPolicy.Validate(Telemetry(gpuGt: 85, exe: "game") with
        {
            Name = "game boost",
            Action = WatcherActionKind.SetFanTurbo,
            FanTurboOn = true,
        }));
    }

    [Fact]
    public void StormFloors_TheStorysHardNumbers()
    {
        Assert.Equal(TimeSpan.FromSeconds(1), WatcherPolicy.TickFloor);
        Assert.Equal(TimeSpan.FromSeconds(5), WatcherPolicy.DiffPollFloor);
        Assert.Equal(32, WatcherPolicy.MaxRules);
    }
}
