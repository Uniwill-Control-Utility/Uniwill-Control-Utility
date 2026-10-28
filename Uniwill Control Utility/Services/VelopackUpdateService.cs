using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Serilog;
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
            // Registration-repair self-destruction guard, field-proven to
            // closure. Velopack's per-machine registration check runs IN
            // PROCESS: on a machine whose security product polices registry
            // views (observed: Emsisoft), the check sees the live MSI
            // registration as missing and the repair logic runs "msiexec /x"
            // on the LIVE product code. Two field-proven deaths came from
            // OPPOSITE views: 20:57 with the ARP keys blind (the guard built
            // on the ARP view caught that one) and 21:37 with ARP VISIBLE
            // while Velopack's own MSI-classes hive read was the blind one -
            // no view-based guard can cover every hive an AV can blind, so
            // the rule is not about the views at all: an app running from
            // the per-machine layout never builds the UpdateManager. The MSI
            // is that layout's update mechanism (running a new Setup.msi);
            // Velopack's managed update flow lives on the per-user portable/
            // dev layouts, whose root has no MSI registration to destroy.
            // Update checks stay off on installed machines: the title-bar
            // card and auto-check no-op, manual MSI installs keep working,
            // and nothing can ever /x the live product from inside the app.
            if (RunsFromMachineLayout())
            {
                State = UpdateState.NotInstalled;
                Log.Warning(
                    "[Update] UpdateManager stays un-built: this app runs from the per-machine (MSI) " +
                    "layout, where Velopack's in-process registration check can be blinded by a " +
                    "security product and its repair would /x the live install (field-proven from both " +
                    "registry-view directions). Updates on installed machines arrive by MSI; the " +
                    "Velopack update flow stays on portable/dev layouts.");
                return;
            }

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

            if (UpdaterShimPath is string shim)
            {
                if (LaunchUpdaterShim(shim))
                    return; // the shim owns the apply, the restart and this process's exit

                SetState(UpdateState.Failed, 0);
                return;
            }

            _manager.ApplyUpdatesAndRestart(asset, new[] { "--from-update" });
        }

        /// <summary>
        /// Applies the staged package on graceful exit (called from App.OnExit
        /// after all hardware teardown). Terminates the process — must be the
        /// last statement of the shutdown path. Returns false when there is
        /// nothing staged (the common case) or the elevation was declined.
        /// </summary>
        public bool TryApplyOnExit()
        {
            var asset = GetPendingAsset();
            if (_manager is null || asset is null)
                return false;

            SetState(UpdateState.Applying, 100);

            if (UpdaterShimPath is string shim)
            {
                if (LaunchUpdaterShim(shim))
                {
                    Environment.Exit(0); // same process-death contract as ApplyUpdatesAndExit
                    return true;
                }

                return false;
            }

            _manager.ApplyUpdatesAndExit(asset);
            return true;
        }

        /// <summary>
        /// The stamped updater shim pack.ps1 ships next to the app, or null on
        /// layouts without it (plain dev builds).
        /// </summary>
        private static string? UpdaterShimPath
        {
            get
            {
                string path = Path.Combine(AppContext.BaseDirectory, "UCU.Updater.exe");
                return File.Exists(path) ? path : null;
            }
        }

        /// <summary>
        /// Hands the apply to UCU.Updater.exe. The per-machine layout needs
        /// admin to write, and the running engine service locks the Service\
        /// payload the update replaces: the shim (requireAdministrator manifest,
        /// compiled by pack.ps1) stops the engine, runs the elevated Velopack
        /// apply (Uninstall.exe -a --from-update, which relaunches the app), and
        /// starts the engine again. Returns false when the user declined the
        /// consent prompt (1223), so callers can fall back to a normal shutdown.
        /// On success this method does not return to the caller's message loop:
        /// the app exits as Velopack's own apply-restart does.
        /// </summary>
        private static bool LaunchUpdaterShim(string shimPath)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = shimPath,
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden,
                };
                Process.Start(psi);

                Log.Information("[Update] Handed the apply to UCU.Updater.exe; exiting for the elevated apply.");
                Environment.Exit(0);
                return true;
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                // 1223: consent declined at the prompt. The staged package
                // stays staged; the next attempt re-asks.
                Log.Warning(ex, "[Update] Update elevation declined (code {Code})", ex.NativeErrorCode);
                return false;
            }
        }

        private VelopackAsset? GetPendingAsset()
            => _manager?.UpdatePendingRestart ?? _lastCheck?.TargetFullRelease;

        private void SetState(UpdateState state, int progress)
        {
            State = state;
            StateChanged?.Invoke(state, progress);
        }

        /// <summary>
        /// Decision rule: an app running from the per-machine (MSI) layout
        /// never builds the UpdateManager. Velopack's per-machine
        /// registration check is an in-process registry read, and every
        /// registry view it consults is one a security product can blind -
        /// field-proven from BOTH directions: the ARP-blind state at 20:57
        /// (a guard built on the ARP view caught that class) and the
        /// ARP-visible / Velopack-classes-blind state at 21:37 (the same
        /// guard stood aside and the repair /x-ed the live product code,
        /// arming the sentinel's sweep). No view-based guard can cover a
        /// policy the app cannot observe, so the rule is about the layout:
        /// the MSI layout's updates arrive by MSI, and portable/dev layouts'
        /// Velopack root has no MSI registration to destroy.
        /// </summary>
        internal static bool ShouldSkipVelopack(bool machineLayoutInstalled)
            => machineLayoutInstalled;

        /// <summary>
        /// The layout half of <see cref="ShouldSkipVelopack"/>, read from the
        /// filesystem probe <see cref="UninstallSentinel.InstalledLayoutPresent"/>:
        /// running from Program Files\Uniwill-Control-Utility\current is an
        /// installed fact that needs no registry view at all - which is the
        /// point, since every registry view on this machine class is a
        /// potential lie. Fails open: a probe error reads as "not the machine
        /// layout", which is the pre-guard behaviour on a layout that has no
        /// MSI registration to destroy.
        /// </summary>
        private static bool RunsFromMachineLayout()
        {
            try
            {
                return ShouldSkipVelopack(UninstallSentinel.InstalledLayoutPresent());
            }
            catch
            {
                return false;
            }
        }
    }
}
