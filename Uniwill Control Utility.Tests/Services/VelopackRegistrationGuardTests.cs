using Uniwill_Control_Utility.Services;

namespace Uniwill_Control_Utility.Tests.Services;

/// <summary>
/// The registration-repair self-destruction guard. On a machine whose security
/// product blinds the app's ARP registry view, Velopack concludes the per-machine
/// install is unregistered and its repair logic runs msiexec /x on the LIVE
/// product code - field-proven (19:41 at +4:16, 20:57 at +16 s, 20:59 at +39 s
/// after GUI installs), each time removing the live registration and licensing
/// the uninstall sentinel's sweep: the app dies at 78 % "Initializing" and
/// Program Files disappears. The guard skips building the UpdateManager exactly
/// under that condition; a wrong rule either disables updates on healthy
/// machines (false positive) or keeps the self-destruction (false negative).
/// The MSI-database probe must NOT be part of the rule: it reads the registry
/// in-process, so the same guard blinds it (field-proven: the first version of
/// this guard, built on MsiEnumProductsW, never fired while the repair /x did).
/// </summary>
public sealed class VelopackRegistrationGuardTests
{
    [Fact]
    public void MachineLayoutWithBlindRegistryView_IsThePolicedCondition()
    {
        // The guarded machine: the app runs from Program Files (installed),
        // its ARP view is blind - Velopack would "repair" = /x the live code.
        Assert.True(VelopackUpdateService.ShouldSkipVelopack(
            machineLayoutInstalled: true, registryViewSeesRegistration: false));
    }

    [Fact]
    public void HealthyInstalledMachine_KeepsTheUpdateFlow()
    {
        Assert.False(VelopackUpdateService.ShouldSkipVelopack(
            machineLayoutInstalled: true, registryViewSeesRegistration: true));
    }

    [Fact]
    public void PortableOrDevLayout_HasNoInstallToDestroy()
    {
        // No machine layout: Velopack's own IsInstalled gate no-ops; the
        // guard must not touch these layouts' update behaviour.
        Assert.False(VelopackUpdateService.ShouldSkipVelopack(
            machineLayoutInstalled: false, registryViewSeesRegistration: false));
    }

    [Fact]
    public void VisibleRegistrationWithoutLayout_KeepsVelopack()
    {
        // A registration husk visible while the layout is gone (mid-sweep,
        // interrupted uninstall): no layout to self-destruct, no skip.
        Assert.False(VelopackUpdateService.ShouldSkipVelopack(
            machineLayoutInstalled: false, registryViewSeesRegistration: true));
    }
}
