using UCU.Core.Supervision;

namespace UCU.Core.Tests.Supervision;

/// <summary>
/// The autostart reconcile rule. The two failure directions are both field-shaped: an
/// installed user whose task is missing (uninstall removed it, the settings file survived)
/// gets no logon entry while the Settings row reads ON, and a toggle switched off must not
/// leave a task pointing at an exe that may no longer exist.
/// </summary>
public sealed class AutostartPolicyTests
{
    private const string CurrentExe = @"C:\Program Files\Uniwill-Control-Utility\current\Uniwill Control Utility.exe";

    [Fact]
    public void EnabledButTheTaskIsGone_Registers()
    {
        // The reinstall case: uninstall deleted "UCU Autostart", StartOnBoot is still true.
        Assert.Equal(
            AutostartReconcileAction.Register,
            AutostartPolicy.Decide(enabled: true, registeredActionPath: null, expectedActionPath: CurrentExe));
    }

    [Fact]
    public void EnabledAndTheTaskPointsHere_LeavesItAlone()
    {
        Assert.Equal(
            AutostartReconcileAction.None,
            AutostartPolicy.Decide(enabled: true, registeredActionPath: CurrentExe, expectedActionPath: CurrentExe));
    }

    [Theory]
    [InlineData(@"C:\Program Files\Uniwill-Control-Utility\current\UNIWILL CONTROL UTILITY.EXE", AutostartReconcileAction.None)]  // case only
    [InlineData(@"D:\Other\Uniwill Control Utility.exe", AutostartReconcileAction.Register)]                                        // moved install
    [InlineData("", AutostartReconcileAction.Register)]                                                                             // empty action
    public void Enabled_ComparesTheActionPathCaseInsensitively(string registered, AutostartReconcileAction expected)
    {
        Assert.Equal(
            expected,
            AutostartPolicy.Decide(enabled: true, registeredActionPath: registered, expectedActionPath: CurrentExe));
    }

    [Fact]
    public void DisabledWithoutATask_DoesNothing()
    {
        Assert.Equal(
            AutostartReconcileAction.None,
            AutostartPolicy.Decide(enabled: false, registeredActionPath: null, expectedActionPath: CurrentExe));
    }

    [Theory]
    [InlineData(CurrentExe)]
    [InlineData(@"D:\Other\Uniwill Control Utility.exe")] // the name is ours, whatever it points at
    public void DisabledWithATask_RemovesIt(string registered)
    {
        Assert.Equal(
            AutostartReconcileAction.Unregister,
            AutostartPolicy.Decide(enabled: false, registeredActionPath: registered, expectedActionPath: CurrentExe));
    }
}
