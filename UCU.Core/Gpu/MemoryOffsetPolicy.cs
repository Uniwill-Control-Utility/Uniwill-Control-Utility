using System;

namespace UCU.Core.Gpu
{
    /// <summary>What a memory-offset write left in force.</summary>
    /// <param name="RequestedMhz">The offset the profile asked for.</param>
    /// <param name="Verified">True when the readback could decide it; false when the
    /// memory clock moved pstate mid-write (or NVML could not answer), which is "unknown",
    /// not a failure.</param>
    /// <param name="Live">True when the offset is in force (or none was asked for).</param>
    /// <param name="Detail">The evidence, one line, English-keyed for hardware.txt.</param>
    public readonly record struct MemoryOffsetVerdict(int RequestedMhz, bool Verified, bool Live, string Detail)
    {
        /// <summary>"+500 MHz (live) - memory 1750 -> 1812 MHz (+62) at pstate 0".</summary>
        public string Summary =>
            (RequestedMhz > 0 ? "+" : string.Empty) + RequestedMhz + " MHz (" +
            (Live ? "live" : Verified ? "NOT in force" : "unverified") + ") - " + Detail;
    }

    /// <summary>
    /// Whether a memory clock offset is actually in force. Driver writes are accepted and
    /// then silently erased by the next pstate write (field, 2026-11-16: the V-F curve apply
    /// reset GPU-Z's memory clock from 1812.6 to 1750.1 MHz), so the only witness is the
    /// clock read back after the write.
    ///
    /// NVML reports the *current pstate's* clock, so a bare "is it above stock" test is only
    /// valid at pstate 0, where the reported maximum is the stock clock (14001 MT/s / 8 =
    /// 1750 MHz on the field card). Away from pstate 0 the comparison is the same baseline
    /// read just before the write; a pstate change between the two reads makes the answer
    /// unknowable and says so instead of guessing.
    /// </summary>
    public static class MemoryOffsetPolicy
    {
        public static MemoryOffsetVerdict Judge(
            int requestedMhz,
            int stockMaxMhz,
            int beforeMhz,
            int afterMhz,
            int pStateBefore,
            int pStateAfter)
        {
            if (requestedMhz == 0)
                return new(requestedMhz, true, true, "no memory offset requested");

            if (pStateBefore != pStateAfter)
                return new(requestedMhz, false, false,
                    $"the memory pstate moved {pStateBefore} -> {pStateAfter} around the write, so the clocks are not comparable");

            int observed = afterMhz - beforeMhz;
            if (observed != 0)
            {
                // shortcut: the offset's driver-side unit is not the MHz the slider shows
                // (measured +500 -> +62.5 MHz), so only the direction is judged here.
                bool sameDirection = Math.Sign(observed) == Math.Sign(requestedMhz);
                return sameDirection
                    ? new(requestedMhz, true, true,
                        $"memory {beforeMhz} -> {afterMhz} MHz ({(observed > 0 ? "+" : string.Empty)}{observed}) at pstate {pStateAfter}")
                    : new(requestedMhz, true, false,
                        $"memory {beforeMhz} -> {afterMhz} MHz at pstate {pStateAfter}, the wrong direction for this offset");
            }

            if (pStateAfter == 0)
            {
                // At pstate 0 the reported maximum IS the stock clock, so an offset is visible
                // as a clock off that maximum (already-live offsets show up this way too).
                int deviation = afterMhz - stockMaxMhz;
                bool live = deviation != 0 && Math.Sign(deviation) == Math.Sign(requestedMhz);
                return live
                    ? new(requestedMhz, true, true,
                        $"memory {afterMhz} MHz at pstate 0 versus the {stockMaxMhz} MHz stock clock")
                    : new(requestedMhz, true, false,
                        $"memory reads {afterMhz} MHz at pstate 0, the {stockMaxMhz} MHz stock clock");
            }

            return new(requestedMhz, false, false,
                $"the memory clock stayed at {afterMhz} MHz and a lower pstate's stock clock is not reported");
        }
    }
}
