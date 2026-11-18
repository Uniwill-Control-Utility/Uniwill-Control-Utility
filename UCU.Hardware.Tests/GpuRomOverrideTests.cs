using Serilog;
using Serilog.Core;
using Serilog.Events;
using UCU.Contracts;
using UCU.Hardware.Ec;
using UCU.Hardware.Gpu;

namespace UCU.Hardware.Tests;

/// <summary>
/// The GPU power-ceiling override store, exercised against a fake driver key. The
/// record bytes are pinned to what the field machine's key actually carried (VBIOS
/// 98.03.5E.00.5D), because a wrong offset or a wrong patch length is accepted by the
/// driver silently — it patches another field of the ROM.
///
/// The store now composes three mechanisms (EC cTGP offset, records + restart, the
/// driver's own power request) and only calls the result Unlocked when all three hold.
/// </summary>
public sealed class GpuRomOverrideTests : IDisposable
{
    private const string InstanceId = @"PCI\VEN_10DE&DEV_2C18&SUBSYS_60521D05&REV_A1\E5151BD5C52DB04800";

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "ucu-rom-override-tests", Guid.NewGuid().ToString("N"));

    private readonly List<string> _log = new();

    private string OriginalsPath => Path.Combine(_directory, "gpu-rom-overrides", "originals.txt");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Encode_MatchesTheRecordsTheDriverKeyCarried()
    {
        // Read back from HKLM\...\Class\{4d36e968-...}\0004 on 2026-11-15: these are
        // MechControl's unlock (250 W / 17.021 A / 250 A / 84 A), pinned byte for byte.
        Assert.Equal("98-03-5E-00-5D-36-7C-05-00-04-90-D0-03-00", Hex(RomOverrideRecord.Encode("98.03.5E.00.5D", 0x57C36, 250000)));
        Assert.Equal("98-03-5E-00-5D-FC-7E-05-00-04-7D-42-00-00", Hex(RomOverrideRecord.Encode("98.03.5E.00.5D", 0x57EFC, 17021)));
        Assert.Equal("98-03-5E-00-5D-00-7F-05-00-04-7D-42-00-00", Hex(RomOverrideRecord.Encode("98.03.5E.00.5D", 0x57F00, 17021)));
        Assert.Equal("98-03-5E-00-5D-94-80-05-00-04-70-7C-04-00", Hex(RomOverrideRecord.Encode("98.03.5E.00.5D", 0x58094, 294000)));
        Assert.Equal("98-03-5E-00-5D-98-80-05-00-04-70-7C-04-00", Hex(RomOverrideRecord.Encode("98.03.5E.00.5D", 0x58098, 294000)));
        Assert.Equal("98-03-5E-00-5D-FA-80-05-00-04-20-48-01-00", Hex(RomOverrideRecord.Encode("98.03.5E.00.5D", 0x580FA, 84000)));
        Assert.Equal("98-03-5E-00-5D-FE-80-05-00-04-20-48-01-00", Hex(RomOverrideRecord.Encode("98.03.5E.00.5D", 0x580FE, 84000)));

        // The stock values (mW / mA) encode at the same offsets and decode back.
        Assert.Equal("98-03-5E-00-5D-36-7C-05-00-04-98-AB-02-00", Hex(RomOverrideRecord.Encode("98.03.5E.00.5D", 0x57C36, 175000)));
        Assert.Equal("98-03-5E-00-5D-FC-7E-05-00-04-2D-2B-00-00", Hex(RomOverrideRecord.Encode("98.03.5E.00.5D", 0x57EFC, 11053)));
        Assert.Equal("98-03-5E-00-5D-94-80-05-00-04-50-34-03-00", Hex(RomOverrideRecord.Encode("98.03.5E.00.5D", 0x58094, 210000)));
        Assert.Equal("98-03-5E-00-5D-FA-80-05-00-04-60-EA-00-00", Hex(RomOverrideRecord.Encode("98.03.5E.00.5D", 0x580FA, 60000)));
    }

    [Fact]
    public void TierRecords_PinTheSevenBytes()
    {
        IReadOnlyList<(string Name, byte[] Data)> records =
            RomOverrideLayout.BuildRecords(GpuPowerTiers.ExtraGpuPower, RomOverrideLayout.PinnedVbiosVersion);

        Assert.Equal(7, records.Count);

        // ceiling 250 W, board input 17.021 A (the policy ceiling, as the reference writes
        // it), NVVDD 294 A, MSVDD 84 A — computed from the tier, so a different tier cannot
        // silently ship another set. The ceiling is deliberately not the 225 W target: a
        // request equal to the ceiling is answered with the stock 175 W limit.
        Assert.Equal("98-03-5E-00-5D-36-7C-05-00-04-90-D0-03-00", Hex(records[0].Data));
        Assert.Equal("98-03-5E-00-5D-FC-7E-05-00-04-7D-42-00-00", Hex(records[1].Data));
        Assert.Equal("98-03-5E-00-5D-00-7F-05-00-04-7D-42-00-00", Hex(records[2].Data));
        Assert.Equal("98-03-5E-00-5D-94-80-05-00-04-90-D0-03-00", Hex(records[3].Data));
        Assert.Equal("98-03-5E-00-5D-98-80-05-00-04-90-D0-03-00", Hex(records[4].Data));
        Assert.Equal("98-03-5E-00-5D-FA-80-05-00-04-20-48-01-00", Hex(records[5].Data));
        Assert.Equal("98-03-5E-00-5D-FE-80-05-00-04-20-48-01-00", Hex(records[6].Data));

        Assert.Equal(
            new[] { "romOverride00", "romOverride01", "romOverride02", "romOverride03", "romOverride04", "romOverride05", "romOverride06" },
            records.Select(r => r.Name));

        // The tier's own numbers, as measured against MechControl-RE on 2026-11-15. The
        // board input carries both: the record maximum the reference writes (17.021 A, the
        // policy ceiling the sliders read) and the runtime current we apply (14.026 A, which
        // keeps a 225 W TGP inside the 420 W adapter).
        Assert.Equal(250_000, GpuPowerTiers.ExtraGpuPower.PowerMilliwatts);
        Assert.Equal(225_000, GpuPowerTiers.ExtraGpuPower.TgpTargetMilliwatts);
        Assert.Equal(225, GpuPowerTiers.ExtraGpuPower.TgpTargetWatts);
        Assert.Equal(250_000, GpuPowerTiers.ExtraGpuPower.NvVddMilliAmps);
        Assert.Equal(84_000, GpuPowerTiers.ExtraGpuPower.MsVddMilliAmps);
        Assert.Equal(17_021, GpuPowerTiers.ExtraGpuPower.BoardInputMilliAmps);
        Assert.Equal(14_026, GpuPowerTiers.ExtraGpuPower.BoardInputCurrentMilliAmps);
    }

    [Fact]
    public void Layout_StockValuesAreTheMeasuredRom()
    {
        Assert.Equal(
            new uint[] { 0x57C36, 0x57EFC, 0x57F00, 0x58094, 0x58098, 0x580FA, 0x580FE },
            RomOverrideLayout.Fields.Select(f => f.Offset));

        Assert.Equal(
            new uint[] { 175000, 11053, 11053, 210000, 210000, 60000, 60000 },
            RomOverrideLayout.Fields.Select(f => f.StockValue));

        Assert.Equal(175000, RomOverrideLayout.StockPowerMilliwatts);
    }

    [Fact]
    public void Decode_RoundTrips_AndRejectsForeignShapes()
    {
        byte[] data = RomOverrideRecord.Encode("98.03.5E.00.5D", 0x57C36, 220000);

        Assert.True(RomOverrideRecord.TryDecode(data, out string version, out uint offset, out uint value));
        Assert.Equal("98.03.5E.00.5D", version);
        Assert.Equal(0x57C36u, offset);
        Assert.Equal(220000u, value);

        Assert.False(RomOverrideRecord.TryDecode(null, out _, out _, out _));
        Assert.False(RomOverrideRecord.TryDecode(new byte[13], out _, out _, out _));

        byte[] wrongLength = RomOverrideRecord.Encode("98.03.5E.00.5D", 0x57C36, 220000);
        wrongLength[9] = 8;
        Assert.False(RomOverrideRecord.TryDecode(wrongLength, out _, out _, out _));
    }

    [Fact]
    public void VersionGating_AcceptsOnlyThePinnedVbios()
    {
        // The driver key's own spelling, verified against the field machine's
        // HardwareInformation.BiosString ("Version98.3.5e.0.5d").
        Assert.Equal("98.03.5E.00.5D", RomOverrideLayout.NormalizeVbiosVersion("Version98.3.5e.0.5d"));
        Assert.Equal("98.03.5E.00.5D", RomOverrideLayout.NormalizeVbiosVersion("98.03.5E.00.5D"));
        Assert.Null(RomOverrideLayout.NormalizeVbiosVersion("98.03.5E.00"));
        Assert.Null(RomOverrideLayout.NormalizeVbiosVersion("garbage"));
        Assert.Null(RomOverrideLayout.NormalizeVbiosVersion(null));

        Assert.True(RomOverrideLayout.IsPinned("98.03.5E.00.5D"));
        Assert.False(RomOverrideLayout.IsPinned("98.03.5E.00.5E"));
        Assert.False(RomOverrideLayout.IsPinned("98.03.5e.0.5d"));
        Assert.False(RomOverrideLayout.IsPinned(null));
    }

    [Fact]
    public void Apply_StagesEveryRecord_WritesOriginalsFirst_AndRestartsTheGpu()
    {
        var registry = new FakeRomRegistry(_log);
        var restarter = new FakeRestarter(_log);
        var ceiling = new FakeCeiling(_log) { Ceiling = 250_000 };
        var ec = new FakeEc(_log) { DefaultTgpWatts = 95 };
        var request = new FakeRequest(_log);
        var budget = new FakeBudget(_log);

        GpuRomOverrideStateDto state = NewStore(registry, restarter, ceiling, ec, request, budget).SetEnabled(true);

        Assert.Equal(GpuRomOverrideStates.Unlocked, state.State);
        Assert.Null(state.Reason);
        Assert.Equal("98.03.5E.00.5D", state.VbiosVersion);
        Assert.True(state.Restarted);
        Assert.True(state.EnforcedLimitMoved);
        Assert.Equal(1, restarter.Calls);
        Assert.Equal(InstanceId, restarter.LastInstanceId);

        // The key carries exactly the tier's seven records, read back equal.
        IReadOnlyList<(string Name, byte[] Data)> desired =
            RomOverrideLayout.BuildRecords(GpuPowerTiers.ExtraGpuPower, RomOverrideLayout.PinnedVbiosVersion);
        Assert.Equal(desired.Count, registry.Records.Count);
        foreach ((string name, byte[] data) in desired)
            Assert.Equal(data, registry.Records[name]);

        Assert.True(registry.Flushed);

        // The EC offset is the target minus this board's DefaultTGP, with Dynamic Boost
        // off (0x0746 = 0 on the measured unlock).
        Assert.Equal(new[] { (130, 0) }, ec.Activations);
        Assert.Equal(0, ec.Deactivations);

        // The driver is asked for the target, not the ceiling, and the DTO reports it.
        Assert.Equal(new[] { 225_000 }, request.Requests);
        Assert.Equal(225_000, state.RequestedLimitMilliwatts!.Value);
        Assert.Equal(250_000, state.CeilingLimitMilliwatts!.Value);

        // The laptop budget is what the request is capped by: measured 150 W / 425 W, so
        // the raise takes 75 W of headroom and the platform follows to 500 W.
        Assert.Equal(new[] { new GpuPowerBudget(225_000, 500_000) }, budget.Sets);
        Assert.Equal(225_000, state.BudgetMilliwatts!.Value);

        // Ordering: EC first, then the records, then the restart, then the budget, then
        // the request (a raise before the restart is undone by it).
        int ecIndex = _log.IndexOf("ec.activate:130,0");
        int writeIndex = _log.IndexOf("write:romOverride00");
        int flushIndex = _log.IndexOf("flush");
        int restartIndex = _log.IndexOf("restart");
        int budgetIndex = _log.IndexOf("budget.set:225000,500000");
        int requestIndex = _log.IndexOf("request:225000");
        Assert.True(ecIndex >= 0 && ecIndex < writeIndex, string.Join(",", _log));
        Assert.True(writeIndex < flushIndex, string.Join(",", _log));
        Assert.True(flushIndex < restartIndex, string.Join(",", _log));
        Assert.True(restartIndex < budgetIndex, string.Join(",", _log));
        Assert.True(budgetIndex < requestIndex, string.Join(",", _log));

        // version line, then offset=value lines (decimal, as the reference writes them),
        // then the captured budget a restore after an app restart still needs.
        string[] lines = File.ReadAllLines(OriginalsPath);
        Assert.Equal("98.03.5E.00.5D", lines[0]);
        Assert.Equal("359478=175000", lines[1]);   // 0x57C36
        Assert.Equal("360192=11053", lines[3]);    // 0x57F00
        Assert.Equal("360600=210000", lines[5]);   // 0x58098
        Assert.Equal("360702=60000", lines[7]);    // 0x580FE
        Assert.Equal("f7=150000", lines[8]);
        Assert.Equal("platform=425000", lines[9]);
        Assert.Equal(10, lines.Length);
    }

    [Fact]
    public void Apply_ComputesTheCtgpOffsetFromTheBoardsOwnDefaultTgp()
    {
        // The offset is a difference, not a constant: an 80 W base needs 145 for 225 W.
        var ec = new FakeEc(_log) { DefaultTgpWatts = 80 };

        GpuRomOverrideStateDto state = NewStore(
            new FakeRomRegistry(_log), new FakeRestarter(_log), new FakeCeiling(_log) { Ceiling = 250_000 }, ec, new FakeRequest(_log))
            .SetEnabled(true);

        Assert.Equal(GpuRomOverrideStates.Unlocked, state.State);
        Assert.Equal(new[] { (145, 0) }, ec.Activations);
    }

    [Fact]
    public void Apply_WhenTheEcPublishesNoPowerTable_RefusesWithoutWriting()
    {
        var registry = new FakeRomRegistry(_log);
        var restarter = new FakeRestarter(_log);
        var ec = new FakeEc(_log) { Supported = false, Reason = "ECSpec unavailable: the EC published no power table (IOCTL 0x9C40A500)" };
        var request = new FakeRequest(_log);

        GpuRomOverrideStateDto state = NewStore(registry, restarter, new FakeCeiling(_log), ec, request).SetEnabled(true);

        Assert.Equal(GpuRomOverrideStates.Unsupported, state.State);
        Assert.Contains("DefaultTGP", state.Reason!, StringComparison.Ordinal);
        Assert.Contains("no power table", state.Reason!, StringComparison.Ordinal);
        Assert.Empty(registry.Writes);
        Assert.Empty(ec.Activations);
        Assert.Equal(0, restarter.Calls);
        Assert.Empty(request.Requests);
        Assert.False(File.Exists(OriginalsPath));
    }

    [Fact]
    public void Apply_WritesOriginalsBeforeTheFirstRegistryWrite()
    {
        // The recovery file is the only way back from a torn key, so it must exist
        // before a single record lands: the fake refuses the first write and the file
        // still has to be there.
        var registry = new FakeRomRegistry(_log) { FailFirstWrite = true };
        GpuRomOverrideStore store = NewStore(
            registry, new FakeRestarter(_log), new FakeCeiling(_log) { Ceiling = 250_000 }, new FakeEc(_log), new FakeRequest(_log));

        GpuRomOverrideStateDto state = store.SetEnabled(true);

        Assert.Equal(GpuRomOverrideStates.Uncertain, state.State);
        Assert.True(File.Exists(OriginalsPath));
        Assert.Contains("refused the write", state.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_OnAnUnpinnedVbios_RefusesWithoutWriting()
    {
        var registry = new FakeRomRegistry(_log) { Version = "98.03.5E.00.5E" };
        var restarter = new FakeRestarter(_log);
        var ec = new FakeEc(_log);

        GpuRomOverrideStateDto state = NewStore(registry, restarter, new FakeCeiling(_log), ec, new FakeRequest(_log)).SetEnabled(true);

        Assert.Equal(GpuRomOverrideStates.Unsupported, state.State);
        Assert.Contains("98.03.5E.00.5E", state.Reason!, StringComparison.Ordinal);
        Assert.Empty(registry.Records);
        Assert.Empty(ec.Activations);
        Assert.Equal(0, restarter.Calls);
        Assert.False(File.Exists(OriginalsPath));
    }

    [Fact]
    public void Apply_OnANonPinnedVersionWhoseParsedLayoutMatches_WritesTheTierValuesUnchanged()
    {
        // A tester's ROM: another version string, its own (broader) policy maxima, and the
        // seven record offsets still landing on the field each record patches. The unlock
        // exists to write above the vendor's caps, so the tier's numbers go in unchanged —
        // never clamped to the image's own maxima.
        const string version = "98.03.5E.00.1E";
        var registry = new FakeRomRegistry(_log) { Version = version };
        var probe = new FakeVbiosProbe { State = TesterImage(version, matches: true) };
        var restarter = new FakeRestarter(_log);

        GpuRomOverrideStateDto state = NewStore(
            registry, restarter, new FakeCeiling(_log) { Ceiling = 250_000 }, new FakeEc(_log),
            new FakeRequest(_log), vbios: probe).SetEnabled(true);

        Assert.Equal(GpuRomOverrideStates.Unlocked, state.State);
        Assert.Equal(1, restarter.Calls);

        // The same ceiling / rails as on the known-good image (250 W / 17.021 A / 250 A /
        // 84 A), carried with this image's own version bytes. Board input 17.021 A
        // deliberately exceeds the image's 17 A cap; NVVDD 250 A matches it.
        AssertRecord(registry.Records["romOverride00"], version, 0x57C36, 250_000);
        AssertRecord(registry.Records["romOverride01"], version, 0x57EFC, 17_021);
        AssertRecord(registry.Records["romOverride03"], version, 0x58094, 250_000);
        AssertRecord(registry.Records["romOverride05"], version, 0x580FA, 84_000);
    }

    [Fact]
    public void Apply_OnANonPinnedVersionWhoseParsedLayoutDiffers_RefusesWithoutWriting()
    {
        var registry = new FakeRomRegistry(_log) { Version = "98.03.5E.00.1E" };
        var restarter = new FakeRestarter(_log);
        var probe = new FakeVbiosProbe { State = TesterImage("98.03.5E.00.1E", matches: false) };

        GpuRomOverrideStateDto state = NewStore(
            registry, restarter, new FakeCeiling(_log), new FakeEc(_log),
            new FakeRequest(_log), vbios: probe).SetEnabled(true);

        Assert.Equal(GpuRomOverrideStates.Unsupported, state.State);
        Assert.Contains("DIFFER", state.Reason!, StringComparison.Ordinal);
        Assert.Empty(registry.Records);
        Assert.Empty(registry.Writes);
        Assert.Equal(0, restarter.Calls);
        Assert.False(File.Exists(OriginalsPath));
    }

    [Fact]
    public void Apply_RefusesAKeyAnotherToolOwns_AndLeavesItUntouched()
    {
        // MechControl's own records are on the field machine today: without our
        // originals file there is no proof they are ours, so they are never deleted
        // or overwritten — and the EC is not touched either.
        var registry = new FakeRomRegistry(_log);
        registry.Records["romOverride00"] = RomOverrideRecord.Encode("98.03.5E.00.5D", 0x57C36, 250000);
        var restarter = new FakeRestarter(_log);
        var ec = new FakeEc(_log);

        GpuRomOverrideStateDto state = NewStore(registry, restarter, new FakeCeiling(_log), ec, new FakeRequest(_log)).SetEnabled(true);

        Assert.Equal(GpuRomOverrideStates.Unsupported, state.State);
        Assert.Contains("owning tool", state.Reason!, StringComparison.Ordinal);
        Assert.True(RomOverrideRecord.TryDecode(registry.Records["romOverride00"], out _, out _, out uint value));
        Assert.Equal(250000u, value);
        Assert.Single(registry.Records);
        Assert.Empty(ec.Activations);
        Assert.Equal(0, restarter.Calls);
    }

    [Fact]
    public void Apply_RefusesForeignShapes()
    {
        // A 'VBIOS' marker, a malformed length and a record from another VBIOS are all
        // "someone else owns this key"; each refusal names the value.
        AssertRefusal(
            registry => registry.Records["VBIOS"] = Array.Empty<byte>(),
            "VBIOS");

        AssertRefusal(
            registry => registry.Records["romOverride00"] = new byte[] { 0x98, 0x03, 0x5E, 0x00, 0x5D, 0x36, 0x7C, 0x05, 0x00, 0x08, 0x00 },
            "malformed",
            withOriginals: true);

        AssertRefusal(
            registry => registry.Records["romOverride00"] = RomOverrideRecord.Encode("98.03.5E.00.5E", 0x57C36, 250000),
            "98.03.5E.00.5E",
            withOriginals: true);
    }

    [Fact]
    public void GetState_ReportsEachStateFromTheKeyTruth()
    {
        var registry = new FakeRomRegistry(_log);
        var restarter = new FakeRestarter(_log);
        var ceiling = new FakeCeiling(_log) { Ceiling = 175_000 };
        var request = new FakeRequest(_log);
        GpuRomOverrideStore store = NewStore(registry, restarter, ceiling, new FakeEc(_log), request);

        GpuRomOverrideStateDto clean = store.GetState();
        Assert.Equal(GpuRomOverrideStates.NotUnlocked, clean.State);
        Assert.False(clean.EnforcedLimitMoved);
        Assert.Equal(175000, clean.CeilingLimitMilliwatts!.Value);
        Assert.Null(clean.RequestedLimitMilliwatts);
        Assert.Equal(0, restarter.Calls);
        Assert.Equal("extra_gpu_power", clean.TierId);
        Assert.Equal(250_000, clean.PowerMilliwatts);

        ceiling.Ceiling = 250_000;
        Assert.Equal(GpuRomOverrideStates.Unlocked, store.SetEnabled(true).State);
        Assert.Equal(GpuRomOverrideStates.Unlocked, store.GetState().State);

        registry.Version = "98.03.5E.00.5F";
        Assert.Equal(GpuRomOverrideStates.Unsupported, store.GetState().State);
    }

    [Fact]
    public void GetState_AfterARestartThatClearedTheRequest_IsUncertain()
    {
        // The request does not survive a reboot (measured), so a staged key alone is not
        // an applied unlock: the card is back on its EC-derived limit.
        var request = new FakeRequest(_log);
        GpuRomOverrideStore store = NewStore(
            new FakeRomRegistry(_log), new FakeRestarter(_log), new FakeCeiling(_log) { Ceiling = 250_000 }, new FakeEc(_log), request);

        Assert.Equal(GpuRomOverrideStates.Unlocked, store.SetEnabled(true).State);

        request.Forget();   // what a GPU restart does to the driver's request

        GpuRomOverrideStateDto after = store.GetState();
        Assert.Equal(GpuRomOverrideStates.Uncertain, after.State);
        Assert.Contains("no power request", after.Reason!, StringComparison.Ordinal);
        Assert.Null(after.RequestedLimitMilliwatts);
    }

    [Fact]
    public void Apply_WhenTheDriverKeepsTheStockCeiling_IsUncertain()
    {
        // The records can be staged and read back perfectly while the driver never
        // consumes them: the ceiling is the witness that matters, and the reason names
        // both numbers as they are.
        var ceiling = new FakeCeiling(_log) { Ceiling = 175_000, Current = 150_000 };

        GpuRomOverrideStateDto state = NewStore(
            new FakeRomRegistry(_log), new FakeRestarter(_log), ceiling, new FakeEc(_log), new FakeRequest(_log)).SetEnabled(true);

        Assert.Equal(GpuRomOverrideStates.Uncertain, state.State);
        Assert.Equal("the driver reports a 175 W ceiling, not 225 W", state.Reason);
        Assert.True(state.Restarted);

        // The current limit is reported next to the ceiling and never used as the witness.
        Assert.Equal(150_000, state.CurrentLimitMilliwatts!.Value);
    }

    [Fact]
    public void Apply_WithTheCeilingAtTheTier_IsUnlocked()
    {
        GpuRomOverrideStateDto state = NewStore(
            new FakeRomRegistry(_log), new FakeRestarter(_log),
            new FakeCeiling(_log) { Ceiling = 250_000, Current = 95_000 }, new FakeEc(_log), new FakeRequest(_log)).SetEnabled(true);

        Assert.Equal(GpuRomOverrideStates.Unlocked, state.State);
        Assert.Null(state.Reason);
        Assert.Equal(250_000, state.CeilingLimitMilliwatts!.Value);
        Assert.Equal(95_000, state.CurrentLimitMilliwatts!.Value);
        Assert.Equal(225_000, state.RequestedLimitMilliwatts!.Value);
    }

    [Fact]
    public void Apply_WhenTheRequestIsRefused_IsUncertain_AndNamesTheFailedStep()
    {
        // The measured failure this tier shipped with: records + ceiling, but nothing
        // asked the driver, so the card stayed at 150 W. Here the request itself fails
        // and the reason says which step, with the numbers read.
        var request = new FakeRequest(_log)
        {
            Failure = new GpuPowerRequestState(null, 5000, 250_000, "NVAPI interface 0x17695269 unavailable"),
        };

        GpuRomOverrideStateDto state = NewStore(
            new FakeRomRegistry(_log), new FakeRestarter(_log), new FakeCeiling(_log) { Ceiling = 250_000 }, new FakeEc(_log), request)
            .SetEnabled(true);

        Assert.Equal(GpuRomOverrideStates.Uncertain, state.State);
        Assert.Contains("the 225 W power request failed", state.Reason!, StringComparison.Ordinal);
        Assert.Contains("NVAPI interface 0x17695269 unavailable", state.Reason!, StringComparison.Ordinal);
        Assert.Contains("ceiling is 250 W", state.Reason!, StringComparison.Ordinal);
        Assert.True(state.Restarted);
    }

    [Fact]
    public void Apply_WhenTheDriverCarriesAnotherRequest_IsUncertain()
    {
        // Unlocked means the driver carries the target: any other request is reported
        // as the number it actually holds.
        var request = new FakeRequest(_log) { Answer = new GpuPowerRequestState(220_000, 5000, 250_000, null) };

        GpuRomOverrideStateDto state = NewStore(
            new FakeRomRegistry(_log), new FakeRestarter(_log), new FakeCeiling(_log) { Ceiling = 250_000 }, new FakeEc(_log), request)
            .SetEnabled(true);

        Assert.Equal(GpuRomOverrideStates.Uncertain, state.State);
        Assert.Equal("the driver reports a 220 W power request, not 225 W", state.Reason);
        Assert.Equal(220_000, state.RequestedLimitMilliwatts!.Value);
    }

    [Fact]
    public void Apply_WhenTheBudgetCannotBeRead_IsUncertain_AndDoesNotAskTheDriver()
    {
        // The F7 value is unreadable (measured after a driver reset): the records and the
        // EC are already staged, so the state is partial and says which move failed. The
        // request is not made — it would be capped by a budget nobody can read.
        var request = new FakeRequest(_log);
        var budget = new FakeBudget(_log) { Failure = "the laptop power budget is unreadable; restart the GPU" };

        GpuRomOverrideStateDto state = NewStore(
            new FakeRomRegistry(_log), new FakeRestarter(_log), new FakeCeiling(_log) { Ceiling = 250_000 },
            new FakeEc(_log), request, budget)
            .SetEnabled(true);

        Assert.Equal(GpuRomOverrideStates.Uncertain, state.State);
        Assert.Equal(
            "the records are staged and the EC is offset, but the laptop power budget could not be read: " +
            "the laptop power budget is unreadable; restart the GPU",
            state.Reason);
        Assert.True(state.Restarted);
        Assert.Empty(request.Requests);
        Assert.Null(state.BudgetMilliwatts);
    }

    [Fact]
    public void Apply_WhenTheBudgetRefusesTheRaise_IsUncertain_AndNamesTheFailedStep()
    {
        var request = new FakeRequest(_log);
        var budget = new FakeBudget(_log) { Refused = "the F7 dispatch signature was not found in this nvapi64" };

        GpuRomOverrideStateDto state = NewStore(
            new FakeRomRegistry(_log), new FakeRestarter(_log), new FakeCeiling(_log) { Ceiling = 250_000 },
            new FakeEc(_log), request, budget)
            .SetEnabled(true);

        Assert.Equal(GpuRomOverrideStates.Uncertain, state.State);
        Assert.Equal(
            "the records are staged and the EC is offset, but the laptop power budget could not be raised to 225 W: " +
            "the F7 dispatch signature was not found in this nvapi64",
            state.Reason);
        Assert.Empty(request.Requests);
        Assert.Equal(150_000, state.BudgetMilliwatts);
    }

    [Fact]
    public void Apply_WhenTheBudgetIsAlreadyAboveTheTarget_DoesNotLowerIt()
    {
        // The EC can publish more than the tier asks for (Dynamic Boost): that is not a
        // raise, and the budget is never brought down to the target.
        var budget = new FakeBudget(_log) { Original = new GpuPowerBudget(250_000, 500_000) };

        GpuRomOverrideStateDto state = NewStore(
            new FakeRomRegistry(_log), new FakeRestarter(_log), new FakeCeiling(_log) { Ceiling = 250_000 },
            new FakeEc(_log), new FakeRequest(_log), budget)
            .SetEnabled(true);

        Assert.Equal(GpuRomOverrideStates.Unlocked, state.State);
        Assert.Empty(budget.Sets);
        Assert.Equal(250_000, state.BudgetMilliwatts);
    }

    [Fact]
    public void GetState_ReportsTheBudgetAndSaysWhenItIsUnreadable()
    {
        var budget = new FakeBudget(_log);
        GpuRomOverrideStore store = NewStore(
            new FakeRomRegistry(_log), new FakeRestarter(_log), new FakeCeiling(_log) { Ceiling = 250_000 },
            new FakeEc(_log), new FakeRequest(_log), budget);

        Assert.Equal(GpuRomOverrideStates.Unlocked, store.SetEnabled(true).State);
        Assert.Equal(225_000, store.GetState().BudgetMilliwatts);

        budget.Failure = "the laptop power budget is unreadable; restart the GPU";
        GpuRomOverrideStateDto unread = store.GetState();

        Assert.Equal(GpuRomOverrideStates.Uncertain, unread.State);
        Assert.Equal(
            "the laptop power budget could not be read: the laptop power budget is unreadable; restart the GPU",
            unread.Reason);
        Assert.Null(unread.BudgetMilliwatts);
    }

    [Fact]
    public void Restore_PutsTheBudgetBackBeforeTheRecordsGo()
    {
        var budget = new FakeBudget(_log);
        var ceiling = new FakeCeiling(_log) { Ceiling = 250_000 };
        GpuRomOverrideStore store = NewStore(
            new FakeRomRegistry(_log), new FakeRestarter(_log), ceiling,
            new FakeEc(_log), new FakeRequest(_log), budget);

        Assert.Equal(GpuRomOverrideStates.Unlocked, store.SetEnabled(true).State);
        Assert.Equal(new GpuPowerBudget(225_000, 500_000), budget.Current);

        ceiling.Ceiling = 175_000;
        _log.Clear();
        Assert.Equal(GpuRomOverrideStates.NotUnlocked, store.SetEnabled(false).State);

        // The captured baseline comes back, and it happens before the records are dropped:
        // the raise is one of the staged moves and must not outlive them.
        Assert.Equal(new GpuPowerBudget(150_000, 425_000), budget.LastRestored);
        int restoreIndex = _log.IndexOf("budget.restore:150000,425000");
        int deleteIndex = _log.IndexOf("delete:romOverride00");
        int deactivateIndex = _log.IndexOf("ec.deactivate");
        int restartIndex = _log.IndexOf("restart");
        Assert.True(restoreIndex >= 0 && restoreIndex < deleteIndex, string.Join(",", _log));
        Assert.True(deleteIndex < deactivateIndex, string.Join(",", _log));
        Assert.True(deactivateIndex < restartIndex, string.Join(",", _log));
    }

    [Fact]
    public void Restore_LeavesAPlatformBudgetAnotherWriterMoved()
    {
        // The reference's restore only rewrites a half that still carries what we applied:
        // a platform budget someone else raised is theirs to keep.
        var budget = new FakeBudget(_log);
        var ceiling = new FakeCeiling(_log) { Ceiling = 250_000 };
        GpuRomOverrideStore store = NewStore(
            new FakeRomRegistry(_log), new FakeRestarter(_log), ceiling,
            new FakeEc(_log), new FakeRequest(_log), budget);

        Assert.Equal(GpuRomOverrideStates.Unlocked, store.SetEnabled(true).State);
        budget.Current = new GpuPowerBudget(225_000, 460_000);   // platform moved by another writer

        ceiling.Ceiling = 175_000;
        Assert.Equal(GpuRomOverrideStates.NotUnlocked, store.SetEnabled(false).State);
        Assert.Equal(new GpuPowerBudget(150_000, 460_000), budget.LastRestored);
    }

    [Fact]
    public void Apply_WhenNvmlRefuses_ReportsBothLimitsAsUnread_AndStaysUncertain()    {
        // A refused read is unknown, never "not moved": the card shows 'not read'.
        GpuRomOverrideStateDto state = NewStore(
            new FakeRomRegistry(_log), new FakeRestarter(_log), new FakeCeiling(_log), new FakeEc(_log), new FakeRequest(_log))
            .SetEnabled(true);

        Assert.Equal(GpuRomOverrideStates.Uncertain, state.State);
        Assert.Null(state.CeilingLimitMilliwatts);
        Assert.Null(state.CurrentLimitMilliwatts);
        Assert.Contains("did not report a power ceiling", state.Reason!, StringComparison.Ordinal);
        Assert.False(state.EnforcedLimitMoved);
    }

    [Fact]
    public void GetState_MapsBothProbeReadsIntoTheDto()
    {
        // The probe's two numbers land in their own fields: the ceiling from the
        // constraint maximum, the current limit from the limit call. Nothing is derived.
        var ceiling = new FakeCeiling(_log) { Ceiling = 250_000, Current = 150_000 };
        GpuRomOverrideStateDto state = NewStore(
            new FakeRomRegistry(_log), new FakeRestarter(_log), ceiling, new FakeEc(_log), new FakeRequest(_log)).GetState();

        Assert.Equal(250_000, state.CeilingLimitMilliwatts!.Value);
        Assert.Equal(150_000, state.CurrentLimitMilliwatts!.Value);

        ceiling.Ceiling = null;
        ceiling.Current = null;
        GpuRomOverrideStateDto unread = NewStore(
            new FakeRomRegistry(_log), new FakeRestarter(_log), ceiling, new FakeEc(_log), new FakeRequest(_log)).GetState();

        Assert.Null(unread.CeilingLimitMilliwatts);
        Assert.Null(unread.CurrentLimitMilliwatts);
    }

    [Fact]
    public void Apply_WhenTheRecordsDoNotReadBack_IsUncertain()
    {
        var registry = new FakeRomRegistry(_log) { DropWrites = true };

        GpuRomOverrideStateDto state = NewStore(
            registry, new FakeRestarter(_log), new FakeCeiling(_log) { Ceiling = 250_000 }, new FakeEc(_log), new FakeRequest(_log))
            .SetEnabled(true);

        Assert.Equal(GpuRomOverrideStates.Uncertain, state.State);
        Assert.Contains("read back", state.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_WhenTheRestartFails_IsUncertainAndSaysSo()
    {
        var restarter = new FakeRestarter(_log) { Failure = new TimeoutException("the GPU restart did not finish within 60 s") };

        GpuRomOverrideStateDto state = NewStore(
            new FakeRomRegistry(_log), restarter, new FakeCeiling(_log) { Ceiling = 175_000 }, new FakeEc(_log), new FakeRequest(_log))
            .SetEnabled(true);

        Assert.Equal(GpuRomOverrideStates.Uncertain, state.State);
        Assert.Contains("60 s", state.Reason!, StringComparison.Ordinal);
        Assert.False(state.Restarted);
    }

    [Fact]
    public void Apply_IsIdempotent_ButReassertsTheEcAndTheRequest()
    {
        var registry = new FakeRomRegistry(_log);
        var restarter = new FakeRestarter(_log);
        var ec = new FakeEc(_log);
        var request = new FakeRequest(_log);
        GpuRomOverrideStore store = NewStore(registry, restarter, new FakeCeiling(_log) { Ceiling = 250_000 }, ec, request);

        store.SetEnabled(true);
        int writesBefore = registry.Writes.Count;
        GpuRomOverrideStateDto again = store.SetEnabled(true);

        Assert.Equal(GpuRomOverrideStates.Unlocked, again.State);
        Assert.Equal(writesBefore, registry.Writes.Count);
        Assert.Equal(1, restarter.Calls);   // no second GPU restart for a no-op
        Assert.False(again.Restarted);

        // The EC byte and the driver's request are not persistent: both are re-asserted.
        Assert.Equal(new[] { (130, 0), (130, 0) }, ec.Activations);
        Assert.Equal(new[] { 225_000, 225_000 }, request.Requests);
    }

    [Fact]
    public void Restore_DeletesOnlyOurRecords_AndIsSafeWhenNothingIsPatched()
    {
        var registry = new FakeRomRegistry(_log);
        var restarter = new FakeRestarter(_log);
        var ceiling = new FakeCeiling(_log) { Ceiling = 175_000 };
        var ec = new FakeEc(_log);
        var request = new FakeRequest(_log);
        GpuRomOverrideStore store = NewStore(registry, restarter, ceiling, ec, request);

        // Nothing patched: a no-op that neither restarts nor writes.
        GpuRomOverrideStateDto untouched = store.SetEnabled(false);
        Assert.Equal(GpuRomOverrideStates.NotUnlocked, untouched.State);
        Assert.Equal(0, restarter.Calls);
        Assert.False(untouched.Restarted);
        Assert.Equal(0, ec.Deactivations);

        ceiling.Ceiling = 250_000;
        Assert.Equal(GpuRomOverrideStates.Unlocked, store.SetEnabled(true).State);
        Assert.True(File.Exists(OriginalsPath));

        ceiling.Ceiling = 175_000;
        _log.Clear();
        GpuRomOverrideStateDto restored = store.SetEnabled(false);

        Assert.Equal(GpuRomOverrideStates.NotUnlocked, restored.State);
        Assert.True(restored.Restarted);
        Assert.Empty(registry.Records);
        Assert.False(File.Exists(OriginalsPath));
        Assert.Equal(2, restarter.Calls);
        Assert.Null(restored.RequestedLimitMilliwatts);

        // Ordering: the records go, then the EC byte, then the restart (which is what
        // clears the driver's request).
        int deleteIndex = _log.IndexOf("delete:romOverride00");
        int deactivateIndex = _log.IndexOf("ec.deactivate");
        int restartIndex = _log.IndexOf("restart");
        Assert.True(deleteIndex >= 0 && deleteIndex < deactivateIndex, string.Join(",", _log));
        Assert.True(deactivateIndex < restartIndex, string.Join(",", _log));
        Assert.Equal(1, ec.Deactivations);

        // Idempotent: a second restore changes nothing and does not restart again.
        GpuRomOverrideStateDto second = store.SetEnabled(false);
        Assert.Equal(GpuRomOverrideStates.NotUnlocked, second.State);
        Assert.False(second.Restarted);
        Assert.Equal(2, restarter.Calls);
    }

    [Fact]
    public void Restore_WhenTheRequestSurvivedTheRestart_IsUncertain()
    {
        // A request that outlived the records would keep the card at the raised limit
        // with a stock key, so it is reported instead of silently left behind.
        var registry = new FakeRomRegistry(_log);
        var restarter = new FakeRestarter(_log);
        var ceiling = new FakeCeiling(_log) { Ceiling = 175_000 };
        var request = new FakeRequest(_log)
        {
            // A restart clears the request on the measured machine; this one does not.
            SurvivesRestart = true,
        };
        GpuRomOverrideStore store = NewStore(registry, restarter, ceiling, new FakeEc(_log), request);

        ceiling.Ceiling = 250_000;
        Assert.Equal(GpuRomOverrideStates.Unlocked, store.SetEnabled(true).State);

        ceiling.Ceiling = 175_000;
        GpuRomOverrideStateDto restored = store.SetEnabled(false);

        Assert.Equal(GpuRomOverrideStates.Uncertain, restored.State);
        Assert.Equal("the records are gone but the driver still carries a 225 W power request", restored.Reason);
    }

    [Fact]
    public void Restore_RefusesRecordsAnotherToolOwns()
    {
        var registry = new FakeRomRegistry(_log);
        registry.Records["romOverride01"] = RomOverrideRecord.Encode("98.03.5E.00.5D", 0x57EFC, 17021);
        var restarter = new FakeRestarter(_log);
        var ec = new FakeEc(_log);

        GpuRomOverrideStateDto state = NewStore(registry, restarter, new FakeCeiling(_log), ec, new FakeRequest(_log)).SetEnabled(false);

        Assert.Equal(GpuRomOverrideStates.Unsupported, state.State);
        Assert.Single(registry.Records);
        Assert.Empty(registry.Deletes);
        Assert.Equal(0, restarter.Calls);
        Assert.Equal(0, ec.Deactivations);
    }

    [Fact]
    public void Restore_DropsAStaleOriginalsFile()
    {
        // Records already gone (a driver reinstall undoes the patch): the file is no
        // longer a claim on anything, so it is removed instead of kept as a lure.
        var registry = new FakeRomRegistry(_log);
        Directory.CreateDirectory(Path.GetDirectoryName(OriginalsPath)!);
        File.WriteAllLines(OriginalsPath, new[] { "98.03.5E.00.5D", "359478=175000" });

        GpuRomOverrideStateDto state = NewStore(
            registry, new FakeRestarter(_log), new FakeCeiling(_log), new FakeEc(_log), new FakeRequest(_log)).SetEnabled(false);

        Assert.Equal(GpuRomOverrideStates.NotUnlocked, state.State);
        Assert.Contains("stale", state.Reason!, StringComparison.Ordinal);
        Assert.False(File.Exists(OriginalsPath));
    }

    [Fact]
    public void Originals_MalformedFileIsRefusedWithoutTouchingTheKey()
    {
        var registry = new FakeRomRegistry(_log);
        registry.Records["romOverride00"] = RomOverrideRecord.Encode("98.03.5E.00.5D", 0x57C36, 250000);
        Directory.CreateDirectory(Path.GetDirectoryName(OriginalsPath)!);
        File.WriteAllLines(OriginalsPath, new[] { "98.03.5E.00.5D", "not-an-offset=175000" });
        var restarter = new FakeRestarter(_log);

        GpuRomOverrideStateDto state = NewStore(
            registry, restarter, new FakeCeiling(_log), new FakeEc(_log), new FakeRequest(_log)).SetEnabled(false);

        Assert.Equal(GpuRomOverrideStates.Unsupported, state.State);
        Assert.Contains("malformed", state.Reason!, StringComparison.Ordinal);
        Assert.Single(registry.Records);
        Assert.Equal(0, restarter.Calls);
    }

    [Fact]
    public void Apply_SetsTheTierRails_AfterTheRestart_AndBeforeTheBudget()
    {
        // The A/B this lever comes from: identical records, budget and request, and the
        // board held 175-179 W on the stock rails where the tier's rails reached 205-214 W.
        var restarter = new FakeRestarter(_log);
        var rails = new FakeRails(_log);
        var budget = new FakeBudget(_log);

        GpuRomOverrideStateDto state = NewStore(
            new FakeRomRegistry(_log), restarter, new FakeCeiling(_log) { Ceiling = 250_000 },
            new FakeEc(_log), new FakeRequest(_log), budget, rails)
            .SetEnabled(true);

        Assert.Equal(GpuRomOverrideStates.Unlocked, state.State);
        Assert.Single(rails.Sets);
        Assert.Equal(250_000, rails.Sets[0].Nvvdd);
        Assert.Equal(70_000, rails.Sets[0].Msvdd);
        Assert.Equal(14_026, rails.Sets[0].Board);

        // The applied values are what the card reads back, not what was requested.
        Assert.Equal(250_000, state.NvvddCurrentMilliamps);
        Assert.Equal(70_000, state.MsvddCurrentMilliamps);
        Assert.Equal(14_026, state.BoardInputCurrentMilliamps);

        // The restart reloads the driver's own defaults, so the rails go after it; the
        // budget and the request follow, as they did before.
        int restartIndex = _log.IndexOf("restart");
        int railsIndex = _log.IndexOf("rails.set:250000,70000,14026");
        int budgetIndex = _log.IndexOf("budget.set:225000,500000");
        int requestIndex = _log.IndexOf("request:225000");
        Assert.True(restartIndex >= 0 && restartIndex < railsIndex, string.Join(",", _log));
        Assert.True(railsIndex < budgetIndex, string.Join(",", _log));
        Assert.True(budgetIndex < requestIndex, string.Join(",", _log));
    }

    [Fact]
    public void Apply_WhenTheRailPolicyDidNotWiden_IsUncertain_NamingTheRailAndBothNumbers()
    {
        // The records did not take (or the driver kept the stock policy): the tier's rail
        // does not fit the live range. That is a refusal with both numbers, never a skip —
        // a silent skip would report an unlock that cannot draw the watts.
        var request = new FakeRequest(_log);
        var rails = new FakeRails(_log)
        {
            Refused = "the NVVDD rail 250000 mA is outside the live rail policy 210000-210000 mA",
        };

        GpuRomOverrideStateDto state = NewStore(
            new FakeRomRegistry(_log), new FakeRestarter(_log), new FakeCeiling(_log) { Ceiling = 250_000 },
            new FakeEc(_log), request, rails: rails)
            .SetEnabled(true);

        Assert.Equal(GpuRomOverrideStates.Uncertain, state.State);
        Assert.Equal(
            "the records are staged and the EC is offset, but the runtime rails could not be set: " +
            "the NVVDD rail 250000 mA is outside the live rail policy 210000-210000 mA",
            state.Reason);

        // Nothing past the failed step was done: no budget raise, no request.
        Assert.Empty(request.Requests);
    }

    [Fact]
    public void Apply_WhenTheRailsDoNotHold_IsUncertain()
    {
        // The set is accepted and the driver keeps the stock current: the readback after
        // the request is what catches it.
        var rails = new FakeRails(_log) { DropSets = true };

        GpuRomOverrideStateDto state = NewStore(
            new FakeRomRegistry(_log), new FakeRestarter(_log), new FakeCeiling(_log) { Ceiling = 250_000 },
            new FakeEc(_log), new FakeRequest(_log), rails: rails)
            .SetEnabled(true);

        Assert.Equal(GpuRomOverrideStates.Uncertain, state.State);
        Assert.Equal("the NVVDD rail reads 210000 mA, not the requested 250000 mA", state.Reason);
        Assert.Equal(210_000, state.NvvddCurrentMilliamps);
    }

    [Fact]
    public void State_AfterAFailedRailsApply_DoesNotReportUnlocked()
    {
        // Field 0.9.12: the apply stopped at the rails (NVAPI -6 after the GPU restart) while
        // the records, the EC offset, the budget and — from the unlock before the reboot —
        // the driver's request all still matched. The status verb reads the live witnesses
        // again, so the rails are one of them: a card at stock currents is not unlocked.
        var registry = new FakeRomRegistry(_log);
        var ceiling = new FakeCeiling(_log) { Ceiling = 250_000 };
        var ec = new FakeEc(_log);
        var request = new FakeRequest(_log) { Answer = new GpuPowerRequestState(225_000, 5_000, 250_000, null) };
        var budget = new FakeBudget(_log) { Current = new GpuPowerBudget(225_000, 500_000) };
        var rails = new FakeRails(_log) { Refused = "EnumPhysicalGPUs failed (status -6)" };
        GpuRomOverrideStore store = NewStore(registry, new FakeRestarter(_log), ceiling, ec, request, budget, rails);

        Assert.Equal(GpuRomOverrideStates.Uncertain, store.SetEnabled(true).State);

        // What the earlier unlock left in the driver: the budget raised and the request made
        // (the restart republished the EC's own 150 W budget).
        budget.Current = new GpuPowerBudget(225_000, 500_000);

        GpuRomOverrideStateDto sameRun = store.GetState();
        Assert.Equal(GpuRomOverrideStates.Uncertain, sameRun.State);
        Assert.Contains("rails", sameRun.Reason!, StringComparison.Ordinal);

        // The engine restarted since (no remembered verdict): the live rail read alone must
        // keep the same answer.
        GpuRomOverrideStateDto nextRun = NewStore(
            registry, new FakeRestarter(_log), ceiling, new FakeEc(_log), request, budget, rails).GetState();

        Assert.Equal(GpuRomOverrideStates.Uncertain, nextRun.State);
        Assert.Contains("NVVDD", nextRun.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void Restore_PutsTheRailsBackToStock_BeforeTheRestart()
    {
        var rails = new FakeRails(_log);
        var ceiling = new FakeCeiling(_log) { Ceiling = 250_000 };
        GpuRomOverrideStore store = NewStore(
            new FakeRomRegistry(_log), new FakeRestarter(_log), ceiling, new FakeEc(_log), new FakeRequest(_log), rails: rails);

        Assert.Equal(GpuRomOverrideStates.Unlocked, store.SetEnabled(true).State);

        ceiling.Ceiling = 175_000;
        _log.Clear();
        GpuRomOverrideStateDto restored = store.SetEnabled(false);

        Assert.Equal(GpuRomOverrideStates.NotUnlocked, restored.State);
        Assert.Equal(210_000, restored.NvvddCurrentMilliamps);
        Assert.Equal(60_000, restored.MsvddCurrentMilliamps);
        Assert.Equal(11_053, restored.BoardInputCurrentMilliamps);

        // budget, records, EC, rails, restart — the rails are runtime values, so they are
        // put back before the only step that can drop them.
        int deleteIndex = _log.IndexOf("delete:romOverride00");
        int deactivateIndex = _log.IndexOf("ec.deactivate");
        int railsIndex = _log.IndexOf("rails.set:210000,60000,11053");
        int restartIndex = _log.IndexOf("restart");
        Assert.True(deleteIndex >= 0 && deleteIndex < deactivateIndex, string.Join(",", _log));
        Assert.True(deactivateIndex < railsIndex, string.Join(",", _log));
        Assert.True(railsIndex < restartIndex, string.Join(",", _log));
    }

    [Fact]
    public void Restore_WhenTheRailsWillNotComeBack_IsUncertain()
    {
        var rails = new FakeRails(_log);
        var ceiling = new FakeCeiling(_log) { Ceiling = 250_000 };
        GpuRomOverrideStore store = NewStore(
            new FakeRomRegistry(_log), new FakeRestarter(_log), ceiling, new FakeEc(_log), new FakeRequest(_log), rails: rails);

        Assert.Equal(GpuRomOverrideStates.Unlocked, store.SetEnabled(true).State);

        rails.Refused = "the NVVDD rail policy is not available on this driver";
        ceiling.Ceiling = 175_000;
        GpuRomOverrideStateDto restored = store.SetEnabled(false);

        Assert.Equal(GpuRomOverrideStates.Uncertain, restored.State);
        Assert.Equal(
            "the records are gone but the runtime rails were not restored: " +
            "the NVVDD rail policy is not available on this driver",
            restored.Reason);
    }

    [Fact]
    public void Restore_WithNothingStaged_DoesNotTouchStockRails()
    {
        // A no-op restore must not report a rails failure for a mechanism it never set: the
        // stock currents are left alone, so a machine whose rails interface is unusable still
        // answers NotUnlocked instead of drifting into Uncertain.
        var rails = new FakeRails(_log) { Failure = "NVAPI interface 0x67F31384 unavailable" };

        GpuRomOverrideStateDto state = NewStore(
            new FakeRomRegistry(_log), new FakeRestarter(_log), new FakeCeiling(_log), new FakeEc(_log),
            new FakeRequest(_log), rails: rails)
            .SetEnabled(false);

        Assert.Equal(GpuRomOverrideStates.NotUnlocked, state.State);
        Assert.Null(state.Reason);
        Assert.Empty(rails.Sets);
    }

    [Fact]
    public void Restore_WithNothingStaged_PutsStillRaisedRailsBack()
    {
        // The records can be gone (a driver reinstall) while the runtime rails still carry the
        // unlock: they are put back even though the key is already clean, without a restart.
        var rails = new FakeRails(_log) { NvVddCurrent = 250_000, MsVddCurrent = 70_000, BoardCurrent = 14_026 };
        var restarter = new FakeRestarter(_log);

        GpuRomOverrideStateDto state = NewStore(
            new FakeRomRegistry(_log), restarter, new FakeCeiling(_log), new FakeEc(_log),
            new FakeRequest(_log), rails: rails)
            .SetEnabled(false);

        Assert.Equal(GpuRomOverrideStates.NotUnlocked, state.State);
        Assert.Single(rails.Sets);
        Assert.Equal(210_000, rails.Sets[0].Nvvdd);
        Assert.Equal(210_000, state.NvvddCurrentMilliamps);
        Assert.Equal(0, restarter.Calls);
    }

    [Fact]
    public void RailLimits_ReportTheLivePolicy_AndARefusalCarriesNoNumbers()    {
        // The sliders' bound is the driver's own policy: the stock maxima before the unlock,
        // the widened ones after it, and nothing at all when the driver cannot be asked.
        var rails = new FakeRails(_log);
        GpuRomOverrideStore store = NewStore(
            new FakeRomRegistry(_log), new FakeRestarter(_log), new FakeCeiling(_log), new FakeEc(_log),
            new FakeRequest(_log), rails: rails);

        GpuRailLimitsDto stock = store.GetRailLimits();
        Assert.Null(stock.Reason);
        Assert.Equal(210_000, stock.NvvddMilliamps);
        Assert.Equal(294_000, stock.NvvddMaxMilliamps);
        Assert.Equal(11_053, stock.BoardInputMilliamps);
        Assert.Equal(17_021, stock.BoardInputMaxMilliamps);

        // The set answers the same shape: values passed through, live ranges alongside.
        GpuRailLimitsDto moved = store.SetRailLimits(250_000, 70_000, 14_026);
        Assert.Equal(250_000, moved.NvvddMilliamps);
        Assert.Equal(70_000, moved.MsvddMilliamps);
        Assert.Equal(14_026, moved.BoardInputMilliamps);
        Assert.Equal(84_000, moved.MsvddMaxMilliamps);

        rails.Failure = "NVAPI interface 0x67F31384 unavailable";
        GpuRailLimitsDto refused = store.GetRailLimits();
        Assert.Equal("NVAPI interface 0x67F31384 unavailable", refused.Reason);
        Assert.Null(refused.NvvddMilliamps);
        Assert.Null(refused.NvvddMaxMilliamps);
        Assert.Null(refused.BoardInputMilliamps);
    }

    [Fact]
    public void StartupReapply_RunsTheLiveHalfInOrder_WithoutTouchingTheRecordsOrTheGpu()
    {
        // The measured reboot: the seven records and the originals file survive, while the
        // EC offset, the rails, the F7 budget and the driver's request do not. The re-apply
        // is the same four live moves as an apply, and it never rewrites a record or
        // restarts the adapter (a boot-time restart would blank the screen every boot).
        var registry = new FakeRomRegistry(_log);
        var restarter = new FakeRestarter(_log);
        var ceiling = new FakeCeiling(_log) { Ceiling = 250_000, Current = 150_000 };
        var ec = new FakeEc(_log);
        var request = new FakeRequest(_log);
        var budget = new FakeBudget(_log);
        var rails = new FakeRails(_log);
        GpuRomOverrideStore store = NewStore(registry, restarter, ceiling, ec, request, budget, rails);

        Assert.Equal(GpuRomOverrideStates.Unlocked, store.SetEnabled(true).State);
        Assert.True(File.Exists(MarkerPath));

        request.Forget();
        budget.Reset();
        rails.NvVddCurrent = RomOverrideLayout.StockNvVddMilliAmps;
        rails.MsVddCurrent = RomOverrideLayout.StockMsVddMilliAmps;
        rails.BoardCurrent = RomOverrideLayout.StockBoardInputMilliAmps;
        _log.Clear();
        int writesBefore = registry.Writes.Count;
        int requestsBefore = request.Requests.Count;

        GpuReapplyResult result = store.ReapplyAtStartup();

        Assert.Equal(GpuReapplyVerdict.Applied, result.Verdict);
        GpuRomOverrideStateDto state = result.State!;
        Assert.Equal(GpuRomOverrideStates.Unlocked, state.State);
        Assert.Null(state.Reason);
        Assert.True(state.Enabled);
        Assert.False(state.Restarted);
        Assert.Equal(1, restarter.Calls);
        Assert.Equal(writesBefore, registry.Writes.Count);
        Assert.Empty(registry.Deletes);

        // The same order as an apply, minus the records and the restart.
        int ecIndex = _log.IndexOf("ec.activate:130,0");
        int railsIndex = _log.IndexOf("rails.set:250000,70000,14026");
        int budgetIndex = _log.IndexOf("budget.set:225000,500000");
        int requestIndex = _log.IndexOf("request:225000");
        Assert.True(ecIndex >= 0 && ecIndex < railsIndex, string.Join(",", _log));
        Assert.True(railsIndex < budgetIndex, string.Join(",", _log));
        Assert.True(budgetIndex < requestIndex, string.Join(",", _log));

        Assert.Equal(requestsBefore + 1, request.Requests.Count);
        Assert.Equal(225_000, request.Requests[^1]);
        Assert.Equal(225_000, state.RequestedLimitMilliwatts);
        Assert.Equal(250_000, state.NvvddCurrentMilliamps);
    }

    [Fact]
    public void StartupReapply_WithoutTheStagedRecords_WritesNothing_AndStaysTruthful()
    {
        // A driver reinstall can remove the records while the marker and the originals file
        // stay. Guessing would write somebody's numbers: the state stays what the key says.
        // (An empty key is also what a not-yet-enumerated adapter looks like at boot, hence
        // the transient verdict: the retry is live and bounded, the write still is not.)
        SeedOriginalsAndMarker();

        var registry = new FakeRomRegistry(_log);
        var ec = new FakeEc(_log);
        var request = new FakeRequest(_log);
        var rails = new FakeRails(_log);
        var restarter = new FakeRestarter(_log);

        GpuReapplyResult result = NewStore(
            registry, restarter, new FakeCeiling(_log), ec, request, rails: rails).ReapplyAtStartup();

        Assert.Equal(GpuReapplyVerdict.Transient, result.Verdict);
        Assert.Equal(GpuRomOverrideStates.NotUnlocked, result.State!.State);
        Assert.False(result.State.Enabled);
        Assert.Empty(registry.Writes);
        Assert.Empty(ec.Activations);
        Assert.Empty(request.Requests);
        Assert.Empty(rails.Sets);
        Assert.Equal(0, restarter.Calls);
    }

    [Fact]
    public void StartupReapply_RefusesAKeyAnotherToolOwns()
    {
        SeedOriginalsAndMarker();

        var registry = new FakeRomRegistry(_log);
        registry.Records["VBIOS"] = Array.Empty<byte>();
        var ec = new FakeEc(_log);

        GpuReapplyResult result = NewStore(
            registry, new FakeRestarter(_log), new FakeCeiling(_log), ec, new FakeRequest(_log)).ReapplyAtStartup();

        Assert.Equal(GpuReapplyVerdict.Refused, result.Verdict);
        Assert.Equal(GpuRomOverrideStates.Unsupported, result.State!.State);
        Assert.Contains("VBIOS", result.State.Reason!, StringComparison.Ordinal);
        Assert.Empty(registry.Writes);
        Assert.Empty(ec.Activations);
    }

    [Fact]
    public void StartupReapply_WithRecordsThatAreNotTheTier_DoesNotGuess()
    {
        // Ours by shape (the VBIOS prefix and an offset the originals file owns), but a
        // 220 W ceiling: not this build's tier, so nothing is re-applied and no retry can
        // turn that into this build's tier.
        SeedOriginalsAndMarker();

        var registry = new FakeRomRegistry(_log);
        registry.Records["romOverride00"] =
            RomOverrideRecord.Encode(RomOverrideLayout.PinnedVbiosVersion, 0x57C36, 220_000);
        var ec = new FakeEc(_log);
        var request = new FakeRequest(_log);

        GpuReapplyResult result = NewStore(
            registry, new FakeRestarter(_log), new FakeCeiling(_log) { Ceiling = 250_000 }, ec, request).ReapplyAtStartup();

        Assert.Equal(GpuReapplyVerdict.Refused, result.Verdict);
        Assert.Equal(GpuRomOverrideStates.Uncertain, result.State!.State);
        Assert.Empty(registry.Writes);
        Assert.Empty(ec.Activations);
        Assert.Empty(request.Requests);
    }

    [Fact]
    public void StartupReapply_WithAMarkerForAnotherTier_StillReapplies()
    {
        // The marker is informational: a build that shipped another tier once wrote it, and
        // it may name that tier. The records and the originals file are the intent, so the
        // re-apply happens — the marker cannot veto it.
        SeedOriginalsAndMarker(tier: "some_other_tier");

        var registry = new FakeRomRegistry(_log);
        foreach ((string name, byte[] data) in
                 RomOverrideLayout.BuildRecords(GpuPowerTiers.ExtraGpuPower, RomOverrideLayout.PinnedVbiosVersion))
        {
            registry.Records[name] = data;
        }

        var ec = new FakeEc(_log);

        GpuReapplyResult result = NewStore(
            registry, new FakeRestarter(_log), new FakeCeiling(_log) { Ceiling = 250_000 }, ec, new FakeRequest(_log))
            .ReapplyAtStartup();

        Assert.Equal(GpuReapplyVerdict.Applied, result.Verdict);
        Assert.Equal(GpuRomOverrideStates.Unlocked, result.State!.State);
        Assert.Equal(new[] { (130, 0) }, ec.Activations);
    }

    [Fact]
    public void StartupReapply_WithTheMarkerGone_StillReapplies()
    {
        // The measured boot loss (2026-11-17): the marker vanished — nothing had restored the
        // unlock — while the records and the originals file survived. The state on disk is the
        // intent, so the live half is re-applied anyway.
        var registry = new FakeRomRegistry(_log);
        var request = new FakeRequest(_log);
        var budget = new FakeBudget(_log);
        var rails = new FakeRails(_log);
        GpuRomOverrideStore store = NewStore(
            registry, new FakeRestarter(_log), new FakeCeiling(_log) { Ceiling = 250_000 },
            new FakeEc(_log), request, budget, rails);

        Assert.Equal(GpuRomOverrideStates.Unlocked, store.SetEnabled(true).State);

        File.Delete(MarkerPath);   // what the reboot did
        request.Forget();
        budget.Reset();
        rails.NvVddCurrent = RomOverrideLayout.StockNvVddMilliAmps;
        rails.BoardCurrent = 17_021;   // what a driver reset leaves: the record maxima

        GpuReapplyResult result = store.ReapplyAtStartup();

        Assert.Equal(GpuReapplyVerdict.Applied, result.Verdict);
        Assert.Equal(GpuRomOverrideStates.Unlocked, result.State!.State);
        Assert.Equal(14_026, result.State.BoardInputCurrentMilliamps);
    }

    [Fact]
    public void StartupReapply_WhenTheEcIsNotReady_IsTransient()
    {
        // The EC behind a boot-time service can be unreadable for a while: another attempt
        // can succeed, so the caller is told to retry instead of giving up.
        SeedOriginalsAndMarker();

        var registry = new FakeRomRegistry(_log);
        foreach ((string name, byte[] data) in
                 RomOverrideLayout.BuildRecords(GpuPowerTiers.ExtraGpuPower, RomOverrideLayout.PinnedVbiosVersion))
        {
            registry.Records[name] = data;
        }

        var ec = new FakeEc(_log) { Supported = false, Reason = "the EC service is not up yet" };
        var restarter = new FakeRestarter(_log);

        GpuReapplyResult result = NewStore(
            registry, restarter, new FakeCeiling(_log), ec, new FakeRequest(_log)).ReapplyAtStartup();

        Assert.Equal(GpuReapplyVerdict.Transient, result.Verdict);
        Assert.Contains("DefaultTGP", result.State!.Reason!, StringComparison.Ordinal);
        Assert.Empty(ec.Activations);
        Assert.Empty(registry.Writes);
        Assert.Equal(0, restarter.Calls);
    }

    [Fact]
    public void StartupReapply_WithoutTheIntent_DoesNothing()
    {
        var ec = new FakeEc(_log);
        var request = new FakeRequest(_log);
        var rails = new FakeRails(_log);
        GpuRomOverrideStore store = NewStore(
            new FakeRomRegistry(_log), new FakeRestarter(_log), new FakeCeiling(_log) { Ceiling = 250_000 }, ec, request, rails: rails);

        GpuReapplyResult result = store.ReapplyAtStartup();

        Assert.Equal(GpuReapplyVerdict.NoIntent, result.Verdict);
        Assert.Null(result.State);
        Assert.Empty(ec.Activations);
        Assert.Empty(request.Requests);
        Assert.Empty(rails.Sets);
        Assert.False(store.GetState().Enabled);
    }

    [Fact]
    public void StartupReapply_AfterARestore_IsNoIntent_SoNothingIsRetried()
    {
        // The unlock is restored while the engine is up: the records and the originals file
        // are gone, so the next attempt has nothing to re-apply (and writes nothing).
        var registry = new FakeRomRegistry(_log);
        var ceiling = new FakeCeiling(_log) { Ceiling = 250_000 };
        GpuRomOverrideStore store = NewStore(
            registry, new FakeRestarter(_log), ceiling, new FakeEc(_log), new FakeRequest(_log));

        Assert.Equal(GpuRomOverrideStates.Unlocked, store.SetEnabled(true).State);
        ceiling.Ceiling = 175_000;
        Assert.Equal(GpuRomOverrideStates.NotUnlocked, store.SetEnabled(false).State);

        Assert.Equal(GpuReapplyVerdict.NoIntent, store.ReapplyAtStartup().Verdict);
        Assert.Empty(registry.Records);
    }

    [Fact]
    public void Enabled_IsDerivedFromTheRecordsAndTheOriginals_NotTheMarker()
    {
        // Enabled is what the status row keys on, so it must not depend on a file a boot can
        // lose: our records, matching the tier, plus our originals file are the intent.
        var registry = new FakeRomRegistry(_log);
        var ceiling = new FakeCeiling(_log) { Ceiling = 250_000 };
        GpuRomOverrideStore store = NewStore(
            registry, new FakeRestarter(_log), ceiling, new FakeEc(_log), new FakeRequest(_log), new FakeBudget(_log));

        Assert.False(store.GetState().Enabled);   // virgin machine: no records, no originals

        Assert.Equal(GpuRomOverrideStates.Unlocked, store.SetEnabled(true).State);
        Assert.True(store.GetState().Enabled);
        Assert.True(File.Exists(OriginalsPath));

        File.Delete(MarkerPath);
        Assert.True(store.GetState().Enabled);   // the marker is informational only

        // The originals file goes: the records on the key are no longer ours to claim.
        File.Delete(OriginalsPath);
        Assert.False(store.GetState().Enabled);
    }

    [Fact]
    public void Enabled_IsFalseForAKeyWeDoNotOwn_AndForAnEmptyKey()
    {
        // Another tool's records (or a foreign shape) are not an unlock of ours, whatever
        // marker a previous install left behind.
        SeedOriginalsAndMarker();

        var registry = new FakeRomRegistry(_log);
        registry.Records["romOverride00"] = RomOverrideRecord.Encode("98.03.5E.00.5E", 0x57C36, 250_000);

        Assert.False(NewStore(
            registry, new FakeRestarter(_log), new FakeCeiling(_log), new FakeEc(_log), new FakeRequest(_log))
            .GetState().Enabled);

        Assert.False(NewStore(
            new FakeRomRegistry(_log), new FakeRestarter(_log), new FakeCeiling(_log), new FakeEc(_log), new FakeRequest(_log))
            .GetState().Enabled);
    }

    [Fact]
    public void Restore_DeletesTheMarkerAndTheIntent_AndEnabledFollows()
    {
        var ceiling = new FakeCeiling(_log) { Ceiling = 250_000 };
        GpuRomOverrideStore store = NewStore(
            new FakeRomRegistry(_log), new FakeRestarter(_log), ceiling, new FakeEc(_log), new FakeRequest(_log));

        Assert.False(store.GetState().Enabled);

        Assert.Equal(GpuRomOverrideStates.Unlocked, store.SetEnabled(true).State);
        Assert.Equal(GpuPowerTiers.ExtraGpuPower.Id, File.ReadAllText(MarkerPath));
        Assert.True(store.GetState().Enabled);

        ceiling.Ceiling = 175_000;
        Assert.Equal(GpuRomOverrideStates.NotUnlocked, store.SetEnabled(false).State);
        Assert.False(File.Exists(MarkerPath));
        Assert.False(store.GetState().Enabled);
    }

    [Fact]
    public void Settle_WhenTheEcRewritesTheBudget_ReappliesOnlyThat()
    {
        // The measured post-boot drift (2026-11-17): the start-up re-apply read the budget
        // back exactly and a few seconds later the EC republished its own 150 W F7. The
        // settle pass writes the budget back and touches nothing else.
        var registry = new FakeRomRegistry(_log);
        var request = new FakeRequest(_log);
        var budget = new FakeBudget(_log);
        var rails = new FakeRails(_log);
        GpuRomOverrideStore store = NewStore(
            registry, new FakeRestarter(_log), new FakeCeiling(_log) { Ceiling = 250_000 },
            new FakeEc(_log), request, budget, rails);

        Assert.Equal(GpuRomOverrideStates.Unlocked, store.SetEnabled(true).State);

        budget.Reset();   // the EC publishes its own F7 after the boot settles
        int writes = registry.Writes.Count;
        int railSets = rails.Sets.Count;
        int requests = request.Requests.Count;
        int budgetSets = budget.Sets.Count;
        _log.Clear();

        GpuSettleResult result = store.SettleAtStartup();

        Assert.Equal(GpuSettleVerdict.Reapplied, result.Verdict);
        Assert.Equal("budget 150000 mW", result.Drift);
        Assert.Null(result.Reason);
        Assert.Equal(budgetSets + 1, budget.Sets.Count);
        Assert.Equal((225_000, 500_000), (budget.Sets[^1].F7Milliwatts, budget.Sets[^1].PlatformMilliwatts));
        Assert.Equal(railSets, rails.Sets.Count);   // only the drifted part is written
        Assert.Equal(requests, request.Requests.Count);
        Assert.Equal(writes, registry.Writes.Count);
        Assert.Contains("budget.set:225000,500000", _log);
        Assert.DoesNotContain(_log, line => line.StartsWith("rails.set", StringComparison.Ordinal));

        Assert.Equal(225_000, store.GetState().BudgetMilliwatts);
        Assert.Equal(GpuRomOverrideStates.Unlocked, store.GetState().State);
    }

    [Fact]
    public void Settle_WhenEverythingStillHolds_WritesNothing()
    {
        var registry = new FakeRomRegistry(_log);
        var request = new FakeRequest(_log);
        var budget = new FakeBudget(_log);
        var rails = new FakeRails(_log);
        GpuRomOverrideStore store = NewStore(
            registry, new FakeRestarter(_log), new FakeCeiling(_log) { Ceiling = 250_000 },
            new FakeEc(_log), request, budget, rails);

        Assert.Equal(GpuRomOverrideStates.Unlocked, store.SetEnabled(true).State);

        int writes = registry.Writes.Count;
        int railSets = rails.Sets.Count;
        int requests = request.Requests.Count;
        int budgetSets = budget.Sets.Count;

        GpuSettleResult result = store.SettleAtStartup();

        Assert.Equal(GpuSettleVerdict.Settled, result.Verdict);
        Assert.Null(result.Drift);
        Assert.Equal(writes, registry.Writes.Count);
        Assert.Equal(railSets, rails.Sets.Count);
        Assert.Equal(requests, request.Requests.Count);
        Assert.Equal(budgetSets, budget.Sets.Count);
    }

    [Fact]
    public void Settle_WhenTheBudgetNeverComesBack_ReportsTheNumbersAndStaysUncertain()
    {
        var budget = new FakeBudget(_log);
        GpuRomOverrideStore store = NewStore(
            new FakeRomRegistry(_log), new FakeRestarter(_log), new FakeCeiling(_log) { Ceiling = 250_000 },
            new FakeEc(_log), new FakeRequest(_log), budget);

        Assert.Equal(GpuRomOverrideStates.Unlocked, store.SetEnabled(true).State);

        budget.Refused = "the EC refused the F7 write";
        budget.Reset();

        GpuSettleResult result = store.SettleAtStartup();

        Assert.Equal(GpuSettleVerdict.Drifted, result.Verdict);
        Assert.Equal("budget 150000 mW", result.Drift);   // the number the warning names
        Assert.Contains("the EC refused the F7 write", result.Reason!, StringComparison.Ordinal);

        // The pass ends short, so the state verb must not read Unlocked over it — and its
        // own live reason names both numbers.
        Assert.Equal(GpuRomOverrideStates.Uncertain, store.GetState().State);
        Assert.Contains("150 W", store.GetState().Reason!, StringComparison.Ordinal);
        Assert.Contains("225 W", store.GetState().Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void Settle_WhenTheDriverReloadTookTheRequestAndTheRails_ReappliesThem()
    {
        var request = new FakeRequest(_log);
        var rails = new FakeRails(_log);
        var budget = new FakeBudget(_log);
        GpuRomOverrideStore store = NewStore(
            new FakeRomRegistry(_log), new FakeRestarter(_log), new FakeCeiling(_log) { Ceiling = 250_000 },
            new FakeEc(_log), request, budget, rails);

        Assert.Equal(GpuRomOverrideStates.Unlocked, store.SetEnabled(true).State);

        request.Forget();   // what a driver reload does
        rails.NvVddCurrent = RomOverrideLayout.StockNvVddMilliAmps;
        int budgetSets = budget.Sets.Count;

        GpuSettleResult result = store.SettleAtStartup();

        Assert.Equal(GpuSettleVerdict.Reapplied, result.Verdict);
        Assert.Contains("rails", result.Drift!, StringComparison.Ordinal);
        Assert.Contains("request", result.Drift!, StringComparison.Ordinal);
        Assert.Equal(225_000, request.Requests[^1]);
        Assert.Equal(250_000, rails.Sets[^1].Nvvdd);
        Assert.Equal(budgetSets, budget.Sets.Count);   // the budget held, so it is not written
    }

    [Fact]
    public void Settle_AfterARestore_StopsInsteadOfReapplying()
    {
        // The owner restores the unlock while the settle phase is live: the records and the
        // originals file are gone, so the pass stops and writes nothing.
        var registry = new FakeRomRegistry(_log);
        var ceiling = new FakeCeiling(_log) { Ceiling = 250_000 };
        var rails = new FakeRails(_log);
        var budget = new FakeBudget(_log);
        var request = new FakeRequest(_log);
        GpuRomOverrideStore store = NewStore(registry, new FakeRestarter(_log), ceiling, new FakeEc(_log), request, budget, rails);

        Assert.Equal(GpuRomOverrideStates.Unlocked, store.SetEnabled(true).State);
        ceiling.Ceiling = 175_000;
        Assert.Equal(GpuRomOverrideStates.NotUnlocked, store.SetEnabled(false).State);

        int writes = registry.Writes.Count;
        int railSets = rails.Sets.Count;
        int budgetSets = budget.Sets.Count;
        int requests = request.Requests.Count;

        GpuSettleResult result = store.SettleAtStartup();

        Assert.Equal(GpuSettleVerdict.Stopped, result.Verdict);
        Assert.Null(result.Drift);
        Assert.Equal(writes, registry.Writes.Count);
        Assert.Equal(railSets, rails.Sets.Count);
        Assert.Equal(budgetSets, budget.Sets.Count);
        Assert.Equal(requests, request.Requests.Count);
        Assert.False(store.GetState().Enabled);
    }

    [Fact]
    public void StartupReapply_UsesTheSavedRailSelection_NotTheTier()
    {
        // Field 0.9.12: every boot put the tier's 250000/70000/14026 mA over the owner's
        // 210000/84000/17000. What the sliders saved is what a boot now applies.
        var registry = new FakeRomRegistry(_log);
        var request = new FakeRequest(_log);
        var budget = new FakeBudget(_log);
        var rails = new FakeRails(_log);
        GpuRomOverrideStore store = NewStore(
            registry, new FakeRestarter(_log), new FakeCeiling(_log) { Ceiling = 250_000 },
            new FakeEc(_log), request, budget, rails);

        Assert.Equal(GpuRomOverrideStates.Unlocked, store.SetEnabled(true).State);
        File.WriteAllText(RailsPath, "nvvdd=210000\nmsvdd=84000\nboard=17000\n");

        request.Forget();
        budget.Reset();
        rails.NvVddCurrent = RomOverrideLayout.StockNvVddMilliAmps;
        rails.MsVddCurrent = RomOverrideLayout.StockMsVddMilliAmps;
        rails.BoardCurrent = RomOverrideLayout.StockBoardInputMilliAmps;

        GpuReapplyResult result = store.ReapplyAtStartup();

        Assert.Equal(GpuReapplyVerdict.Applied, result.Verdict);
        Assert.Equal(GpuRomOverrideStates.Unlocked, result.State!.State);
        Assert.Equal((210_000, 84_000, 17_000), rails.Sets[^1]);
        Assert.Equal(210_000, result.State.NvvddCurrentMilliamps);
        Assert.Equal(84_000, result.State.MsvddCurrentMilliamps);
        Assert.Equal(17_000, result.State.BoardInputCurrentMilliamps);
    }

    [Fact]
    public void StartupReapply_WithoutASavedSelection_UsesTheTier_AndSaysSo()
    {
        var registry = new FakeRomRegistry(_log);
        var request = new FakeRequest(_log);
        var budget = new FakeBudget(_log);
        var rails = new FakeRails(_log);
        GpuRomOverrideStore store = NewStore(
            registry, new FakeRestarter(_log), new FakeCeiling(_log) { Ceiling = 250_000 },
            new FakeEc(_log), request, budget, rails);

        Assert.Equal(GpuRomOverrideStates.Unlocked, store.SetEnabled(true).State);
        File.Delete(RailsPath);   // a machine whose owner never saved a selection

        request.Forget();
        budget.Reset();
        rails.NvVddCurrent = RomOverrideLayout.StockNvVddMilliAmps;

        using var capture = new LogCapture();
        GpuReapplyResult result = store.ReapplyAtStartup();

        Assert.Equal(GpuReapplyVerdict.Applied, result.Verdict);
        Assert.Equal((250_000, 70_000, 14_026), rails.Sets[^1]);
        Assert.Contains(capture.Lines, line =>
            line.Contains("rails from the tier defaults (no saved selection)", StringComparison.Ordinal));
    }

    [Fact]
    public void StartupReapply_ClampsASavedValueThePolicyNoLongerAllows_WithAWarning()
    {
        // A driver update can narrow the policy under a saved selection: the nearest allowed
        // value is written, the warning names both, and the verdict compares against what was
        // actually requested — not against the saved number, which would read short forever.
        var rails = new FakeRails(_log);
        var request = new FakeRequest(_log);
        var budget = new FakeBudget(_log);
        GpuRomOverrideStore store = NewStore(
            new FakeRomRegistry(_log), new FakeRestarter(_log), new FakeCeiling(_log) { Ceiling = 250_000 },
            new FakeEc(_log), request, budget, rails);

        Assert.Equal(GpuRomOverrideStates.Unlocked, store.SetEnabled(true).State);
        File.WriteAllText(RailsPath, "nvvdd=294000\nmsvdd=84000\nboard=17000\n");

        rails.NvVddPolicy = (210_000, 260_000);
        rails.NvVddCurrent = 210_000;
        request.Forget();
        budget.Reset();

        using var capture = new LogCapture();
        GpuReapplyResult result = store.ReapplyAtStartup();

        Assert.Equal(GpuReapplyVerdict.Applied, result.Verdict);
        Assert.Equal(GpuRomOverrideStates.Unlocked, result.State!.State);
        Assert.Equal(260_000, rails.Sets[^1].Nvvdd);
        Assert.Equal(260_000, result.State.NvvddCurrentMilliamps);
        Assert.Contains(capture.Lines, line =>
            line.StartsWith("Warning: ", StringComparison.Ordinal) &&
            line.Contains(
                "the saved NVVDD rail 294000 mA is outside the live rail policy 210000-260000 mA; using 260000 mA",
                StringComparison.Ordinal));
    }

    [Fact]
    public void Settle_ComparesAgainstTheSavedSelection_SoATunedSelectionStaysUnlocked()
    {
        // The settle pass used to compare the live rails against the tier, so an owner-tuned
        // selection drifted on every pass. It compares against the saved values now, and the
        // state verb (which makes the same comparison) reads Unlocked over them.
        var rails = new FakeRails(_log);
        var budget = new FakeBudget(_log);
        GpuRomOverrideStore store = NewStore(
            new FakeRomRegistry(_log), new FakeRestarter(_log), new FakeCeiling(_log) { Ceiling = 250_000 },
            new FakeEc(_log), new FakeRequest(_log), budget, rails);

        Assert.Equal(GpuRomOverrideStates.Unlocked, store.SetEnabled(true).State);
        Assert.Equal(210_000, store.SetRailLimits(210_000, 84_000, 17_000).NvvddMilliamps);

        int railSets = rails.Sets.Count;
        GpuSettleResult result = store.SettleAtStartup();

        Assert.Equal(GpuSettleVerdict.Settled, result.Verdict);
        Assert.Null(result.Drift);
        Assert.Equal(railSets, rails.Sets.Count);   // the saved values hold, so nothing is written

        GpuRomOverrideStateDto state = store.GetState();
        Assert.Equal(GpuRomOverrideStates.Unlocked, state.State);
        Assert.Null(state.Reason);
    }

    [Fact]
    public void Restore_DeletesTheSavedRailSelection()
    {
        var ceiling = new FakeCeiling(_log) { Ceiling = 250_000 };
        GpuRomOverrideStore store = NewStore(
            new FakeRomRegistry(_log), new FakeRestarter(_log), ceiling, new FakeEc(_log), new FakeRequest(_log));

        Assert.Equal(GpuRomOverrideStates.Unlocked, store.SetEnabled(true).State);
        store.SetRailLimits(210_000, 84_000, 17_000);
        Assert.True(File.Exists(RailsPath));

        ceiling.Ceiling = 175_000;
        Assert.Equal(GpuRomOverrideStates.NotUnlocked, store.SetEnabled(false).State);

        Assert.False(File.Exists(RailsPath));
        Assert.False(File.Exists(MarkerPath));
        Assert.False(File.Exists(OriginalsPath));
    }

    [Fact]
    public void ReUnlock_DoesNotOverwriteASavedRailSelection()
    {
        // The first completed unlock establishes the defaults; a later one must not discard
        // what the owner tuned in between (and it applies the saved values, not the tier's).
        var rails = new FakeRails(_log);
        GpuRomOverrideStore store = NewStore(
            new FakeRomRegistry(_log), new FakeRestarter(_log), new FakeCeiling(_log) { Ceiling = 250_000 },
            new FakeEc(_log), new FakeRequest(_log), rails: rails);

        Assert.Equal(GpuRomOverrideStates.Unlocked, store.SetEnabled(true).State);
        store.SetRailLimits(210_000, 84_000, 17_000);

        Assert.Equal(GpuRomOverrideStates.Unlocked, store.SetEnabled(true).State);

        Assert.Equal(
            new[] { "nvvdd=210000", "msvdd=84000", "board=17000" },
            File.ReadAllLines(RailsPath).Where(line => line.Length > 0));
        Assert.Equal((210_000, 84_000, 17_000), rails.Sets[^1]);
    }

    private string MarkerPath => Path.Combine(Path.GetDirectoryName(OriginalsPath)!, "enabled");

    private string RailsPath => Path.Combine(Path.GetDirectoryName(OriginalsPath)!, "rails.txt");

    /// <summary>The state a completed unlock leaves on disk, without touching the driver key.</summary>
    private void SeedOriginalsAndMarker(string? tier = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(OriginalsPath)!);

        var lines = new List<string> { RomOverrideLayout.PinnedVbiosVersion };
        lines.AddRange(RomOverrideLayout.Fields.Select(f => $"{f.Offset}={f.StockValue}"));
        File.WriteAllLines(OriginalsPath, lines);
        File.WriteAllText(MarkerPath, tier ?? GpuPowerTiers.ExtraGpuPower.Id);
    }

    private void AssertRefusal(Action<FakeRomRegistry> seed, string expected, bool withOriginals = false)
    {
        var registry = new FakeRomRegistry(_log);
        seed(registry);

        if (withOriginals)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(OriginalsPath)!);
            var lines = new List<string> { RomOverrideLayout.PinnedVbiosVersion };
            lines.AddRange(RomOverrideLayout.Fields.Select(f => $"{f.Offset}={f.StockValue}"));
            File.WriteAllLines(OriginalsPath, lines);
        }

        var restarter = new FakeRestarter(_log);
        var ec = new FakeEc(_log);

        GpuRomOverrideStateDto state = NewStore(registry, restarter, new FakeCeiling(_log), ec, new FakeRequest(_log)).SetEnabled(true);

        Assert.Equal(GpuRomOverrideStates.Unsupported, state.State);
        Assert.Contains(expected, state.Reason!, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(registry.Writes);
        Assert.Empty(registry.Deletes);
        Assert.Empty(ec.Activations);
        Assert.Equal(0, restarter.Calls);
    }

    [Fact]
    public void GetState_CarriesTheVbiosPolicyAndThePinnedOffsetVerdict()
    {
        // The diagnostics need the answer, not just the version: whether the seven record
        // offsets the unlock patches still land on this image's policy fields.
        var probe = new FakeVbiosProbe
        {
            State = new VbiosPolicyState(
                null, RomOverrideLayout.PinnedVbiosVersion, 0x57B1D, "40 38 66 14", 0x2C18,
                new VbiosPolicyField(0x57C2E, 5000, 95000, 220000),
                new VbiosPolicyField(0x58090, 1, 120000, 120000),
                new VbiosPolicyField(0x580F6, 1, 50000, 50000),
                new VbiosPolicyField(0x57EF8, 1, 13500, 13500),
                new[]
                {
                    new VbiosPinnedCheck("romOverride00", 0x57C36, 220000, 0x57C36, "max", 220000),
                    new VbiosPinnedCheck("romOverride01", 0x57EFC, 13500, 0x57EFC, "default", 13500),
                }),
        };

        GpuRomOverrideStateDto state = NewStore(
            new FakeRomRegistry(_log), new FakeRestarter(_log), new FakeCeiling(_log), new FakeEc(_log),
            new FakeRequest(_log), vbios: probe).GetState();

        Assert.NotNull(state.VbiosPolicy);
        Assert.Equal(RomOverrideLayout.PinnedVbiosVersion, state.VbiosPolicy!.VbiosVersion);
        Assert.True(state.VbiosPolicy.Pinned);
        Assert.Equal(0x57B1D, state.VbiosPolicy.TableOffset);
        Assert.Equal("40 38 66 14", state.VbiosPolicy.TableHeader);
        Assert.Equal(0x2C18, state.VbiosPolicy.PciDevice);
        Assert.Equal(220000u, state.VbiosPolicy.Power!.Max);
        Assert.Equal(0x57EF8, state.VbiosPolicy.BoardInput!.Offset);
        Assert.All(state.VbiosPolicy.PinnedOffsets, check => Assert.True(check.Match));

        // What an apply writes on this image: the tier's numbers, unchanged (250 W / 250 A /
        // 84 A / 17.021 A), so a report shows them next to the stock caps they exceed.
        Assert.Equal(
            new[] { "power", "NVVDD", "MSVDD", "board input" },
            state.VbiosPolicy.Records.Select(record => record.Rail));
        Assert.Equal(
            new uint[] { 250_000, 250_000, 84_000, 17_021 },
            state.VbiosPolicy.Records.Select(record => record.Value));
    }

    [Fact]
    public void GetState_CarriesAProbeRefusalAsData()
    {
        var probe = new FakeVbiosProbe
        {
            State = VbiosPolicyState.Failed("the VBIOS dump call failed (status -1)"),
        };

        GpuRomOverrideStateDto state = NewStore(
            new FakeRomRegistry(_log), new FakeRestarter(_log), new FakeCeiling(_log), new FakeEc(_log),
            new FakeRequest(_log), vbios: probe).GetState();

        Assert.Equal("the VBIOS dump call failed (status -1)", state.VbiosPolicy!.Reason);
        Assert.Null(state.VbiosPolicy.TableOffset);
        Assert.False(state.VbiosPolicy.Pinned);
        Assert.Empty(state.VbiosPolicy.PinnedOffsets);
        Assert.Empty(state.VbiosPolicy.Records);
    }

    private GpuRomOverrideStore NewStore(
        FakeRomRegistry registry,
        FakeRestarter restarter,
        FakeCeiling ceiling,
        FakeEc ec,
        FakeRequest request,
        FakeBudget? budget = null,
        FakeRails? rails = null,
        IVbiosPolicyProbe? vbios = null)
    {
        budget ??= new FakeBudget(_log);
        rails ??= new FakeRails(_log);
        restarter.OnRestart = () =>
        {
            request.Forget();
            budget.Reset();
        };

        return new(registry, restarter, ceiling, ec, request, budget, rails, OriginalsPath,
            vbios: vbios ?? new FakeVbiosProbe());
    }

    /// <summary>
    /// A tester's ROM as the probe would read it: the same table layout, stock caps above
    /// the known-good image's (250 W / 250 A / 100 A / 11.053-17 A). <paramref name="matches"/>
    /// decides whether the seven record offsets land on the field each record patches.
    /// </summary>
    private static VbiosPolicyState TesterImage(string version, bool matches)
    {
        VbiosPinnedCheck Check(string name, uint offset, uint value, string component) => new(
            name, offset, matches ? value : 0, (int)(matches ? offset : offset - 0x1000), component, value);

        return new VbiosPolicyState(
            null, version, 0x57B1D, "40 38 66 14", 0x2C18,
            new VbiosPolicyField(0x57C2E, 5000, 225000, 250000),
            new VbiosPolicyField(0x58090, 1, 250000, 250000),
            new VbiosPolicyField(0x580F6, 1, 100000, 100000),
            new VbiosPolicyField(0x57EF8, 1, 11053, 17000),
            new[]
            {
                Check("romOverride00", 0x57C36, 250000, "max"),
                Check("romOverride01", 0x57EFC, 11053, "default"),
                Check("romOverride02", 0x57F00, 17000, "max"),
                Check("romOverride03", 0x58094, 250000, "default"),
                Check("romOverride04", 0x58098, 250000, "max"),
                Check("romOverride05", 0x580FA, 100000, "default"),
                Check("romOverride06", 0x580FE, 100000, "max"),
            });
    }

    private static void AssertRecord(byte[]? data, string version, uint offset, uint value)
    {
        Assert.True(RomOverrideRecord.TryDecode(data, out string actualVersion, out uint actualOffset, out uint actualValue));
        Assert.Equal(version, actualVersion);
        Assert.Equal(offset, actualOffset);
        Assert.Equal(value, actualValue);
    }

    private static string Hex(byte[] data) => BitConverter.ToString(data).ToUpperInvariant();

    private sealed class FakeRomRegistry : IGpuRomRegistry
    {
        private readonly List<string> _log;

        public FakeRomRegistry(List<string> log) => _log = log;

        public string? InstanceId { get; set; } = GpuRomOverrideTests.InstanceId;

        public string? Version { get; set; } = RomOverrideLayout.PinnedVbiosVersion;

        public Dictionary<string, byte[]> Records { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Writes { get; } = new();

        public List<string> Deletes { get; } = new();

        public bool Flushed { get; private set; }

        public bool DropWrites { get; set; }

        public bool FailFirstWrite { get; set; }

        public string? ReadVbiosVersion() => Version;

        // A fresh snapshot per read, exactly as the real display-driver registry does: the
        // store deletes while walking the dictionary it read.
        public IReadOnlyDictionary<string, byte[]> ReadRecords() =>
            new Dictionary<string, byte[]>(Records, StringComparer.OrdinalIgnoreCase);

        public void WriteRecord(string name, byte[] data)
        {
            if (FailFirstWrite && Writes.Count == 0)
                throw new InvalidOperationException("the driver key refused the write");

            Writes.Add(name);
            _log.Add($"write:{name}");
            if (!DropWrites)
                Records[name] = data;
        }

        public void DeleteRecord(string name)
        {
            Deletes.Add(name);
            _log.Add($"delete:{name}");
            Records.Remove(name);
        }

        public void Flush()
        {
            Flushed = true;
            _log.Add("flush");
        }
    }

    private sealed class FakeRestarter : IGpuRestarter
    {
        private readonly List<string> _log;

        public FakeRestarter(List<string> log) => _log = log;

        public int Calls { get; private set; }

        public string? LastInstanceId { get; private set; }

        public Exception? Failure { get; set; }

        /// <summary>What the restart does to the rest of the machine (clears the power request).</summary>
        public Action? OnRestart { get; set; }

        public bool Restart(string? instanceId)
        {
            Calls++;
            LastInstanceId = instanceId;
            if (Failure != null)
                throw Failure;

            OnRestart?.Invoke();
            _log.Add("restart");
            return true;
        }
    }

    private sealed class FakeCeiling : IGpuPowerCeilingProbe
    {
        private readonly List<string> _log;

        public FakeCeiling(List<string> log) => _log = log;

        public int? Ceiling { get; set; }

        public int? Current { get; set; }

        public GpuPowerLimits Read()
        {
            _log.Add("ceiling");
            return new GpuPowerLimits(Ceiling, Current);
        }
    }

    /// <summary>The VBIOS probe without a driver: answers a scripted state.</summary>
    private sealed class FakeVbiosProbe : IVbiosPolicyProbe
    {
        public VbiosPolicyState State { get; set; } = VbiosPolicyState.Failed("not scripted");

        public VbiosPolicyState Read() => State;
    }

    /// <summary>The EC's GPU power-control slice: records what the store asked it to do.</summary>
    private sealed class FakeEc : IEcGpuPowerControl
    {
        private readonly List<string> _log;

        public FakeEc(List<string> log) => _log = log;

        public bool Supported { get; set; } = true;

        public int DefaultTgpWatts { get; set; } = 95;

        public string Reason { get; set; } = string.Empty;

        public List<(int Offset, int Db)> Activations { get; } = new();

        public int Deactivations { get; private set; }

        public GpuPowerInfoDto GetGpuPowerInfo() => new()
        {
            Supported = Supported,
            DefaultTgpWatts = DefaultTgpWatts,
            Reason = Reason,
        };

        public void ActivateGpuPowerControl(int ctgpOffset, int dbOffset)
        {
            Activations.Add((ctgpOffset, dbOffset));
            _log.Add($"ec.activate:{ctgpOffset},{dbOffset}");
        }

        public void DeactivateGpuPowerControl()
        {
            Deactivations++;
            _log.Add("ec.deactivate");
        }
    }

    /// <summary>The driver's power request. A successful Request is remembered, as the driver does.</summary>
    private sealed class FakeRequest : IGpuPowerRequest
    {
        private readonly List<string> _log;

        public FakeRequest(List<string> log) => _log = log;

        /// <summary>When set, Request answers with it instead of accepting the value.</summary>
        public GpuPowerRequestState? Failure { get; set; }

        /// <summary>A fixed answer that wins over what the fake accepted (another tool's request).</summary>
        public GpuPowerRequestState? Answer { get; set; }

        /// <summary>A GPU restart clears the request on the measured machine.</summary>
        public bool SurvivesRestart { get; set; }

        public List<int> Requests { get; } = new();

        private uint? _accepted;

        public GpuPowerRequestState Read() => Answer ?? From(_accepted);

        public GpuPowerRequestState Request(int milliwatts)
        {
            Requests.Add(milliwatts);
            _log.Add($"request:{milliwatts}");

            if (Failure != null)
                return Failure;

            _accepted = (uint)milliwatts;
            return Answer ?? From(_accepted);
        }

        /// <summary>What a GPU restart does to the request on the measured machine.</summary>
        public void Forget()
        {
            if (!SurvivesRestart)
                _accepted = null;
        }

        private static GpuPowerRequestState From(uint? milliwatts) =>
            new(milliwatts is uint value ? (int)value : (int?)null, 5_000, 250_000, null);
    }

    /// <summary>
    /// The laptop's power budget. The measured machine published f7 150 W and a 425 W
    /// platform budget; a raise takes the headroom from the platform half (225 W -> 500 W)
    /// and a GPU restart republishes the EC's own values.
    /// </summary>
    private sealed class FakeBudget : INvApiPowerBudget
    {
        private readonly List<string> _log;

        public FakeBudget(List<string> log)
        {
            _log = log;
            Current = Original;
        }

        /// <summary>The EC's published budget: what the driver carries after a restart.</summary>
        public GpuPowerBudget Original { get; set; } = new(150_000, 425_000);

        public GpuPowerBudget Current { get; set; }

        /// <summary>When set, every answer is this refusal.</summary>
        public string? Failure { get; set; }

        /// <summary>When set, Set answers the refusal instead of accepting the target.</summary>
        public string? Refused { get; set; }

        public List<GpuPowerBudget> Sets { get; } = new();

        /// <summary>The budget a restore left behind (the restart republishes after it).</summary>
        public GpuPowerBudget? LastRestored { get; private set; }

        /// <summary>What the GPU restart does to the budget on the measured machine.</summary>
        public void Reset() => Current = Original;

        public GpuPowerBudgetState Read()
        {
            _log.Add("budget.read");
            return Failure != null
                ? new GpuPowerBudgetState(null, null, Failure)
                : new GpuPowerBudgetState(Current.F7Milliwatts, Current.PlatformMilliwatts, null);
        }

        public GpuPowerBudgetState Set(GpuPowerBudget target)
        {
            Sets.Add(target);
            _log.Add($"budget.set:{target.F7Milliwatts},{target.PlatformMilliwatts}");

            if (Failure != null)
                return new GpuPowerBudgetState(null, null, Failure);

            if (Refused != null)
                return new GpuPowerBudgetState(Current.F7Milliwatts, Current.PlatformMilliwatts, Refused);

            Current = target;
            return new GpuPowerBudgetState(target.F7Milliwatts, target.PlatformMilliwatts, null);
        }

        public GpuPowerBudgetState Restore(GpuPowerBudget baseline, GpuPowerBudget applied)
        {
            _log.Add($"budget.restore:{baseline.F7Milliwatts},{baseline.PlatformMilliwatts}");

            if (Failure != null)
                return new GpuPowerBudgetState(null, null, Failure);

            GpuPowerBudget desired = Current;
            if (Current.F7Milliwatts == applied.F7Milliwatts)
                desired = desired with { F7Milliwatts = baseline.F7Milliwatts };
            if (Current.PlatformMilliwatts == applied.PlatformMilliwatts)
                desired = desired with { PlatformMilliwatts = baseline.PlatformMilliwatts };

            Current = desired;
            LastRestored = desired;
            return new GpuPowerBudgetState(Current.F7Milliwatts, Current.PlatformMilliwatts, null);
        }
    }

    /// <summary>
    /// Serilog's static sink, for the decisions that are only visible in the log: which source
    /// the rails came from, and the clamp warning.
    /// </summary>
    private sealed class LogCapture : ILogEventSink, IDisposable
    {
        private readonly ILogger _previous = Log.Logger;

        public LogCapture() =>
            Log.Logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(this).CreateLogger();

        public List<string> Lines { get; } = new();

        public void Emit(LogEvent logEvent)
        {
            // RenderMessage quotes string properties; the sink output does not.
            lock (Lines)
                Lines.Add($"{logEvent.Level}: {logEvent.RenderMessage().Replace("\"", string.Empty)}");
        }

        public void Dispose() => Log.Logger = _previous;
    }

    /// <summary>
    /// The runtime rail currents. The policy is what the staged records widen (the stock
    /// maxima 210000/60000/11053 mA, the tier's 294000/84000/17021 — the reference's board
    /// input policy ceiling), the currents are what the driver carries (the tier applies
    /// 14.026 A on the board input, inside that ceiling) — and the measured point of this
    /// lever is that they are two different things: the same records held the board at
    /// 175-179 W until the rails moved.
    /// </summary>
    private sealed class FakeRails : IGpuRailControl
    {
        private readonly List<string> _log;

        public FakeRails(List<string> log) => _log = log;

        public (int Min, int Max) NvVddPolicy { get; set; } = (210_000, 294_000);

        public (int Min, int Max) MsVddPolicy { get; set; } = (60_000, 84_000);

        public (int Min, int Max)? BoardPolicy { get; set; } = (11_053, 17_021);

        public int? NvVddCurrent { get; set; } = 210_000;

        public int? MsVddCurrent { get; set; } = 60_000;

        public int? BoardCurrent { get; set; } = 11_053;

        /// <summary>When set, every answer is this refusal.</summary>
        public string? Failure { get; set; }

        /// <summary>When set, Set answers this refusal instead of writing.</summary>
        public string? Refused { get; set; }

        /// <summary>When true the set is accepted but the driver keeps what it carried.</summary>
        public bool DropSets { get; set; }

        public List<(int? Nvvdd, int? Msvdd, int? Board)> Sets { get; } = new();

        public GpuRailLimits Read()
        {
            _log.Add("rails.read");
            return Failure != null ? GpuRailLimits.Refused(Failure) : Answer();
        }

        public GpuRailLimits Set(int? nvvddMilliamps, int? msvddMilliamps, int? boardInputMilliamps)
        {
            Sets.Add((nvvddMilliamps, msvddMilliamps, boardInputMilliamps));
            _log.Add(
                $"rails.set:{Show(nvvddMilliamps)},{Show(msvddMilliamps)},{Show(boardInputMilliamps)}");

            if (Failure != null)
                return GpuRailLimits.Refused(Failure);

            if (Refused != null)
                return GpuRailLimits.Refused(Refused);

            if (!DropSets)
            {
                NvVddCurrent = nvvddMilliamps ?? NvVddCurrent;
                MsVddCurrent = msvddMilliamps ?? MsVddCurrent;
                BoardCurrent = boardInputMilliamps ?? BoardCurrent;
            }

            return Answer();
        }

        private GpuRailLimits Answer() => new(
            new GpuRailValue(NvVddCurrent, NvVddPolicy.Min, NvVddPolicy.Max),
            new GpuRailValue(MsVddCurrent, MsVddPolicy.Min, MsVddPolicy.Max),
            BoardPolicy is { } board ? new GpuRailValue(BoardCurrent, board.Min, board.Max) : GpuRailValue.Unknown,
            null);

        private static string Show(int? milliamps) => milliamps?.ToString() ?? "-";
    }
}
