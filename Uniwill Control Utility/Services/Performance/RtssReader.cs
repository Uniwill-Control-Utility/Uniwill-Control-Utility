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

        // Cached shared memory mapping to avoid OpenFileMapping/MapViewOfFile on every
        // read. These are kernel transitions; caching them eliminates 4 P/Invoke calls
        // per sample. We validate the signature on each read to detect RTSS restart/crash.
        //
        // _mapLock guards the cache AND every shared-memory read. Unmapping a view
        // while another thread reads it is a use-after-free, and reading offsets that
        // run past the mapping faults it: both raise AccessViolationException, which
        // modern .NET does NOT let anyone catch - the process dies (field-proven
        // 2026-10-31: RTSS force-killed mid-write left a section with a valid 'RTSS'
        // signature and array bounds beyond the mapping; the FPS sampler starting
        // during splash preload crashed the app at the 92% mark on every launch,
        // event stacks in Marshal.ReadInt32 <- TryRead and PtrToStructure <-
        // AcquireMapping). So: one lock across acquire-to-copy, and zero trust -
        // every header-derived offset is bounds-checked against the real view size
        // before any read.
        private static readonly object _mapLock = new();
        private static IntPtr _cachedMappingHandle;
        private static IntPtr _cachedViewPtr;
        private static ulong _cachedViewSize;

        // App-entry bytes this reader touches: fields through dwFrameTime (offset 280) + 4.
        internal const int EntryLayoutBytes = 284;

        // Sanity caps for header-derived array geometry (a real RTSS array is 64 entries
        // of a few hundred bytes; anything past these caps is garbage and fails closed).
        internal const uint MaxAppArrayEntries = 4096;
        internal const uint MaxAppEntrySize = 0x10000;

        /// <summary>
        /// Bounds contract of a section's app array against the mapped view size:
        /// the array must lie wholly inside the view, entries must be large enough
        /// to carry every field this reader touches, and the view must be addressable
        /// by the int offsets the scan uses. Pure so it is testable without a section.
        /// </summary>
        internal static bool LayoutFits(RtssHeader header, ulong viewSize)
        {
            ulong headerSize = (ulong)System.Runtime.InteropServices.Marshal.SizeOf<RtssHeader>();

            return viewSize > 0
                && viewSize <= int.MaxValue
                && header.AppArrOffset >= headerSize
                && header.AppArrOffset < viewSize
                && header.AppEntrySize >= EntryLayoutBytes
                && header.AppEntrySize <= MaxAppEntrySize
                && header.AppArrSize > 0
                && header.AppArrSize <= MaxAppArrayEntries
                && (ulong)header.AppArrSize * header.AppEntrySize <= viewSize - header.AppArrOffset;
        }

        /// <summary>The size of the mapped view (whole-section map), 0 when unknown.</summary>
        private static ulong ViewRegionSize(IntPtr view)
        {
            MEMORY_BASIC_INFORMATION info = default;
            UIntPtr ok = VirtualQuery(view, out info, (UIntPtr)System.Runtime.InteropServices.Marshal.SizeOf<MEMORY_BASIC_INFORMATION>());
            return ok == UIntPtr.Zero ? 0 : (ulong)info.RegionSize;
        }

        /// <summary>
        /// Acquire a shared-memory view, validated for this reader: size covers the
        /// header, the signature is live, and the app array lies wholly inside the
        /// view. A torn/garbage section fails closed (nothing cached). Caller must
        /// hold <see cref="_mapLock"/> for the whole read (the cached view is only
        /// ever unmapped under the same lock).
        /// </summary>
        private static bool AcquireMappingLocked(out IntPtr viewPtr, out ulong viewSize)
        {
            viewPtr = IntPtr.Zero;
            viewSize = 0;

            // Try cached handle first.
            if (_cachedViewPtr != IntPtr.Zero)
            {
                // Validate the signature is still correct (RTSS may have restarted).
                // Safe: the view was size-checked at map time, and the lock keeps
                // it from being unmapped under us.
                if (Marshal.ReadInt32(_cachedViewPtr) == (int)ValidSignature)
                {
                    viewPtr = _cachedViewPtr;
                    viewSize = _cachedViewSize;
                    return true;
                }

                ReleaseCachedMapping();
            }

            // Open fresh mapping.
            IntPtr mappingHandle = OpenFileMapping(FileMapRead, false, SharedMemoryName);
            if (mappingHandle == IntPtr.Zero)
                return false;

            IntPtr view = MapViewOfFile(mappingHandle, FileMapRead, 0, 0, 0);
            if (view == IntPtr.Zero)
            {
                CloseHandle(mappingHandle);
                return false;
            }

            ulong size = ViewRegionSize(view);
            if (size < (ulong)Marshal.SizeOf<RtssHeader>())
            {
                UnmapViewOfFile(view);
                CloseHandle(mappingHandle);
                return false;
            }

            RtssHeader header = Marshal.PtrToStructure<RtssHeader>(view);
            if (header.Signature != ValidSignature || !LayoutFits(header, size))
            {
                // Signature lives, layout garbage: the section is torn (RTSS died
                // mid-write). Treat it as absent and cache nothing.
                UnmapViewOfFile(view);
                CloseHandle(mappingHandle);
                return false;
            }

            // Cache for next call.
            _cachedMappingHandle = mappingHandle;
            _cachedViewPtr = view;
            _cachedViewSize = size;
            viewPtr = view;
            viewSize = size;
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
            _cachedViewSize = 0;
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

        /// <summary>
        /// MEMORY_BASIC_INFORMATION (x64 layout, current through the Windows 11
        /// 24H2 PartitionId extension: RegionSize sits at offset 24 in both).
        /// RegionSize of a mapped view is the mapped region - the whole-section
        /// map's size, the bound every shared-memory read is checked against.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORY_BASIC_INFORMATION
        {
            public IntPtr BaseAddress;
            public IntPtr AllocationBase;
            public uint AllocationProtect;
            public ushort PartitionId;
            public UIntPtr RegionSize;
            public uint State;
            public uint Protect;
            public uint Type;
        }

        [LibraryImport("kernel32.dll", EntryPoint = "VirtualQuery")]
        private static partial UIntPtr VirtualQuery(IntPtr lpAddress, out MEMORY_BASIC_INFORMATION lpBuffer, UIntPtr dwLength);

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

            // Size-then-read: a signature read is safe only inside the mapped
            // region (a torn section can be smaller than the header, and the
            // fault it produces is uncatchable).
            try
            {
                return ViewRegionSize(viewPtr) >= (ulong)Marshal.SizeOf<RtssHeader>()
                    && Marshal.ReadInt32(viewPtr) == (int)ValidSignature;
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

            // The lock spans the whole shared-memory access: the cached view is
            // only ever unmapped under it, and the header/app-array bounds were
            // validated against the view size at acquire - every ReadInt32 below
            // is in-bounds by construction (an out-of-bounds read here is an
            // uncatchable AV, see the _mapLock comment).
            lock (_mapLock)
            {
                if (!AcquireMappingLocked(out IntPtr viewPtr, out ulong viewSize))
                {
                    LogOnce("Could not acquire RTSS mapping — RTSS not running or mapping inaccessible");
                    return false;
                }

                // Read header (signature and array layout already validated).
                RtssHeader header = Marshal.PtrToStructure<RtssHeader>(viewPtr);

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
