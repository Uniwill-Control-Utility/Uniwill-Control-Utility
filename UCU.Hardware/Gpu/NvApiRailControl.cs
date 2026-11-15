using System.Buffers.Binary;

namespace UCU.Hardware.Gpu
{
    /// <summary>
    /// The private NVAPI ids and packet layout of the runtime rail currents. Ported from
    /// MechControl-RE (its <c>nvidia_api.cpp</c> <c>Api::rails</c> / <c>control</c> /
    /// <c>rail</c> / <c>set_currents</c>) and read/written on the field machine on
    /// 2026-11-15 (status 0, readback exact).
    ///
    /// This is a different mechanism from the <c>romOverride</c> records: the records move
    /// the rail <em>policy</em> maxima, this moves the rail <em>currents</em>. Measured
    /// with everything else identical (records 250000/17021/294000/84000, EC 744=130,
    /// F7 budget 225000, request 225000, 97 % load, VRel): stock rails 175-179 W board
    /// power, 250000/70000/14026 mA rails 205-214 W.
    /// </summary>
    public static class NvApiRailLayout
    {
        /// <summary>The rail policy table: entry i sits at 0x3C + i*0x58.</summary>
        public const uint PolicyTableId = 0x67F31384;
        public const int PolicyBufferLength = 0xA70;
        public const uint PolicyVersion = 0x40A70;
        public const int PolicyEntryBase = 0x3C;
        public const int PolicyEntryStride = 0x58;

        /// <summary>The channel table: channel spec c sits at 0x28 + c*0x3C.</summary>
        public const uint ChannelsId = 0xC12EB19E;
        public const int ChannelsBufferLength = 0xCA8;
        public const uint ChannelsVersion = 0x30CA8;
        public const int ChannelsMaskOffset = 0x10;
        public const int ChannelEntryBase = 0x28;
        public const int ChannelEntryStride = 0x3C;

        /// <summary>The control packet: reads the rail currents, writes carry the new ones.</summary>
        public const uint ControlId = 0x8B3E7343;
        public const int ControlBufferLength = 0xA4C;
        public const uint ControlVersion = 0x10A4C;
        public const uint SetCurrentsId = 0xAFFC2279;

        public const int SelectionMaskOffset = 0x04;

        /// <summary>Where a rail's own packet field sits: read at 0x1C + i*0x28, write at 0x20 + i*0x28.</summary>
        public const int ReadValueBase = 0x1C;
        public const int ReadValueStride = 0x28;
        public const int ReadTypeOffset = 0x00;
        public const int ReadValueOffset = 0x04;
        public const int ControlValueBase = 0x20;
        public const int ControlValueStride = 0x28;

        public const int MaxEntries = 32;
    }

    /// <summary>The three rails the unlock and the sliders move.</summary>
    public enum GpuRail
    {
        NvVdd,
        MsVdd,
        BoardInput,
    }

    /// <summary>One rail: what it carries now (mA) and the live policy it is bounded by.</summary>
    public sealed record GpuRailValue(int? CurrentMilliamps, int? MinMilliamps, int? MaxMilliamps)
    {
        public static readonly GpuRailValue Unknown = new(null, null, null);
    }

    /// <summary>
    /// What the driver answered for the three rails, or why the answer is not usable. A
    /// refusal carries no numbers at all: a slider bounded by a guessed range is worse
    /// than one that says the driver could not be asked.
    /// </summary>
    public sealed record GpuRailLimits(GpuRailValue NvVdd, GpuRailValue MsVdd, GpuRailValue BoardInput, string? Reason)
    {
        /// <summary>Nothing read: the place a store starts before it has asked the driver.</summary>
        public static readonly GpuRailLimits None = new(GpuRailValue.Unknown, GpuRailValue.Unknown, GpuRailValue.Unknown, null);

        public bool Succeeded => Reason == null;

