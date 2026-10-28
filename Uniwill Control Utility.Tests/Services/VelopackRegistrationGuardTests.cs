using Uniwill_Control_Utility.Services;

namespace Uniwill_Control_Utility.Tests.Services;

/// <summary>
/// The registration-repair self-destruction guard, pinned to its closed form.
/// Velopack's per-machine registration check is an IN-PROCESS registry read:
/// on a machine whose security product polices registry views, the check sees
/// the live MSI registration as missing and the repair logic runs
/// "msiexec /x" on the LIVE product code - arming the uninstall sentinel's
/// sweep, killing the app at "Initializing" while Program Files vanishes.
/// Field-proven from OPPOSITE registry-view directions on the same machine:
/// 20:57 with the ARP keys blind, and 21:37 with ARP VISIBLE while Velopack's
/// own MSI-classes hive read was the blind one - so a guard that consults any
/// registry view is whack-a-mole against a policy the app cannot observe.
/// The rule that closes the class: the per-machine (MSI) layout never builds
/// the UpdateManager; its updates arrive by MSI. Portable/dev layouts keep
/// the Velopack flow (their root has no MSI registration to destroy).
/// </summary>
public sealed class VelopackRegistrationGuardTests
{
    [Fact]
    public void MachineLayout_NeverBuildsTheUpdateManager()
    {
        // The MSI layout, every registry-view state (visible, blind,
        // inconsistent): Velopack's repair is unguardable from in-process,
        // and the layout probe needs no registry at all.
        Assert.True(VelopackUpdateService.ShouldSkipVelopack(machineLayoutInstalled: true));
    }

    [Fact]
    public void PortableOrDevLayout_KeepsTheVelopackFlow()
    {
        // No per-machine layout: no MSI registration for a repair to destroy;
        // the update flow (feed check, silent download, exit apply) stays on.
        Assert.False(VelopackUpdateService.ShouldSkipVelopack(machineLayoutInstalled: false));
    }
}
