using UCU.Hardware.Devices;

namespace UCU.Engine.Tests;

/// <summary>
/// The scripted MiniLED handshake the dimming verb runs against. The
/// production service executes the OEM ACPI WMI method that switches a real
/// panel's local dimming, which no test may touch. The fake mirrors the
/// production semantics: the pair of commands must arrive in order, each
/// answered with the firmware's expected reply byte (scriptable to a
/// mismatch), and the verb's answer is the verification, not the call's
/// success.
/// </summary>
public sealed class FakeMiniLedService : IMiniLedService
{
    public List<ulong> Commands { get; } = new();

    /// <summary>Reply bytes the fake answers for step 1 / step 2. Defaults to
    /// the firmware's expected vocabulary; set a mismatch to pin the
    /// verification.</summary>
    public byte Step1Reply { get; set; } = MiniLedWmiService.ExpectedEnableStep1;

    public byte Step2Reply { get; set; } = MiniLedWmiService.ExpectedEnableStep2;

    /// <summary>Throw shape for a WMI fault (the access-denied class the
    /// unelevated app hit); null = calls succeed.</summary>
    public Exception? Fault { get; set; }

    public bool SetLocalDimming(bool enabled)
    {
        // The production service CATCHES WMI faults (the access-denied shape
        // the unelevated app hit) and answers false with the reason logged, so
        // the verb carries the failure as an answer, never a wire fault. The
        // scripted Fault reproduces that answer shape.
        if (Fault != null)
            return false;

        ulong step1 = enabled ? MiniLedWmiService.CmdEnableStep1 : MiniLedWmiService.CmdDisableStep1;
        ulong step2 = enabled ? MiniLedWmiService.CmdEnableStep2 : MiniLedWmiService.CmdDisableStep2;
        byte expected1 = enabled ? MiniLedWmiService.ExpectedEnableStep1 : MiniLedWmiService.ExpectedDisableStep1;
        byte expected2 = enabled ? MiniLedWmiService.ExpectedEnableStep2 : MiniLedWmiService.ExpectedDisableStep2;

        Commands.Add(step1);
        if (Step1Reply != expected1)
            return false;

        Commands.Add(step2);
        return Step2Reply == expected2;
    }
}
