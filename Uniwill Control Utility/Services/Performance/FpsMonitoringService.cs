using System;
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
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
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

                // Try RTSS first — it works with DX12, anti-cheat, and all graphics APIs.
                if (!RtssReader.TryRead(processId, out RtssFpsData rtssData) || rtssData.Fps <= 0)
                {
                    if (tickCount % 10 == 0)
                        Serilog.Log.Debug("[FPS] tick {Tick} fgPid={Pid} - no RTSS data", tickCount, processId);
                    Volatile.Write(ref _latest, FpsMetricsSnapshot.Empty);
                    continue;
                }

                if (tickCount % 10 == 0)
                    Serilog.Log.Debug("[FPS] tick {Tick} fgPid={FgPid} rtssPid={RtssPid} name={Name} fps={Fps}", tickCount, processId, rtssData.ProcessId, rtssData.ProcessName, rtssData.Fps);

                // RTSS returned data for a hooked process. We must verify that the
                // process belongs to a recognized installed game AND that the game
                // window is actually in focus (or fullscreen) before showing the overlay.
                //
                // Two scenarios:
                //
                // 1) Exact PID match (rtssData.ProcessId == processId): the foreground
                //    IS the hooked game. Verify it's a recognized installed game.
                //
                // 2) Fallback match (rtssData.ProcessId != processId): the foreground
                //    is NOT the hooked game. This happens when our overlay (topmost)
                //    reports itself as foreground. The game is still active behind it.
                //    We verify the game has a visible, non-minimized window so we
                //    don't show the overlay when the user has switched to a different
                //    app (Terminal, browser, etc.).

                int gamePid;
                string gameHint;

                if (rtssData.ProcessId == processId)
                {
                    // Exact match — foreground is the hooked process.
                    gamePid = processId;
                    gameHint = rtssData.ProcessName;
                }
                else
                {
                    // Fallback — foreground is NOT the hooked game (overlay is
                    // topmost). Verify the game has a visible window that isn't
                    // minimized, meaning it's still active behind us.
                    if (!HasVisibleWindow(rtssData.ProcessId))
                    {
                        Volatile.Write(ref _latest, FpsMetricsSnapshot.Empty);
                        continue;
                    }

                    gamePid = rtssData.ProcessId;
                    gameHint = rtssData.ProcessName;
                }

                // Only show for recognized installed games (same list as Adaptive Mode).
                bool recognized = IsRecognizedGame(gamePid, gameHint);
                if (!recognized)
                {
                    Volatile.Write(ref _latest, FpsMetricsSnapshot.Empty);
                    continue;
                }

                int actualPid = rtssData.ProcessId;

                // TryGetProcessDetails may fail for anti-cheat-protected games (EAC blocks MainModule access).
                // RTSS already provides the process name and executable path, so use those as fallback.
                string name = string.Empty;
                string path = string.Empty;
                if (!TryGetProcessDetailsCached(actualPid, out name, out path))
                {
                    // Extract file name from RTSS-provided path (RTSS stores full path in szName).
                    path = rtssData.ProcessName;
                    name = System.IO.Path.GetFileNameWithoutExtension(rtssData.ProcessName);
                }

                Volatile.Write(ref _latest, new FpsMetricsSnapshot
                {
                    IsAvailable = true,
                    ProcessId = actualPid,
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
            return EnumWindows((hWnd, lParam) =>
            {
                if (GetWindowThreadProcessId(hWnd, out uint pid) != 0 && unchecked((int)pid) == gamePid)
                {
                    // Check if it's a top-level window (no owner).
                    IntPtr owner = GetWindow(hWnd, GW_OWNER);
                    if (owner == IntPtr.Zero && IsWindow(hWnd) && IsWindowVisible(hWnd))
                    {
                        // Check if it's not minimized.
                        if (!IsIconic(hWnd))
                            return false; // found it, stop enumeration
                    }
                }
                return true; // continue enumeration
            }, IntPtr.Zero);
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

        /// <summary>
        /// Checks whether the given process ID matches a recognized installed game
        /// from Game_Manager.installedGames (same positive-list used by the Adaptive page).
        /// Falls back to matching by processNameHint when MainModule is inaccessible
        /// (EAC-protected games block process.MainModule).
        /// </summary>
        private static bool IsRecognizedGame(int processId, string processNameHint = "")
        {
            if (processId <= 0)
                return false;

            var games = Game_Manager.installedGames;
            // If the game library hasn't loaded yet (syncGame_Library is async and
            // runs after the overlay starts), trust RTSS data. Once loaded, filter
            // by the positive list (same as Adaptive Mode's combobox).
            if (games is null)
            {
                Serilog.Log.Debug("[FPS] IsRecognizedGame pid={Pid} - installedGames is null, allowing", processId);
                return true;
            }

            if (games.Count == 0)
            {
                Serilog.Log.Debug("[FPS] IsRecognizedGame pid={Pid} - installedGames is empty, blocking", processId);
                return false;
            }

            string? exeName = null;
            string? exePath = null;

            try
            {
                using Process process = Process.GetProcessById(processId);
                exeName = process.ProcessName;
                exePath = process.MainModule?.FileName;
            }
            catch
            {
                // Process gone or inaccessible (anti-cheat protection).
                // Fall back to the hint if provided.
                if (string.IsNullOrEmpty(processNameHint))
                    return false;
            }

            foreach (var game in games)
            {
                // Match by executable path (most reliable).
                if (!string.IsNullOrWhiteSpace(game.path) && exePath is not null && exePath.Contains(game.path, StringComparison.OrdinalIgnoreCase))
                    return true;

                // Match RTSS hint path against game's install dir.
                if (!string.IsNullOrWhiteSpace(game.path) && !string.IsNullOrWhiteSpace(processNameHint) && processNameHint.Contains(game.path, StringComparison.OrdinalIgnoreCase))
                    return true;

                // Match game's install dir exe name (last path segment) against RTSS hint.
                // game.path is the install dir, but for some launchers it may include the exe.
                if (!string.IsNullOrWhiteSpace(game.path) && !string.IsNullOrWhiteSpace(processNameHint))
                {
                    string pathFileName = System.IO.Path.GetFileNameWithoutExtension(game.path);
                    if (processNameHint.Contains(pathFileName, StringComparison.OrdinalIgnoreCase))
                        return true;
                }

                // Match by exe name.
                if (!string.IsNullOrWhiteSpace(game.exe) && exeName is not null && exeName.Contains(game.exe, StringComparison.OrdinalIgnoreCase))
                    return true;

                // Match RTSS hint file name against game's exe.
                if (!string.IsNullOrWhiteSpace(game.exe) && !string.IsNullOrWhiteSpace(processNameHint))
                {
                    string hintName = System.IO.Path.GetFileNameWithoutExtension(processNameHint);
                    if (hintName.Contains(game.exe, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }

            // Debug: log why a process wasn't recognized.
            string hintDisplay = string.IsNullOrWhiteSpace(processNameHint) ? "(no hint)" : processNameHint;
            Serilog.Log.Debug("[FPS] IsRecognizedGame pid={Pid} - not recognized exeName={ExeName} exePath={ExePath} hint={Hint} games={GameCount}", processId, exeName, exePath, hintDisplay, games.Count);
            return false;
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
