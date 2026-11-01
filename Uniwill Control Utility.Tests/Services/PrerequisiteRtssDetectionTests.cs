using System;
using System.IO;
using System.Linq;

namespace Uniwill_Control_Utility.Tests.Services;

/// <summary>
/// The RTSS prerequisite row and its start routes, field-shaped. Two field
/// findings drive the pins: (1) the row judged RTSS by LIVENESS, so an
/// installed RTSS outside the boot set was reported as "Required Software";
/// (2) on the user's machines RTSS carries the "Run as administrator" launch
/// flag (double-clicking prompts UAC), and the elevation-free scheduled-task
/// route lands a GUI process in SESSION 0 - invisible, injecting nothing,
/// while the global shared-memory segment still answers "running". So: the row
/// judges installation, liveness is SESSION-SCOPED, and the start goes through
/// the engine (which places RTSS in the active session with the privileges its
/// flags demand).
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

        // Installed: the engine-routed start. Absent: the download page.
        Assert.Contains("RtssLauncher.StartAsync()", click, StringComparison.Ordinal);
        Assert.Contains("if (!RtssDetectionService.IsInstalled())", click, StringComparison.Ordinal);
        Assert.Contains("Process.Start(new ProcessStartInfo(RtssUrl)", click, StringComparison.Ordinal);
    }

    [Fact]
    public void LivenessIsScopedToTheSessionBecauseTheSharedMemorySegmentIsGlobal()
    {
        string service = ReadAppSource("Services", "RtssDetectionService.cs");

        string running = MethodBody(service, "public static bool IsRunning()");

        // A session-0 RTSS satisfies the global segment and serves no overlay.
        Assert.Contains("RtssLocation.TryRunningSessions(out int[] sessions)", running, StringComparison.Ordinal);
        Assert.Contains("SafeSessionId()", running, StringComparison.Ordinal);

        // No RTSS process means NOT running: the segment is not evidence of a
        // live RTSS, because RTSS's overlay DLL keeps the mapping open in every
        // game it injected (field-proven: zero processes, segment present).
        int empty = running.IndexOf("sessions.Length == 0", StringComparison.Ordinal);
        Assert.True(empty > 0, "the empty process list must be answered explicitly");
        string emptyAnswer = running.Substring(empty, Math.Min(800, running.Length - empty));
        Assert.Contains("return false;", emptyAnswer, StringComparison.Ordinal);
        Assert.DoesNotContain("RtssReader.IsRunning()", emptyAnswer, StringComparison.Ordinal);

        // The segment is the witness only when the snapshot itself is refused.
        Assert.Contains("RtssReader.IsRunning()", running, StringComparison.Ordinal);

        // Installation and location are the shared Core witnesses (engine too).
        Assert.Contains("RtssLocation.IsInstalled()", service, StringComparison.Ordinal);
        Assert.Contains("RtssLocation.Locate()", service, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSharedLocatorReadsBothViewsAndRefusesTheIconBoobyTrap()
    {
        string locator = ReadCoreSource("Rtss", "RtssLocation.cs");

        // The RTSS installer is 32-bit: the 64-bit view alone would miss it.
        Assert.Contains("RegistryView.Registry64", locator, StringComparison.Ordinal);
        Assert.Contains("RegistryView.Registry32", locator, StringComparison.Ordinal);
        Assert.Contains(@"Uninstall\RTSS", locator, StringComparison.Ordinal);

        // The probed install names uninstall.exe as its icon, so the icon is
        // accepted only as an image named RTSS.exe, and only when it exists.
        Assert.Contains("IsLaunchableImage", locator, StringComparison.Ordinal);
        Assert.Contains("IsLaunchableFile", locator, StringComparison.Ordinal);
        Assert.Contains("File.Exists", locator, StringComparison.Ordinal);

        // A running RTSS names its own image (the truth witness), and the
        // module read plus the snapshot are individually guarded.
        Assert.Contains("RunningPaths()", locator, StringComparison.Ordinal);
        Assert.True(locator.Split("catch").Length >= 5, "the OS witnesses must be guarded");
    }

    [Fact]
    public void TheStartGoesThroughTheEngineAndNeverTheSessionZeroTaskRoute()
    {
        string launcher = ReadAppSource("Services", "RtssLauncher.cs");

        // The engine places RTSS in the ACTIVE session with the linked token;
        // no consent dialog, and the process is visible to the user.
        Assert.Contains("App.RtssCapability", launcher, StringComparison.Ordinal);
        Assert.Contains("StartThroughEngineAsync", launcher, StringComparison.Ordinal);
        Assert.Contains("rtss.StartAsync()", launcher, StringComparison.Ordinal);

        // Field-proven: the one-shot Highest task puts a GUI process in session
        // 0. It must not come back as the RTSS route.
        Assert.DoesNotContain("RunOneShotHighestPrivileges", launcher, StringComparison.Ordinal);

        // Without an engine, the fallback is the user's own double-click.
        Assert.Contains("UseShellExecute = true", launcher, StringComparison.Ordinal);

        // Success is the observed process in this session, not the verb's word.
        Assert.Contains("RtssDetectionService.IsRunning()", launcher, StringComparison.Ordinal);
    }

    [Fact]
    public void TheEngineCapabilityIsRegisteredAndReachableFromTheNonDiPaths()
    {
        string app = ReadAppSource("App.xaml.cs");

        // One verb proxy, in the same capability family as the rest of the engine surface.
        Assert.Contains("services.AddSingleton<IRtssCapability, RtssCapability>();", app, StringComparison.Ordinal);

        // The prerequisite dialog is created with new, so the proxy resolves
        // from the host; null before the host exists is covered by the fallback.
        Assert.Contains("_host?.Services.GetService<IRtssCapability>()", app, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAppStartsRtssForTheBootStartAndTheOverlayStartupPair()
    {
        string app = ReadAppSource("App.xaml.cs");

        string startUp = MethodBody(app, "private static async Task AutoStartRtssForOverlayAsync()");

        // (a) "UCU is set to start with Windows" is witnessed by the autostart task.
        Assert.Contains("ScheduledTaskService.GetActionPath(AutostartTaskName) is not null", startUp, StringComparison.Ordinal);

        // (b) The Overlay page's "Enable overlay" + "Show on startup" pair demands
        // RTSS at every startup: the OSD needs RTSS live BEFORE games launch (RTSS
        // hooks at game start), and the boot-start task is not the only route the
        // app is started by. Read from the store (the toggles' source of truth).
        Assert.Contains("GetService<OverlaySettingsStore>()?.Current", startUp, StringComparison.Ordinal);
        Assert.Contains("overlay.Enabled && overlay.ShowOnStartup", startUp, StringComparison.Ordinal);

        // Neither contract -> no opinion about RTSS.
        Assert.Contains("if (!bootStarted && !overlayDemandsRtss)", startUp, StringComparison.Ordinal);

        // Installed-and-idle only, through the launcher (never a raw Process.Start).
        Assert.Contains("!RtssDetectionService.IsInstalled() || RtssDetectionService.IsRunning()", startUp, StringComparison.Ordinal);
        Assert.Contains("RtssLauncher.StartAsync()", startUp, StringComparison.Ordinal);
        Assert.DoesNotContain("Process.Start", startUp, StringComparison.Ordinal);

        // The hook is in the startup chain, fired once and unhandled-fault-free.
        Assert.Contains("SafeFireAndForget(AutoStartRtssForOverlayAsync())", app, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSessionLauncherPassesNoDebugProcessFlagToCreateProcessAsUser()
    {
        // FIELD DEFECT 2026-11-01: AppSessionLauncher passed LOGON_WITH_PROFILE
        // (0x1) into the CreateProcessAsUser flag word. LOGON_WITH_PROFILE
        // belongs to CreateProcessWithTokenW/WithLogonW; in the CreateProcess*
        // space 0x1 is DEBUG_PROCESS - every launched process attached to a
        // nonexistent debugger and froze at the ntdll image-load debug event.
        // The field shape: the RTSS "Start" button showed an RTSS.exe in Task
        // Manager that spawned no Hooks Loader (no injection, no OSD), and a
        // supervised app relaunch never painted a window - all frozen 1-thread
        // ~3 MB husks. The engine test suite has no source-reading helper, so
        // this contract is pinned here, beside the other RTSS start-route pins.
        string launcher = ReadEngineSource("AppSessionLauncher.cs");

        Assert.DoesNotContain("LOGON_WITH_PROFILE", launcher, StringComparison.Ordinal);

        // The call site passes exactly the one valid flag.
        Assert.Contains("CREATE_UNICODE_ENVIRONMENT,", launcher, StringComparison.Ordinal);
        Assert.DoesNotContain("CREATE_UNICODE_ENVIRONMENT |", launcher, StringComparison.Ordinal);
    }

    private static string ReadAppSource(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { RepositoryRoot(), "Uniwill Control Utility" }.Concat(parts).ToArray()));

    private static string ReadEngineSource(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { RepositoryRoot(), "UCU.Engine" }.Concat(parts).ToArray()));

    private static string ReadCoreSource(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { RepositoryRoot(), "UCU.Core" }.Concat(parts).ToArray()));

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
