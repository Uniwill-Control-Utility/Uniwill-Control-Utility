using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using Uniwill_Control_Utility.Scripts.Misc;

namespace Uniwill_Control_Utility.Services.Performance
{
    // RTSS v2.x APPFLAG_* constants for graphics API detection.
    // Source: RTSSharedMemory.h (v2.10+ flag layout)
    internal static class RtssFlags
    {
        public const int ApiUsageMask = 0x0000FFFF;

        public const int Ogl = 0x00000001;
        public const int Dd = 0x00000002;
        public const int D3d8 = 0x00000003;
        public const int D3d9 = 0x00000004;
        public const int D3d9Ex = 0x00000005;
        public const int D3d10 = 0x00000006;
        public const int D3d11 = 0x00000007;
        public const int D3d12 = 0x00000008;
        public const int D3d12Afr = 0x00000009;
        public const int Vulkan = 0x0000000A;
    }

    // RTSS v2.x shared memory header.
    // Source: RTSSharedMemory.h (RTSS_SHARED_MEMORY)
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct RtssHeader
    {
        public uint Signature;
        public uint Version;
        public uint AppEntrySize;
        public uint AppArrOffset;
        public uint AppArrSize;
        public uint OsdEntrySize;
        public uint OsdArrOffset;
        public uint OsdArrSize;
        public uint OsdFrame;
    }

    // Per-process FPS data read from RTSS shared memory.
    internal readonly struct RtssFpsData
    {
        public int ProcessId { get; }
        public string ProcessName { get; }
        public string GraphicsApi { get; }
        public double Fps { get; }
        public double FrameTimeMs { get; }
        public double OnePercentLowFps { get; }
        public double OnePercentFrameTimeMs { get; }
        public double PointOnePercentLowFps { get; }
        public double PointOnePercentFrameTimeMs { get; }

        public RtssFpsData(int processId, string processName, string graphicsApi,
            double fps, double frameTimeMs,
            double onePercentLowFps, double onePercentFrameTimeMs,
            double pointOnePercentLowFps, double pointOnePercentFrameTimeMs)
        {
            ProcessId = processId;
            ProcessName = processName;
            GraphicsApi = graphicsApi;
            Fps = fps;
            FrameTimeMs = frameTimeMs;
            OnePercentLowFps = onePercentLowFps;
            OnePercentFrameTimeMs = onePercentFrameTimeMs;
            PointOnePercentLowFps = pointOnePercentLowFps;
            PointOnePercentFrameTimeMs = pointOnePercentFrameTimeMs;
        }
    }

    /// <summary>
    /// Reads FPS metrics from RivaTuner Statistics Server shared memory (RTSSSharedMemoryV2).
    /// Uses Win32 OpenFileMapping/MapViewOfFile because .NET's MemoryMappedFile.OpenExisting
    /// applies restrictive ACL checks that RTSS's shared memory doesn't grant.
    /// </summary>
    internal static partial class RtssReader
    {
        private const string SharedMemoryName = "RTSSSharedMemoryV2";
        private const uint ValidSignature = 0x52545353; // 'RTSS' as stored by RTSS (verified from live shared memory)
        private const int MaxPath = 260; // MAX_PATH

        // Win32 file mapping access flags
        private const uint FileMapRead = 0x0004;

        // Cached shared memory mapping to avoid OpenFileMapping/MapViewOfFile on every read.
        // These are kernel transitions; caching them eliminates 4 P/Invoke calls per sample.
        // We validate the signature on each read to detect RTSS restart/crash.
        private static IntPtr _cachedMappingHandle;
        private static IntPtr _cachedViewPtr;

        private static bool AcquireMapping(out IntPtr viewPtr)
        {
            // Try cached handle first.
            if (_cachedViewPtr != IntPtr.Zero)
            {
                // Validate the signature is still correct (RTSS may have restarted).
                try
                {
                    RtssHeader header = Marshal.PtrToStructure<RtssHeader>(_cachedViewPtr);
                    if (header.Signature == ValidSignature)
                    {
                        viewPtr = _cachedViewPtr;
                        return true;
                    }
                }
                catch
                {
                    // Cache corrupted, fall through to recreate.
                }

                // Dispose cached handles.
                ReleaseCachedMapping();
            }

            // Open fresh mapping.
            IntPtr mappingHandle = OpenFileMapping(FileMapRead, false, SharedMemoryName);
            if (mappingHandle == IntPtr.Zero)
            {
                viewPtr = IntPtr.Zero;
                return false;
            }

            viewPtr = MapViewOfFile(mappingHandle, FileMapRead, 0, 0, 0);
            if (viewPtr == IntPtr.Zero)
            {
                CloseHandle(mappingHandle);
                viewPtr = IntPtr.Zero;
                return false;
            }

            // Cache for next call.
            _cachedMappingHandle = mappingHandle;
            _cachedViewPtr = viewPtr;
            return true;
        }