        public static GpuRailLimits Refused(string reason) =>
            new(GpuRailValue.Unknown, GpuRailValue.Unknown, GpuRailValue.Unknown, reason);
    }

    /// <summary>
    /// The driver's runtime rail currents (mA). The records the store stages only raise
    /// the policy maxima; these are the values the card actually runs, so they are what
    /// the unlock writes after the GPU restart and what the sliders move live.
    /// </summary>
    public interface IGpuRailControl
    {
        /// <summary>Every rail's current and the live policy range it sits in.</summary>
        GpuRailLimits Read();

        /// <summary>
        /// Sets the rails a value is given for (null = leave that rail alone), refusing
        /// anything outside the live policy range before the driver is called, and
        /// verifying every written rail reads back exactly.
        /// </summary>
        GpuRailLimits Set(int? nvvddMilliamps, int? msvddMilliamps, int? boardInputMilliamps);
    }

    /// <summary>
    /// The rail-current path, faithful to the measured reference: the rails are selected
    /// from the driver's own policy and channel tables (type 4 is the board input and
    /// needs channel unit 8; NVVDD/MSVDD are channel ids 1/16 with unit 6, and the RTX 40
    /// driver reports NVVDD as type 13), every policy range is checked, a duplicate or a
    /// missing NVVDD is a refusal, and every buffer is handed to the driver between two
    /// 4 KiB <c>0xCD</c> canary bands.
    /// </summary>
    public sealed class NvApiRailControl : IGpuRailControl
    {
        private const uint TypeNvVddSensor = 19;
        private const uint TypeNvVddPolicy = 13;
        private const uint TypeBoardInput = 4;
        private const uint EntryUnitMilliamp = 1;
        private const uint ChannelUnitMilliamp = 6;
        private const uint ChannelUnitBoardInput = 8;
        private const uint ChannelIdNvVdd = 1;
        private const uint ChannelIdMsVdd = 16;

        private readonly INvApiPowerCalls _calls;

        public NvApiRailControl()
            : this(new NvApiPowerCalls())
        {
        }

        public NvApiRailControl(INvApiPowerCalls calls) => _calls = calls;

        public GpuRailLimits Read() => Execute(null, null, null);

        public GpuRailLimits Set(int? nvvddMilliamps, int? msvddMilliamps, int? boardInputMilliamps) =>
            Execute(nvvddMilliamps, msvddMilliamps, boardInputMilliamps);

        private GpuRailLimits Execute(int? nvvdd, int? msvdd, int? boardInput)
        {
            if (!_calls.TryGetGpu(out IntPtr gpu, out string? gpuReason))
                return GpuRailLimits.Refused(gpuReason ?? "NVAPI enumerated no GPU");

            if (!TrySelect(gpu, out Selection selection, out string? reason) ||
                !TryControl(gpu, selection.Mask, out byte[] control, out reason))
            {
                return GpuRailLimits.Refused(reason!);
            }

            if (TryRead(control, selection, out reason) is not GpuRailLimits before)
                return GpuRailLimits.Refused(reason!);

            // Refused before the call: a value the driver would clamp is worse than a
            // refusal that names the rail and the range it did not fit.
            var changes = new List<RailPolicy>(3);
            if (!TryChange(GpuRail.NvVdd, selection.NvVdd, nvvdd, changes, out reason) ||
                !TryChange(GpuRail.MsVdd, selection.MsVdd, msvdd, changes, out reason) ||
                !TryChange(GpuRail.BoardInput, selection.BoardInput, boardInput, changes, out reason))
            {
                return GpuRailLimits.Refused(reason!);
            }

            if (changes.Count == 0)
                return before;

            uint selected = 0;
            foreach (RailPolicy policy in changes)
            {
                Write32(
                    control,
                    NvApiRailLayout.ControlValueBase + policy.Index * NvApiRailLayout.ControlValueStride,
                    (uint)policy.Requested);
                selected |= 1u << policy.Index;
            }

            // The write mask is the rails this call touches, never the whole selection.
            Write32(control, NvApiRailLayout.SelectionMaskOffset, selected);

            if (!TryCall(gpu, NvApiRailLayout.SetCurrentsId, control, out int status, out reason))
                return GpuRailLimits.Refused(reason!);

            if (status != 0)
                return GpuRailLimits.Refused($"the rail current write failed (status {status})");

            if (!TryControl(gpu, selection.Mask, out byte[] after, out reason) ||
                TryRead(after, selection, out reason) is not GpuRailLimits applied)
            {
                return GpuRailLimits.Refused(reason!);
            }

            // Both halves of the reference's check: what was written reads back, and no
            // rail this call did not select moved.
            foreach (RailPolicy policy in changes)
            {
                if (Current(applied, policy.Rail) != policy.Requested)
                {
                    return GpuRailLimits.Refused(
                        $"the {Name(policy.Rail)} rail reads " +
                        $"{Text(Current(applied, policy.Rail))}, not {policy.Requested} mA");
                }
            }

            foreach (GpuRail rail in new[] { GpuRail.NvVdd, GpuRail.MsVdd, GpuRail.BoardInput })
            {
                if (changes.Exists(c => c.Rail == rail))
                    continue;

                int? was = Current(before, rail);
                int? now = Current(applied, rail);
                if (was != now)
                {
                    return GpuRailLimits.Refused(
                        $"the unselected {Name(rail)} rail moved from {Text(was)} to {Text(now)}");
                }
            }

            return applied;
        }

