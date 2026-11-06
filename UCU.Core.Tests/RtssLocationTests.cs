using UCU.Core.Rtss;

namespace UCU.Core.Tests;

/// <summary>
/// The RTSS launch guard, in pure form (no registry, no process list). The
/// field probe of a real 7.3.7 install is the reason it exists: that install's
/// uninstall entry names uninstall.exe as its DisplayIcon and leaves
/// InstallLocation empty, so a locator that trusts the icon hands the Start
/// button an uninstaller.
/// </summary>
public sealed class RtssLocationTests
{
    [Fact]
    public void TheInstallImageIsAccepted()
    {
        Assert.True(RtssLocation.IsLaunchableImage(
            @"C:\Program Files (x86)\RivaTuner Statistics Server\RTSS.exe"));
    }

    [Fact]
    public void TheImageNameIsMatchedWithoutCase()
    {
        Assert.True(RtssLocation.IsLaunchableImage(@"C:\tools\rtss.EXE"));
    }

    [Fact]
    public void TheUninstallersTheUninstallEntryNamesAreRefused()
    {
        // The real-world witness: DisplayIcon of the probed 7.3.7 install.
        Assert.False(RtssLocation.IsLaunchableImage(
            @"C:\Program Files (x86)\RivaTuner Statistics Server\uninstall.exe"));

        Assert.False(RtssLocation.IsLaunchableImage(@"C:\RivaTuner Statistics Server\Installer.exe"));
    }

    [Fact]
    public void NearMissesAreRefused()
    {
        Assert.False(RtssLocation.IsLaunchableImage(null));
        Assert.False(RtssLocation.IsLaunchableImage(string.Empty));
        Assert.False(RtssLocation.IsLaunchableImage("   "));
        Assert.False(RtssLocation.IsLaunchableImage(@"C:\RTSS"));
        Assert.False(RtssLocation.IsLaunchableImage(@"C:\RTSS.exe.bak"));
        Assert.False(RtssLocation.IsLaunchableImage(@"C:\Not RTSS.exe"));
    }

    [Fact]
    public void TheExeNameIsTheContractTheVerbEnforces()
    {
        Assert.Equal("RTSS.exe", RtssLocation.ExeName);
    }

    [Fact]
    public void TheProductIsNamedByItsKeyAndItsDisplayName()
    {
        // The probed install shape: the key name the 32-bit installer writes.
        Assert.True(RtssLocation.IsRtssInstallName("RTSS", null));

        // Any key name, the product display name (case-tolerant).
        Assert.True(RtssLocation.IsRtssInstallName("{2D67E58B-1234}", "RivaTuner Statistics Server"));
        Assert.True(RtssLocation.IsRtssInstallName("RivaTuner Statistics Server", "RIVATUNER Statistics Server"));
    }

    [Fact]
    public void UnrelatedEntriesAreNotRTSS()
    {
        // A bare RTSS substring in a display name (a path value, a plugin)
        // cannot claim the witness.
        Assert.False(RtssLocation.IsRtssInstallName("SomeTool", "My Tool RTSS plugin"));
        Assert.False(RtssLocation.IsRtssInstallName("RivaTunerOverlayHelper", null));
        Assert.False(RtssLocation.IsRtssInstallName("SomeTool", null));
        Assert.False(RtssLocation.IsRtssInstallName("SomeTool", "Some Tool"));
    }
}