        private static void ReleaseCachedMapping()
        {
            if (_cachedViewPtr != IntPtr.Zero)
            {
                UnmapViewOfFile(_cachedViewPtr);
                _cachedViewPtr = IntPtr.Zero;
            }
            if (_cachedMappingHandle != IntPtr.Zero)
            {
                CloseHandle(_cachedMappingHandle);
                _cachedMappingHandle = IntPtr.Zero;
            }
        }

        // Per-process ring buffers for frame-time percentile calculation.
        // RTSS shared memory only exposes the latest per-frame time (dwFrameTime).
        // We accumulate samples across TryRead calls and compute real 1% / 0.1% lows.
        // Each process gets its own buffer to avoid cross-process pollution on
        // multi-monitor setups where desktop compositing noise corrupts percentiles.
        private const int FrameTimeBufferSize = 8192;
        private static readonly object _perProcessLock = new();
        private static readonly Dictionary<int, PerProcessBuffer> _perProcessBuffers = new();

        private sealed class PerProcessBuffer
        {
            public double[] Timestamps = new double[FrameTimeBufferSize];
            public int Next;
            public int Count;
            public DateTime LastSeen = DateTime.UtcNow;
        }

        // Remove buffers for processes not seen in the last 30 seconds.
        private static void EvictStaleBuffers()
        {
            var now = DateTime.UtcNow;
            foreach (var kvp in _perProcessBuffers.ToList())
            {
                if (now - kvp.Value.LastSeen > TimeSpan.FromSeconds(30))
                    _perProcessBuffers.Remove(kvp.Key);
            }
        }

        [LibraryImport("kernel32.dll", EntryPoint = "OpenFileMappingW", StringMarshalling = StringMarshalling.Utf16)]
        private static partial IntPtr OpenFileMapping(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, string lpName);

        [LibraryImport("kernel32.dll")]
        private static partial IntPtr MapViewOfFile(IntPtr hFileMappingObject, uint dwDesiredAccess,
            uint dwFileOffsetHigh, uint dwFileOffsetLow, ulong dwNumberOfBytesToMap);

        [LibraryImport("kernel32.dll")]
        private static partial int UnmapViewOfFile(IntPtr lpBaseAddress);

        [LibraryImport("kernel32.dll")]
        private static partial int CloseHandle(IntPtr hObject);

        /// <summary>Quick check whether RTSS is installed and running.</summary>
        /// <remarks>
        /// Checks both the shared memory signature AND the process list. Shared memory
        /// alone is unreliable — it can linger after a crash or be inaccessible due to
        /// permissions. The process check catches cases where RTSS is running but hasn't
        /// created shared memory yet, or when the user's process lacks access.
        /// </remarks>
        public static bool IsRunning()
        {
            // Check shared memory first (fast, no process enumeration).
            bool sharedMemoryValid = CheckSharedMemory();

            // Also check for the Rtss.exe process to avoid false negatives.
            bool processRunning = CheckProcess();

            return sharedMemoryValid || processRunning;
        }

        private static bool CheckSharedMemory()
        {
            IntPtr mappingHandle = OpenFileMapping(FileMapRead, false, SharedMemoryName);
            if (mappingHandle == IntPtr.Zero)
                return false;
            IntPtr viewPtr = MapViewOfFile(mappingHandle, FileMapRead, 0, 0, 0);
            if (viewPtr == IntPtr.Zero)
            {
                CloseHandle(mappingHandle);
                return false;
            }
            try
            {
                RtssHeader header = Marshal.PtrToStructure<RtssHeader>(viewPtr);
                return header.Signature == ValidSignature;
            }
            catch
            {
                return false;
            }
            finally
            {
                UnmapViewOfFile(viewPtr);
                CloseHandle(mappingHandle);
            }
        }

        private static bool CheckProcess()
        {
            try
            {
                return Process.GetProcessesByName("Rtss").Length > 0;
            }
            catch
            {
                return false;
            }
        }

        // Debounce logging so the debug output doesn't flood at 2 Hz.
        private static readonly object _logSync = new();
        private static string? _lastLogState;
        private static int _logCount;

        private static void LogOnce(string message)
        {
            lock (_logSync)
            {
                if (message == _lastLogState)
                {
                    _logCount++;
                    if (_logCount >= 10)
                        DiagnosticLogger.LogDebug($"RTSS: {message} (repeated {_logCount}x)");
                    return;
                }

                _lastLogState = message;
                _logCount = 1;
                DiagnosticLogger.LogDebug($"RTSS: {message}");
            }
        }

