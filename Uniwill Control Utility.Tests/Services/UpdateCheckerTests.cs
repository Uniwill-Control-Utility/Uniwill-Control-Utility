using System;
using Uniwill_Control_Utility.Services;

namespace Uniwill_Control_Utility.Tests.Services;

/// <summary>
/// The About card's release check. There is no updater any more, so what is
/// pinned here is the whole surface: the payload parse, the version comparison,
/// and the fact that the check is a plain HTTPS GET - never Velopack's
/// <c>UpdateManager</c> (its in-process per-machine registration repair could
/// <c>/x</c> the live install; see the runbook).
/// </summary>
public sealed class UpdateCheckerTests
{
    private const string Payload =
        "{\"tag_name\":\"v0.9.8\",\"html_url\":\"https://github.com/Uniwill-Control-Utility/Uniwill-Control-Utility/releases/tag/0.9.8\",\"name\":\"0.9.8\"}";

    [Fact]
    public void Parse_ReadsTheTagAndTheReleasePage()
    {
        UpdateChecker.ReleaseInfo? info = UpdateChecker.Parse(Payload);

        Assert.NotNull(info);
        Assert.Equal(new Version(0, 9, 8), info!.Version);
        Assert.Equal("v0.9.8", info.TagName);
        Assert.Equal("https://github.com/Uniwill-Control-Utility/Uniwill-Control-Utility/releases/tag/0.9.8", info.HtmlUrl);
    }

    [Fact]
    public void Parse_AcceptsAPlainTag_AndFallsBackToTheReleasesPage()
    {
        UpdateChecker.ReleaseInfo? info =
            UpdateChecker.Parse("{\"tag_name\":\"0.10.0\"}");

        Assert.NotNull(info);
        Assert.Equal(new Version(0, 10, 0), info!.Version);
        Assert.Equal(UpdateChecker.ReleasesPageUrl, info.HtmlUrl);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"tag_name\":null}")]
    [InlineData("{\"tag_name\":\"release-candidate\"}")]
    [InlineData("{\"tag_name\":\"\"}")]
    public void Parse_AnswersNull_WhenThePayloadCannotBeTrusted(string json)
    {
        Assert.Null(UpdateChecker.Parse(json));
    }

    [Theory]
    [InlineData("0.9.7", "0.9.8", true)]
    [InlineData("0.9.7.0", "0.9.7", false)]
    [InlineData("0.9.7", "0.9.7", false)]
    [InlineData("0.9.8", "0.9.7", false)]
    [InlineData("0.9.7", "0.10.0", true)]
    [InlineData("0.9.7", "1.0.0", true)]
    [InlineData("0.9.7.41", "0.9.8.0", true)]
    public void IsNewer_ComparesPatchLevel_AndIgnoresTheBuildSuffix(string current, string latest, bool expected)
    {
        Assert.Equal(expected, UpdateChecker.IsNewer(new Version(current), new Version(latest)));
    }

    [Fact]
    public void TheCheck_IsAnHttpsGet_WithNoUpdateManager()
    {
        // Source pin on the one property that matters: this class must never grow
        // an apply path. Reading the release API is harmless; touching Velopack's
        // update machinery is what self-destructed in the field. The doc comment
        // names UpdateManager on purpose - the pins are on the code, not the prose.
        string source = ReadAppFile("Services", "UpdateChecker.cs");

        Assert.Contains("https://api.github.com/repos/Uniwill-Control-Utility/Uniwill-Control-Utility/releases/latest", source, StringComparison.Ordinal);
        Assert.DoesNotContain("using Velopack", source, StringComparison.Ordinal);
        Assert.DoesNotContain("UpdateManager(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ApplyUpdates", source, StringComparison.Ordinal);
    }

    private static string ReadAppFile(params string[] parts) =>
        System.IO.File.ReadAllText(System.IO.Path.Combine(new[] { RepositoryRoot(), "Uniwill Control Utility" }.Concat(parts).ToArray()));

    private static string RepositoryRoot()
    {
        System.IO.DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (System.IO.Directory.Exists(System.IO.Path.Combine(directory.FullName, "Uniwill Control Utility")) &&
                System.IO.File.Exists(System.IO.Path.Combine(directory.FullName, "Uniwill Control Utility.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Repository root not found from " + AppContext.BaseDirectory);
    }
}
