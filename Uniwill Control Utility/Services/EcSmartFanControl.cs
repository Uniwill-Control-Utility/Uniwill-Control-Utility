using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Serilog;
using Uniwill_Control_Utility.Models;

namespace Uniwill_Control_Utility.Services
{
    /// <summary>
    /// Temperature-driven smart fan control for the Uniwill EC.
    ///
    /// Reads temperatures on a timer, evaluates a fan curve, and updates the
    /// CPU and GPU duty tables so both fans respond to a single temperature
    /// source. Temperature tables are configured once when the loop starts.
    /// This follows the same approach as
    /// MechControl's "Link Fans" feature — curve table updates instead of
    /// direct PWM writes, which the EC respects for both fans.
    ///
    /// This enables cross-sensor fan control (e.g., both fans react to CPU
    /// temperature) without disabling the EC firmware's thermal protection.
    /// </summary>
    public sealed class EcSmartFanControl : IDisposable
    {
        /* ------------------------------------------------------------------ */
        /*  App-scoped singleton — ensures only one control loop runs at a    */
        /*  time.  DashboardViewModel starts it on app launch, MainWindow     */
        /*  restarts it on resume, and the FanControl page reuses it.        */
        /* ------------------------------------------------------------------ */

        private static EcSmartFanControl? _instance;
        private static readonly object _instanceLock = new();

        /// <summary>
        /// Returns the current app-scoped instance, or null if not started.
        /// </summary>
        public static EcSmartFanControl? CurrentInstance => _instance;

        /// <summary>
        /// Creates or returns the app-scoped singleton instance.
        /// If an instance already exists and is active, it updates the curve
        /// and settings instead of creating a new one.
        /// </summary>
        public static EcSmartFanControl GetOrCreate(
            UniwillECService ec,
            FlydigiTemperatureProvider temperatureProvider,
            FanControlSettings settings,
            EcFanCurve curve,
            int[] temperatures)
        {
            lock (_instanceLock)
            {
                if (_instance != null && _instance.IsActive)
                {
                    // Instance already running — update curve/settings in place
                    _instance.UpdateCurve(curve, temperatures);
                    _instance.UpdateSettings(settings);
                    return _instance;
                }

                // Create new instance
                _instance = new EcSmartFanControl(ec, temperatureProvider, settings, curve, temperatures);
                return _instance;
            }
        }

        /// <summary>
        /// Stops and clears the app-scoped singleton instance.
        /// </summary>
        public static void ClearInstance()
        {
            lock (_instanceLock)
            {
                if (_instance != null)
                {
                    _instance.Stop();
                    _instance = null;
                }
            }
        }

        /// <summary>
        /// Stops the singleton if it exists, then creates and starts a fresh instance.
        /// Useful after resume from sleep/hibernation when the EC needs re-initialization.
        /// </summary>
        public static EcSmartFanControl Recreate(
            UniwillECService ec,
            FlydigiTemperatureProvider temperatureProvider,
            FanControlSettings settings,
            EcFanCurve curve,
            int[] temperatures)
        {
            lock (_instanceLock)
            {
                _instance?.Stop();
                _instance = new EcSmartFanControl(ec, temperatureProvider, settings, curve, temperatures);
                return _instance;
            }
        }

        private readonly UniwillECService _ec;
        private readonly FlydigiTemperatureProvider _temperatureProvider;
        private readonly EcSmartFanCycleGate _cycleGate = new();
        private Timer? _timer;
        private bool _disposed;
        private bool _curveTablesInitialized;

        /// <summary>EWMA-filtered temperature carried across ticks.</summary>
        private double? _filteredTemperature;

        /// <summary>Last PWM duty value written to the EC (for ramp limiting).</summary>
        private int? _lastDuty;

        /// <summary>Settings for temperature source and safety thresholds.</summary>
        private FanControlSettings _settings;

        /// <summary>The active fan curve to evaluate.</summary>
        private EcFanCurve _curve;

        /// <summary>Temperature thresholds to use for curve evaluation.</summary>
        private int[] _temperatures;

        /* ------------------------------------------------------------------ */
        /*  Public state                                                       */
        /* ------------------------------------------------------------------ */

        /// <summary>Whether the control loop is currently running.</summary>
        public bool IsActive { get; private set; }

