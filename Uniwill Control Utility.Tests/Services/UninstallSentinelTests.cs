namespace Uniwill_Control_Utility.Tests.Services;

/// <summary>
/// The self-heal state machine behind the Control Panel / Settings uninstall
/// route: the app watches its MSI product registration and, once it vanishes
/// (the thin vpk MSI removal unregisters in ~2 s and nothing else), runs the
/// elevated engine --uninstall-engine verb. The verb is destructive, so the
/// arming, debouncing, and failure-safety rules are the contract under test.
/// </summary>
public sealed class UninstallSentinelTests
{
    private static UninstallSentinel Create(
        Func<string[]> enumerate,
        Action onDetected,
        TimeSpan? pollInterval = null) =>
        new(enumerate, onDetected, _ => { }, pollInterval ?? TimeSpan.FromMilliseconds(10));

    [Fact]
    public void Step_NeverArmedProductMissing_DoesNotFire()
    {
        // Dev/portable runs have no MSI registration from the start: an
        // un-armed sentinel must stay inert forever.
        var sentinel = Create(() => Array.Empty<string>(), () => { });
        for (int i = 0; i < 10; i++)
            Assert.False(sentinel.Step(found: false));
    }

    [Fact]
    public void Step_ArmsOnSightThenFiresAfterDebounceWindow()
    {
        var sentinel = Create(() => Array.Empty<string>(), () => { });

        Assert.False(sentinel.Step(found: true)); // arms

        // One miss is inside the debounce window (registration flickers
        // during transactions); the second consecutive miss fires.
        Assert.False(sentinel.Step(found: false));
        Assert.True(sentinel.Step(found: false));
    }

    [Fact]
    public void Step_RegistrationBackResetsDebounce()
    {
        var sentinel = Create(() => Array.Empty<string>(), () => { });

        sentinel.Step(found: true);
        sentinel.Step(found: false); // one miss banked

        Assert.False(sentinel.Step(found: true)); // back: misses reset
        Assert.False(sentinel.Step(found: false));
        Assert.True(sentinel.Step(found: false));
    }

    [Fact]
    public async Task RunAsync_RegistrationVanishes_RunsReaction()
    {
        bool present = true;
        int fired = 0;
        var sentinel = Create(() => present ? [UninstallSentinel.ProductName] : [], () => fired++);

        var run = sentinel.RunAsync();
        await Task.Delay(100);
        present = false;
        await run.ConfigureAwait(false); // RunAsync exits after firing

        Assert.Equal(1, fired);
    }

    [Fact]
    public async Task RunAsync_EnumerationAlwaysFails_NeverFires()
    {
        // A broken probe must never trigger the destructive verb.
        int fired = 0;
        var sentinel = Create(
            () => throw new InvalidOperationException("MSI unavailable"),
            () => fired++);

        var run = sentinel.RunAsync();
        await Task.Delay(300);

        Assert.Equal(0, fired);
        Assert.False(run.IsCompleted); // still polling, treating failures as present
    }

    [Fact]
    public void ProductRegistered_MatchesTheVpkProductNameCaseInsensitively()
    {
        Assert.True(UninstallSentinel.ProductRegistered(
            ["Some App", "Uniwill Control Utility"]));
        Assert.True(UninstallSentinel.ProductRegistered(
            ["uniwill control utility"]));
        Assert.False(UninstallSentinel.ProductRegistered(
            ["Some App", "Uniwill Control Utility Pro"]));
        Assert.False(UninstallSentinel.ProductRegistered([]));
    }
}
