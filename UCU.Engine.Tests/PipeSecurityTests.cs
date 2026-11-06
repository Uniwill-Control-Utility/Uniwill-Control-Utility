namespace UCU.Engine.Tests;

/// <summary>
/// The production pipe-security path. The RPC suite runs with
/// RestrictPipeAccess=false, which skipped the DACL code entirely - the first field
/// run then exposed it: SetAccessControl on a handle without WRITE_DAC throws
/// UnauthorizedAccessException, the accept loop spun failing every second, and
/// clients hung against a pipe the server never served. The fix bakes the SDDL into
/// the pipe-creation call; this is the regression guard that only the production
/// constructor path satisfies it.
/// </summary>
public sealed class PipeSecurityTests
{
    [Fact]
    public async Task RestrictedPipe_ServesTheInteractiveUser()
    {
        // The real service DACL (SY/BA full, IU read+write, no inheritance). The test
        // process runs as the interactive user, so a round-trip proves the create-time
        // security descriptor both applies and admits the app's account class.
        await using var host = await EngineHostFixture.StartAsync(
            configure: o => o with { RestrictPipeAccess = true });

        Assert.True(await host.Engine.PingAsync());
    }
}