        /// <summary>
        /// Try to read FPS data for the given process ID from RTSS shared memory.
        /// If the exact PID is not found, falls back to the first hooked app with FPS &gt; 0
        /// (handles the case where the foreground window is not the game itself).
        /// </summary>
        public static bool TryRead(int processId, out RtssFpsData data)
        {
            data = default;

            if (!AcquireMapping(out IntPtr viewPtr))
            {
                LogOnce("Could not acquire RTSS mapping — RTSS not running or mapping inaccessible");
                return false;
            }

            // Read header (signature already validated by AcquireMapping, but read fields).
            RtssHeader header = Marshal.PtrToStructure<RtssHeader>(viewPtr);

            if (header.AppArrOffset == 0 || header.AppEntrySize == 0 || header.AppArrSize == 0)
            {
                LogOnce($"Invalid header offsets: offset={header.AppArrOffset}, entrySize={header.AppEntrySize}, arrSize={header.AppArrSize}");
                return false;
            }

            // Scan app array — collect all apps, look for exact PID match and best fallback.
            RtssFpsData? bestFallback = null;
            List<(int Pid, string Name, int FrameTime, int ApiFlag)> allApps = new();

            for (uint i = 0; i < header.AppArrSize; i++)
            {
                IntPtr entryPtr = IntPtr.Add(viewPtr, (int)(header.AppArrOffset + i * header.AppEntrySize));
                int pid = Marshal.ReadInt32(entryPtr);
                if (pid == 0)
                    continue; // Empty slot

                string name = ReadAnsiString(entryPtr, 4, MaxPath);
                int flags = Marshal.ReadInt32(entryPtr, 4 + MaxPath);
                int dwFrameTime = Marshal.ReadInt32(entryPtr, 268 + 12); // offset 280

                allApps.Add((pid, name, dwFrameTime, flags & RtssFlags.ApiUsageMask));

                if (pid == processId)
                {
                    data = ParseAppEntry(entryPtr, (int)header.AppEntrySize, processId);
                    if (data.Fps > 0)
                        return true;
                }

                // Collect fallback: best game-like app with FPS > 0.
                if (IsGameProcess(name) && dwFrameTime > 0)
                {
                    RtssFpsData candidate = ParseAppEntry(entryPtr, (int)header.AppEntrySize, pid);
                    if (candidate.Fps > 0 && (bestFallback == null || candidate.Fps > bestFallback.Value.Fps))
                        bestFallback = candidate;
                }
            }

            // If exact PID wasn't found but we have a game entry with FPS, use it.
            if (bestFallback != null && bestFallback.Value.Fps > 0)
            {
                data = bestFallback.Value;
                LogOnce($"PID {processId} not in RTSS, using fallback [{data.ProcessId}] {System.IO.Path.GetFileName(data.ProcessName)} FPS={data.Fps:F0}");
                return true;
            }

            // Log all apps once for diagnostics (debounced by LogOnce).
            string appList = string.Join(", ", allApps.Take(5).Select(a => $"[{a.Pid}] {System.IO.Path.GetFileName(a.Name)} ft={a.FrameTime} api=0x{a.ApiFlag:X}"));
            LogOnce($"RTSS apps ({allApps.Count}): {appList}{(allApps.Count > 5 ? " ..." : "")}");

            return false;
        }

        private static bool IsGameProcess(string processName)
        {
            // Filter out known non-game processes that RTSS may hook.
            // RTSS stores the full path in szName, so extract the file name.
            if (string.IsNullOrEmpty(processName))
                return false;

            string fileName = System.IO.Path.GetFileName(processName).ToLowerInvariant();
            return fileName != "gameoverlayui64.exe" &&
                   fileName != "gameoverlayui.exe" &&
                   fileName != "steam.exe" &&
                   fileName != "egui.exe" &&
                   fileName != "rtss.exe" &&
                   fileName != "rivatuner.exe" &&
                   fileName != "msiafterburner.exe" &&
                   fileName != "afterburner.exe" &&
                   fileName != "a2guard.exe";
        }

        private static string ReadAnsiString(IntPtr basePtr, int offset, int maxLength)
        {
            try
            {
                int len = 0;
                while (len < maxLength)
                {
                    byte b = Marshal.ReadByte(basePtr, offset + len);
                    if (b == 0)
                        break;
                    len++;
                }

                if (len == 0)
                    return string.Empty;

                char[] chars = new char[len];
                for (int i = 0; i < len; i++)
                    chars[i] = (char)Marshal.ReadByte(basePtr, offset + i);
                return new string(chars);
            }
            catch
            {
                return string.Empty;
            }
        }

