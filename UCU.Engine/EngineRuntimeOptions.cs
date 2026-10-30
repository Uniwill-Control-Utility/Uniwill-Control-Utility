using System.IO;
using UCU.Contracts;
using UCU.Core.Ec;
using UCU.Hardware.Ec;

namespace UCU.Engine
{
    /// <summary>
    /// Engine configuration. Defaults are what the installed service runs with;
    /// overrides exist so the host can be started in-process by tests, with a fake EC
    /// transport, on a private pipe, with the pipe ACL turned off.
    /// </summary>
    public sealed record EngineRuntimeOptions
    {
        /// <summary>Pipe name (without the <c>\\.\pipe\</c> prefix).</summary>
        public string PipeName { get; init; } = EngineProtocol.PipeName;

        /// <summary>
        /// When true (the service default) the pipe's DACL grants the pipe to
        /// LocalSystem and Administrators plus the interactive console user only.
        /// A pipe that any process can open is a way to move the EC without a UAC
        /// prompt, so the ACL is the security boundary of this design.
        /// </summary>
        public bool RestrictPipeAccess { get; init; } = true;

        public int MaxConcurrentConnections { get; init; } = 8;

        /// <summary>
        /// How long a hardware call may take before the caller gets
        /// <see cref="EngineErrorCode.Timeout"/>. The EC operation itself is not
        /// cancellable (the IOCTL has no cancellation), so the write completes and the
        /// queue stays serialised; the client stops waiting.
        /// </summary>
        public TimeSpan HardwareCallBudget { get; init; } = TimeSpan.FromSeconds(30);

        /// <summary>Queue depth that counts as saturation, answered with Busy.</summary>
        public int MaxQueuedCallsPerDomain { get; init; } = 64;

        /// <summary>
        /// A hardware call older than this is a wedged EC (the driver stopped
        /// answering), not a slow one: the watchdog terminates the process so the
        /// service control manager restarts it, which is the only way to clear a stuck
        /// kernel handle.
        /// </summary>
        public TimeSpan StuckCallThreshold { get; init; } = TimeSpan.FromMinutes(2);

        /// <summary>
        /// App crash supervision (phase 5): the engine relaunches the app after
        /// a crash/kill, following GuardianPolicy. True for the installed
        /// service; tests and dev console runs pass <c>--no-supervision</c> so
        /// a debug session never spawns UI processes.
        /// </summary>
        public bool SupervisionEnabled { get; init; } = true;

        /// <summary>Log directory. Null selects ProgramData for the service, LocalAppData for --console.</summary>
        public string? LogDirectory { get; init; }

        /// <summary>Platform override; null resolves it from the CPU.</summary>
        public EcPlatform? PlatformOverride { get; init; }

        /// <summary>
        /// Transport override. The service leaves it null and talks to
        /// UWACPIDriver.sys; tests inject a fake so the whole RPC path, dispatcher and
        /// queues can be exercised without hardware.
        /// </summary>
        public IEcTransport? TransportOverride { get; init; }

        /// <summary>
        /// Sensor probe override, same test seam as the transport: the production
        /// probe opens real sensor drivers (Ring0 MSR access, SMBus SPD), which has
        /// no place in a test process. Tests inject a scripted probe.
        /// </summary>
        public UCU.Hardware.Metrics.ISensorProbe? SensorProbeOverride { get; init; }

        /// <summary>
        /// PnP device service override, same test seam: the production service
        /// enables and disables real device nodes (the camera, the dGPU), which has
        /// no place in a test process. Tests inject a scripted device tree.
        /// </summary>
        public UCU.Hardware.Devices.IPnpDeviceService? PnpDeviceOverride { get; init; }

        /// <summary>Engine build identity reported in <c>engine.get_info</c>.</summary>
        public string EngineVersion { get; init; } = ReadEngineVersion();

        public static string ReadEngineVersion()
        {
            string? version = typeof(EngineRuntimeOptions).Assembly
                .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .Cast<System.Reflection.AssemblyInformationalVersionAttribute>()
                .FirstOrDefault()?.InformationalVersion;

            // Strip the source-link "+commithash" suffix: this string is shown in the UI.
            int plus = version?.IndexOf('+') ?? -1;
            return plus > 0 ? version![..plus] : version ?? "0.0.0";
        }
    }
}
