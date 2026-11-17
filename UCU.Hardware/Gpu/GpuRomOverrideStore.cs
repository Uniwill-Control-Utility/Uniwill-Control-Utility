using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Serilog;
using UCU.Contracts;
using UCU.Core.Ec;
using UCU.Hardware.Devices;
using UCU.Hardware.Ec;

namespace UCU.Hardware.Gpu
{
    /// <summary>
    /// The driver-key side of the GPU ROM override store. Split from the store so the
    /// record logic (encode/decode, ownership, tier→value) runs in a test against a
    /// fake key: only this implementation touches HKLM.
    /// </summary>
    public interface IGpuRomRegistry
    {
        /// <summary>The present NVIDIA adapter's PnP instance id (the restart target), or null.</summary>
        string? InstanceId { get; }

        /// <summary>The adapter's VBIOS version as the records spell it ("98.03.5E.00.5D"), or null.</summary>
        string? ReadVbiosVersion();

        /// <summary>
        /// Every <c>romOverride*</c> / override-shaped value in the driver key, name →
        /// raw data. A value of another type arrives as empty data, which the store
        /// reads as malformed rather than as ours.
        /// </summary>
        IReadOnlyDictionary<string, byte[]> ReadRecords();

        void WriteRecord(string name, byte[] data);

        void DeleteRecord(string name);

        /// <summary>RegFlushKey: the write must be on disk before the GPU restarts.</summary>
        void Flush();
    }

    /// <summary>Restarts the display adapter so the driver re-reads the ROM records.</summary>
    public interface IGpuRestarter
    {
        /// <summary>True when the device restarted and the driver is reachable again.</summary>
        bool Restart(string? instanceId);
    }

    /// <summary>
    /// The driver's board-power limits (mW), for "did the unlock land". The ceiling is
    /// the constraint maximum — the number the ROM records raise. The current limit is
    /// what the EC's cTGP asks for, a separate mechanism, so it is reported and never
    /// used as the witness. Null means the driver answered nothing, which is reported
    /// as unknown, never as "not moved".
    /// </summary>
    public sealed record GpuPowerLimits(int? CeilingMilliwatts, int? CurrentMilliwatts);

    public interface IGpuPowerCeilingProbe
    {
        GpuPowerLimits Read();
    }

    /// <summary>What one start-up re-apply attempt settled, for the engine's retry loop.</summary>
    public enum GpuReapplyVerdict
    {
        /// <summary>No unlock is staged: nothing to do, at this boot or any later one.</summary>
        NoIntent,

        /// <summary>The live half is back and every witness holds.</summary>
        Applied,

        /// <summary>Another attempt can still succeed (the driver/key/NVAPI is not up yet).</summary>
        Transient,

        /// <summary>Retrying cannot help: the key, the VBIOS or the tier refuses.</summary>
        Refused,
    }

    /// <summary>One start-up attempt: the verdict and the state it read back (null for NoIntent).</summary>
    public sealed record GpuReapplyResult(GpuReapplyVerdict Verdict, GpuRomOverrideStateDto? State);

    /// <summary>What one settle-phase verification pass settled, for the engine's schedule.</summary>
    public enum GpuSettleVerdict
    {
        /// <summary>Every live witness still matches the tier: nothing was written.</summary>
        Settled,

        /// <summary>Something had drifted and was re-applied, and the tier holds again.</summary>
        Reapplied,

        /// <summary>Something drifted and the pass ends short; <see cref="GpuSettleResult.Reason"/> names the numbers.</summary>
        Drifted,

        /// <summary>The unlock is no longer staged (a restore ran): the settle phase must end.</summary>
        Stopped,
    }

    /// <summary>
    /// One settle pass: <see cref="Drift"/> lists what did not match, <see cref="Reason"/> why
    /// the pass still ends short (null when the tier holds at the end of it).
    /// </summary>
    public sealed record GpuSettleResult(GpuSettleVerdict Verdict, string? Drift, string? Reason);

    /// <summary>The four gpu.* ROM/rail verbs' target.</summary>
    public interface IGpuRomOverrideService
    {
        GpuRomOverrideStateDto GetState();

        /// <summary>true stages the tier's records and restarts the GPU; false restores stock.</summary>
        GpuRomOverrideStateDto SetEnabled(bool enabled);

        /// <summary>
        /// One attempt at re-applying the live half of a completed unlock, for engine start:
        /// a reboot keeps the records but clears the EC offset, the rails, the laptop budget
        /// and the driver's request. The verdict says whether another attempt can help —
        /// the engine retries the transient ones with backoff, because the service starts
        /// before the display driver and NVAPI are necessarily ready.
        /// </summary>
        GpuReapplyResult ReapplyAtStartup();

        /// <summary>
        /// One verification pass after a re-apply reported Applied: the EC publishes its own
        /// F7 budget asynchronously after a boot, so a raise that read back exactly can be
        /// back at the EC's value seconds later. Only the parts that drifted are written.
        /// </summary>
        GpuSettleResult SettleAtStartup();

        /// <summary>The three runtime rail currents and the live policy ranges the sliders are bounded by.</summary>
        GpuRailLimitsDto GetRailLimits();

        /// <summary>Moves the rails a value is given for (null = leave alone), live: no restart.</summary>
        GpuRailLimitsDto SetRailLimits(int? nvvddMilliamps, int? msvddMilliamps, int? boardInputMilliamps);
    }

    /// <summary>
    /// The ROM override store for the pinned VBIOS: stage the tier's seven records on
    /// the GPU's display-driver key, raise the EC's cTGP offset, restart the GPU, set the
    /// runtime rail currents, raise the laptop's own power budget, then make the driver's
    /// own power request — and restore by undoing all of it.
    ///
    /// Safety rules, all from the measured reference (MechControl-RE nvidia_store.cpp):
    /// the originals file is written (tmp + flush + move) BEFORE the first registry
    /// write so a half-written key is always recoverable; records are read back and
    /// must equal what was written; a key that already carries someone else's override
    /// (a 'VBIOS' marker, an unknown value name, a record from another VBIOS or a
    /// malformed one) is refused untouched; and every failure after the first write is
    /// reported as Uncertain, because the key state is then no longer ours to assume.
    ///
    /// The records alone deliver nothing (measured 2026-11-15): they only raise the
    /// policy ceiling, while the card keeps enforcing what the EC derives (~150 W) until
    /// a request is made against that ceiling. The request alone delivers nothing either
    /// (measured the same day): the driver enforces min(request, laptop power budget),
    /// and the EC publishes a 150 W budget. The rails are a fifth mechanism — the driver
    /// takes them through a separate NVAPI call, and with everything else identical the
    /// stock rails held the board at 175-179 W where the tier's rails reach 205-214 W.
    /// So the unlock is five moves — EC offset, records + restart, rails, budget raise,
    /// driver request — and Unlocked means all of them.
    /// </summary>
    public sealed class GpuRomOverrideStore : IGpuRomOverrideService
    {
        private readonly IGpuRomRegistry _registry;
        private readonly IGpuRestarter _restarter;
        private readonly IGpuPowerCeilingProbe _ceiling;
        private readonly IEcGpuPowerControl _ec;
        private readonly IGpuPowerRequest _request;
        private readonly INvApiPowerBudget _budget;
        private readonly IGpuRailControl _rails;
        private readonly IVbiosPolicyProbe _vbios;
        private readonly GpuPowerTier _tier;
        private readonly string _originalsPath;
        private readonly string _markerPath;

        /// <summary>
        /// The last start-up attempt's state (null before one ran, cleared by a completed
        /// apply). A boot whose re-apply never landed must not read as Unlocked: after a
        /// driver reset the rails sit at the record maxima, which no live read-back in
        /// <see cref="GetState"/> would call wrong.
        /// </summary>
        private volatile GpuRomOverrideStateDto? _startup;

