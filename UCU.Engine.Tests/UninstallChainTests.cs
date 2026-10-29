namespace UCU.Engine.Tests;

/// <summary>
/// The uninstall chain the elevated install pass repairs. Control Panel's entry for an
/// MSI install runs <c>msiexec /x {GUID}</c>, which leaves the app-registered engine
/// service and the per-user Velopack ARP entry behind (field reports: the service
/// survived the uninstall, a husk entry stayed in the program list). The fix points the
/// entry at the layout's <c>Uninstall.exe</c> shim, so the path derivation - from the
/// engine exe the install pass runs from, to the shim beside the app root - has to be
/// right: a wrong path makes Control Panel launch nothing.
/// </summary>
public sealed class UninstallChainTests
{
    [Fact]
    public void CompressProductCode_MatchesTheRealMsiRegistrationKeys()
    {
        // Field-verified pairs: the UserData\<SID>\Products record keys read
        // back from a machine carrying these registrations. The sweep deletes
        // these keys, so a wrong form silently deletes nothing.
        Assert.Equal(
            "54792657DF4DB274387B84C7563DF230",
            EngineInstaller.CompressProductCode("{75629745-D4FD-472B-83B7-487C65D32F03}"));
        Assert.Equal(
            "D6FDFD89F775DD94196370D49A1980A7",
            EngineInstaller.CompressProductCode("{98DFDF6D-577F-49DD-9136-074DA991087A}"));
    }

    [Fact]
    public void ShimPath_ResolvesFromTheServiceDirectoryToTheLayoutRoot()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ucu-uninstall-chain-" + Guid.NewGuid().ToString("N"));
        string serviceDir = Path.Combine(dir, "current", "Service");
        Directory.CreateDirectory(serviceDir);

        try
        {
            string engine = Path.Combine(serviceDir, "Uniwill Control Utility Engine.exe");
            File.WriteAllText(engine, string.Empty);

            // Dev layout first: no shim next to the app, so nothing is redirected.
            Assert.Null(EngineInstaller.UninstallShimPath(engine));

            string shim = Path.Combine(dir, "current", EngineInstaller.ShimFileName);
            File.WriteAllText(shim, string.Empty);

            Assert.Equal(shim, EngineInstaller.UninstallShimPath(engine));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ShimPlacementTarget_PlacesTheShimOnlyWhereThePackedEntryPoints()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ucu-shim-placement-" + Guid.NewGuid().ToString("N"));
        string appDir = Path.Combine(dir, "current");
        string serviceDir = Path.Combine(appDir, "Service");
        Directory.CreateDirectory(serviceDir);

        try
        {
            string shim = Path.Combine(appDir, EngineInstaller.ShimFileName);
            File.WriteAllText(shim, string.Empty);

            // The per-machine install signature: the packed entry expands to
            // the layout root, the shim ships inside current\ - placement.
            string rootTarget = Path.Combine(dir, EngineInstaller.ShimFileName);
            Assert.Equal(rootTarget,
                EngineInstaller.ShimPlacementTarget("\"" + rootTarget + "\"", dir, shim));

            // Target already valid (a layout where INSTALLFOLDER expands to
            // the app dir): nothing to do.
            Assert.Null(EngineInstaller.ShimPlacementTarget("\"" + shim + "\"", dir, shim));

            // Never write outside the layout tree, follow nothing, name nothing else.
            Assert.Null(EngineInstaller.ShimPlacementTarget(
                "\"C:\\Windows\\Temp\\Uninstall.exe\"", dir, shim));
            Assert.Null(EngineInstaller.ShimPlacementTarget(
                "\"" + Path.Combine(dir, "Uninstall.bat") + "\"", dir, shim));
            Assert.Null(EngineInstaller.ShimPlacementTarget(null, dir, shim));
            Assert.Null(EngineInstaller.ShimPlacementTarget("   ", dir, shim));
            Assert.Null(EngineInstaller.ShimPlacementTarget(
                "\"" + rootTarget + "\"", null, shim));

            // Malformed paths die quietly: the belt never throws at install.
            Assert.Null(EngineInstaller.ShimPlacementTarget("|x|Uninstall.exe", dir, shim));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void MsiProductKey_IsOnlySearchedAmongGuidShapedKeys()
    {
        // The scan reads a display name out of HKLM\...\Uninstall; the shape filter is
        // what keeps it from opening every key (the list contains hotfix and OEM keys,
        // some of them protected). A non-GUID name must never be returned as a product.
        Assert.Null(EngineInstaller.FindMsiProductKey("UCU-Definitely-Not-Installed-" + Guid.NewGuid()));
    }

    [Fact]
    public void ShimFileAndMsiDisplayName_AreTheNamesTheShimScansFor()
    {
        // The shim's own batch deletes the ARP entries it finds by these names, and
        // pack.ps1 compiles Uninstall.exe into the publish dir root. If either name
        // drifts, the redirect points at an entry the shim will not remove.
        Assert.Equal("Uninstall.exe", EngineInstaller.ShimFileName);
        Assert.Equal("Uniwill Control Utility", EngineInstaller.MsiDisplayName);
    }
}
