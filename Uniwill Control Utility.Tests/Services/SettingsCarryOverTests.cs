using System.Text;
using Uniwill_Control_Utility.Services;

namespace Uniwill_Control_Utility.Tests.Services;

/// <summary>
/// The update carry-over (0.9.11-0.9.13 regression): Upgrade() copies from the nearest
/// lower version directory, so a stale one shadows the config that held the user's
/// choice. These pin the replacement rule: fill only settings the current config does
/// not carry at all, newest older config first, and never overwrite what is there.
/// </summary>
public sealed class SettingsCarryOverTests
{
    [Fact]
    public void Plan_AdoptsFromTheNewestOlderFileThatCarriesTheSetting()
    {
        string dir = NewTempDir();
        try
        {
            // Newest older config does not carry the setting at all.
            var history = new List<(Version, string)>
            {
                (new Version(0, 9, 12, 0), WriteConfig(dir, "0.9.12.0", new()
                {
                    ["StartOnBoot"] = "True",
                })),
                (new Version(0, 9, 10, 0), WriteConfig(dir, "0.9.10.0", new()
                {
                    ["EnablePowerBoostMode"] = "True",
                })),
                (new Version(0, 9, 9, 0), WriteConfig(dir, "0.9.9.0", new()
                {
                    ["EnablePowerBoostMode"] = "False",
                })),
            };

            var adopted = SettingsCarryOver.Plan(history, Array.Empty<string>(),
                new[] { "EnablePowerBoostMode", "StartOnBoot" });

            Assert.Collection(adopted,
                first =>
                {
                    Assert.Equal("StartOnBoot", first.Setting);
                    Assert.Equal("0.9.12.0", first.Version);
                },
                second =>
                {
                    Assert.Equal("EnablePowerBoostMode", second.Setting);
                    Assert.Equal("True", second.Value);
                    Assert.Equal("0.9.10.0", second.Version);
                });
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Plan_NeverOverwritesAValueTheCurrentConfigCarries()
    {
        string dir = NewTempDir();
        try
        {
            string current = WriteConfig(dir, "0.9.13.0", new()
            {
                ["EnablePowerBoostMode"] = "False", // chosen in this version: must win
            });
            var history = new List<(Version, string)>
            {
                (new Version(0, 9, 9, 0), WriteConfig(dir, "0.9.9.0", new()
                {
                    ["EnablePowerBoostMode"] = "True",
                    ["StartOnBoot"] = "True",
                })),
            };

            var present = SettingsCarryOver.Read(current);
            Assert.NotNull(present);

            var adopted = SettingsCarryOver.Plan(history, present!.Keys,
                new[] { "EnablePowerBoostMode", "StartOnBoot" });

            Assert.Equal("StartOnBoot", Assert.Single(adopted).Setting);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Plan_SkipsAMalformedFile_AndTriesTheNext()
    {
        string dir = NewTempDir();
        try
        {
            string broken = Path.Combine(dir, "0.9.12.0");
            Directory.CreateDirectory(broken);
            File.WriteAllText(Path.Combine(broken, "user.config"), "<configuration><userSettings>");

            var history = new List<(Version, string)>
            {
                (new Version(0, 9, 12, 0), Path.Combine(broken, "user.config")),
                (new Version(0, 9, 9, 0), WriteConfig(dir, "0.9.9.0", new()
                {
                    ["EnablePowerBoostMode"] = "True",
                })),
            };

            var skipped = new List<string>();
            var adopted = SettingsCarryOver.Plan(history, Array.Empty<string>(),
                new[] { "EnablePowerBoostMode" }, skipped.Add);

            var carried = Assert.Single(adopted);
            Assert.Equal("True", carried.Value);
            Assert.Equal("0.9.9.0", carried.Version);
            Assert.Contains("skipping unreadable", Assert.Single(skipped), StringComparison.Ordinal);
            Assert.Null(SettingsCarryOver.Read(Path.Combine(broken, "user.config")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Plan_WithNoHistory_AdoptsNothingAndSaysSo()
    {
        var adopted = SettingsCarryOver.Plan(
            Array.Empty<(Version, string)>(),
            Array.Empty<string>(),
            new[] { "EnablePowerBoostMode" });

        Assert.Empty(adopted);
        Assert.Contains("nothing to adopt", SettingsCarryOver.Summary(0, 0), StringComparison.Ordinal);
        Assert.Contains("1 setting(s) adopted", SettingsCarryOver.Summary(1, 3), StringComparison.Ordinal);
        Assert.Empty(SettingsCarryOver.History(Path.Combine(NewTempDir(), "missing", "user.config")));
    }

    [Fact]
    public void History_ReturnsOlderVersionsNewestFirst_ExcludingCurrentAndLater()
    {
        string root = NewTempDir();
        try
        {
            string company = Path.Combine(root, "jgudec");
            string identity = Path.Combine(company, "Uniwill_Control_Utility_Url_abc123");
            foreach (string version in new[] { "0.9.9.0", "0.9.10.0", "0.9.13.0", "0.9.24.0" })
            {
                string dir = Path.Combine(identity, version);
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "user.config"), Config(new() { ["StartOnBoot"] = "True" }));
            }

            // A different application under the same company must not be walked.
            string other = Path.Combine(company, "Other_App_Url_def456", "0.9.9.0");
            Directory.CreateDirectory(other);
            File.WriteAllText(Path.Combine(other, "user.config"), Config(new() { ["StartOnBoot"] = "True" }));

            var history = SettingsCarryOver.History(
                Path.Combine(identity, "0.9.13.0", "user.config"));

            Assert.Equal(
                new[] { new Version(0, 9, 10, 0), new Version(0, 9, 9, 0) },
                history.Select(candidate => candidate.Version));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ucu-tests", $"carryover_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string WriteConfig(string root, string version, Dictionary<string, string> settings)
    {
        string dir = Path.Combine(root, version);
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "user.config");
        File.WriteAllText(path, Config(settings));
        return path;
    }

    private static string Config(Dictionary<string, string> settings)
    {
        var xml = new StringBuilder();
        xml.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        xml.AppendLine("<configuration>");
        xml.AppendLine("  <userSettings>");
        xml.AppendLine("    <Uniwill_Control_Utility.Properties.Settings>");
        foreach (var pair in settings)
        {
            xml.AppendLine($"      <setting name=\"{pair.Key}\" serializeAs=\"String\">");
            xml.AppendLine($"        <value>{pair.Value}</value>");
            xml.AppendLine("      </setting>");
        }
        xml.AppendLine("    </Uniwill_Control_Utility.Properties.Settings>");
        xml.AppendLine("  </userSettings>");
        xml.AppendLine("</configuration>");
        return xml.ToString();
    }
}