        public GpuRomOverrideStore(
            IGpuRomRegistry registry,
            IGpuRestarter restarter,
            IGpuPowerCeilingProbe ceiling,
            IEcGpuPowerControl ec,
            IGpuPowerRequest request,
            INvApiPowerBudget budget,
            IGpuRailControl rails,
            string? originalsPath = null,
            GpuPowerTier? tier = null,
            IVbiosPolicyProbe? vbios = null)
        {
            _registry = registry;
            _restarter = restarter;
            _ceiling = ceiling;
            _ec = ec;
            _request = request;
            _budget = budget;
            _rails = rails;
            _vbios = vbios ?? new NvApiVbiosPolicyProbe();
            _tier = tier ?? GpuPowerTiers.ExtraGpuPower;
            _originalsPath = originalsPath ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "UCU",
                "gpu-rom-overrides",
                "originals.txt");

            // The intent marker, beside the originals. It is only an informational copy of
            // the intent, written by a completed unlock: nothing reads it to decide anything,
            // because a boot can lose it (measured 2026-11-17 — the file was gone while the
            // records and originals survived, and the unlock stopped re-applying).
            _markerPath = Path.Combine(Path.GetDirectoryName(_originalsPath) ?? string.Empty, "enabled");
        }

        /// <summary>
        /// The state, plus the VBIOS policy report. The probe is read-only and runs on
        /// demand (the diagnostics export asks for it), never on a write path.
        /// </summary>
        public GpuRomOverrideStateDto GetState() =>
            ReadState() with { VbiosPolicy = VbiosPolicyDto(_vbios.Read()) };

        private static GpuVbiosPolicyDto VbiosPolicyDto(VbiosPolicyState probe) => new()
        {
            Reason = probe.Reason,
            VbiosVersion = probe.Version,
            Pinned = RomOverrideLayout.IsPinned(probe.Version),
            TableOffset = probe.Succeeded ? probe.TableOffset : (int?)null,
            TableHeader = probe.TableHeader,
            PciDevice = probe.PciDevice,
            Power = LimitsDto(probe.Power),
            NvVdd = LimitsDto(probe.NvVdd),
            MsVdd = LimitsDto(probe.MsVdd),
            BoardInput = LimitsDto(probe.BoardInput),
            PinnedOffsets = probe.Pinned.Select(check => new GpuPinnedOffsetDto
            {
                Offset = check.Offset,
                ImageValue = check.ImageValue,
                Component = check.Component,
                TableValueOffset = check.TableValueOffset,
                TableValue = check.TableValue,
                Match = check.Matches,
            }).ToArray(),
        };

        private static GpuPolicyLimitsDto? LimitsDto(VbiosPolicyField? field) =>
            field is VbiosPolicyField parsed
                ? new GpuPolicyLimitsDto
                {
                    Min = parsed.Min,
                    Default = parsed.Default,
                    Max = parsed.Max,
                    Offset = parsed.Offset,
                }
                : null;

        private GpuRomOverrideStateDto ReadState()
        {
            GpuPowerLimits limits = _ceiling.Read();
            GpuPowerRequestState request = _request.Read();
            GpuPowerBudgetState budget = _budget.Read();
            GpuRailLimits rails = _rails.Read();

            try
            {
                string? version = _registry.ReadVbiosVersion();
                if (version == null)
                    return State(GpuRomOverrideStates.Unsupported, "the display driver key carries no VBIOS version", null, limits, request, budget: budget, rails: rails);

                if (!RomOverrideLayout.IsPinned(version))
                    return State(GpuRomOverrideStates.Unsupported, UnpinnedReason(version), version, limits, request, budget: budget, rails: rails);

                IReadOnlyDictionary<string, byte[]> records = _registry.ReadRecords();
                if (records.Count == 0)
                    return State(GpuRomOverrideStates.NotUnlocked, null, version, limits, request, budget: budget, rails: rails);

                if (!TryReadOriginals(out Originals? originals, out string? fileError))
                    return State(GpuRomOverrideStates.Unsupported, fileError, version, limits, request, budget: budget, rails: rails);

                if (ForeignRecordReason(records, originals) is string foreign)
                    return State(GpuRomOverrideStates.Unsupported, foreign, version, limits, request, budget: budget, rails: rails);

                if (CeilingReason(limits) is string unmoved)
                    return State(GpuRomOverrideStates.Uncertain, unmoved, version, limits, request, budget: budget, rails: rails);

                if (RequestReason(request) is string notAsked)
                    return State(GpuRomOverrideStates.Uncertain, notAsked, version, limits, request, budget: budget, rails: rails);

                if (BudgetReason(budget) is string capped)
                    return State(GpuRomOverrideStates.Uncertain, capped, version, limits, request, budget: budget, rails: rails);

                // Every live witness can hold while a start-up attempt still failed (the
                // rails it could not move read back as the record maxima): say so instead of
                // reporting an unlock the card does not carry.
                if (_startup is GpuRomOverrideStateDto unfinished &&
                    unfinished.State != GpuRomOverrideStates.Unlocked)
                {
                    return State(
                        GpuRomOverrideStates.Uncertain,
                        unfinished.Reason ?? "the start-up re-apply has not landed",
                        version, limits, request, budget: budget, rails: rails);
                }

                return State(GpuRomOverrideStates.Unlocked, null, version, limits, request, budget: budget, rails: rails);
            }
            catch (Exception ex)
            {
                Log.Information("gpu.get_rom_override_state: {Type} {Message}", ex.GetType().Name, ex.Message);
                return State(GpuRomOverrideStates.Uncertain, ex.Message, null, limits, request, budget: budget, rails: rails);
            }
        }

        public GpuRomOverrideStateDto SetEnabled(bool enabled) => enabled ? Apply() : Restore();

        /// <summary>
        /// The sliders' read: the live rail currents and the policy range each is bounded
        /// by. Read-only and reported as an answer, never as an error.
        /// </summary>
        public GpuRailLimitsDto GetRailLimits() => RailLimitsDto(_rails.Read());

        /// <summary>
        /// The sliders' write: live, no restart and no screen blank. The rail values are
        /// machine-wide, so they are never part of a preset.
        /// </summary>
        public GpuRailLimitsDto SetRailLimits(int? nvvddMilliamps, int? msvddMilliamps, int? boardInputMilliamps) =>
            RailLimitsDto(_rails.Set(nvvddMilliamps, msvddMilliamps, boardInputMilliamps));

        private static GpuRailLimitsDto RailLimitsDto(GpuRailLimits rails) => new()
        {
            NvvddMilliamps = rails.NvVdd.CurrentMilliamps,
            NvvddMinMilliamps = rails.NvVdd.MinMilliamps,
            NvvddMaxMilliamps = rails.NvVdd.MaxMilliamps,
            MsvddMilliamps = rails.MsVdd.CurrentMilliamps,
            MsvddMinMilliamps = rails.MsVdd.MinMilliamps,
            MsvddMaxMilliamps = rails.MsVdd.MaxMilliamps,
            BoardInputMilliamps = rails.BoardInput.CurrentMilliamps,
            BoardInputMinMilliamps = rails.BoardInput.MinMilliamps,
            BoardInputMaxMilliamps = rails.BoardInput.MaxMilliamps,
            Reason = rails.Reason,
        };

