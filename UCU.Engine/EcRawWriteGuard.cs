using Serilog;
using UCU.Contracts;
using UCU.Core.Ec;

namespace UCU.Engine
{
    /// <summary>Enforcement mode for the raw-write gate (see <see cref="EngineRuntimeOptions.RawWriteGate"/>).</summary>
    public enum RawWriteGateMode
    {
        /// <summary>Refuse the write with <see cref="EngineErrorCode.BadRequest"/>; the EC is untouched.</summary>
        Enforce,
        /// <summary>Log the refusal and let the write through — field debugging of a too-strict allowlist.</summary>
        ReportOnly,
    }

    /// <summary>
    /// The chokepoint for the raw EC-write verbs. fan.write_register_blocks and
    /// telemetry.write_register carry a caller-chosen address, and device.set_nvram_value
    /// a caller-chosen GUID; the pipe's DACL admits every interactive user, so the
    /// server side decides what those free-form targets may be — the OpenRevo
    /// raw-verb chokepoint pattern (audits of designs like this one's keep finding
    /// the "escape hatch" verb as the one door with no lock).
    ///
    /// A refusal throws <see cref="ProtocolViolationException"/>, which the
    /// dispatcher maps to <see cref="EngineErrorCode.BadRequest"/>: the write
    /// never reaches the transport, the EC keeps its previous bytes.
    /// ReportOnly mode logs and proceeds (the field-debug escape valve; the codec
    /// Validate* range checks on the semantic verbs are never report-only — a bad
    /// value is always reported, per the envelope contract).
    /// </summary>
    public sealed class EcRawWriteGuard
    {
        private readonly RawWriteGateMode _mode;

        public EcRawWriteGuard(EngineRuntimeOptions options)
        {
            _mode = options.RawWriteGate;
        }

        /// <summary>Gate one raw register block write (fan domain).</summary>
        public void GateRegisterBlockWrite(ushort address, int length)
        {
            Gate($"register block 0x{address:X4}+{length}", () => EcWriteGatePolicy.IsRawWriteAllowed(address, length));
        }

        /// <summary>Gate one raw single-register write (telemetry domain).</summary>
        public void GateRegisterWrite(ushort address)
        {
            Gate($"register 0x{address:X4}", () => EcWriteGatePolicy.IsRawWriteAllowed(address, 1));
        }

        /// <summary>Gate one NVRAM write (device domain).</summary>
        public void GateNvramWrite(string guid)
        {
            Gate($"NVRAM variable guid '{guid}'", () => EcWriteGatePolicy.IsNvramWriteAllowed(guid));
        }

        private void Gate(string target, Func<bool> allowed)
        {
            if (allowed())
                return;

            if (_mode == RawWriteGateMode.ReportOnly)
            {
                Log.Warning("[Gate] raw write to {Target} is outside the allowlist — allowing (gate=report-only)", target);
                return;
            }

            throw new ProtocolViolationException($"raw write to {target} is outside the engine's allowlist");
        }
    }
}
