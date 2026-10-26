using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using UCU.Contracts;

namespace UCU.Client
{
    /// <summary>
    /// Calls the engine service: one pipe connection per request, one request per
    /// connection, matching the server's accept loop.
    ///
    /// Per-call connections are cheap on a local named pipe (no TLS, no handshake,
    /// microseconds) and they remove the hard parts of a persistent connection: no
    /// reconnect state machine, no half-open detection, and the server can tie a
    /// request's cancellation to its connection, which it does.
    ///
    /// A failure to connect is reported as <see cref="EngineErrorCode.HardwareUnavailable"/>
    /// with an "engine service" message: from the app's point of view "the service is
    /// not running" and "the EC is not reachable" both mean the hardware features are
    /// degraded, and the banner text distinguishes them.
    /// </summary>
    public sealed class EngineClient
    {
        private readonly string _pipeName;
        private readonly TimeSpan _connectTimeout;

        /// <summary>
        /// How long the client waits for a response. Must exceed the engine's hardware
        /// call budget (30 s by default): the engine answers slow calls with a Timeout
        /// envelope, and a client that hangs up first loses that answer.
        /// </summary>
        public static readonly TimeSpan DefaultResponseBudget = TimeSpan.FromSeconds(45);

        public EngineClient(
            string pipeName = EngineProtocol.PipeName,
            TimeSpan? connectTimeout = null)
        {
            _pipeName = pipeName;
            _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(5);
        }

        public async Task InvokeAsync(string verb, CancellationToken cancellationToken = default) =>
            await InvokeCoreAsync<object?>(verb, null, cancellationToken).ConfigureAwait(false);

        public Task InvokeAsync<TParams>(string verb, TParams parameters, CancellationToken cancellationToken = default) =>
            InvokeCoreAsync<object?>(verb, EngineSerializer.ToParams(parameters), cancellationToken);

        public Task<T> InvokeAsync<T>(string verb, CancellationToken cancellationToken = default) =>
            InvokeCoreAsync<T>(verb, null, cancellationToken);

        public Task<T> InvokeAsync<T>(string verb, JsonElement parameters, CancellationToken cancellationToken = default) =>
            InvokeCoreAsync<T>(verb, parameters, cancellationToken);

        private async Task<T> InvokeCoreAsync<T>(string verb, JsonElement? parameters, CancellationToken cancellationToken)
        {
            using var pipe = new NamedPipeClientStream(
                ".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

            using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                connectCts.CancelAfter(_connectTimeout);
                try
                {
                    await pipe.ConnectAsync(connectCts.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is TimeoutException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
                {
                    throw new EngineException(EngineErrorCode.HardwareUnavailable,
                        $"cannot reach the engine service on pipe '{_pipeName}' (is '{EngineServiceIdentity.ServiceName}' running?)");
                }
            }

            EngineRequest request = new()
            {
                Id = Guid.NewGuid().ToString("N"),
                Verb = verb,
                Params = parameters,
            };

            using (var responseCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                responseCts.CancelAfter(DefaultResponseBudget);

                await FrameCodec.WriteFrameAsync(pipe, EngineSerializer.SerializeToUtf8Bytes(request), responseCts.Token)
                    .ConfigureAwait(false);

                byte[] payload = await FrameCodec.ReadFrameAsync(pipe, EngineProtocol.MaxResponseBytes, responseCts.Token)
                    .ConfigureAwait(false);

                EngineResponse response = EngineSerializer.Deserialize<EngineResponse>(payload);

                if (!response.Ok)
                    throw new EngineException(response.Error ?? EngineErrorCode.Internal, response.Message);

                if (response.Result == null)
                    return default!; // result-less verbs run with T=object?; result verbs always carry a Result, and FromElement enforces that

                return EngineSerializer.FromElement<T>(response.Result.Value);
            }
        }
    }

    /// <summary>SCM identity, needed in client messages; kept here so the client does
    /// not reference the engine assembly.</summary>
    public static class EngineServiceIdentity
    {
        public const string ServiceName = "UCUEngine";
    }
}
