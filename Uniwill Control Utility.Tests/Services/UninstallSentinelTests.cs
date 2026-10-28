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
        new(enumerate, onDetected, _ => { }, pollInterval ?? TimeSpan.FromMilliseconds(10),
            removalEvidence: () => true);

    [Fact]
    public void Step_NeverArmedProductMissing_DoesNotFire()
    {
        // Dev/portable runs have no MSI registration from the start: an
        // un-armed sentinel must stay inert forever.
        var sentinel = Create(() => Array.Empty<string>(), () => { });
        for (int i = 0; i < 10; i++)
            Assert.False(sentinel.Step(registrationFound: false));
    }

    [Fact]
    public void Step_ArmsOnSightThenFiresAfterDebounceWindow()
    {
        var sentinel = Create(() => Array.Empty<string>(), () => { });

        Assert.False(sentinel.Step(registrationFound: true)); // arms

        // One miss is inside the debounce window (registration flickers
        // during transactions); the second consecutive miss fires.
        Assert.False(sentinel.Step(registrationFound: false));
        Assert.True(sentinel.Step(registrationFound: false));
    }

    [Fact]
    public void Step_RegistrationBackResetsDebounce()
    {
        var sentinel = Create(() => Array.Empty<string>(), () => { });

        sentinel.Step(registrationFound: true);
        sentinel.Step(registrationFound: false); // one miss banked

        Assert.False(sentinel.Step(registrationFound: true)); // back: misses reset
        Assert.False(sentinel.Step(registrationFound: false));
        Assert.True(sentinel.Step(registrationFound: false));
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
    public async Task RunAsync_RegistrationGoneBeforeArming_LayoutArmsIt()
    {
        // The race the thin uninstall wins: /x finishes in ~2 s while the
        // app's startup takes minutes - the registration is already gone at
        // the first poll. Running from the installed layout is the same
        // "installed" fact, so the sentinel arms and fires.
        int fired = 0;
        var sentinel = new UninstallSentinel(
            () => Array.Empty<string>(),
            () => fired++,
            _ => { },
            pollInterval: TimeSpan.FromMilliseconds(10),
            installedLayoutProbe: () => true,
            removalEvidence: () => true);

        var run = sentinel.RunAsync();
        var winner = await Task.WhenAny(run, Task.Delay(2000));
        Assert.Same(run, winner); // fired and returned
        Assert.Equal(1, fired);
    }

    [Fact]
    public async Task RunAsync_RegistrationGoneAndNoLayout_StaysInert()
    {
        // Dev/portable runs: no registration, no installed layout - the
        // destructive verb must never run.
        int fired = 0;
        var sentinel = new UninstallSentinel(
            () => Array.Empty<string>(),
            () => fired++,
            _ => { },
            pollInterval: TimeSpan.FromMilliseconds(10),
            installedLayoutProbe: () => false,
            removalEvidence: () => true);

        var run = sentinel.RunAsync();
        await Task.Delay(300);

        Assert.Equal(0, fired);
        Assert.False(run.IsCompleted);
    }

    [Fact]
    public async Task RunAsync_RegistrationGoneWithoutRemovalEvidence_NeverFires()
    {
        // Field-proven: an AV with ARP-key protection (Emsisoft) hides the
        // live registration from the app's registry view - the miss is a
        // phantom that repeats every poll, and the sentinel must not
        // self-delete a live install. Only the MsiInstaller removal event
        // licenses the verb.
        int fired = 0;
        var sentinel = new UninstallSentinel(
            () => Array.Empty<string>(),
            () => fired++,
            _ => { },
            pollInterval: TimeSpan.FromMilliseconds(10),
            installedLayoutProbe: () => true,
            removalEvidence: () => false);

        var run = sentinel.RunAsync();
        await Task.Delay(300);

        Assert.Equal(0, fired);
        Assert.False(run.IsCompleted); // stays armed and polling, verb parked
    }

    [Fact]
    public async Task RunAsync_PhantomThenRealRemoval_FiresOnEvidence()
    {
        // The phantom clears the moment the real uninstall runs: the state
        // machine keeps the miss banked, and the event-log evidence arriving
        // mid-watch must fire on the next poll.
        bool evidence = false;
        int fired = 0;
        var sentinel = new UninstallSentinel(
            () => Array.Empty<string>(),
            () => fired++,
            _ => { },
            pollInterval: TimeSpan.FromMilliseconds(10),
            installedLayoutProbe: () => true,
            removalEvidence: () => evidence);

        var run = sentinel.RunAsync();
        await Task.Delay(150);          // banked misses, evidence parked it
        evidence = true;                // the Control Panel /x completes
        var winner = await Task.WhenAny(run, Task.Delay(2000));
        Assert.Same(run, winner);
        Assert.Equal(1, fired);
    }

    [Fact]
    public void Registered_MatchesTheProductNameCaseInsensitively()
    {
        Assert.True(UninstallSentinel.Registered(
            ["Some App", "Uniwill Control Utility"]));
        Assert.True(UninstallSentinel.Registered(
            ["uniwill control utility"]));
        Assert.False(UninstallSentinel.Registered(
            ["Some App", "Uniwill Control Utility Pro"]));
        Assert.False(UninstallSentinel.Registered([]));
    }

    [Fact]
    public void EnumerateMachineRegistrations_ProbeIsHiveSafe()
    {
        // A dev-box run has no machine registration: the probe must be
        // callable and must not throw. (The MsiExec UninstallString rule and
        // the HKLM-64 scoping are the field-proven contract: husk entries -
        // the velopack shim, old broken-uninstall keys - carry no MsiExec
        // string, and per-user registrations live in other hives.)
        string[] names = UninstallSentinel.EnumerateMachineRegistrations();
        Assert.All(names, name =>
            Assert.True(string.Equals(name, UninstallSentinel.ProductName, StringComparison.OrdinalIgnoreCase)));
    }
}

