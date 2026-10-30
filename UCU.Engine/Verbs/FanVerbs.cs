using UCU.Contracts;
using UCU.Hardware.Ec;

namespace UCU.Engine
{
    /// <summary>Fan-domain verbs: curves, duty, turbo and the smart-fan table blocks.</summary>
    internal static class FanVerbs
    {
        public static void Register(Action<string, VerbHandler> add, EcEngineGate gate, EcRawWriteGuard rawWriteGuard)
        {
            add(EngineVerbs.Fan.GetStatus, (p, ct) =>
            {
                EcEngine ec = gate.Acquire();
                EcFanDutySnapshot duty = ec.ReadFanDutyTelemetry();

                // One aggregate for the pages that show the whole fan panel: it is one
                // queue hop and one consistent EC snapshot, not nine racing reads.
                return Task.FromResult<object?>(new FanStatusDto
                {
                    CpuTemperature = ec.GetCpuTemperature(),
                    GpuTemperature = ec.GetGpuTemperature(),
                    MainFanRpm = ec.GetMainFanRpm(),
                    SecondFanRpm = ec.GetSecondFanRpm(),
                    CpuFanDuty = duty.CpuFanDuty,
                    GpuFanDuty = duty.GpuFanDuty,
                    TurboActive = ec.IsTurboActive(),
                    CustomProfileActive = ec.IsCustomProfileActive(),
                    FanControlSupported = ec.IsFanControlSupported(),
                    PerformanceMode = ec.GetPerformanceMode(),
                });
            });

            add(EngineVerbs.Fan.SetSpeedPercent, (p, ct) =>
            {
                FanSpeedDto v = EngineSerializer.BindParams<FanSpeedDto>(p);
                gate.Acquire().SetFanSpeedPercent(v.Percent);
                return Task.FromResult<object?>(null);
            });

            add(EngineVerbs.Fan.RestoreAutoControl, (p, ct) =>
            {
                gate.Acquire().RestoreAutoFanControl();
                return Task.FromResult<object?>(null);
            });

            add(EngineVerbs.Fan.ToggleTurbo, (p, ct) =>
            {
                gate.Acquire().ToggleTurbo();
                return Task.FromResult<object?>(null);
            });

            add(EngineVerbs.Fan.ResetState, (p, ct) =>
            {
                gate.Acquire().ResetFanState();
                return Task.FromResult<object?>(null);
            });

            add(EngineVerbs.Fan.ApplyCurve, (p, ct) =>
            {
                ApplyCurveDto dto = EngineSerializer.BindParams<ApplyCurveDto>(p);

                // The engine takes the EC-level spec (name + per-zone duty), not the
                // app's model; the DTO is the same shape across the wire.
                gate.Acquire().ApplyFanCurve(
                    new EcFanCurveSpec(dto.Cpu.Name, dto.Cpu.DutyPercentByZone),
                    new EcFanCurveSpec(dto.Gpu.Name, dto.Gpu.DutyPercentByZone),
                    dto.CpuTemps,
                    dto.GpuTemps);

                return Task.FromResult<object?>(null);
            });

            add(EngineVerbs.Fan.UploadCurve, (p, ct) =>
            {
                UploadCurveDto dto = EngineSerializer.BindParams<UploadCurveDto>(p);
                gate.Acquire().UploadFanCurve(dto.Source, dto.TempUp, dto.TempDown, dto.Duty);
                return Task.FromResult<object?>(null);
            });

            add(EngineVerbs.Fan.ReadCurve, (p, ct) =>
            {
                CurveSourceDto dto = EngineSerializer.BindParams<CurveSourceDto>(p);
                (int[] tempUp, int[] tempDown, int[] duty) = gate.Acquire().ReadFanCurve(dto.Source);

                return Task.FromResult<object?>(new FanCurveTablesDto
                {
                    TempUp = tempUp,
                    TempDown = tempDown,
                    Duty = duty,
                });
            });

            add(EngineVerbs.Fan.SetUserFanMode, (p, ct) =>
            {
                BoolValueDto v = EngineSerializer.BindParams<BoolValueDto>(p);
                gate.Acquire().SetUserFanMode(v.Value);
                return Task.FromResult<object?>(null);
            });

            add(EngineVerbs.Fan.GetFanPwm, (p, ct) =>
            {
                FanPwmDto dto = EngineSerializer.BindParams<FanPwmDto>(p);
                return Task.FromResult<object?>(gate.Acquire().GetFanPwm(dto.FanIndex));
            });

            add(EngineVerbs.Fan.SetFanPwm, (p, ct) =>
            {
                FanPwmDto dto = EngineSerializer.BindParams<FanPwmDto>(p);
                return Task.FromResult<object?>(gate.Acquire().SetFanPwmDirect(dto.FanIndex, dto.Duty));
            });

            add(EngineVerbs.Fan.SetFanPwmBoth, (p, ct) =>
            {
                FanPwmDto dto = EngineSerializer.BindParams<FanPwmDto>(p);
                gate.Acquire().SetFanPwmBothDirect(dto.Duty);
                return Task.FromResult<object?>(null);
            });

            add(EngineVerbs.Fan.SetFanSwitchSpeed, (p, ct) =>
            {
                FanSwitchSpeedDto dto = EngineSerializer.BindParams<FanSwitchSpeedDto>(p);
                gate.Acquire().SetFanSwitchSpeed(dto.Rpm);
                return Task.FromResult<object?>(null);
            });

            add(EngineVerbs.Fan.ApplySmartFanCurve, (p, ct) =>
            {
                SmartFanCurveDto dto = EngineSerializer.BindParams<SmartFanCurveDto>(p);
                gate.Acquire().ApplySmartFanCurve(
                    dto.CpuTempUp, dto.CpuTempDown, dto.CpuDuty,
                    dto.GpuTempUp, dto.GpuTempDown, dto.GpuDuty);
                return Task.FromResult<object?>(null);
            });

            add(EngineVerbs.Fan.ApplySmartFanDuty, (p, ct) =>
            {
                SmartFanDutyDto dto = EngineSerializer.BindParams<SmartFanDutyDto>(p);
                gate.Acquire().ApplySmartFanDuty(dto.CpuDuty, dto.GpuDuty);
                return Task.FromResult<object?>(null);
            });

            add(EngineVerbs.Fan.UnlockManualMode, (p, ct) =>
            {
                gate.Acquire().UnlockManualMode();
                return Task.FromResult<object?>(null);
            });

            add(EngineVerbs.Fan.WriteRegisterBlocks, (p, ct) =>
            {
                // The smart-fan planner builds raw register blocks in the app; the
                // engine writes them in the order given. Splitting them across verbs
                // would reorder the plan, so they stay one call. The addresses are
                // caller-chosen, so the raw-write gate decides the domains before
                // the first byte moves (fan tables and PWM blocks are allowed;
                // power/mux/magic addresses are refused, EC untouched).
                RegisterBlocksDto dto = EngineSerializer.BindParams<RegisterBlocksDto>(p);

                foreach (EcRegisterBlockDto block in dto.Blocks)
                    rawWriteGuard.GateRegisterBlockWrite(block.Address, block.Data.Length);

                EcEngine ec = gate.Acquire();
                foreach (EcRegisterBlockDto block in dto.Blocks)
                    ec.WriteECRegisters(block.Address, block.Data);

                return Task.FromResult<object?>(null);
            });

            add(EngineVerbs.Fan.IsControlSupported, (p, ct) =>
                Task.FromResult<object?>(gate.Acquire().IsFanControlSupported()));
        }
    }
}
