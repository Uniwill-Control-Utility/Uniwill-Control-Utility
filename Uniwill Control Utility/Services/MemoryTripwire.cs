using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Serilog;

namespace Uniwill_Control_Utility.Services
{
    /// <summary>
    /// TEMPORARY DIAGNOSTIC (added 2026-11-12, remove once the owner is known).
    ///
    /// One session held 5.7 GB of private commit - six identical 794 MB private regions -
    /// while the managed heap was 41 MB, GC committed 101 MB and GPU counters showed 69 MB.
    /// It has not reproduced from a clean start (7.5 min with minimize/restore, a resize
    /// storm, gcdump and counters all stayed near 280 MB), so the next occurrence has to
    /// name itself: this samples the commit charge every 30 s and, when it is large or has
    /// just jumped, logs the delta, the biggest private regions and WPF's render mode.
    ///
    /// Delete this file and the MemoryTripwire.Start() call in MainWindow once the cause is
    /// identified. Nothing else references it.
    /// </summary>
    internal static class MemoryTripwire
    {
        private const long WarnThresholdBytes = 1200L * 1024 * 1024;
        private const long JumpThresholdBytes = 64L * 1024 * 1024;

        /// <summary>
        /// A burst that allocates and frees inside one sampling window leaves the commit
        /// line flat - but GC.GetTotalAllocatedBytes never forgets. 256 MB of allocation in
        /// 30 s is well above this app's idle churn (~10 MB) and catches the shape the
        /// 5.7 GB case must have had.
        /// </summary>
        private const long AllocationBurstBytes = 256L * 1024 * 1024;

        private const uint MemCommit = 0x1000;
        private const uint MemPrivate = 0x20000;
        private const long BytesPerMb = 1024L * 1024L;

        private static Timer? _timer;
        private static long _lastCommit;
        private static long _lastAllocated;

        public static void Start()
        {
            if (_timer is not null)
                return;

            _lastCommit = CurrentCommit();
            _lastAllocated = GC.GetTotalAllocatedBytes(precise: false);
            _timer = new Timer(_ => Sample(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        }

        private static long CurrentCommit()
        {
            try
            {
                using Process self = Process.GetCurrentProcess();
                return self.PrivateMemorySize64;
            }
            catch
            {
                return 0;
            }
        }

        private static long CurrentWorkingSet()
        {
            try
            {
                using Process self = Process.GetCurrentProcess();
                return self.WorkingSet64;
            }
            catch
            {
                return 0;
            }
        }

        private static void Sample()
        {
            try
            {
                long commit = CurrentCommit();
                long delta = commit - Interlocked.Exchange(ref _lastCommit, commit);
                long allocated = GC.GetTotalAllocatedBytes(precise: false);
                long allocatedDelta = allocated - Interlocked.Exchange(ref _lastAllocated, allocated);

                // Quiet unless it is big, it just moved, or the heap churned hard:
                // an idle app logs nothing.
                if (commit < WarnThresholdBytes
                    && Math.Abs(delta) < JumpThresholdBytes
                    && allocatedDelta < AllocationBurstBytes)
                {
                    return;
                }

                Log.Warning(
                    "[Mem] commit {CommitMb:N0} MB (delta {DeltaMb:+#;-#;0} MB), allocated in 30 s {AllocMb:N0} MB, " +
                    "working set {WsMb:N0} MB, render tier {Tier}, render mode {Mode}, private regions: {Regions}",
                    commit / BytesPerMb,
                    delta / BytesPerMb,
                    allocatedDelta / BytesPerMb,
                    CurrentWorkingSet() / BytesPerMb,
                    System.Windows.Media.RenderCapability.Tier >> 16,
                    System.Windows.Media.RenderOptions.ProcessRenderMode,
                    DescribeRegions());
            }
            catch
            {
                // A diagnostic must never disturb the app it is watching.
            }
        }

        /// <summary>
        /// Census of private committed regions, grouped by whole megabytes (VirtualQueryEx
        /// coalesces adjacent same-attribute regions, so a repeated same-size allocation
        /// shows up as one entry with a count). Only sizes of 16 MB and up are listed.
        /// </summary>
        private static string DescribeRegions()
        {
            IntPtr handle = GetCurrentProcess();
            var buckets = new Dictionary<long, int>();
            long total = 0;
            IntPtr address = IntPtr.Zero;
            int structSize = Marshal.SizeOf<MEMORY_BASIC_INFORMATION>();

            while (VirtualQueryEx(handle, address, out MEMORY_BASIC_INFORMATION mbi, structSize) != 0)
            {
                if (mbi.State == MemCommit && mbi.Type == MemPrivate)
                {
                    long bytes = mbi.RegionSize.ToInt64();
                    total += bytes;

                    long megabytes = bytes / BytesPerMb;
                    if (megabytes >= 16)
                        buckets[megabytes] = buckets.TryGetValue(megabytes, out int count) ? count + 1 : 1;
                }

                long next = mbi.BaseAddress.ToInt64() + mbi.RegionSize.ToInt64();
                if (next <= address.ToInt64())
                    break;

                address = new IntPtr(next);
            }

            string biggest = string.Join(", ", buckets
                .OrderByDescending(pair => pair.Key)
                .Take(8)
                .Select(pair => $"{pair.Key}MB x{pair.Value}"));

            return total == 0 ? "(none)" : $"{total / BytesPerMb}MB [{biggest}]";
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern int VirtualQueryEx(
            IntPtr process, IntPtr address, out MEMORY_BASIC_INFORMATION info, int length);

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORY_BASIC_INFORMATION
        {
            public IntPtr BaseAddress;
            public IntPtr AllocationBase;
            public uint AllocationProtect;
            public IntPtr RegionSize;
            public uint State;
            public uint Protect;
            public uint Type;
        }
    }
}
