using UCU.Contracts;
using UCU.Contracts.Capabilities;
using UCU.Core.Ec;

namespace UCU.Client
{
    /// <summary>
    /// One proxy per capability interface: each method binds its parameters into the
    /// verb's DTO and issues the RPC. The proxies contain no logic on purpose - every
    /// decision (validation, sequencing, per-platform encoding) lives engine-side, so
    /// the UI and the service cannot diverge on behaviour, only on transport.
    /// </summary>
    public sealed class FanCapability : IFanCapability
    {
        private readonly EngineClient _client;

        public FanCapability(EngineClient client) => _client = client;

        public Task<FanStatusDto> GetStatusAsync(CancellationToken cancellationToken = default) =>
            _client.InvokeAsync<FanStatusDto>(EngineVerbs.Fan.GetStatus, cancellationToken);

        public Task SetSpeedPercentAsync(int percent, CancellationToken cancellationToken = default) =>
            _client.InvokeAsync(EngineVerbs.Fan.SetSpeedPercent, new FanSpeedDto { Percent = percent }, cancellationToken);

        public Task RestoreAutoControlAsync(CancellationToken cancellationToken = default) =>
            _client.InvokeAsync(EngineVerbs.Fan.RestoreAutoControl, cancellationToken);

        public Task ToggleTurboAsync(CancellationToken cancellationToken = default) =>
            _client.InvokeAsync(EngineVerbs.Fan.ToggleTurbo, cancellationToken);

        public Task ResetStateAsync(CancellationToken cancellationToken = default) =>
            _client.InvokeAsync(EngineVerbs.Fan.ResetState, cancellationToken);

        public Task ApplyCurveAsync(
            FanCurveDto cpu,
            FanCurveDto gpu,
            int[]? cpuTemps = null,
            int[]? gpuTemps = null,
            CancellationToken cancellationToken = default) =>
            _client.InvokeAsync(EngineVerbs.Fan.ApplyCurve, new ApplyCurveDto
            {
                Cpu = cpu,
                Gpu = gpu,
                CpuTemps = cpuTemps,
                GpuTemps = gpuTemps,
            }, cancellationToken);

        public Task UploadCurveAsync(
            EcFanCurveSource source,
            int[] tempUp,
            int[] tempDown,
            int[] duty,
            CancellationToken cancellationToken = default) =>
            _client.InvokeAsync(EngineVerbs.Fan.UploadCurve, new UploadCurveDto
            {
                Source = source,
                TempUp = tempUp,
                TempDown = tempDown,
                Duty = duty,
            }, cancellationToken);

        public Task<FanCurveTablesDto> ReadCurveAsync(EcFanCurveSource source, CancellationToken cancellationToken = default) =>
            _client.InvokeAsync<FanCurveTablesDto>(EngineVerbs.Fan.ReadCurve, EngineSerializer.ToParams(new CurveSourceDto { Source = source }), cancellationToken);

        public Task SetUserFanModeAsync(bool enable, CancellationToken cancellationToken = default) =>
            _client.InvokeAsync(EngineVerbs.Fan.SetUserFanMode, new BoolValueDto { Value = enable }, cancellationToken);

        public Task<int> GetFanPwmAsync(int fanIndex, CancellationToken cancellationToken = default) =>
            _client.InvokeAsync<int>(EngineVerbs.Fan.GetFanPwm, EngineSerializer.ToParams(new FanPwmDto { FanIndex = fanIndex }), cancellationToken);

        public Task<bool> SetFanPwmAsync(int fanIndex, int duty, CancellationToken cancellationToken = default) =>
            _client.InvokeAsync<bool>(EngineVerbs.Fan.SetFanPwm, EngineSerializer.ToParams(new FanPwmDto { FanIndex = fanIndex, Duty = duty }), cancellationToken);

        public Task SetFanPwmBothAsync(int duty, CancellationToken cancellationToken = default) =>
            _client.InvokeAsync(EngineVerbs.Fan.SetFanPwmBoth, new FanPwmDto { Duty = duty }, cancellationToken);

        public Task SetFanSwitchSpeedAsync(int rpm, CancellationToken cancellationToken = default) =>
            _client.InvokeAsync(EngineVerbs.Fan.SetFanSwitchSpeed, new FanSwitchSpeedDto { Rpm = rpm }, cancellationToken);

