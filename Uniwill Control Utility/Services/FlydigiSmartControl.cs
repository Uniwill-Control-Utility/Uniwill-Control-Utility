using System;
using System.Linq;
using System.Threading;
using Uniwill_Control_Utility.Models;

namespace Uniwill_Control_Utility.Services
{
    /// <summary>
    /// Temperature-driven fan control service for Flydigi cooler devices.
    /// Reads temperatures on a timer, applies a fan curve profile, and writes
    /// the resulting RPM to the cooler with smoothing and safety overrides.
    /// </summary>
    public sealed class FlydigiSmartControl : IDisposable
    {
        private readonly FlydigiCoolerService _coolerService;
        private readonly FlydigiTemperatureProvider _temperatureProvider;
        private Timer? _timer;
        private bool _disposed;

        /// <summary>EWMA-filtered temperature carried across ticks.</summary>
        private double? _filteredTemperature;

        /// <summary>Last RPM value actually commanded to the device (for ramp limiting).</summary>
        private ushort? _lastCommandedRpm;

        /// <summary>Smoothed RPM from the asymmetric ramp limiter (carried across ticks).</summary>
        private ushort? _lastSmoothedRpm;

        /// <summary>Last target RPM computed from the curve (before ramp limiting, for keepalive use).</summary>
        private ushort? _lastTargetRpm;

        /// <summary>Last RPM sent by the keepalive (for ramp-limiting the keepalive itself).</summary>
        private ushort? _lastKeepaliveRpm;

        /// <summary>Tick counter for periodic keepalive (re-send RPM to maintain device realtime mode).</summary>
        private int _tickCounter;

        /// <summary>Learning engine that observes steady-state temps and adjusts curve offsets.</summary>
        private readonly FlydigiLearningEngine _learningEngine = new();

        /* ------------------------------------------------------------------ */
        /*  Public state                                                       */
        /* ------------------------------------------------------------------ */

        /// <summary>Whether the control loop is currently running.</summary>
        public bool IsActive { get; private set; }

        /// <summary>Latest filtered temperature reading.</summary>
        public double? CurrentTemperature { get; private set; }

        /// <summary>Latest target RPM computed by the control loop.</summary>
        public ushort? TargetRpm { get; private set; }

        /// <summary>Active fan curve profile. Setting a new profile forces re-evaluation on the next tick.</summary>
        public FlydigiFanCurveProfile? ActiveProfile
        {
            get => _activeProfile;
            set
            {
                _activeProfile = value;
                // Force re-evaluation on next tick so the new profile takes effect immediately
                // without being blocked by hysteresis or ramp limiting.
                _filteredTemperature = null;
                _lastCommandedRpm = null;
                _lastSmoothedRpm = null;
                _lastTargetRpm = null;
                _lastKeepaliveRpm = null;
                _tickCounter = 0;
                _learningEngine.Reset();
            }
        }
        private FlydigiFanCurveProfile? _activeProfile;

        /// <summary>Settings for avoidance zones and temperature source.</summary>
        public Bs2ProSettings? Settings { get; set; }

        /* ------------------------------------------------------------------ */
        /*  Configuration                                                      */
        /* ------------------------------------------------------------------ */

        /// <summary>How often (ms) to read temperature and update fan speed. Default 1000.</summary>
        public int PollIntervalMs { get; set; } = 1000;

        /// <summary>Maximum RPM change allowed per cycle (ramp limiter). Default 200.</summary>
        public int MaxRpmChangePerCycle { get; set; } = 200;

        /// <summary>
        /// Hysteresis deadzone in degrees Celsius. Temperature changes smaller than
        /// this threshold are ignored to prevent oscillation. Default 2.0.
        /// </summary>
        public double HysteresisDeadzone { get; set; } = 2.0;

        /// <summary>
        /// Temperature source: "cpu", "gpu", or "max" (default).
        /// </summary>
        public string TempSource { get; set; } = "max";

        /* ------------------------------------------------------------------ */
        /*  Events                                                             */
        /* ------------------------------------------------------------------ */

        /// <summary>
        /// Raised on each successful control cycle with the latest filtered
        /// temperature and the RPM that was sent to the device.
        /// </summary>
        public event EventHandler<(double? Temp, ushort? Rpm)>? TemperatureUpdated;

        /* ------------------------------------------------------------------ */
        /*  Constructor                                                        */
        /* ------------------------------------------------------------------ */

        public FlydigiSmartControl(FlydigiCoolerService coolerService, FlydigiTemperatureProvider temperatureProvider)
        {
            _coolerService = coolerService;
            _temperatureProvider = temperatureProvider;
        }

