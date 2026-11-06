namespace Uniwill_Control_Utility.Tests.Services;

/// <summary>
/// The removal-evidence filter of the uninstall sentinel: a transaction this
/// very process initiated is Velopack's registration repair, not the Control
/// Panel uninstall the sentinel is contracted to execute. Field-proven: the
/// app's own UpdateManager /x-ed the live product code 17 s after a GUI
/// install on a registry-guarded machine, the evidence rule accepted it, and
/// the sentinel swept the live app mid-splash. A wrong filter here either
/// self-deletes live installs (miss) or silences the real Control Panel route
/// (false reject) - both are product-ending on guarded machines.
/// </summary>
public sealed class SelfIssuedRemovalTests
{
    private const string Transaction1042 =
        "Ending a Windows Installer transaction: {16F35389-55B3-4F3B-8237-0EDE14B72979}. Client Process Id: 4242.";

    [Fact]
    public void ClientPidEqualToSelf_IsSelfIssuedRepair()
    {
        Assert.True(UninstallSentinel.IsSelfIssuedTransaction(Transaction1042, selfPid: 4242));
    }

    [Fact]
    public void ExternalMsiexecInitiatedRemoval_IsNotSelfIssued()
    {
        // Control Panel / Settings / shim / manual elevated msiexec all
        // initiate as their own process - the sentinel's real trigger.
        Assert.False(UninstallSentinel.IsSelfIssuedTransaction(Transaction1042, selfPid: 14132));
    }

    [Fact]
    public void PidDigitRunIsNotSubstringMatched()
    {
        // 1234 is a prefix of the client pid 12345: a naive text match would
        // call an external uninstall self-issued and silence the Control
        // Panel route. The parser must take the whole digit run.
        Assert.False(UninstallSentinel.IsSelfIssuedTransaction(Transaction1042, selfPid: 424));
        Assert.False(UninstallSentinel.IsSelfIssuedTransaction(Transaction1042, selfPid: 42422));
    }

    [Fact]
    public void MessageWithoutClientPid_FailsOpenTowardTheUninstallRoute()
    {
        // An event shape we do not recognize must never suppress evidence:
        // the Control Panel uninstall must keep firing the sweep.
        Assert.False(UninstallSentinel.IsSelfIssuedTransaction(
            "Ending a Windows Installer transaction: {16F35389-55B3-4F3B-8237-0EDE14B72979}.", 4242));
    }
}