        public Task ApplySmartFanCurveAsync(SmartFanCurveDto curve, CancellationToken cancellationToken = default) =>
            _client.InvokeAsync(EngineVerbs.Fan.ApplySmartFanCurve, curve, cancellationToken);

        public Task ApplySmartFanDutyAsync(byte[] cpuDuty, byte[] gpuDuty, CancellationToken cancellationToken = default) =>
            _client.InvokeAsync(EngineVerbs.Fan.ApplySmartFanDuty, new SmartFanDutyDto { CpuDuty = cpuDuty, GpuDuty = gpuDuty }, cancellationToken);

        public Task UnlockManualModeAsync(CancellationToken cancellationToken = default) =>
            _client.InvokeAsync(EngineVerbs.Fan.UnlockManualMode, cancellationToken);

        public Task WriteRegisterBlocksAsync(IReadOnlyList<EcRegisterBlockDto> blocks, CancellationToken cancellationToken = default) =>
            _client.InvokeAsync(EngineVerbs.Fan.WriteRegisterBlocks, new RegisterBlocksDto { Blocks = blocks }, cancellationToken);

        public Task<bool> IsControlSupportedAsync(CancellationToken cancellationToken = default) =>
            _client.InvokeAsync<bool>(EngineVerbs.Fan.IsControlSupported, cancellationToken);
    }

    /// <summary>Power verb proxies.</summary>
    public sealed class PowerCapability : IPowerCapability
    {
        private readonly EngineClient _client;

        public PowerCapability(EngineClient client) => _client = client;

        public Task<PowerLimitsDto> GetLimitsAsync(CancellationToken cancellationToken = default) =>
            _client.InvokeAsync<PowerLimitsDto>(EngineVerbs.Power.GetLimits, cancellationToken);

        public Task SetPowerLimitAsync(PowerLimitKind kind, int watts, CancellationToken cancellationToken = default) =>
            _client.InvokeAsync(EngineVerbs.Power.SetPowerLimit, new PowerLimitDto { Kind = kind, Watts = watts }, cancellationToken);

        public Task ApplyCpuPowerLimitsAsync(int pl1Watts, int pl2Watts, CancellationToken cancellationToken = default) =>
            _client.InvokeAsync(EngineVerbs.Power.ApplyCpuPowerLimits, new CpuPowerLimitsDto { Pl1Watts = pl1Watts, Pl2Watts = pl2Watts }, cancellationToken);

        public Task<int> GetTccOffsetAsync(CancellationToken cancellationToken = default) =>
            _client.InvokeAsync<int>(EngineVerbs.Power.GetTccOffset, cancellationToken);

        public Task SetTccOffsetAsync(int offset, CancellationToken cancellationToken = default) =>
            _client.InvokeAsync(EngineVerbs.Power.SetTccOffset, new IntValueDto { Value = offset }, cancellationToken);

        public Task<int> GetPerformanceModeAsync(CancellationToken cancellationToken = default) =>
            _client.InvokeAsync<int>(EngineVerbs.Power.GetPerformanceMode, cancellationToken);

        public Task SetPerformanceModeAsync(int mode, CancellationToken cancellationToken = default) =>
            _client.InvokeAsync(EngineVerbs.Power.SetPerformanceMode, new PerformanceModeDto { Mode = mode }, cancellationToken);

        public Task ApplyOemStockModeAsync(int modeCase, CancellationToken cancellationToken = default) =>
            _client.InvokeAsync(EngineVerbs.Power.ApplyOemStockMode, new StockModeDto { ModeCase = modeCase }, cancellationToken);

        public Task SetOverboostProfileAsync(CancellationToken cancellationToken = default) =>
            _client.InvokeAsync(EngineVerbs.Power.SetOverboostProfile, cancellationToken);

        public Task EnsurePowerManagementActiveAsync(CancellationToken cancellationToken = default) =>
            _client.InvokeAsync(EngineVerbs.Power.EnsurePowerManagementActive, cancellationToken);

        public Task ActivateGpuPowerControlAsync(int ctgpOffset, int dbOffset, CancellationToken cancellationToken = default) =>
            _client.InvokeAsync(EngineVerbs.Power.ActivateGpuPowerControl, new GpuPowerControlDto { CtgpOffset = ctgpOffset, DbOffset = dbOffset }, cancellationToken);

