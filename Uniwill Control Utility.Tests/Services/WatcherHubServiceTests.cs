using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Uniwill_Control_Utility.Services;
using Uniwill_Control_Utility.Services.Performance;
using UCU.Contracts;
using UCU.Contracts.Capabilities;
using UCU.Core.Ec;
using UCU.Core.Supervision;

namespace Uniwill_Control_Utility.Tests.Services;

/// <summary>
/// The watcher hub runtime (U-201) driven hermetically through its test
/// seams — the same shape as PresetArbiterTests: the telemetry observation,
/// the verb readbacks, the foreground resolution, the action executor and
/// the clock are all delegates, so the loop logic runs headless. The pure
/// decision rules themselves are pinned in the Core suite
/// (WatcherPolicyTests); these are the runtime contracts: cooldown
/// bookkeeping, edge wiring, the diff-poll floor, Busy backoff, the fps
/// lease discipline, and the store's persistence guarantees. The final
/// section pins the production action executor's semantics (turbo set
/// semantics, the arbiter-routed preset toast) with capability fakes.
/// </summary>
public sealed class WatcherHubServiceTests
{
    private static readonly DateTime T0 = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);

    #region Runtime harness

    private DateTime _now = T0;
    private string _foregroundExe = "";
    private readonly List<WatcherRule> _fired = new();
    private WatcherTelemetry _telemetry = new(true, 0, 0, 0, 0, 0, true, "");
    private Func<WatcherValueSource, Task<string?>> _readValue = _ => Task.FromResult<string?>(null);

    private sealed class TempStore : IDisposable
    {
        public WatcherStore Store { get; } = new(System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "ucu-watcher-tests",
            Guid.NewGuid().ToString("N"), "watchers.json"));

        public void Dispose()
        {
            foreach (string file in new[] { Store.FilePath, Store.FilePath + ".bak", Store.FilePath + ".tmp" })
            {
                try { if (System.IO.File.Exists(file)) System.IO.File.Delete(file); }
                catch { /* temp litter */ }
            }
        }
    }

    private WatcherHubService Hub(WatcherStore store, IFpsMonitoringService? fps = null) => new(
        store,
        observe: () => _telemetry,
        readValue: source => _readValue(source),
        execute: rule =>
        {
            _fired.Add(rule);
            return Task.CompletedTask;
        },
        clock: () => _now,
        fps: fps,
        foreground: () => _foregroundExe);

    private void Advance(TimeSpan d) => _now += d;

    #endregion

    #region Rule builders

    private static WatcherRule GpuAbove(int threshold, int cooldownSeconds = 5) => new()
    {
        Name = "gpu hot",
        Kind = WatcherConditionKind.Telemetry,
        GpuTempGt = threshold,
        CooldownSeconds = cooldownSeconds,
        Action = WatcherActionKind.Notify,
        NotifyMessage = "hot",
    };

    private static WatcherRule Foreground(string target, WatcherEdgeKind edge, int cooldownSeconds = 5) => new()
    {
        Name = "fg " + target,
        Kind = WatcherConditionKind.ForegroundProcess,
        ForegroundTargets = new[] { target },
        EdgeKind = edge,
        CooldownSeconds = cooldownSeconds,
        Action = WatcherActionKind.Notify,
        NotifyMessage = "fg",
    };

    private static WatcherRule Power(WatcherPowerEventKind edge, int cooldownSeconds = 5) => new()
    {
        Name = "power " + edge,
        Kind = WatcherConditionKind.PowerEvent,
        PowerEvent = edge,
        CooldownSeconds = cooldownSeconds,
        Action = WatcherActionKind.Notify,
        NotifyMessage = "power",
    };

    private static WatcherRule WatchFanRpm(int cooldownSeconds = 5) => new()
    {
        Name = "rpm watch",
        Kind = WatcherConditionKind.ValueChange,
        ValueSource = WatcherValueSource.FanRpm,
        CooldownSeconds = cooldownSeconds,
        Action = WatcherActionKind.Notify,
        NotifyMessage = "rpm",
    };

    #endregion

    #region Telemetry runtime

    [Fact]
    public async Task TelemetryRule_FiresOnMatch_RespectsCooldown_RefiresAfter()
    {
        using var tmp = new TempStore();
        tmp.Store.Save(new[] { GpuAbove(85) });
        var hub = Hub(tmp.Store);

        _telemetry = new WatcherTelemetry(true, 0, 90, 0, 0, 0, true, "");

        await hub.TickAsync(_now, CancellationToken.None);           // t0: fires
        Assert.Single(_fired);

        Advance(TimeSpan.FromSeconds(1));
        await hub.TickAsync(_now, CancellationToken.None);           // t+1s: in cooldown
        Assert.Single(_fired);

        Advance(TimeSpan.FromSeconds(4));
        await hub.TickAsync(_now, CancellationToken.None);           // t+5s: cooldown elapsed
        Assert.Equal(2, _fired.Count);
    }

    [Fact]
    public async Task TelemetryRule_GateUnmatched_NeverFires()
    {
        using var tmp = new TempStore();
        tmp.Store.Save(new[] { GpuAbove(85) });
        var hub = Hub(tmp.Store);

        _telemetry = new WatcherTelemetry(true, 0, 80, 0, 0, 0, true, "");

        for (int i = 0; i < 10; i++)
        {
            await hub.TickAsync(_now, CancellationToken.None);
            Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Empty(_fired);
    }

    [Fact]
    public async Task DisabledRule_NeverFires()
    {
        using var tmp = new TempStore();
        tmp.Store.Save(new[] { GpuAbove(85) with { Enabled = false } });
        var hub = Hub(tmp.Store);

        _telemetry = new WatcherTelemetry(true, 0, 90, 0, 0, 0, true, "");
        await hub.TickAsync(_now, CancellationToken.None);

        Assert.Empty(_fired);
    }

    [Fact]
    public async Task ActionFailure_EveryTickKeepsTheLoopAlive()
    {
        using var tmp = new TempStore();
        tmp.Store.Save(new[] { GpuAbove(85) });

        var hub = new WatcherHubService(
            tmp.Store,
            observe: () => _telemetry,
            readValue: _ => Task.FromResult<string?>(null),
            execute: rule => throw new InvalidOperationException("verb exploded"),
            clock: () => _now);

        _telemetry = new WatcherTelemetry(true, 0, 90, 0, 0, 0, true, "");

        // TickAsync must not throw: one rule's failing action cannot kill the hub.
        await hub.TickAsync(_now, CancellationToken.None);
        Advance(TimeSpan.FromSeconds(5));
        await hub.TickAsync(_now, CancellationToken.None);
    }

    #endregion

    #region Foreground edges

    [Fact]
    public async Task ForegroundEnterEdge_FiresOnce_NoRefireWithoutTransition()
    {
        using var tmp = new TempStore();
        tmp.Store.Save(new[] { Foreground("game.exe", WatcherEdgeKind.Enter) });
        var hub = Hub(tmp.Store);

        _foregroundExe = "notepad";
        await hub.TickAsync(_now, CancellationToken.None);
        Assert.Empty(_fired);

        Advance(TimeSpan.FromSeconds(1));
        _foregroundExe = "game";
        await hub.TickAsync(_now, CancellationToken.None);
        Assert.Single(_fired);

        Advance(TimeSpan.FromSeconds(6));
        await hub.TickAsync(_now, CancellationToken.None);           // no edge: not state
        Assert.Single(_fired);
    }

    [Fact]
    public async Task ForegroundExitEdge_FiresWhenTargetIsLost()
    {
        using var tmp = new TempStore();
        tmp.Store.Save(new[] { Foreground("game.exe", WatcherEdgeKind.Exit) });
        var hub = Hub(tmp.Store);

        _foregroundExe = "game";
        await hub.TickAsync(_now, CancellationToken.None);           // Enter is not Exit
        Assert.Empty(_fired);

        Advance(TimeSpan.FromSeconds(1));
        _foregroundExe = "notepad";
        await hub.TickAsync(_now, CancellationToken.None);           // Exit edge
        Assert.Single(_fired);
    }

    #endregion

    #region Power/session edges

    [Fact]
    public async Task PowerEvent_FiresMatchingRule_WithCooldown()
    {
        using var tmp = new TempStore();
        tmp.Store.Save(new[]
        {
            Power(WatcherPowerEventKind.AcConnected),
            Power(WatcherPowerEventKind.AcDisconnected),
        });
        var hub = Hub(tmp.Store);

        await hub.NotifyPowerEventAsync(WatcherPowerEventKind.AcConnected, _now);
        Assert.Single(_fired);                                        // only the AC rule

        await hub.NotifyPowerEventAsync(WatcherPowerEventKind.AcConnected, _now + TimeSpan.FromSeconds(1));
        Assert.Single(_fired);                                        // in cooldown

        await hub.NotifyPowerEventAsync(WatcherPowerEventKind.AcConnected, _now + TimeSpan.FromSeconds(5));
        Assert.Equal(2, _fired.Count);
    }

    #endregion

    #region Value-change / diff poll

    [Fact]
    public async Task ValueChange_FirstReadSeeds_ChangeFires_StableDoesNot()
    {
        using var tmp = new TempStore();
        tmp.Store.Save(new[] { WatchFanRpm() });

        int rpm = 1000;
        _readValue = _ => Task.FromResult<string?>(rpm.ToString());
        var hub = Hub(tmp.Store);

        await hub.TickAsync(_now, CancellationToken.None);            // seed
        Assert.Empty(_fired);

        rpm = 3000;
        Advance(TimeSpan.FromSeconds(5));
        await hub.TickAsync(_now, CancellationToken.None);            // change
        Assert.Single(_fired);

        Advance(TimeSpan.FromSeconds(5));
        await hub.TickAsync(_now, CancellationToken.None);            // stable
        Assert.Single(_fired);
    }

    [Fact]
    public async Task ValueChange_DiffPollKeepsTheFiveSecondFloor()
    {
        using var tmp = new TempStore();
        tmp.Store.Save(new[] { WatchFanRpm() });

        int reads = 0;
        _readValue = _ => { reads++; return Task.FromResult<string?>("1"); };
        var hub = Hub(tmp.Store);

        for (int i = 0; i < 10; i++)                                  // 10 one-second ticks
        {
            await hub.TickAsync(_now, CancellationToken.None);
            Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Equal(2, reads);                                       // t0 and t+5s, never faster
    }

    [Fact]
    public async Task ValueChange_BusyReadback_IsBackedOff_NotRetriedStormed()
    {
        using var tmp = new TempStore();
        tmp.Store.Save(new[] { WatchFanRpm() });

        var script = new Queue<Func<Task<string?>>>();
        script.Enqueue(() => throw new EngineException(EngineErrorCode.Busy, "engine busy"));
        script.Enqueue(() => Task.FromResult<string?>("1"));          // seeds (no fire)
        script.Enqueue(() => Task.FromResult<string?>("2"));          // fires
        _readValue = _ => script.Dequeue()();
        var hub = Hub(tmp.Store);

        await hub.TickAsync(_now, CancellationToken.None);            // Busy: swallowed
        Assert.Empty(_fired);

        Advance(TimeSpan.FromSeconds(5));
        await hub.TickAsync(_now, CancellationToken.None);            // seed
        Assert.Empty(_fired);

        Advance(TimeSpan.FromSeconds(5));
        await hub.TickAsync(_now, CancellationToken.None);            // change
        Assert.Single(_fired);
    }

    [Fact]
    public async Task FpsLease_AcquiredOnlyWhileAnEnabledFpsRuleExists()
    {
        using var tmp = new TempStore();
        var fps = new FpsFake();

        Hub(tmp.Store, fps);

        Assert.Equal(0, fps.ActiveLeases);

        tmp.Store.Save(new[]
        {
            new WatcherRule
            {
                Name = "fps low",
                Kind = WatcherConditionKind.Telemetry,
                FpsGt = 30,
                Action = WatcherActionKind.Notify,
                NotifyMessage = "fps",
            }
        });
        Assert.Equal(1, fps.ActiveLeases);                            // consumer appeared

        tmp.Store.Save(new[]
        {
            new WatcherRule
            {
                Name = "fps low",
                Kind = WatcherConditionKind.Telemetry,
                FpsGt = 30,
                Enabled = false,
                Action = WatcherActionKind.Notify,
                NotifyMessage = "fps",
            }
        });
        Assert.Equal(0, fps.ActiveLeases);                            // consumer gone: sampler stops

        await Task.CompletedTask;
    }

    private sealed class FpsFake : IFpsMonitoringService
    {
        public int ActiveLeases { get; private set; }

        public FpsMetricsSnapshot Latest { get; } = new();

        public IDisposable Acquire()
        {
            ActiveLeases++;
            return new Lease(this);
        }

        private sealed class Lease : IDisposable
        {
            private readonly FpsFake _owner;
            public Lease(FpsFake owner) => _owner = owner;
            public void Dispose() => _owner.ActiveLeases--;
        }
    }

    #endregion

    #region Store

    [Fact]
    public void Store_RoundTrips_TheSavedRules()
    {
        using var tmp = new TempStore();
        var rules = new[]
        {
            GpuAbove(85) with { Action = WatcherActionKind.SetFanTurbo, FanTurboOn = true, NotifyMessage = null },
            Foreground("game.exe", WatcherEdgeKind.Both),
            WatchFanRpm() with { CooldownSeconds = 30 },
        };

        tmp.Store.Save(rules);

        var reloaded = new WatcherStore(tmp.Store.FilePath);
        reloaded.Load();

        // Field-wise fingerprints, not record Equals: deserialization
        // materializes ForegroundTargets as List<string> (the record's member
        // is IReadOnlyList<string>), which never Equals() the saved array.
        Assert.Equal(rules.Select(Fingerprint), reloaded.Rules.Select(Fingerprint));
    }

    private static string Fingerprint(WatcherRule r) => string.Join('|',
        r.Id, r.Name, r.Enabled, r.CooldownSeconds, (int)r.Kind,
        r.CpuTempGt, r.CpuTempLt, r.GpuTempGt, r.GpuTempLt,
        r.CpuUsageGt, r.GpuUsageGt, r.FpsGt, r.OnAc, r.ForegroundExe,
        string.Join(",", r.ForegroundTargets), (int)r.EdgeKind,
        r.PowerEvent is null ? "" : (int)r.PowerEvent,
        r.ValueSource is null ? "" : (int)r.ValueSource,
        (int)r.Action, r.ProfileName, r.PerformanceMode, r.FanTurboOn,
        r.NotifyTitle, r.NotifyMessage);

    [Fact]
    public void Store_CorruptFile_MovesToBak_StartsEmpty()
    {
        string path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "ucu-watcher-tests", Guid.NewGuid().ToString("N"), "watchers.json");
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, "{ this is not watchers.json");

        try
        {
            var store = new WatcherStore(path);
            store.Load();

            Assert.Empty(store.Rules);
            Assert.True(System.IO.File.Exists(path + ".bak"));        // user data preserved
            Assert.False(System.IO.File.Exists(path));                // the hub starts clean
        }
        finally
        {
            System.IO.File.Delete(path + ".bak");
        }
    }

    [Fact]
    public void Store_Load_DropsRulesThatFailValidation()
    {
        string path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "ucu-watcher-tests", Guid.NewGuid().ToString("N"), "watchers.json");
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);

        var good = GpuAbove(85);

        // Hand-append a shapeless rule (Save would refuse to write it): the
        // load path is the last line of defense for hand-edited files.
        var store0 = new WatcherStore(path);
        store0.Save(new[] { good });
        string json = System.IO.File.ReadAllText(path).TrimEnd(']');
        json += ",{\"name\":\"no gates\",\"kind\":\"telemetry\",\"action\":\"notify\",\"notifyMessage\":\"m\"}]";
        System.IO.File.WriteAllText(path, json);

        try
        {
            var store = new WatcherStore(path);
            store.Load();

            Assert.Equal(new[] { Fingerprint(good) }, store.Rules.Select(Fingerprint));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public void Store_Save_ClampsCooldown_RejectsInvalidRules_AndCapsTheCount()
    {
        using var tmp = new TempStore();

        tmp.Store.Save(new[] { GpuAbove(85) with { CooldownSeconds = 0 } });
        Assert.Equal(1, tmp.Store.Rules[0].CooldownSeconds);          // storm floor

        var noProfile = GpuAbove(85) with { Action = WatcherActionKind.ApplyPreset, ProfileName = null, NotifyMessage = null };
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => tmp.Store.Save(new[] { noProfile }));
        Assert.Contains("profile", ex.Message, StringComparison.Ordinal);

        var many = new List<WatcherRule>();
        for (int i = 0; i <= WatcherPolicy.MaxRules; i++)
            many.Add(GpuAbove(80 + i % 15) with { Name = "rule " + i });
        Assert.Throws<InvalidOperationException>(() => tmp.Store.Save(many));
    }

    [Fact]
    public void Store_RaisesChanged_OnSave()
    {
        using var tmp = new TempStore();
        int changes = 0;
        tmp.Store.Changed += () => changes++;

        tmp.Store.Save(new[] { GpuAbove(85) });

        Assert.Equal(1, changes);
    }

    #endregion

    #region Production action executor (whitelist semantics)

    [Fact]
    public async Task SetFanTurbo_IsSetSemantics_TogglesOnlyOnMismatch()
    {
        var fan = new FanFake();

        // Desired state already holds: a blind toggle would turn turbo OFF.
        fan.Status = new FanStatusDto { TurboActive = true };
        await WatcherHubService.ExecuteAsync(
            GpuAbove(85) with { Action = WatcherActionKind.SetFanTurbo, FanTurboOn = true, NotifyMessage = null },
            arbiter: null!, fan, new PowerFake());
        Assert.Equal(0, fan.Toggles);

        await WatcherHubService.ExecuteAsync(
            GpuAbove(85) with { Action = WatcherActionKind.SetFanTurbo, FanTurboOn = false, NotifyMessage = null },
            arbiter: null!, fan, new PowerFake());
        Assert.Equal(1, fan.Toggles);                                 // mismatch: one toggle
    }

    [Fact]
    public async Task SetPerformanceMode_ForwardsTheModeNumber()
    {
        var power = new PowerFake();

        await WatcherHubService.ExecuteAsync(
            GpuAbove(85) with { Action = WatcherActionKind.SetPerformanceMode, PerformanceMode = 2, NotifyMessage = null },
            arbiter: null!, new FanFake(), power);

        Assert.Equal(new[] { 2 }, power.Modes);
    }

    [Fact]
    public async Task ApplyPreset_RoutesThroughTheArbiter_BootingGraceSuppresses_AndToastsOnlyWhenApplied()
    {
        var toasts = new List<(string Title, string Body, string? Accent)>();
        var now = T0;
        var arbiter = new PresetArbiter(
            apply: (profile, _) => Task.CompletedTask,
            isAdaptiveRunning: () => false,
            persistSelection: _ => { },
            clock: () => now);

        var rule = GpuAbove(85) with { Action = WatcherActionKind.ApplyPreset, ProfileName = "Gaming", NotifyMessage = null };

        // The arbiter starts now: the boot-grace window suppresses automation
        // intents (the startup-restore race this whole pipeline exists for).
        await WatcherHubService.ExecuteAsync(rule, arbiter, new FanFake(), new PowerFake(),
            (title, body, accent) => toasts.Add((title, body, accent)));
        Assert.Empty(toasts);

        now = T0 + TimeSpan.FromSeconds(11);                          // grace over
        await WatcherHubService.ExecuteAsync(rule, arbiter, new FanFake(), new PowerFake(),
            (title, body, accent) => toasts.Add((title, body, accent)));
        Assert.Single(toasts);
        Assert.Equal("Gaming", toasts[0].Accent);                     // accent = the profile span
    }

    [Fact]
    public async Task Notify_ShowsTheRuleTextVerbatim()
    {
        var toasts = new List<(string Title, string Body, string? Accent)>();

        await WatcherHubService.ExecuteAsync(
            GpuAbove(85) with { NotifyTitle = "Game closed", NotifyMessage = "Fan back to auto" },
            arbiter: null!, new FanFake(), new PowerFake(),
            (title, body, accent) => toasts.Add((title, body, accent)));

        Assert.Equal(("Game closed", "Fan back to auto", (string?)null), toasts[0]);
    }

    #endregion

    #region DI wiring pin

    [Fact]
    public void App_Xaml_WiresTheWatcherHub_AsAHostedSingletonAfterTheArbiter()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !System.IO.File.Exists(Path.Combine(directory.FullName, "Uniwill Control Utility", "App.xaml.cs")))
            directory = directory.Parent;
        string source = File.ReadAllText(Path.Combine(directory!.FullName, "Uniwill Control Utility", "App.xaml.cs"));

        int arbiter = source.IndexOf("services.AddSingleton<PresetArbiter>();", StringComparison.Ordinal);
        int store = source.IndexOf("services.AddSingleton<WatcherStore>();", StringComparison.Ordinal);
        int hub = source.IndexOf("services.AddSingleton<WatcherHubService>();", StringComparison.Ordinal);
        int hosted = source.IndexOf(
            "AddHostedService(provider => provider.GetRequiredService<WatcherHubService>())", StringComparison.Ordinal);

        Assert.True(arbiter >= 0 && store > arbiter && hub > store && hosted > hub,
            "the hub depends on the arbiter and is a singleton double-registered like every other hosted service");
    }

    #endregion

    #region Capability fakes

    private sealed class FanFake : IFanCapability
    {
        public FanStatusDto Status { get; set; } = new();
        public int Toggles { get; private set; }

        public Task<FanStatusDto> GetStatusAsync(CancellationToken ct = default) => Task.FromResult(Status);
        public Task ToggleTurboAsync(CancellationToken ct = default)
        {
            Toggles++;
            Status = Status with { TurboActive = !Status.TurboActive };
            return Task.CompletedTask;
        }

        public Task SetSpeedPercentAsync(int percent, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RestoreAutoControlAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task ResetStateAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task ApplyCurveAsync(FanCurveDto cpu, FanCurveDto gpu, int[]? cpuTemps = null, int[]? gpuTemps = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UploadCurveAsync(EcFanCurveSource source, int[] tempUp, int[] tempDown, int[] duty, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<FanCurveTablesDto> ReadCurveAsync(EcFanCurveSource source, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetUserFanModeAsync(bool enable, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> GetFanPwmAsync(int fanIndex, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> SetFanPwmAsync(int fanIndex, int duty, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetFanPwmBothAsync(int duty, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetFanSwitchSpeedAsync(int rpm, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ApplySmartFanCurveAsync(SmartFanCurveDto curve, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ApplySmartFanDutyAsync(byte[] cpuDuty, byte[] gpuDuty, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UnlockManualModeAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task WriteRegisterBlocksAsync(IReadOnlyList<EcRegisterBlockDto> blocks, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> IsControlSupportedAsync(CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class PowerFake : IPowerCapability
    {
        public List<int> Modes { get; } = new();

        public Task SetPerformanceModeAsync(int mode, CancellationToken ct = default)
        {
            Modes.Add(mode);
            return Task.CompletedTask;
        }

        public Task<PowerLimitsDto> GetLimitsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetPowerLimitAsync(PowerLimitKind kind, int watts, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ApplyCpuPowerLimitsAsync(int pl1Watts, int pl2Watts, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> GetTccOffsetAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetTccOffsetAsync(int offset, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> GetPerformanceModeAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task ApplyOemStockModeAsync(int modeCase, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetOverboostProfileAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task EnsurePowerManagementActiveAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task ActivateGpuPowerControlAsync(int ctgpOffset, int dbOffset, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeactivateGpuPowerControlAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> IsCustomProfileActiveAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<PowerContractTruthDto> GetContractTruthAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task HealContractAsync(HealContractDto plan, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> GetAcRecoveryAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> SetAcRecoveryAsync(bool enabled, CancellationToken ct = default) => throw new NotSupportedException();
    }

    #endregion
}
