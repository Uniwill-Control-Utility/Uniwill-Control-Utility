using System.IO;

namespace Uniwill_Control_Utility.Tests.UI;

/// <summary>
/// The MiniLED local-dimming row (Display page + tray) and its elevation,
/// plus the dead-code removals that came with it. Executing the OEM ACPI WMI
/// method (AcpiTest_MULong.GetSetULong) demands root\WMI namespace write
/// access an unelevated process does not have — on the guarded field
/// machines both rows were silently dead: the InvokeMethod threw access
/// denied into a catch returning a bare false, no prompt to explain. The
/// handshake now routes through the engine's device verb (LocalSystem
/// executes it; no prompt exists to decline), the app's WMI code survives as
/// the route's fallback, and the inert runas decorations (Run_CLI, the
/// restart button) are retired with the overlay-scheme call's status now
/// logged. These tests pin the routing and the removals.
/// </summary>
public sealed class MiniLedRelocationTests
{
    [Fact]
    public void MiniLedAccess_RoutesEngineFirst_TheLocalWmi_IsFallbackOnly()
    {
        string source = Read("Scripts", "Misc", "MiniLedAccess.cs");

        int engine = source.IndexOf("SetMiniLedDimmingAsync", StringComparison.Ordinal);
        int local = source.IndexOf("LocalHandshake", StringComparison.Ordinal);

        Assert.True(engine >= 0, "MiniLedAccess must go through the engine capability.");
        Assert.True(local >= 0, "the app's WMI handshake stays as the fallback.");
        Assert.True(engine < local,
            "the engine verb is the FIRST route; the unelevated WMI invoke (the access-denied silent death) is the fallback, never the default.");
        Assert.Contains("local route", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MiniLedAccess_Constants_MatchTheEngineService_HandshakeCannotDrift()
    {
        Assert.Equal(
            (UCU.Hardware.Devices.MiniLedWmiService.CmdEnableStep1,
             UCU.Hardware.Devices.MiniLedWmiService.CmdEnableStep2,
             UCU.Hardware.Devices.MiniLedWmiService.CmdDisableStep1,
             UCU.Hardware.Devices.MiniLedWmiService.CmdDisableStep2,
             UCU.Hardware.Devices.MiniLedWmiService.ExpectedEnableStep1,
             UCU.Hardware.Devices.MiniLedWmiService.ExpectedEnableStep2,
             UCU.Hardware.Devices.MiniLedWmiService.ExpectedDisableStep1,
             UCU.Hardware.Devices.MiniLedWmiService.ExpectedDisableStep2,
             UCU.Hardware.Devices.MiniLedWmiService.DefaultInstanceName),
            (Scripts.Misc.MiniLedAccess.CmdEnableStep1,
             Scripts.Misc.MiniLedAccess.CmdEnableStep2,
             Scripts.Misc.MiniLedAccess.CmdDisableStep1,
             Scripts.Misc.MiniLedAccess.CmdDisableStep2,
             Scripts.Misc.MiniLedAccess.ExpectedEnableStep1,
             Scripts.Misc.MiniLedAccess.ExpectedEnableStep2,
             Scripts.Misc.MiniLedAccess.ExpectedDisableStep1,
             Scripts.Misc.MiniLedAccess.ExpectedDisableStep2,
             Scripts.Misc.MiniLedAccess.DefaultInstanceName));
    }

    [Fact]
    public void DisplayPage_LocalDimming_Delegates_WmiCodeIsGone_EcReadbackStays()
    {
        string source = Read("Views", "Pages", "Display.xaml.cs");

        Assert.Contains("MiniLedAccess.SetLocalDimming(enable)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ManagementObject", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetSetULong", source, StringComparison.Ordinal);

        // The EC 0x72B readback verify stays page-side: it rides the
        // telemetry verb, which is already engine-routed.
        Assert.Contains("EcLocalDimmingRegister", source, StringComparison.Ordinal);

        // The restart button's inert runas decoration is gone: the console
        // user holds SeShutdownPrivilege, the verb only bought a UAC prompt.
        Assert.DoesNotContain("Verb = \"runas\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Verb=\"runas\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TrayQuickControl_LocalDimming_Delegates_NoDuplicatedWmi()
    {
        string source = Read("Services", "TrayQuickControlService.cs");

        Assert.Contains("MiniLedAccess.SetLocalDimming(enable)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetSetULong", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AcpiTest_MULong", source, StringComparison.Ordinal);
    }

    [Fact]
    public void InertRunAs_CLI_IsRetired_GameLaunches_ShellExecute()
    {
        string gameManager = Read("Scripts", "Game_Manager.cs");

        // Run_CLI set Verb="runas" under UseShellExecute=false, where the
        // verb is inert: the class is deleted and the game-client launches
        // are plain shell-execute Process.Start calls.
        Assert.False(File.Exists(Path.Combine(RepositoryRoot(), "Uniwill Control Utility", "Scripts", "Intel Backend", "Run_CLI.cs")));
        Assert.DoesNotContain("Run_CLI.RunCommand", gameManager, StringComparison.Ordinal);
    }

    [Fact]
    public void OverlayScheme_Applies_LogTheStatusCode()
    {
        string source = Read("Scripts", "RyzenAdj_To_UCU.cs");

        // PowerSetActiveOverlayScheme is a standard-user powrprof act (no
        // elevation to route), so the fix is the witness, not a verb: the
        // calls used to discard the status code, leaving a failed scheme
        // switch with no trace.
        Assert.Contains("status=0x{overlayStatus:X}", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_ = PowerSetActiveOverlayScheme", source, StringComparison.Ordinal);
    }

    [Fact]
    public void App_WiresTheMiniLedStatic_BeforeTheWindowOpens()
    {
        string source = Read("App.xaml.cs");

        Assert.Contains("Scripts.Misc.MiniLedAccess.Devices = deviceCapability;", source, StringComparison.Ordinal);
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { RepositoryRoot(), "Uniwill Control Utility" }.Concat(parts).ToArray()));

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "Uniwill Control Utility")) &&
                Directory.Exists(Path.Combine(directory.FullName, "UCU.Hardware")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