        public Task DeactivateGpuPowerControlAsync(CancellationToken cancellationToken = default) =>
            _client.InvokeAsync(EngineVerbs.Power.DeactivateGpuPowerControl, cancellationToken);

        public Task<PowerContractTruthDto> GetContractTruthAsync(CancellationToken cancellationToken = default) =>
            _client.InvokeAsync<PowerContractTruthDto>(EngineVerbs.Power.GetContractTruth, cancellationToken);

        public Task HealContractAsync(HealContractDto plan, CancellationToken cancellationToken = default) =>
            _client.InvokeAsync(EngineVerbs.Power.HealContract, plan, cancellationToken);

        public Task<bool> IsCustomProfileActiveAsync(CancellationToken cancellationToken = default) =>
            _client.InvokeAsync<bool>(EngineVerbs.Power.IsCustomProfileActive, cancellationToken);

        public Task<bool> GetAcRecoveryAsync(CancellationToken cancellationToken = default) =>
            _client.InvokeAsync<bool>(EngineVerbs.Power.GetAcRecovery, cancellationToken);

        public Task<bool> SetAcRecoveryAsync(bool enabled, CancellationToken cancellationToken = default) =>
            _client.InvokeAsync<bool>(EngineVerbs.Power.SetAcRecovery, EngineSerializer.ToParams(new DeviceToggleDto { Enabled = enabled }), cancellationToken);
    }

    /// <summary>MUX verb proxies; <see cref="GetStateAsync"/> composes three reads.</summary>
    public sealed class GpuMuxCapability : IGpuMuxCapability
    {
        private readonly EngineClient _client;

        public GpuMuxCapability(EngineClient client) => _client = client;

        public async Task<GpuMuxMode> GetModeAsync(CancellationToken cancellationToken = default)
        {
            MuxModeDto dto = await _client.InvokeAsync<MuxModeDto>(EngineVerbs.Mux.GetMode, cancellationToken).ConfigureAwait(false);
            return dto.Mode;
        }

        public Task SetModeAsync(GpuMuxMode mode, CancellationToken cancellationToken = default) =>
            _client.InvokeAsync(EngineVerbs.Mux.SetMode, new MuxModeDto { Mode = mode }, cancellationToken);

        public async Task<MuxStateDto> GetStateAsync(CancellationToken cancellationToken = default)
        {
            // Sequential, not parallel: the three reads share the EC lock in the
            // engine, so parallel calls would only add a queue race between them.
            MuxModeDto mode = await _client.InvokeAsync<MuxModeDto>(EngineVerbs.Mux.GetMode, cancellationToken).ConfigureAwait(false);
            int hardwareState = await _client.InvokeAsync<int>(EngineVerbs.Mux.GetHardwareState, cancellationToken).ConfigureAwait(false);
            int moduleId = await _client.InvokeAsync<int>(EngineVerbs.Mux.GetModuleId, cancellationToken).ConfigureAwait(false);

            return new MuxStateDto
            {
                Mode = mode.Mode,
                Platform = mode.Platform,
                HardwareState = (ushort)hardwareState,
                ModuleId = (byte)moduleId,
            };
        }
    }

    /// <summary>Device-row verb proxies (webcam/dGPU PnP).</summary>
    public sealed class DeviceCapability : IDeviceCapability
    {
        private readonly EngineClient _client;

        public DeviceCapability(EngineClient client) => _client = client;

        // The DTO is registered non-nullably (the wire's shape); the T?=nullable
        // instantiation is what accepts the engine's "no matching device node"
        // answer (an absent Result element) as null, per the interface contract.
        public Task<PnpDeviceStateDto?> GetWebcamStateAsync(CancellationToken cancellationToken = default) =>
            _client.InvokeAsync<PnpDeviceStateDto?>(EngineVerbs.Device.GetWebcamStatus, cancellationToken);

        public Task<bool> SetWebcamEnabledAsync(bool enabled, CancellationToken cancellationToken = default) =>
            _client.InvokeAsync<bool>(EngineVerbs.Device.SetWebcamEnabled, EngineSerializer.ToParams(new DeviceToggleDto { Enabled = enabled }), cancellationToken);

