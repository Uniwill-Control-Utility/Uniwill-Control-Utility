using System;
using System.IO;
using System.Linq;

namespace Uniwill_Control_Utility.Tests.Services;

/// <summary>
/// The power-edge re-apply (which makes the battery envelope clamp take effect
/// when the charger is unplugged) must yield only to an automation that will
/// actually apply something on THAT edge.
///
/// The first version yielded to the automation master switch
/// (EnableAutoNavigation) alone. With the automation enabled but the edge's
/// preset set to "None" - the common case - nothing applied, and the saved
/// Custom preset kept its AC power numbers after unplugging: the gap the hook
/// exists to close.
/// </summary>
public sealed class PowerEdgeReapplyTests
{
    [Fact]
    public void ReapplyYieldsToTheEdgesOwnAutomation_NotTheMasterSwitch()
    {
        string source = ReadAppFile("Services", "WatcherHubService.cs");

        int start = source.IndexOf("private static async Task ReapplyPresetForPowerEdgeAsync", StringComparison.Ordinal);
        Assert.True(start > 0, "ReapplyPresetForPowerEdgeAsync not found");
        int end = source.IndexOf("private void OnSessionSwitch", start, StringComparison.Ordinal);
        string body = source.Substring(start, end - start);

        // The gate reads the edge's own preset and treats "None" as "no automation".
        Assert.Contains("dcPreset", body, StringComparison.Ordinal);
        Assert.Contains("acPreset", body, StringComparison.Ordinal);
        Assert.Contains("\"None\"", body, StringComparison.Ordinal);

        // And it must not be a bare master-switch early return.
        Assert.DoesNotContain(
            "if (Properties.Settings.Default.EnableAutoNavigation)\n                    return;",
            body.Replace("\r\n", "\n"),
            StringComparison.Ordinal);
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
