using System.ComponentModel;
using UCU.Contracts;
using UCU.Core.Ec;
using UCU.Hardware.Ec;

namespace UCU.Engine
{
    /// <summary>
    /// Owns the one <see cref="EcEngine"/> instance and the two steps that make it
    /// usable: opening the driver handle and running the EC activation handshake.
    ///
    /// The service starts before the EC is necessarily ready (the ACPI driver loads
    /// during boot), so initialization is lazy and retryable: the first verb that
    /// needs the EC opens it, and a failure leaves the gate closed so the next verb
    /// tries again rather than caching a dead handle. The handshake is part of
    /// initialization because the app always ran it at startup: the EC ignores custom
    /// fan tables until it has been written, so an engine the app never touched has
    /// to be activated by whoever writes the first curve.
    /// </summary>
    public sealed class EcEngineGate : IDisposable
    {
        private readonly object _gate = new();
        private readonly EngineRuntimeOptions _options;
        private EcEngine? _engine;
        private bool _initialized;

        public EcEngineGate(EngineRuntimeOptions options)
        {
            _options = options;
        }

        /// <summary>
        /// A ready engine. Throws <see cref="EngineException"/> with
        /// <see cref="EngineErrorCode.HardwareUnavailable"/> when the driver cannot be
        /// opened, which the dispatcher turns into the error code the app shows as
        /// "hardware unavailable".
        /// </summary>
        public EcEngine Acquire()
        {
            lock (_gate)
            {
                if (_initialized)
                    return _engine!;

                _engine ??= new EcEngine(
                    _options.TransportOverride ?? new EcDriverTransport(),
                    _options.PlatformOverride ?? CpuPlatformDetector.Detect());

                if (!_engine.Initialize())
                    throw new EngineException(EngineErrorCode.HardwareUnavailable,
                        "the EC driver (UWACPIDriver) could not be opened");

                // The activation handshake is write-only, so it is safe to repeat once
                // the handle is open; Initialize() succeeding means the EC answered.
                _engine.InitializeEc();
                _initialized = true;
                return _engine;
            }
        }

        /// <summary>
        /// Whether the EC can be reached, without throwing. <c>engine.is_hardware_available</c>
        /// answers from this, and a false here is what the app shows as the degraded
        /// banner instead of an exception per page.
        /// </summary>
        public bool TryProbe()
        {
            try
            {
                Acquire();
                return true;
            }
            catch (Exception ex) when (ex is EngineException or Win32Exception or InvalidOperationException)
            {
                return false;
            }
        }

        /// <summary>The platform the engine resolved, initializing the engine if needed.</summary>
        public EcPlatform Platform => Acquire().Platform;

        public void Dispose()
        {
            lock (_gate)
            {
                _engine?.Dispose();
                _engine = null;
                _initialized = false;
            }
        }
    }
}
