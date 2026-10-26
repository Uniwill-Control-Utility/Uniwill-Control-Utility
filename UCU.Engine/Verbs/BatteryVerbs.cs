using UCU.Contracts;
using UCU.Hardware.Ec;

namespace UCU.Engine
{
    /// <summary>Battery-domain verbs: telemetry reads and the charging-mode register.</summary>
    internal static class BatteryVerbs
    {
        public static void Register(Action<string, VerbHandler> add, EcEngineGate gate)
        {
            add(EngineVerbs.Battery.GetTelemetry, (p, ct) =>
            {
                // A null result means the EC block was unreadable; the dashboard keeps
                // its last known values, which is what the in-process code does today.
                BatteryTelemetry? telemetry = gate.Acquire().ReadBatteryTelemetry();
                if (telemetry == null)
                    return Task.FromResult<object?>(null);

                BatteryTelemetry t = telemetry.Value;
                return Task.FromResult<object?>(new BatteryTelemetryDto
                {
                    IsAcPowered = t.IsAcPowered,
                    StateOfCharge = t.StateOfCharge,
                    HealthPercent = t.HealthPercent,
                    TemperatureC = t.TemperatureC,
                    CycleCount = t.CycleCount,
                    PresentVoltageMv = t.PresentVoltageMv,
                    ChargeRateMa = t.ChargeRateMa,
                });
            });

            add(EngineVerbs.Battery.GetTemperature, (p, ct) =>
                Task.FromResult<object?>(gate.Acquire().ReadBatteryTemperatureC()));

            add(EngineVerbs.Battery.GetMode, (p, ct) =>
                Task.FromResult<object?>(gate.Acquire().GetBatteryMode()));

            add(EngineVerbs.Battery.SetMode, (p, ct) =>
            {
                BatteryModeDto dto = EngineSerializer.BindParams<BatteryModeDto>(p);
                gate.Acquire().SetBatteryMode(dto.Mode);
                return Task.FromResult<object?>(null);
            });
        }
    }
}