        private GpuRomOverrideStateDto Apply()
        {
            GpuPowerLimits limits = new(null, null);
            GpuPowerRequestState request = new(null, null, null, null);
            GpuPowerBudgetState budget = new(null, null, null);
            GpuRailLimits rails = GpuRailLimits.None;
            string? version = null;

            try
            {
                if (Check(out version, out Originals? originals, out IReadOnlyDictionary<string, byte[]> records)
                    is GpuRomOverrideStateDto refused)
                {
                    return refused;
                }

                // (a) The EC's cTGP offset, against this board's own DefaultTGP. The
                // declared TgpOffsetMax is not enforced (130 → 225 W was accepted).
                // Nothing is written yet, so a refusal here is clean.
                GpuPowerInfoDto info;
                try
                {
                    info = _ec.GetGpuPowerInfo();
                }
                catch (Exception ex)
                {
                    return State(
                        GpuRomOverrideStates.Unsupported,
                        $"the EC could not answer the GPU power table request: {ex.Message}",
                        version, limits, request);
                }

                if (!info.Supported)
                {
                    return State(
                        GpuRomOverrideStates.Unsupported,
                        $"the EC publishes no usable DefaultTGP, so the cTGP offset cannot be computed: {info.Reason}",
                        version, limits, request);
                }

                int offset = EcSpecCodec.CtgpOffsetForTgp(_tier.TgpTargetWatts, info.DefaultTgpWatts);
                _ec.ActivateGpuPowerControl(offset, 0);

                string pinned = version!;
                IReadOnlyList<(string Name, byte[] Data)> desired = RomOverrideLayout.BuildRecords(_tier, pinned);
                bool staged = Matches(records, desired);
                bool restarted = false;

                if (!staged)
                {
                    // (b) The records: originals first, then write and read back.
                    if (originals == null)
                        WriteOriginals(pinned);

                    foreach ((string name, byte[] data) in desired)
                        _registry.WriteRecord(name, data);

                    _registry.Flush();

                    IReadOnlyDictionary<string, byte[]> readback = _registry.ReadRecords();
                    foreach ((string name, byte[] data) in desired)
                    {
                        if (!readback.TryGetValue(name, out byte[]? written) || !written.AsSpan().SequenceEqual(data))
                            return State(GpuRomOverrideStates.Uncertain, $"the '{name}' record did not read back as written", version, limits, request);
                    }

                    // (c) Only a driver restart makes it re-read the records.
                    restarted = _restarter.Restart(_registry.InstanceId);
                }

                GpuRomOverrideStateDto state = ApplyLive(
                    "gpu.set_rom_override",
                    staged ? "the records were already staged; the GPU was not restarted" : null,
                    pinned, restarted, limits, request, budget, rails,
                    captureBudgetFor: originals?.Budget == null ? pinned : null);

                // The marker is an informational copy of the intent (the records and the
                // originals file are the intent); it survives a boot only sometimes, so
                // nothing may depend on it.
                if (state.State != GpuRomOverrideStates.Unlocked)
                    return state;

                _startup = null;
                WriteMarker();
                return state;
            }
            catch (Exception ex)
            {
                // Past the first registry write the key state is unknown: say so.
                Log.Information("gpu.set_rom_override: {Type} {Message}", ex.GetType().Name, ex.Message);
                return State(GpuRomOverrideStates.Uncertain, ex.Message, version, limits, request, budget: budget, rails: rails);
            }
        }

        /// <summary>
        /// The live moves after the EC offset — the runtime rails, the laptop budget raise
        /// and the driver's own request — with the read-back verdict. A fresh apply reaches
        /// this with the records just staged; the start-up re-apply reaches it with them
        /// staged by a previous boot, so the same steps and the same witnesses prove both.
        /// <paramref name="verb"/> is the log prefix, <paramref name="note"/> rides the
        /// Unlocked answer, and <paramref name="captureBudgetFor"/> is the version to
        /// capture the stock budget into the originals file for (null: never — the
        /// start-up re-apply must not rewrite the recovery file).
        /// </summary>
        private GpuRomOverrideStateDto ApplyLive(
            string verb,
            string? note,
            string version,
            bool restarted,
            GpuPowerLimits limits,
            GpuPowerRequestState request,
            GpuPowerBudgetState budget,
            GpuRailLimits rails,
            string? captureBudgetFor = null)
        {
            // (d) The runtime rail currents: a separate NVAPI call from the records,
            // and the lever that moves the board power itself (measured 2026-11-15:
            // same records, budget and request, stock rails 175-179 W, tier rails
            // 205-214 W). Written after the restart, which reloads the driver's own
            // defaults.
            rails = _rails.Set(
                _tier.NvVddCurrentMilliAmps, _tier.MsVddCurrentMilliAmps, _tier.BoardInputCurrentMilliAmps);
            if (rails.Reason is string railed)
            {
                return State(
                    GpuRomOverrideStates.Uncertain,
                    "the records are staged and the EC is offset, but the runtime rails could not be " +
                    $"set to the tier: {railed}",
                    version, limits, request, restarted, budget, rails);
            }

            // (e) The laptop's own power budget. The driver enforces
            // min(request, budget) and the EC publishes a ~150 W budget, so the
            // request alone is answered with 150 W (measured 2026-11-15) — this is
            // the lever that makes the raised ceiling reachable.
            budget = _budget.Read();
            if (budget.Budget is not GpuPowerBudget current)
            {
                return State(
                    GpuRomOverrideStates.Uncertain,
                    "the records are staged and the EC is offset, but the laptop power budget " +
                    $"could not be read: {budget.Reason}",
                    version, limits, request, restarted, budget, rails);
            }

            // The reference's budget_original_: captured once, before the first raise, and
            // kept in the originals file so a restore after an app restart still knows the
            // stock values.
            if (captureBudgetFor is string capture)
                WriteOriginals(capture, current);

            if (current.F7Milliwatts < _tier.TgpTargetMilliwatts)
            {
                if (!current.TryRaise(_tier.TgpTargetMilliwatts, out GpuPowerBudget raised))
                {
                    return State(
                        GpuRomOverrideStates.Uncertain,
                        "the records are staged and the EC is offset, but the raised laptop power budget overflows",
                        version, limits, request, restarted, budget, rails);
                }

                GpuPowerBudgetState set = _budget.Set(raised);
                if (set.Budget is not GpuPowerBudget appliedBudget)
                {
                    return State(
                        GpuRomOverrideStates.Uncertain,
                        "the records are staged and the EC is offset, but the laptop power budget " +
                        $"could not be raised to {Watts(_tier.TgpTargetMilliwatts)}: {set.Reason}",
                        version, limits, request, restarted, set, rails);
                }

                // Set only answers after its own exact F7 readback matched.
                budget = set;
                Log.Information(
                    "{Verb}: laptop budget raised {FromF7} -> {F7} mW, platform {Platform} mW",
                    verb, current.F7Milliwatts, appliedBudget.F7Milliwatts, appliedBudget.PlatformMilliwatts);
            }

            // (e) The request: without it the ceiling moves and the card stays on its
            // EC-derived limit, which is the failure this tier shipped with.
            request = _request.Request(_tier.TgpTargetMilliwatts);
            limits = _ceiling.Read();

            // (f) The rails read back: the tier's currents are what the card runs.
            rails = _rails.Read();

            Log.Information(
                "{Verb}: {Tier} staged on {Version} ({Records} records), GPU restart {Restart}, rails {Nv}/{Ms}/{Input} mA, ceiling {Ceiling} mW, current limit {Current} mW, budget {Budget} mW, request {Request} mW",
                verb, _tier.Id, version, RomOverrideLayout.Fields.Length, restarted ? "yes" : "no",
                rails.NvVdd.CurrentMilliamps?.ToString(CultureInfo.InvariantCulture) ?? "unknown",
                rails.MsVdd.CurrentMilliamps?.ToString(CultureInfo.InvariantCulture) ?? "unknown",
                rails.BoardInput.CurrentMilliamps?.ToString(CultureInfo.InvariantCulture) ?? "unknown",
                limits.CeilingMilliwatts?.ToString(CultureInfo.InvariantCulture) ?? "unknown",
                limits.CurrentMilliwatts?.ToString(CultureInfo.InvariantCulture) ?? "unknown",
                budget.F7Milliwatts?.ToString(CultureInfo.InvariantCulture) ?? "unknown",
                request.RequestedMilliwatts?.ToString(CultureInfo.InvariantCulture) ?? "none");

            // (g) All five, or it is Uncertain and the reason names the failed step.
            if (RailsReason(rails) is string notRailed)
                return State(GpuRomOverrideStates.Uncertain, notRailed, version, limits, request, restarted, budget, rails);

            if (request.Reason is string refused)
            {
                return State(
                    GpuRomOverrideStates.Uncertain,
                    $"the records are staged and the ceiling is {CeilingText(limits)}, " +
                    $"but the {Watts(_tier.TgpTargetMilliwatts)} power request failed: {refused}",
                    version, limits, request, restarted, budget, rails);
            }

            if (CeilingReason(limits) is string shortfall)
                return State(GpuRomOverrideStates.Uncertain, shortfall, version, limits, request, restarted, budget, rails);

            if (RequestReason(request) is string notAsked)
                return State(GpuRomOverrideStates.Uncertain, notAsked, version, limits, request, restarted, budget, rails);

            if (BudgetReason(budget) is string capped)
                return State(GpuRomOverrideStates.Uncertain, capped, version, limits, request, restarted, budget, rails);

            return State(GpuRomOverrideStates.Unlocked, note, version, limits, request, restarted, budget, rails);
        }

