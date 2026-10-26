using System;
using UCU.Contracts;
using UCU.Contracts.Capabilities;
using UCU.Core.Ec;
using UCU.Hardware.Ec;

namespace UCU.Bridge
{
    /// <summary>
    /// Shared plumbing: long EC operations run on the thread pool (so an async UI
    /// caller never parks a UI thread, matching the RPC proxies' behaviour), and each
    /// capability serialises its calls on its own semaphore, matching the engine's
    /// per-domain queue. Dev parity is the point: the app must see the same ordering
    /// semantics through the bridge as through the service, so behaviour tested
    /// against the bridge transfers to the shipped path.
    /// </summary>
    public abstract class BridgeCapabilityBase
    {
        private readonly Lazy<bool> _available;

        protected BridgeCapabilityBase(EcEngine engine)
        {
            Engine = engine;
            // The app always opened the driver at startup before touching the EC;
            // the gate reproduces that for the bridge, and one Lazy matches the
            // app's historical one-shot Initialize semantics in dev mode.
            _available = new Lazy<bool>(engine.Initialize, LazyThreadSafetyMode.ExecutionAndPublication);
        }

        protected EcEngine Engine { get; }

        private SemaphoreSlim Queue { get; } = new(1, 1);

        /// <summary>Runs a synchronous engine call the way a domain queue would.</summary>
        protected Task RunAsync(Action work) =>
            Task.Run(async () =>
            {
                await Queue.WaitAsync().ConfigureAwait(false);
                try
                {
                    EnsureAvailable();
                    work();
                }
                finally
                {
                    Queue.Release();
                }
            });

        protected Task<T> RunAsync<T>(Func<T> work) =>
            Task.Run(async () =>
            {
                await Queue.WaitAsync().ConfigureAwait(false);
                try
                {
                    EnsureAvailable();
                    return work();
                }
                finally
                {
                    Queue.Release();
                }
            });

        private void EnsureAvailable()
        {
            if (!_available.Value)
                throw new InvalidOperationException("the EC driver (UWACPIDriver) could not be opened");
        }
    }

    /// <summary>In-process <see cref="IFanCapability"/> over the EC engine.</summary>
    public sealed class BridgeFanCapability : BridgeCapabilityBase, IFanCapability
    {
        public BridgeFanCapability(EcEngine engine) : base(engine)
        {
        }

        public Task<FanStatusDto> GetStatusAsync(CancellationToken cancellationToken = default) => RunAsync(() =>
        {
            EcFanDutySnapshot duty = Engine.ReadFanDutyTelemetry();
            return new FanStatusDto
            {
                CpuTemperature = Engine.GetCpuTemperature(),
                GpuTemperature = Engine.GetGpuTemperature(),
                MainFanRpm = Engine.GetMainFanRpm(),
                SecondFanRpm = Engine.GetSecondFanRpm(),
                CpuFanDuty = duty.CpuFanDuty,
                GpuFanDuty = duty.GpuFanDuty,
                TurboActive = Engine.IsTurboActive(),
                CustomProfileActive = Engine.IsCustomProfileActive(),
                FanControlSupported = Engine.IsFanControlSupported(),
                PerformanceMode = Engine.GetPerformanceMode(),
            };
        });

        public Task SetSpeedPercentAsync(int percent, CancellationToken cancellationToken = default) =>
            RunAsync(() => Engine.SetFanSpeedPercent(percent));

        public Task RestoreAutoControlAsync(CancellationToken cancellationToken = default) =>
            RunAsync(() => Engine.RestoreAutoFanControl());

        public Task ToggleTurboAsync(CancellationToken cancellationToken = default) =>
            RunAsync(() => Engine.ToggleTurbo());

        public Task ResetStateAsync(CancellationToken cancellationToken = default) =>
            RunAsync(() => Engine.ResetFanState());

        public Task ApplyCurveAsync(
            FanCurveDto cpu,
            FanCurveDto gpu,
            int[]? cpuTemps = null,
            int[]? gpuTemps = null,
            CancellationToken cancellationToken = default) =>
            RunAsync(() => Engine.ApplyFanCurve(
                new EcFanCurveSpec(cpu.Name, cpu.DutyPercentByZone),
                new EcFanCurveSpec(gpu.Name, gpu.DutyPercentByZone),
                cpuTemps,
                gpuTemps));

