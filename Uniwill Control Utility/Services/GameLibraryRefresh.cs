using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Uniwill_Control_Utility.Scripts;
using Uniwill_Control_Utility.Scripts.Misc;

namespace Uniwill_Control_Utility.Services
{
    /// <summary>
    /// Keeps the installed-games list fresh for the whole app run. The scan used to run
    /// once at startup, so a game installed with the app running stayed invisible until
    /// the next start (the Resident Evil 9 field report, 2026-11-18: it never reached
    /// Adaptive Mode's picker). This watches the launcher manifest locations the scan
    /// reads and re-runs the same scan, debounced so an install (manifest created, then
    /// rewritten as the download progresses) is one scan, and capped for on-demand
    /// callers so navigating to a page cannot re-walk every volume.
    /// </summary>
    internal sealed class GameLibraryRefresh : IDisposable
    {
        /// <summary>A burst of manifest writes (an install in progress) lands in one scan.</summary>
        internal static readonly TimeSpan DebounceWindow = TimeSpan.FromSeconds(2);

        /// <summary>On-demand scans (a page showing the list opening) no more often than this.</summary>
        internal static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(3);

        /// <summary>One launcher manifest location: the directory and the watcher's filter.</summary>
        internal readonly record struct ManifestWatch(string Directory, string Filter);

        private readonly Func<List<Game_Manager.GameLauncherItem>?> _scan;
        private readonly Func<List<Game_Manager.GameLauncherItem>?, (int Total, int Added)> _publish;
        private readonly Func<IEnumerable<ManifestWatch>> _watchTargets;
        private readonly TimeProvider _time;
        private readonly TimeSpan _debounce;
        private readonly object _gate = new object();
        private readonly List<FileSystemWatcher> _watchers = new List<FileSystemWatcher>();

        private Task _pending = Task.CompletedTask;
        private bool _pendingBusy;
        private bool _dirty;
        private DateTimeOffset _lastCompleted;
        private bool _disposed;

        internal GameLibraryRefresh(
            Func<List<Game_Manager.GameLauncherItem>?> scan,
            Func<List<Game_Manager.GameLauncherItem>?, (int Total, int Added)> publish,
            Func<IEnumerable<ManifestWatch>> watchTargets,
            TimeProvider? time = null,
            TimeSpan? debounce = null)
        {
            _scan = scan ?? throw new ArgumentNullException(nameof(scan));
            _publish = publish ?? throw new ArgumentNullException(nameof(publish));
            _watchTargets = watchTargets ?? throw new ArgumentNullException(nameof(watchTargets));
            _time = time ?? TimeProvider.System;
            _debounce = debounce ?? DebounceWindow;
            // The caller starts this right after the startup scan, so the first page
            // open does not repeat it.
            _lastCompleted = _time.GetUtcNow();
        }

        /// <summary>The pending debounced refresh; tests await it instead of sleeping.</summary>
        internal Task WhenIdle
        {
            get { lock (_gate) return _pending; }
        }

        internal int WatcherCount
        {
            get { lock (_gate) return _watchers.Count; }
        }

        /// <summary>
        /// Starts the manifest watchers. Safe to call again: it only adds locations that
        /// appeared since (a Steam library mounted after startup).
        /// </summary>
        internal void StartWatching()
        {
            IEnumerable<ManifestWatch> targets;
            try
            {
                targets = _watchTargets();
            }
            catch (Exception ex)
            {
                DiagnosticLogger.LogError(ex, "game library watch locations unavailable");
                return;
            }

            foreach (ManifestWatch target in targets)
                AddWatcher(target);
        }

        /// <summary>
        /// A manifest changed: one scan once the burst settles. Returns immediately, and
        /// an event that lands while a refresh is already pending only marks the batch
        /// dirty (logged at Debug) - it never starts a second scan.
        /// </summary>
        internal void NotifyManifestChanged()
        {
            bool coalesced;
            lock (_gate)
            {
                if (_disposed)
                    return;

                coalesced = _dirty;
                _dirty = true;
                if (_pendingBusy)
                    return;

                _pendingBusy = true;
                _pending = Task.Run(RunDebouncedAsync);
            }

            if (coalesced)
                DiagnosticLogger.LogDebug("game library refresh already pending; manifest event coalesced");
        }

