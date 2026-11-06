using System;
using System.IO;
using System.Linq;

namespace Uniwill_Control_Utility.Tests.Services;

/// <summary>
/// The fan page's "stock preset active" lock must follow the active preset, not
/// the EC's custom-profile bit (0x0727 bit6).
///
/// Field 2026-11-02: with Custom preset 1 applied, relaunching locked the Fan
/// Control page and captioned it "Custom preset applied - fan control
/// customization is only possible with a Custom preset." The lock read the EC
/// bit, but that bit is a GPU-power-control prerequisite: a custom fan table
/// applies with it clear (measured: 0x0727 = 0x00, Full Speed tables -> duty
/// 200/200). Any Custom preset that never activated GPU power control (on
/// battery, or without GPU changes) therefore locked its own fan page.
/// </summary>
public sealed class FanLockOwnershipTests
{
    [Fact]
    public void LockFollowsTheActivePreset_NotTheEcCustomProfileBit()
    {
        string source = ReadAppFile("Services", "DeviceApplier.cs");

        int start = source.IndexOf("public void SyncManualControlLock()", StringComparison.Ordinal);
        Assert.True(start > 0, "SyncManualControlLock not found");

        int end = source.IndexOf('}', source.IndexOf('{', start));
        string body = source.Substring(start, end - start);

        Assert.Contains("PresetProfileApplier.IsBuiltInProfile(", body, StringComparison.Ordinal);
        Assert.DoesNotContain("IsCustomProfileActiveAsync", body, StringComparison.Ordinal);
    }

    [Fact]
    public void LockTransitionsAreLoggedForTheField()
    {
        string source = ReadAppFile("Services", "DeviceApplier.cs");

        Assert.Contains("[Fan] manual control lock engaged", source, StringComparison.Ordinal);
        Assert.Contains("[Fan] manual control lock released", source, StringComparison.Ordinal);
    }

    private static string ReadAppFile(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { RepositoryRoot(), "Uniwill Control Utility" }.Concat(parts).ToArray()));

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "Uniwill Control Utility")) &&
                File.Exists(Path.Combine(directory.FullName, "Uniwill Control Utility.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Repository root not found from " + AppContext.BaseDirectory);
    }
}
