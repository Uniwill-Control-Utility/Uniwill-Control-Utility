using UCU.Core.Ec;
using UCU.Hardware.Ec;

namespace UCU.Bridge
{
    /// <summary>
    /// Owns the one <see cref="EcEngine"/> of dev-inprocess mode and hands out the
    /// seven capability implementations over it. The factory exists so the app's
    /// composition root never names a <c>UCU.Hardware</c> type: it registers this
    /// class, and every capability resolves from it, which keeps the hardware
    /// assembly reachable only through this dev-only project.
    /// </summary>
    public sealed class BridgeEngine : IDisposable
    {
        private readonly EcEngine _engine;

        private readonly Lazy<BridgeFanCapability> _fan;
        private readonly Lazy<BridgePowerCapability> _power;
        private readonly Lazy<BridgeGpuMuxCapability> _mux;
        private readonly Lazy<BridgeKeyboardCapability> _keyboard;
        private readonly Lazy<BridgeBatteryCapability> _battery;
        private readonly Lazy<BridgeTelemetryCapability> _telemetry;
        private readonly Lazy<BridgeEngineCapability> _engineCapability;

        public BridgeEngine(EcPlatform platform)
        {
            _engine = new EcEngine(new EcDriverTransport(), platform);

            _fan = new Lazy<BridgeFanCapability>(() => new BridgeFanCapability(_engine));
            _power = new Lazy<BridgePowerCapability>(() => new BridgePowerCapability(_engine));
            _mux = new Lazy<BridgeGpuMuxCapability>(() => new BridgeGpuMuxCapability(_engine));
            _keyboard = new Lazy<BridgeKeyboardCapability>(() => new BridgeKeyboardCapability(_engine));
            _battery = new Lazy<BridgeBatteryCapability>(() => new BridgeBatteryCapability(_engine));
            _telemetry = new Lazy<BridgeTelemetryCapability>(() => new BridgeTelemetryCapability(_engine));
            _engineCapability = new Lazy<BridgeEngineCapability>(() => new BridgeEngineCapability(_engine));
        }

        public BridgeFanCapability Fan => _fan.Value;
        public BridgePowerCapability Power => _power.Value;
        public BridgeGpuMuxCapability Mux => _mux.Value;
        public BridgeKeyboardCapability Keyboard => _keyboard.Value;
        public BridgeBatteryCapability Battery => _battery.Value;
        public BridgeTelemetryCapability Telemetry => _telemetry.Value;
        public BridgeEngineCapability Engine => _engineCapability.Value;

        public void Dispose() => _engine.Dispose();
    }
}
