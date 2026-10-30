using Uniwill_Control_Utility.Services;
using UCU.Core.Supervision;

namespace Uniwill_Control_Utility.Tests.Services;

/// <summary>
/// The preset arbiter runtime: one serialized pipeline with the pure
/// PresetArbiterPolicy in front of the applier. The applier, the Adaptive flag,
/// the persistence sink and the clock are injected — no settings, no hardware,
/// no WPF. (The applier delegate IS the seam: production wires it to
/// PresetProfileApplier.ApplyProfileAsync.)
/// </summary>
public sealed class PresetArbiterTests
{
    private static readonly DateTime T0 = new(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);

    private DateTime _now = T0;
    private readonly List<string> _applied = new();
    private readonly List<string> _persisted = new();
    private bool _adaptiveRunning;
    private Func<string, string?, Task>? _applierOverride;

    private PresetArbiter Create() => new(
        apply: async (profile, _) =>
        {
            if (_applierOverride is not null)
            {
                await _applierOverride(profile, null);
                return;
            }
            _applied.Add(profile);
            await Task.CompletedTask;
        },
        isAdaptiveRunning: () => _adaptiveRunning,
        persistSelection: p => _persisted.Add(p),
        clock: () => _now);

    private void Advance(TimeSpan d) => _now += d;

    // ---- happy paths -------------------------------------------------------

    [Fact]
    public async Task UserUiIntent_AppliesAndPersists()
    {
        var arbiter = Create();

        var result = await arbiter.RequestAsync(PresetIntentSource.UserInterface, "Gaming");

        Assert.True(result.Applied);
        Assert.Equal(new[] { "Gaming" }, _applied);
        Assert.Equal(new[] { "Gaming" }, _persisted); // UI selection persists
    }

    [Fact]
    public async Task BootRestore_AppliesInsideGrace_AndDoesNotPersist()
    {
        var arbiter = Create();

        var result = await arbiter.RequestAsync(PresetIntentSource.BootRestore, "Office");

        Assert.True(result.Applied); // boot restore is never suppressed by grace
        Assert.Equal(new[] { "Office" }, _applied);
        Assert.Empty(_persisted);    // a restore is not a new selection
    }

    [Fact]
    public async Task AutomationEdge_UnderGrace_IsSilentlyDropped()
    {
        var arbiter = Create();
        Advance(TimeSpan.FromSeconds(2));

        var result = await arbiter.RequestAsync(PresetIntentSource.AutomationAc, "Turbo");

        Assert.False(result.Applied);
        Assert.Equal(PresetArbiterAction.SuppressBootGrace, result.Action);
        Assert.Empty(_applied);
        Assert.Empty(_persisted);
    }

    [Fact]
    public async Task AutomationEdge_AfterGrace_AppliesAndPersistsLastKnownGood()
    {
        var arbiter = Create();
        Advance(PresetArbiterPolicy.BootGraceWindow + TimeSpan.FromSeconds(1));

        var result = await arbiter.RequestAsync(PresetIntentSource.AutomationAc, "Gaming");

        Assert.True(result.Applied);
        Assert.Equal(new[] { "Gaming" }, _applied);
        // The AC automation updates the persisted last-known-good selection so the
        // Presets page and the tray checks track reality.
        Assert.Equal(new[] { "Gaming" }, _persisted);
    }

    // ---- Adaptive ownership --------------------------------------------------

    [Fact]
    public async Task ButtonPress_WhileAdaptiveRunning_IsDropped()
    {
        var arbiter = Create();
        Advance(TimeSpan.FromMinutes(3));
        _adaptiveRunning = true;

        var result = await arbiter.RequestAsync(PresetIntentSource.HardwarePhysicalButton, "Office");

        Assert.False(result.Applied);
        Assert.Equal(PresetArbiterAction.SuppressAdaptiveRunning, result.Action);
        Assert.Empty(_applied);
    }

