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
        Device = new DeviceCapability(Client);
        Smu = new AmdSmuCapability(Client);
        IntelMsr = new IntelMsrCapability(Client);
    }

    public EngineRuntimeOptions Options { get; }
    public IEcTransport Transport { get; }

    public EngineClient Client { get; }
    public FanCapability Fan { get; }
    public PowerCapability Power { get; }
    public GpuMuxCapability Mux { get; }
    public KeyboardCapability Keyboard { get; }
    public BatteryCapability Battery { get; }
    public DeviceCapability Device { get; }
    public AmdSmuCapability Smu { get; }
    public IntelMsrCapability IntelMsr { get; }
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
            // Hermetic default: the production probe opens sensor drivers (Ring0,
            // SMBus); tests opt into a scripted reading through configure.
            SensorProbeOverride = new FakeSensorProbe(),
            // Hermetic default, same reasoning: the production service enables and
            // disables REAL device nodes. Tests script a device tree through configure.
            PnpDeviceOverride = new FakePnpDeviceService(),
            // Hermetic default: the production service writes REAL UEFI variables.
            FwNvramOverride = new FakeFwNvramService(),
            // Hermetic default: the production service drives REAL power telemetry
            // registers through PawnIO. Tests script a command table through configure.
            AmdSmuOverride = new FakeAmdSmuService(),
            // Hermetic default: the production service writes REAL power MSRs.
            IntelMsrOverride = new FakeIntelMsrService(),
            // Hermetic default: the production service executes the REAL OEM
            // ACPI WMI handshake. Tests script the handshake through configure.
            MiniLedOverride = new FakeMiniLedService(),
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