        public Task<PnpDeviceStateDto?> GetDgpuStateAsync(CancellationToken cancellationToken = default) =>
            _client.InvokeAsync<PnpDeviceStateDto?>(EngineVerbs.Device.GetDgpuStatus, cancellationToken);

        public Task<bool> SetDgpuEnabledAsync(bool enabled, CancellationToken cancellationToken = default) =>
            _client.InvokeAsync<bool>(EngineVerbs.Device.SetDgpuEnabled, EngineSerializer.ToParams(new DeviceToggleDto { Enabled = enabled }), cancellationToken);

        public async Task<byte[]?> GetNvramValueAsync(string name, string guid, CancellationToken cancellationToken = default)
        {
            // The result DTO carries the bytes; null data is the firmware's
            // "no such variable" answer, which the interface contract renders
            // as a null buffer.
            NvramValueDto result = await _client.InvokeAsync<NvramValueDto>(
                EngineVerbs.Device.GetNvramValue,
                EngineSerializer.ToParams(new NvramValueDto { Name = name, Guid = guid }),
                cancellationToken).ConfigureAwait(false);

            return result.Data;
        }

        public Task<bool> SetNvramValueAsync(string name, string guid, byte[] data, CancellationToken cancellationToken = default) =>
            _client.InvokeAsync<bool>(EngineVerbs.Device.SetNvramValue,
                EngineSerializer.ToParams(new NvramValueDto { Name = name, Guid = guid, Data = data }), cancellationToken);

        public Task<bool> GetSupportsCpuApMenuAsync(CancellationToken cancellationToken = default) =>
            _client.InvokeAsync<bool>(EngineVerbs.Device.GetSupportsCpuApMenu, cancellationToken);

        public Task<bool> SetCpuApMenuUnlockAsync(bool enabled, CancellationToken cancellationToken = default) =>
            _client.InvokeAsync<bool>(EngineVerbs.Device.SetCpuApMenuUnlock, EngineSerializer.ToParams(new BoolValueDto { Value = enabled }), cancellationToken);

        public Task<bool> SetMiniLedDimmingAsync(bool enabled, CancellationToken cancellationToken = default) =>
            _client.InvokeAsync<bool>(EngineVerbs.Device.SetMiniLedDimming, EngineSerializer.ToParams(new BoolValueDto { Value = enabled }), cancellationToken);
    }

    /// <summary>AMD SMU verb proxy.</summary>
    public sealed class AmdSmuCapability : IAmdSmuCapability
    {
        private readonly EngineClient _client;

        public AmdSmuCapability(EngineClient client) => _client = client;

        public Task<int> ExecuteCommandAsync(string name, uint value, CancellationToken cancellationToken = default) =>
            _client.InvokeAsync<int>(EngineVerbs.Cpu.ExecuteAmdSmuCommand,
                EngineSerializer.ToParams(new SmuCommandDto { Name = name, Value = value }), cancellationToken);
    }

    /// <summary>Intel MSR verb proxies.</summary>
    public sealed class IntelMsrCapability : IIntelMsrCapability
    {
        private readonly EngineClient _client;

        public IntelMsrCapability(EngineClient client) => _client = client;

        public Task<bool> SetTdpAsync(int pl1, int pl2, CancellationToken cancellationToken = default) =>
            _client.InvokeAsync<bool>(EngineVerbs.Cpu.IntelSetTdp,
                EngineSerializer.ToParams(new IntelTdpDto { Pl1 = pl1, Pl2 = pl2 }), cancellationToken);

        public Task<bool> SetPowerBalanceAsync(int value, int cpuOrGpu, CancellationToken cancellationToken = default) =>
            _client.InvokeAsync<bool>(EngineVerbs.Cpu.IntelSetPowerBalance,
                EngineSerializer.ToParams(new PlaneValueDto { Value = value, Plane = cpuOrGpu }), cancellationToken);

        public Task<bool> SetVoltageOffsetAsync(int value, int plane, CancellationToken cancellationToken = default) =>
            _client.InvokeAsync<bool>(EngineVerbs.Cpu.IntelSetVoltageOffset,
                EngineSerializer.ToParams(new PlaneValueDto { Value = value, Plane = plane }), cancellationToken);

