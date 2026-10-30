using System.IO;

namespace Uniwill_Control_Utility.Tests.UI;

/// <summary>
/// The PnP device rows (webcam, dGPU, touchpad) and their elevation. These rows
/// were the unelevated app's remaining per-operation UAC prompts, and two of them
/// were reachable from AUTOMATIC paths: the GPU power-saving apply fires at
/// startup, on charger transitions, on resume, from the tray, and from
/// Automations - a consent prompt an unattended apply can never answer. The
/// mutations now run as engine verbs (LocalSystem CM API, no prompt, verification
/// re-read engine-side), and the touchpad's interactive-session admin flow runs
/// as a one-shot "highest privileges" task - the route the Windows Settings app
/// itself uses for that flow. These tests pin the routing so a refactor cannot
/// quietly put a consent prompt back on an automatic path.
///
/// The elevated PowerShell/RunAsync routes stay in the files as the FALLBACK for
/// a machine whose engine is not answering; what must not come back is them
/// being the FIRST route.
/// </summary>
public sealed class DeviceElevationRelocationTests
{
    [Fact]
    public void SetWebcamAsync_TriesTheEngineVerb_First_TheElevatedShell_IsFallbackOnly()
    {
        string source = Read("Scripts", "Misc", "HardwareManagement.cs");
        string body = MethodBody(source, "public static async Task<bool> SetWebcamAsync");

        int engine = body.IndexOf("Devices.SetWebcamEnabledAsync", StringComparison.Ordinal);
        int elevated = body.IndexOf("ElevatedProcess.RunPowershellAsync", StringComparison.Ordinal);

        Assert.True(engine >= 0, "SetWebcamAsync must actuate through the engine's device verb.");
        Assert.True(elevated >= 0, "The elevated fallback stays for a machine whose engine is not running.");
        Assert.True(engine < elevated,
            "The engine verb is the FIRST route; an elevated webcam mutation is the fallback, never the default - the first route is what the user's click pays a prompt for.");

        int fallbackLog = body.IndexOf("elevated fallback", StringComparison.Ordinal);
        Assert.True(fallbackLog >= 0 && fallbackLog < elevated,
            "Taking the fallback is a visible breadcrumb: the prompt that appears needs a witness naming why.");
    }

    [Fact]
    public void SetTouchpadAsync_RunsTheAdminFlow_AsAOneShotHighestTask_First()
    {
        string source = Read("Scripts", "Misc", "HardwareManagement.cs");
        string body = MethodBody(source, "public static async Task<bool> SetTouchpadAsync");

        int task = body.IndexOf("RunOneShotHighestPrivileges", StringComparison.Ordinal);
        int elevated = body.IndexOf("ElevatedProcess.RunAsync", StringComparison.Ordinal);

        Assert.True(task >= 0, "The touchpad admin flow runs as the one-shot interactive task (the Settings app's own route): prompt-free by construction.");
        Assert.True(elevated >= 0, "The direct elevated flow stays as the fallback.");
        Assert.True(task < elevated, "The task bridge is the FIRST route; the consent prompt is the fallback.");
    }

    [Fact]
    public void DisableAndEnableDgpu_UseTheEngineVerb_First()
    {
        string source = Read("Scripts", "GPUs", "NVIDIA", "GpuPowerSaving.cs");

        foreach (string handler in new[] { "DisableDgpuAsync", "EnableDgpuAsync" })
        {
            string body = MethodBody(source, $"public static async Task<bool> {handler}");

            int engine = body.IndexOf("Devices.SetDgpuEnabledAsync", StringComparison.Ordinal);
            int elevated = body.IndexOf("ElevatedProcess.RunPowershellAsync", StringComparison.Ordinal);

            Assert.True(engine >= 0, $"{handler} must actuate through the engine's device verb: the dGPU mutation is reached by AUTOMATIC applies, which must never prompt.");
            Assert.True(elevated >= 0, "The elevated fallback stays for a machine whose engine is not running.");
            Assert.True(engine < elevated, $"{handler}: engine verb first, consent prompt is the fallback only.");
        }
    }

    [Fact]
    public void ApplyAsync_LogsThePnpVerification_NotAnUnqualifiedSuccess()
    {
        // The defect this pins: "Mode X applied successfully" was logged even
        // when the user DECLINED the consent prompt, so the log promised a
        // machine state it had not achieved. The line now carries the
        // verification and the resulting PnP status - the same information the
        // status re-read already provides, where the field can read it.
        string source = Read("Scripts", "GPUs", "NVIDIA", "GpuPowerSaving.cs");
        string body = MethodBody(source, "public static async Task ApplyAsync");

        Assert.DoesNotContain("applied successfully", body, StringComparison.Ordinal);
        Assert.Contains("PnP verified: {Verified}", body, StringComparison.Ordinal);
    }

    [Fact]
    public void NvTuning_SetClocks_DoesNotReportSuccessOnARefusedWrite()
    {
        // The audit found SetClocks returned 1 (success) without consulting the
        // driver at all; GeForce drivers refuse performance-state modification,
        // so every preset clock offset was silently fiction. The write's witness
        // is the capability's read-only flag; a refusal returns -1.
        string source = Read("Scripts", "GPUs", "NVIDIA", "NvTuning.cs");
        string body = MethodBody(source, "public static int SetClocks");

        Assert.Contains("PerformanceStatesInfo.IsReadOnly", body, StringComparison.Ordinal);
        Assert.Contains("readOnly ? -1 : 1", body, StringComparison.Ordinal);
    }

    [Fact]
    public void App_WiresTheDeviceCapability_BeforeAnyStartupApply()
    {
        string source = Read("App.xaml.cs");

        Assert.Contains("services.AddSingleton<IDeviceCapability, DeviceCapability>();", source, StringComparison.Ordinal);

        // The same phase-1 idiom as the power/fan capabilities, which exists
        // because the startup apply runs before the MainWindow is created.
        Assert.Contains("GpuPowerSaving.Devices = deviceCapability;", source, StringComparison.Ordinal);
        Assert.Contains("HardwareManagement.Devices = deviceCapability;", source, StringComparison.Ordinal);
    }

    [Fact]
    public void OneShotTask_RegistersHighestInteractive_NoPassword()
    {
        string source = Read("Services", "ScheduledTaskService.cs");
        string body = MethodBody(source, "public static int? RunOneShotHighestPrivileges");

        // The security shape of the bridge, pinned: highest privileges, the
        // current user, passwordless S4U, and the task is always deleted. It is
        // on-demand only - no trigger that persists to fire later.
        Assert.Contains("TaskRunLevel.Highest", body, StringComparison.Ordinal);
        Assert.Contains("TaskLogonType.S4U", body, StringComparison.Ordinal);
        Assert.Contains("Environment.UserName", body, StringComparison.Ordinal);
        Assert.Contains("DeleteTask(name)", body, StringComparison.Ordinal);
        Assert.DoesNotContain("LogonTrigger", body, StringComparison.Ordinal);
    }

    private static string MethodBody(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find {signature}.");

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
