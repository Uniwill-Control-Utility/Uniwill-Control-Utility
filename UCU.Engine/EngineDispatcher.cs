using System.Text.Json;
using UCU.Contracts;
using UCU.Hardware.Cpu;
using UCU.Hardware.Cpu.Intel;
using UCU.Hardware.Devices;
using UCU.Hardware.Firmware;
using UCU.Hardware.Takeover;

namespace UCU.Engine
{
    /// <summary>
    /// Routes a request envelope to its verb handler and turns the result into a
    /// response envelope. This is the dispatcher's whole job: verb lookup, protocol
    /// version check, queue selection, and the exception-to-error-code mapping.
    ///
    /// The mapping is concentrated here on purpose. Hardware code reports faults by
    /// exception type (the driver throws <c>Win32Exception</c>, validation throws
    /// <c>ArgumentOutOfRangeException</c>), and the client needs stable codes, not
    /// exception type names: every handler can therefore stay as terse as the engine
    /// call it wraps.
    /// </summary>
    public sealed class EngineDispatcher
    {
        private readonly Dictionary<string, VerbHandler> _handlers;
        private readonly EngineDomainQueues _queues;
        private readonly EcEngineGate _gate;
        private readonly AppExitSignal _appExit;

        public EngineDispatcher(
            EcEngineGate gate, EngineDomainQueues queues, AppExitSignal appExit,
            EngineMetricsSnapshotSource metrics, IPnpDeviceService devices, IFwNvramService nvram,
            IAmdSmuService smu, IIntelMsrService msr, IMiniLedService miniLed,
            IOemTakeoverService oemTakeover,
            EcRawWriteGuard rawWriteGuard, EngineRuntimeOptions options)
        {
            _gate = gate;
            _queues = queues;
            _appExit = appExit;
            _handlers = BuildHandlers(gate, appExit, metrics, devices, nvram, smu, msr, miniLed, oemTakeover, rawWriteGuard, options.RtssLaunchOverride);
        }

        public IReadOnlyCollection<string> Verbs => _handlers.Keys;

        public async Task<EngineResponse> DispatchAsync(EngineRequest request, CancellationToken cancellationToken)
        {
            if (request.ProtocolVersion != EngineProtocol.Version)
            {
                return EngineResponse.Failure(request.Id, EngineErrorCode.ProtocolVersion,
                    $"client protocol {request.ProtocolVersion}, engine protocol {EngineProtocol.Version}");
            }

            if (!_handlers.TryGetValue(request.Verb, out VerbHandler? handler) || handler is null)
            {
                return EngineResponse.Failure(request.Id, EngineErrorCode.UnsupportedVerb,
                    $"'{request.Verb}' is not a verb of this engine");
            }

            try
            {
                object? result = await RunOnAppropriateQueueAsync(request.Verb, handler, request.Params, cancellationToken)
                    .ConfigureAwait(false);

                return EngineResponse.Success(request.Id, result == null ? null : EngineSerializer.ToParamsObject(result));
            }
            catch (Exception ex)
            {
                return EngineResponse.Failure(request.Id, CodeFor(ex), MessageFor(ex));
            }
        }

        private async Task<object?> RunOnAppropriateQueueAsync(
            string verb, VerbHandler handler, JsonElement? parameters, CancellationToken cancellationToken)
        {
            SerialExecutor? queue = _queues.QueueFor(verb);
            if (queue == null)
                return await handler(parameters, cancellationToken).ConfigureAwait(false);

            // The whole handler (parameter binding plus the engine call) runs on the
            // domain queue, so a verb is ordered against every other verb in its domain.
            return await queue.RunAsync(verb, () => handler(parameters, cancellationToken).GetAwaiter().GetResult(), cancellationToken)
                .ConfigureAwait(false);
        }

        private static EngineErrorCode CodeFor(Exception ex) => ex switch
        {
            EngineException engineEx => engineEx.Code,
            ProtocolViolationException => EngineErrorCode.BadRequest,
            JsonException => EngineErrorCode.BadRequest,
            ArgumentOutOfRangeException => EngineErrorCode.InvalidValue,
            System.ComponentModel.Win32Exception => EngineErrorCode.HardwareUnavailable,
            InvalidOperationException => EngineErrorCode.HardwareFault,
            OperationCanceledException => EngineErrorCode.Timeout,
            _ => EngineErrorCode.Internal,
        };

        private static string MessageFor(Exception ex) => ex switch
        {
            EngineException engineEx => engineEx.Message,
            // Driver and validation messages name the register or the value, which is
            // what a support log needs; they carry no paths or account names.
            System.ComponentModel.Win32Exception or ArgumentOutOfRangeException or InvalidOperationException
                => ex.Message,
            _ => "unexpected engine fault; see the engine log",
        };

        private static Dictionary<string, VerbHandler> BuildHandlers(
            EcEngineGate gate, AppExitSignal appExit, EngineMetricsSnapshotSource metrics,
            IPnpDeviceService devices, IFwNvramService nvram, IAmdSmuService smu, IIntelMsrService msr,
            IMiniLedService miniLed, IOemTakeoverService oem, EcRawWriteGuard rawWriteGuard, Func<string, bool>? rtssLaunch)
        {
            var handlers = new Dictionary<string, VerbHandler>(StringComparer.Ordinal);

            void Add(string verb, VerbHandler handler) => handlers.Add(verb, handler);

            FanVerbs.Register(Add, gate, rawWriteGuard);
            PowerVerbs.Register(Add, gate);
            MuxVerbs.Register(Add, gate);
            KeyboardVerbs.Register(Add, gate);
            BatteryVerbs.Register(Add, gate);
            DeviceVerbs.Register(Add, devices, miniLed);
            NvramVerbs.Register(Add, nvram, rawWriteGuard);
            CpuVerbs.Register(Add, smu, msr);
            TelemetryVerbs.Register(Add, gate, metrics, rawWriteGuard);
            EngineVerbsImpl.Register(Add, gate, appExit);

            // Runs inline: RtssDomain is declared inline in EngineDomainQueues
            // (a user-session process launch has nothing to order against on
            // the EC queues).
            RtssVerbs.Register(Add, rtssLaunch);

            // oem.* falls to the default (telemetry) queue: pause/restore
            // sequence against each other, away from the EC verbs' queues.
            OemVerbs.Register(Add, oem);

            return handlers;
        }
    }
}
