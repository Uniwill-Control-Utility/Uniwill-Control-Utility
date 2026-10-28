using Uniwill_Control_Utility.Services;

namespace Uniwill_Control_Utility.Tests.Services;

/// <summary>
/// The registration-repair self-destruction guard. On a machine whose security
/// product polices the ARP keys, Velopack's own registration check sees the
/// live install as missing and its repair logic runs msiexec /x on the LIVE
/// product code - field-proven twice (19:41 at +4:16, 20:32 at +17 s after a
/// GUI install), each time removing the live registration and licensing the
/// uninstall sentinel's sweep. The guard skips building the UpdateManager
/// exactly under that condition; a wrong rule either disables updates on
/// healthy machines (false positive) or keeps the self-destruction (false
/// negative).
/// </summary>
public sealed class VelopackRegistrationGuardTests
{
    [Fact]
    public void MsiDatabaseSeesIt_RegistryViewBlind_IsThePolicedCondition()
    {
        // The guarded machine: msiserver/SYSTEM see the product, the app's
        // own ARP view is blind - Velopack would "repair" = /x the live code.
        Assert.True(VelopackUpdateService.ShouldSkipVelopack(
            msiDatabaseSeesProduct: true, registryViewSeesProduct: false));
    }

    [Fact]
    public void HealthyMachine_KeepsTheUpdateFlow()
    {
        Assert.False(VelopackUpdateService.ShouldSkipVelopack(
            msiDatabaseSeesProduct: true, registryViewSeesProduct: true));
    }

    [Fact]
    public void PortableOrDevLayout_HasNoRepairTargetAndKeepsVelopack()
    {
        // No MSI product: the repair has nothing to /x; Velopack's own
        // IsInstalled gate handles the portable no-op.
        Assert.False(VelopackUpdateService.ShouldSkipVelopack(
            msiDatabaseSeesProduct: false, registryViewSeesProduct: false));
    }

    [Fact]
    public void RegistrationHuskWithoutMsiProduct_KeepsVelopack()
    {
        // A real /x leaves the ARP husk visible while the MSI database is
        // clean - mid-uninstall shape, not the policed condition.
        Assert.False(VelopackUpdateService.ShouldSkipVelopack(
            msiDatabaseSeesProduct: false, registryViewSeesProduct: true));
    }
}