        /// <summary>
        /// The rails' policies and the driver's channel table, from which the three rails
        /// are selected: type 4 is the board input (channel unit 8, a real channel id),
        /// everything else is NVVDD (channel id 1) or MSVDD (channel id 16) on channel
        /// unit 6. Type 13 with any other id is skipped — the reference's measured case.
        /// </summary>
        private bool TrySelect(IntPtr gpu, out Selection selection, out string? reason)
        {
            selection = default;

            byte[] policies = new byte[NvApiRailLayout.PolicyBufferLength];
            Write32(policies, 0, NvApiRailLayout.PolicyVersion);

            if (!TryCall(gpu, NvApiRailLayout.PolicyTableId, policies, out int status, out reason))
                return false;

            if (status != 0)
            {
                reason = $"the rail policy table read failed (status {status})";
                return false;
            }

            if (Read32(policies, 0) != NvApiRailLayout.PolicyVersion)
            {
                reason = "the rail policy table did not echo its version";
                return false;
            }

            byte[] channels = new byte[NvApiRailLayout.ChannelsBufferLength];
            Write32(channels, 0, NvApiRailLayout.ChannelsVersion);

            if (!TryCall(gpu, NvApiRailLayout.ChannelsId, channels, out status, out reason))
                return false;

            if (status != 0)
            {
                reason = $"the rail channel table read failed (status {status})";
                return false;
            }

            if (Read32(channels, 0) != NvApiRailLayout.ChannelsVersion)
            {
                reason = "the rail channel table did not echo its version";
                return false;
            }

            uint mask = Read32(policies, 4);
            uint channelMask = Read32(channels, NvApiRailLayout.ChannelsMaskOffset);
            RailPolicy? nv = null, ms = null, input = null;

            for (int i = 0; i < NvApiRailLayout.MaxEntries; i++)
            {
                if ((mask & (1u << i)) == 0)
                    continue;

                int entry = NvApiRailLayout.PolicyEntryBase + i * NvApiRailLayout.PolicyEntryStride;
                uint type = Read32(policies, entry);
                uint channel = Read32(policies, entry + 4);

                if ((type != TypeNvVddSensor && type != TypeNvVddPolicy && type != TypeBoardInput) ||
                    Read32(policies, entry + 8) != EntryUnitMilliamp ||
                    channel >= NvApiRailLayout.MaxEntries ||
                    (channelMask & (1u << (int)channel)) == 0)
                {
                    continue;
                }

                int spec = NvApiRailLayout.ChannelEntryBase + (int)channel * NvApiRailLayout.ChannelEntryStride;
                uint rawMin = Read32(policies, entry + 12);
                uint rawDef = Read32(policies, entry + 16);
                uint rawMax = Read32(policies, entry + 20);

                // The reference's range(): min <= def <= max, non-zero, and inside an int.
                bool plausible = rawMin <= rawDef && rawDef <= rawMax && rawMax > 0 && rawMax <= int.MaxValue;
                int min = (int)rawMin;
                int max = (int)rawMax;

                if (type == TypeBoardInput)
                {
                    if (Read32(channels, spec + 12) != ChannelUnitBoardInput ||
                        Read32(channels, spec + 16) == uint.MaxValue)
                    {
                        continue;
                    }

                    if (input != null)
                    {
                        reason = "the rail policy table carries more than one board-input entry";
                        return false;
                    }

                    // The board input is optional: a range the driver will not honour
                    // leaves it out of the selection instead of refusing every rail.
                    if (plausible)
                        input = new RailPolicy(GpuRail.BoardInput, i, type, min, max);

                    continue;
                }

                if (Read32(channels, spec + 12) != ChannelUnitMilliamp)
                    continue;

                uint id = Read32(channels, spec + 16) & 255;
                if ((id != ChannelIdNvVdd && id != ChannelIdMsVdd) || (type == TypeNvVddPolicy && id != ChannelIdNvVdd))
                    continue;

                GpuRail rail = id == ChannelIdNvVdd ? GpuRail.NvVdd : GpuRail.MsVdd;

                if (!plausible)
                {
                    reason = $"the {Name(rail)} rail policy range is not plausible ({min}-{max} mA)";
                    return false;
                }

                if (rail == GpuRail.NvVdd)
                {
                    if (nv != null)
                    {
                        reason = "the rail policy table carries more than one NVVDD entry";
                        return false;
                    }

                    nv = new RailPolicy(rail, i, type, min, max);
                }
                else
                {
                    if (ms != null)
                    {
                        reason = "the rail policy table carries more than one MSVDD entry";
                        return false;
                    }

                    ms = new RailPolicy(rail, i, type, min, max);
                }
            }

            if (nv is not RailPolicy nvvdd)
            {
                reason = "the rail policy table carries no NVVDD entry";
                return false;
            }

            uint selectionMask = 1u << nvvdd.Index;
            if (ms is RailPolicy msvdd)
                selectionMask |= 1u << msvdd.Index;
            if (input is RailPolicy board)
                selectionMask |= 1u << board.Index;

            selection = new Selection(nvvdd, ms, input, selectionMask);
            return true;
        }

