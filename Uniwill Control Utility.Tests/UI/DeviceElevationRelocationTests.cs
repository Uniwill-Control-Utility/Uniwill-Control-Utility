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
///
/// The GPU tuning rows (V-F curve, NVIDIA DRS preference) make the same argument
/// from the other side: they never had an elevated route at all, so an
/// unelevated NVAPI write was answered NVAPI_INVALID_USER_PRIVILEGE and the
/// write silently never landed. They now ride the engine's gpu verbs.
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

    [Fact]
    public void EnginePnpIdentity_RidesWmi_TheDeadCmEnumeratorStaysRetired()
    {
        // Field defect (2026-10-30, guarded dev box): the webcam toggle OFF
        // answered verified=false without touching the devnode. The engine's
        // enumeration had used CM_Get_Device_ID_List, and on that machine
        // EVERY call to it - W and A, every filter, every documented PMCL flag
        // set - answers CR_FAIL/CR_NO_MORE_HANDLES, and the FriendlyName/Class
        // devprops answer CR_NO_SUCH_VALUE on nodes whose status/mutation
        // calls work. A service on that enumerator sees an empty machine and
        // reports every row "already disabled". Identity moved to WMI
        // Win32_PnPEntity - the rows Get-PnpDevice rides, the same source the
        // app's elevated fallback proves on every field box - with the CM
        // per-node calls for status and mutation.
        string service = ReadEngine("Devices", "PnpDeviceService.cs");
        string native = ReadEngine("Devices", "Native.cs");

        Assert.Contains("Win32_PnPEntity", service, StringComparison.Ordinal);
        Assert.Contains("PNPClass", service, StringComparison.Ordinal);
        Assert.Contains("CM_Locate_DevNode", service, StringComparison.Ordinal);

        // The dead enumerator and the dead property reads stay retired on both
        // sides (call sites; the prose may name them to explain why): a
        // resurrection re-breaks the device rows on field machines.
        Assert.DoesNotContain("CM_Get_Device_ID_List(", service, StringComparison.Ordinal);
        Assert.DoesNotContain("CM_Get_Device_ID_List(", native, StringComparison.Ordinal);
        Assert.DoesNotContain("CM_Get_DevNode_Property(", native, StringComparison.Ordinal);

        // The mutation and the verify-by-re-read stay CM per-node (proven working).
        Assert.Contains("CM_Enable_DevNode", native, StringComparison.Ordinal);
        Assert.Contains("CM_Disable_DevNode", native, StringComparison.Ordinal);
        Assert.Contains("CM_Get_DevNode_Status", native, StringComparison.Ordinal);

        // The disable call shape, pinned by the 2026-10-30 probe matrix: the
        // GLOBAL_W disable flag answers CR_FAIL from every caller on the
        // field box; the legacy (0, DICS_FLAG_GLOBAL) call lands the node in
        // CM_PROB_DISABLED, and that is what Disable-PnpDevice does there.
        Assert.Contains(
            "CM_Disable_DevNode(device.DevInst, 0, Native.DICS_FLAG_GLOBAL)",
            service,
            StringComparison.Ordinal);
        Assert.DoesNotContain("CM_Disable_DevNode(device.DevInst, Native.", service, StringComparison.Ordinal);
    }

    [Fact]
    public void VfCurveWrite_AsksTheEngineFirst_TheLocalNvapiWrite_IsFallbackOnly()
    {
        // Field defect 2026-11-10: the V-F curve silently stopped applying after
        // phase 4 put the app on an asInvoker token. The log named it:
        // "setControl returned 0xFFFFFF77" = NVAPI_INVALID_USER_PRIVILEGE, the
        // driver's "run me elevated" answer. The write now asks the LocalSystem
        // engine first; the local NVAPI call is the fallback for an engine that
        // is not answering (and is what an elevated dev run still uses).
        string source = Read("Scripts", "GPUs", "NVIDIA", "NvTuning.cs");
        string body = MethodBody(source, "public static int ApplyVfCurveOffsets_ClockBoostTable");

        int engine = body.IndexOf("Tuning.ApplyVfCurveAsync", StringComparison.Ordinal);
        int local = body.IndexOf("ApplyVfCurveOffsets_ClockBoostTableLocal", StringComparison.Ordinal);

        Assert.True(engine >= 0, "The curve write must ask the engine's gpu verb: an unelevated NVAPI write is refused.");
        Assert.True(local >= 0, "The local NVAPI write stays as the fallback.");
        Assert.True(engine < local, "The engine is the FIRST route; the local write is the fallback.");

        // A refusal must be readable in the field log at the default level, and
        // it must name the privilege answer instead of logging a bare hex code.
        Assert.Contains("LogWarning", body, StringComparison.Ordinal);
        Assert.Contains("NVAPI_INVALID_USER_PRIVILEGE", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DrsPreference_AsksTheEngineFirst_TheLocalNvapiWrite_IsFallbackOnly()
    {
        // Same privilege gate, same automatic-path argument as the PnP rows above:
        // GpuPowerSaving.ApplyAsync fires at startup, on charger transitions, on
        // resume, from the tray and from Automations.
        string source = Read("Scripts", "GPUs", "NVIDIA", "GpuPowerSaving.cs");
        string body = MethodBody(source, "private static void SetNvidiaDrsGpuPreference");

        int engine = body.IndexOf("Tuning.SetDrsPreferenceAsync", StringComparison.Ordinal);
        int local = body.IndexOf("0x8B34C9A2", StringComparison.Ordinal);

        Assert.True(engine >= 0, "The DRS preference must ask the engine's gpu verb.");
        Assert.True(local >= 0, "The local NVAPI write stays as the fallback.");
        Assert.True(engine < local, "The engine is the FIRST route; the local write is the fallback.");
    }

    [Fact]
    public void App_WiresTheGpuTuningCapability_BeforeAnyStartupApply()
    {
        string source = Read("App.xaml.cs");

        Assert.Contains("services.AddSingleton<IGpuTuningCapability, GpuTuningCapability>();", source, StringComparison.Ordinal);
        Assert.Contains("NvTuning.Tuning = gpuTuningCapability;", source, StringComparison.Ordinal);
        Assert.Contains("GpuPowerSaving.Tuning = gpuTuningCapability;", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SetClocks_AsksTheEngineFirst_TheLocalPstateWrite_IsFallbackOnly()
    {
        // The core/memory sliders hit the same privilege gate as the curve: the
        // field log shows "SetClocks: EXCEPTION NVIDIAApiException:
        // NVAPI_INVALID_USER_PRIVILEGE" on every call and never the driver's
        // read-only declaration. The pstate write therefore rides the engine's
        // gpu.set_clocks verb first, and the local write is the fallback.
        string source = Read("Scripts", "GPUs", "NVIDIA", "NvTuning.cs");
        string body = MethodBody(source, "public static int SetClocks");

        int engine = body.IndexOf("Tuning.SetClocksAsync", StringComparison.Ordinal);
        int local = body.IndexOf("GPUApi.SetPerformanceStates20", StringComparison.Ordinal);

        Assert.True(engine >= 0, "SetClocks must ask the engine's gpu verb.");
        Assert.True(local >= 0, "The local pstate write stays as the fallback.");
        Assert.True(engine < local, "The engine is the FIRST route; the local write is the fallback.");

        // A refusal has to be visible at the default log level, not only at the
        // Information level the debug panel raises.
        Assert.Contains("the core/memory offsets are NOT applied", body, StringComparison.Ordinal);
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

    private static string ReadEngine(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { RepositoryRoot(), "UCU.Hardware" }.Concat(parts).ToArray()));

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
