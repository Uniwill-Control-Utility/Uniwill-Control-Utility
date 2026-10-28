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