        /// <summary>Latest filtered temperature reading.</summary>
        public double? CurrentTemperature { get; private set; }

        /// <summary>Latest PWM duty (0-200) written to the EC.</summary>
        public int? LastDuty { get; private set; }

        /// <summary>Current temperature source setting.</summary>
        public string TempSource
        {
            get => _settings.EcSmartFanTempSource;
            set
            {
                _settings.EcSmartFanTempSource = value;
                // Force re-evaluation on next tick
                _filteredTemperature = null;
                _lastDuty = null;
            }
        }

        /* ------------------------------------------------------------------ */
        /*  Configuration                                                      */
        /* ------------------------------------------------------------------ */

        /// <summary>How often (ms) to read temperature and update fan speed.</summary>
        public int PollIntervalMs => (int)_cycleGate.PollInterval.TotalMilliseconds;

        /// <summary>Maximum duty change allowed per cycle (ramp limiter). Default 20 (10%).</summary>
        public int MaxDutyChangePerCycle { get; set; } = 20;

        /// <summary>
        /// Hysteresis deadzone in degrees Celsius. Temperature changes smaller than
        /// this threshold are ignored to prevent oscillation. Default 2.0.
        /// </summary>
        public double HysteresisDeadzone { get; set; } = 2.0;

        /// <summary>Critical temperature (°C) at which fans force to 100%. Default 92.</summary>
        public double CriticalTemperature { get; set; } = 92.0;

        /// <summary>Warning temperature (°C) at which fans force to at least 80%. Default 87.</summary>
        public double WarningTemperature { get; set; } = 87.0;

        /* ------------------------------------------------------------------ */
        /*  Events                                                             */
        /* ------------------------------------------------------------------ */

        /// <summary>
        /// Raised on each successful control cycle with the latest filtered
        /// temperature and the duty that was written to the EC.
        /// </summary>
        public event EventHandler<(double? Temp, int? Duty)>? TemperatureUpdated;

        /// <summary>
        /// Whether any subscriber has registered for TemperatureUpdated.
        /// Used by callers to avoid duplicate subscriptions on the singleton.
        /// </summary>
        public bool HasTemperatureSubscribers => TemperatureUpdated != null;

        /* ------------------------------------------------------------------ */
        /*  Constructor                                                        */
        /* ------------------------------------------------------------------ */

        public EcSmartFanControl(
            UniwillECService ec,
            FlydigiTemperatureProvider temperatureProvider,
            FanControlSettings settings,
            EcFanCurve curve,
            int[] temperatures)
        {
            _ec = ec;
            _temperatureProvider = temperatureProvider;
            _settings = settings;
            _curve = curve;
            _temperatures = temperatures;
        }

        /// <summary>
        /// Updates the active curve and temperature thresholds.
        /// Call this when the user changes the fan curve on the Fan Control page.
        /// </summary>
        public void UpdateCurve(EcFanCurve curve, int[] temperatures)
        {
            _curve = curve;
            _temperatures = temperatures;
            // Force re-evaluation on next tick
            _filteredTemperature = null;
            _lastDuty = null;
        }

        /// <summary>
        /// Updates the settings reference (e.g., when temp source changes).
        /// </summary>
        public void UpdateSettings(FanControlSettings settings)
        {
            _settings = settings;
        }

        /* ------------------------------------------------------------------ */
        /*  Lifecycle                                                          */
        /* ------------------------------------------------------------------ */

        /// <summary>
        /// Starts the control loop timer.
        /// Does NOT use USER mode — instead dynamically rebuilds fan curve tables
        /// so both fans respond to the same temperature source (MechControl pattern).
        /// </summary>
        public void Start()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            StopLoop();

            _curveTablesInitialized = false;
            IsActive = true;
            _timer = new Timer(Tick, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(PollIntervalMs));
        }

        /// <summary>
        /// Stops the control loop and disposes the timer, and clears the app-scoped
        /// singleton so callers know it's stopped. Smart Fan Mode keeps 0x0741 bit0
        /// armed for its whole duration (that gate is what makes the EC honor the
        /// duty tables it rewrites every tick), and the Performance-button intercept
        /// listens for the EC's press echo through that same gate, so stopping leaves
        /// manual control alone.
        /// </summary>
        public void Stop()
        {
            StopLoop();
        }

