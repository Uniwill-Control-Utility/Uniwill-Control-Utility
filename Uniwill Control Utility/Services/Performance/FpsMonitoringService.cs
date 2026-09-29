using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
        private FpsTraceProcessor? _processor;
        private CancellationTokenSource? _cancellation;
        private int _leaseCount;
        private FpsMetricsSnapshot _latest = FpsMetricsSnapshot.Empty;
        private int _cachedApiPid;
        private string _cachedApi = "N/A";
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
            try
            {
                _processor = new FpsTraceProcessor();
                _processor.Start();
            }
            catch (Exception ex)
            {
                try { _processor?.Dispose(); } catch { /* best-effort cleanup */ }
                _processor = null;
                DiagnosticLogger.LogError(ex, "Failed to start the ETW FPS monitor");
            }

            // Always start the sampling loop even if ETW failed — RTSS still works.
            _cancellation = new CancellationTokenSource();
            Task.Run(() => SampleAsync(_cancellation.Token));
        }

        private async Task SampleAsync(CancellationToken cancellationToken)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                int processId = GetForegroundProcessId();

                // Try RTSS first — it works with DX12, anti-cheat, and all graphics APIs.
                if (RtssReader.TryRead(processId, out RtssFpsData rtssData) && rtssData.Fps > 0)
                {
                    // If RTSS returned a different PID than the foreground, it used the fallback
                    // (best game-like process in RTSS). Only accept the fallback if the foreground
                    // process is a recognized installed game — otherwise the user alt-tabbed away.
                    if (rtssData.ProcessId != processId && !IsRecognizedGame(processId))
                    {
                        Volatile.Write(ref _latest, FpsMetricsSnapshot.Empty);
                        continue;
                    }

                    // Use the PID from RTSS data (may differ from foreground PID when overlay is on top).
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
                    continue;
                }

                // Fall back to ETW when RTSS is not available or has no data for this process.
                FpsTraceProcessor? processor = _processor;
                if (processor is null)
                {
                    Volatile.Write(ref _latest, FpsMetricsSnapshot.Empty);
                    continue;
                }

                try
                {
                    processor.TargetProcessId = processId;
                    FrameStatistics statistics = processor.GetStatistics(processId);
                    if (statistics.Fps == 0 || !TryGetProcessDetailsCached(processId, out string name, out string path))
                    {
                        Volatile.Write(ref _latest, FpsMetricsSnapshot.Empty);
                        continue;
                    }

                    Volatile.Write(ref _latest, new FpsMetricsSnapshot
                    {
                        IsAvailable = true,
                        ProcessId = processId,
                        ProcessName = name,
                        ExecutablePath = path,
                        GraphicsApi = GetGraphicsApi(processId),
                        Fps = (int)Math.Round(statistics.Fps),
                        FrameTimeMs = statistics.FrameTimeMs,
                        OnePercentLowFps = (int)Math.Round(statistics.OnePercentLowFps),
                        OnePercentFrameTimeMs = statistics.OnePercentFrameTimeMs,
                        PointOnePercentLowFps = (int)Math.Round(statistics.PointOnePercentLowFps),
                        PointOnePercentFrameTimeMs = statistics.PointOnePercentFrameTimeMs
                    });
                }
                catch
                {
                    // A transient ETW read failure must not crash the sampling loop.
                }
            }
        }

        private string GetGraphicsApi(int processId)
        {
            if (_cachedApiPid == processId)
                return _cachedApi;

            _cachedApiPid = processId;
            _cachedApi = DetectGraphicsApi(processId);
            return _cachedApi;
        }

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

        private static string DetectGraphicsApi(int processId)
        {
            try
            {
                using Process process = Process.GetProcessById(processId);
                bool dx12 = false, dx11 = false, dx10 = false, dx9 = false, vulkan = false, openGl = false;
                foreach (ProcessModule module in process.Modules)
                {
                    string name = module.ModuleName;
                    vulkan |= name.Contains("vulkan", StringComparison.OrdinalIgnoreCase) || name.Contains("dxvk", StringComparison.OrdinalIgnoreCase);
                    openGl |= name.Contains("opengl", StringComparison.OrdinalIgnoreCase);
                    dx12 |= name.Contains("d3d12", StringComparison.OrdinalIgnoreCase);
                    dx11 |= name.Contains("d3d11", StringComparison.OrdinalIgnoreCase);
                    dx10 |= name.Contains("d3d10", StringComparison.OrdinalIgnoreCase);
                    dx9 |= name.Contains("d3d9", StringComparison.OrdinalIgnoreCase);
                }

                if (vulkan) return "Vulkan";
                if (dx12) return "DirectX 12";
                if (openGl) return "OpenGL";
                if (dx11) return "DirectX 11";
                if (dx10) return "DirectX 10";
                if (dx9) return "DirectX 9";
            }
            catch
            {
                // Process query failed (access denied, process exited); fall back to N/A.
            }

            return "N/A";
        }

        /// <summary>
        /// Checks whether the given process ID matches a recognized installed game
        /// from Game_Manager.installedGames (same positive-list used by the Adaptive page).
        /// </summary>
        private static bool IsRecognizedGame(int processId)
        {
            if (processId <= 0)
                return false;

            var games = Game_Manager.installedGames;
            if (games is null || games.Count == 0)
                return false;

            try
            {
                using Process process = Process.GetProcessById(processId);
                string exeName = process.ProcessName;
                string? exePath = process.MainModule?.FileName;

                foreach (var game in games)
                {
                    // Match by executable path (most reliable).
                    if (!string.IsNullOrWhiteSpace(game.path) &&
                        !string.IsNullOrWhiteSpace(exePath) &&
                        exePath.Contains(game.path, StringComparison.OrdinalIgnoreCase))
                        return true;

                    // Match by exe name.
                    if (!string.IsNullOrWhiteSpace(game.exe) &&
                        exeName.Contains(game.exe, StringComparison.OrdinalIgnoreCase))
                        return true;
                }

                return false;
            }
            catch
            {
                // Process gone or inaccessible (anti-cheat protection) — can't verify.
                return false;
            }
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
                _processor?.Dispose();
                _processor = null;
                _cancellation?.Dispose();
                _cancellation = null;
                _latest = FpsMetricsSnapshot.Empty;
                _cachedApiPid = 0;
                _cachedApi = "N/A";
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
