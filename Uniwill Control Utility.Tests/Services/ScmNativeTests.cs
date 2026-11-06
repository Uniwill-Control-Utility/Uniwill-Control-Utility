using System.ComponentModel;
using Uniwill_Control_Utility.Services;

namespace Uniwill_Control_Utility.Tests.Services;

/// <summary>
/// The app's SCM access, narrowed to the rights the engine service's DACL grants
/// Authenticated Users. These tests cannot install services, so they pin the two
/// paths that need no fixtures: the not-installed diagnosis (the classification the
/// lifecycle repairs against) and the mapping of a healthy SCM query's state
/// constant. The real "can the unelevated app see Running" proof is the install
/// acceptance checklist, because it needs the installed descriptor.
/// </summary>
public sealed class ScmNativeTests
{
    [Fact]
    public void MissingService_IsDiagnosedAsDoesNotExist()
    {
        string name = "UCU-Test-" + Guid.NewGuid().ToString("N");

        Win32Exception ex = Assert.Throws<Win32Exception>(() => ScmNative.QueryState(name));

        // 1060: the state that means "register it" (one elevation), not the
        // access-denied state that means "the DACL needs re-asserting" (also one
        // elevation, but the log lines and the field reports separate cleanly when
        // the codes are honest).
        Assert.Equal(EngineAvailability.ErrorServiceDoesNotExist, ex.NativeErrorCode);
        Assert.Equal(EngineServiceState.NotInstalled, EngineAvailability.ClassifyScmFailure(ex));
    }

    [Fact]
    public void RunningConstantMatchesWin32()
    {
        // SERVICE_STATUS_PROCESS.currentState values are fixed by the protocol; the
        // gate compares against ScmNative.ServiceRunning, so the pairing with
        // EngineServiceState.Running must not drift.
        Assert.Equal(4, ScmNative.ServiceRunning);
        Assert.Equal(1, ScmNative.ServiceStopped);
    }

    [Fact]
    public void OnlyGrantedRightsAreRequested()
    {
        // The design constraint: the app's SCM opens request exactly the rights the
        // install's AU ace carries (SW query-status, RP start) and nothing that the
        // descriptor withholds - interrogate (LC/0x80) and enumerate-dependents are
        // what made .NET ServiceController unusable against a healthy install.
        Assert.Equal(0x0004u, ScmNative.ServiceQueryStatus);
        Assert.Equal(0x0020u, ScmNative.ServiceStart);
    }
}
