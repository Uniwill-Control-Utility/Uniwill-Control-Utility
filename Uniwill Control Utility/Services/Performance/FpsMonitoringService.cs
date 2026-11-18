using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Uniwill_Control_Utility.Scripts;
using Uniwill_Control_Utility.Scripts.Misc;

namespace Uniwill_Control_Utility.Services.Performance
{
    public sealed record FpsMetricsSnapshot
    {
        public static FpsMetricsSnapshot Empty { get; } = new();
        public bool IsAvailable { get; init; }
        public int ProcessId { get; init; }
        public string ProcessName { get; init; } = string.Empty;
        public string ExecutablePath { get; init; } = string.Empty;
        public string GraphicsApi { get; init; } = "N/A";
        public int Fps { get; init; }
        public double FrameTimeMs { get; init; }
        public int OnePercentLowFps { get; init; }
        public double OnePercentFrameTimeMs { get; init; }
        public int PointOnePercentLowFps { get; init; }
        public double PointOnePercentFrameTimeMs { get; init; }
    }

    public interface IFpsMonitoringService
    {
        IDisposable Acquire();
        FpsMetricsSnapshot Latest { get; }
    }

    public sealed partial class FpsMonitoringService : IFpsMonitoringService, IDisposable
    {
        private readonly object _sync = new();
        private CancellationTokenSource? _cancellation;
        private int _leaseCount;
        private FpsMetricsSnapshot _latest = FpsMetricsSnapshot.Empty;
        private int _cachedDetailsPid;
        private string _cachedProcessName = string.Empty;
        private string _cachedExecutablePath = string.Empty;

        public FpsMetricsSnapshot Latest => Volatile.Read(ref _latest);

        public IDisposable Acquire()
        {
            lock (_sync)
            {
                _leaseCount++;
                if (_leaseCount == 1)
                    Start();
                return new Lease(this);
            }
        }

        private void Start()
        {
            _cancellation = new CancellationTokenSource();
            Serilog.Log.Debug("[FPS] Start - sampling loop starting");
            var t = Task.Run(() => SampleAsync(_cancellation.Token));
            Serilog.Log.Debug("[FPS] Task.Run returned, task status={Status}", t.Status);
        }

        private async Task SampleAsync(CancellationToken cancellationToken)
        {
            Serilog.Log.Debug("[FPS] SampleAsync entered, creating timer");
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
            int tickCount = 0;
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                tickCount++;
                try
                {
                    int processId = GetForegroundProcessId();

                // Hide during Windows alt-tab — the overlay should not appear
                // over the task switcher even if a game is running in background.
                if (IsAltTabActive())
                {
                    Volatile.Write(ref _latest, FpsMetricsSnapshot.Empty);
                    continue;
                }

                // RTSS answers only for the foreground process's own entry: no entry
                // means no FPS this tick, never another process's borrowed frame rate.
                if (!RtssReader.TryRead(processId, out RtssFpsData rtssData) || rtssData.Fps <= 0)
                {
                    if (tickCount % 10 == 0)
                        Serilog.Log.Debug("[FPS] tick {Tick} fgPid={Pid} - no RTSS entry for the foreground process", tickCount, processId);
                    Volatile.Write(ref _latest, FpsMetricsSnapshot.Empty);
                    continue;
                }

                // TryGetProcessDetails may fail for anti-cheat-protected games (EAC blocks
                // MainModule access). RTSS already provides the executable path, so use it.
                string name;
                string path;
                if (!TryGetProcessDetailsCached(processId, out name, out path))
                {
                    path = rtssData.ProcessName;
                    name = System.IO.Path.GetFileNameWithoutExtension(rtssData.ProcessName);
                }

                // Live frames for the foreground process are the strongest "this is a
                // game" signal, so it is the rule that accepts a hooked game missing
                // from the installed list. The shell/our own process are still refused.
                string? rule = AcceptanceRule(name, path, rtssTracked: true);
                if (rule is null)
                {
                    Serilog.Log.Debug("[FPS] IsRecognizedGame pid={Pid} - not recognized exeName={ExeName} exePath={ExePath}", processId, name, path);
                    Volatile.Write(ref _latest, FpsMetricsSnapshot.Empty);
                    continue;
                }

                // Guard: the frames must belong to a process with a visible top-level
                // window. RTSS hooks whatever renders (wallpaper engines included), and
                // hidden frames are not what the user is looking at.
                if (!HasVisibleWindow(processId))
                {
                    if (tickCount % 10 == 0)
                        Serilog.Log.Debug("[FPS] tick {Tick} pid={Pid} - accepted {Rule} but no visible top-level window", tickCount, processId, rule);
                    Volatile.Write(ref _latest, FpsMetricsSnapshot.Empty);
                    continue;
                }

                LogAcceptanceOnce(processId, rule, name, path);

                if (tickCount % 10 == 0)
                    Serilog.Log.Debug("[FPS] tick {Tick} fgPid={FgPid} name={Name} rule={Rule} fps={Fps}", tickCount, processId, rtssData.ProcessName, rule, rtssData.Fps);

                Volatile.Write(ref _latest, new FpsMetricsSnapshot
                {
                    IsAvailable = true,
                    ProcessId = processId,
                    ProcessName = name,
                    ExecutablePath = path,
                    GraphicsApi = rtssData.GraphicsApi,
                    Fps = (int)Math.Round(rtssData.Fps),
                    FrameTimeMs = rtssData.FrameTimeMs,
                    OnePercentLowFps = (int)Math.Round(rtssData.OnePercentLowFps),
                    OnePercentFrameTimeMs = rtssData.OnePercentFrameTimeMs,
                    PointOnePercentLowFps = (int)Math.Round(rtssData.PointOnePercentLowFps),
                    PointOnePercentFrameTimeMs = rtssData.PointOnePercentFrameTimeMs
                });
                }
                catch (Exception ex)
                {
                    Serilog.Log.Error(ex, "[FPS] tick {Tick} - unhandled exception", tickCount);
                }
            }
        }

