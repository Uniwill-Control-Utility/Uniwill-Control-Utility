using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace Uniwill_Control_Utility.Services
{
    /// <summary>A physical core and where it sits.</summary>
    /// <param name="Index">0-based physical core index, ordered by lowest logical processor.</param>
    /// <param name="CcdIndex">The CCD (L3 instance) this core belongs to.</param>
    /// <param name="IndexInCcd">0-based position inside its CCD - the number a per-core
    /// slider is grouped under.</param>
    /// <param name="LogicalProcessors">The OS logical processors of this core (2 with SMT,
    /// and only the ones that exist - a BIOS-disabled CCD simply has no records).</param>
    public sealed record CpuCore(
        int Index,
        int CcdIndex,
        int IndexInCcd,
        IReadOnlyList<int> LogicalProcessors,
        bool HyperThreaded);

    /// <summary>One CCD, identified by its L3 instance.</summary>
    /// <param name="L3Bytes">The L3 this CCD owns. A plain Zen 4/5 CCD reports 32 MB; a
    /// 3D V-Cache CCD reports 96 MB or more.</param>
    /// <param name="IsX3D">Derived from <paramref name="L3Bytes"/>; see
    /// <see cref="CpuTopologyReader.X3DL3ThresholdBytes"/>.</param>
    public sealed record Ccd(int Index, long L3Bytes, bool IsX3D, IReadOnlyList<CpuCore> Cores);

    /// <summary>
    /// The CPU's CCD/core layout as the operating system reports it.
    ///
    /// The source is <c>GetLogicalProcessorInformationEx</c>, not CPUID: measured on the
    /// field machine (Ryzen 9 9955HX3D, 2026-11-14), CPUID leaf 0x8000001E reported NodeId 0
    /// for all 16 cores and a flat APIC-id range, so it carries no CCD information at all,
    /// while the cache records split cleanly on the L3 instance boundary
    /// (96 MB / mask 0x0000FFFF vs 32 MB / mask 0xFFFF0000).
    ///
    /// Disabled hardware needs no special case: a CCD turned off in the BIOS has no cores
    /// and no cache record, so it is absent from this layout by construction.
    /// </summary>
    public sealed record CpuTopology(IReadOnlyList<Ccd> Ccds)
    {
        public static CpuTopology Empty { get; } = new(Array.Empty<Ccd>());

        /// <summary>Physical cores across all active CCDs - the length of a per-core map.</summary>
        public int CoreCount => Ccds.Sum(ccd => ccd.Cores.Count);

        public bool HasX3D => Ccds.Any(ccd => ccd.IsX3D);

        public IEnumerable<CpuCore> Cores => Ccds.SelectMany(ccd => ccd.Cores);
    }

    /// <summary>
    /// Reads the CCD/core layout. <see cref="Build"/> is the testable half: it turns
    /// processor/cache masks into the layout, and every topology case (2x8, 2x6, 1x8, one
    /// CCD fused off) is a test over synthetic masks. <see cref="Read"/> is the thin native
    /// half and only marshals what <see cref="Build"/> consumes.
    /// </summary>
    public static class CpuTopologyReader
    {
        /// <summary>
        /// L3 size at or above which a CCD is a 3D V-Cache die. Zen 4/5 plain CCDs report
        /// 32 MB; the stacked die reports 96 MB (or 64 MB on the 6-core X3D parts). A part
        /// whose firmware reports something else degrades to "not X3D", which only affects
        /// the label and the advisory text - never the offsets.
        /// </summary>
        public const long X3DL3ThresholdBytes = 64L * 1024 * 1024;

        private const int RelationProcessorCore = 0;
        private const int RelationCache = 2;
        private const int CacheLevelL3 = 3;
        private const int GroupAffinitySize = 16;
        private const int ProcessorMaskOffset = 32;
        private const int CacheMaskOffset = 40;

        /// <summary>
        /// Builds the layout from raw masks. <paramref name="cores"/> is
        /// (logical-processor mask, SMT flag) per physical core; <paramref name="l3Caches"/>
        /// is (mask, size) per L3 instance. Cores that no L3 claims fall into a synthetic
        /// CCD with no cache - the fallback when the OS reports no Level 3 records at all.
        /// </summary>
        public static CpuTopology Build(
            IEnumerable<(ulong Mask, bool HyperThreaded)> cores,
            IEnumerable<(ulong Mask, long Bytes)> l3Caches)
        {
            ArgumentNullException.ThrowIfNull(cores);
            ArgumentNullException.ThrowIfNull(l3Caches);

            var coreList = cores.Where(c => c.Mask != 0).OrderBy(c => LowestBit(c.Mask)).ToList();
            if (coreList.Count == 0)
                return CpuTopology.Empty;

            var caches = l3Caches.Where(c => c.Mask != 0).OrderBy(c => LowestBit(c.Mask)).ToList();

            // Each core belongs to the L3 instance whose mask contains its first logical
            // processor; a core claimed by none lands in a trailing synthetic CCD.
            var perCache = new List<CpuCore>?[caches.Count];
            var orphaned = new List<CpuCore>();

            foreach ((ulong mask, bool hyperThreaded) in coreList)
            {
                int cacheIndex = -1;
                for (int i = 0; i < caches.Count; i++)
                {
                    if ((caches[i].Mask & mask) != 0)
                    {
                        cacheIndex = i;
                        break;
                    }
                }

                var target = cacheIndex < 0
                    ? orphaned
                    : perCache[cacheIndex] ??= new List<CpuCore>();

                int coreIndex = perCache.Sum(list => list?.Count ?? 0) + orphaned.Count;
                target.Add(new CpuCore(
                    Index: coreIndex,
                    CcdIndex: cacheIndex < 0 ? caches.Count : cacheIndex,
                    IndexInCcd: target.Count,
                    LogicalProcessors: SetBits(mask),
                    HyperThreaded: hyperThreaded));
            }

            var ccds = new List<Ccd>(caches.Count + (orphaned.Count > 0 ? 1 : 0));
            for (int i = 0; i < caches.Count; i++)
            {
                var coresOfCcd = perCache[i];
                if (coresOfCcd is null || coresOfCcd.Count == 0)
                    continue;

                int index = ccds.Count;
                ccds.Add(new Ccd(
                    Index: index,
                    L3Bytes: caches[i].Bytes,
                    IsX3D: caches[i].Bytes >= X3DL3ThresholdBytes,
                    Cores: coresOfCcd.Select(core => core with { CcdIndex = index }).ToList()));
            }

            if (orphaned.Count > 0)
            {
                int index = ccds.Count;
                ccds.Add(new Ccd(
                    Index: index,
                    L3Bytes: 0,
                    IsX3D: false,
                    Cores: orphaned.Select(core => core with { CcdIndex = index }).ToList()));
            }

            return new CpuTopology(ccds);
        }

        /// <summary>Reads the layout from the OS. Returns <see cref="CpuTopology.Empty"/>
        /// when the topology query fails, so callers gate the feature rather than guess.</summary>
        public static CpuTopology Read()
        {
            try
            {
                var cores = new List<(ulong, bool)>();
                var caches = new List<(ulong, long)>();

                Apply(RelationProcessorCore, (relationship, entry, size) =>
                {
                    if (relationship != RelationProcessorCore)
                        return;

                    byte flags = Marshal.ReadByte(entry, 8);
                    foreach (ulong mask in ReadMasks(entry, ProcessorMaskOffset, entryMaskCount: (ushort)Marshal.ReadInt16(entry, 30)))
                        cores.Add((mask, (flags & 2) != 0));
                });

                Apply(RelationCache, (relationship, entry, size) =>
                {
                    if (relationship != RelationCache)
                        return;
                    if (Marshal.ReadByte(entry, 8) != CacheLevelL3)
                        return;

                    long bytes = Marshal.ReadInt32(entry, 12);
                    int count = Math.Max(1, (size - CacheMaskOffset) / GroupAffinitySize);
                    foreach (ulong mask in ReadMasks(entry, CacheMaskOffset, count))
                        caches.Add((mask, bytes));
                });

                return Build(cores, caches);
            }
            catch (Exception)
            {
                // A topology query that throws is a machine we cannot describe; the caller
                // shows the feature as unavailable rather than inventing a layout.
                return CpuTopology.Empty;
            }
        }

        private static void Apply(int relationshipType, Action<int, IntPtr, int> perEntry)
        {
            uint length = 0;
            GetLogicalProcessorInformationEx(relationshipType, IntPtr.Zero, ref length);
            if (length == 0)
                return;

            IntPtr buffer = Marshal.AllocHGlobal((int)length);
            try
            {
                if (!GetLogicalProcessorInformationEx(relationshipType, buffer, ref length))
                    return;

                int offset = 0;
                while (offset < length)
                {
                    IntPtr entry = IntPtr.Add(buffer, offset);
                    int relationship = Marshal.ReadInt32(entry);
                    int size = Marshal.ReadInt32(entry, 4);
                    if (size <= 0)
                        return;

                    perEntry(relationship, entry, size);
                    offset += size;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static IEnumerable<ulong> ReadMasks(IntPtr entry, int offset, int entryMaskCount)
        {
            for (int g = 0; g < entryMaskCount; g++)
                yield return (ulong)Marshal.ReadInt64(entry, offset + g * GroupAffinitySize);
        }

        private static int LowestBit(ulong mask)
        {
            int bit = 0;
            while (bit < 64 && (mask & (1UL << bit)) == 0)
                bit++;
            return bit;
        }

        private static IReadOnlyList<int> SetBits(ulong mask)
        {
            var bits = new List<int>();
            for (int i = 0; i < 64; i++)
            {
                if ((mask & (1UL << i)) != 0)
                    bits.Add(i);
            }
            return bits;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetLogicalProcessorInformationEx(
            int relationshipType, IntPtr buffer, ref uint returnedLength);
    }
}
