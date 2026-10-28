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
            // Registration-repair self-destruction guard, field-proven necessity.
            // On a machine whose security product polices the ARP registry keys
            // (observed: Emsisoft), Velopack's own registration check sees the
            // live machine registration as missing (the app's registry view is
            // blind) and its repair logic runs "msiexec /x" on the LIVE product
            // code - twice proven to fire (19:41 at +4:16, 20:32 at +17 s after
            // a GUI install), each time removing the live registration and
            // licensing the uninstall sentinel's sweep: the app dies mid-splash
            // and the machine is cleaned under it. The repair needs the
            // UpdateManager, so under the phantom condition the manager stays
            // un-built: update checks are off (manual installs keep working),
            // and the install plus the Control Panel entry survive. The
            // condition is AV-specific and observable: the MSI database shows
            // the product, the app's own registry view does not.
            if (RegistrationViewIsPoliced())
            {
                State = UpdateState.NotInstalled;
                Log.Warning(
                    "[Update] UpdateManager stays un-built: the live MSI registration is invisible in " +
                    "this process's registry view (security-product registry protection), and Velopack's " +
                    "registration repair would /x the live install and arm the uninstall sentinel. " +
                    "Update checks stay off on this machine; installing updates by hand keeps working.");
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
        /// Decision rule: skip Velopack when the app runs from the per-machine
        /// layout (an installed fact, visible without any registry view) while
        /// the app's ARP registry view shows no registration of ours - the
        /// state in which Velopack concludes "not registered" and its repair
        /// logic /x-es the live product code. The other combinations are
        /// inert: dev/portable layouts have no install to destroy, and a
        /// visible registration gives the repair nothing to repair.
        /// </summary>
        internal static bool ShouldSkipVelopack(bool machineLayoutInstalled, bool registryViewSeesRegistration)
            => machineLayoutInstalled && !registryViewSeesRegistration;

        /// <summary>
        /// Reads the two halves of <see cref="ShouldSkipVelopack"/> from the
        /// machine. The MSI-database probe (MsiEnumProductsW) is NOT used:
        /// it reads the registry in-process, so a registry-guarded product
        /// blinds it too (field-proven 2026-10-28: the guard built on it
        /// never fired while the repair /x did). The observable truth is
        /// local: the app runs from the per-machine layout - an installed
        /// fact independent of registry views - and the ARP registry view
        /// shows no registration of ours.
        /// </summary>
        private static bool RegistrationViewIsPoliced()
        {
            try
            {
                bool machineLayoutInstalled = UninstallSentinel.InstalledLayoutPresent();
                bool registryViewSeesRegistration =
                    UninstallSentinel.Registered(UninstallSentinel.EnumerateMachineRegistrations());

                return ShouldSkipVelopack(machineLayoutInstalled, registryViewSeesRegistration);
            }
            catch
            {
                return false;
            }
        }
    }
}
