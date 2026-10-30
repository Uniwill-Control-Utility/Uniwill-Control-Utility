using System.IO;

namespace Uniwill_Control_Utility.Tests.UI;

/// <summary>
/// The Intel MSR rows (TDP, voltage offset, clock ratio, power balance) and
/// their elevation. The MSRs ride the same PawnIO device the SMU mailboxes
/// do, whose ACL admits System and Administrators — an unelevated app's
/// driver open is access-denied, and the app's IntelManagement methods
/// swallowed that into a log line: every Intel preset apply on the field
/// machines was silently dead. The methods now route through the engine's
/// cpu.intel_* verbs first (LocalSystem writes the MSRs; no prompt exists to
/// decline), with the app's own MSR stack as the fallback for a machine
/// whose engine is not answering. The driver-blocklist write the app used to
/// carry is retired, not relocated: a LocalSystem service must not weaken
/// the CI driver blocklist. These tests pin the routing and the retirement.
/// </summary>
public sealed class IntelMsrRelocationTests
{
    [Fact]
    public void IntelManagement_RoutesEveryMutator_EngineFirst_TheLocalStack_IsFallbackOnly()
    {
        string source = Read("Scripts", "Intel Backend", "Intel_Management.cs");

        // Each public mutator asks the capability before EnsureInitialised,
        // reports the engine's "landed" verdict, and takes the fallback only
        // with a visible breadcrumb.
        foreach ((string verb, string marker) in new[]
        {
            ("SetTdpAsync", "Intel TDP MSR via engine"),
            ("SetPowerBalanceAsync", "Intel power balance MSR via engine"),
            ("SetVoltageOffsetAsync", "Intel voltage offset MSR via engine"),
            ("SetClockRatiosAsync", "Intel clock ratio MSR via engine"),
            ("GetClockRatiosAsync", "Intel clock ratios"),
        })
        {
            Assert.Contains(verb, source, StringComparison.Ordinal);
            Assert.Contains(marker, source, StringComparison.Ordinal);
        }

        Assert.Contains("local route", source, StringComparison.Ordinal);

        // The fallback trigger is the "engine did not speak" set only: a
        // HardwareFault (the MSR failed on a token that CAN touch it) is the
        // truth, and the unelevated PawnIO open must not paper over it.
        Assert.Contains("EngineErrorCode.HardwareUnavailable", source, StringComparison.Ordinal);
        Assert.Contains("EngineNotAnswering", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DriverBlocklistWrite_IsRetired_NotRelocated()
    {
        string app = Read("Scripts", "Intel Backend", "Intel_Management.cs");
        string engine = ReadEngine("Cpu", "Intel", "IntelMsrStack.cs");

        // The app: the CI Config write (checkDriverBlockRegistry / determineCPU)
        // is gone, and with it the whole registry surface of the file.
        Assert.DoesNotContain("OpenSubKey", app, StringComparison.Ordinal);
        Assert.DoesNotContain("SetValue(", app, StringComparison.Ordinal);
        Assert.DoesNotContain("Microsoft.Win32", app, StringComparison.Ordinal);

        // And it must NOT live in the engine either: weakening the vulnerable-
        // driver blocklist from a LocalSystem service is not on the menu.
        Assert.DoesNotContain("OpenSubKey", engine, StringComparison.Ordinal);
        Assert.DoesNotContain("CI\\Config", engine, StringComparison.Ordinal);
    }

    [Fact]
    public void App_WiresTheIntelMsrStatic_BeforeTheWindowOpens()
    {
        string source = Read("App.xaml.cs");

        Assert.Contains("Scripts.Intel_Backend.IntelManagement.Msr = intelMsrCapability;", source, StringComparison.Ordinal);
    }

    [Fact]
    public void EngineMsrPort_MirrorsTheAppStack_TheAddressTableIsTheSame()
    {
        // The MSR stack exists twice on purpose (the app can never reference
        // UCU.Hardware); the pair must agree on the register set, or the two
        // routes write different addresses.
        string app = Read("Scripts", "Intel Backend", "Intel_Management.cs");
        string engine = ReadEngine("Cpu", "Intel", "IntelMsrStack.cs");

        foreach (string address in new[] { "0x610", "0x150", "0x1AD", "0x63A", "0x642" })
        {
            Assert.Contains(address, app, StringComparison.Ordinal);
            Assert.Contains(address, engine, StringComparison.Ordinal);
        }

        foreach (string plane in new[] { "0x80000011", "0x80000111", "0x80000211", "0x80000411" })
        {
            Assert.Contains(plane, app, StringComparison.Ordinal);
            Assert.Contains(plane, engine, StringComparison.Ordinal);
        }

        // An empty engine-side port (a stub) would answer every Intel apply
        // from the access-denied app route exactly as before the relocation.
        Assert.Contains("WriteMsr", engine, StringComparison.Ordinal);
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
