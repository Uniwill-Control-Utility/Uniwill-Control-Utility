using UCU.Contracts;
using UCU.Hardware.Ec;

namespace UCU.Engine
{
    /// <summary>Power-domain verbs: CPU power limits, TCC offset, OEM performance modes.</summary>
    internal static class PowerVerbs
    {
        public static void Register(Action<string, VerbHandler> add, EcEngineGate gate)
        {
            add(EngineVerbs.Power.GetLimits, (p, ct) =>
            {
                var ec = gate.Acquire();
                return Task.FromResult<object?>(new PowerLimitsDto
                {
                    Pl1 = ec.GetPl1(),
                    Pl2 = ec.GetPl2(),
                    Pl4 = ec.GetPl4(),
                    Pl5 = ec.GetPl5(),
                    TccOffset = ec.GetTccOffset(),
                });
            });

            add(EngineVerbs.Power.SetPowerLimit, (p, ct) =>
            {
                PowerLimitDto dto = EngineSerializer.BindParams<PowerLimitDto>(p);
                var ec = gate.Acquire();

                // The engine has one method per limit; the wire has one verb with a
                // discriminator, so the client does not need four verbs to set one value.
                switch (dto.Kind)
                {
                    case PowerLimitKind.Pl1:
                        ec.SetPl1(dto.Watts);
                        break;
                    case PowerLimitKind.Pl2:
                        ec.SetPl2(dto.Watts);
                        break;
                    case PowerLimitKind.Pl4:
                        ec.SetPl4(dto.Watts);
                        break;
                    case PowerLimitKind.Pl5:
                        ec.SetPl5(dto.Watts);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(dto.Kind), dto.Kind,
                            $"no EC power limit '{dto.Kind}'");
                }

                return Task.FromResult<object?>(null);
            });

            add(EngineVerbs.Power.ApplyCpuPowerLimits, (p, ct) =>
            {
                CpuPowerLimitsDto dto = EngineSerializer.BindParams<CpuPowerLimitsDto>(p);
                gate.Acquire().ApplyCpuPowerLimits(dto.Pl1Watts, dto.Pl2Watts);
                return Task.FromResult<object?>(null);
            });

            add(EngineVerbs.Power.GetTccOffset, (p, ct) =>
                Task.FromResult<object?>(gate.Acquire().GetTccOffset()));

            add(EngineVerbs.Power.SetTccOffset, (p, ct) =>
            {
                IntValueDto v = EngineSerializer.BindParams<IntValueDto>(p);
                gate.Acquire().SetTccOffset(v.Value);
                return Task.FromResult<object?>(null);
            });

            add(EngineVerbs.Power.GetPerformanceMode, (p, ct) =>
                Task.FromResult<object?>(gate.Acquire().GetPerformanceMode()));

            add(EngineVerbs.Power.SetPerformanceMode, (p, ct) =>
            {
                PerformanceModeDto dto = EngineSerializer.BindParams<PerformanceModeDto>(p);
                gate.Acquire().SetPerformanceMode(dto.Mode);
                return Task.FromResult<object?>(null);
            });

            add(EngineVerbs.Power.ApplyOemStockMode, (p, ct) =>
            {
                StockModeDto dto = EngineSerializer.BindParams<StockModeDto>(p);
                gate.Acquire().ApplyOemStockMode(dto.ModeCase);
                return Task.FromResult<object?>(null);
            });

            add(EngineVerbs.Power.SetOverboostProfile, (p, ct) =>
            {
                gate.Acquire().SetOverboostProfile();
                return Task.FromResult<object?>(null);
            });

            add(EngineVerbs.Power.EnsurePowerManagementActive, (p, ct) =>
            {
                gate.Acquire().EnsurePowerManagementActive();
                return Task.FromResult<object?>(null);
            });

            add(EngineVerbs.Power.ActivateGpuPowerControl, (p, ct) =>
            {
                GpuPowerControlDto dto = EngineSerializer.BindParams<GpuPowerControlDto>(p);
                gate.Acquire().ActivateGpuPowerControl(dto.CtgpOffset, dto.DbOffset);
                return Task.FromResult<object?>(null);
            });

            add(EngineVerbs.Power.DeactivateGpuPowerControl, (p, ct) =>
            {
                gate.Acquire().DeactivateGpuPowerControl();
                return Task.FromResult<object?>(null);
            });

            add(EngineVerbs.Power.IsCustomProfileActive, (p, ct) =>
                Task.FromResult<object?>(gate.Acquire().IsCustomProfileActive()));

            add(EngineVerbs.Power.GetContractTruth, (p, ct) =>
            {
                // The drift oracle (U-107): the GPU cTGP/DB bytes and the CPU
                // PL wattage pair in one serialized power-domain pass, so a
                // heal decision never races a half-updated register set.
                var ec = gate.Acquire();
                EcEngine.GpuPowerControlStateBytes gpu = ec.GetGpuPowerControlState();

                return Task.FromResult<object?>(new PowerContractTruthDto
                {
                    GpuEnable = gpu.Enable,
                    GpuCtgp = gpu.Ctgp,
                    GpuTpp = gpu.Tpp,
                    GpuDb = gpu.Db,
                    GpuCustomProfileActive = gpu.CustomProfileActive,
                    Pl1 = ec.GetPl1(),
                    Pl2 = ec.GetPl2(),
                });
            });

            add(EngineVerbs.Power.HealContract, (p, ct) =>
            {
                // The engine replays the preset pipeline's own activation
                // methods — each half behind its flag. Validation is not the
                // guardian's plan's shape: ActivateGpuPowerControl /
                // ApplyCpuPowerLimits validate their ranges exactly as the
                // preset path does (out-of-range → InvalidValue, EC untouched).
                HealContractDto dto = EngineSerializer.BindParams<HealContractDto>(p);
                var ec = gate.Acquire();

                if (dto.ApplyGpu)
                    ec.ActivateGpuPowerControl(dto.GpuCtgpOffset, dto.GpuDbOffset);
                if (dto.ApplyCpu)
                    ec.ApplyCpuPowerLimits(dto.Pl1Watts, dto.Pl2Watts);

                return Task.FromResult<object?>(null);
            });
        }
    }
}
