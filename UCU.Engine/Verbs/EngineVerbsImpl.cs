using System.Diagnostics;
using UCU.Contracts;
using UCU.Core.Ec;

namespace UCU.Engine
{
    /// <summary>
    /// Engine-identity verbs. These run inline (no hardware queue), so a client can
    /// ask about engine health while every hardware queue is parked on a stuck call.
    /// </summary>
    internal static class EngineVerbsImpl
    {
        public static void Register(Action<string, VerbHandler> add, EcEngineGate gate)
        {
            add(EngineVerbs.Engine.Ping, (p, ct) => Task.FromResult<object?>(true));

            add(EngineVerbs.Engine.GetInfo, (p, ct) =>
            {
                // Probe first: a get_info on a machine with no EC must answer with the
                // engine's identity and EcAvailable=false, not fault.
                bool available = gate.TryProbe();

                return Task.FromResult<object?>(new EngineInfoDto
                {
                    ProtocolVersion = EngineProtocol.Version,
                    EngineVersion = EngineRuntimeOptions.ReadEngineVersion(),
                    Platform = available ? gate.Platform : CpuPlatformDetector.Detect(),
                    EcAvailable = available,
                    ProcessId = Environment.ProcessId,
                });
            });

            add(EngineVerbs.Engine.IsHardwareAvailable, (p, ct) =>
                Task.FromResult<object?>(gate.TryProbe()));
        }
    }
}
