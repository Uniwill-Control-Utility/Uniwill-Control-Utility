using System;
using System.Collections.Generic;

namespace Uniwill_Control_Utility.Models
{
    /// <summary>
    /// Observes steady-state temperatures and adjusts fan curve offsets automatically.
    /// Uses exponential moving average to learn heat and cool offsets per fan-gear index.
    /// </summary>
    public class FlydigiLearningEngine
    {
        /* ------------------------------------------------------------------ */
        /*  Configuration                                                      */
        /* ------------------------------------------------------------------ */

        /// <summary>
        /// Whether learning is active. When false, <see cref="FeedObservation"/> is a no-op.
        /// </summary>
        public bool IsLearningEnabled { get; set; }

        /// <summary>
        /// How aggressively to apply learned offsets (0–1). Default 0.1.
        /// Higher values converge faster but may overshoot.
        /// </summary>
        public double LearnRate { get; set; } = 0.1;

        /// <summary>
        /// Bias mode for asymmetric learning:
        /// <list type="bullet">
        ///   <item><description>"balanced" — symmetric learning (default)</description></item>
        ///   <item><description>"cooling" — learn 1.5× faster on heat</description></item>
        ///   <item><description>"quiet" — learn 1.5× faster on cool</description></item>
        /// </list>
        /// </summary>
        public string BiasMode { get; set; } = "balanced";

        /// <summary>
        /// How long (seconds) the fan speed must remain stable before learning triggers.
        /// Default 30.
        /// </summary>
        public int StableThresholdSeconds { get; set; } = 30;

        /* ------------------------------------------------------------------ */
        /*  Learned state                                                      */
        /* ------------------------------------------------------------------ */

        /// <summary>Per-gear offsets learned when the system runs hotter than target.</summary>
        public Dictionary<int, double> HeatOffsets { get; private set; } = new();

        /// <summary>Per-gear offsets learned when the system runs cooler than target.</summary>
        public Dictionary<int, double> CoolOffsets { get; private set; } = new();

        /* ------------------------------------------------------------------ */
        /*  RPM stability tracker                                              */
        /* ------------------------------------------------------------------ */

        private readonly Dictionary<int, ushort> _lastRpm = new();
        private readonly Dictionary<int, DateTime> _stableSince = new();

        /// <summary>
        /// Initializes a new learning engine with default parameters.
        /// </summary>
        public FlydigiLearningEngine()
        {
        }

        /* ------------------------------------------------------------------ */
        /*  Public API                                                         */
        /* ------------------------------------------------------------------ */

        /// <summary>
        /// Feeds a new temperature observation and updates learned offsets if the fan
        /// speed has been stable for at least <see cref="StableThresholdSeconds"/>.
        /// </summary>
        /// <param name="gearIndex">Fan-gear index the observation belongs to.</param>
        /// <param name="observedTemp">Current temperature reported by the sensor.</param>
        /// <param name="targetTemp">Target temperature for the gear.</param>
        /// <param name="currentRpm">Current fan RPM (used for stability gating).</param>
        public void FeedObservation(int gearIndex, double observedTemp, double targetTemp, ushort currentRpm)
        {
            if (!IsLearningEnabled)
                return;

            double delta = observedTemp - targetTemp;

            // Track RPM stability
            if (_lastRpm.TryGetValue(gearIndex, out ushort lastRpm))
            {
                if (lastRpm != currentRpm)
                {
                    // RPM changed — reset stability timer
                    _stableSince[gearIndex] = DateTime.UtcNow;
                }
            }
            else
            {
                _lastRpm[gearIndex] = currentRpm;
                _stableSince[gearIndex] = DateTime.UtcNow;
            }

            _lastRpm[gearIndex] = currentRpm;

            // Gate on stability: only learn if RPM has been stable long enough
            if (!HasStabilized(gearIndex))
                return;

            // Skip zero-delta observations (nothing to learn)
            if (delta == 0.0)
                return;

            if (delta > 0)
            {
                // Running hot — update heat offset
                double alpha = GetAlpha(gearIndex, true);
                UpdateOffset(HeatOffsets, gearIndex, delta, alpha);
            }
            else
            {
                // Running cool — update cool offset with absolute delta
                double alpha = GetAlpha(gearIndex, false);
                UpdateOffset(CoolOffsets, gearIndex, Math.Abs(delta), alpha);
            }
        }

        /// <summary>
        /// Returns the effective offset for a gear. If both heat and cool offsets
        /// exist, returns their average. Otherwise returns whichever exists.
        /// </summary>
        public double GetEffectiveOffset(int gearIndex)
        {
            bool hasHeat = HeatOffsets.TryGetValue(gearIndex, out double heatVal);
            bool hasCool = CoolOffsets.TryGetValue(gearIndex, out double coolVal);

            if (hasHeat && hasCool)
                return (heatVal + coolVal) / 2.0;
            if (hasHeat)
                return heatVal;
            if (hasCool)
                return coolVal;

            return 0.0;
        }

        /// <summary>
        /// Clears all learned offsets and resets the RPM stability tracker.
        /// </summary>
        public void Reset()
        {
            HeatOffsets.Clear();
            CoolOffsets.Clear();
            _lastRpm.Clear();
            _stableSince.Clear();
        }

        /* ------------------------------------------------------------------ */
        /*  Internals                                                          */
        /* ------------------------------------------------------------------ */

        private bool HasStabilized(int gearIndex)
        {
            if (!_stableSince.TryGetValue(gearIndex, out DateTime since))
                return false;

            return (DateTime.UtcNow - since).TotalSeconds >= StableThresholdSeconds;
        }

        private double GetAlpha(int gearIndex, bool isHeat)
        {
            double alpha = LearnRate;

            if (isHeat && BiasMode == "cooling")
                alpha *= 1.5;
            else if (!isHeat && BiasMode == "quiet")
                alpha *= 1.5;

            return alpha;
        }

        private void UpdateOffset(Dictionary<int, double> dictionary, int gearIndex, double delta, double alpha)
        {
            if (dictionary.TryGetValue(gearIndex, out double current))
            {
                // Exponential moving average
                dictionary[gearIndex] = current * (1.0 - alpha) + delta * alpha;
            }
            else
            {
                // First observation — initialize with delta * alpha
                dictionary[gearIndex] = delta * alpha;
            }
        }
    }
}
