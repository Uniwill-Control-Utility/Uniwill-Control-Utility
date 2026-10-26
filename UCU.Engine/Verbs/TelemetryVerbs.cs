using UCU.Contracts;
using UCU.Hardware.Ec;

namespace UCU.Engine
{
    /// <summary>
    /// Telemetry-domain verbs: the fast reads the tray, overlay and dashboard poll,
    /// and the raw register access the SKU/display-variant/fan-table decoders need.
    /// </summary>
    internal static class TelemetryVerbs
    {
        public static void Register(Action<string, VerbHandler> add, EcEngineGate gate)
        {
            add(EngineVerbs.Telemetry.GetFanDuty, (p, ct) =>
            {
                EcFanDutySnapshot duty = gate.Acquire().ReadFanDutyTelemetry();
                return Task.FromResult<object?>(new FanDutySnapshotDto
                {
                    CpuFanDuty = duty.CpuFanDuty,
                    GpuFanDuty = duty.GpuFanDuty,
                });
            });

            add(EngineVerbs.Telemetry.GetCpuTemperature, (p, ct) =>
                Task.FromResult<object?>(gate.Acquire().GetCpuTemperature()));

            add(EngineVerbs.Telemetry.GetGpuTemperature, (p, ct) =>
                Task.FromResult<object?>(gate.Acquire().GetGpuTemperature()));

            add(EngineVerbs.Telemetry.GetMainFanRpm, (p, ct) =>
                Task.FromResult<object?>(gate.Acquire().GetMainFanRpm()));

            add(EngineVerbs.Telemetry.GetSecondFanRpm, (p, ct) =>
                Task.FromResult<object?>(gate.Acquire().GetSecondFanRpm()));

            add(EngineVerbs.Telemetry.GetProjectId, (p, ct) =>
                Task.FromResult<object?>(gate.Acquire().GetProjectId()));

            add(EngineVerbs.Telemetry.ReadRegister, (p, ct) =>
            {
                WordAddressDto dto = EngineSerializer.BindParams<WordAddressDto>(p);
                return Task.FromResult<object?>((int)gate.Acquire().ReadECRegister(dto.Address));
            });

            add(EngineVerbs.Telemetry.ReadRegisters, (p, ct) =>
            {
                RegisterRangeDto dto = EngineSerializer.BindParams<RegisterRangeDto>(p);
                return Task.FromResult<object?>(gate.Acquire().ReadECRegisters(dto.Address, dto.Count));
            });

            add(EngineVerbs.Telemetry.WriteRegister, (p, ct) =>
            {
                // The escape hatch for decoders that write (the display page's NVRAM
                // mirror). Kept deliberately: the pages that need it are SKU-specific,
                // and a semantic verb per layout would be the same door with more keys.
                RegisterWriteDto dto = EngineSerializer.BindParams<RegisterWriteDto>(p);
                gate.Acquire().WriteECRegister(dto.Address, dto.Value);
                return Task.FromResult<object?>(null);
            });
        }
    }
}