        /// <summary>One control packet, with its version and selection mask echoed back.</summary>
        private bool TryControl(IntPtr gpu, uint mask, out byte[] control, out string? reason)
        {
            control = new byte[NvApiRailLayout.ControlBufferLength];
            Write32(control, 0, NvApiRailLayout.ControlVersion);
            Write32(control, NvApiRailLayout.SelectionMaskOffset, mask);

            if (!TryCall(gpu, NvApiRailLayout.ControlId, control, out int status, out reason))
                return false;

            if (status != 0)
            {
                reason = $"the rail control packet read failed (status {status})";
                return false;
            }

            if (Read32(control, 0) != NvApiRailLayout.ControlVersion)
            {
                reason = "the rail control packet did not echo its version";
                return false;
            }

            if (Read32(control, NvApiRailLayout.SelectionMaskOffset) != mask)
            {
                reason = "the rail control packet did not echo its selection mask";
                return false;
            }

            return true;
        }

        /// <summary>Every selected rail's current and policy range, or null with a reason.</summary>
        private static GpuRailLimits? TryRead(byte[] control, Selection selection, out string? reason)
        {
            reason = null;

            if (!TryCurrent(control, selection.NvVdd, out GpuRailValue nv, out reason))
                return null;

            GpuRailValue ms = GpuRailValue.Unknown;
            if (selection.MsVdd is RailPolicy msvdd && !TryCurrent(control, msvdd, out ms, out reason))
                return null;

            GpuRailValue input = GpuRailValue.Unknown;
            if (selection.BoardInput is RailPolicy board && !TryCurrent(control, board, out input, out reason))
                return null;

            return new GpuRailLimits(nv, ms, input, null);
        }

