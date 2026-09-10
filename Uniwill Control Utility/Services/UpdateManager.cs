using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Octokit;
using ProductHeaderValue = Octokit.ProductHeaderValue;

public class UpdateManager
{
    private const int LatestReleaseIndex = 0;
    private const int PrimaryAssetIndex = 0;

    private readonly string _owner;
    private readonly string _repo;
    private readonly string _currentVersion;
    private readonly string _downloadPath;
    public string NewVersion { get; set; } = string.Empty;

    public UpdateManager(string owner, string repo, string currentVersion, string downloadPath)
    {
        _owner = owner;
        _repo = repo;
        _currentVersion = currentVersion;
        _downloadPath = downloadPath;
    }

    public async Task<bool> IsUpdateAvailable()
    {
        try
        {
            var client = new GitHubClient(new ProductHeaderValue(_repo));
            var releases = await client.Repository.Release.GetAll(_owner, _repo);

            if (releases.Count == 0)
            {
                return false;
            }

            var latestRelease = releases[LatestReleaseIndex];
            var latestVersion = new Version(latestRelease.TagName);
            NewVersion = latestVersion.ToString();
            var currentVersion = new Version(_currentVersion);

            return latestVersion.CompareTo(currentVersion) > 0;
        }
        catch (IOException)
        {
            // Network I/O failure when fetching releases
            return false;
        }
        catch (ArgumentOutOfRangeException)
        {
            // No releases available
            return false;
        }
        catch (ArgumentException)
        {
            // Invalid version format in tag name
            return false;
        }
        catch (Exception ex)
        {
            // Unexpected error during update check; log and fail gracefully.
            _ = ex;
            return false;
        }
    }

    public async Task<bool> DownloadAndInstallUpdate()
    {
        try
        {
            var client = new GitHubClient(new ProductHeaderValue(_repo));
            var releases = await client.Repository.Release.GetAll(_owner, _repo);

            if (releases.Count == 0)
            {
                return false;
            }

            var latestRelease = releases[LatestReleaseIndex];
            var latestVersion = latestRelease.TagName;

            if (latestVersion != _currentVersion)
            {
                var assets = latestRelease.Assets;
                if (assets.Count == 0)
                {
                    return false;
                }

                var downloadUrl = assets[PrimaryAssetIndex].BrowserDownloadUrl;
                if (downloadUrl == null)
                {
                    return false;
                }

                var fileName = Path.GetFileName(downloadUrl);
                var filePath = Path.Combine(_downloadPath, fileName);

                using var httpClient = new HttpClient();
                using var response = await httpClient.GetAsync(downloadUrl, System.Threading.CancellationToken.None);
                response.EnsureSuccessStatusCode();
                await using var fileStream = File.Create(filePath);
                await response.Content.CopyToAsync(fileStream, System.Threading.CancellationToken.None);

                return true;
            }
        }
        catch (IOException)
        {
            // Network I/O failure during download
            return false;
        }
        catch (HttpRequestException)
        {
            // HTTP-level failure (404, 500, etc.) during download
            return false;
        }
        catch (ArgumentOutOfRangeException)
        {
            // No releases or assets available
            return false;
        }
        catch (Exception ex)
        {
            // Unexpected error during download; log and fail gracefully.
            _ = ex;
            return false;
        }

        return false;
    }
}