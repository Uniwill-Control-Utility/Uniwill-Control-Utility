namespace Uniwill_Control_Utility.Tests.Services;

/// <summary>
/// The ghost-registration self-heal's path rule. The per-user Velopack root
/// drives Velopack's registration-repair /x storm on per-machine installs
/// (the app's UpdateManager /x-es the live registrations; the uninstall
/// sentinel then executes the vanished registration, field-proven), so the
/// root deletes when and only when the app runs from the per-machine layout.
/// A wrong rule silently deletes a portable layout's real Velopack storage
/// or leaves the poison in place for the next launch.
/// </summary>
public sealed class StaleVelopackRootTests
{
    [Fact]
    public void MachineLayout_ReturnsThePerUserRootForDeletion()
    {
        string? root = App.StaleVelopackRootPath(
            @"C:\Program Files\Uniwill-Control-Utility\current\",
            @"C:\Program Files",
            @"C:\Users\someone\AppData\Local");

        Assert.Equal(
            Path.Combine(@"C:\Users\someone\AppData\Local", "Uniwill-Control-Utility"),
            root);
    }

    [Fact]
    public void PortableOrDevLayout_KeepsTheRootUntouched()
    {
        // Dev/portable runs keep their own Velopack storage: the per-user
        // root is not poison when the app does not run per-machine.
        Assert.Null(App.StaleVelopackRootPath(
            @"D:\Tools\Uniwill\portable\",
            @"C:\Program Files",
            @"C:\Users\someone\AppData\Local"));
    }

    [Fact]
    public void ProgramFilesComparison_IsCaseInsensitive()
    {
        string? root = App.StaleVelopackRootPath(
            @"c:\program files\uniwill-control-utility\current\",
            @"C:\Program Files",
            @"C:\Users\someone\AppData\Local");

        Assert.NotNull(root);
    }

    [Fact]
    public void MissingProgramFilesPath_KeepsTheRootUntouched()
    {
        // A shell that cannot resolve ProgramFiles must never delete anything.
        Assert.Null(App.StaleVelopackRootPath(
            @"C:\Program Files\Uniwill-Control-Utility\current\",
            string.Empty,
            @"C:\Users\someone\AppData\Local"));
    }
}