    [Fact]
    public async Task UserUiClick_WhileAdaptiveRunning_Applies()
    {
        // The UI is the user's way of taking control back.
        var arbiter = Create();
        Advance(TimeSpan.FromMinutes(3));
        _adaptiveRunning = true;

        var result = await arbiter.RequestAsync(PresetIntentSource.UserInterface, "Turbo");

        Assert.True(result.Applied);
    }

    // ---- debounce + recorded direct applies ----------------------------------

    [Fact]
    public async Task IdenticalUiClicks_WithinDebounce_AppliesOnce()
    {
        var arbiter = Create();
        Advance(TimeSpan.FromMinutes(1));

        var first = await arbiter.RequestAsync(PresetIntentSource.UserInterface, "Gaming");
        var second = await arbiter.RequestAsync(PresetIntentSource.UserInterface, "Gaming");

        Assert.True(first.Applied);
        Assert.False(second.Applied);
        Assert.Equal(PresetArbiterAction.SuppressDuplicate, second.Action);
        Assert.Equal(new[] { "Gaming" }, _applied);
    }

    [Fact]
    public async Task RecordedDirectApply_IsRespectedByTheDebounce()
    {
        // The Presets page applies directly (slot-aware); the arbiter must know the
        // machine already moved so a racing automation edge for the same profile
        // is recognized as a duplicate.
        var arbiter = Create();
        Advance(TimeSpan.FromMinutes(1));

        arbiter.RecordApply(PresetIntentSource.UserInterface, "Custom 2");
        var edge = await arbiter.RequestAsync(PresetIntentSource.AutomationAc, "Custom 2");

        Assert.False(edge.Applied);
        Assert.Equal(PresetArbiterAction.SuppressDuplicate, edge.Action);
    }

    // ---- failure semantics -----------------------------------------------------

    [Fact]
    public async Task FailedApply_KeepsRetryOpen_AndPersistsNothing()
    {
        _applierOverride = (profile, _) => throw new InvalidOperationException("SMU mailbox refused");
        var arbiter = Create();
        Advance(TimeSpan.FromMinutes(1));

        var failed = await arbiter.RequestAsync(PresetIntentSource.UserInterface, "Turbo");

        Assert.False(failed.Applied);
        Assert.Equal(PresetArbiterAction.Apply, failed.Action); // applied, and the apply failed
        Assert.Empty(_persisted);

        _applierOverride = null; // hardware recovers
        var retry = await arbiter.RequestAsync(PresetIntentSource.UserInterface, "Turbo");

        // The failed apply must not poison the debounce: a retry of the same
        // profile is not a duplicate of something that never landed.
        Assert.True(retry.Applied);
    }

    // ---- serialization ----------------------------------------------------------

    [Fact]
    public async Task Intents_Serialize_AsingleExecutionPipeline()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var concurrent = 0;
        var maxConcurrent = 0;

        _applierOverride = async (profile, _) =>
        {
            var now = Interlocked.Increment(ref concurrent);
            maxConcurrent = Math.Max(maxConcurrent, now);
            if (profile == "Turbo")
            {
                started.TrySetResult();
                await release.Task; // park the first apply
            }
            Interlocked.Decrement(ref concurrent);
            lock (_applied) _applied.Add(profile);
            await Task.CompletedTask;
        };

        var arbiter = Create();
        Advance(TimeSpan.FromMinutes(1));

        var first = arbiter.RequestAsync(PresetIntentSource.UserInterface, "Turbo");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var second = arbiter.RequestAsync(PresetIntentSource.AutomationBattery, "Office");
        var third = arbiter.RequestAsync(PresetIntentSource.BootRestore, "Gaming");

        // While the first apply is parked, nothing else may run.
        await Task.Delay(200);
        Assert.Equal(1, maxConcurrent);
        Assert.False(second.IsCompleted);
        Assert.False(third.IsCompleted);

        release.TrySetResult();
        var results = await Task.WhenAll(first, second, third);

        Assert.All(results, r => Assert.True(r.Applied));
        Assert.Equal(1, maxConcurrent);
        // Queue order: the parked UI apply, then the intents in arrival order.
        Assert.Equal(new[] { "Turbo", "Office", "Gaming" }, _applied);
    }
}