        /// <summary>
        /// One attempt at the live half of a completed unlock: a reboot keeps the seven
        /// records and the originals file, and clears everything the EC and the driver hold
        /// — the cTGP offset, the rails, the laptop budget and the driver's own request. So
        /// the four live moves are made again, with no record write and no adapter restart
        /// (that would blank the screen at every boot), and the verdict is the same read-back
        /// an apply makes.
        ///
        /// The intent is derived from what a reboot cannot take away: our records staged on
        /// the driver key, matching this build's tier, with our originals file owning them.
        /// That combination has no other cause — a restore deletes the records and the file,
        /// a virgin machine has neither — while the marker file is only an informational
        /// copy (a boot lost it once and the unlock silently stopped re-applying). The
        /// verdict tells the caller whether another attempt can help: a key that is not
        /// readable yet, the EC, or NVAPI warming up are transient; a foreign key, an
        /// unpinned VBIOS or records of another tier are not.
        /// </summary>
        public GpuReapplyResult ReapplyAtStartup()
        {
            string? version = null;
            GpuPowerLimits none = new(null, null);

            try
            {
                IReadOnlyDictionary<string, byte[]> records = _registry.ReadRecords();
                version = _registry.ReadVbiosVersion();

                // Nothing staged and no originals: no unlock was ever completed (or it was
                // restored), so there is nothing to do — not now, not two minutes from now.
                if (records.Count == 0 && !File.Exists(_originalsPath))
                    return new(GpuReapplyVerdict.NoIntent, null);

                if (version == null)
                    return Attempt(GpuReapplyVerdict.Transient, State(
                        GpuRomOverrideStates.Uncertain, "the display driver key is not readable yet", null, none));

                if (!RomOverrideLayout.IsPinned(version))
                    return Attempt(GpuReapplyVerdict.Refused, State(
                        GpuRomOverrideStates.Unsupported, UnpinnedReason(version), version, none));

                // An empty key beside an originals file is the adapter or its class key not
                // being there yet (a driver reinstall looks the same; the retry is bounded).
                if (records.Count == 0)
                    return Attempt(GpuReapplyVerdict.Transient, State(
                        GpuRomOverrideStates.NotUnlocked, "the driver key carries no records yet", version, none));

                if (!TryReadOriginals(out Originals? originals, out string? fileError))
                    return Attempt(GpuReapplyVerdict.Refused, State(
                        GpuRomOverrideStates.Unsupported, fileError, version, none));

                if (ForeignRecordReason(records, originals) is string foreign)
                    return Attempt(GpuReapplyVerdict.Refused, State(
                        GpuRomOverrideStates.Unsupported, foreign, version, none));

                if (!Matches(records, RomOverrideLayout.BuildRecords(_tier, version)))
                    return Attempt(GpuReapplyVerdict.Refused, State(
                        GpuRomOverrideStates.Uncertain,
                        $"the driver key does not carry the {_tier.Id} records", version, none));

                // The EC offset goes first, exactly as in an apply: nothing is written
                // before it, so a refusal here is clean.
                GpuPowerInfoDto info = _ec.GetGpuPowerInfo();
                if (!info.Supported)
                {
                    return Attempt(GpuReapplyVerdict.Transient, State(
                        GpuRomOverrideStates.Unsupported,
                        $"the EC publishes no usable DefaultTGP, so the cTGP offset cannot be computed: {info.Reason}",
                        version, none));
                }

                _ec.ActivateGpuPowerControl(EcSpecCodec.CtgpOffsetForTgp(_tier.TgpTargetWatts, info.DefaultTgpWatts), 0);

                GpuRomOverrideStateDto state = ApplyLive(
                    "gpu.startup", null, version, restarted: false,
                    none, new GpuPowerRequestState(null, null, null, null),
                    new GpuPowerBudgetState(null, null, null), GpuRailLimits.None);

                return Attempt(state.State == GpuRomOverrideStates.Unlocked
                    ? GpuReapplyVerdict.Applied
                    : GpuReapplyVerdict.Transient, state);
            }
            catch (Exception ex)
            {
                // Unknown, so it is given the benefit of the retry: the driver, NVAPI or the
                // EC may simply not be up yet, and the attempt is bounded by the caller.
                return Attempt(GpuReapplyVerdict.Transient, State(GpuRomOverrideStates.Uncertain, ex.Message, version, none));
            }
        }

        /// <summary>
        /// One settle pass, after a start-up re-apply reported Applied. The reference documents
        /// the hazard (MechControl-RE Power::apply): the EC/ACPI publishes its own F7 budget
        /// asynchronously after a mode switch or EC initialisation, so the raise can read back
        /// exactly and still be gone seconds later (measured 2026-11-17: 225 W at +0 s, the
        /// EC's 150 W a few seconds after). This reads the four live witnesses and writes only
        /// the ones that drifted, reusing the same store moves an apply makes.
        ///
        /// A restore during the settle phase removes the records and the originals file, so
        /// the intent is gone and the pass stops instead of putting an unlock back that the
        /// owner has just undone. A short pass leaves the start-up verdict Uncertain, which
        /// the state verb already reports.
        /// </summary>
        public GpuSettleResult SettleAtStartup()
        {
            try
            {
                if (!IntentHolds())
                    return new(GpuSettleVerdict.Stopped, null, null);

                string version = _registry.ReadVbiosVersion()!;
                var drift = new List<string>();

                // The rails: a driver reload leaves the policy maxima, so the tier's currents
                // are rewritten only when the card does not carry them.
                GpuRailLimits rails = _rails.Read();
                if (RailsReason(rails) is not null)
                {
                    drift.Add("rails");
                    rails = _rails.Set(
                        _tier.NvVddCurrentMilliAmps, _tier.MsVddCurrentMilliAmps, _tier.BoardInputCurrentMilliAmps);
                }

                // The laptop budget: the measured drift.
                GpuPowerBudgetState budget = _budget.Read();
                if (budget.Budget is GpuPowerBudget current)
                {
                    if (current.F7Milliwatts < _tier.TgpTargetMilliwatts)
                    {
                        drift.Add($"budget {current.F7Milliwatts} mW");
                        if (current.TryRaise(_tier.TgpTargetMilliwatts, out GpuPowerBudget raised))
                            budget = _budget.Set(raised);
                    }
                }
                else
                {
                    drift.Add("budget unreadable");
                }

                // The driver's own request: cleared by a driver reload.
                GpuPowerRequestState request = _request.Read();
                if (RequestReason(request) is not null)
                {
                    drift.Add("request");
                    request = _request.Request(_tier.TgpTargetMilliwatts);
                }

                // The ceiling is the records' witness, so it is read but never written here:
                // only a restart makes the driver re-read the records.
                GpuPowerLimits limits = _ceiling.Read();
                if (CeilingReason(limits) is not null)
                    drift.Add("ceiling");

                if (drift.Count == 0)
                {
                    _startup = null;
                    return new(GpuSettleVerdict.Settled, null, null);
                }

                string what = string.Join(", ", drift);
                string? shortfall =
                    BudgetReason(budget) ?? RequestReason(request) ?? CeilingReason(limits) ?? RailsReason(rails);

                if (shortfall is null)
                {
                    _startup = null;
                    Log.Information("gpu.settle: {Drift} - re-applied", what);
                    return new(GpuSettleVerdict.Reapplied, what, null);
                }

                // Still short: the state verb must not read Unlocked over it.
                _startup = State(
                    GpuRomOverrideStates.Uncertain, shortfall, version, limits, request, false, budget, rails);
                Log.Information("gpu.settle: {Drift} - {Reason}", what, shortfall);
                return new(GpuSettleVerdict.Drifted, what, shortfall);
            }
            catch (Exception ex)
            {
                Log.Information("gpu.settle: {Type} {Message}", ex.GetType().Name, ex.Message);
                return new(GpuSettleVerdict.Drifted, null, ex.Message);
            }
        }