        public Task<bool> SetClockRatiosAsync(int[] clockRatios, CancellationToken cancellationToken = default) =>
            _client.InvokeAsync<bool>(EngineVerbs.Cpu.IntelSetClockRatio,
                EngineSerializer.ToParams(clockRatios), cancellationToken);

        public Task<int[]> GetClockRatiosAsync(CancellationToken cancellationToken = default) =>
            _client.InvokeAsync<int[]>(EngineVerbs.Cpu.IntelGetClockRatios, cancellationToken);
    }

    /// <summary>Keyboard verb proxies.</summary>
    public sealed class KeyboardCapability : IKeyboardCapability
    {
        private readonly EngineClient _client;

        public KeyboardCapability(EngineClient client) => _client = client;

        public Task SetColorAsync(byte red, byte green, byte blue, CancellationToken cancellationToken = default) =>
            _client.InvokeAsync(EngineVerbs.Keyboard.SetColor, new KeyboardColorDto { Red = red, Green = green, Blue = blue }, cancellationToken);

        public Task SetBrightnessAsync(int brightness, CancellationToken cancellationToken = default) =>
            _client.InvokeAsync(EngineVerbs.Keyboard.SetBrightness, new KeyboardBrightnessDto { Brightness = brightness }, cancellationToken);

        public Task TurnOnAsync(CancellationToken cancellationToken = default) =>
            _client.InvokeAsync(EngineVerbs.Keyboard.TurnOn, cancellationToken);

        public Task TurnOffAsync(CancellationToken cancellationToken = default) =>
            _client.InvokeAsync(EngineVerbs.Keyboard.TurnOff, cancellationToken);

        public Task SetChinaModeAsync(CancellationToken cancellationToken = default) =>
            _client.InvokeAsync(EngineVerbs.Keyboard.SetChinaMode, cancellationToken);

        public Task TriggerLogoEffectAsync(CancellationToken cancellationToken = default) =>
            _client.InvokeAsync(EngineVerbs.Keyboard.TriggerLogoEffect, cancellationToken);

        public Task TriggerRainbowEffectAsync(CancellationToken cancellationToken = default) =>
            _client.InvokeAsync(EngineVerbs.Keyboard.TriggerRainbowEffect, cancellationToken);

        public Task<bool> IsRgbAsync(CancellationToken cancellationToken = default) =>
            _client.InvokeAsync<bool>(EngineVerbs.Keyboard.IsRgbKeyboard, cancellationToken);

        public Task<bool> IsWhiteOnlyAsync(CancellationToken cancellationToken = default) =>
            _client.InvokeAsync<bool>(EngineVerbs.Keyboard.IsWhiteOnlyKeyboard, cancellationToken);

        public Task<bool> GetWinLockAsync(CancellationToken cancellationToken = default) =>
            _client.InvokeAsync<bool>(EngineVerbs.Keyboard.GetWinLock, cancellationToken);

        public Task<bool> SetWinLockAsync(bool locked, CancellationToken cancellationToken = default) =>
            _client.InvokeAsync<bool>(EngineVerbs.Keyboard.SetWinLock, EngineSerializer.ToParams(new DeviceToggleDto { Enabled = locked }), cancellationToken);

        public Task<bool> GetFnLockAsync(CancellationToken cancellationToken = default) =>
            _client.InvokeAsync<bool>(EngineVerbs.Keyboard.GetFnLock, cancellationToken);

        public Task<bool> SetFnLockAsync(bool locked, CancellationToken cancellationToken = default) =>
            _client.InvokeAsync<bool>(EngineVerbs.Keyboard.SetFnLock, EngineSerializer.ToParams(new DeviceToggleDto { Enabled = locked }), cancellationToken);
    }

    /// <summary>Battery verb proxies.</summary>
    public sealed class BatteryCapability : IBatteryCapability
    {
        private readonly EngineClient _client;

        public BatteryCapability(EngineClient client) => _client = client;

        public Task<BatteryTelemetryDto?> GetTelemetryAsync(CancellationToken cancellationToken = default) =>
            _client.InvokeAsync<BatteryTelemetryDto?>(EngineVerbs.Battery.GetTelemetry, cancellationToken);

        public Task<int> GetTemperatureAsync(CancellationToken cancellationToken = default) =>
            _client.InvokeAsync<int>(EngineVerbs.Battery.GetTemperature, cancellationToken);