        /// <summary>
        /// Returns true if the foreground window appears to be the Windows
        /// alt-tab overlay (TaskSwitcherWnd). During alt-tab the overlay
        /// should hide even if a recognized game is running in background.
        /// </summary>
        private static bool IsAltTabActive()
        {
            IntPtr window = GetForegroundWindow();
            if (window == IntPtr.Zero)
                return false;

            // The alt-tab window class is "TaskSwitcherWnd" (Windows 10/11).
            int classNameLen = GetClassName(window, IntPtr.Zero, 0);
            if (classNameLen <= 0)
                return false;

            char[] className = new char[classNameLen + 1];
            GetClassName(window, className, className.Length);
            string name = new string(className).TrimEnd('\0');

            return name.Equals("TaskSwitcherWnd", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("TaskSwitcherMPHost", StringComparison.OrdinalIgnoreCase);
        }

        [LibraryImport("user32.dll", SetLastError = true, EntryPoint = "GetClassNameW")]
        private static partial int GetClassName(IntPtr hWnd, IntPtr lpClassName, int nMaxCount);

        [LibraryImport("user32.dll", SetLastError = true, EntryPoint = "GetClassNameW", StringMarshalling = StringMarshalling.Utf16)]
        private static partial int GetClassName(IntPtr hWnd, char[]? lpClassName, int nMaxCount);

        /// <summary>
        /// Returns true if the process has a top-level window that is visible
        /// and not minimized. This is used in the fallback path to verify the
        /// game is still active behind our topmost overlay, rather than having
        /// been switched away to a different application.
        /// </summary>
        private static bool HasVisibleWindow(int gamePid)
        {
            bool found = false;

            EnumWindows((hWnd, lParam) =>
            {
                if (GetWindowThreadProcessId(hWnd, out uint pid) != 0 && unchecked((int)pid) == gamePid)
                {
                    // Check if it's a top-level window (no owner).
                    IntPtr owner = GetWindow(hWnd, GW_OWNER);
                    if (owner == IntPtr.Zero && IsWindow(hWnd) && IsWindowVisible(hWnd))
                    {
                        // Check if it's not minimized.
                        if (!IsIconic(hWnd))
                        {
                            found = true;
                            return false; // found it, stop enumeration
                        }
                    }
                }
                return true; // continue enumeration
            }, IntPtr.Zero);

            // EnumWindows answers "the callback never returned false", which is the
            // OPPOSITE of "found one" - returning that value inverted this check: the
            // overlay was hidden when the game was behind it and shown when the game
            // was minimised. The closure is the fact.
            return found;
        }

        [LibraryImport("user32.dll")]
        private static partial IntPtr GetWindow(IntPtr hWnd, uint uCmd);

        private const uint GW_OWNER = 4;

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool IsIconic(IntPtr hWnd);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool IsWindow(IntPtr hWnd);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool IsWindowVisible(IntPtr hWnd);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        private bool TryGetProcessDetailsCached(int processId, out string name, out string path)
        {
            if (_cachedDetailsPid == processId)
            {
                name = _cachedProcessName;
                path = _cachedExecutablePath;
                return !string.IsNullOrEmpty(name);
            }

            bool found = TryGetProcessDetails(processId, out name, out path);
            if (found)
            {
                _cachedDetailsPid = processId;
                _cachedProcessName = name;
                _cachedExecutablePath = path;
            }
            else
            {
                _cachedDetailsPid = 0;
                _cachedProcessName = string.Empty;
                _cachedExecutablePath = string.Empty;
            }
            return found;
        }

        private const string SteamCommonSegment = @"\steamapps\common\";

        // The desktop shell and our own process are never games, whatever RTSS hooks.
        private static readonly HashSet<string> NonGameProcesses = BuildNonGameProcessNames();

        private static HashSet<string> BuildNonGameProcessNames()
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "explorer", "progman", "winlogon", "lockapp", "searchhost",
                "startmenuexperiencehost", "shellexperiencehost", "textinputhost"
            };
            try { names.Add(Process.GetCurrentProcess().ProcessName); } catch { }
            return names;
        }

        /// <summary>
        /// Why a foreground process counts as a game, or null when it does not, in
        /// precedence order: the shell/our own process are refused first, then RTSS
        /// tracking (live frames for this process — the strongest rendering signal),
        /// then a Steam library path, then the installed-games list. The installed list
        /// is an additional acceptance path, never a gate: 0.9.13 rejected re9.exe,
        /// which RTSS was hooking perfectly, only because the list had not enumerated it.
        /// </summary>
        internal static string? AcceptanceRule(string? exeName, string? exePath, bool rtssTracked)
        {
            string name = FileNameWithoutExtension(exeName);
            if (name.Length == 0)
                name = FileNameWithoutExtension(exePath);

            if (name.Length == 0 || NonGameProcesses.Contains(name))
                return null;

            if (rtssTracked)
                return "rtss-tracked";

            if (!string.IsNullOrEmpty(exePath) && exePath.Contains(SteamCommonSegment, StringComparison.OrdinalIgnoreCase))
                return "steam-library path";

            return MatchesInstalledList(exeName, exePath) ? "installed list" : null;
        }

        private static string FileNameWithoutExtension(string? path)
            => string.IsNullOrWhiteSpace(path) ? string.Empty : System.IO.Path.GetFileNameWithoutExtension(path);

        /// <summary>
        /// Positive-list match against Game_Manager.installedGames. A null or empty list
        /// (syncGame_Library is async and may not have run yet) matches nothing and falls
        /// through to the other rules, rather than allowing or blocking everything.
        /// </summary>
        private static bool MatchesInstalledList(string? exeName, string? exePath)
        {
            List<Game_Manager.GameLauncherItem>? games = Game_Manager.installedGames;
            if (games is null || games.Count == 0)
                return false;

            foreach (Game_Manager.GameLauncherItem game in games)
            {
                // Match by executable path (most reliable).
                if (!string.IsNullOrWhiteSpace(game.path) && exePath is not null && exePath.Contains(game.path, StringComparison.OrdinalIgnoreCase))
                    return true;

                // game.path is usually the install dir, but for some launchers it names
                // the exe; either way its last segment identifies the game.
                if (!string.IsNullOrWhiteSpace(game.path) && exeName is not null)
                {
                    string pathName = FileNameWithoutExtension(game.path);
                    if (pathName.Length > 0 && exeName.Contains(pathName, StringComparison.OrdinalIgnoreCase))
                        return true;
                }

                // Match by exe name.
                if (!string.IsNullOrWhiteSpace(game.exe) && exeName is not null && exeName.Contains(game.exe, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        // One acceptance line per (foreground pid, rule): the sampler runs at 4 Hz.
        private int _lastAcceptPid;
        private string? _lastAcceptRule;

        private void LogAcceptanceOnce(int pid, string rule, string name, string path)
        {
            if (_lastAcceptPid == pid && _lastAcceptRule == rule)
                return;

            _lastAcceptPid = pid;
            _lastAcceptRule = rule;
            Serilog.Log.Debug("[FPS] accepted: {Rule} pid={Pid} exeName={ExeName} exePath={ExePath}", rule, pid, name, path);
        }

        private static bool TryGetProcessDetails(int processId, out string name, out string path)
        {
            name = string.Empty;
            path = string.Empty;
            try
            {
                using Process process = Process.GetProcessById(processId);
                if (process.HasExited)
                    return false;
                name = process.ProcessName;
                path = process.MainModule?.FileName ?? string.Empty;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void Release()
        {
            lock (_sync)
            {
                if (_leaseCount == 0 || --_leaseCount > 0)
                    return;

                _cancellation?.Cancel();
                _cancellation?.Dispose();
                _cancellation = null;
                _latest = FpsMetricsSnapshot.Empty;
                _cachedDetailsPid = 0;
                _cachedProcessName = string.Empty;
                _cachedExecutablePath = string.Empty;
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                _leaseCount = 1;
                Release();
            }
        }

        private sealed class Lease : IDisposable
        {
            private FpsMonitoringService? _owner;
            public Lease(FpsMonitoringService owner) => _owner = owner;
            public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
        }

        private static int GetForegroundProcessId()
        {
            IntPtr window = GetForegroundWindow();
            if (window == IntPtr.Zero)
                return 0;

            _ = GetWindowThreadProcessId(window, out uint processId);
            return unchecked((int)processId);
        }

        [LibraryImport("user32.dll")]
        private static partial IntPtr GetForegroundWindow();

        [LibraryImport("user32.dll")]
        private static partial uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    }
}
