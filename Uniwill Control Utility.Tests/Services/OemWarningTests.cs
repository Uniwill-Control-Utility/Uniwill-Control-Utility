using Uniwill_Control_Utility.Services;

namespace Uniwill_Control_Utility.Tests.Services;

/// <summary>
/// The title-bar OEM warning chip's decision (U-203 visibility follow-up).
/// The chip exists because the takeover is otherwise discoverable only inside
/// Settings, and a live OEM Control Center silently fights UCU's fan/power
/// writes. The rule is deliberately narrow: warn only while the conflict is
/// happening (Active / PartiallyPaused on a machine that has the stack), stay
/// silent for the states this feature creates on purpose (Paused/Stale) and
/// for machines without the stack, and never claim a conflict it cannot see
/// (engine unreachable).
/// </summary>
public sealed class OemWarningTests
{
    private static OemOverview Overview(
        string state, bool engineReachable = true, int servicesInstalled = 1) => new()
        {
            State = state,
            EngineReachable = engineReachable,
            ServicesInstalled = servicesInstalled,
            ServicesRunning = servicesInstalled,
        };

    [Theory]
    [InlineData("Active")]
    [InlineData("PartiallyPaused")]
    public void Warns_WhileTheStackIsLive(string state)
    {
        Assert.True(OemTakeoverService.ShouldWarn(Overview(state)));
    }

    [Theory]
    [InlineData("NotInstalled")]
    [InlineData("Paused")]
    [InlineData("Stale")]
    [InlineData("Busy")]
    public void Silent_ForStatesThisFeatureCreatesOrIrrelevantOnes(string state)
    {
        Assert.False(OemTakeoverService.ShouldWarn(Overview(state)));
    }

    [Fact]
    public void Silent_WhenTheEngineIsUnreachable()
    {
        // The machine scope is blind: the stack may or may not be live, and a
        // warning the user cannot act on from here is noise. The Settings card
        // still shows "Engine offline" for the diagnosis.
        Assert.False(OemTakeoverService.ShouldWarn(Overview("Active", engineReachable: false)));
    }

    [Fact]
    public void Silent_WhenTheMachineHasNoCatalogServices()
    {
        // Active with zero services is the user-scope-only picture (a bare
        // HKCU autostart entry); the chip names the OEM CONTROL CENTER stack,
        // and a service-less machine is the reference's Thin & Light case.
        Assert.False(OemTakeoverService.ShouldWarn(Overview("Active", servicesInstalled: 0)));
    }

    [Fact]
    public void Silent_ForNull()
    {
        Assert.False(OemTakeoverService.ShouldWarn(null!));
    }

    [Fact]
    public void Failures_PicksRefusalsAndFailures_InOrder_Capped()
    {
        var report = new OemMergeReport
        {
            Actions = new[]
            {
                "re-armed service GCUBridge (Start=3)",
                "started service GCUBridge",
                "service GCUService: start failed (Win32 1068)",
                "task ControlCenter: disable refused",
                "service GCUBridge: still not running after restore (state 1)",
            },
        };

        IReadOnlyList<string> failures = OemTakeoverService.Failures(report);

        Assert.Equal(2, failures.Count);
        Assert.Equal("service GCUService: start failed (Win32 1068)", failures[0]);
        Assert.Equal("task ControlCenter: disable refused", failures[1]);
    }

    [Fact]
    public void Failures_EmptyWhenEveryActionLanded()
    {
        var report = new OemMergeReport
        {
            Actions = new[] { "re-armed service GCUBridge (Start=2)", "started service GCUBridge" },
        };

        Assert.Empty(OemTakeoverService.Failures(report));
        Assert.Empty(OemTakeoverService.Failures(null!));
    }
}
