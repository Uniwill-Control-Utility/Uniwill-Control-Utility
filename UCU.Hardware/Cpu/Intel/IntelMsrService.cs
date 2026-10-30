using System.Threading;

namespace UCU.Hardware.Cpu.Intel
{
    /// <summary>
    /// The seam the Intel MSR verbs run on: the production implementation
    /// drives the ported MSR stack (IntelPawnIO transport) as LocalSystem;
    /// the RPC tests inject a scripted register file, because no test may
    /// write a real CPU's power MSRs.
    /// </summary>
    public interface IIntelMsrService
    {
        /// <summary>Write the TDP MSR (0x610) with the PL1/PL2 pair. True when
        /// the write landed (the methods keep the ported stack's catch-and-log
        /// semantics: the engine log carries the reason for a false).</summary>
        bool SetTdp(int pl1, int pl2, CancellationToken cancellationToken = default);

        /// <summary>Power balance (0x63A cpu / 0x642 gpu); plane 0/1, value 0..31
        /// (out of range is the ported stack's silent no-op, answered true).</summary>
        bool SetPowerBalance(int value, int cpuOrGpu, CancellationToken cancellationToken = default);

        /// <summary>Voltage offset (0x150); plane 0 cpu / 1 gpu / 2 cache / 3 sa.</summary>
        bool SetVoltageOffset(int value, int plane, CancellationToken cancellationToken = default);

        /// <summary>Clock ratio offsets (0x1AD), up to 8 packed bytes.</summary>
        bool SetClockRatios(int[] clockRatios, CancellationToken cancellationToken = default);

        /// <summary>The 8 clock ratio bytes read back from 0x1AD; empty when the
        /// read failed (the ported stack's answer shape).</summary>
        int[] GetClockRatios(CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Intel MSR access in the engine. The MSR registers ride the same
    /// PawnIO device the SMU mailboxes do, whose ACL admits System and
    /// Administrators — an unelevated app's opens are access-denied, so every
    /// Intel TDP / voltage / ratio preset apply on a field machine was
    /// silently dead. The engine runs LocalSystem: the same IntelMSR.bin
    /// module loads there, and each verb answers "the write landed" — the
    /// honest signal the app route never had (its methods swallowed the
    /// access-denied into a log line).
    ///
    /// The stack is a verbatim port of the app's IntelManagement (bit masks,
    /// plane commands, encodings re-homed, not rewritten); the app keeps its
    /// copy as the fallback, as everywhere in this architecture. The
    /// driver-blocklist write was NOT ported: a LocalSystem service has no
    /// business weakening the CI driver blocklist, and the app-side write is
    /// an approved dead-code removal.
    /// </summary>
    public sealed class IntelMsrService : IIntelMsrService
    {
        public bool SetTdp(int pl1, int pl2, CancellationToken cancellationToken = default) =>
            IntelMsrStack.SetTdp(pl1, pl2);

        public bool SetPowerBalance(int value, int cpuOrGpu, CancellationToken cancellationToken = default) =>
            IntelMsrStack.SetPowerBalance(value, cpuOrGpu);

        public bool SetVoltageOffset(int value, int plane, CancellationToken cancellationToken = default) =>
            IntelMsrStack.SetVoltageOffset(value, plane);

        public bool SetClockRatios(int[] clockRatios, CancellationToken cancellationToken = default) =>
            IntelMsrStack.SetClockRatios(clockRatios);

        public int[] GetClockRatios(CancellationToken cancellationToken = default) =>
            IntelMsrStack.GetClockRatios();
    }
}