        public Task UploadCurveAsync(
            EcFanCurveSource source,
            int[] tempUp,
            int[] tempDown,
            int[] duty,
            CancellationToken cancellationToken = default) =>
            RunAsync(() => Engine.UploadFanCurve(source, tempUp, tempDown, duty));

        public Task<FanCurveTablesDto> ReadCurveAsync(EcFanCurveSource source, CancellationToken cancellationToken = default) =>
            RunAsync(() =>
            {
                (int[] tempUp, int[] tempDown, int[] duty) = Engine.ReadFanCurve(source);
                return new FanCurveTablesDto { TempUp = tempUp, TempDown = tempDown, Duty = duty };
            });

        public Task SetUserFanModeAsync(bool enable, CancellationToken cancellationToken = default) =>
            RunAsync(() => Engine.SetUserFanMode(enable));

        public Task<int> GetFanPwmAsync(int fanIndex, CancellationToken cancellationToken = default) =>
            RunAsync(() => Engine.GetFanPwm(fanIndex));

        public Task<bool> SetFanPwmAsync(int fanIndex, int duty, CancellationToken cancellationToken = default) =>
            RunAsync(() => Engine.SetFanPwmDirect(fanIndex, duty));

        public Task SetFanPwmBothAsync(int duty, CancellationToken cancellationToken = default) =>
            RunAsync(() => Engine.SetFanPwmBothDirect(duty));

        public Task SetFanSwitchSpeedAsync(int rpm, CancellationToken cancellationToken = default) =>
            RunAsync(() => Engine.SetFanSwitchSpeed(rpm));

        public Task ApplySmartFanCurveAsync(SmartFanCurveDto curve, CancellationToken cancellationToken = default) =>
            RunAsync(() => Engine.ApplySmartFanCurve(
                curve.CpuTempUp, curve.CpuTempDown, curve.CpuDuty,
                curve.GpuTempUp, curve.GpuTempDown, curve.GpuDuty));

        public Task ApplySmartFanDutyAsync(byte[] cpuDuty, byte[] gpuDuty, CancellationToken cancellationToken = default) =>
            RunAsync(() => Engine.ApplySmartFanDuty(cpuDuty, gpuDuty));

        public Task UnlockManualModeAsync(CancellationToken cancellationToken = default) =>
            RunAsync(() => Engine.UnlockManualMode());

        public Task WriteRegisterBlocksAsync(IReadOnlyList<EcRegisterBlockDto> blocks, CancellationToken cancellationToken = default) =>
            RunAsync(() =>
            {
                foreach (EcRegisterBlockDto block in blocks)
                    Engine.WriteECRegisters(block.Address, block.Data);
            });

        public Task<bool> IsControlSupportedAsync(CancellationToken cancellationToken = default) =>
            RunAsync(() => Engine.IsFanControlSupported());
    }

    /// <summary>In-process <see cref="IPowerCapability"/> over the EC engine.</summary>
    public sealed class BridgePowerCapability : BridgeCapabilityBase, IPowerCapability
    {
        public BridgePowerCapability(EcEngine engine) : base(engine)
        {
        }

        public Task<PowerLimitsDto> GetLimitsAsync(CancellationToken cancellationToken = default) => RunAsync(() => new PowerLimitsDto
        {
            Pl1 = Engine.GetPl1(),
            Pl2 = Engine.GetPl2(),
            Pl4 = Engine.GetPl4(),
            Pl5 = Engine.GetPl5(),
            TccOffset = Engine.GetTccOffset(),
        });

        public Task SetPowerLimitAsync(PowerLimitKind kind, int watts, CancellationToken cancellationToken = default) =>
            RunAsync(() =>
            {
                switch (kind)
                {
                    case PowerLimitKind.Pl1: Engine.SetPl1(watts); break;
                    case PowerLimitKind.Pl2: Engine.SetPl2(watts); break;
                    case PowerLimitKind.Pl4: Engine.SetPl4(watts); break;
                    case PowerLimitKind.Pl5: Engine.SetPl5(watts); break;
                    default: throw new ArgumentOutOfRangeException(nameof(kind), kind, $"no EC power limit '{kind}'");
                }
            });

        public Task ApplyCpuPowerLimitsAsync(int pl1Watts, int pl2Watts, CancellationToken cancellationToken = default) =>
            RunAsync(() => Engine.ApplyCpuPowerLimits(pl1Watts, pl2Watts));

        public Task<int> GetTccOffsetAsync(CancellationToken cancellationToken = default) =>
            RunAsync(() => Engine.GetTccOffset());

