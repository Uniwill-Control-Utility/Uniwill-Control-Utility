using System;
using System.Threading;
using Universal_x86_Tuning_Utility.Models;

namespace Universal_x86_Tuning_Utility.Services
{
    /// <summary>
    /// Temperature-driven fan control service for the Flydigi BS1 (BLE-only) cooler.
    /// Unlike BS2+, the BS1 has no built-in temperature curve, so all curve computation
    /// happens host-side: read temperature → interpolate fan curve → send RPM via BLE.
    /// </summary>
    public class Bs1SmartControl : IDisposable
    {
        private readonly Bs1Service _bs1Service;
        private readonly FlydigiTemperatureProvider _temperatureProvider;
        private Timer? _timer;
        private bool _disposed;

        /// <summary>EWMA-filtered temperature carried across ticks.</summary>
        private double? _filteredTemperature;

        /// <summary>Last RPM value actually commanded to the device (for ramp limiting).</summary>
        private ushort? _lastCommandedRpm;

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
                _filteredTemperature = null;
                _lastCommandedRpm = null;
            }
        }
        private FlydigiFanCurveProfile? _activeProfile;

        /// <summary>Settings for avoidance zones.</summary>
        public Bs1Settings? Settings { get; set; }

        /* ------------------------------------------------------------------ */
        /*  Configuration                                                      */
        /* ------------------------------------------------------------------ */

        /// <summary>How often (ms) to read temperature and update fan speed. Default 2000.</summary>
        public int PollIntervalMs { get; set; } = 2000;

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

        public Bs1SmartControl(Bs1Service bs1Service, FlydigiTemperatureProvider temperatureProvider)
        {
            _bs1Service = bs1Service;
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
            if (_disposed)
                throw new ObjectDisposedException(nameof(Bs1SmartControl));

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

        private async void Tick(object? state)
        {
            // 1. Guard: connected + profile
            if (!_bs1Service.IsConnected || ActiveProfile == null)
                return;

            // 2. Read temperature based on TempSource
            double? rawTemp = ReadTemperature();
            if (!rawTemp.HasValue)
                return;

            // 3. Asymmetric EWMA filtering
            double filtered = ApplyEwmaFilter(rawTemp.Value);

            // 4. Hysteresis deadzone check
            if (_filteredTemperature.HasValue && Math.Abs(filtered - _filteredTemperature.Value) < HysteresisDeadzone)
                return;

            _filteredTemperature = filtered;

            // 5. Compute target RPM from fan curve (clamped to BS1 range 1300-3000)
            ushort targetRpm = ActiveProfile.GetRpmForTemperature(filtered);
            targetRpm = (ushort)Math.Clamp(targetRpm, Bs1DefaultGearRpm.MinRpm, Bs1DefaultGearRpm.MaxRpm);

            // 6. Apply speed avoidance
            if (Settings != null)
            {
                targetRpm = FlydigiSpeedAvoidance.Apply(
                    targetRpm,
                    Settings.AvoidanceEnabled,
                    Settings.AvoidanceStartRpm,
                    Settings.AvoidanceEndRpm,
                    filtered);
            }

            // 7. Critical temperature overrides (BS1 max is 3000)
            targetRpm = ApplyCriticalTempOverride(targetRpm, filtered);

            // 8. Ramp limiting
            targetRpm = ApplyRampLimit(targetRpm);

            // 9. Send to device (fire-and-forget)
            _ = _bs1Service.WriteRpmAsync(targetRpm);
            _lastCommandedRpm = targetRpm;

            // 10. Update properties and raise event
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
        /// Applies asymmetric exponential weighted moving average.
        /// Rising temperatures use alpha=0.5 (fast response).
        /// Falling temperatures use alpha=0.15 (slow decay, prevents fan flutter).
        /// </summary>
        private double ApplyEwmaFilter(double rawTemp)
        {
            if (!_filteredTemperature.HasValue)
                return rawTemp;

            double alpha = rawTemp >= _filteredTemperature.Value ? 0.5 : 0.15;
            return alpha * rawTemp + (1.0 - alpha) * _filteredTemperature.Value;
        }

        /* ------------------------------------------------------------------ */
        /*  Critical temperature override                                      */
        /* ------------------------------------------------------------------ */

        /// <summary>
        /// Forces high fan speed when temperature reaches critical levels.
        /// >= 90°C → 3000 RPM (BS1 max speed).
        /// >= 85°C → at least 2500 RPM.
        /// </summary>
        private ushort ApplyCriticalTempOverride(ushort targetRpm, double temp)
        {
            if (temp >= 90.0)
                return Bs1DefaultGearRpm.MaxRpm; // 3000
            if (temp >= 85.0)
                return (ushort)Math.Max((int)targetRpm, 2500);
            return targetRpm;
        }

        /* ------------------------------------------------------------------ */
        /*  Ramp limiting                                                      */
        /* ------------------------------------------------------------------ */

        /// <summary>
        /// Clamps the RPM change to ±MaxRpmChangePerCycle from the last commanded RPM.
        /// Prevents sudden, jarring fan speed jumps.
        /// </summary>
        private ushort ApplyRampLimit(ushort targetRpm)
        {
            if (!_lastCommandedRpm.HasValue)
                return targetRpm;

            int delta = targetRpm - _lastCommandedRpm.Value;
            int clampedDelta = Math.Clamp(delta, -MaxRpmChangePerCycle, MaxRpmChangePerCycle);
            return (ushort)(_lastCommandedRpm.Value + clampedDelta);
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
        }
    }
}
