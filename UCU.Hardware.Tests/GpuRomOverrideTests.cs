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
        // MechControl's unlock (250 W / 17.021 A / 294 A / 84 A), pinned byte for byte.
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

        // ceiling 250 W, board input 14.026 A, NVVDD 294 A, MSVDD 84 A — computed from
        // the tier, so a different tier cannot silently ship another set. The ceiling is
        // deliberately not the 225 W target: a request equal to the ceiling is answered
        // with the stock 175 W limit.
        Assert.Equal("98-03-5E-00-5D-36-7C-05-00-04-90-D0-03-00", Hex(records[0].Data));
        Assert.Equal("98-03-5E-00-5D-FC-7E-05-00-04-CA-36-00-00", Hex(records[1].Data));
        Assert.Equal("98-03-5E-00-5D-00-7F-05-00-04-CA-36-00-00", Hex(records[2].Data));
        Assert.Equal("98-03-5E-00-5D-94-80-05-00-04-70-7C-04-00", Hex(records[3].Data));
        Assert.Equal("98-03-5E-00-5D-98-80-05-00-04-70-7C-04-00", Hex(records[4].Data));
        Assert.Equal("98-03-5E-00-5D-FA-80-05-00-04-20-48-01-00", Hex(records[5].Data));
        Assert.Equal("98-03-5E-00-5D-FE-80-05-00-04-20-48-01-00", Hex(records[6].Data));

        Assert.Equal(
            new[] { "romOverride00", "romOverride01", "romOverride02", "romOverride03", "romOverride04", "romOverride05", "romOverride06" },
            records.Select(r => r.Name));

        // The tier's own numbers, as measured against MechControl-RE on 2026-11-15.
        Assert.Equal(250_000, GpuPowerTiers.ExtraGpuPower.PowerMilliwatts);
        Assert.Equal(225_000, GpuPowerTiers.ExtraGpuPower.TgpTargetMilliwatts);
        Assert.Equal(225, GpuPowerTiers.ExtraGpuPower.TgpTargetWatts);
        Assert.Equal(294_000, GpuPowerTiers.ExtraGpuPower.NvVddMilliAmps);
        Assert.Equal(84_000, GpuPowerTiers.ExtraGpuPower.MsVddMilliAmps);
        Assert.Equal(14_026, GpuPowerTiers.ExtraGpuPower.BoardInputMilliAmps);
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

        GpuRomOverrideStateDto state = NewStore(registry, restarter, ceiling, ec, request).SetEnabled(true);

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

        // Ordering: EC first, then the records, then the restart, then the request.
        int ecIndex = _log.IndexOf("ec.activate:130,0");
        int writeIndex = _log.IndexOf("write:romOverride00");
        int flushIndex = _log.IndexOf("flush");
        int restartIndex = _log.IndexOf("restart");
        int requestIndex = _log.IndexOf("request:225000");
        Assert.True(ecIndex >= 0 && ecIndex < writeIndex, string.Join(",", _log));
        Assert.True(writeIndex < flushIndex, string.Join(",", _log));
        Assert.True(flushIndex < restartIndex, string.Join(",", _log));
        Assert.True(restartIndex < requestIndex, string.Join(",", _log));

        // version line, then offset=value lines (decimal, as the reference writes them).
        string[] lines = File.ReadAllLines(OriginalsPath);
        Assert.Equal("98.03.5E.00.5D", lines[0]);
        Assert.Equal("359478=175000", lines[1]);   // 0x57C36
        Assert.Equal("360192=11053", lines[3]);    // 0x57F00
        Assert.Equal("360600=210000", lines[5]);   // 0x58098
        Assert.Equal("360702=60000", lines[7]);    // 0x580FE
        Assert.Equal(8, lines.Length);
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
    public void Apply_WhenNvmlRefuses_ReportsBothLimitsAsUnread_AndStaysUncertain()
    {
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

    private GpuRomOverrideStore NewStore(
        FakeRomRegistry registry,
        FakeRestarter restarter,
        FakeCeiling ceiling,
        FakeEc ec,
        FakeRequest request)
    {
        restarter.OnRestart = request.Forget;
        return new(registry, restarter, ceiling, ec, request, OriginalsPath);
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
}