        public Task SetTccOffsetAsync(int offset, CancellationToken cancellationToken = default) =>
            RunAsync(() => Engine.SetTccOffset(offset));

        public Task<int> GetPerformanceModeAsync(CancellationToken cancellationToken = default) =>
            RunAsync(() => Engine.GetPerformanceMode());

        public Task SetPerformanceModeAsync(int mode, CancellationToken cancellationToken = default) =>
            RunAsync(() => Engine.SetPerformanceMode(mode));

        public Task ApplyOemStockModeAsync(int modeCase, CancellationToken cancellationToken = default) =>
            RunAsync(() => Engine.ApplyOemStockMode(modeCase));

        public Task SetOverboostProfileAsync(CancellationToken cancellationToken = default) =>
            RunAsync(() => Engine.SetOverboostProfile());

        public Task EnsurePowerManagementActiveAsync(CancellationToken cancellationToken = default) =>
            RunAsync(() => Engine.EnsurePowerManagementActive());

        public Task ActivateGpuPowerControlAsync(int ctgpOffset, int dbOffset, CancellationToken cancellationToken = default) =>
            RunAsync(() => Engine.ActivateGpuPowerControl(ctgpOffset, dbOffset));

        public Task DeactivateGpuPowerControlAsync(CancellationToken cancellationToken = default) =>
            RunAsync(() => Engine.DeactivateGpuPowerControl());

        public Task<bool> IsCustomProfileActiveAsync(CancellationToken cancellationToken = default) =>
            RunAsync(() => Engine.IsCustomProfileActive());
    }

    /// <summary>In-process <see cref="IGpuMuxCapability"/> over the EC engine.</summary>
    public sealed class BridgeGpuMuxCapability : BridgeCapabilityBase, IGpuMuxCapability
    {
        public BridgeGpuMuxCapability(EcEngine engine) : base(engine)
        {
        }

        public Task<GpuMuxMode> GetModeAsync(CancellationToken cancellationToken = default) =>
            RunAsync(() => Engine.GetCurrentGpuMuxMode());

        public Task SetModeAsync(GpuMuxMode mode, CancellationToken cancellationToken = default) =>
            RunAsync(() => Engine.SetGpuMuxMode(mode));

        public Task<MuxStateDto> GetStateAsync(CancellationToken cancellationToken = default) => RunAsync(() => new MuxStateDto
        {
            Mode = Engine.GetCurrentGpuMuxMode(),
            Platform = Engine.Platform,
            HardwareState = Engine.GetMuxHardwareState(),
            ModuleId = Engine.GetGpuModuleId(),
        });
    }

    /// <summary>In-process <see cref="IKeyboardCapability"/> over the EC engine.</summary>
    public sealed class BridgeKeyboardCapability : BridgeCapabilityBase, IKeyboardCapability
    {
        public BridgeKeyboardCapability(EcEngine engine) : base(engine)
        {
        }

        public Task SetColorAsync(byte red, byte green, byte blue, CancellationToken cancellationToken = default) =>
            RunAsync(() => Engine.SetKeyboardColor(red, green, blue));

        public Task SetBrightnessAsync(int brightness, CancellationToken cancellationToken = default) =>
            RunAsync(() => Engine.SetKeyboardBrightness(brightness));

        public Task TurnOnAsync(CancellationToken cancellationToken = default) =>
            RunAsync(() => Engine.TurnOnKeyboard());

        public Task TurnOffAsync(CancellationToken cancellationToken = default) =>
            RunAsync(() => Engine.TurnOffKeyboard());

        public Task SetChinaModeAsync(CancellationToken cancellationToken = default) =>
            RunAsync(() => Engine.SetChinaMode());

        public Task TriggerLogoEffectAsync(CancellationToken cancellationToken = default) =>
            RunAsync(() => Engine.TriggerLogoEffect());

        public Task TriggerRainbowEffectAsync(CancellationToken cancellationToken = default) =>
            RunAsync(() => Engine.TriggerRainbowEffect());

        public Task<bool> IsRgbAsync(CancellationToken cancellationToken = default) =>
            RunAsync(() => Engine.IsRgbKeyboard());

        public Task<bool> IsWhiteOnlyAsync(CancellationToken cancellationToken = default) =>
            RunAsync(() => Engine.IsWhiteOnlyKeyboard());
    }

