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
        public static void Register(Action<string, VerbHandler> add, EcEngineGate gate, AppExitSignal appExit)
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

            add(EngineVerbs.Engine.NotifyAppExit, (p, ct) =>
            {
                // The app's "this shutdown is intentional". Inline, and the timestamp
                // is taken as received: the supervisor's poll is the consumer, the app
                // is on its way out, and a hardware queue has nothing to do with it.
                appExit.SignalGracefulExit(DateTime.UtcNow);
                return Task.FromResult<object?>(true);
            });

            add(EngineVerbs.Engine.SetAutostart, (p, ct) =>
            {
                AutostartRequestDto request = EngineSerializer.BindParams<AutostartRequestDto>(p);
                return Task.FromResult<object?>(AutostartTask.Apply(request.Enabled, request.DelaySeconds));
            });
        }
    }
}