        /// Tears the loop down; Start() restarts the timer through this so a restart
        /// never tears down twice.
        private void StopLoop()
        {
            IsActive = false;
            _timer?.Dispose();
            _timer = null;

            // Clear singleton reference so GetOrCreate creates a fresh instance next time
            lock (_instanceLock)
            {
                if (_instance == this)
                    _instance = null;
            }
        }

        /* ------------------------------------------------------------------ */
        /*  Control loop tick                                                  */
        /* ------------------------------------------------------------------ */

        private void Tick(object? state)
        {
            DateTimeOffset cycleStarted = DateTimeOffset.UtcNow;
            if (!_cycleGate.TryEnter(cycleStarted))
                return;

            bool cycleCompleted = false;
            bool writeSucceeded = false;

            try
            {
                if (!IsActive)
                {
                    _cycleGate.CompleteWithoutWrite();
                    cycleCompleted = true;
                    return;
                }

                // 1. Read temperature based on TempSource
                double? rawTemp = ReadTemperature();
                if (!rawTemp.HasValue)
                {
                    Log.Warning("[DIAG] EcSmartFanControl: No temperature reading available");
                    _cycleGate.CompleteWithoutWrite();
                    cycleCompleted = true;
                    return;
                }

                // 2. EWMA filtering
                double filtered = ApplyEwmaFilter(rawTemp.Value);

                // 3. Critical temperature overrides (always evaluate, even through hysteresis)
                int targetDuty = EvaluateCurve(filtered);
                targetDuty = ApplyCriticalTempOverride(targetDuty, filtered);

                // 4. Hysteresis deadzone check. A failed write clears the filter state,
                // ensuring the desired duty is retried after the backoff period.
                bool hysteresisBlocked = _filteredTemperature.HasValue
                    && Math.Abs(filtered - _filteredTemperature.Value) < HysteresisDeadzone;

                if (hysteresisBlocked)
                {
                    CurrentTemperature = filtered;
                    TemperatureUpdated?.Invoke(this, (filtered, LastDuty));
                    _cycleGate.CompleteWithoutWrite();
                    cycleCompleted = true;
                    return;
                }

                // 5. Ramp limiting
                targetDuty = ApplyRampLimit(targetDuty);

                // 6. Write only when duty changes by >= 2% to avoid EC spam.
                bool shouldWrite = !_lastDuty.HasValue
                    || Math.Abs(targetDuty - _lastDuty.Value) >= 4; // 4/200 = 2%

                Log.Information("[DIAG] EcSmartFanControl tick: temp {Temp:F1}°C, targetDuty {Duty} ({DutyPct}%), shouldWrite={ShouldWrite}, lastDuty={LastDuty}",
                    filtered, targetDuty, targetDuty * 100 / 200, shouldWrite, _lastDuty);

                if (shouldWrite)
                {
                    byte[] cpuDutyTable = BuildFlatDutyTable(targetDuty);
                    byte[] gpuDutyTable = BuildFlatDutyTable(targetDuty);

                    try
                    {
                        if (!_curveTablesInitialized)
                        {
                            byte[] tempUp = BuildTempUpTable();
                            byte[] tempDown = BuildTempDownTable();
                            _ec.ApplySmartFanCurve(tempUp, tempDown, cpuDutyTable,
                                tempUp, tempDown, gpuDutyTable);
                            _curveTablesInitialized = true;
                        }
                        else
                        {
                            _ec.ApplySmartFanDuty(cpuDutyTable, gpuDutyTable);
                        }
                    }
                    catch (Exception ex)
                    {
                        _filteredTemperature = null;
                        _lastDuty = null;
                        _cycleGate.CompleteFailure(DateTimeOffset.UtcNow);
                        cycleCompleted = true;
                        Log.Warning(ex, "[DIAG] EcSmartFanControl: Curve apply failed; repeated failures trigger a 5 second pause");
                        return;
                    }

                    _lastDuty = targetDuty;
                    writeSucceeded = true;
                }

                _filteredTemperature = filtered;
                LastDuty = targetDuty;

                // 7. Update properties and raise event
                CurrentTemperature = filtered;
                TemperatureUpdated?.Invoke(this, (filtered, targetDuty));

                if (writeSucceeded)
                    _cycleGate.CompleteSuccess();
                else
                    _cycleGate.CompleteWithoutWrite();
                cycleCompleted = true;
            }
            catch (Exception ex)
            {
                if (writeSucceeded)
                    _cycleGate.CompleteSuccess();
                else
                    _cycleGate.CompleteWithoutWrite();
                cycleCompleted = true;
                Log.Warning(ex, "[DIAG] EcSmartFanControl: Control cycle failed");
            }
            finally
            {
                if (!cycleCompleted)
                    _cycleGate.CompleteWithoutWrite();
            }
        }

