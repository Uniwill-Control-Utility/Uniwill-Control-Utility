using System;
using System.Threading.Tasks;
using Velopack;

namespace Uniwill_Control_Utility.Services
{
    /// <summary>
    /// Lifecycle of the Velopack update flow for this process.
    /// </summary>
    public enum UpdateState
    {
        /// <summary>Portable (zip) layout — Velopack APIs are unavailable; all update features no-op.</summary>
        NotInstalled,
        Idle,
        Checking,
        UpToDate,
        UpdateAvailable,
        Downloading,
        /// <summary>Package downloaded; applied on graceful exit or via "Update and restart".</summary>
        PendingApply,
        Applying,
        Failed,
    }

    /// <summary>
    /// Pure decision core for the update flow — no Velopack types, unit-testable.
    /// </summary>
    internal static class UpdatePolicy
    {
        public static bool ShouldAutoCheck(bool isInstalled, bool autoCheck, bool internetAvailable)
            => isInstalled && autoCheck && internetAvailable;

        public static bool ShouldSilentDownload(bool isInstalled, bool autoCheck, bool autoApply, UpdateState state)
            => isInstalled && autoCheck && autoApply && state == UpdateState.UpdateAvailable;

        public static bool ShouldShowTitleBarCard(UpdateState state)
            => state is UpdateState.UpdateAvailable
                or UpdateState.Downloading
                or UpdateState.PendingApply;
    }

    /// <summary>
    /// Wraps Velopack's UpdateManager against the GitHub Releases feed.
    /// The feed is discovered via the releases.win.json asset that vpk pack
    /// publishes with each release; releases without it are ignored.
    ///
    /// UX contract: updates are downloaded silently and applied on graceful
    /// exit (App.OnExit → TryApplyOnExit) or via the title-bar card
    /// (ApplyAndRestart). Nothing is ever applied as a surprise restart.
    ///
    /// On portable (non-installed) layouts every entry point no-ops —
    /// Velopack throws NotInstalledException for those, so IsInstalled gates
    /// all calls.
    /// </summary>
    public sealed class VelopackUpdateService
    {
        public const string GitHubFeedUrl = "https://github.com/jgudec/Uniwill-Control-Utility";

        /// <summary>
        /// Local folder feed override for end-to-end testing (points the
        /// UpdateManager at a vpk "Releases" directory instead of GitHub).
        /// </summary>
        public const string FeedPathOverrideEnvVar = "UCU_UPDATE_FEED_PATH";

        private readonly UpdateManager? _manager;
        private UpdateInfo? _lastCheck;

        public VelopackUpdateService()
        {
            var feedOverride = Environment.GetEnvironmentVariable(FeedPathOverrideEnvVar);
            var feed = string.IsNullOrWhiteSpace(feedOverride) ? GitHubFeedUrl : feedOverride;

            var manager = new UpdateManager(feed);
            if (!manager.IsInstalled)
            {
                State = UpdateState.NotInstalled;
                return;
            }

            _manager = manager;

            // A package downloaded in a previous session (e.g. the app crashed
            // before apply) is already staged — surface it immediately.
            State = manager.UpdatePendingRestart is not null
                ? UpdateState.PendingApply
                : UpdateState.Idle;
        }

        public UpdateState State { get; private set; }

        public bool IsInstalled => _manager is not null;

        public string? NewVersion => _lastCheck?.TargetFullRelease.Version.ToString();

        /// <summary>
        /// Fires on every state transition with the download progress (0-100,
        /// meaningful only while Downloading). Subscribers marshal to their
        /// dispatcher.
        /// </summary>
        public event Action<UpdateState, int>? StateChanged;

        /// <summary>
        /// Checks the feed. Returns the update info, or null when up to date.
        /// Throws on network/feed errors (state transitions to Failed).
        /// </summary>
        public async Task<UpdateInfo?> CheckForUpdatesAsync()
        {
            if (_manager is null)
                return null;

            SetState(UpdateState.Checking, 0);
            try
            {
                var info = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);

                if (info is null)
                {
                    SetState(UpdateState.UpToDate, 0);
                    return null;
                }

                _lastCheck = info;
                SetState(_manager.UpdatePendingRestart is not null
                    ? UpdateState.PendingApply
                    : UpdateState.UpdateAvailable, 0);
                return info;
            }
            catch (Exception)
            {
                SetState(UpdateState.Failed, 0);
                throw;
            }
        }

        /// <summary>
        /// Downloads the package found by the last check. Transitions to
        /// PendingApply on success.
        /// </summary>
        public async Task DownloadAsync(IProgress<int>? progress)
        {
            if (_manager is null)
                return;

            var info = _lastCheck;
            if (info is null)
                return;

            SetState(UpdateState.Downloading, 0);
            try
            {
                await _manager.DownloadUpdatesAsync(
                    info,
                    progress is null ? null : new Action<int>(value => progress.Report(value))).ConfigureAwait(false);

                SetState(UpdateState.PendingApply, 100);
            }
            catch (Exception)
            {
                SetState(UpdateState.Failed, 0);
                throw;
            }
        }

        /// <summary>
        /// Applies the staged package and relaunches the app (title-bar card
        /// button). The restart carries --from-update so the relaunched app
        /// can toast the new version. Terminates this process.
        /// </summary>
        public void ApplyAndRestart()
        {
            var asset = GetPendingAsset();
            if (_manager is null || asset is null)
                return;

            SetState(UpdateState.Applying, 100);
            _manager.ApplyUpdatesAndRestart(asset, new[] { "--from-update" });
        }

        /// <summary>
        /// Applies the staged package on graceful exit (called from App.OnExit
        /// after all hardware teardown). Terminates the process — must be the
        /// last statement of the shutdown path. Returns false when there is
        /// nothing staged (the common case).
        /// </summary>
        public bool TryApplyOnExit()
        {
            var asset = GetPendingAsset();
            if (_manager is null || asset is null)
                return false;

            SetState(UpdateState.Applying, 100);
            _manager.ApplyUpdatesAndExit(asset);
            return true;
        }

        private VelopackAsset? GetPendingAsset()
            => _manager?.UpdatePendingRestart ?? _lastCheck?.TargetFullRelease;

        private void SetState(UpdateState state, int progress)
        {
            State = state;
            StateChanged?.Invoke(state, progress);
        }
    }
}
