using Microsoft.Extensions.Hosting;
using UCU.Client;
using UCU.Contracts;
using UCU.Core.Ec;
using UCU.Hardware.Ec;

namespace UCU.Engine.Tests;

/// <summary>
/// Starts the real engine host, in-process, on a private pipe, with a fake EC
/// transport and the pipe ACL disabled. The service graph is the installed service's
/// graph (<see cref="EngineHostFactory.CreateHost"/>), so every layer the RPC calls
/// traverse in these tests — accept loop, envelope codec, dispatcher, domain queues,
/// gate, EcEngine — is the code that ships.
/// </summary>
public sealed class EngineHostFixture : IAsyncDisposable
{
    private readonly IHost _host;

    private EngineHostFixture(IHost host, EngineRuntimeOptions options, IEcTransport transport)
    {
        _host = host;
        Options = options;
        Transport = transport;
        Client = new EngineClient(options.PipeName);
        Fan = new FanCapability(Client);
        Power = new PowerCapability(Client);
        Mux = new GpuMuxCapability(Client);
        Keyboard = new KeyboardCapability(Client);
        Battery = new BatteryCapability(Client);
        Telemetry = new EcTelemetryCapability(Client);
        Engine = new EngineCapability(Client);
    }

    public EngineRuntimeOptions Options { get; }
    public IEcTransport Transport { get; }

    public EngineClient Client { get; }
    public FanCapability Fan { get; }
    public PowerCapability Power { get; }
    public GpuMuxCapability Mux { get; }
    public KeyboardCapability Keyboard { get; }
    public BatteryCapability Battery { get; }
    public EcTelemetryCapability Telemetry { get; }
    public EngineCapability Engine { get; }

    public static async Task<EngineHostFixture> StartAsync(
        Func<EngineRuntimeOptions, EngineRuntimeOptions>? configure = null,
        IEcTransport? transport = null,
        EcPlatform platform = EcPlatform.Amd)
    {
        string pipeName = $"UCUEngineTest.{Guid.NewGuid():N}";

        var overrides = new EngineRuntimeOptions
        {
            PipeName = pipeName,
            RestrictPipeAccess = false,
            TransportOverride = transport ?? new FakeEcTransport(),
            PlatformOverride = platform,
            LogDirectory = Path.Combine(Path.GetTempPath(), "ucu-engine-tests", pipeName),
        };

        overrides = configure?.Invoke(overrides) ?? overrides;

        IHost host = EngineHostFactory.CreateHost(Array.Empty<string>(), isService: false, overrides);
        await host.StartAsync().ConfigureAwait(false);

        return new EngineHostFixture(host, overrides, overrides.TransportOverride!);
    }

    public FakeEcTransport Fake => (FakeEcTransport)Transport;

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _host.StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // a wedged queue under test; the host is being thrown away anyway
        }

        (_host as IAsyncDisposable)?.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