        /// <summary>One attempt's line — every attempt, whatever it settled, is logged.</summary>
        private GpuReapplyResult Attempt(GpuReapplyVerdict verdict, GpuRomOverrideStateDto state)
        {
            Log.Information(
                "gpu.startup: {Verdict}: {State}{Reason}",
                verdict, state.State, state.Reason is null ? string.Empty : " — " + state.Reason);
            _startup = state;
            return new(verdict, state);
        }

        /// <summary>
        /// The checks every apply path makes before it writes anything: a pinned VBIOS, an
        /// originals file that can be read, and a key that carries no one else's override.
        /// Returns the refusal, or null with the three inputs the caller needs.
        /// </summary>
        private GpuRomOverrideStateDto? Check(
            out string? version,
            out Originals? originals,
            out IReadOnlyDictionary<string, byte[]> records)
        {
            version = _registry.ReadVbiosVersion();
            records = _registry.ReadRecords();
            originals = null;
            var none = new GpuPowerLimits(null, null);

            if (version == null)
                return State(GpuRomOverrideStates.Unsupported, "the display driver key carries no VBIOS version", null, none);

            if (!RomOverrideLayout.IsPinned(version))
                return State(GpuRomOverrideStates.Unsupported, UnpinnedReason(version), version, none);

            if (!TryReadOriginals(out originals, out string? fileError))
                return State(GpuRomOverrideStates.Unsupported, fileError, version, none);

            if (ForeignRecordReason(records, originals) is string foreign)
                return State(GpuRomOverrideStates.Unsupported, foreign, version, none);

            return null;
        }

        private GpuRomOverrideStateDto Restore()
        {
            string? version = null;
            GpuPowerRequestState request = new(null, null, null, null);
            GpuPowerBudgetState budget = new(null, null, null);
            GpuRailLimits rails = GpuRailLimits.None;
            string? budgetReason = null;
            string? railsReason = null;

            // A restore ends the intent, so a boot verdict about it must not outlive the
            // records: whatever happens below, the state verb reads the live machine again.
            _startup = null;

            try
            {
                version = _registry.ReadVbiosVersion();
                IReadOnlyDictionary<string, byte[]> records = _registry.ReadRecords();

                if (!TryReadOriginals(out Originals? originals, out string? fileError))
                    return State(GpuRomOverrideStates.Unsupported, fileError, version, _ceiling.Read(), _request.Read());

                // The captured budget goes back before anything else: the raise is one of
                // the staged moves, and doing it first means the F7 value never outlives
                // the records even if a later step fails. (The GPU restart below also
                // resets the budget to what the EC publishes.)
                if (originals?.Budget is GpuPowerBudget baseline)
                {
                    GpuPowerBudget applied = baseline.TryRaise(_tier.TgpTargetMilliwatts, out GpuPowerBudget raised)
                        ? raised
                        : baseline;

                    GpuPowerBudgetState restored = _budget.Restore(baseline, applied);
                    budget = restored;
                    if (!restored.Succeeded)
                        budgetReason = $"the laptop power budget was not restored: {restored.Reason}";
                }

                if (records.Count == 0)
                {
                    // Nothing staged: a no-op, and a stale originals file is dropped so the
                    // next apply writes fresh stock values. A budget or rails value that
                    // would not go back keeps the file: it is the only record of them. The
                    // rails are a runtime value the records never touched, so they are only
                    // written back when something still carries a raised current.
                    rails = _rails.Read();
                    if (RailsAboveStock(rails))
                        rails = RestoreStockRails(out railsReason);

                    // Nothing is staged any more, so no intent survives either: without this
                    // a marker whose records a driver reinstall removed would re-apply at
                    // the next boot against a key that carries nothing.
                    DiscardMarker();

                    if (originals != null)
                    {
                        if (budgetReason is null && railsReason is null)
                        {
                            DiscardOriginals();
                            return State(GpuRomOverrideStates.NotUnlocked, "no overrides were staged; the stale originals file was removed", version, _ceiling.Read(), _request.Read(), budget: budget, rails: rails);
                        }

                        return State(GpuRomOverrideStates.Uncertain, budgetReason ?? railsReason, version, _ceiling.Read(), _request.Read(), budget: budget, rails: rails);
                    }

                    return State(GpuRomOverrideStates.NotUnlocked, budgetReason ?? railsReason, version, _ceiling.Read(), _request.Read(), budget: budget, rails: rails);
                }

                if (ForeignRecordReason(records, originals) is string foreign)
                    return State(GpuRomOverrideStates.Unsupported, foreign, version, _ceiling.Read(), _request.Read(), budget: budget, rails: rails);

                foreach (string name in records.Keys)
                    _registry.DeleteRecord(name);

                _registry.Flush();

                IReadOnlyDictionary<string, byte[]> readback = _registry.ReadRecords();
                if (readback.Count != 0)
                    return State(GpuRomOverrideStates.Uncertain, "not every override record disappeared from the driver key", version, _ceiling.Read(), _request.Read(), budget: budget, rails: rails);

                // The EC byte goes back to the value captured before our activation, then
                // the rails, then the restart — which is also what clears the driver's request.
                _ec.DeactivateGpuPowerControl();

                rails = RestoreStockRails(out railsReason);

                bool restarted = _restarter.Restart(_registry.InstanceId);
                GpuPowerLimits limits = _ceiling.Read();
                request = _request.Read();

                // The restart reloads the driver, so the rails are read again: the answer
                // is what the card carries, not what this call wrote.
                GpuRailLimits after = _rails.Read();
                if (after.Succeeded)
                {
                    rails = after;
                }
                else if (railsReason is null)
                {
                    railsReason = $"the runtime rails could not be read back: {after.Reason}";
                }

                Log.Information(
                    "gpu.set_rom_override: stock restored on {Version}, GPU restart {Restart}, rails {Nv}/{Ms}/{Input} mA, ceiling {Ceiling} mW, current limit {Current} mW, budget {Budget} mW, request {Request} mW",
                    version, restarted ? "yes" : "no",
                    rails.NvVdd.CurrentMilliamps?.ToString(CultureInfo.InvariantCulture) ?? "unknown",
                    rails.MsVdd.CurrentMilliamps?.ToString(CultureInfo.InvariantCulture) ?? "unknown",
                    rails.BoardInput.CurrentMilliamps?.ToString(CultureInfo.InvariantCulture) ?? "unknown",
                    limits.CeilingMilliwatts?.ToString(CultureInfo.InvariantCulture) ?? "unknown",
                    limits.CurrentMilliwatts?.ToString(CultureInfo.InvariantCulture) ?? "unknown",
                    budget.F7Milliwatts?.ToString(CultureInfo.InvariantCulture) ?? "unknown",
                    request.RequestedMilliwatts?.ToString(CultureInfo.InvariantCulture) ?? "none");

                // The records are gone; a ceiling still above stock is the driver holding
                // the old policy, which no restart of ours accounted for.
                if (limits.CeilingMilliwatts is int ceiling && ceiling > RomOverrideLayout.StockPowerMilliwatts)
                {
                    return State(
                        GpuRomOverrideStates.Uncertain,
                        $"the records are gone but the driver still reports a {Watts(ceiling)} ceiling, not {Watts(RomOverrideLayout.StockPowerMilliwatts)}",
                        version, limits, request, restarted, budget, rails);
                }

                // A restart is what clears the request; one that survived would keep the
                // card at the raised limit with stock records.
                if (request.Reason is string unread)
                {
                    return State(
                        GpuRomOverrideStates.Uncertain,
                        $"the records are gone but the power request could not be read back: {unread}",
                        version, limits, request, restarted, budget, rails);
                }

                if (request.RequestedMilliwatts is int stillSet)
                {
                    return State(
                        GpuRomOverrideStates.Uncertain,
                        $"the records are gone but the driver still carries a {Watts(stillSet)} power request",
                        version, limits, request, restarted, budget, rails);
                }

                if (budgetReason is string notRestored)
                    return State(GpuRomOverrideStates.Uncertain, $"the records are gone but {notRestored}", version, limits, request, restarted, budget, rails);

                if (railsReason is string notRailed)
                    return State(GpuRomOverrideStates.Uncertain, $"the records are gone but {notRailed}", version, limits, request, restarted, budget, rails);

                DiscardOriginals();
                return State(GpuRomOverrideStates.NotUnlocked, null, version, limits, request, restarted, budget, rails);
            }
            catch (Exception ex)
            {
                Log.Information("gpu.set_rom_override: {Type} {Message}", ex.GetType().Name, ex.Message);
                return State(GpuRomOverrideStates.Uncertain, ex.Message, version, new GpuPowerLimits(null, null), request, budget: budget, rails: rails);
            }
        }

