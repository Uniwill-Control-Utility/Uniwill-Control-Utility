using System.IO;

namespace Uniwill_Control_Utility.Tests.UI;

/// <summary>
/// The NVRAM rows (CPU Advanced Performance Menu, GPU-mode firmware mirror) and
/// their elevation. Reading and writing UEFI firmware variables demands
/// SeSystemEnvironmentPrivilege, which an unelevated process cannot enable —
/// the silent-failure class the field saw: the AP-menu row's support probe
/// found nothing, its write failed with access denied, the mux's firmware
/// mirror never persisted, and NO prompt ever appeared to explain (the direct
/// API answers empty, it does not ask). The reads and writes now route through
/// the engine's device verbs (LocalSystem enables the privilege; no prompt
/// exists to decline), and the pages' original direct P/Invokes survive as the
/// route's local fallback. These tests pin the routing so a refactor cannot
/// quietly put the silent-death condition back.
/// </summary>
public sealed class NvramRelocationTests
{
    [Fact]
    public void NvramAccess_RoutesEveryAccess_EngineFirst_TheLocalPInvoke_IsFallbackOnly()
    {
        string source = Read("Scripts", "Misc", "NvramAccess.cs");

        // Every public entry point asks the capability before touching the
        // local P/Invoke, and taking the fallback is a visible breadcrumb.
        foreach (string method in new[] { "ReadOemMagic", "WriteOemMagic", "SupportsCpuApMenu", "SetCpuApMenuUnlock" })
        {
            string body = NvramAccessBody(source, method);

            int engine = body.IndexOf("Devices.", StringComparison.Ordinal);
            int local = body.IndexOf("LocalRead", StringComparison.Ordinal);
            if (local < 0)
                local = body.IndexOf("LocalWrite", StringComparison.Ordinal);

            Assert.True(engine >= 0, $"{method} must go through the engine capability first.");
            if (local >= 0)
            {
                Assert.True(engine < local,
                    $"{method}: the engine verb is the FIRST route; the local P/Invoke (privilege an asInvoker token cannot enable) is the fallback, never the default.");
                Assert.Contains("local route", body, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void SettingsPage_ApMenuRow_DelegatesToNvramAccess_NoDirectPInvoke()
    {
        string source = Read("Views", "Pages", "SettingsPage.xaml.cs");

        // The row's probe and write delegate; the page no longer carries its
        // own firmware-variable P/Invokes (that call shape is the silent-death
        // condition on an asInvoker token) — the advapi32 token calls and the
        // firmware-variable calls are both gone from the file.
        Assert.Contains("NvramAccess.SupportsCpuApMenu()", source, StringComparison.Ordinal);
        Assert.Contains("NvramAccess.SetCpuApMenuUnlock(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetFirmwareEnvironmentVariable", source, StringComparison.Ordinal);
        Assert.DoesNotContain("advapi32", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DisplayPage_MuxMirror_DelegatesToNvramAccess_NoDirectPInvoke()
    {
        string source = Read("Views", "Pages", "Display.xaml.cs");

        Assert.Contains("NvramAccess.ReadOemMagic()", source, StringComparison.Ordinal);
        Assert.Contains("NvramAccess.WriteOemMagic(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetFirmwareEnvironmentVariable", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SetFirmwareEnvironmentVariable", source, StringComparison.Ordinal);

        // The availability probe is no longer gated on this process being
        // administrator, and the token-adjust helper is gone from the page:
        // the engine route answers from its LocalSystem token, so an
        // unelevated app has NVRAM whenever the engine does.
        Assert.DoesNotContain("EnablePrivilege(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("if (_isAdministrator)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void NvramAccess_Constants_MatchTheEngineService_PairWriteOffsets()
    {
        // The engine's semantic pair-write verb and the app's local fallback
        // implement the same layout; drift between the two means the fallback
        // writes bytes the verb's machine never reads.
        Assert.Equal(
            (UCU.Hardware.Firmware.FwNvramService.OffsetCpuApMenuSupport,
             UCU.Hardware.Firmware.FwNvramService.OffsetCpuApMenuSwitch,
             UCU.Hardware.Firmware.FwNvramService.OffsetMemOcSwitch,
             UCU.Hardware.Firmware.FwNvramService.OemMagicVariableGuid),
            (Scripts.Misc.NvramAccess.OffsetCpuApMenuSupport,
             Scripts.Misc.NvramAccess.OffsetCpuApMenuSwitch,
             Scripts.Misc.NvramAccess.OffsetMemOcSwitch,
             Scripts.Misc.NvramAccess.OemMagicGuid));
    }

    [Fact]
    public void App_WiresTheNvramStatic_BeforeTheWindowOpens()
    {
        string source = Read("App.xaml.cs");

        // Same phase-1 idiom as the device capability: the pages construct
        // during window creation, and the first support probe runs there.
        Assert.Contains("Scripts.Misc.NvramAccess.Devices = deviceCapability;", source, StringComparison.Ordinal);
    }

    private static string NvramAccessBody(string source, string method)
    {
        string? body = MethodBody(source, $"public static byte[]? {method}")
            ?? MethodBody(source, $"public static bool {method}");

        Assert.NotNull(body);
        return body;
    }

    private static string? MethodBody(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        if (start < 0)
            return null;

        int end = source.IndexOf("\n        }", start, StringComparison.Ordinal);
        Assert.True(end > start, $"Could not find the end of {signature}.");

        return source.Substring(start, end - start);
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { RepositoryRoot(), "Uniwill Control Utility" }.Concat(parts).ToArray()));

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

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
