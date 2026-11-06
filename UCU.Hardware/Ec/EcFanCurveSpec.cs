using System.Collections.Generic;

namespace UCU.Hardware.Ec
{
    /// <summary>
    /// A fan curve as the EC engine needs it: a display name for the log and one duty
    /// percentage (0-100) per zone. Deliberately a plain data type — the UI's editable
    /// curve model (presets, temperature labels, persistence) stays in the app and is
    /// converted with <c>EcFanCurve.ToEngineSpec()</c>, so the hardware layer never
    /// depends on a UI/persistence type and the same shape crosses the RPC contract
    /// unchanged.
    /// </summary>
    public sealed class EcFanCurveSpec
    {
        public EcFanCurveSpec(string name, IReadOnlyList<int> dutyPercentByZone)
        {
            Name = name;
            DutyPercentByZone = dutyPercentByZone;
        }

        /// <summary>Curve name, used only for the write-sequence log.</summary>
        public string Name { get; }

        /// <summary>
        /// Duty per EC zone as a UI percentage (0-100). Index 0 is the OFF zone; the
        /// engine scales onto the EC 0-200 scale through <c>EcDutyCodec</c>.
        /// </summary>
        public IReadOnlyList<int> DutyPercentByZone { get; }
    }
}
