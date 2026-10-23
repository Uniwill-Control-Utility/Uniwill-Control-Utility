using Uniwill_Control_Utility.Services;

namespace Uniwill_Control_Utility.Tests.Services;

public sealed class UpdatePolicyTests
{
    [Fact]
    public void PortableLayout_NeverChecksOrDownloads()
    {
        // Velopack throws NotInstalledException on portable (zip) layouts —
        // the policy must gate every network path on IsInstalled.
        Assert.False(UpdatePolicy.ShouldAutoCheck(isInstalled: false, autoCheck: true, internetAvailable: true));
        Assert.False(UpdatePolicy.ShouldSilentDownload(isInstalled: false, autoCheck: true, autoApply: true, UpdateState.UpdateAvailable));
    }

    [Fact]
    public void AutoCheck_RequiresToggleAndInternet()
    {
        Assert.True(UpdatePolicy.ShouldAutoCheck(isInstalled: true, autoCheck: true, internetAvailable: true));
        Assert.False(UpdatePolicy.ShouldAutoCheck(isInstalled: true, autoCheck: false, internetAvailable: true));
        Assert.False(UpdatePolicy.ShouldAutoCheck(isInstalled: true, autoCheck: true, internetAvailable: false));
    }

    [Fact]
    public void SilentDownload_RequiresAutoApplyToggleAndAvailableState()
    {
        Assert.True(UpdatePolicy.ShouldSilentDownload(isInstalled: true, autoCheck: true, autoApply: true, UpdateState.UpdateAvailable));

        // Check-only mode: notify, never download.
        Assert.False(UpdatePolicy.ShouldSilentDownload(isInstalled: true, autoCheck: true, autoApply: false, UpdateState.UpdateAvailable));

        // Only a freshly discovered update is downloaded — never while a
        // download is running or a package is already staged.
        Assert.False(UpdatePolicy.ShouldSilentDownload(isInstalled: true, autoCheck: true, autoApply: true, UpdateState.Downloading));
        Assert.False(UpdatePolicy.ShouldSilentDownload(isInstalled: true, autoCheck: true, autoApply: true, UpdateState.PendingApply));
        Assert.False(UpdatePolicy.ShouldSilentDownload(isInstalled: true, autoCheck: true, autoApply: true, UpdateState.UpToDate));
    }

    [Fact]
    public void TitleBarCard_ShownForAvailableDownloadingAndPending()
    {
        Assert.True(UpdatePolicy.ShouldShowTitleBarCard(UpdateState.UpdateAvailable));
        Assert.True(UpdatePolicy.ShouldShowTitleBarCard(UpdateState.Downloading));
        Assert.True(UpdatePolicy.ShouldShowTitleBarCard(UpdateState.PendingApply));

        Assert.False(UpdatePolicy.ShouldShowTitleBarCard(UpdateState.Idle));
        Assert.False(UpdatePolicy.ShouldShowTitleBarCard(UpdateState.Checking));
        Assert.False(UpdatePolicy.ShouldShowTitleBarCard(UpdateState.UpToDate));
        Assert.False(UpdatePolicy.ShouldShowTitleBarCard(UpdateState.Failed));
        Assert.False(UpdatePolicy.ShouldShowTitleBarCard(UpdateState.NotInstalled));
        Assert.False(UpdatePolicy.ShouldShowTitleBarCard(UpdateState.Applying));
    }
}
