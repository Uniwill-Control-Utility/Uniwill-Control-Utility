using UCU.Contracts;
using UCU.Hardware.Takeover;

namespace UCU.Engine.Tests;

/// <summary>
/// The oem.* verbs end to end: client proxy → pipe → dispatcher (the
/// default telemetry queue: pause/restore sequence against each other, away
/// from the EC queues) → takeover service. The service is the scripted
/// machine (the production one mutates real services/tasks/registry/process
/// scope, which has no place in a test process), so these pins cover the
/// contract: the verbs exist, the DTO round-trips counts AND audit lists,
/// the State name is decided Core-side from the counts, and the machine
/// without the stack auto-bypasses to NotInstalled (the dev-box-proven
/// path). The gate itself is pinned in the Core suite (TakeoverPolicyTests).
/// </summary>
public sealed class OemVerbsTests
{
    private static OemMachineStatus ActiveStack => new()
    {
        ServicesInstalled = 4,
        ServicesRunning = 4,
        OemProcessesRunning = 3,
        JournalCount = 0,
        AutostartNeutralized = 0,
        SquatSuspects = new[] { "service 'GCUBridge': image 'C:\\users\\bad\\GCUBridge.exe' is unsigned or its chain is invalid" },
    };

    private static OemMachineStatus PausedStack => new()
    {
        ServicesInstalled = 4,
        ServicesRunning = 0,
        OemProcessesRunning = 0,
        JournalCount = 9,
        AutostartNeutralized = 6,
        NeutralizedTargets = new[] { @"HKLM\Run\ControlCenter", "task ControlCenterTray", "service GCUBridge (demand)" },
    };

    [Fact]
    public async Task GetStatus_RoundTripsCountsAndAuditLists_AndNamesTheDecidedState()
    {
        FakeOemTakeoverService fake = Scripted();
        await using var host = await EngineHostFixture.StartAsync(configure: o => o with { OemTakeoverOverride = fake });

        OemStatusDto status = await host.Oem.GetStatusAsync();

        Assert.Equal("Active", status.State);
        Assert.Equal(4, status.ServicesInstalled);
        Assert.Equal(4, status.ServicesRunning);
        Assert.Equal(3, status.OemProcessesRunning);
        Assert.Equal(0, status.JournalCount);
        Assert.Single(status.SquatSuspects);
        Assert.Contains("unsigned", status.SquatSuspects[0]);
        Assert.Equal(new[] { "status" }, fake.Calls); // status is the only verb the proxy calls
    }

    [Fact]
    public async Task GetStatus_MachineWithoutTheStack_AutoBypassesToNotInstalled()
    {
        // The dev box: probe-proven no OEM services/tasks/keys. The verbs
        // must answer a clean NotInstalled, not an error.
        await using var host = await EngineHostFixture.StartAsync();

        OemStatusDto status = await host.Oem.GetStatusAsync();

        Assert.Equal("NotInstalled", status.State);
        Assert.Equal(0, status.ServicesInstalled);
        Assert.Empty(status.SquatSuspects);
    }

    [Fact]
    public async Task Pause_JournaledMachine_AnswersThePausedStateAndTheAuditLines()
    {
        FakeOemTakeoverService fake = Scripted();
        fake.PauseResult = new OemActionResult
        {
            StatusAfter = PausedStack,
            MutationsApplied = 9,
            Actions = new[]
            {
                "killed ControlCenter (pid 4242)",
                "stopped service GCUBridge",
                "parked service GCUBridge at demand-start (was 2)",
                @"neutralized run key HKLM\Run\ControlCenter",
            },
        };

        await using var host = await EngineHostFixture.StartAsync(configure: o => o with { OemTakeoverOverride = fake });

        OemActionDto result = await host.Oem.PauseAsync();

        Assert.Equal("Paused", result.State);
        Assert.Equal(9, result.MutationsApplied);
        Assert.Equal(4, result.Actions.Count);
        Assert.Contains(result.Actions, a => a.Contains("parked service GCUBridge"));
        Assert.Equal(new[] { "pause" }, fake.Calls);
    }

    [Fact]
    public async Task Resume_DrainedJournal_AnswersTheActiveState()
    {
        FakeOemTakeoverService fake = Scripted();
        fake.ResumeResult = new OemActionResult
        {
            StatusAfter = ActiveStack,
            MutationsApplied = 6,
            Actions = new[]
            {
                @"restored run key HKLM\Run\ControlCenter",
                "task ControlCenterTray: Enabled=True",
                "re-armed service GCUBridge (Start=2)",
                "started service GCUBridge",
            },
        };

        await using var host = await EngineHostFixture.StartAsync(configure: o => o with { OemTakeoverOverride = fake });

        OemActionDto result = await host.Oem.ResumeAsync();

        Assert.Equal("Active", result.State);
        Assert.Equal(6, result.MutationsApplied);
        Assert.Contains(result.Actions, a => a.Contains("re-armed service GCUBridge"));
        Assert.Equal(new[] { "resume" }, fake.Calls);
    }

    [Fact]
    public async Task OemVerbs_RunOnTheDefaultQueue_NotInline()
    {
        // Pause and restore must sequence against each other (double-click
        // on the UI buttons is the field case): the default queue does that;
        // inline would race a pause against a resume.
        await using var queues = new EngineDomainQueues(new EngineRuntimeOptions());

        SerialExecutor? pause = queues.QueueFor(EngineVerbs.Oem.Pause);
        SerialExecutor? resume = queues.QueueFor(EngineVerbs.Oem.Resume);

        Assert.NotNull(pause);
        Assert.Same(pause, resume);
    }

    private static FakeOemTakeoverService Scripted() => new() { Status = ActiveStack };
}