        public Task<int> GetModeAsync(CancellationToken cancellationToken = default) =>
            _client.InvokeAsync<int>(EngineVerbs.Battery.GetMode, cancellationToken);

        public Task SetModeAsync(int mode, CancellationToken cancellationToken = default) =>
            _client.InvokeAsync(EngineVerbs.Battery.SetMode, new BatteryModeDto { Mode = mode }, cancellationToken);

        public Task<bool> GetGateStatusAsync(CancellationToken cancellationToken = default) =>
            _client.InvokeAsync<bool>(EngineVerbs.Battery.GetGateStatus, cancellationToken);

        public Task<BatteryThresholdsDto> GetThresholdsAsync(CancellationToken cancellationToken = default) =>
            _client.InvokeAsync<BatteryThresholdsDto>(EngineVerbs.Battery.GetThresholds, cancellationToken);

        public Task<bool> GetPlatformOverrideAsync(CancellationToken cancellationToken = default) =>
            _client.InvokeAsync<bool>(EngineVerbs.Battery.GetPlatformOverride, cancellationToken);
    }

    /// <summary>Telemetry verb proxies, including raw register access.</summary>
    public sealed class EcTelemetryCapability : IEcTelemetryCapability
    {
        private readonly EngineClient _client;

        public EcTelemetryCapability(EngineClient client) => _client = client;

        public Task<FanDutySnapshotDto> GetFanDutyAsync(CancellationToken cancellationToken = default) =>
            _client.InvokeAsync<FanDutySnapshotDto>(EngineVerbs.Telemetry.GetFanDuty, cancellationToken);

        public Task<int> GetCpuTemperatureAsync(CancellationToken cancellationToken = default) =>
            _client.InvokeAsync<int>(EngineVerbs.Telemetry.GetCpuTemperature, cancellationToken);

        public Task<int> GetGpuTemperatureAsync(CancellationToken cancellationToken = default) =>
            _client.InvokeAsync<int>(EngineVerbs.Telemetry.GetGpuTemperature, cancellationToken);

        public Task<int> GetMainFanRpmAsync(CancellationToken cancellationToken = default) =>
            _client.InvokeAsync<int>(EngineVerbs.Telemetry.GetMainFanRpm, cancellationToken);

        public Task<int> GetSecondFanRpmAsync(CancellationToken cancellationToken = default) =>
            _client.InvokeAsync<int>(EngineVerbs.Telemetry.GetSecondFanRpm, cancellationToken);

        public Task<int> GetProjectIdAsync(CancellationToken cancellationToken = default) =>
            _client.InvokeAsync<int>(EngineVerbs.Telemetry.GetProjectId, cancellationToken);

        public Task<MetricsSnapshotDto> GetMetricsSnapshotAsync(CancellationToken cancellationToken = default) =>
            _client.InvokeAsync<MetricsSnapshotDto>(EngineVerbs.Telemetry.GetMetricsSnapshot, cancellationToken);

        public async Task<byte> ReadRegisterAsync(ushort address, CancellationToken cancellationToken = default)
        {
            // The wire carries the byte as an int (System.Text.Json has no byte in the
            // registered context); the cast is exact because the EC byte is 0-255.
            int value = await _client.InvokeAsync<int>(EngineVerbs.Telemetry.ReadRegister, EngineSerializer.ToParams(new WordAddressDto { Address = address }), cancellationToken).ConfigureAwait(false);
            return (byte)value;
        }

        public Task<byte[]> ReadRegistersAsync(ushort address, int count, CancellationToken cancellationToken = default) =>
            _client.InvokeAsync<byte[]>(EngineVerbs.Telemetry.ReadRegisters, EngineSerializer.ToParams(new RegisterRangeDto { Address = address, Count = count }), cancellationToken);

        public Task WriteRegisterAsync(ushort address, byte value, CancellationToken cancellationToken = default) =>
            _client.InvokeAsync(EngineVerbs.Telemetry.WriteRegister, new RegisterWriteDto { Address = address, Value = value }, cancellationToken);
    }

    /// <summary>NVIDIA tuning verb proxies (privileged writes; reads stay app-side).</summary>
    public sealed class GpuTuningCapability : IGpuTuningCapability
    {
        private readonly EngineClient _client;

        public GpuTuningCapability(EngineClient client) => _client = client;