        /* ------------------------------------------------------------------ */
        /*  Lifecycle                                                          */
        /* ------------------------------------------------------------------ */

        /// <summary>
        /// Starts the control loop timer.
        /// </summary>
        public void Start()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            Stop();

            IsActive = true;
            _timer = new Timer(Tick, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(PollIntervalMs));
        }

        /// <summary>
        /// Stops the control loop and disposes the timer.
        /// </summary>
        public void Stop()
        {
            IsActive = false;
            _timer?.Dispose();
            _timer = null;
        }

        /* ------------------------------------------------------------------ */
        /*  Control loop tick                                                  */
        /* ------------------------------------------------------------------ */

        private void Tick(object? state)
        {
            _tickCounter++;

            // 1. Guard: connected + profile
            if (!_coolerService.IsConnected || ActiveProfile == null)
                return;

            // 2. Read temperature based on TempSource
            double? rawTemp = ReadTemperature();
            if (!rawTemp.HasValue)
                return;

            // 3. Asymmetric EWMA filtering
            double filtered = ApplyEwmaFilter(rawTemp.Value);

            // 4. Compute target RPM from fan curve (always, even when hysteresis blocks).
            // This ensures _lastTargetRpm stays current for the keepalive below.
            ushort targetRpm = ActiveProfile.GetRpmForTemperature(filtered);

            // 5. Apply speed avoidance
            if (Settings != null)
            {
                targetRpm = FlydigiSpeedAvoidance.Apply(
                    targetRpm,
                    Settings.AvoidanceEnabled,
                    Settings.AvoidanceStartRpm,
                    Settings.AvoidanceEndRpm,
                    filtered);
            }

            // 6. Apply learning offset
            if (Settings != null && Settings.LearningEnabled)
            {
                _learningEngine.IsLearningEnabled = true;
                _learningEngine.BiasMode = Settings.LearningBias;

                // Determine which curve segment (gear index) we're in for per-gear learning
                int gearIndex = GetCurveSegmentIndex(filtered);

                // Feed observation: observed temp vs. the implied target temp for current RPM
                // The target temp is inverse: what temp should produce this RPM on the curve.
                // We use the filtered temp as both observed and derive target from curve inverse.
                if (_lastCommandedRpm.HasValue)
                {
                    double impliedTargetTemp = ActiveProfile.GetTemperatureForRpm(_lastCommandedRpm.Value);
                    _learningEngine.FeedObservation(gearIndex, filtered, impliedTargetTemp, _lastCommandedRpm.Value);
                }

                // Apply learned offset to RPM target
                double offset = _learningEngine.GetEffectiveOffset(gearIndex);
                if (offset != 0.0)
                {
                    int adjustedRpm = (int)targetRpm + (int)Math.Round(offset);
                    targetRpm = (ushort)Math.Clamp(adjustedRpm, FlydigiFanCurveProfile.MinRpm, FlydigiFanCurveProfile.MaxRpm);
                }
            }

            // 7. Critical temperature overrides
            targetRpm = ApplyCriticalTempOverride(targetRpm, filtered);

            // 7. Store the un-limited target for keepalive use
            _lastTargetRpm = targetRpm;

            // 8. Hysteresis deadzone check — gates the ramp-limited command to the device.
            // The keepalive (below) still runs to prevent device drift, but uses the
            // current target RPM so the fan gradually reaches the correct speed.
            bool hysteresisBlocked = _filteredTemperature.HasValue
                && Math.Abs(filtered - _filteredTemperature.Value) < HysteresisDeadzone;

            // 9. Periodic keepalive: re-send the current target RPM every 5 ticks (~5 seconds)
            // to maintain the device's realtime mode. Uses the freshly computed target RPM
            // with ramp-limiting so that during hysteresis-blocking periods the fan gradually
            // reaches the correct speed rather than staying at a stale value or jumping.
            if (_tickCounter % 5 == 0 && _lastTargetRpm.Value > 0)
            {
                ushort keepaliveRpm = ApplyKeepaliveRampLimit(_lastTargetRpm.Value);
                _lastKeepaliveRpm = keepaliveRpm;
                _ = _coolerService.WriteRealtimeRpmAsync(keepaliveRpm, force: true);
            }

            if (hysteresisBlocked)
                return;

            _filteredTemperature = filtered;

            // 10. Ramp limiting
            targetRpm = ApplyRampLimit(targetRpm);

            // 11. Send to device (fire-and-forget)
            _ = _coolerService.WriteRealtimeRpmAsync(targetRpm);
            _lastCommandedRpm = targetRpm;

            // 12. Update properties and raise event
            CurrentTemperature = filtered;
            TargetRpm = targetRpm;
            TemperatureUpdated?.Invoke(this, (filtered, targetRpm));
        }

