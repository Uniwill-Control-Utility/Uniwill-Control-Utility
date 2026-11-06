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
            removalEvidence: code => code is not null,
            enumerateCodes: () => ["{TEST-CODE}"]);

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
    public async Task RunAsync_RegistrationGoneBeforeArming_LayoutArmsButBindingParks()
    {
        // The race the thin uninstall wins: /x finishes in ~2 s while the
        // app's startup takes minutes - the registration is already gone at
        // the first poll, so no product code was ever watched. The layout
        // arms the sentinel, but evidence cannot bind to an unwatched code:
        // the verb stays parked (2026-10-28 field-proven: unbindable
        // firings executed 3-minute-pool ghosts and killed fresh installs
        // 16 s after launch). The complete route on such a machine is the
        // app's own uninstall entry.
        int fired = 0;
        var sentinel = new UninstallSentinel(
            () => Array.Empty<string>(),
            () => fired++,
            _ => { },
            pollInterval: TimeSpan.FromMilliseconds(10),
            installedLayoutProbe: () => true,
            removalEvidence: code => code is not null,
            enumerateCodes: () => Array.Empty<string>());

        var run = sentinel.RunAsync();
        await Task.Delay(300);

        Assert.Equal(0, fired);
        Assert.False(run.IsCompleted); // armed, parked on binding, polling
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
            removalEvidence: _ => true);

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
            removalEvidence: _ => false);

        var run = sentinel.RunAsync();
        await Task.Delay(300);

        Assert.Equal(0, fired);
        Assert.False(run.IsCompleted); // stays armed and polling, verb parked
    }

    [Fact]
    public async Task RunAsync_WatchedThenRealRemoval_FiresOnBoundEvidence()
    {
        // The canonical Control Panel route on a machine with a visible
        // registration: watched code {WATCHED}, the /x lands, evidence naming
        // that code arrives mid-watch, and the sentinel fires on the next
        // poll. This is the binding contract the 3-minute event pool alone
        // cannot provide.
        bool present = true;
        bool evidence = false;
        int fired = 0;
        var sentinel = new UninstallSentinel(
            () => present ? [UninstallSentinel.ProductName] : Array.Empty<string>(),
            () => fired++,
            _ => { },
            pollInterval: TimeSpan.FromMilliseconds(10),
            removalEvidence: code => { evidence = code is not null; return evidence; },
            enumerateCodes: () => present ? ["{WATCHED}"] : Array.Empty<string>());

        var run = sentinel.RunAsync();
        await Task.Delay(60);              // arms, watches {WATCHED}
        present = false;                   // the Control Panel /x completes
        await Task.Delay(60);              // misses bank, evidence parked
        evidence = true;                   // (flip only keeps the probe true)
        var winner = await Task.WhenAny(run, Task.Delay(2000));
        Assert.Same(run, winner);
        Assert.Equal(1, fired);
    }

    [Fact]
    public async Task RunAsync_ProtectedMachineNeverSawCode_EvidenceCannotFireVerb()
    {
        // Field-proven death loop (2026-10-28 20:57/20:59): on the Emsisoft
        // box the app never sees the registration, so _lastSeenCode stays
        // null; an "any transaction in 3 min" firing executed ghosts - a
        // test /x from the pool, and the PREVIOUS app session's self-repair
        // /x - and swept fresh installs 16-39 s after launch. The firing
        // gate must park the verb for the whole session on an unbindable
        // watch, with the app's own uninstall entry as the complete route.
        int fired = 0;
        var sentinel = new UninstallSentinel(
            () => Array.Empty<string>(),
            () => fired++,
            _ => { },
            pollInterval: TimeSpan.FromMilliseconds(10),
            installedLayoutProbe: () => true,
            removalEvidence: _ => true,     // perfect removal evidence every poll
            enumerateCodes: () => Array.Empty<string>()); // view blind from birth

        var run = sentinel.RunAsync();
        await Task.Delay(300);

        Assert.Equal(0, fired);
        Assert.False(run.IsCompleted);
    }

    [Fact]
    public async Task RunAsync_FireAsksEvidenceForTheWatchedCode()
    {
        // A previous uninstall's success event must not license the verb for
        // a fresh install: the sentinel watched {WATCHED-GUID} while the
        // registration was visible, and the evidence probe must be asked
        // about that exact code.
        string? asked = null;
        int fired = 0;
        bool present = true;
        var sentinel2 = new UninstallSentinel(
            () => present ? [UninstallSentinel.ProductName] : Array.Empty<string>(),
            () => fired++,
            _ => { },
            pollInterval: TimeSpan.FromMilliseconds(10),
            removalEvidence: code => { asked = code; return true; },
            enumerateCodes: () => present ? ["{WATCHED-GUID}"] : Array.Empty<string>());

        var run = sentinel2.RunAsync();
        await Task.Delay(60);
        present = false;                      // the Control Panel /x lands
        var winner = await Task.WhenAny(run, Task.Delay(2000));
        Assert.Same(run, winner);             // fired
        Assert.Equal(1, fired);
        Assert.Equal("{WATCHED-GUID}", asked); // bound to the watched code
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

