using UCU.Contracts;

namespace UCU.Engine
{
    /// <summary>GPU MUX verbs. A write here persists in the EC until a restart.</summary>
    internal static class MuxVerbs
    {
        public static void Register(Action<string, VerbHandler> add, EcEngineGate gate)
        {
            add(EngineVerbs.Mux.GetMode, (p, ct) =>
                Task.FromResult<object?>(new MuxModeDto
                {
                    Mode = gate.Acquire().GetCurrentGpuMuxMode(),
                    Platform = gate.Acquire().Platform,
                }));

            add(EngineVerbs.Mux.SetMode, (p, ct) =>
            {
                MuxModeDto dto = EngineSerializer.BindParams<MuxModeDto>(p);
                gate.Acquire().SetGpuMuxMode(dto.Mode);
                return Task.FromResult<object?>(null);
            });

            add(EngineVerbs.Mux.GetHardwareState, (p, ct) =>
                Task.FromResult<object?>((int)gate.Acquire().GetMuxHardwareState()));

            add(EngineVerbs.Mux.GetModuleId, (p, ct) =>
                Task.FromResult<object?>((int)gate.Acquire().GetGpuModuleId()));
        }
    }
}