        /* ------------------------------------------------------------------ */
        /*  Temperature reading                                                */
        /* ------------------------------------------------------------------ */

        private double? ReadTemperature()
        {
            return TempSource.ToLowerInvariant() switch
            {
                "cpu" => _temperatureProvider.GetCpuTemperature(),
                "gpu" => _temperatureProvider.GetGpuTemperature(),
                "max" => _temperatureProvider.GetMaxTemperature(),
                _ => _temperatureProvider.GetMaxTemperature()
            };
        }

        /* ------------------------------------------------------------------ */
        /*  EWMA filter                                                        */
        /* ------------------------------------------------------------------ */

        /// <summary>
        /// Applies symmetric exponential weighted moving average.
        /// Uses alpha=0.5 for both rising and falling temperatures.
        /// The hysteresis deadzone prevents fan oscillation — no need for
        /// asymmetric slow-decay which causes stale RPM targets when temps drop.
        /// </summary>
        private double ApplyEwmaFilter(double rawTemp)
        {
            if (!_filteredTemperature.HasValue)
                return rawTemp;

            const double alpha = 0.5;
            return alpha * rawTemp + (1.0 - alpha) * _filteredTemperature.Value;
        }

        /* ------------------------------------------------------------------ */
        /*  Critical temperature override                                      */
        /* ------------------------------------------------------------------ */

        /// <summary>
        /// Forces high fan speed when temperature reaches critical levels.
        /// >= 90°C → 4000 RPM (full speed).
        /// >= 85°C → at least 3500 RPM.
        /// </summary>
        private static ushort ApplyCriticalTempOverride(ushort targetRpm, double temp)
        {
            if (temp >= 90.0)
                return 4000;
            if (temp >= 85.0)
                return (ushort)Math.Max((int)targetRpm, 3500);
            return targetRpm;
        }

        /* ------------------------------------------------------------------ */
        /*  Ramp limiting                                                      */
        /* ------------------------------------------------------------------ */

        /// <summary>
        /// Asymmetric EWMA ramp limiter. Fast ramp up (alpha=0.4) so the fan
        /// responds quickly to heating. Slow ramp down (alpha=0.08) so the fan
        /// decreases gradually when temps drop, avoiding sudden noise changes.
        /// </summary>
        private ushort ApplyRampLimit(ushort targetRpm)
        {
            if (!_lastSmoothedRpm.HasValue)
                return targetRpm;

            int delta = targetRpm - _lastSmoothedRpm.Value;
            double alpha = delta >= 0 ? 0.4 : 0.08;

            double smoothed = _lastSmoothedRpm.Value + alpha * delta;
            _lastSmoothedRpm = (ushort)Math.Round(smoothed);
            return _lastSmoothedRpm.Value;
        }

        /// <summary>
        /// Clamps the keepalive RPM change to ±MaxRpmChangePerCycle from the last
        /// keepalive RPM. This allows the fan to gradually reach the correct target
        /// during hysteresis-blocking periods without sudden jumps.
        /// </summary>
        private ushort ApplyKeepaliveRampLimit(ushort targetRpm)
        {
            if (!_lastKeepaliveRpm.HasValue)
                return targetRpm;

            int delta = targetRpm - _lastKeepaliveRpm.Value;
            int clampedDelta = Math.Clamp(delta, -MaxRpmChangePerCycle, MaxRpmChangePerCycle);
            return (ushort)(_lastKeepaliveRpm.Value + clampedDelta);
        }

        /// <summary>
        /// Returns the index of the curve segment the given temperature falls into.
        /// Used as a gear index for per-segment learning.
        /// </summary>
        private int GetCurveSegmentIndex(double temp)
        {
            if (ActiveProfile == null || ActiveProfile.Points.Count < 2)
                return 0;

            var sorted = ActiveProfile.Points.OrderBy(p => p.Temperature).ToList();
            for (int i = 0; i < sorted.Count - 1; i++)
            {
                if (temp >= sorted[i].Temperature && temp < sorted[i + 1].Temperature)
                    return i;
            }
            return Math.Max(0, sorted.Count - 2);
        }

        /* ------------------------------------------------------------------ */
        /*  IDisposable                                                        */
        /* ------------------------------------------------------------------ */

        public void Dispose()
        {
            if (!_disposed)
            {
                Stop();
                _disposed = true;
            }
            GC.SuppressFinalize(this);
        }
    }
}
