using UCU.Core.Supervision;

namespace UCU.Core.Tests.Supervision;

/// <summary>
/// The U-110 block diagnostic as pure policy. The hard codes are the SCM's
/// own word (1275 = StartService refused the driver because Core Isolation
/// blocks it — the code OpenRevo's driver manager recognizes at
/// driver\bld_driver_mgr.rs; 577 = the image failed integrity-hash
/// verification); the inference path is UCU's shape, where the vendor
/// installer hides the start code and the app only observes "device
/// absent". The policy's discipline: a hard code wins and says exactly what
/// it saw; the inference fires ONLY on HVCI-on + driver-installed + no
/// observed code, so the copy may say "is blocking" truthfully; every other
/// combination stays silent — the app keeps its existing generic copy
/// (acceptance b), because a diagnostic that guesses is worse than none.
/// </summary>
public sealed class PawnIoDriverBlockPolicyTests
{
    [Fact]
    public void TheNamedCodes_AreTheWindowsContracts()
    {
        Assert.Equal(1275, PawnIoDriverBlockPolicy.ErrorDriverBlockedByCoreIsolation); // ERROR_DRIVER_BLOCKED_BY_CORE_ISOLATION
        Assert.Equal(577, PawnIoDriverBlockPolicy.ErrorInvalidImageHash);              // ERROR_INVALID_IMAGE_HASH
    }

    [Theory]
    // The hard codes answer on their own, whatever the inference inputs say.
    [InlineData(1275, false, false, PawnIoBlockReason.CoreIsolation)]
    [InlineData(1275, true, true, PawnIoBlockReason.CoreIsolation)]
    [InlineData(577, false, false, PawnIoBlockReason.UnsignedImage)]
    [InlineData(577, true, false, PawnIoBlockReason.UnsignedImage)]
    // Every other code is not this diagnostic: 2/3 "device absent" with
    // HVCI off says nothing, 5 "access denied" means the device EXISTS
    // (not blocked), and a hard code never triggers the inference — the
    // start was observed and it did not name HVCI.
    [InlineData(2, false, false, PawnIoBlockReason.None)]
    [InlineData(3, true, true, PawnIoBlockReason.None)]
    [InlineData(5, true, true, PawnIoBlockReason.None)]
    [InlineData(1053, true, true, PawnIoBlockReason.None)]
    public void Diagnose_HardCodesWin_TheInferenceNeverOverlaysThem(
        int code, bool hvciOn, bool driverInstalled, PawnIoBlockReason expected)
    {
        Assert.Equal(
            expected,
            PawnIoDriverBlockPolicy.Diagnose(code, hvciOn, driverInstalled));
    }

    [Theory]
    // The inference: installed driver + Memory Integrity ON + no observed
    // start code is the HVCI shape on UCU's vendor-installer flow.
    [InlineData(true, true, PawnIoBlockReason.CoreIsolation)]
    // HVCI off: the absence has some other cause, and the copy would be a
    // guess. Driver not installed: that is just "not installed" — the
    // existing flow already handles it.
    [InlineData(false, true, PawnIoBlockReason.None)]
    [InlineData(true, false, PawnIoBlockReason.None)]
    [InlineData(false, false, PawnIoBlockReason.None)]
    public void Diagnose_InferencePath_NeedsNoCodeAndHvciOnAndInstalledDriver(
        bool hvciOn, bool driverInstalled, PawnIoBlockReason expected)
    {
        Assert.Equal(
            expected,
            PawnIoDriverBlockPolicy.Diagnose(null, hvciOn, driverInstalled));
    }

    [Fact]
    public void Diagnose_NullWithNothingInstalled_IsSilent_TheExistingFlowOwnsThatCase()
    {
        // "Device absent, HVCI on, no driver entry" is the not-installed
        // shape; naming HVCI there would block-blame a driver that was
        // never installed.
        Assert.Equal(
            PawnIoBlockReason.None,
            PawnIoDriverBlockPolicy.Diagnose(startWin32Code: null, memoryIntegrityOn: true, driverEntryInstalled: false));
    }
}