        private static RtssFpsData ParseAppEntry(IntPtr entryPtr, int entrySize, int expectedPid)
        {
            // RTSS_SHARED_MEMORY_APP_ENTRY layout (from RTSSharedMemory.h):
            // offset 0:    dwProcessID (DWORD)
            // offset 4:    szName[MAX_PATH] (260 bytes)
            // offset 264:  dwFlags (DWORD)
            // offset 268:  dwTime0 (DWORD)
            // offset 272:  dwTime1 (DWORD)
            // offset 276:  dwFrames (DWORD)
            // offset 280:  dwFrameTime (DWORD, microseconds)

            const int FlagsOffset = 4 + MaxPath;       // 264
            const int TimeOffset = FlagsOffset + 4;    // 268

            string processName = ReadAnsiString(entryPtr, 4, MaxPath);
            int flags = Marshal.ReadInt32(entryPtr, FlagsOffset);
            string graphicsApi = DecodeGraphicsApi(flags & RtssFlags.ApiUsageMask);

            int dwFrameTime = Marshal.ReadInt32(entryPtr, TimeOffset + 12); // offset 280

            // Calculate FPS from per-frame frame time (microseconds)
            double fps = dwFrameTime > 0 ? 1_000_000.0 / dwFrameTime : 0;
            double frameTimeMs = dwFrameTime / 1000.0;

            // Get or create the per-process ring buffer.
            PerProcessBuffer? buffer = null;
            lock (_perProcessLock)
            {
                if (!_perProcessBuffers.TryGetValue(expectedPid, out buffer))
                {
                    buffer = new PerProcessBuffer();
                    _perProcessBuffers[expectedPid] = buffer;
                }
                buffer.LastSeen = DateTime.UtcNow;

                // Feed valid frame time into this process's ring buffer.
                if (dwFrameTime > 0)
                {
                    buffer.Timestamps[buffer.Next] = frameTimeMs;
                    buffer.Next = (buffer.Next + 1) % FrameTimeBufferSize;
                    if (buffer.Count < FrameTimeBufferSize)
                        buffer.Count++;
                }

                // Periodically evict stale process buffers.
                if (_perProcessBuffers.Count > 16)
                    EvictStaleBuffers();
            }

            // Calculate real 1% and 0.1% lows from this process's ring buffer.
            double onePercentLowFps = fps;
            double onePercentFrameTimeMs = frameTimeMs;
            double pointOnePercentLowFps = fps;
            double pointOnePercentFrameTimeMs = frameTimeMs;

            if (buffer != null && buffer.Count >= 2)
            {
                try
                {
                    double[] rented = ArrayPool<double>.Shared.Rent(buffer.Count);
                    try
                    {
                        // Copy ring buffer to linear array.
                        int oldest = buffer.Count == FrameTimeBufferSize ? buffer.Next : 0;
                        for (int i = 0; i < buffer.Count; i++)
                            rented[i] = buffer.Timestamps[(oldest + i) % FrameTimeBufferSize];

                        // Sort ascending — slowest (worst) frames are at the end.
                        Array.Sort(rented, 0, buffer.Count);

                        // 1% low: average of the worst 1% of frames (highest frame times).
                        int count1Percent = Math.Max(1, (int)Math.Ceiling(buffer.Count * 0.01));
                        double sum1Percent = 0;
                        for (int i = buffer.Count - count1Percent; i < buffer.Count; i++)
                            sum1Percent += rented[i];
                        onePercentFrameTimeMs = sum1Percent / count1Percent;
                        onePercentLowFps = 1000.0 / onePercentFrameTimeMs;

                        // 0.1% low: average of the worst 0.1% of frames.
                        int count01Percent = Math.Max(1, (int)Math.Ceiling(buffer.Count * 0.001));
                        double sum01Percent = 0;
                        for (int i = buffer.Count - count01Percent; i < buffer.Count; i++)
                            sum01Percent += rented[i];
                        pointOnePercentFrameTimeMs = sum01Percent / count01Percent;
                        pointOnePercentLowFps = 1000.0 / pointOnePercentFrameTimeMs;
                    }
                    finally
                    {
                        ArrayPool<double>.Shared.Return(rented);
                    }
                }
                catch
                {
                    // Percentile calculation failure must not break the FPS read — fall back to current values.
                }
            }

            return new RtssFpsData(
                expectedPid, processName, graphicsApi,
                fps, frameTimeMs,
                onePercentLowFps, onePercentFrameTimeMs,
                pointOnePercentLowFps, pointOnePercentFrameTimeMs);
        }

        private static string DecodeGraphicsApi(int apiFlag)
        {
            return apiFlag switch
            {
                RtssFlags.D3d12 or RtssFlags.D3d12Afr => "DirectX 12",
                RtssFlags.D3d11 => "DirectX 11",
                RtssFlags.D3d10 => "DirectX 10",
                RtssFlags.D3d9 or RtssFlags.D3d9Ex => "DirectX 9",
                RtssFlags.D3d8 => "DirectX 8",
                RtssFlags.Ogl => "OpenGL",
                RtssFlags.Dd => "DirectDraw",
                RtssFlags.Vulkan => "Vulkan",
                _ => "N/A"
            };
        }
    }
}
