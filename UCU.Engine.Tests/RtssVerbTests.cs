using UCU.Core.Rtss;

namespace UCU.Engine.Tests;

/// <summary>
/// The rtss.start verb end to end: client proxy → pipe → dispatcher (inline:
/// starting a user-session process has nothing to order against on the EC
/// queues) → locator → session launcher. The launcher is the hermetic recorder
/// (the production route starts a real process in the active user's session),
/// so the test pins the contract that matters: the verb exists, answers the
/// observed launch, and hands the launcher the located RTSS image — never a
/// sibling, which on a real install is the uninstaller the uninstall entry's
/// icon names.
/// </summary>
public sealed class RtssVerbTests
{
    [Fact]
    public async Task Start_LaunchesTheLocatedRoute_AndAnswersTheObservedLaunch()
    {
        await using var host = await EngineHostFixture.StartAsync();

        string? located = RtssLocation.Locate();

        bool started = await host.Rtss.StartAsync();

        if (located is null)
        {
            // No RTSS on this machine: the verb answers false and asks for nothing.
            Assert.False(started);
            Assert.Empty(host.RtssLaunches);
            return;
        }

        Assert.True(started);

        string image = Assert.Single(host.RtssLaunches);

        Assert.Equal(located, image);
        Assert.Equal(RtssLocation.ExeName, Path.GetFileName(image));
    }
}
