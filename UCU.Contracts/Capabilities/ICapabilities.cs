using UCU.Core.Ec;

namespace UCU.Contracts.Capabilities
{
    /// <summary>
    /// Fan control: curves, duty, turbo, and the EC fan-table blocks the smart-fan
    /// planner writes. Every method is asynchronous because the call crosses a process
    /// boundary and the EC sequence it triggers takes seconds (the firmware needs
    /// delays between table writes); the UI must never block a thread on it.
    ///
    /// These interfaces are the app's view of the hardware: the app codes against them
    /// and never against the EC engine, which is what lets the UI run unelevated.
    /// They are deliberately semantic (set a duty percent, apply a curve) rather than
    /// a mirror of the engine's register helpers.
    /// </summary>
    public interface IFanCapability
    {
        Task<FanStatusDto> GetStatusAsync(CancellationToken cancellationToken = default);

        Task SetSpeedPercentAsync(int percent, CancellationToken cancellationToken = default);

        Task RestoreAutoControlAsync(CancellationToken cancellationToken = default);

        Task ToggleTurboAsync(CancellationToken cancellationToken = default);

        Task ResetStateAsync(CancellationToken cancellationToken = default);

        Task ApplyCurveAsync(
            FanCurveDto cpu,
            FanCurveDto gpu,
            int[]? cpuTemps = null,
            int[]? gpuTemps = null,
            CancellationToken cancellationToken = default);

        Task UploadCurveAsync(
            EcFanCurveSource source,
            int[] tempUp,
            int[] tempDown,
            int[] duty,
            CancellationToken cancellationToken = default);

        Task<FanCurveTablesDto> ReadCurveAsync(EcFanCurveSource source, CancellationToken cancellationToken = default);

        Task SetUserFanModeAsync(bool enable, CancellationToken cancellationToken = default);

        Task<int> GetFanPwmAsync(int fanIndex, CancellationToken cancellationToken = default);

        Task<bool> SetFanPwmAsync(int fanIndex, int duty, CancellationToken cancellationToken = default);

        Task SetFanPwmBothAsync(int duty, CancellationToken cancellationToken = default);

        Task SetFanSwitchSpeedAsync(int rpm, CancellationToken cancellationToken = default);

        Task ApplySmartFanCurveAsync(SmartFanCurveDto curve, CancellationToken cancellationToken = default);

        Task ApplySmartFanDutyAsync(byte[] cpuDuty, byte[] gpuDuty, CancellationToken cancellationToken = default);

        Task UnlockManualModeAsync(CancellationToken cancellationToken = default);

        Task WriteRegisterBlocksAsync(IReadOnlyList<EcRegisterBlockDto> blocks, CancellationToken cancellationToken = default);

        Task<bool> IsControlSupportedAsync(CancellationToken cancellationToken = default);
    }

    /// <summary>CPU/GPU power limits, TCC offset and the OEM performance modes.</summary>
    public interface IPowerCapability
    {
        Task<PowerLimitsDto> GetLimitsAsync(CancellationToken cancellationToken = default);

        Task SetPowerLimitAsync(PowerLimitKind kind, int watts, CancellationToken cancellationToken = default);

        Task ApplyCpuPowerLimitsAsync(int pl1Watts, int pl2Watts, CancellationToken cancellationToken = default);

        Task<int> GetTccOffsetAsync(CancellationToken cancellationToken = default);

        Task SetTccOffsetAsync(int offset, CancellationToken cancellationToken = default);

        Task<int> GetPerformanceModeAsync(CancellationToken cancellationToken = default);

        Task SetPerformanceModeAsync(int mode, CancellationToken cancellationToken = default);

        Task ApplyOemStockModeAsync(int modeCase, CancellationToken cancellationToken = default);

        Task SetOverboostProfileAsync(CancellationToken cancellationToken = default);

        Task EnsurePowerManagementActiveAsync(CancellationToken cancellationToken = default);

        Task ActivateGpuPowerControlAsync(int ctgpOffset, int dbOffset, CancellationToken cancellationToken = default);

        Task DeactivateGpuPowerControlAsync(CancellationToken cancellationToken = default);