        /// <summary>
        /// The on-demand path (a page that shows the list opening). Returns true when a
        /// scan ran, false when the cap (or disposal) says the published list is fresh
        /// enough. An on-demand scan is not debounced: the caller is waiting for it.
        /// </summary>
        internal async Task<bool> RefreshAsync(string reason, bool force = false)
        {
            if (!force)
            {
                TimeSpan since;
                lock (_gate)
                {
                    if (_disposed)
                        return false;

                    since = _time.GetUtcNow() - _lastCompleted;
                }

                if (since < MinimumInterval)
                {
                    DiagnosticLogger.LogDebug(
                        $"game library refresh skipped ({reason}): last scan {since.TotalSeconds:F0}s ago, cap {MinimumInterval.TotalMinutes:F0} min");
                    return false;
                }
            }

            return await RunScanAsync().ConfigureAwait(false);
        }

        private async Task RunDebouncedAsync()
        {
            await Task.Delay(_debounce).ConfigureAwait(false);

            while (true)
            {
                lock (_gate)
                {
                    if (_disposed)
                        return;

                    _dirty = false;
                }

                await RunScanAsync().ConfigureAwait(false);

                bool again;
                lock (_gate)
                {
                    again = _dirty && !_disposed;
                    if (!again)
                    {
                        _pendingBusy = false;
                        return;
                    }
                }

                // A manifest was written while the scan was reading: that write may have
                // landed after it (Steam's final StateFlags rewrite), so scan again.
                await Task.Delay(_debounce).ConfigureAwait(false);
            }
        }

        private async Task<bool> RunScanAsync()
        {
            List<Game_Manager.GameLauncherItem>? discovered = null;
            bool scanned = await Task.Run(() =>
            {
                try
                {
                    discovered = _scan();
                    return discovered is not null;
                }
                catch (Exception ex)
                {
                    DiagnosticLogger.LogError(ex, "game library scan failed");
                    return false;
                }
            }).ConfigureAwait(false);

            // syncGame_Library logs its own cause for a null result.
            if (!scanned || discovered is null)
                return false;

            (int Total, int Added) published;
            try
            {
                published = _publish(discovered);
            }
            catch (Exception ex)
            {
                DiagnosticLogger.LogError(ex, "game library publish failed");
                return false;
            }

            lock (_gate)
                _lastCompleted = _time.GetUtcNow();

            DiagnosticLogger.LogInfo($"game library refreshed: {published.Total} games (+{published.Added} new)");

            // A library mounted since startup has new manifest directories to watch.
            StartWatching();
            return true;
        }

        private void AddWatcher(ManifestWatch target)
        {
            lock (_gate)
            {
                if (_disposed)
                    return;

                foreach (FileSystemWatcher existing in _watchers)
                {
                    if (string.Equals(existing.Path, target.Directory, StringComparison.OrdinalIgnoreCase))
                        return;
                }
            }

            // A launcher that is not installed has no manifest directory. That is the
            // normal case for most of them, so it is a debug no-op, never an error.
            if (!Directory.Exists(target.Directory))
            {
                DiagnosticLogger.LogDebug($"game library watcher skipped, no manifest directory: {target.Directory}");
                return;
            }

            try
            {
                var watcher = new FileSystemWatcher(target.Directory, target.Filter)
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                };

                FileSystemEventHandler onEvent = (_, __) => NotifyManifestChanged();
                watcher.Created += onEvent;
                watcher.Changed += onEvent;
                watcher.Deleted += onEvent;
                watcher.Renamed += (_, __) => NotifyManifestChanged();
                watcher.Error += (_, e) =>
                    DiagnosticLogger.LogDebug($"game library watcher error on {target.Directory}: {e.GetException().Message}");
                watcher.EnableRaisingEvents = true;

                lock (_gate)
                    _watchers.Add(watcher);

                // shortcut: changes to steamapps\libraryfolders.vdf (a whole new Steam
                // library) are not watched, so a fresh library is picked up by the next
                // scan that some other manifest change or a page open triggers.
                DiagnosticLogger.LogDebug($"game library watcher: {target.Directory} ({target.Filter})");
            }
            catch (Exception ex)
            {
                // Unreadable or blocked directory: watch what we can, never fail the app.
                DiagnosticLogger.LogDebug($"game library watcher unavailable for {target.Directory}: {ex.Message}");
            }
        }

        public void Dispose()
        {
            List<FileSystemWatcher> watchers;
            lock (_gate)
            {
                if (_disposed)
                    return;

                _disposed = true;
                watchers = new List<FileSystemWatcher>(_watchers);
                _watchers.Clear();
            }

            foreach (FileSystemWatcher watcher in watchers)
            {
                try
                {
                    watcher.EnableRaisingEvents = false;
                    watcher.Dispose();
                }
                catch
                {
                    // Teardown: a watcher that cannot close is the process ending anyway.
                }
            }
        }
    }
}
