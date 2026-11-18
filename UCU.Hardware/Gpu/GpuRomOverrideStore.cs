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

    /// <summary>What one drift-watch tick found.</summary>
    public enum GpuBudgetWatchVerdict
    {
        /// <summary>The unlock is no longer staged (a restore ran): the watch ends.</summary>
        Stopped,

        /// <summary>The budget already carries the tier target: nothing was written.</summary>
        Holding,

        /// <summary>The EC had republished a lower budget and it was raised again.</summary>
        Raised,

        /// <summary>The read or the raise failed; <see cref="GpuBudgetWatchResult.Reason"/> says why.</summary>
        Failed,
    }

    /// <summary>One drift-watch tick: the verdict and, for a failure, the reason.</summary>
    public sealed record GpuBudgetWatchResult(GpuBudgetWatchVerdict Verdict, string? Reason);

    /// <summary>The four gpu.* ROM/rail verbs' target.</summary>
    public interface IGpuRomOverrideService
    {
        GpuRomOverrideStateDto GetState();

        /// <summary>true stages the tier's records and restarts the GPU; false restores stock.</summary>
        /// <param name="targetTgpMilliwatts">
        /// The TGP to aim at, in mW (the preset's slider). Null keeps the target the last
        /// apply saved, or the tier's tested target when none was ever saved. A value below
        /// the tier's target is refused; above the ROM ceiling it is clamped.
        /// </param>
        GpuRomOverrideStateDto SetEnabled(bool enabled, int? targetTgpMilliwatts = null);

        /// <summary>
        /// The EC now carries a cTGP ceiling below the unlock's target: a preset lowered its
        /// slider, and the EC is the hardware envelope, so the target and the driver half
        /// follow it down (the target is saved, the driver's request and the laptop budget
        /// are lowered with it). It only ever lowers: a preset that raises the EC leaves the
        /// target alone, and so does one below the unlock's accepted floor — that ceiling
        /// cannot be a target, so the card's shortfall line names the gap instead.
        /// </summary>
        void ClampTargetToEcCeiling(int ecTgpMilliwatts);

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

        /// <summary>
        /// One drift-watch tick while the unlock is enabled: reads the laptop budget and
        /// re-raises it from the captured baseline when the EC has republished a lower F7.
        /// <see cref="GpuBudgetWatchVerdict.Stopped"/> when the intent is gone.
        /// </summary>
        GpuBudgetWatchResult WatchBudget();

        /// <summary>
        /// Raised when a completed apply lands, and when a state read finds the laptop budget
        /// below the target (the EC republished it and the drift watch's repair is due). Only a
        /// notification: the engine starts its drift watch from it, and nothing in the store
        /// reads it back.
        /// </summary>
        event Action? Unlocked;

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
        private readonly string _railsPath;
        private readonly string _targetPath;

        /// <summary>
        /// The last attempt's state (null before one ran, cleared by a completed apply or
        /// settle): a start-up re-apply that did not land, or an apply that stopped after its
        /// first write. A boot or a failed apply must not read as Unlocked: after a driver
        /// reset the rails sit at the record maxima, which no live read-back in
        /// <see cref="GetState"/> would call wrong.
        /// </summary>
        private volatile GpuRomOverrideStateDto? _startup;

        /// <summary>
        /// Fires when a completed apply lands, and when a state read finds the budget short;
        /// see <see cref="IGpuRomOverrideService.Unlocked"/>.
        /// </summary>
        public event Action? Unlocked;

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

            // The owner's rail currents, beside the originals: the sliders' Apply writes it, and
            // every path that sets the rails reads it instead of the tier's numbers.
            _railsPath = Path.Combine(Path.GetDirectoryName(_originalsPath) ?? string.Empty, "rails.txt");

            // The target TGP the last apply was given (the preset's slider), beside the
            // originals: the boot re-apply, the settle pass and the drift watch all aim at
            // it instead of the tier's tested number.
            _targetPath = Path.Combine(Path.GetDirectoryName(_originalsPath) ?? string.Empty, "target.txt");
        }

        /// <summary>
        /// The state, plus the VBIOS policy report. The probe is read-only and runs on
        /// demand (the diagnostics export asks for it), never on a write path.
        /// </summary>
        public GpuRomOverrideStateDto GetState()
        {
            VbiosPolicyState image = _vbios.Read();
            return ReadState(image) with { VbiosPolicy = VbiosPolicyDto(image) };
        }

        private GpuVbiosPolicyDto VbiosPolicyDto(VbiosPolicyState probe)
        {
            bool pinned = RomOverrideLayout.IsPinned(probe.Version, probe);

            return new GpuVbiosPolicyDto
            {
                Reason = probe.Reason,
                VbiosVersion = probe.Version,
                Pinned = pinned,
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

                // What an apply writes: the tier's values, unchanged — above the stock caps
                // the lines above print, which is the unlock's point.
                Records = pinned
                    ? new[]
                    {
                        RecordDto(RomOverrideRail.Power, "power", "mW"),
                        RecordDto(RomOverrideRail.NvVdd, "NVVDD", "mA"),
                        RecordDto(RomOverrideRail.MsVdd, "MSVDD", "mA"),
                        RecordDto(RomOverrideRail.BoardInput, "board input", "mA"),
                    }
                    : Array.Empty<GpuRecordValueDto>(),
            };
        }

        private GpuRecordValueDto RecordDto(RomOverrideRail rail, string name, string unit) => new()
        {
            Rail = name,
            Unit = unit,
            Value = RomOverrideLayout.ValueFor(rail, _tier),
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

        private GpuRomOverrideStateDto ReadState(VbiosPolicyState image)
        {
            GpuPowerLimits limits = _ceiling.Read();
            GpuPowerRequestState request = _request.Read();
            GpuPowerBudgetState budget = _budget.Read();
            GpuRailLimits rails = _rails.Read();
            int target = TargetMilliwatts;

            try
            {
                string? version = _registry.ReadVbiosVersion();
                if (version == null)
                    return State(GpuRomOverrideStates.Unsupported, "the display driver key carries no VBIOS version", null, limits, request, budget: budget, rails: rails);

                if (!RomOverrideLayout.IsPinned(version, image))
                    return State(GpuRomOverrideStates.Unsupported, UnpinnedReason(version, image), version, limits, request, budget: budget, rails: rails);

                IReadOnlyDictionary<string, byte[]> records = _registry.ReadRecords();
                if (records.Count == 0)
                    return State(GpuRomOverrideStates.NotUnlocked, null, version, limits, request, budget: budget, rails: rails);

                if (!TryReadOriginals(out Originals? originals, out string? fileError))
                    return State(GpuRomOverrideStates.Unsupported, fileError, version, limits, request, budget: budget, rails: rails);

                if (ForeignRecordReason(records, originals) is string foreign)
                    return State(GpuRomOverrideStates.Unsupported, foreign, version, limits, request, budget: budget, rails: rails);

                if (CeilingReason(limits, target) is string unmoved)
                    return State(GpuRomOverrideStates.Uncertain, unmoved, version, limits, request, budget: budget, rails: rails);

                if (RequestReason(request, target) is string notAsked)
                    return State(GpuRomOverrideStates.Uncertain, notAsked, version, limits, request, budget: budget, rails: rails);

                if (BudgetReason(budget, target) is string capped)
                {
                    // The EC republishes its own F7 asynchronously, so a read can land in the
                    // window before the drift watch's next tick repairs it: ask for that tick
                    // now, so the card does not report a gap the feature is about to close. Only
                    // the notification - WatchBudget stays the one write path.
                    if (budget.Budget is GpuPowerBudget held && held.F7Milliwatts < target)
                        NotifyUnlocked();

                    return State(GpuRomOverrideStates.Uncertain, capped, version, limits, request, budget: budget, rails: rails);
                }

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

                // The rails are the move the records do not make, and the one a driver reset
                // takes away: whatever else reads back intact, a card at stock rail currents
                // is not unlocked.
                if (RailsReason(rails) is string unrailed)
                    return State(GpuRomOverrideStates.Uncertain, unrailed, version, limits, request, budget: budget, rails: rails);

                return State(GpuRomOverrideStates.Unlocked, null, version, limits, request, budget: budget, rails: rails);
            }
            catch (Exception ex)
            {
                Log.Information("gpu.get_rom_override_state: {Type} {Message}", ex.GetType().Name, ex.Message);
                return State(GpuRomOverrideStates.Uncertain, ex.Message, null, limits, request, budget: budget, rails: rails);
            }
        }

        public GpuRomOverrideStateDto SetEnabled(bool enabled, int? targetTgpMilliwatts = null) =>
            enabled ? Apply(targetTgpMilliwatts) : Restore();

        public void ClampTargetToEcCeiling(int ecTgpMilliwatts)
        {
            try
            {
                int target = TargetMilliwatts;
                if (ecTgpMilliwatts >= target || ecTgpMilliwatts < RomOverrideLayout.StockPowerMilliwatts)
                    return;

                // No staged unlock: nothing holds the driver half, so there is nothing to move.
                if (!IntentHolds())
                    return;

                WriteTarget(ecTgpMilliwatts);

                // The driver enforces min(request, budget), so both follow the EC down: a
                // request left above the EC would read as a shortfall the EC is causing.
                _request.Request(ecTgpMilliwatts);

                GpuPowerBudgetState budget = _budget.Read();
                if (budget.Budget is GpuPowerBudget current &&
                    current.F7Milliwatts > ecTgpMilliwatts &&
                    TryTierBudget("gpu.ec_clamp", ecTgpMilliwatts, current, out GpuPowerBudget lowered, out _))
                {
                    _budget.Set(lowered);
                }

                Log.Information(
                    "gpu: the EC ceiling is {Ceiling}, below the unlock target {Target}: the target, the driver request and the laptop budget follow the EC",
                    Watts(ecTgpMilliwatts), Watts(target));
            }
            catch (Exception ex)
            {
                Log.Information("gpu: the EC ceiling could not be applied to the unlock target: {Type} {Message}", ex.GetType().Name, ex.Message);
            }
        }

        /// <summary>
        /// The sliders' read: the live rail currents and the policy range each is bounded
        /// by. Read-only and reported as an answer, never as an error.
        /// </summary>
        public GpuRailLimitsDto GetRailLimits() => RailLimitsDto(_rails.Read());

        /// <summary>
        /// The sliders' write: live, no restart and no screen blank. The rail values are
        /// machine-wide, so they are never part of a preset — a successful write is saved
        /// beside the originals, which is what the next boot applies.
        /// </summary>
        public GpuRailLimitsDto SetRailLimits(int? nvvddMilliamps, int? msvddMilliamps, int? boardInputMilliamps)
        {
            GpuRailLimits rails = _rails.Set(nvvddMilliamps, msvddMilliamps, boardInputMilliamps);

            // The read-back is what the owner chose; a rail the driver does not report keeps
            // this tier's value so the saved file always names all three.
            if (rails.Succeeded)
            {
                RailSelection applied = new(
                    rails.NvVdd.CurrentMilliamps ?? _tier.NvVddCurrentMilliAmps,
                    rails.MsVdd.CurrentMilliamps ?? _tier.MsVddCurrentMilliAmps,
                    rails.BoardInput.CurrentMilliamps ?? _tier.BoardInputCurrentMilliAmps);

                WriteRailSelection(applied);
            }

            return RailLimitsDto(rails);
        }

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

        private GpuRomOverrideStateDto Apply(int? targetMilliwatts)
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

                // The target: the preset's slider when one was passed, otherwise what the
                // last apply saved. The accepted range is the stock ceiling up to the tier's
                // ceiling — 200 W is a supported configuration, so the measured target is no
                // longer the floor. Outside the range a target is refused (nothing is written,
                // so the refusal is clean); above the ROM record's ceiling it is clamped,
                // because no request can be honoured there.
                int target = targetMilliwatts ?? TargetMilliwatts;
                if (target < RomOverrideLayout.StockPowerMilliwatts)
                {
                    return State(
                        GpuRomOverrideStates.Unsupported,
                        $"the requested TGP {Watts(target)} is outside the {Watts(RomOverrideLayout.StockPowerMilliwatts)} to {Watts(_tier.PowerMilliwatts)} range this unlock accepts, so nothing was applied",
                        version, limits);
                }

                target = Math.Min(target, _tier.PowerMilliwatts);

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

                int offset = EcSpecCodec.CtgpOffsetForTgp(target / 1000, info.DefaultTgpWatts);
                _ec.ActivateGpuPowerControl(offset, 0);

                // Saved with the first hardware write: a boot re-apply and the drift watch
                // aim at this same number, not at the tier's, from here on.
                WriteTarget(target);

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
                        {
                            return Partial(State(GpuRomOverrideStates.Uncertain, $"the '{name}' record did not read back as written", version, limits, request));
                        }
                    }

                    // (c) Only a driver restart makes it re-read the records.
                    restarted = _restarter.Restart(_registry.InstanceId);
                }

                GpuRomOverrideStateDto state = ApplyLive(
                    "gpu.set_rom_override",
                    staged ? "the records were already staged; the GPU was not restarted" : null,
                    pinned, target, restarted, limits, request, budget, rails,
                    out RailSelection appliedRails,
                    captureBudgetFor: originals?.Budget == null ? pinned : null);

                // The marker is an informational copy of the intent (the records and the
                // originals file are the intent); it survives a boot only sometimes, so
                // nothing may depend on it.
                if (state.State != GpuRomOverrideStates.Unlocked)
                    return Partial(state);

                // The first completed unlock establishes the tested defaults as the selection.
                // A re-unlock must not overwrite values the owner tuned afterwards.
                if (!File.Exists(_railsPath))
                    WriteRailSelection(appliedRails);

                _startup = null;
                WriteMarker();
                NotifyUnlocked();
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
        /// Unlocked answer, <paramref name="targetMilliwatts"/> is the TGP this apply aims
        /// at (the budget raise and the driver's request use it), <paramref name="requestedRails"/>
        /// is the selection the rails
        /// were moved to, and <paramref name="captureBudgetFor"/> is the version to
        /// capture the stock budget into the originals file for (null: never — the
        /// start-up re-apply must not rewrite the recovery file).
        /// </summary>
        private GpuRomOverrideStateDto ApplyLive(
            string verb,
            string? note,
            string version,
            int targetMilliwatts,
            bool restarted,
            GpuPowerLimits limits,
            GpuPowerRequestState request,
            GpuPowerBudgetState budget,
            GpuRailLimits rails,
            out RailSelection requestedRails,
            string? captureBudgetFor = null)
        {
            // (d) The runtime rail currents: a separate NVAPI call from the records,
            // and the lever that moves the board power itself (measured 2026-11-15:
            // same records, budget and request, stock rails 175-179 W, tier rails
            // 205-214 W). Written after the restart, which reloads the driver's own
            // defaults. The numbers are the owner's saved selection, or this tier's
            // when nothing was saved.
            requestedRails = RequestedRails(verb);
            rails = _rails.Set(
                requestedRails.NvVdd, requestedRails.MsVdd, requestedRails.BoardInput);
            if (rails.Reason is string railed)
            {
                return State(
                    GpuRomOverrideStates.Uncertain,
                    "the records are staged and the EC is offset, but the runtime rails could not be " +
                    $"set: {railed}",
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

            if (current.F7Milliwatts < targetMilliwatts)
            {
                if (!TryTierBudget(verb, targetMilliwatts, current, out GpuPowerBudget raised, out string? overflow))
                {
                    return State(
                        GpuRomOverrideStates.Uncertain,
                        $"the records are staged and the EC is offset, but {overflow}",
                        version, limits, request, restarted, budget, rails);
                }

                GpuPowerBudgetState set = _budget.Set(raised);
                if (set.Budget is not GpuPowerBudget appliedBudget)
                {
                    return State(
                        GpuRomOverrideStates.Uncertain,
                        "the records are staged and the EC is offset, but the laptop power budget " +
                        $"could not be raised to {Watts(targetMilliwatts)}: {set.Reason}",
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
            request = _request.Request(targetMilliwatts);
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
                    $"but the {Watts(targetMilliwatts)} power request failed: {refused}",
                    version, limits, request, restarted, budget, rails);
            }

            if (CeilingReason(limits, targetMilliwatts) is string shortfall)
                return State(GpuRomOverrideStates.Uncertain, shortfall, version, limits, request, restarted, budget, rails);

            if (RequestReason(request, targetMilliwatts) is string notAsked)
                return State(GpuRomOverrideStates.Uncertain, notAsked, version, limits, request, restarted, budget, rails);

            if (BudgetReason(budget, targetMilliwatts) is string capped)
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

                if (!Pinned(version, out VbiosPolicyState? image))
                    return Attempt(GpuReapplyVerdict.Refused, State(
                        GpuRomOverrideStates.Unsupported, UnpinnedReason(version, image), version, none));

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
                // before it, so a refusal here is clean. The target is the one the last
                // apply saved, so a boot aims at the same TGP the owner chose.
                int target = TargetMilliwatts;
                GpuPowerInfoDto info = _ec.GetGpuPowerInfo();
                if (!info.Supported)
                {
                    return Attempt(GpuReapplyVerdict.Transient, State(
                        GpuRomOverrideStates.Unsupported,
                        $"the EC publishes no usable DefaultTGP, so the cTGP offset cannot be computed: {info.Reason}",
                        version, none));
                }

                _ec.ActivateGpuPowerControl(EcSpecCodec.CtgpOffsetForTgp(target / 1000, info.DefaultTgpWatts), 0);

                GpuRomOverrideStateDto state = ApplyLive(
                    "gpu.startup", null, version, target, restarted: false,
                    none, new GpuPowerRequestState(null, null, null, null),
                    new GpuPowerBudgetState(null, null, null), GpuRailLimits.None, out _);

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
                int target = TargetMilliwatts;
                var drift = new List<string>();

                // The rails: a driver reload leaves the policy maxima, so the requested currents
                // are rewritten only when the card does not carry them.
                GpuRailLimits rails = _rails.Read();
                if (RailsReason(rails) is not null)
                {
                    RailSelection requested = RequestedRails(null);
                    drift.Add($"rails {requested.NvVdd}/{requested.MsVdd}/{requested.BoardInput} mA");
                    rails = _rails.Set(requested.NvVdd, requested.MsVdd, requested.BoardInput);
                }

                // The laptop budget: the measured drift. Raised from the baseline captured
                // once, so a second settle pass lands on the same numbers.
                GpuPowerBudgetState budget = _budget.Read();
                if (budget.Budget is GpuPowerBudget current)
                {
                    if (current.F7Milliwatts < target)
                    {
                        drift.Add($"budget {current.F7Milliwatts} mW");
                        if (TryTierBudget("gpu.settle", target, current, out GpuPowerBudget raised, out _))
                            budget = _budget.Set(raised);
                    }
                }
                else
                {
                    drift.Add("budget unreadable");
                }

                // The driver's own request: cleared by a driver reload.
                GpuPowerRequestState request = _request.Read();
                if (RequestReason(request, target) is not null)
                {
                    drift.Add("request");
                    request = _request.Request(target);
                }

                // The ceiling is the records' witness, so it is read but never written here:
                // only a restart makes the driver re-read the records.
                GpuPowerLimits limits = _ceiling.Read();
                if (CeilingReason(limits, target) is not null)
                    drift.Add("ceiling");

                if (drift.Count == 0)
                {
                    _startup = null;
                    return new(GpuSettleVerdict.Settled, null, null);
                }

                string what = string.Join(", ", drift);
                string? shortfall =
                    BudgetReason(budget, target) ?? RequestReason(request, target) ??
                    CeilingReason(limits, target) ?? RailsReason(rails);

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

        public GpuBudgetWatchResult WatchBudget()
        {
            try
            {
                if (!IntentHolds())
                    return new(GpuBudgetWatchVerdict.Stopped, null);

                GpuPowerBudgetState state = _budget.Read();
                if (state.Budget is not GpuPowerBudget current)
                    return new(GpuBudgetWatchVerdict.Failed, $"the laptop power budget could not be read: {state.Reason}");

                int target = TargetMilliwatts;
                if (current.F7Milliwatts >= target)
                    return new(GpuBudgetWatchVerdict.Holding, null);

                if (!TryTierBudget("gpu.budget_watch", target, current, out GpuPowerBudget wanted, out string? reason))
                    return new(GpuBudgetWatchVerdict.Failed, reason);

                GpuPowerBudgetState set = _budget.Set(wanted);
                if (set.Budget is not GpuPowerBudget applied)
                {
                    return new(GpuBudgetWatchVerdict.Failed,
                        $"the laptop power budget could not be raised to {Watts(target)}: {set.Reason}");
                }

                // The raise is the fix, so a stored verdict about the drift must not outlive
                // it: the state verb then reads the live machine and the owner sees the card
                // recover without re-applying. Every other witness is still checked there.
                _startup = null;
                Log.Information(
                    "gpu.budget_watch: the EC republished the laptop budget {FromF7} -> {F7} mW, platform {Platform} mW",
                    current.F7Milliwatts, applied.F7Milliwatts, applied.PlatformMilliwatts);
                return new(GpuBudgetWatchVerdict.Raised, null);
            }
            catch (Exception ex)
            {
                return new(GpuBudgetWatchVerdict.Failed, ex.Message);
            }
        }

        /// <summary>
        /// The raise the target wants, computed from the baseline captured once in the
        /// originals file (<c>f7 = max(baseline.f7, target)</c>, <c>platform = baseline.platform
        /// + (f7 - baseline.f7)</c>). Computing from the baseline rather than the current
        /// budget is what makes a repeated apply idempotent. An originals file written before
        /// the capture (no f7 / platform lines) falls back to the current budget, and says so.
        /// </summary>
        private bool TryTierBudget(
            string verb, int targetMilliwatts, GpuPowerBudget current, out GpuPowerBudget raised, out string? reason)
        {
            reason = null;
            GpuPowerBudget baseline = current;

            if (TryReadOriginals(out Originals? originals, out string? fileError) &&
                originals?.Budget is GpuPowerBudget captured)
            {
                baseline = captured;
            }
            else
            {
                Log.Information(
                    "{Verb}: no captured power budget in the originals file{Error}; the raise is computed from the current {F7} mW",
                    verb, fileError is null ? string.Empty : $" ({fileError})", current.F7Milliwatts);
            }

            if (!GpuPowerBudget.TryRaise(baseline, targetMilliwatts, out raised))
            {
                reason = "the raised laptop power budget overflows";
                return false;
            }

            return true;
        }

        /// <summary>
        /// An unlock listener's failure must not turn an apply or a state read into a failure.
        /// </summary>
        private void NotifyUnlocked()
        {
            try
            {
                Unlocked?.Invoke();
            }
            catch (Exception ex)
            {
                Log.Information("gpu: the unlock listener failed: {Type} {Message}", ex.GetType().Name, ex.Message);
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

            if (!Pinned(version, out VbiosPolicyState? image))
                return State(GpuRomOverrideStates.Unsupported, UnpinnedReason(version, image), version, none);

            if (!TryReadOriginals(out originals, out string? fileError))
                return State(GpuRomOverrideStates.Unsupported, fileError, version, none);

            if (ForeignRecordReason(records, originals) is string foreign)
                return State(GpuRomOverrideStates.Unsupported, foreign, version, none);

            return null;
        }

        /// <summary>
        /// Whether this build may patch this adapter's image: the known-good version without
        /// asking, or any other version whose own parsed image puts all seven record offsets
        /// on the field they patch. The version string is evidence for the log, never the
        /// gate; a DIFFER verdict, no answer and an unparseable image all stay refusals.
        /// </summary>
        private bool Pinned(string? version, out VbiosPolicyState? image)
        {
            image = null;
            if (RomOverrideLayout.IsPinned(version))
                return true;

            image = _vbios.Read();
            if (!RomOverrideLayout.IsPinned(version, image))
                return false;

            Log.Information(
                "gpu.set_rom_override: VBIOS {Version} is accepted by its parsed layout (MATCH): the record offsets land on this build's policy fields",
                version);
            return true;
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
                    int target = TargetMilliwatts;
                    GpuPowerBudget applied = GpuPowerBudget.TryRaise(baseline, target, out GpuPowerBudget raised)
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
                    DiscardTarget();

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

        /// <summary>
        /// An Uncertain answer from a path that has already written (the EC offset, the
        /// records, the rails). It is remembered so the status verb, which reads the live
        /// witnesses again, cannot report Unlocked over it: after a driver reset the rails
        /// read back at the record maxima, which is exactly the field case of 0.9.12 — a
        /// failed re-apply whose rails never moved. A completed apply or settle clears it.
        /// </summary>
        private GpuRomOverrideStateDto Partial(GpuRomOverrideStateDto state)
        {
            _startup = state;
            return state;
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
            TargetTgpMilliwatts = TargetMilliwatts,
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
                if (version == null || !Pinned(version, out _))
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
        private string? CeilingReason(GpuPowerLimits limits, int targetMilliwatts) =>
            limits.CeilingMilliwatts is not int ceiling
                ? "the driver did not report a power ceiling, so the records could not be confirmed"
                : ceiling >= targetMilliwatts
                    ? null
                    : $"the driver reports a {Watts(ceiling)} ceiling, not {Watts(targetMilliwatts)}";

        /// <summary>
        /// Why the driver's own request is not the target, or null when it is.
        /// The request is what the driver enforces: a staged ceiling without it leaves
        /// the card on its EC-derived limit.
        /// </summary>
        private string? RequestReason(GpuPowerRequestState request, int targetMilliwatts) =>
            request.Reason is string unread
                ? $"the driver's power request could not be read: {unread}"
                : request.RequestedMilliwatts is not int requested
                    ? "the driver carries no power request, so the card stays on its EC-derived limit"
                    : requested == targetMilliwatts
                        ? null
                        : $"the driver reports a {Watts(requested)} power request, not {Watts(targetMilliwatts)}";

        private static string CeilingText(GpuPowerLimits limits) =>
            limits.CeilingMilliwatts is int ceiling ? Watts(ceiling) : "unknown";

        /// <summary>
        /// Why the laptop's own budget cannot carry the tier, or null when it can. The
        /// driver enforces min(request, budget), so a budget below the target would cap
        /// the request no matter what the ceiling says.
        /// </summary>
        private string? BudgetReason(GpuPowerBudgetState budget, int targetMilliwatts) =>
            budget.Budget is not GpuPowerBudget current
                ? $"the laptop power budget could not be read: {budget.Reason}"
                : current.F7Milliwatts >= targetMilliwatts
                    ? null
                    : $"the driver reports a {Watts(current.F7Milliwatts)} laptop power budget, not {Watts(targetMilliwatts)}";

        /// <summary>
        /// Why the rails are not the requested ones, or null when they are. The comparison is
        /// against the owner's saved selection (this tier's numbers only when none was saved),
        /// clamped by the live policy, so a value the policy no longer allows does not read as
        /// a permanent mismatch. Named here rather than left to the DTO's numbers: a rail that
        /// did not take is the difference between an unlock and a 175 W card.
        /// </summary>
        private string? RailsReason(GpuRailLimits rails)
        {
            if (rails.Reason is string refused)
                return $"the runtime rails could not be read back: {refused}";

            RailSelection expected = SavedOrDefault();

            return RailsMismatch(GpuRail.NvVdd, rails.NvVdd, ClampRail(GpuRail.NvVdd, expected.NvVdd, rails.NvVdd, warn: false))
                ?? RailsMismatch(GpuRail.MsVdd, rails.MsVdd, ClampRail(GpuRail.MsVdd, expected.MsVdd, rails.MsVdd, warn: false))
                ?? RailsMismatch(GpuRail.BoardInput, rails.BoardInput, ClampRail(GpuRail.BoardInput, expected.BoardInput, rails.BoardInput, warn: false));
        }

        private static string? RailsMismatch(GpuRail rail, GpuRailValue value, int expected) =>
            value.CurrentMilliamps == expected
                ? null
                : $"the {RailName(rail)} rail reads " +
                  $"{(value.CurrentMilliamps is int current ? $"{current} mA" : "nothing")}, not the requested {expected} mA";

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

        /// <summary>
        /// The rail currents to set: the owner's saved selection, validated against the live
        /// policy (a driver update can narrow it), or this tier's numbers when nothing was
        /// ever saved — which the log line says, one per call that asks for rails.
        /// </summary>
        private RailSelection RequestedRails(string? verb)
        {
            if (TryReadRailSelection(out string? error) is not RailSelection saved)
            {
                if (verb != null)
                {
                    Log.Information(
                        error is null
                            ? "{Verb}: rails from the tier defaults (no saved selection)"
                            : "{Verb}: rails from the tier defaults (the saved selection could not be read: {Error})",
                        verb, error);
                }

                return TierRails;
            }

            GpuRailLimits live = _rails.Read();
            RailSelection requested = new(
                ClampRail(GpuRail.NvVdd, saved.NvVdd, live.NvVdd, warn: true),
                ClampRail(GpuRail.MsVdd, saved.MsVdd, live.MsVdd, warn: true),
                ClampRail(GpuRail.BoardInput, saved.BoardInput, live.BoardInput, warn: true));

            if (verb != null)
            {
                Log.Information(
                    "{Verb}: rails from the saved selection: {Nv}/{Ms}/{Input} mA",
                    verb, requested.NvVdd, requested.MsVdd, requested.BoardInput);
            }

            return requested;
        }

        private RailSelection SavedOrDefault() =>
            TryReadRailSelection(out _) ?? TierRails;

        private RailSelection TierRails => new(
            _tier.NvVddCurrentMilliAmps, _tier.MsVddCurrentMilliAmps, _tier.BoardInputCurrentMilliAmps);

        /// <summary>
        /// A saved value the live policy no longer allows is clamped to the nearest bound; the
        /// write path warns, the read-only comparison stays quiet.
        /// </summary>
        private static int ClampRail(GpuRail rail, int milliamps, GpuRailValue live, bool warn)
        {
            if (live.MinMilliamps is not int min || live.MaxMilliamps is not int max)
                return milliamps;

            int clamped = Math.Clamp(milliamps, min, max);
            if (clamped != milliamps && warn)
            {
                Log.Warning(
                    "gpu: the saved {Rail} rail {Saved} mA is outside the live rail policy {Min}-{Max} mA; using {Clamped} mA",
                    RailName(rail), milliamps, min, max, clamped);
            }

            return clamped;
        }

        /// <summary>
        /// The saved rail selection, or null with a reason when it is unreadable. Absent is
        /// null with no reason: that is a machine whose owner never chose one.
        /// </summary>
        private RailSelection? TryReadRailSelection(out string? error)
        {
            error = null;
            if (!File.Exists(_railsPath))
                return null;

            string[] lines;
            try
            {
                lines = File.ReadAllLines(_railsPath);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }

            int? nvvdd = null, msvdd = null, board = null;

            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0)
                    continue;

                int equal = line.IndexOf('=');
                if (equal <= 0 ||
                    !int.TryParse(line.AsSpan(equal + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int milliamps) ||
                    milliamps <= 0)
                {
                    error = "the UCU saved rail selection file is malformed";
                    return null;
                }

                string name = line.Substring(0, equal).Trim();
                if (name.Equals("nvvdd", StringComparison.OrdinalIgnoreCase))
                {
                    if (nvvdd != null)
                        return Malformed(out error);
                    nvvdd = milliamps;
                }
                else if (name.Equals("msvdd", StringComparison.OrdinalIgnoreCase))
                {
                    if (msvdd != null)
                        return Malformed(out error);
                    msvdd = milliamps;
                }
                else if (name.Equals("board", StringComparison.OrdinalIgnoreCase))
                {
                    if (board != null)
                        return Malformed(out error);
                    board = milliamps;
                }
                else
                {
                    error = $"the UCU saved rail selection file names the unexpected rail '{name}'";
                    return null;
                }
            }

            if (nvvdd is not int nv || msvdd is not int ms || board is not int input)
                return Malformed(out error);

            return new RailSelection(nv, ms, input);
        }

        private static RailSelection? Malformed(out string? error)
        {
            error = "the UCU saved rail selection file is malformed";
            return null;
        }

        /// <summary>
        /// The owner's choice, written whole (three lines, tmp → move): the file is either the
        /// previous selection or this one, never a half-written mix.
        /// </summary>
        private void WriteRailSelection(RailSelection selection)
        {
            string? directory = Path.GetDirectoryName(_railsPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            string temporary = _railsPath + ".tmp";
            File.WriteAllText(
                temporary,
                new StringBuilder()
                    .Append("nvvdd=").Append(selection.NvVdd.ToString(CultureInfo.InvariantCulture))
                    .Append('\n').Append("msvdd=").Append(selection.MsVdd.ToString(CultureInfo.InvariantCulture))
                    .Append('\n').Append("board=").Append(selection.BoardInput.ToString(CultureInfo.InvariantCulture))
                    .Append('\n').ToString());

            File.Move(temporary, _railsPath, overwrite: true);
            Log.Information(
                "gpu: the rail selection {Nv}/{Ms}/{Input} mA is saved to {Path}",
                selection.NvVdd, selection.MsVdd, selection.BoardInput, _railsPath);
        }

        private void DiscardRailSelection()
        {
            if (File.Exists(_railsPath))
            {
                File.Delete(_railsPath);
                Log.Information("gpu.set_rom_override: the saved rail selection was removed from {Path}", _railsPath);
            }
        }

        /// <summary>
        /// The TGP this unlock aims at: the target the last apply was given, saved beside
        /// the originals so a boot re-apply, the settle pass and the drift watch all aim at
        /// it, or the tier's tested target when none was ever saved. A stored value outside
        /// the accepted range (the stock ceiling up to the tier's) is clamped and logged —
        /// the range belongs to the store, not to the file.
        /// </summary>
        private int TargetMilliwatts
        {
            get
            {
                if (!File.Exists(_targetPath))
                    return _tier.TgpTargetMilliwatts;

                try
                {
                    if (int.TryParse(
                        File.ReadAllText(_targetPath).Trim(),
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out int stored))
                    {
                        int clamped = Math.Clamp(stored, RomOverrideLayout.StockPowerMilliwatts, _tier.PowerMilliwatts);
                        if (clamped != stored)
                        {
                            Log.Warning(
                                "gpu: the saved unlock target {Stored} mW is outside {Floor}-{Ceiling} mW; using {Clamped} mW",
                                stored, RomOverrideLayout.StockPowerMilliwatts, _tier.PowerMilliwatts, clamped);
                        }

                        return clamped;
                    }

                    Log.Information(
                        "gpu: the saved unlock target file {Path} is unreadable; using the {Target} mW tested target",
                        _targetPath, _tier.TgpTargetMilliwatts);
                }
                catch (Exception ex)
                {
                    Log.Information(
                        "gpu: the saved unlock target could not be read from {Path}: {Type} {Message}",
                        _targetPath, ex.GetType().Name, ex.Message);
                }

                return _tier.TgpTargetMilliwatts;
            }
        }

        /// <summary>
        /// The target, written whole (tmp → move) with the EC offset — the first hardware
        /// move of an apply: a boot re-apply must aim at the same number the owner asked for.
        /// </summary>
        private void WriteTarget(int milliwatts)
        {
            string? directory = Path.GetDirectoryName(_targetPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            string temporary = _targetPath + ".tmp";
            File.WriteAllText(temporary, milliwatts.ToString(CultureInfo.InvariantCulture));
            File.Move(temporary, _targetPath, overwrite: true);
            Log.Information("gpu.set_rom_override: the unlock target {Target} mW is saved to {Path}", milliwatts, _targetPath);
        }

        private void DiscardTarget()
        {
            if (File.Exists(_targetPath))
            {
                File.Delete(_targetPath);
                Log.Information("gpu.set_rom_override: the unlock target was removed from {Path}", _targetPath);
            }
        }

        private static string Watts(int milliwatts) =>
            (milliwatts / 1000.0).ToString("0.##", CultureInfo.InvariantCulture) + " W";

        /// <summary>
        /// Why this image is refused: the version is named, and so is what the probe found —
        /// no answer, or a layout whose offsets land on other fields than this build's.
        /// </summary>
        private static string UnpinnedReason(string version, VbiosPolicyState? image) =>
            image is { Succeeded: true }
                ? $"VBIOS {version} is not pinned; the image's parsed policy does not match this build's record offsets (DIFFER), and they are only known for {RomOverrideLayout.PinnedVbiosVersion} or an image that matches"
                : $"VBIOS {version} is not pinned; the VBIOS image could not be checked ({image?.Reason ?? "the probe was not read"}), and the offsets are only known for {RomOverrideLayout.PinnedVbiosVersion} or an image that matches";

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

            // The informational copy, the saved rail selection and the unlock target go with
            // them, and so does a verdict about a boot that no longer has anything staged.
            DiscardRailSelection();
            DiscardTarget();
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

        /// <summary>The three runtime rail currents, in mA, as one selection.</summary>
        private sealed record RailSelection(int NvVdd, int MsVdd, int BoardInput);
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