        /// <summary>
        /// One rail's answer: the control packet must carry this rail's own policy type
        /// where its policy says, and a value inside the policy range. A mismatch is
        /// another rail's field, so its value is not ours to trust.
        /// </summary>
        private static bool TryCurrent(byte[] control, RailPolicy policy, out GpuRailValue value, out string? reason)
        {
            value = GpuRailValue.Unknown;
            reason = null;

            int at = NvApiRailLayout.ReadValueBase + policy.Index * NvApiRailLayout.ReadValueStride;
            uint type = Read32(control, at + NvApiRailLayout.ReadTypeOffset);
            uint raw = Read32(control, at + NvApiRailLayout.ReadValueOffset);

            if (type != policy.Type)
            {
                reason = $"the {Name(policy.Rail)} control entry is not that rail's policy type";
                return false;
            }

            if (raw > int.MaxValue || (int)raw < policy.Min || (int)raw > policy.Max)
            {
                reason =
                    $"the {Name(policy.Rail)} rail reads {raw} mA, outside its policy range {policy.Min}-{policy.Max} mA";
                return false;
            }

            value = new GpuRailValue((int)raw, policy.Min, policy.Max);
            return true;
        }

        private static bool TryChange(
            GpuRail rail, RailPolicy? policy, int? milliamps, List<RailPolicy> changes, out string? reason)
        {
            reason = null;

            if (milliamps is not int wanted)
                return true;

            if (policy is not RailPolicy found)
            {
                reason = $"the {Name(rail)} rail policy is not available on this driver";
                return false;
            }

            if (wanted < found.Min || wanted > found.Max)
            {
                reason =
                    $"the {Name(found.Rail)} rail {wanted} mA is outside the live rail policy {found.Min}-{found.Max} mA";
                return false;
            }

            changes.Add(found with { Requested = wanted });
            return true;
        }

        private bool TryCall(IntPtr gpu, uint id, byte[] buffer, out int status, out string? reason)
        {
            status = 0;

            if (!_calls.TryResolve(id, out NvApiGpuCall call))
            {
                reason = NvApiPowerLayout.Unavailable(id);
                return false;
            }

            return NvApiPowerRequest.TryCall(gpu, call, buffer, out status, out reason);
        }

        private static int? Current(GpuRailLimits limits, GpuRail rail) => rail switch
        {
            GpuRail.NvVdd => limits.NvVdd.CurrentMilliamps,
            GpuRail.MsVdd => limits.MsVdd.CurrentMilliamps,
            _ => limits.BoardInput.CurrentMilliamps,
        };

        private static string Name(GpuRail rail) => rail switch
        {
            GpuRail.NvVdd => "NVVDD",
            GpuRail.MsVdd => "MSVDD",
            _ => "board input",
        };

        private static string Text(int? milliamps) =>
            milliamps is int value ? $"{value} mA" : "nothing";

        private static uint Read32(byte[] buffer, int offset) =>
            BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset, 4));

        private static void Write32(byte[] buffer, int offset, uint value) =>
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset, 4), value);

        /// <summary>One selected rail: which policy entry it is and the range it allows.</summary>
        private readonly record struct RailPolicy(GpuRail Rail, int Index, uint Type, int Min, int Max, int Requested = 0);

        private readonly record struct Selection(RailPolicy NvVdd, RailPolicy? MsVdd, RailPolicy? BoardInput, uint Mask);
    }
}