        Task<bool> IsCustomProfileActiveAsync(CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// GPU MUX mode. A write persists in the EC and only takes physical effect after a
    /// restart, so the engine answers with the platform it resolved: the caller needs
    /// it to encode the NVRAM mirror of the same value.
    /// </summary>
    public interface IGpuMuxCapability
    {
        Task<GpuMuxMode> GetModeAsync(CancellationToken cancellationToken = default);

        Task SetModeAsync(GpuMuxMode mode, CancellationToken cancellationToken = default);

        Task<MuxStateDto> GetStateAsync(CancellationToken cancellationToken = default);
    }

    /// <summary>Keyboard colour, brightness and the effect triggers.</summary>
    public interface IKeyboardCapability
    {
        Task SetColorAsync(byte red, byte green, byte blue, CancellationToken cancellationToken = default);

        Task SetBrightnessAsync(int brightness, CancellationToken cancellationToken = default);

        Task TurnOnAsync(CancellationToken cancellationToken = default);

        Task TurnOffAsync(CancellationToken cancellationToken = default);

        Task SetChinaModeAsync(CancellationToken cancellationToken = default);

        Task TriggerLogoEffectAsync(CancellationToken cancellationToken = default);

        Task TriggerRainbowEffectAsync(CancellationToken cancellationToken = default);

        Task<bool> IsRgbAsync(CancellationToken cancellationToken = default);

        Task<bool> IsWhiteOnlyAsync(CancellationToken cancellationToken = default);
    }

    /// <summary>Battery telemetry and the charging-mode register.</summary>
    public interface IBatteryCapability
    {
        Task<BatteryTelemetryDto?> GetTelemetryAsync(CancellationToken cancellationToken = default);

        Task<int> GetTemperatureAsync(CancellationToken cancellationToken = default);

        Task<int> GetModeAsync(CancellationToken cancellationToken = default);

        Task SetModeAsync(int mode, CancellationToken cancellationToken = default);

        /// <summary>Live charge-gate state: true while the EC gates charging (battery at its ceiling).</summary>
        Task<bool> GetGateStatusAsync(CancellationToken cancellationToken = default);

        /// <summary>The charge thresholds the current mode implies (read-only derived view, U-103).</summary>
        Task<BatteryThresholdsDto> GetThresholdsAsync(CancellationToken cancellationToken = default);

        /// <summary>True while a platform override is pinned on the EC (a tool spoofs the platform; modes apply non-naively).</summary>
        Task<bool> GetPlatformOverrideAsync(CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Telemetry reads, including the raw register access the pages that decode
    /// firmware-specific layouts need (SKU detection, display variant, fan tables).
    /// Raw access is intentionally narrow: reads, and one single-byte write for the
    /// paths that have no semantic verb.
    /// </summary>
    public interface IEcTelemetryCapability
    {
        Task<FanDutySnapshotDto> GetFanDutyAsync(CancellationToken cancellationToken = default);

        Task<int> GetCpuTemperatureAsync(CancellationToken cancellationToken = default);

        Task<int> GetGpuTemperatureAsync(CancellationToken cancellationToken = default);

        Task<int> GetMainFanRpmAsync(CancellationToken cancellationToken = default);

        Task<int> GetSecondFanRpmAsync(CancellationToken cancellationToken = default);

        Task<int> GetProjectIdAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// The engine's cached kernel-side sensor readings (CPU power/core clock,
        /// DIMM SPD speed) - the fields the app's own sensor probe cannot read
        /// unelevated (device ACLs admit System + Administrators). See
        /// <see cref="EngineVerbs.Telemetry.GetMetricsSnapshot"/>.
        /// </summary>
        Task<MetricsSnapshotDto> GetMetricsSnapshotAsync(CancellationToken cancellationToken = default);

        Task<byte> ReadRegisterAsync(ushort address, CancellationToken cancellationToken = default);

        Task<byte[]> ReadRegistersAsync(ushort address, int count, CancellationToken cancellationToken = default);

        Task WriteRegisterAsync(ushort address, byte value, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// PnP device enable/disable for the Device Manager-style rows (webcam, dGPU).
    /// These rows used to elevate per-operation from the unelevated app - webcam on
    /// a user click, dGPU additionally from AUTOMATIC applies (startup, charger
    /// transitions, tray, Automations), where a consent prompt is not an acceptable
    /// answer. The engine performs the CM API mutation as LocalSystem and verifies
    /// it by re-reading the device node, so the app learns the truth without any
    /// prompt and without a powershell.exe child.
    /// </summary>
    public interface IDeviceCapability
    {
        /// <summary>Null when the engine cannot reach the device node (no camera
        /// present is a Present=false DTO, not null).</summary>
        Task<PnpDeviceStateDto?> GetWebcamStateAsync(CancellationToken cancellationToken = default);

        /// <summary>True when the mutation ran and the verification re-read sees
        /// the device running (enable) or not running (disable).</summary>
        Task<bool> SetWebcamEnabledAsync(bool enabled, CancellationToken cancellationToken = default);

        Task<PnpDeviceStateDto?> GetDgpuStateAsync(CancellationToken cancellationToken = default);

        Task<bool> SetDgpuEnabledAsync(bool enabled, CancellationToken cancellationToken = default);

        /// <summary>
        /// UEFI firmware environment variable access (OemMagicVariable and its
        /// offsets). Null when the variable does not exist — on an unelevated
        /// app the read returned null for every variable because
        /// SeSystemEnvironmentPrivilege cannot be enabled there; through the
        /// engine it answers with the firmware's bytes.
        /// </summary>
        Task<byte[]?> GetNvramValueAsync(string name, string guid, CancellationToken cancellationToken = default);

        Task<bool> SetNvramValueAsync(string name, string guid, byte[] data, CancellationToken cancellationToken = default);

        /// <summary>The CPU Advanced Performance Menu row's gate (firmware
        /// support byte), answered by the engine because the probe needs the
        /// same privilege as the write.</summary>
        Task<bool> GetSupportsCpuApMenuAsync(CancellationToken cancellationToken = default);

        Task<bool> SetCpuApMenuUnlockAsync(bool enabled, CancellationToken cancellationToken = default);

        /// <summary>MiniLED local dimming: the engine performs the OEM ACPI
        /// WMI two-step handshake (executing the custom root\WMI method needs
        /// write access an unelevated process lacks — the app-side InvokeMethod
        /// was the silent death). True when both steps answered the firmware's
        /// expected reply byte; the EC 0x72B readback verify stays app-side on
        /// the telemetry verb.</summary>
        Task<bool> SetMiniLedDimmingAsync(bool enabled, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// AMD SMU mailbox access (Curve Optimizer, PBO, preset power commands).
    /// PawnIO's device ACL admits System and Administrators only, so an
    /// unelevated app's opens are access-denied and every SMU apply on the
    /// field machines was silently dead: the preset row saved, the register
    /// never moved, no prompt and no error. The engine performs the mailbox
    /// protocol as LocalSystem and the verb answers with the SMU's own status
    /// code, so the row reports the register set's verdict.
    ///
    /// Command names are the preset vocabulary; the per-socket address tables
    /// and the mailbox protocol are engine-side (encoding rules live with the
    /// hardware, so the UI and the service cannot diverge on behaviour).
    /// </summary>
    public interface IAmdSmuCapability
    {
        /// <summary>The SMU status code (0x01 = OK). Throws on engine-side
        /// faults mapped to error codes: unknown command (InvalidValue),
        /// PawnIO init failure / SMU refusal (HardwareFault), no engine
        /// (HardwareUnavailable).</summary>
        Task<int> ExecuteCommandAsync(string name, uint value, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Intel MSR power registers (TDP 0x610, voltage offset 0x150, clock
    /// ratio 0x1AD, power balance 0x63A/0x642). Same PawnIO ACL as the SMU:
    /// an unelevated app's driver open is access-denied, so the Intel TDP /
    /// voltage / ratio presets were silently dead on the field machines. The
    /// engine performs the MSR writes as LocalSystem, and each call answers
    /// "the write landed" — the methods keep the ported stack's catch-and-log
    /// semantics, so the engine log carries the reason for a false.
    ///
    /// The verbs are per-register-set, not raw MSR access: the four sets are
    /// the same power-limit authority the EC power verbs already grant the
    /// console user; a raw write-MSR-by-address verb would not be.
    /// </summary>
    public interface IIntelMsrCapability
    {
        Task<bool> SetTdpAsync(int pl1, int pl2, CancellationToken cancellationToken = default);

        Task<bool> SetPowerBalanceAsync(int value, int cpuOrGpu, CancellationToken cancellationToken = default);

        Task<bool> SetVoltageOffsetAsync(int value, int plane, CancellationToken cancellationToken = default);

        Task<bool> SetClockRatiosAsync(int[] clockRatios, CancellationToken cancellationToken = default);

        Task<int[]> GetClockRatiosAsync(CancellationToken cancellationToken = default);
    }

    /// <summary>Engine identity and health; the app uses this for its status display
    /// and to detect a version-skewed install.</summary>
    public interface IEngineCapability
    {
        Task<bool> PingAsync(CancellationToken cancellationToken = default);

        Task<EngineInfoDto> GetInfoAsync(CancellationToken cancellationToken = default);

        Task<bool> IsHardwareAvailableAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Reports an intentional app shutdown (tray Exit / window close) so the
        /// engine's supervisor does not relaunch the app. Call it best-effort with
        /// a short budget: the engine poll is the consumer, a crash never sends it,
        /// and a lost signal costs one relaunch.
        /// </summary>
        Task<bool> NotifyAppExitAsync(CancellationToken cancellationToken = default);
    }
}
