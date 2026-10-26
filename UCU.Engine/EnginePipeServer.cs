using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UCU.Contracts;

namespace UCU.Engine
{
    /// <summary>
    /// The pipe accept loop: one <see cref="NamedPipeServerStream"/> per client
    /// connection, one request per connection (the client opens, writes, reads,
    /// closes), which is why the protocol needs no request-id matching.
    ///
    /// A connection is a request boundary in every sense: the client's
    /// CancellationToken is wired to the connection's lifetime, so a client that dies
    /// mid-call stops occupying a dispatcher slot for reads. A hardware write already
    /// handed to a domain queue is not cancellable by design, and finishes.
    /// </summary>
    public sealed class EnginePipeServer : BackgroundService
    {
        private readonly EngineDispatcher _dispatcher;
        private readonly EngineRuntimeOptions _options;
        private readonly ILogger<EnginePipeServer> _log;

        public EnginePipeServer(EngineDispatcher dispatcher, EngineRuntimeOptions options, ILogger<EnginePipeServer> log)
        {
            _dispatcher = dispatcher;
            _options = options;
            _log = log;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using var connectionLimiter = new SemaphoreSlim(_options.MaxConcurrentConnections);

            _log.LogInformation("engine pipe listening on {Pipe} (protocol {Version})",
                EngineProtocol.FullPipeName(_options.PipeName), EngineProtocol.Version);

            while (!stoppingToken.IsCancellationRequested)
            {
                NamedPipeServerStream? pipe = null;
                try
                {
                    pipe = CreatePipe();
                    await pipe.WaitForConnectionAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    pipe?.Dispose();
                    break;
                }
                catch (Exception ex)
                {
                    // A failed accept is usually "pipe instance in use" or the pipe
                    // being deleted during a crash-restart; the loop recovers.
                    _log.LogWarning("pipe accept failed: {Message}", ex.Message);
                    pipe?.Dispose();
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
                    continue;
                }

                // The limiter bounds concurrent dispatches, not pipe instances: an
                // accepted connection that finds the engine saturated is answered
                // Busy, not left in the queue of a machine already under load.
                _ = Task.Run(
                    () => ServeConnectionAsync(pipe, connectionLimiter, stoppingToken),
                    stoppingToken);
            }
        }

        private NamedPipeServerStream CreatePipe()
        {
            // The DACL must be baked into CreateNamedPipeW (.NET 10 has no managed
            // secured-pipe API: the PipeSecurity constructor overload is gone, and
            // SetAccessControl on the created handle throws UnauthorizedAccessException
            // - the first field run spun the accept loop every second). The service
            // always runs the restricted SDDL; dev/test hosts take the default
            // descriptor. See PipeSecurityBuilder for what the SDDL means.
            string? sddl = _options.RestrictPipeAccess ? PipeSecurityBuilder.RestrictedSddl : null;
            return NativePipeFactory.Create(_options.PipeName, sddl);
        }

        private async Task ServeConnectionAsync(NamedPipeServerStream pipe, SemaphoreSlim limiter, CancellationToken appStopping)
        {
            // The request's lifetime is the connection's: if the client process dies,
            // the pipe breaks and the dispatcher's token cancels.
            using (pipe)
            using (var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(appStopping))
            {
                try
                {
                    if (!await limiter.WaitAsync(TimeSpan.FromSeconds(5), connectionCts.Token).ConfigureAwait(false))
                    {
                        await WriteAsync(pipe, EngineResponse.Failure(string.Empty,
                            EngineErrorCode.Busy, "engine connection limit reached"), connectionCts.Token).ConfigureAwait(false);
                        return;
                    }

                    try
                    {
                        await ServeOneRequestAsync(pipe, connectionCts.Token).ConfigureAwait(false);
                    }
                    finally
                    {
                        limiter.Release();
                    }
                }
                catch (OperationCanceledException)
                {
                    // client vanished mid-request: nothing left to answer
                }
                catch (ProtocolViolationException ex)
                {
                    _log.LogWarning("protocol violation on a connection: {Message}", ex.Message);
                }
                catch (EndOfStreamException)
                {
                    // client closed without sending a frame
                }
                catch (Exception ex)
                {
                    _log.LogError("connection fault: {Message}", ex.Message);
                }
            }
        }

        private async Task ServeOneRequestAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
        {
            byte[] payload = await FrameCodec.ReadFrameAsync(pipe, EngineProtocol.MaxRequestBytes, cancellationToken).ConfigureAwait(false);

            EngineRequest request;
            try
            {
                request = EngineSerializer.Deserialize<EngineRequest>(payload);
            }
            catch (JsonException ex)
            {
                await WriteAsync(pipe, EngineResponse.Failure(string.Empty, EngineErrorCode.BadRequest,
                    $"not a request envelope: {ex.Message}"), cancellationToken).ConfigureAwait(false);
                return;
            }

            EngineResponse response = await _dispatcher.DispatchAsync(request, cancellationToken).ConfigureAwait(false);
            await WriteAsync(pipe, response, cancellationToken).ConfigureAwait(false);
        }

        private static async Task WriteAsync(NamedPipeServerStream pipe, EngineResponse response, CancellationToken cancellationToken)
        {
            byte[] payload = EngineSerializer.SerializeToUtf8Bytes(response);
            await FrameCodec.WriteFrameAsync(pipe, payload, cancellationToken).ConfigureAwait(false);
        }
    }
}
