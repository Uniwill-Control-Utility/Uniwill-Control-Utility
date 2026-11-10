using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace Uniwill_Control_Utility.Services
{
    /// <summary>
    /// Read-only release check against GitHub, for the About page's Updates card.
    ///
    /// There is no in-app updater: an installed copy updates by running the
    /// release's <c>Uniwill-Control-Utility-win.msi</c>, and this class exists only
    /// to answer "is there a newer release?". It performs one HTTPS GET, parses the
    /// tag and touches nothing else - no <c>UpdateManager</c>, no registration
    /// read, no apply. That is deliberate: Velopack's in-process per-machine
    /// registration repair could run <c>msiexec /x</c> on the live product code
    /// (field-proven from both registry-view directions; see the runbook), which
    /// is why the old self-update flow was deleted rather than repaired.
    /// </summary>
    internal static class UpdateChecker
    {
        /// <summary>Where a human downloads the installer.</summary>
        internal const string ReleasesPageUrl =
            "https://github.com/Uniwill-Control-Utility/Uniwill-Control-Utility/releases/latest";

        private const string LatestReleaseApiUrl =
            "https://api.github.com/repos/Uniwill-Control-Utility/Uniwill-Control-Utility/releases/latest";

        private static readonly HttpClient Http = CreateClient();

        internal sealed record ReleaseInfo(Version Version, string TagName, string HtmlUrl);

        /// <summary>
        /// The latest published release, or null when the check could not be
        /// answered (offline, rate-limited, unexpected payload). A null is an
        /// answer the card states honestly, never a silent "up to date".
        /// </summary>
        public static async Task<ReleaseInfo?> GetLatestAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                using HttpResponseMessage response =
                    await Http.GetAsync(LatestReleaseApiUrl, cancellationToken).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    Log.Information("[Update] release check answered {Status}", (int)response.StatusCode);
                    return null;
                }

                string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                return Parse(json);
            }
            catch (Exception ex)
            {
                Log.Information("[Update] release check failed: {Type} {Message}", ex.GetType().Name, ex.Message);
                return null;
            }
        }

        /// <summary>Parses a GitHub release payload: <c>tag_name</c> plus <c>html_url</c>.</summary>
        internal static ReleaseInfo? Parse(string json)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(json);
                JsonElement root = document.RootElement;

                if (!root.TryGetProperty("tag_name", out JsonElement tagElement))
                    return null;

                string? tag = tagElement.GetString();
                if (string.IsNullOrWhiteSpace(tag))
                    return null;

                string versionText = tag.Trim().TrimStart('v', 'V');
                if (!Version.TryParse(versionText, out Version? version))
                    return null;

                string htmlUrl = root.TryGetProperty("html_url", out JsonElement urlElement)
                    ? urlElement.GetString() ?? ReleasesPageUrl
                    : ReleasesPageUrl;

                return new ReleaseInfo(Normalize(version), tag, htmlUrl);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>Patch-level comparison; the build/revision suffixes carry no release meaning.</summary>
        internal static bool IsNewer(Version current, Version latest) =>
            Normalize(latest).CompareTo(Normalize(current)) > 0;

        private static Version Normalize(Version version) =>
            new(version.Major, version.Minor, Math.Max(version.Build, 0));

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("UCU-UpdateCheck");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return client;
        }
    }
}
