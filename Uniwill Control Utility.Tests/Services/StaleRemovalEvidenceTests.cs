namespace Uniwill_Control_Utility.Tests.Services;

/// <summary>
/// The time floor of the uninstall sentinel's evidence rule - the third
/// binding after code and pid. Field-proven death (21:55:22): the user ran
/// the uninstall shim ({09C0B11B} /x at 21:53:59, genuine 11724 + code-bound
/// 1042), reinstalled the SAME package 67 s later - pack.ps1 pins the product
/// code per package, so the fresh install carries the same code - and the
/// relaunched app's sentinel (its registry view visible that run, so the
/// binding gate was satisfiable) found the OLD uninstall's fingerprint in its
/// 3-minute pool naming exactly the watched code. Code bound, pid external,
/// window honored: the verb fired and the fresh install died 16 s after
/// launch. Events older than this process started cannot describe THIS
/// install's uninstall - the app that outlives a real removal started before
/// it - so a firing's evidence must postdate the process start, both halves.
/// A missing floor: sentinel sweeps fresh installs after every
/// uninstall-reinstall-restart inside 3 minutes. An over-strict floor
/// (e.g., ignoring the name-only 11724's staleness): silence stays fine,
/// the shim route completes - the misses here are cheap, the false fires
/// are product-ending, which is why every entry in the pool is floored.
/// </summary>
public sealed class StaleRemovalEvidenceTests
{
    private static readonly DateTime ProcessStart = new(2026, 10, 28, 21, 55, 7);
    private const string Code = "{09C0B11B-B6FB-4FBE-885A-6DCEE6D3D5F2}";
    private const int SelfPid = 5996;

    private static UninstallSentinel.EvidenceEntry Completed(DateTime when) =>
        new(11724, when, "Product: Uniwill Control Utility -- Removal completed successfully.");

    private static UninstallSentinel.EvidenceEntry Ended(
        DateTime when, int clientPid, string code = Code) =>
        new(1042, when, $"Ending a Windows Installer transaction: {code}. Client Process Id: {clientPid}.");

    [Fact]
    public void UninstallPredatingThisProcess_IsNotEvidenceForThisInstall()
    {
        // The exact 21:55 death: shim /x at 21:53:59 (87 s before the app
        // started), same package reinstalled at 21:55:06, sentinel watches
        // the identical code. The stale fingerprint must not license the verb.
        var entries = new[]
        {
            Ended(ProcessStart.AddSeconds(-87), clientPid: 27980),
            Completed(ProcessStart.AddSeconds(-88)),
        };

        Assert.False(UninstallSentinel.HasRemovalEvidence(entries, Code, SelfPid, ProcessStart));
    }

    [Fact]
    public void ControlPanelRemovalAfterStartup_IsEvidence()
    {
        // The canonical route: app running, user removes via Control Panel,
        // thin /x lands, sentinel starts before the transaction - both
        // halves postdate the floor, the verb fires.
        var entries = new[]
        {
            Ended(ProcessStart.AddSeconds(600), clientPid: 14132),
            Completed(ProcessStart.AddSeconds(599)),
        };

        Assert.True(UninstallSentinel.HasRemovalEvidence(entries, Code, SelfPid, ProcessStart));
    }

    [Fact]
    public void FreshCodeBoundHalf_StaleNameOnlyHalf_IsNotEvidence()
    {
        // 11724 carries no product code, only the DisplayName - so a fresh
        // 1042 from an unrelated transaction must not pair with OUR old
        // 11724. Every entry counts against the floor, not just the code-
        // bound one.
        var entries = new[]
        {
            Ended(ProcessStart.AddSeconds(30), clientPid: 14132),
            Completed(ProcessStart.AddSeconds(-88)),
        };

        Assert.False(UninstallSentinel.HasRemovalEvidence(entries, Code, SelfPid, ProcessStart));
    }

    [Fact]
    public void SelfIssuedRemoval_AfterTheFloor_IsStillRefused()
    {
        // The floor composes with the self-pid filter: the app's own /x (the
        // repair, should it ever exist again) is refused even when fresh.
        var entries = new[]
        {
            Ended(ProcessStart.AddSeconds(15), clientPid: SelfPid),
            Completed(ProcessStart.AddSeconds(14)),
        };

        Assert.False(UninstallSentinel.HasRemovalEvidence(entries, Code, SelfPid, ProcessStart));
    }

    [Fact]
    public void ForeignCodeTransaction_DoesNotLicenseOurVerb()
    {
        // Fresh and external, but bound to a different product code: the
        // code binding stands next to the floor.
        var entries = new[]
        {
            Ended(ProcessStart.AddSeconds(30), clientPid: 14132, code: "{OTHER-CODE}"),
            Completed(ProcessStart.AddSeconds(29)),
        };

        Assert.False(UninstallSentinel.HasRemovalEvidence(entries, Code, SelfPid, ProcessStart));
    }
}
