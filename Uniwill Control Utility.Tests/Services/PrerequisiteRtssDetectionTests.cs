using System;
using System.IO;
using System.Linq;

namespace Uniwill_Control_Utility.Tests.Services;

/// <summary>
/// The prerequisite dialog must ask "is RTSS INSTALLED", not "is RTSS running"
/// (field-reported on a fresh laptop: RTSS installed, not in the boot set, and
/// UCU popped the Required Software warning at it). The witness is RTSS's own
/// uninstall entry, read in BOTH registry views - the RTSS installer is 32-bit,
/// so on 64-bit Windows the key lands under WOW6432Node. Liveness stays
/// informational (the button label), never the prerequisite.
/// </summary>
public sealed class PrerequisiteRtssDetectionTests
{
    [Fact]
    public void TheDialogJudgesRtssByInstallationNotLiveness()
    {
        string dialog = ReadAppSource("Views", "Windows", "PrerequisiteDialog.xaml.cs");

        string row = MethodBody(dialog, "private void UpdateRtssStatus()");

        Assert.Contains("RtssDetectionService.IsInstalled()", row, StringComparison.Ordinal);
        Assert.Contains("installed ? SymbolRegular.CheckmarkCircle20", row, StringComparison.Ordinal);

        // Liveness is the button label, and the running-only prerequisite is gone.
        Assert.Contains("running ? \"Running\" : \"Installed\"", row, StringComparison.Ordinal);
        Assert.DoesNotContain("RtssReader.IsRunning()", row, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDetectionReadsBothRegistryViewsAndSelfEvidencesWhenRunning()
    {
        string service = ReadAppSource("Services", "RtssDetectionService.cs");

        // The RTSS installer is 32-bit: the 64-bit view alone would miss it.
        Assert.Contains("RegistryView.Registry64", service, StringComparison.Ordinal);
        Assert.Contains("RegistryView.Registry32", service, StringComparison.Ordinal);
        Assert.Contains(@"Uninstall\RTSS", service, StringComparison.Ordinal);

        // A live process or shared-memory segment is self-evidencing.
        Assert.Contains("Process.GetProcessesByName(\"RTSS\")", service, StringComparison.Ordinal);
        Assert.Contains("RtssReader.IsRunning()", service, StringComparison.Ordinal);

        // Detection never outlives its read (the PawnIO detection rule).
        string[] catchBlocks = service.Split("catch");
        Assert.True(catchBlocks.Length >= 2, "the registry reads must be guarded");
    }

    private static string ReadAppSource(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { RepositoryRoot(), "Uniwill Control Utility" }.Concat(parts).ToArray()));

    private static string MethodBody(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find {signature}.");

        int end = source.IndexOf("\n        }", start, StringComparison.Ordinal);
        Assert.True(end > start, $"Could not find the end of {signature}.");

        return source.Substring(start, end - start);
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "Uniwill Control Utility")) &&
                Directory.Exists(Path.Combine(directory.FullName, "Uniwill Control Utility.Tests")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root with the prerequisite dialog not found.");
    }
}