        private GpuRomOverrideStateDto State(
            string state,
            string? reason,
            string? version,
            GpuPowerLimits limits,
            GpuPowerRequestState? request = null,
            bool restarted = false,
            GpuPowerBudgetState? budget = null,
            GpuRailLimits? rails = null) => new()
        {
            State = state,
            Reason = reason,
            VbiosVersion = version,
            TierId = _tier.Id,
            PowerMilliwatts = _tier.PowerMilliwatts,
            NvVddMilliAmps = _tier.NvVddMilliAmps,
            MsVddMilliAmps = _tier.MsVddMilliAmps,
            BoardInputMilliAmps = _tier.BoardInputMilliAmps,
            NvvddCurrentMilliamps = rails?.NvVdd.CurrentMilliamps,
            MsvddCurrentMilliamps = rails?.MsVdd.CurrentMilliamps,
            BoardInputCurrentMilliamps = rails?.BoardInput.CurrentMilliamps,
            CeilingLimitMilliwatts = limits.CeilingMilliwatts,
            CurrentLimitMilliwatts = limits.CurrentMilliwatts,
            BudgetMilliwatts = budget?.F7Milliwatts,
            RequestedLimitMilliwatts = request?.RequestedMilliwatts,
            EnforcedLimitMoved = limits.CeilingMilliwatts is int ceiling && ceiling > RomOverrideLayout.StockPowerMilliwatts,
            Restarted = restarted,
            Enabled = IntentHolds(),
        };

