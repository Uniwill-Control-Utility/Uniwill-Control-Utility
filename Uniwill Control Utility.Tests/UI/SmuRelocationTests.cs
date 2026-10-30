using System.IO;

namespace Uniwill_Control_Utility.Tests.UI;

/// <summary>
/// The SMU rows (Curve Optimizer, PBO, the preset power vocabulary) and their
/// elevation. The SMU commands drive PawnIO, whose device ACL admits System
/// and Administrators — an unelevated app's opens of the driver are
/// access-denied, so every SMU preset apply on the field machines was
/// silently dead: the row saved, the register never moved, no prompt and no
/// error. The applies now route through the engine's cpu.execute_amd_smu_command
/// verb (LocalSystem loads the PawnIO module; no prompt exists to decline),
/// and the app's own SMU stack survives as the route's local fallback. These
/// tests pin the routing so a refactor cannot quietly put the silent death
/// back.
/// </summary>
public sealed class SmuRelocationTests
{
    [Fact]
    public void SmuAccess_RoutesEngineFirst_TheLocalStack_IsFallbackOnly()
    {
        string source = Read("Scripts", "Misc", "SmuAccess.cs");

        // The engine capability is the FIRST route, and the switch to the
        // local route is a visible breadcrumb. Only the "engine is not
        // answering" codes move to the fallback: an engine HardwareFault (the
        // SMU refused on a token that CAN touch it) is the truth and rethrows,
        // never the unelevated PawnIO open that dies silently.
        int engine = source.IndexOf("ExecuteCommandAsync", StringComparison.Ordinal);
        int fallback = source.IndexOf("SMUCommands.applySettings", StringComparison.Ordinal);

        Assert.True(engine >= 0, "SmuAccess must go through the engine capability.");
        Assert.True(fallback >= 0, "the local SMU stack stays as the fallback.");
        Assert.True(engine < fallback,
            "the engine verb is the FIRST route; the app's PawnIO open (access-denied unelevated) is the fallback, never the default.");
        Assert.Contains("engine not answering", source, StringComparison.Ordinal);
        Assert.Contains("EngineErrorCode.HardwareUnavailable", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RyzenAdjInterpreter_SendsSmuThroughTheRoute_NotTheDirectStack()
    {
        string source = Read("Scripts", "RyzenAdj_To_UCU.cs");

        // The preset interpreter's SMU call sites are routed; a raw
        // SMUCommands.applySettings call left in the interpreter is the
        // access-denied silent death for every unelevated machine.
        Assert.Contains("SmuAccess.Execute(ryzenAdjCommandString, 0x0)", source, StringComparison.Ordinal);
        Assert.Contains("SmuAccess.Execute(ryzenAdjCommandString, ryzenAdjCommandValue)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SMUCommands.applySettings", source, StringComparison.Ordinal);
    }

    [Fact]
    public void App_WiresTheSmuStatic_BeforeTheWindowOpens()
    {
        string source = Read("App.xaml.cs");

        // Same phase-1 idiom as the device capability: the startup apply batch
        // can reach the SMU interpreter before the first window paints.
        Assert.Contains("Scripts.Misc.SmuAccess.Cpu = smuCapability;", source, StringComparison.Ordinal);
    }

    [Fact]
    public void EngineSmuPort_MirrorsTheAppStack_TheDuplicationIsTheArchitecture()
    {
        // The app can never reference UCU.Hardware, so the SMU stack exists
        // twice on purpose. The pair must stay real: an empty engine-side port
        // (a stub verb surface) would answer every apply from the unelevated
        // app's access-denied route exactly as before the relocation.
        string engineStack = ReadEngine("Cpu", "AmdRyzenSmu.cs");
        string appStack = Read("Scripts", "AMD Backend", "RyzenSmu.cs");

        Assert.Contains("SendSmuCommand", engineStack, StringComparison.Ordinal);
        Assert.Contains("SendMp1", engineStack, StringComparison.Ordinal);
        Assert.Contains("ExecuteWithStatus", engineStack, StringComparison.Ordinal);
        Assert.Contains("SendSmuCommand", appStack, StringComparison.Ordinal);

        // The mailbox addresses and the PawnIO transport are the ported stack,
        // not a rewrite: the MP1 mailbox triple is the FireRange table.
        Assert.Contains("0x3B10528", engineStack, StringComparison.Ordinal);
        Assert.Contains("0x3B10528", appStack, StringComparison.Ordinal);
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { RepositoryRoot(), "Uniwill Control Utility" }.Concat(parts).ToArray()));

    private static string ReadEngine(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { RepositoryRoot(), "UCU.Hardware" }.Concat(parts).ToArray()));

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
