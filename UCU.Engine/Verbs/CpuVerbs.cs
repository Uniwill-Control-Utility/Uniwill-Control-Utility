using UCU.Contracts;
using UCU.Hardware.Cpu;
using UCU.Hardware.Cpu.Intel;

namespace UCU.Engine
{
    /// <summary>
    /// CPU register rows: the AMD SMU mailbox commands (Curve Optimizer, PBO,
    /// the preset power vocabulary) and the Intel MSRs (TDP, voltage offset,
    /// clock ratio, power balance). PawnIO's device ACL admits System and
    /// Administrators, so an unelevated app's opens were access-denied and
    /// every SMU/MSR apply on the field machines was silently dead - the
    /// preset row saved, the register never moved, no prompt and no error.
    /// The engine runs the mailbox protocol and the MSR writes as LocalSystem,
    /// and each verb answers with the hardware's own verdict: the SMU status
    /// code, the "the write landed" bool - the honest witnesses the app route
    /// never had.
    ///
    /// The verbs carry the preset vocabulary (command names, plane ints,
    /// packed values) because that is machine data the app owns; the address
    /// tables, encodings, and transports are engine-side, on the cpu queue,
    /// so two preset applies can never interleave on the shared mailbox.
    /// Intel verbs are per-register-set, not raw MSR access: the four sets
    /// are the same power-limit authority the EC power verbs already grant.
    /// </summary>
    internal static class CpuVerbs
    {
        public static void Register(Action<string, VerbHandler> add, IAmdSmuService smu, IIntelMsrService msr)
        {
            add(EngineVerbs.Cpu.ExecuteAmdSmuCommand, (p, ct) =>
            {
                SmuCommandDto dto = EngineSerializer.BindParams<SmuCommandDto>(p);
                return Task.FromResult<object?>(smu.Execute(dto.Name, dto.Value, ct));
            });

            add(EngineVerbs.Cpu.IntelSetTdp, (p, ct) =>
            {
                IntelTdpDto dto = EngineSerializer.BindParams<IntelTdpDto>(p);
                return Task.FromResult<object?>(msr.SetTdp(dto.Pl1, dto.Pl2, ct));
            });

            add(EngineVerbs.Cpu.IntelSetPowerBalance, (p, ct) =>
            {
                PlaneValueDto dto = EngineSerializer.BindParams<PlaneValueDto>(p);
                return Task.FromResult<object?>(msr.SetPowerBalance(dto.Value, dto.Plane, ct));
            });

            add(EngineVerbs.Cpu.IntelSetVoltageOffset, (p, ct) =>
            {
                PlaneValueDto dto = EngineSerializer.BindParams<PlaneValueDto>(p);
                return Task.FromResult<object?>(msr.SetVoltageOffset(dto.Value, dto.Plane, ct));
            });

            add(EngineVerbs.Cpu.IntelSetClockRatio, (p, ct) =>
            {
                int[] ratios = EngineSerializer.BindParamsInts(p);
                return Task.FromResult<object?>(msr.SetClockRatios(ratios, ct));
            });

            add(EngineVerbs.Cpu.IntelGetClockRatios, (p, ct) =>
                Task.FromResult<object?>(msr.GetClockRatios(ct)));
        }
    }
}
