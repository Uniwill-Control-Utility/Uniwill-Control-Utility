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

    /// <summary>The two gpu.* ROM verbs' target.</summary>
    public interface IGpuRomOverrideService
    {
        GpuRomOverrideStateDto GetState();

        /// <summary>true stages the tier's records and restarts the GPU; false restores stock.</summary>
        GpuRomOverrideStateDto SetEnabled(bool enabled);
    }

    /// <summary>
    /// The ROM override store for the pinned VBIOS: stage the tier's seven records on
    /// the GPU's display-driver key, raise the EC's cTGP offset, restart the GPU, raise
    /// the laptop's own power budget, then make the driver's own power request — and
    /// restore by undoing all of it.
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
    /// and the EC publishes a 150 W budget, so the unlock is four moves — EC offset,
    /// records + restart, budget raise, driver request — and Unlocked means all of them.
    /// </summary>
    public sealed class GpuRomOverrideStore : IGpuRomOverrideService
    {
        private readonly IGpuRomRegistry _registry;
        private readonly IGpuRestarter _restarter;
        private readonly IGpuPowerCeilingProbe _ceiling;
        private readonly IEcGpuPowerControl _ec;
        private readonly IGpuPowerRequest _request;
        private readonly INvApiPowerBudget _budget;
        private readonly GpuPowerTier _tier;
        private readonly string _originalsPath;

        public GpuRomOverrideStore(
            IGpuRomRegistry registry,
            IGpuRestarter restarter,
            IGpuPowerCeilingProbe ceiling,
            IEcGpuPowerControl ec,
            IGpuPowerRequest request,
            INvApiPowerBudget budget,
            string? originalsPath = null,
            GpuPowerTier? tier = null)
        {
            _registry = registry;
            _restarter = restarter;
            _ceiling = ceiling;
            _ec = ec;
            _request = request;
            _budget = budget;
            _tier = tier ?? GpuPowerTiers.ExtraGpuPower;
            _originalsPath = originalsPath ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "UCU",
                "gpu-rom-overrides",
                "originals.txt");
        }

        public GpuRomOverrideStateDto GetState()
        {
            GpuPowerLimits limits = _ceiling.Read();
            GpuPowerRequestState request = _request.Read();
            GpuPowerBudgetState budget = _budget.Read();

            try
            {
                string? version = _registry.ReadVbiosVersion();
                if (version == null)
                    return State(GpuRomOverrideStates.Unsupported, "the display driver key carries no VBIOS version", null, limits, request, budget: budget);

                if (!RomOverrideLayout.IsPinned(version))
                    return State(GpuRomOverrideStates.Unsupported, UnpinnedReason(version), version, limits, request, budget: budget);

                IReadOnlyDictionary<string, byte[]> records = _registry.ReadRecords();
                if (records.Count == 0)
                    return State(GpuRomOverrideStates.NotUnlocked, null, version, limits, request, budget: budget);

                if (!TryReadOriginals(out Originals? originals, out string? fileError))
                    return State(GpuRomOverrideStates.Unsupported, fileError, version, limits, request, budget: budget);

                if (ForeignRecordReason(records, originals) is string foreign)
                    return State(GpuRomOverrideStates.Unsupported, foreign, version, limits, request, budget: budget);

                if (CeilingReason(limits) is string unmoved)
                    return State(GpuRomOverrideStates.Uncertain, unmoved, version, limits, request, budget: budget);

                if (RequestReason(request) is string notAsked)
                    return State(GpuRomOverrideStates.Uncertain, notAsked, version, limits, request, budget: budget);

                return BudgetReason(budget) is string capped
                    ? State(GpuRomOverrideStates.Uncertain, capped, version, limits, request, budget: budget)
                    : State(GpuRomOverrideStates.Unlocked, null, version, limits, request, budget: budget);
            }
            catch (Exception ex)
            {
                Log.Information("gpu.get_rom_override_state: {Type} {Message}", ex.GetType().Name, ex.Message);
                return State(GpuRomOverrideStates.Uncertain, ex.Message, null, limits, request, budget: budget);
            }
        }

        public GpuRomOverrideStateDto SetEnabled(bool enabled) => enabled ? Apply() : Restore();

        private GpuRomOverrideStateDto Apply()
        {
            GpuPowerLimits limits = new(null, null);
            GpuPowerRequestState request = new(null, null, null, null);
            GpuPowerBudgetState budget = new(null, null, null);
            string? version = null;

            try
            {
                version = _registry.ReadVbiosVersion();
                if (version == null)
                    return State(GpuRomOverrideStates.Unsupported, "the display driver key carries no VBIOS version", null, limits, request, budget: budget);

                if (!RomOverrideLayout.IsPinned(version))
                    return State(GpuRomOverrideStates.Unsupported, UnpinnedReason(version), version, limits, request, budget: budget);

                IReadOnlyDictionary<string, byte[]> records = _registry.ReadRecords();
                if (!TryReadOriginals(out Originals? originals, out string? fileError))
                    return State(GpuRomOverrideStates.Unsupported, fileError, version, limits, request, budget: budget);

                if (ForeignRecordReason(records, originals) is string foreign)
                    return State(GpuRomOverrideStates.Unsupported, foreign, version, limits, request, budget: budget);

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

                IReadOnlyList<(string Name, byte[] Data)> desired = RomOverrideLayout.BuildRecords(_tier, version);
                bool staged = Matches(records, desired);
                bool restarted = false;

                if (!staged)
                {
                    // (b) The records: originals first, then write and read back.
                    if (originals == null)
                        WriteOriginals(version);

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

                // (d) The laptop's own power budget. The driver enforces
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
                        version, limits, request, restarted, budget);
                }

                // The reference's budget_original_: captured once, before the first raise,
                // and kept in the originals file so a restore after an app restart still
                // knows the stock values.
                if (originals?.Budget == null)
                    WriteOriginals(version, current);

                if (current.F7Milliwatts < _tier.TgpTargetMilliwatts)
                {
                    if (!current.TryRaise(_tier.TgpTargetMilliwatts, out GpuPowerBudget raised))
                    {
                        return State(
                            GpuRomOverrideStates.Uncertain,
                            "the records are staged and the EC is offset, but the raised laptop power budget overflows",
                            version, limits, request, restarted, budget);
                    }

                    GpuPowerBudgetState set = _budget.Set(raised);
                    if (set.Budget is not GpuPowerBudget appliedBudget)
                    {
                        return State(
                            GpuRomOverrideStates.Uncertain,
                            "the records are staged and the EC is offset, but the laptop power budget " +
                            $"could not be raised to {Watts(_tier.TgpTargetMilliwatts)}: {set.Reason}",
                            version, limits, request, restarted, set);
                    }

                    // Set only answers after its own exact F7 readback matched.
                    budget = set;
                    Log.Information(
                        "gpu.set_rom_override: laptop budget raised {FromF7} -> {F7} mW, platform {Platform} mW",
                        current.F7Milliwatts, appliedBudget.F7Milliwatts, appliedBudget.PlatformMilliwatts);
                }

                // (e) The request: without it the ceiling moves and the card stays on its
                // EC-derived limit, which is the failure this tier shipped with.
                request = _request.Request(_tier.TgpTargetMilliwatts);
                limits = _ceiling.Read();

                Log.Information(
                    "gpu.set_rom_override: {Tier} staged on {Version} ({Records} records), GPU restart {Restart}, ceiling {Ceiling} mW, current limit {Current} mW, budget {Budget} mW, request {Request} mW",
                    _tier.Id, version, desired.Count, restarted ? "yes" : "no",
                    limits.CeilingMilliwatts?.ToString(CultureInfo.InvariantCulture) ?? "unknown",
                    limits.CurrentMilliwatts?.ToString(CultureInfo.InvariantCulture) ?? "unknown",
                    budget.F7Milliwatts?.ToString(CultureInfo.InvariantCulture) ?? "unknown",
                    request.RequestedMilliwatts?.ToString(CultureInfo.InvariantCulture) ?? "none");

                // (f) All four, or it is Uncertain and the reason names the failed step.
                if (request.Reason is string refused)
                {
                    return State(
                        GpuRomOverrideStates.Uncertain,
                        $"the records are staged and the ceiling is {CeilingText(limits)}, " +
                        $"but the {Watts(_tier.TgpTargetMilliwatts)} power request failed: {refused}",
                        version, limits, request, restarted, budget);
                }

                if (CeilingReason(limits) is string shortfall)
                    return State(GpuRomOverrideStates.Uncertain, shortfall, version, limits, request, restarted, budget);

                if (RequestReason(request) is string notAsked)
                    return State(GpuRomOverrideStates.Uncertain, notAsked, version, limits, request, restarted, budget);

                if (BudgetReason(budget) is string capped)
                    return State(GpuRomOverrideStates.Uncertain, capped, version, limits, request, restarted, budget);

                return State(
                    GpuRomOverrideStates.Unlocked,
                    staged ? "the records were already staged; the GPU was not restarted" : null,
                    version, limits, request, restarted, budget);
            }
            catch (Exception ex)
            {
                // Past the first registry write the key state is unknown: say so.
                Log.Information("gpu.set_rom_override: {Type} {Message}", ex.GetType().Name, ex.Message);
                return State(GpuRomOverrideStates.Uncertain, ex.Message, version, limits, request, budget: budget);
            }
        }

        private GpuRomOverrideStateDto Restore()
        {
            string? version = null;
            GpuPowerRequestState request = new(null, null, null, null);
            GpuPowerBudgetState budget = new(null, null, null);
            string? budgetReason = null;

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
                    // next apply writes fresh stock values. A budget that would not go back
                    // keeps the file: it is the only record of the stock values.
                    if (originals != null)
                    {
                        if (budgetReason is null)
                        {
                            DiscardOriginals();
                            return State(GpuRomOverrideStates.NotUnlocked, "no overrides were staged; the stale originals file was removed", version, _ceiling.Read(), _request.Read());
                        }

                        return State(GpuRomOverrideStates.Uncertain, budgetReason, version, _ceiling.Read(), _request.Read(), budget: budget);
                    }

                    return State(GpuRomOverrideStates.NotUnlocked, budgetReason, version, _ceiling.Read(), _request.Read(), budget: budget);
                }

                if (ForeignRecordReason(records, originals) is string foreign)
                    return State(GpuRomOverrideStates.Unsupported, foreign, version, _ceiling.Read(), _request.Read(), budget: budget);

                foreach (string name in records.Keys)
                    _registry.DeleteRecord(name);

                _registry.Flush();

                IReadOnlyDictionary<string, byte[]> readback = _registry.ReadRecords();
                if (readback.Count != 0)
                    return State(GpuRomOverrideStates.Uncertain, "not every override record disappeared from the driver key", version, _ceiling.Read(), _request.Read(), budget: budget);

                // The EC byte goes back to the value captured before our activation, then
                // the restart — which is also what clears the driver's request.
                _ec.DeactivateGpuPowerControl();

                bool restarted = _restarter.Restart(_registry.InstanceId);
                GpuPowerLimits limits = _ceiling.Read();
                request = _request.Read();

                Log.Information(
                    "gpu.set_rom_override: stock restored on {Version}, GPU restart {Restart}, ceiling {Ceiling} mW, current limit {Current} mW, budget {Budget} mW, request {Request} mW",
                    version, restarted ? "yes" : "no",
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
                        version, limits, request, restarted, budget);
                }

                // A restart is what clears the request; one that survived would keep the
                // card at the raised limit with stock records.
                if (request.Reason is string unread)
                {
                    return State(
                        GpuRomOverrideStates.Uncertain,
                        $"the records are gone but the power request could not be read back: {unread}",
                        version, limits, request, restarted, budget);
                }

                if (request.RequestedMilliwatts is int stillSet)
                {
                    return State(
                        GpuRomOverrideStates.Uncertain,
                        $"the records are gone but the driver still carries a {Watts(stillSet)} power request",
                        version, limits, request, restarted, budget);
                }

                if (budgetReason is string notRestored)
                    return State(GpuRomOverrideStates.Uncertain, $"the records are gone but {notRestored}", version, limits, request, restarted, budget);

                DiscardOriginals();
                return State(GpuRomOverrideStates.NotUnlocked, null, version, limits, request, restarted, budget);
            }
            catch (Exception ex)
            {
                Log.Information("gpu.set_rom_override: {Type} {Message}", ex.GetType().Name, ex.Message);
                return State(GpuRomOverrideStates.Uncertain, ex.Message, version, new GpuPowerLimits(null, null), request, budget: budget);
            }
        }

        private GpuRomOverrideStateDto State(
            string state,
            string? reason,
            string? version,
            GpuPowerLimits limits,
            GpuPowerRequestState? request = null,
            bool restarted = false,
            GpuPowerBudgetState? budget = null) => new()
        {
            State = state,
            Reason = reason,
            VbiosVersion = version,
            TierId = _tier.Id,
            PowerMilliwatts = _tier.PowerMilliwatts,
            NvVddMilliAmps = _tier.NvVddMilliAmps,
            MsVddMilliAmps = _tier.MsVddMilliAmps,
            BoardInputMilliAmps = _tier.BoardInputMilliAmps,
            CeilingLimitMilliwatts = limits.CeilingMilliwatts,
            CurrentLimitMilliwatts = limits.CurrentMilliwatts,
            BudgetMilliwatts = budget?.F7Milliwatts,
            RequestedLimitMilliwatts = request?.RequestedMilliwatts,
            EnforcedLimitMoved = limits.CeilingMilliwatts is int ceiling && ceiling > RomOverrideLayout.StockPowerMilliwatts,
            Restarted = restarted,
        };

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