        /// <summary>
        /// True when the owner's unlock is staged on this machine: our records on the driver
        /// key, matching this build's tier, with our originals file owning them. No other
        /// state produces that — a restore deletes the records and the file, a virgin machine
        /// has neither, and a key another tool wrote fails the ownership check — so it is the
        /// intent, and the marker file beside the originals is never consulted for it.
        /// </summary>
        private bool IntentHolds()
        {
            try
            {
                string? version = _registry.ReadVbiosVersion();
                if (version == null || !RomOverrideLayout.IsPinned(version))
                    return false;

                if (!TryReadOriginals(out Originals? originals, out _) || originals == null)
                    return false;

                IReadOnlyDictionary<string, byte[]> records = _registry.ReadRecords();
                return ForeignRecordReason(records, originals) == null
                    && Matches(records, RomOverrideLayout.BuildRecords(_tier, version));
            }
            catch (Exception ex)
            {
                Log.Information("gpu: the unlock intent could not be judged: {Type} {Message}", ex.GetType().Name, ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Why the ceiling is not confirmed, or null when it is. The ceiling is the
        /// number the records raise, so it is the witness the records were consumed; a
        /// null read is unknown, which is reported as such instead of as "not moved".
        /// </summary>
        private string? CeilingReason(GpuPowerLimits limits) =>
            limits.CeilingMilliwatts is not int ceiling
                ? "the driver did not report a power ceiling, so the records could not be confirmed"
                : ceiling >= _tier.TgpTargetMilliwatts
                    ? null
                    : $"the driver reports a {Watts(ceiling)} ceiling, not {Watts(_tier.TgpTargetMilliwatts)}";

        /// <summary>
        /// Why the driver's own request is not the tier's target, or null when it is.
        /// The request is what the driver enforces: a staged ceiling without it leaves
        /// the card on its EC-derived limit.
        /// </summary>
        private string? RequestReason(GpuPowerRequestState request) =>
            request.Reason is string unread
                ? $"the driver's power request could not be read: {unread}"
                : request.RequestedMilliwatts is not int requested
                    ? "the driver carries no power request, so the card stays on its EC-derived limit"
                    : requested == _tier.TgpTargetMilliwatts
                        ? null
                        : $"the driver reports a {Watts(requested)} power request, not {Watts(_tier.TgpTargetMilliwatts)}";

        private static string CeilingText(GpuPowerLimits limits) =>
            limits.CeilingMilliwatts is int ceiling ? Watts(ceiling) : "unknown";

        /// <summary>
        /// Why the laptop's own budget cannot carry the tier, or null when it can. The
        /// driver enforces min(request, budget), so a budget below the target would cap
        /// the request no matter what the ceiling says.
        /// </summary>
        private string? BudgetReason(GpuPowerBudgetState budget) =>
            budget.Budget is not GpuPowerBudget current
                ? $"the laptop power budget could not be read: {budget.Reason}"
                : current.F7Milliwatts >= _tier.TgpTargetMilliwatts
                    ? null
                    : $"the driver reports a {Watts(current.F7Milliwatts)} laptop power budget, not {Watts(_tier.TgpTargetMilliwatts)}";

        /// <summary>
        /// Why the rails are not the tier's, or null when they are. Named here rather than
        /// left to the DTO's numbers: a rail that did not take is the difference between
        /// an unlock and a 175 W card.
        /// </summary>
        private string? RailsReason(GpuRailLimits rails)
        {
            if (rails.Reason is string refused)
                return $"the runtime rails could not be read back: {refused}";

            return RailsMismatch(GpuRail.NvVdd, rails.NvVdd, _tier.NvVddCurrentMilliAmps)
                ?? RailsMismatch(GpuRail.MsVdd, rails.MsVdd, _tier.MsVddCurrentMilliAmps)
                ?? RailsMismatch(GpuRail.BoardInput, rails.BoardInput, _tier.BoardInputCurrentMilliAmps);
        }

        private static string? RailsMismatch(GpuRail rail, GpuRailValue value, int expected) =>
            value.CurrentMilliamps == expected
                ? null
                : $"the {RailName(rail)} rail reads " +
                  $"{(value.CurrentMilliamps is int current ? $"{current} mA" : "nothing")}, not the tier's {expected} mA";

        /// <summary>
        /// True when any rail still carries more than the VBIOS's own current. A read that
        /// failed is not a raise: it is left alone (the report shows the numbers as unknown).
        /// </summary>
        private static bool RailsAboveStock(GpuRailLimits rails) =>
            rails.Succeeded &&
            (AboveStock(rails.NvVdd, RomOverrideLayout.StockNvVddMilliAmps) ||
             AboveStock(rails.MsVdd, RomOverrideLayout.StockMsVddMilliAmps) ||
             AboveStock(rails.BoardInput, RomOverrideLayout.StockBoardInputMilliAmps));

        private static bool AboveStock(GpuRailValue value, int stock) =>
            value.CurrentMilliamps is int current && current > stock;

        /// <summary>Puts the rails back to the VBIOS-pinned stock currents.</summary>
        private GpuRailLimits RestoreStockRails(out string? reason)
        {
            GpuRailLimits restored = _rails.Set(
                RomOverrideLayout.StockNvVddMilliAmps,
                RomOverrideLayout.StockMsVddMilliAmps,
                RomOverrideLayout.StockBoardInputMilliAmps);

            reason = restored.Reason is string refused
                ? $"the runtime rails were not restored: {refused}"
                : null;
            return restored;
        }

        private static string RailName(GpuRail rail) => rail switch
        {
            GpuRail.NvVdd => "NVVDD",
            GpuRail.MsVdd => "MSVDD",
            _ => "board input",
        };

        private static string Watts(int milliwatts) =>
            (milliwatts / 1000.0).ToString("0.##", CultureInfo.InvariantCulture) + " W";

        private static string UnpinnedReason(string version) =>
            $"VBIOS {version} is not pinned; the ROM offsets are only known for {RomOverrideLayout.PinnedVbiosVersion}";

        /// <summary>
        /// Why the key carries a record we do not own, or null when every record is ours.
        /// Mirrors the reference's require_owned: a 'VBIOS' marker, an unknown value
        /// name, a record with a different version prefix or offset, or a malformed one
        /// all mean another writer owns this key.
        /// </summary>
        private static string? ForeignRecordReason(IReadOnlyDictionary<string, byte[]> records, Originals? originals)
        {
            foreach ((string name, byte[] data) in records)
            {
                if (string.Equals(name, "VBIOS", StringComparison.OrdinalIgnoreCase))
                    return "another tool left a 'VBIOS' override marker in the GPU driver key; restore it with its owning tool and restart the GPU first";

                if (name.Length != 13 ||
                    !name.StartsWith("romOverride", StringComparison.OrdinalIgnoreCase) ||
                    !Uri.IsHexDigit(name[11]) ||
                    !Uri.IsHexDigit(name[12]))
                {
                    return $"the GPU driver key carries the unexpected override value '{name}'; it was not written by UCU";
                }

                if (originals == null)
                    return $"the GPU driver key already carries '{name}' and no UCU originals file owns it; restore it with its owning tool first";

                if (!RomOverrideRecord.TryDecode(data, out string version, out uint offset, out _))
                    return $"'{name}' is malformed ({(data.Length == RomOverrideRecord.Length ? $"patch length {data[9]}" : $"{data.Length} bytes")})";

                if (!string.Equals(version, originals.Version, StringComparison.Ordinal) || !originals.Values.ContainsKey(offset))
                    return $"'{name}' belongs to another VBIOS or field ({version} at 0x{offset:X}); UCU will not take the key over";
            }

            return null;
        }

        private static bool Matches(IReadOnlyDictionary<string, byte[]> records, IReadOnlyList<(string Name, byte[] Data)> desired)
        {
            if (records.Count != desired.Count)
                return false;

            foreach ((string name, byte[] data) in desired)
            {
                if (!records.TryGetValue(name, out byte[]? current) || !current.AsSpan().SequenceEqual(data))
                    return false;
            }

            return true;
        }

        private bool TryReadOriginals(out Originals? originals, out string? error)
        {
            originals = null;
            error = null;

            if (!File.Exists(_originalsPath))
                return true;

            string[] lines = File.ReadAllLines(_originalsPath);
            if (lines.Length == 0)
            {
                error = "the UCU GPU originals file is empty";
                return false;
            }

            string version = lines[0].Trim();
            if (RomOverrideLayout.NormalizeVbiosVersion(version) != version)
            {
                error = "the UCU GPU originals file does not start with a VBIOS version";
                return false;
            }

            var values = new Dictionary<uint, uint>();
            int? f7 = null;
            int? platform = null;

            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0)
                    continue;

                int equal = line.IndexOf('=');
                if (equal <= 0)
                {
                    error = "the UCU GPU originals file is malformed";
                    return false;
                }

                // The captured laptop budget rides the same file (f7 / platform lines):
                // a restore after an app restart must still know the stock values.
                string name = line.Substring(0, equal).Trim();
                if (name.Equals("f7", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("platform", StringComparison.OrdinalIgnoreCase))
                {
                    if (!int.TryParse(line.AsSpan(equal + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int milliwatts) ||
                        milliwatts <= 0)
                    {
                        error = "the UCU GPU originals file is malformed";
                        return false;
                    }

                    if (name.Equals("f7", StringComparison.OrdinalIgnoreCase))
                    {
                        if (f7 != null)
                        {
                            error = "the UCU GPU originals file is malformed";
                            return false;
                        }

                        f7 = milliwatts;
                    }
                    else
                    {
                        if (platform != null)
                        {
                            error = "the UCU GPU originals file is malformed";
                            return false;
                        }

                        platform = milliwatts;
                    }

                    continue;
                }

                if (!uint.TryParse(line.AsSpan(0, equal), NumberStyles.None, CultureInfo.InvariantCulture, out uint offset) ||
                    !uint.TryParse(line.AsSpan(equal + 1), NumberStyles.None, CultureInfo.InvariantCulture, out uint value) ||
                    !values.TryAdd(offset, value))
                {
                    error = "the UCU GPU originals file is malformed";
                    return false;
                }
            }

            if (f7.HasValue != platform.HasValue)
            {
                error = "the UCU GPU originals file carries only half of the captured power budget";
                return false;
            }

            originals = new Originals(
                version,
                values,
                f7 is int budgetF7 && platform is int budgetPlatform ? new GpuPowerBudget(budgetF7, budgetPlatform) : null);
            return true;
        }

        /// <summary>
        /// The stock values, written tmp → fsync → move BEFORE the first registry write:
        /// a torn key can always be traced back to the values the ROM had. The laptop
        /// budget is only known after the GPU restart, so it is written by a second pass
        /// (same atomic write) as soon as it has been read.
        /// </summary>
        private void WriteOriginals(string version, GpuPowerBudget? budget = null)
        {
            string? directory = Path.GetDirectoryName(_originalsPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var text = new StringBuilder(version);
            foreach (RomOverrideField field in RomOverrideLayout.Fields)
            {
                text.Append('\n')
                    .Append(field.Offset.ToString(CultureInfo.InvariantCulture))
                    .Append('=')
                    .Append(field.StockValue.ToString(CultureInfo.InvariantCulture));
            }

            if (budget is GpuPowerBudget captured)
            {
                text.Append('\n')
                    .Append("f7=")
                    .Append(captured.F7Milliwatts.ToString(CultureInfo.InvariantCulture))
                    .Append('\n')
                    .Append("platform=")
                    .Append(captured.PlatformMilliwatts.ToString(CultureInfo.InvariantCulture));
            }

            string temporary = _originalsPath + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                byte[] bytes = new UTF8Encoding(false).GetBytes(text.ToString());
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, _originalsPath, overwrite: true);
            Log.Information("gpu.set_rom_override: originals written to {Path}", _originalsPath);
        }

        private void DiscardOriginals()
        {
            if (File.Exists(_originalsPath))
                File.Delete(_originalsPath);

            // The informational copy goes with them, and so does a verdict about a boot that
            // no longer has anything staged.
            DiscardMarker();
            _startup = null;
        }

        /// <summary>
        /// Records the intent beside the originals, for support bundles and for a human: a
        /// completed unlock is already readable from the records and the originals file, so
        /// no decision reads this back.
        /// </summary>
        private void WriteMarker()
        {
            string? directory = Path.GetDirectoryName(_markerPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(_markerPath, _tier.Id);
            Log.Information("gpu.set_rom_override: intent marker written to {Path}", _markerPath);
        }

        private void DiscardMarker()
        {
            if (File.Exists(_markerPath))
            {
                File.Delete(_markerPath);
                Log.Information("gpu.set_rom_override: intent marker removed from {Path}", _markerPath);
            }
        }

        private sealed record Originals(string Version, Dictionary<uint, uint> Values, GpuPowerBudget? Budget);
    }

    /// <summary>
    /// The production registry: the present NVIDIA adapter's display-driver key,
    /// located the way the reference does it — the device's own <c>Driver</c> value in
    /// the Enum key names the class subkey, so a stale duplicate adapter key on the same
    /// machine is never written by mistake.
    /// </summary>
    public sealed class DisplayDriverRomRegistry : IGpuRomRegistry
    {
        internal const string DisplayClassKey =
            @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

        private static readonly PnpDeviceFilter NvidiaDisplay = new()
        {
            DeviceClass = "Display",
            FriendlyNamePatterns = new[] { "*NVIDIA*" },
        };

        private readonly IPnpDeviceService _devices;
        private Target? _target;

        public DisplayDriverRomRegistry(IPnpDeviceService devices) => _devices = devices;

        public string? InstanceId => ResolveTarget()?.InstanceId;

        public string? ReadVbiosVersion()
        {
            // The string is a VALUE on the driver key named "HardwareInformation.BiosString"
            // (REG_SZ "Version98.3.5e.0.5d"), not a value of a HardwareInformation subkey.
            using RegistryKey? key = OpenKey(writable: false);
            return RomOverrideLayout.NormalizeVbiosVersion(key?.GetValue("HardwareInformation.BiosString") as string);
        }

        public IReadOnlyDictionary<string, byte[]> ReadRecords()
        {
            var records = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

            using RegistryKey? key = OpenKey(writable: false);
            if (key == null)
                return records;

            foreach (string name in key.GetValueNames())
            {
                if (!name.StartsWith("romOverride", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(name, "VBIOS", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // A value of a non-binary type arrives as empty data: the store reads
                // that as malformed, which is the refusal we want.
                records[name] = key.GetValue(name) as byte[] ?? Array.Empty<byte>();
            }

            return records;
        }

        public void WriteRecord(string name, byte[] data)
        {
            using RegistryKey? key = OpenKey(writable: true);
            if (key == null)
                throw new InvalidOperationException("the NVIDIA display driver key is not writable");

            key.SetValue(name, data, RegistryValueKind.Binary);
        }

        public void DeleteRecord(string name)
        {
            using RegistryKey? key = OpenKey(writable: true);
            if (key == null)
                throw new InvalidOperationException("the NVIDIA display driver key is not writable");

            key.DeleteValue(name, throwOnMissingValue: false);
        }

        public void Flush()
        {
            using RegistryKey? key = OpenKey(writable: true);
            if (key == null)
                throw new InvalidOperationException("the NVIDIA display driver key is not writable");

            // A silent no-flush would break the one guarantee the restart relies on:
            // the records are on disk before the driver re-reads them.
            key.Flush();
        }

        private RegistryKey? OpenKey(bool writable)
        {
            Target? target = ResolveTarget();
            if (target == null)
                return null;

            using RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Default);
            return baseKey.OpenSubKey(target.DriverKey, writable);
        }

        /// <summary>
        /// The resolved adapter, cached: one WMI device query per store operation, not
        /// one per record write. A null resolution is not cached, so a transient PnP
        /// enumeration failure does not disable the verbs for the process lifetime.
        /// </summary>
        private Target? ResolveTarget()
        {
            if (_target != null)
                return _target;

            return _target = Resolve();
        }

        /// <summary>
        /// The present adapter's class key. Two NVIDIA class keys exist on the field
        /// machine (one per subsystem id the ROM has reported); only the present
        /// adapter's own <c>Driver</c> pointer names the live one.
        /// </summary>
        private Target? Resolve()
        {
            PnpDeviceState? state = _devices.GetStateAsync(NvidiaDisplay).GetAwaiter().GetResult();
            if (state == null || !state.Present || state.InstanceId.Length == 0)
                return null;

            using RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Default);
            using RegistryKey? deviceKey = baseKey.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\" + state.InstanceId);
            if (deviceKey?.GetValue("Driver") is not string driver)
                return null;

            int slash = driver.IndexOf('\\');
            if (slash <= 0 || slash + 1 >= driver.Length)
                return null;

            return new Target(state.InstanceId, DisplayClassKey + @"\" + driver.Substring(slash + 1));
        }

        private sealed record Target(string InstanceId, string DriverKey);
    }

    /// <summary>
    /// <c>pnputil /restart-device</c> with a bounded wait. The display adapter only
    /// re-reads the ROM records when the driver restarts the device, and the restart
    /// blanks the screen, so it is never guessed at: a timeout or a non-zero exit is
    /// reported as a failure the caller can hand to the user.
    /// </summary>
    public sealed class PnputilGpuRestarter : IGpuRestarter
    {
        internal const int RestartTimeoutMilliseconds = 60_000;

        public bool Restart(string? instanceId)
        {
            if (string.IsNullOrEmpty(instanceId))
                throw new InvalidOperationException("no present NVIDIA display adapter to restart");

            var startInfo = new ProcessStartInfo(
                Path.Combine(Environment.SystemDirectory, "pnputil.exe"),
                $"/restart-device \"{instanceId}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
            };

            using Process? process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("pnputil.exe did not start");

            if (!process.WaitForExit(RestartTimeoutMilliseconds))
                throw new TimeoutException("the GPU restart did not finish within 60 s; restart Windows to activate the staged limits");

            if (process.ExitCode != 0)
                throw new InvalidOperationException($"pnputil /restart-device exited with {process.ExitCode}");

            return true;
        }
    }

    /// <summary>
    /// The driver's power limits through NVML (mW). Read-only, and the only witness that
    /// the new ROM maximum was actually consumed: the records can be staged and read back
    /// perfectly while the driver keeps running the old ceiling.
    /// </summary>
    public sealed class NvmlPowerCeilingProbe : IGpuPowerCeilingProbe
    {
        public GpuPowerLimits Read()
        {
            try
            {
                if (NvmlInit() != 0)
                    return new GpuPowerLimits(null, null);

                try
                {
                    // shortcut: the first NVML device; this fork's boxes are single-GPU.
                    if (NvmlDeviceGetHandleByIndex(0, out IntPtr device) != 0)
                        return new GpuPowerLimits(null, null);

                    // The ceiling the records raise is the constraint max. The current
                    // limit is what the EC's cTGP asks for — a separate mechanism, so it
                    // is reported next to the ceiling and never compared against it.
                    int constraint = NvmlDeviceGetPowerManagementLimitConstraints(device, out _, out uint maxMilliwatts);
                    int current = NvmlDeviceGetPowerManagementLimit(device, out uint limitMilliwatts);

                    return new GpuPowerLimits(
                        constraint == 0 ? (int)maxMilliwatts : null,
                        current == 0 ? (int)limitMilliwatts : null);
                }
                finally
                {
                    _ = NvmlShutdown();
                }
            }
            catch (DllNotFoundException)
            {
                return new GpuPowerLimits(null, null);
            }
            catch (EntryPointNotFoundException)
            {
                return new GpuPowerLimits(null, null);
            }
        }

        [DllImport("nvml.dll", EntryPoint = "nvmlInit_v2")]
        private static extern int NvmlInit();

        [DllImport("nvml.dll", EntryPoint = "nvmlShutdown")]
        private static extern int NvmlShutdown();

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetHandleByIndex_v2")]
        private static extern int NvmlDeviceGetHandleByIndex(uint index, out IntPtr device);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetPowerManagementLimitConstraints")]
        private static extern int NvmlDeviceGetPowerManagementLimitConstraints(
            IntPtr device,
            out uint minMilliwatts,
            out uint maxMilliwatts);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetPowerManagementLimit")]
        private static extern int NvmlDeviceGetPowerManagementLimit(IntPtr device, out uint limitMilliwatts);
    }
}