        /// <summary>
        /// Number of active curve entries the EC respects (platform-specific).
        /// The IDY_7Y / NB05 platform uses 10 active entries out of 16 total.
        /// Entries 0..9 are active, entries 10..15 are padding.
        /// </summary>
        private const int ActiveEntries = 10;

        /// <summary>
        /// Padding temperature value for inactive zones (matches MechControl profile).
        /// </summary>
        private const byte PaddingTemp = 255;

        /// <summary>
        /// Fixed temperature thresholds (matches MechControl's LinkedFanController).
        /// The EC uses its own internal temperature sensor to pick zones based on these.
        /// </summary>
        private static readonly int[] FixedTemps = { 19, 29, 39, 49, 57, 65, 73, 81, 89, 97 };

        /// <summary>
        /// Builds a 16-byte tempUp table for smart fan mode using fixed thresholds.
        /// The EC reads its own internal sensor to pick which zone to activate.
        ///
        /// Zones 0..ActiveEntries-1: FixedTemps (19, 29, 39, 49, 57, 65, 73, 81, 89, 97)
        /// Zones ActiveEntries..15: 255 (padding)
        /// </summary>
        private byte[] BuildTempUpTable()
        {
            byte[] table = new byte[16];
            // Use fixed temps matching MechControl so the EC can pick zones properly.
            for (int i = 0; i < ActiveEntries; i++)
                table[i] = (byte)FixedTemps[i];
            for (int i = ActiveEntries; i < 16; i++)
                table[i] = PaddingTemp;
            return table;
        }

        /// <summary>
        /// Builds a 16-byte tempDown (hysteresis) table for smart fan mode.
        /// Each zone's tempDown is set to the previous zone's temp minus 4°C
        /// (matching MechControl's MinHeadroomCelsius), with zone 0 = 0.
        ///
        /// Zone 0: 0
        /// Zone i: max(0, FixedTemps[i-1] - 4)
        /// Zone ActiveEntries: FixedTemps[ActiveEntries-1] + 4 (boundary)
        /// Zones ActiveEntries+1..15: 255 (padding)
        /// </summary>
        private byte[] BuildTempDownTable()
        {
            byte[] table = new byte[16];
            // Zone 0 has no hysteresis drop
            table[0] = 0;
            // Zones 1..ActiveEntries-1 use previous temp - 4
            for (int i = 1; i < ActiveEntries; i++)
                table[i] = (byte)Math.Max(0, FixedTemps[i - 1] - 4);
            // Boundary entry
            if (ActiveEntries < 16)
                table[ActiveEntries] = (byte)Math.Min(255, FixedTemps[ActiveEntries - 1] + 4);
            // Pad remaining zones with 255
            for (int i = ActiveEntries + 1; i < 16; i++)
                table[i] = PaddingTemp;
            return table;
        }