        public Task<GpuTuningResultDto> ApplyVfCurveAsync(Dictionary<int, int> offsetsMhz, CancellationToken cancellationToken = default) =>
            _client.InvokeAsync<GpuTuningResultDto>(
                EngineVerbs.Gpu.ApplyVfCurve,
                EngineSerializer.ToParams(new VfCurveOffsetsDto { OffsetsMhz = offsetsMhz }),
                cancellationToken);

        public Task<GpuTuningResultDto> SetDrsPreferenceAsync(DrsGpuPreference preference, CancellationToken cancellationToken = default) =>
            _client.InvokeAsync<GpuTuningResultDto>(
                EngineVerbs.Gpu.SetDrsPreference,
                EngineSerializer.ToParams(new DrsPreferenceDto { Preference = preference }),
                cancellationToken);

        public Task<GpuTuningResultDto> SetClocksAsync(int coreOffsetMhz, int memoryOffsetMhz, CancellationToken cancellationToken = default) =>
            _client.InvokeAsync<GpuTuningResultDto>(
                EngineVerbs.Gpu.SetClocks,
                EngineSerializer.ToParams(new SetClocksDto { CoreOffsetMhz = coreOffsetMhz, MemoryOffsetMhz = memoryOffsetMhz }),
                cancellationToken);
    }

    /// <summary>Engine identity proxies.</summary>
    public sealed class EngineCapability : IEngineCapability
    {
        private readonly EngineClient _client;

        public EngineCapability(EngineClient client) => _client = client;

        public Task<bool> PingAsync(CancellationToken cancellationToken = default) =>
            _client.InvokeAsync<bool>(EngineVerbs.Engine.Ping, cancellationToken);

        public Task<EngineInfoDto> GetInfoAsync(CancellationToken cancellationToken = default) =>
            _client.InvokeAsync<EngineInfoDto>(EngineVerbs.Engine.GetInfo, cancellationToken);

        public Task<bool> IsHardwareAvailableAsync(CancellationToken cancellationToken = default) =>
            _client.InvokeAsync<bool>(EngineVerbs.Engine.IsHardwareAvailable, cancellationToken);

        public Task<bool> NotifyAppExitAsync(CancellationToken cancellationToken = default) =>
            _client.InvokeAsync<bool>(EngineVerbs.Engine.NotifyAppExit, cancellationToken);

        public Task<bool> SetAutostartAsync(bool enabled, int delaySeconds, CancellationToken cancellationToken = default) =>
            _client.InvokeAsync<bool>(
                EngineVerbs.Engine.SetAutostart,
                EngineSerializer.ToParams(new AutostartRequestDto { Enabled = enabled, DelaySeconds = delaySeconds }),
                cancellationToken);
    }

    /// <summary>
    /// RTSS start proxy. One verb, no parameters: the engine locates the image
    /// and places it in the active session, so the client sends no path.
    /// </summary>
    public sealed class RtssCapability : IRtssCapability
    {
        private readonly EngineClient _client;

        public RtssCapability(EngineClient client) => _client = client;

        public Task<bool> StartAsync(CancellationToken cancellationToken = default) =>
            _client.InvokeAsync<bool>(EngineVerbs.Rtss.Start, cancellationToken);
    }

    /// <summary>
    /// OEM takeover proxies (story U-203). No parameters on any call: the
    /// target vocabulary is the engine's compiled, gate-checked catalog, so
    /// the client sends intent only - never a name, path, or service handle
    /// the pipe could be talked into touching.
    /// </summary>
    public sealed class OemCapability : IOemCapability
    {
        private readonly EngineClient _client;

        public OemCapability(EngineClient client) => _client = client;

        public Task<OemStatusDto> GetStatusAsync(CancellationToken cancellationToken = default) =>
            _client.InvokeAsync<OemStatusDto>(EngineVerbs.Oem.GetStatus, cancellationToken);

        public Task<OemActionDto> PauseAsync(CancellationToken cancellationToken = default) =>
            _client.InvokeAsync<OemActionDto>(EngineVerbs.Oem.Pause, cancellationToken);

        public Task<OemActionDto> ResumeAsync(CancellationToken cancellationToken = default) =>
            _client.InvokeAsync<OemActionDto>(EngineVerbs.Oem.Resume, cancellationToken);
    }
}
