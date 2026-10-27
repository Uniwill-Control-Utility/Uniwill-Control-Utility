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

        Task<byte> ReadRegisterAsync(ushort address, CancellationToken cancellationToken = default);

        Task<byte[]> ReadRegistersAsync(ushort address, int count, CancellationToken cancellationToken = default);

        Task WriteRegisterAsync(ushort address, byte value, CancellationToken cancellationToken = default);
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