        /// <summary>
        /// Builds a 16-byte duty table for smart fan mode.
        /// Follows MechControl's BuildSafeCurve pattern:
        /// - duty[0] = 0 (EC requirement, zone 0 is "below threshold")
        /// - duty[1..ActiveEntries-1] = targetDuty (flat operating duty)
        /// - duty[ActiveEntries..15] = 200 (safety max / padding)
        ///
        /// The EC reads its own internal sensor to pick a zone based on tempUp thresholds.
        /// Since all active zones (1+) have the same duty, the fan runs at targetDuty
        /// regardless of which zone the EC picks.
        /// </summary>
        private byte[] BuildFlatDutyTable(int targetDuty)
        {
            const int maxDuty = 200;
            targetDuty = Math.Clamp(targetDuty, 2, maxDuty);
            byte[] table = new byte[16];

            // Zone 0 is duty=0 (EC requirement, MechControl pattern)
            table[0] = 0;

            // Zones 1..ActiveEntries-1 all get the same target duty
            for (int i = 1; i < ActiveEntries; i++)
                table[i] = (byte)targetDuty;

            // Zones ActiveEntries..15 use max duty (safety net + padding)
            for (int i = ActiveEntries; i < 16; i++)
                table[i] = (byte)maxDuty;

            return table;
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
        /*  Curve evaluation                                                   */
        /* ------------------------------------------------------------------ */

        /// <summary>
        /// Evaluates the fan curve at the given temperature and returns the duty (0-200).
        /// Uses linear interpolation between the nearest temperature thresholds.
        /// Note: GetDuty() returns 0-100%, so we multiply by 2 to get the EC's 0-200 scale.
        /// </summary>
        private int EvaluateCurve(double temp)
        {
            if (_temperatures.Length < 2)
                return _curve.GetDuty(0) * 2;

            // Find the zone this temperature falls into
            int zoneIndex = FindZoneIndex(temp);

            // Get duties for this zone and the next (for interpolation).
            // GetDuty() returns 0-100%, so convert to 0-200 scale.
            int dutyLow = _curve.GetDuty(zoneIndex) * 2;
            int dutyHigh = zoneIndex + 1 < _curve.Duties.Count
                ? _curve.GetDuty(zoneIndex + 1) * 2
                : dutyLow;

            // Get temperature bounds for interpolation
            int tempLow = _temperatures[Math.Max(0, zoneIndex)];
            int tempHigh = zoneIndex + 1 < _temperatures.Length
                ? _temperatures[zoneIndex + 1]
                : tempLow;

            // Linear interpolation between zones
            if (tempHigh > tempLow)
            {
                double fraction = (temp - tempLow) / (tempHigh - tempLow);
                fraction = Math.Clamp(fraction, 0, 1);
                return (int)Math.Round(dutyLow + fraction * (dutyHigh - dutyLow));
            }

            return dutyLow;
        }

        /// <summary>
        /// Finds the zone index for the given temperature.
        /// Returns an index into the 11-element temperature/duty arrays.
        /// </summary>
        private int FindZoneIndex(double temp)
        {
            // Zone 0 is "below threshold" — temp index 0 is always 0 (or very low)
            for (int i = _temperatures.Length - 1; i >= 0; i--)
            {
                if (temp >= _temperatures[i])
                    return Math.Clamp(i, 0, _temperatures.Length - 2);
            }
            return 0;
        }

        /* ------------------------------------------------------------------ */
        /*  EWMA filter                                                        */
        /* ------------------------------------------------------------------ */

        /// <summary>
        /// Applies exponential weighted moving average to smooth temperature readings.
        /// Uses alpha=0.5 for symmetric response.
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
        /// >= CriticalTemperature → 200 (100% duty).
        /// >= WarningTemperature → at least 160 (80% duty).
        /// </summary>
        private int ApplyCriticalTempOverride(int targetDuty, double temp)
        {
            if (temp >= CriticalTemperature)
                return 200; // 100% duty
            if (temp >= WarningTemperature)
                return Math.Max(targetDuty, 160); // at least 80%
            return targetDuty;
        }

        /* ------------------------------------------------------------------ */
        /*  Ramp limiting                                                      */
        /* ------------------------------------------------------------------ */

        /// <summary>
        /// Asymmetric ramp limiter. Fast ramp up (alpha=0.4) so the fan
        /// responds quickly to heating. Slow ramp down (alpha=0.08) so the fan
        /// decreases gradually when temps drop, avoiding sudden noise changes.
        /// </summary>
        private int ApplyRampLimit(int targetDuty)
        {
            if (!_lastDuty.HasValue)
                return targetDuty;

            int delta = targetDuty - _lastDuty.Value;

            // Clamp the delta to the max change per cycle
            int clampedDelta = Math.Clamp(delta, -MaxDutyChangePerCycle, MaxDutyChangePerCycle);
            return _lastDuty.Value + clampedDelta;
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
