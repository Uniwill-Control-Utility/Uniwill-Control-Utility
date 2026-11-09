using UCU.Contracts;
using UCU.Core.Ec;
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
        /// <summary>
        /// The SMU command vocabulary the app's rows and premade presets actually
        /// emit (PresetProfileApplier / PremadePresets). The SMU table also carries
        /// overclocking-only mailboxes - enable-oc, oc-volt, oc-clk,
        /// per-core-oc-clk, set-vddoff-vid, set-cofall - that no row sends and whose
        /// wrong value hangs a CPU until the battery is pulled. The name arrives from
        /// the pipe, so it is allowlisted here, at the trust boundary, rather than
        /// trusted from the client. Adding a row means adding its command here.
        /// </summary>
        private static readonly HashSet<string> AllowedSmuCommands = new(StringComparer.OrdinalIgnoreCase)
        {
            "tctl-temp", "cHTC-temp",
            "stapm-limit", "stapm-time", "fast-limit", "slow-limit", "slow-time", "apu-slow-limit",
            "ppt-limit", "tdc-limit", "edc-limit",
            "vrm-current", "vrmmax-current", "vrmsoc-current", "vrmsocmax-current", "vrmgfx-current",
            "set-coall", "set-coper", "pbo-scalar",
        };

        /// <summary>
        /// The MSR 0x150 offset field is 11 bits at 1/1.024 mV per step (~±1024 mV).
        /// The app's sliders stay inside ±150; this is the field's own limit, so a
        /// caller on the pipe cannot shift a garbage value into the register.
        /// </summary>
        private const int MaxVoltageOffsetMillivolts = 1000;

        public static void Register(Action<string, VerbHandler> add, IAmdSmuService smu, IIntelMsrService msr)
        {
            add(EngineVerbs.Cpu.ExecuteAmdSmuCommand, (p, ct) =>
            {
                SmuCommandDto dto = EngineSerializer.BindParams<SmuCommandDto>(p);

                if (dto.Name is null || !AllowedSmuCommands.Contains(dto.Name))
                {
                    throw new ArgumentOutOfRangeException(nameof(dto.Name), dto.Name,
                        "not a command this app sends; the overclocking mailboxes are not reachable over the pipe");
                }

                return Task.FromResult<object?>(smu.Execute(dto.Name, dto.Value, ct));
            });

            add(EngineVerbs.Cpu.IntelSetTdp, (p, ct) =>
            {
                IntelTdpDto dto = EngineSerializer.BindParams<IntelTdpDto>(p);

                // Above the EC's own power domain there is no laptop TDP to set; the
                // ported stack would mask `pl2 * 8` and write a silent nonsense value.
                if (dto.Pl1 > EcPowerCodec.MaxPowerWatts || dto.Pl2 > EcPowerCodec.MaxPowerWatts)
                {
                    throw new ArgumentOutOfRangeException(nameof(dto.Pl2), dto.Pl2,
                        $"a power limit above {EcPowerCodec.MaxPowerWatts} W is not a laptop TDP");
                }

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

                if (dto.Value < -MaxVoltageOffsetMillivolts || dto.Value > MaxVoltageOffsetMillivolts)
                {
                    throw new ArgumentOutOfRangeException(nameof(dto.Value), dto.Value,
                        $"a voltage offset of {dto.Value} mV does not fit the MSR field (±{MaxVoltageOffsetMillivolts} mV)");
                }

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
