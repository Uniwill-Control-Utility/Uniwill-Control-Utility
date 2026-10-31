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
/// informational (the button label), never the prerequisite - and installed
/// with RTSS not running offers Start, through the elevation-free route (the
/// field box runs RTSS with the "Run as administrator" flag: double-clicking
/// it prompts UAC, so the start must not be a plain Process.Start).
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
        Assert.Contains("running ? \"Running\" : \"Start\"", row, StringComparison.Ordinal);
        Assert.DoesNotContain("RtssReader.IsRunning()", row, StringComparison.Ordinal);

        // Installed-idle is actionable; running (and absent RTSS) is not.
        Assert.Contains("installed && !running && RtssLauncher.CanStart()", row, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDialogButtonStartsInstalledRtssAndOnlyDownloadsAbsentRtss()
    {
        string dialog = ReadAppSource("Views", "Windows", "PrerequisiteDialog.xaml.cs");

        string click = MethodBody(dialog, "private async void RtssButton_Click");

        // Installed: the elevation-free start. Absent: the download page.
        Assert.Contains("RtssLauncher.StartAsync()", click, StringComparison.Ordinal);
        Assert.Contains("if (!RtssDetectionService.IsInstalled())", click, StringComparison.Ordinal);
        Assert.Contains("Process.Start(new ProcessStartInfo(RtssUrl)", click, StringComparison.Ordinal);
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

    [Fact]
    public void TheExeLocationRefusesTheUninstallEntryBoobyTrapAndVerifiesFiles()
    {
        string service = ReadAppSource("Services", "RtssDetectionService.cs");

        string locate = MethodBody(service, "public static string? LocateExe()");

        // The uninstall entry is a location witness, read in both views.
        Assert.Contains("InstallLocation", locate, StringComparison.Ordinal);
        Assert.Contains("DisplayIcon", locate, StringComparison.Ordinal);
        Assert.Contains("RegistryView.Registry64", locate, StringComparison.Ordinal);
        Assert.Contains("RegistryView.Registry32", locate, StringComparison.Ordinal);

        // Field probe of a real 7.3.7 install: DisplayIcon names uninstall.exe
        // and InstallLocation is empty. An unguarded DisplayIcon would make
        // the Start button launch RTSS's own uninstaller - the icon is
        // accepted only when it names RTSS.exe.
        Assert.Contains("Path.GetFileName(icon), \"RTSS.exe\"", locate, StringComparison.Ordinal);

        // A path is returned only when the file exists - launchable by contract.
        Assert.Contains("File.Exists", locate, StringComparison.Ordinal);
        Assert.DoesNotContain("Process.Start", locate, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRunningRtssProcessIsTheFirstLocationWitness()
    {
        string service = ReadAppSource("Services", "RtssDetectionService.cs");

        string locate = MethodBody(service, "public static string? LocateExe()");
        Assert.Contains("RunningExePath()", locate, StringComparison.Ordinal);

        string witness = MethodBody(service, "private static string? RunningExePath()");

        // The running image path is the truth (field-proven: a 64-bit reader
        // can name a 32-bit RTSS's image), and a denied module read is not fatal.
        Assert.Contains("process.MainModule?.FileName", witness, StringComparison.Ordinal);
        Assert.Contains("File.Exists", witness, StringComparison.Ordinal);
        Assert.True(witness.Split("catch").Length >= 3, "the module read and the snapshot must both be guarded");
    }

    [Fact]
    public void StartingRtssGoesThroughTheElevationFreeTaskRoute()
    {
        string launcher = ReadAppSource("Services", "RtssLauncher.cs");

        // The proven pattern (touchpad settings flow, uninstall verb): a
        // one-shot Highest-privileges task supplies the full token that RTSS's
        // "Run as administrator" flag demands, with no consent dialog.
        Assert.Contains("ScheduledTaskService.RunOneShotHighestPrivileges", launcher, StringComparison.Ordinal);

        // The fallback behaves like double-clicking (one prompt), and success
        // is the verified process, not the task's exit code.
        Assert.Contains("UseShellExecute = true", launcher, StringComparison.Ordinal);
        Assert.Contains("RtssDetectionService.IsRunning()", launcher, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAppStartsRtssOnlyForTheBootStartedAppAndNeverTheEngine()
    {
        string app = ReadAppSource("App.xaml.cs");

        string startUp = MethodBody(app, "private static async Task AutoStartRtssForBootStartAsync()");

        // "UCU is set to start with Windows" is witnessed by the autostart task.
        Assert.Contains("ScheduledTaskService.GetActionPath(AutostartTaskName) is null", startUp, StringComparison.Ordinal);

        // Installed-and-idle only, through the launcher (never a raw Process.Start).
        Assert.Contains("!RtssDetectionService.IsInstalled() || RtssDetectionService.IsRunning()", startUp, StringComparison.Ordinal);
        Assert.Contains("RtssLauncher.StartAsync()", startUp, StringComparison.Ordinal);
        Assert.DoesNotContain("Process.Start", startUp, StringComparison.Ordinal);

        // The hook is in the startup chain, fired once and unhandled-fault-free.
        Assert.Contains("SafeFireAndForget(AutoStartRtssForBootStartAsync())", app, StringComparison.Ordinal);
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
