namespace UCU.Core.Supervision
{
    /// <summary>
    /// What "the tuning UCU applied" MEANS for the features the power-tuning
    /// guardian watches (U-107, scope agreed narrow): the GPU cTGP/Dynamic
    /// Boost activation the preset pipeline writes through the EC, and the
    /// CPU PL1/PL2 pair. Each half is present only when a Custom preset
    /// applied it — stock presets write no cTGP/DB and no EC PL values (they
    /// assert the OEM mode and let the firmware's factory table rule), so a
    /// stock apply CLEARS the half: the guardian must never "heal" custom
    /// values over a preset that deliberately yields them to the firmware.
    /// </summary>
    public sealed record PowerContract
    {
        /// <summary>True when a Custom preset last applied the GPU cTGP/DB
        /// activation; the two fields below are the exact offsets it sent.</summary>
        public bool HasGpu { get; init; }

        /// <summary>The cTGP offset byte written to EC 0x0744 (targetWatts -
        /// defaultWatts, clamped 0-255 — the app computes it at apply time).</summary>
        public int GpuCtgpOffset { get; init; }

        /// <summary>The Dynamic Boost watts written to EC 0x0746 (0-25).</summary>
        public int GpuDbOffset { get; init; }

        /// <summary>True when a Custom preset last applied the CPU PL pair.</summary>
        public bool HasCpu { get; init; }

        public int Pl1Watts { get; init; }

        public int Pl2Watts { get; init; }

        public bool IsEmpty => !HasGpu && !HasCpu;

        public static readonly PowerContract Empty = new();
    }

    /// <summary>The EC's GPU power-control registers as read (0x0743/0x0744/
    /// 0x0745/0x0746, 0x0727 bit6). Raw bytes in; the expected-value rules
    /// live here, mirroring the engine's activation sequence.</summary>
    public sealed record GpuPowerTruth(byte Enable, byte Ctgp, byte Tpp, byte Db, bool CustomProfileActive);

    /// <summary>The EC's CPU PL1/PL2 readback in watts.</summary>
    public sealed record CpuPowerTruth(int Pl1, int Pl2);

    /// <summary>What the guardian should re-apply this cycle, with the
    /// human-readable drift evidence the log carries (acceptance a: the heal
    /// logs what the truth said before it healed).</summary>
    public sealed record PowerHealPlan
    {
        public bool HealGpu { get; init; }

        public int GpuCtgpOffset { get; init; }

        public int GpuDbOffset { get; init; }

        public bool HealCpu { get; init; }

        public int Pl1Watts { get; init; }

        public int Pl2Watts { get; init; }

        public string? GpuReason { get; init; }

        public string? CpuReason { get; init; }

        public bool IsClean => !HealGpu && !HealCpu;
    }

    /// <summary>
    /// Pure drift decision: compare the recorded contract against the EC's
    /// answer and decide which halves to re-apply. The GPU expected bytes
    /// mirror EcEngine.ActivateGpuPowerControl exactly (the engine writes
    /// enable = GENERAL|CTGP, plus DB when db &gt; 0; the pool byte is 255
    /// only on the DB-enabled path; the custom-profile bit arms with the
    /// activation) — the parity is pinned by tests, and a third party
    /// (Afterburner, an OEM control center) flipping any of it, clearing the
    /// enable bits, or clearing the custom-profile bit is drift.
    ///
    /// A null truth is "no answer this cycle", never drift: the guardian
    /// heals only on the firmware's word, not on missing data.
    /// </summary>
    public static class PowerContractPolicy
    {
        // The 0x0743 enable bits, mirroring EcEngine's CTGP_DB_ENABLE_* consts.
        public const byte EnableGeneral = 0x01;
        public const byte EnableDb = 0x02;
        public const byte EnableCtgp = 0x04;

        /// <summary>The enable byte ActivateGpuPowerControl leaves behind for
        /// this db offset (the DB bit only when db &gt; 0, so the NVIDIA
        /// driver sees no boost to chase on a db-disabled preset).</summary>
        public static byte ExpectedGpuEnable(int dbOffset) =>
            (byte)(EnableGeneral | EnableCtgp | (dbOffset > 0 ? EnableDb : 0));

        /// <summary>The total-power-pool byte (0x0745): 255 on the
        /// DB-enabled path, 0 otherwise.</summary>
        public static byte ExpectedGpuTpp(int dbOffset) => (byte)(dbOffset > 0 ? 255 : 0);

        public static PowerHealPlan ComputeHealPlan(PowerContract contract, GpuPowerTruth? gpu, CpuPowerTruth? cpu)
        {
            var reasons = new List<string>();

            bool healGpu = false;
            if (contract.HasGpu && gpu is not null)
            {
                byte wantEnable = ExpectedGpuEnable(contract.GpuDbOffset);
                byte wantTpp = ExpectedGpuTpp(contract.GpuDbOffset);

                if (!gpu.CustomProfileActive)
                    reasons.Add("custom-profile bit (0x0727 bit6) cleared");
                if (gpu.Enable != wantEnable)
                    reasons.Add($"enable 0x{gpu.Enable:X2} (want 0x{wantEnable:X2})");
                if (gpu.Ctgp != contract.GpuCtgpOffset)
                    reasons.Add($"cTGP 0x{gpu.Ctgp:X2} (want 0x{contract.GpuCtgpOffset:X2})");
                if (gpu.Tpp != wantTpp)
                    reasons.Add($"pool 0x{gpu.Tpp:X2} (want 0x{wantTpp:X2})");
                if (gpu.Db != contract.GpuDbOffset)
                    reasons.Add($"DB 0x{gpu.Db:X2} (want 0x{contract.GpuDbOffset:X2})");

                healGpu = reasons.Count > 0;
            }

            var gpuReasons = reasons.Count > 0 ? string.Join("; ", reasons) : null;
            reasons.Clear();

            bool healCpu = false;
            if (contract.HasCpu && cpu is not null)
            {
                if (cpu.Pl1 != contract.Pl1Watts)
                    reasons.Add($"PL1 {cpu.Pl1}W (want {contract.Pl1Watts}W)");
                if (cpu.Pl2 != contract.Pl2Watts)
                    reasons.Add($"PL2 {cpu.Pl2}W (want {contract.Pl2Watts}W)");

                healCpu = reasons.Count > 0;
            }

            return new PowerHealPlan
            {
                HealGpu = healGpu,
                GpuCtgpOffset = contract.GpuCtgpOffset,
                GpuDbOffset = contract.GpuDbOffset,
                HealCpu = healCpu,
                Pl1Watts = contract.Pl1Watts,
                Pl2Watts = contract.Pl2Watts,
                GpuReason = gpuReasons,
                CpuReason = reasons.Count > 0 ? string.Join("; ", reasons) : null,
            };
        }
    }
}
