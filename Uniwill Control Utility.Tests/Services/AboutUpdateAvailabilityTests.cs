using System;
using System.IO;
using System.Linq;

namespace Uniwill_Control_Utility.Tests.Services;

/// <summary>
/// Field 2026-11-10: an installed build's About card said "Updates are not
/// available for this installation type (portable)" — a build type the product no
/// longer ships, chosen by a layout probe no dev machine could ever exercise.
///
/// The final shape: the card states the real mechanism (the MSI installer) and
/// offers a "Check for updates" button that only asks GitHub whether a newer
/// release exists, plus a "Download installer" button that opens the release page.
/// Nothing is downloaded or applied in-process — the self-update flow stays
/// deleted, because Velopack's in-process per-machine registration repair could
/// <c>/x</c> the live product (see the runbook).
/// </summary>
public sealed class AboutUpdateAvailabilityTests
{
    [Fact]
    public void AboutCard_StatesTheInstaller_AndOffersTheCheckButton()
    {
        string xaml = ReadAppFile("Views", "Pages", "AboutPage.xaml");
        string code = ReadAppFile("Views", "Pages", "AboutPage.xaml.cs");

        // Caption: short, and it names the artifact the product actually ships.
        Assert.Contains("Updates ship as Uniwill-Control-Utility-win.msi.", xaml, StringComparison.Ordinal);
        Assert.Contains("Updates ship as Uniwill-Control-Utility-win.msi.", code, StringComparison.Ordinal);

        // The button is visible from the start (in XAML), and its actions are wired.
        Assert.Contains("x:Name=\"btnCheck\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"btnCheck_Click\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"btnDownload_Click\"", xaml, StringComparison.Ordinal);
        Assert.Contains("UpdateChecker.GetLatestAsync()", code, StringComparison.Ordinal);
        Assert.Contains("UpdateChecker.IsNewer(", code, StringComparison.Ordinal);

        // No layout probe, no portable wording: both were the bug.
        Assert.DoesNotContain("InstalledLayoutPresent", code, StringComparison.Ordinal);
        Assert.DoesNotContain("installation type (portable)", code, StringComparison.Ordinal);
        Assert.DoesNotContain("installation type (portable)", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSelfUpdateFlow_StaysRemoved_AndThePackagedEntryPointStays()
    {
        string[] removed = { "VelopackUpdateService", "UpdatePolicy", "UpdateState", "AutoUpdateApply", "UCU.Updater", "ApplyUpdatesAndExit", "TryApplyOnExit" };
        string root = RepositoryRoot();
        string[] trees =
        {
            Path.Combine(root, "Uniwill Control Utility"),
            Path.Combine(root, "build"),
            Path.Combine(root, "UCU.Core"),
            Path.Combine(root, "UCU.Contracts"),
            Path.Combine(root, "UCU.Hardware"),
            Path.Combine(root, "UCU.Engine"),
            Path.Combine(root, "UCU.Client"),
        };

        var hits = trees
            .Where(Directory.Exists)
            .SelectMany(tree => Directory.EnumerateFiles(tree, "*.*", SearchOption.AllDirectories))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                        && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                        && (path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                            || path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)
                            || path.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase)))
            .SelectMany(path => removed
                .Where(token => File.ReadAllText(path).Contains(token, StringComparison.Ordinal))
                .Select(token => $"{path}: {token}"));

        Assert.Empty(hits);

        string app = ReadAppFile("App.xaml.cs");
        Assert.Contains("VelopackApp.Build()", app, StringComparison.Ordinal);
        Assert.Contains("velopackuninstall", app, StringComparison.Ordinal);
    }

    private static string ReadAppFile(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { RepositoryRoot(), "Uniwill Control Utility" }.Concat(parts).ToArray()));

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