    /// <summary>In-process <see cref="IBatteryCapability"/> over the EC engine.</summary>
    public sealed class BridgeBatteryCapability : BridgeCapabilityBase, IBatteryCapability
    {
        public BridgeBatteryCapability(EcEngine engine) : base(engine)
        {
        }

        public Task<BatteryTelemetryDto?> GetTelemetryAsync(CancellationToken cancellationToken = default) =>
            RunAsync<BatteryTelemetryDto?>(() =>
            {
                BatteryTelemetry? telemetry = Engine.ReadBatteryTelemetry();
                if (telemetry == null)
                    return null;

                BatteryTelemetry t = telemetry.Value;
                return new BatteryTelemetryDto
                {
                    IsAcPowered = t.IsAcPowered,
                    StateOfCharge = t.StateOfCharge,
                    HealthPercent = t.HealthPercent,
                    TemperatureC = t.TemperatureC,
                    CycleCount = t.CycleCount,
                    PresentVoltageMv = t.PresentVoltageMv,
                    ChargeRateMa = t.ChargeRateMa,
                };
            });

        public Task<int> GetTemperatureAsync(CancellationToken cancellationToken = default) =>
            RunAsync(() => Engine.ReadBatteryTemperatureC());

        public Task<int> GetModeAsync(CancellationToken cancellationToken = default) =>
            RunAsync(() => Engine.GetBatteryMode());

        public Task SetModeAsync(int mode, CancellationToken cancellationToken = default) =>
            RunAsync(() => Engine.SetBatteryMode(mode));
    }

    /// <summary>In-process <see cref="IEcTelemetryCapability"/> over the EC engine.</summary>
    public sealed class BridgeTelemetryCapability : BridgeCapabilityBase, IEcTelemetryCapability
    {
        public BridgeTelemetryCapability(EcEngine engine) : base(engine)
        {
        }

        public Task<FanDutySnapshotDto> GetFanDutyAsync(CancellationToken cancellationToken = default) => RunAsync(() =>
        {
            EcFanDutySnapshot duty = Engine.ReadFanDutyTelemetry();
            return new FanDutySnapshotDto { CpuFanDuty = duty.CpuFanDuty, GpuFanDuty = duty.GpuFanDuty };
        });

        public Task<int> GetCpuTemperatureAsync(CancellationToken cancellationToken = default) =>
            RunAsync(() => Engine.GetCpuTemperature());

        public Task<int> GetGpuTemperatureAsync(CancellationToken cancellationToken = default) =>
            RunAsync(() => Engine.GetGpuTemperature());

        public Task<int> GetMainFanRpmAsync(CancellationToken cancellationToken = default) =>
            RunAsync(() => Engine.GetMainFanRpm());

        public Task<int> GetSecondFanRpmAsync(CancellationToken cancellationToken = default) =>
            RunAsync(() => Engine.GetSecondFanRpm());

        public Task<int> GetProjectIdAsync(CancellationToken cancellationToken = default) =>
            RunAsync(() => Engine.GetProjectId());

        public Task<byte> ReadRegisterAsync(ushort address, CancellationToken cancellationToken = default) =>
            RunAsync(() => Engine.ReadECRegister(address));

        public Task<byte[]> ReadRegistersAsync(ushort address, int count, CancellationToken cancellationToken = default) =>
            RunAsync(() => Engine.ReadECRegisters(address, count));

        public Task WriteRegisterAsync(ushort address, byte value, CancellationToken cancellationToken = default) =>
            RunAsync(() => Engine.WriteECRegister(address, value));
    }

    /// <summary>In-process <see cref="IEngineCapability"/> over the EC engine.</summary>
    public sealed class BridgeEngineCapability : BridgeCapabilityBase, IEngineCapability
    {
        public BridgeEngineCapability(EcEngine engine) : base(engine)
        {
        }

        public Task<bool> PingAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<EngineInfoDto> GetInfoAsync(CancellationToken cancellationToken = default) => RunAsync(() => new EngineInfoDto
        {
            ProtocolVersion = EngineProtocol.Version,
            EngineVersion = "dev-inprocess",
            Platform = Engine.Platform,
            EcAvailable = TryProbe(),
            ProcessId = Environment.ProcessId,
        });

        public Task<bool> IsHardwareAvailableAsync(CancellationToken cancellationToken = default) =>
            Task.Run(TryProbe, cancellationToken);

        private bool TryProbe()
        {
            try
            {
                return Engine.Initialize();
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
