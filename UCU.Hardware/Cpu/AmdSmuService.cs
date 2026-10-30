using System;
using System.Threading;
using Serilog;

namespace UCU.Hardware.Cpu
{
    /// <summary>
    /// The seam the SMU verbs run on: the production implementation drives the
    /// ported SMU stack (PawnIO + mailbox protocol) as LocalSystem; the RPC
    /// tests inject a scripted command table, because no test may write a real
    /// CPU's power telemetry registers.
    /// </summary>
    public interface IAmdSmuService
    {
        /// <summary>
        /// Execute an SMU command by name (the preset vocabulary: "set-coper",
        /// "ppt-limit", ...) with the packed value. Returns the SMU status code
        /// (0x01 = OK). Throws ArgumentOutOfRangeException for an unknown
        /// command, InvalidOperationException when PawnIO cannot initialise or
        /// the SMU answers a non-OK status.
        /// </summary>
        int Execute(string commandName, uint value, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// AMD SMU access in the engine. The Curve Optimizer and the preset SMU
    /// commands drive PawnIO, whose device ACL admits System and Administrators
    /// — an unelevated app's opens are access-denied, so every SMU preset apply
    /// on a field machine was silently dead (the row saved, the register never
    /// moved). The engine runs LocalSystem: the same PawnIO module loads there,
    /// the mailbox protocol runs there, and the verb answers with the SMU's own
    /// status code — the honest witness the app route never had.
    ///
    /// The SMU stack here is a verbatim port of the app's (RyzenSMU mailbox
    /// protocol, PawnIO transport, per-socket address/command tables): native
    /// register code must not be rewritten on the move, only re-homed. The app
    /// keeps its copy as the local fallback, exactly as it keeps its own LHM
    /// copy for the GPU — the boundary forbids the app from loading this
    /// assembly, so the duplication is the architecture, not an oversight.
    /// </summary>
    public sealed class AmdSmuService : IAmdSmuService
    {
        private static readonly object TableGate = new();
        private static bool _tablesConfigured;

        public int Execute(string commandName, uint value, CancellationToken cancellationToken = default)
        {
            EnsureTablesConfigured();

            uint[] args = new uint[6];
            args[0] = value;

            return SMUCommands.ExecuteWithStatus(commandName, args);
        }

        /// <summary>
        /// The socket address/command tables are statics on the ported stack;
        /// configure them once per process, the first command's moment (the
        /// gate: FireRange only, brand-string parsed as the app's Family class
        /// parsed it).
        /// </summary>
        private static void EnsureTablesConfigured()
        {
            lock (TableGate)
            {
                if (_tablesConfigured)
                    return;

                Addresses.setAddresses();
                _tablesConfigured = true;

                Log.Information("[AmdSmu] tables configured, {Commands} commands registered (FireRange gate: {Gate})",
                    SMUCommands.commands.Count, CpuModelDetection.IsFireRange());
            }
        }
    }
}
