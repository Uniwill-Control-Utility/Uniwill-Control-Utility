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

        public Task<bool> IsCustomProfileActiveAsync(CancellationToken cancellationToken = default) =>
            _client.InvokeAsync<bool>(EngineVerbs.Power.IsCustomProfileActive, cancellationToken);
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
    }
}
