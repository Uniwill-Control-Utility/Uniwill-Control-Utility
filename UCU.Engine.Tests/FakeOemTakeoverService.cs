using UCU.Hardware.Takeover;

namespace UCU.Engine.Tests;

/// <summary>
/// Scripted OEM machine for the takeover verb tests (story U-203). The
/// production service stops REAL services, disables REAL tasks, rewrites
/// REAL HKLM run keys and kills REAL processes - none of which has a place
/// in a test process. This fake answers scripted status/action records and
/// records which verbs the pipe actually invoked.
/// </summary>
public sealed class FakeOemTakeoverService : IOemTakeoverService
{
    public OemMachineStatus Status { get; set; } = new()
    {
        ServicesInstalled = 0,
        ServicesRunning = 0,
        OemProcessesRunning = 0,
        JournalCount = 0,
        AutostartNeutralized = 0,
    };

    public OemActionResult PauseResult { get; set; } = new() { MutationsApplied = 0 };

    public OemActionResult ResumeResult { get; set; } = new() { MutationsApplied = 0 };

    /// <summary>Verb calls in order ("status", "pause", "resume").</summary>
    public List<string> Calls { get; } = new();

    public OemMachineStatus GetStatus()
    {
        Calls.Add("status");
        return Status;
    }

    public OemActionResult Pause()
    {
        Calls.Add("pause");
        return PauseResult;
    }

    public OemActionResult Resume()
    {
        Calls.Add("resume");
        return ResumeResult;
    }
}
