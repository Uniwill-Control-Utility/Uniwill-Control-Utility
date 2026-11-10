using System;
using System.IO;
using System.Linq;

namespace Uniwill_Control_Utility.Tests.Services;

/// <summary>
/// The release asset contract. UCU has no in-app updater (2026-11-10: the
/// Velopack <c>UpdateManager</c> flow was deleted after its in-process
/// per-machine registration repair destroyed live installs), so the packing
/// script must ship the per-machine MSI and nothing that only an updater reads:
/// no delta pass, no feed seeding, no nupkg / releases.win.json in the release.
/// </summary>
public sealed class ReleaseAssetTests
{
    [Fact]
    public void PackScript_ShipsTheMsi_AndKeepsSetupPayload()

    {
        string script = ReadRepoFile("build", "pack.ps1");

        // The MSI is a direct-install route, and the Setup bootstrapper installs
        // from the FULL nupkg - so that package and the feed index are shipped
        // (deleting them broke Setup.exe in the field-report sense: it is a
        // downloader, not a bundle).
        Assert.Contains("--msi --instLocation PerMachine", script, StringComparison.Ordinal);
        Assert.Contains("-delta\\.nupkg$", script, StringComparison.Ordinal);
        Assert.DoesNotContain("-full\\.nupkg$' -or $_.Name -eq 'releases", script, StringComparison.Ordinal);

        // The upload manifest offers installers plus Setup's payload.
        Assert.Contains("-full\\.nupkg$", script, StringComparison.Ordinal);
        Assert.Contains("releases.win.json", script, StringComparison.Ordinal);

        // The updater-only half stays gone: no delta pass, no feed download.
        Assert.DoesNotContain("--delta", script, StringComparison.Ordinal);
        Assert.DoesNotContain("vpk download github", script, StringComparison.Ordinal);
    }

    private static string ReadRepoFile(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { RepositoryRoot() }.Concat(parts).ToArray()));

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "Uniwill Control Utility")) &&
                File.Exists(Path.Combine(directory.FullName, "Uniwill Control Utility.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Repository root not found from " + AppContext.BaseDirectory);
    }
}
