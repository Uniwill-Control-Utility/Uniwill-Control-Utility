using UCU.Contracts;
using UCU.Hardware.Gpu;

namespace UCU.Hardware.Tests;

/// <summary>
/// The GPU power-ceiling override store, exercised against a fake driver key. The
/// record bytes are pinned to what the field machine's key actually carried (VBIOS
/// 98.03.5E.00.5D), because a wrong offset or a wrong patch length is accepted by the
/// driver silently — it patches another field of the ROM.
/// </summary>
public sealed class GpuRomOverrideTests : IDisposable
{
    private const string InstanceId = @"PCI\VEN_10DE&DEV_2C18&SUBSYS_60521D05&REV_A1\E5151BD5C52DB04800";

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "ucu-rom-override-tests", Guid.NewGuid().ToString("N"));

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

        // power 220 W, board input 13.5 A, NVVDD 120 A, MSVDD 50 A — computed from the
        // tier, so a different tier cannot silently ship 250 W.
        Assert.Equal("98-03-5E-00-5D-36-7C-05-00-04-60-5B-03-00", Hex(records[0].Data));
        Assert.Equal("98-03-5E-00-5D-FC-7E-05-00-04-BC-34-00-00", Hex(records[1].Data));
        Assert.Equal("98-03-5E-00-5D-00-7F-05-00-04-BC-34-00-00", Hex(records[2].Data));
        Assert.Equal("98-03-5E-00-5D-94-80-05-00-04-C0-D4-01-00", Hex(records[3].Data));
        Assert.Equal("98-03-5E-00-5D-98-80-05-00-04-C0-D4-01-00", Hex(records[4].Data));
        Assert.Equal("98-03-5E-00-5D-FA-80-05-00-04-50-C3-00-00", Hex(records[5].Data));
        Assert.Equal("98-03-5E-00-5D-FE-80-05-00-04-50-C3-00-00", Hex(records[6].Data));

        Assert.Equal(
            new[] { "romOverride00", "romOverride01", "romOverride02", "romOverride03", "romOverride04", "romOverride05", "romOverride06" },
            records.Select(r => r.Name));
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
        var registry = new FakeRomRegistry();
        var restarter = new FakeRestarter();
        var ceiling = new FakeCeiling { Limit = 220_000 };

        GpuRomOverrideStateDto state = NewStore(registry, restarter, ceiling).SetEnabled(true);

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
    public void Apply_WritesOriginalsBeforeTheFirstRegistryWrite()
    {
        // The recovery file is the only way back from a torn key, so it must exist
        // before a single record lands: the fake refuses the first write and the file
        // still has to be there.
        var registry = new FakeRomRegistry { FailFirstWrite = true };
        GpuRomOverrideStore store = NewStore(registry, new FakeRestarter(), new FakeCeiling { Limit = 220_000 });

        GpuRomOverrideStateDto state = store.SetEnabled(true);

        Assert.Equal(GpuRomOverrideStates.Uncertain, state.State);
        Assert.True(File.Exists(OriginalsPath));
        Assert.Contains("refused the write", state.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_OnAnUnpinnedVbios_RefusesWithoutWriting()
    {
        var registry = new FakeRomRegistry { Version = "98.03.5E.00.5E" };
        var restarter = new FakeRestarter();

        GpuRomOverrideStateDto state = NewStore(registry, restarter, new FakeCeiling()).SetEnabled(true);

        Assert.Equal(GpuRomOverrideStates.Unsupported, state.State);
        Assert.Contains("98.03.5E.00.5E", state.Reason!, StringComparison.Ordinal);
        Assert.Empty(registry.Records);
        Assert.Equal(0, restarter.Calls);
        Assert.False(File.Exists(OriginalsPath));
    }

    [Fact]
    public void Apply_RefusesAKeyAnotherToolOwns_AndLeavesItUntouched()
    {
        // MechControl's own records are on the field machine today: without our
        // originals file there is no proof they are ours, so they are never deleted
        // or overwritten.
        var registry = new FakeRomRegistry();
        registry.Records["romOverride00"] = RomOverrideRecord.Encode("98.03.5E.00.5D", 0x57C36, 250000);
        var restarter = new FakeRestarter();

        GpuRomOverrideStateDto state = NewStore(registry, restarter, new FakeCeiling()).SetEnabled(true);

        Assert.Equal(GpuRomOverrideStates.Unsupported, state.State);
        Assert.Contains("owning tool", state.Reason!, StringComparison.Ordinal);
        Assert.True(RomOverrideRecord.TryDecode(registry.Records["romOverride00"], out _, out _, out uint value));
        Assert.Equal(250000u, value);
        Assert.Single(registry.Records);
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
        var registry = new FakeRomRegistry();
        var restarter = new FakeRestarter();
        var ceiling = new FakeCeiling { Limit = 175_000 };
        GpuRomOverrideStore store = NewStore(registry, restarter, ceiling);

        GpuRomOverrideStateDto clean = store.GetState();
        Assert.Equal(GpuRomOverrideStates.NotUnlocked, clean.State);
        Assert.False(clean.EnforcedLimitMoved);
        Assert.Equal(175000, clean.EnforcedLimitMilliwatts!.Value);
        Assert.Equal(0, restarter.Calls);
        Assert.Equal("extra_gpu_power", clean.TierId);

        ceiling.Limit = 220_000;
        Assert.Equal(GpuRomOverrideStates.Unlocked, store.SetEnabled(true).State);
        Assert.Equal(GpuRomOverrideStates.Unlocked, store.GetState().State);

        registry.Version = "98.03.5E.00.5F";
        Assert.Equal(GpuRomOverrideStates.Unsupported, store.GetState().State);
    }

    [Fact]
    public void Apply_WhenTheDriverKeepsTheStockCeiling_IsUncertain()
    {
        // The records can be staged and read back perfectly while the driver never
        // consumes them: the enforced ceiling is the witness that matters.
        var ceiling = new FakeCeiling { Limit = 175_000 };

        GpuRomOverrideStateDto state = NewStore(new FakeRomRegistry(), new FakeRestarter(), ceiling).SetEnabled(true);

        Assert.Equal(GpuRomOverrideStates.Uncertain, state.State);
        Assert.Contains("175000", state.Reason!, StringComparison.Ordinal);
        Assert.True(state.Restarted);
    }

    [Fact]
    public void Apply_WhenTheRecordsDoNotReadBack_IsUncertain()
    {
        var registry = new FakeRomRegistry { DropWrites = true };

        GpuRomOverrideStateDto state = NewStore(registry, new FakeRestarter(), new FakeCeiling { Limit = 220_000 }).SetEnabled(true);

        Assert.Equal(GpuRomOverrideStates.Uncertain, state.State);
        Assert.Contains("read back", state.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_WhenTheRestartFails_IsUncertainAndSaysSo()
    {
        var restarter = new FakeRestarter { Failure = new TimeoutException("the GPU restart did not finish within 60 s") };

        GpuRomOverrideStateDto state = NewStore(new FakeRomRegistry(), restarter, new FakeCeiling { Limit = 175_000 }).SetEnabled(true);

        Assert.Equal(GpuRomOverrideStates.Uncertain, state.State);
        Assert.Contains("60 s", state.Reason!, StringComparison.Ordinal);
        Assert.False(state.Restarted);
    }

    [Fact]
    public void Apply_IsIdempotent()
    {
        var registry = new FakeRomRegistry();
        var restarter = new FakeRestarter();
        GpuRomOverrideStore store = NewStore(registry, restarter, new FakeCeiling { Limit = 220_000 });

        store.SetEnabled(true);
        int writesBefore = registry.Writes.Count;
        GpuRomOverrideStateDto again = store.SetEnabled(true);

        Assert.Equal(GpuRomOverrideStates.Unlocked, again.State);
        Assert.Equal(writesBefore, registry.Writes.Count);
        Assert.Equal(1, restarter.Calls);   // no second GPU restart for a no-op
        Assert.False(again.Restarted);
    }

    [Fact]
    public void Restore_DeletesOnlyOurRecords_AndIsSafeWhenNothingIsPatched()
    {
        var registry = new FakeRomRegistry();
        var restarter = new FakeRestarter();
        var ceiling = new FakeCeiling { Limit = 175_000 };
        GpuRomOverrideStore store = NewStore(registry, restarter, ceiling);

        // Nothing patched: a no-op that neither restarts nor writes.
        GpuRomOverrideStateDto untouched = store.SetEnabled(false);
        Assert.Equal(GpuRomOverrideStates.NotUnlocked, untouched.State);
        Assert.Equal(0, restarter.Calls);
        Assert.False(untouched.Restarted);

        ceiling.Limit = 220_000;
        Assert.Equal(GpuRomOverrideStates.Unlocked, store.SetEnabled(true).State);
        Assert.True(File.Exists(OriginalsPath));

        ceiling.Limit = 175_000;
        GpuRomOverrideStateDto restored = store.SetEnabled(false);

        Assert.Equal(GpuRomOverrideStates.NotUnlocked, restored.State);
        Assert.True(restored.Restarted);
        Assert.Empty(registry.Records);
        Assert.False(File.Exists(OriginalsPath));
        Assert.Equal(2, restarter.Calls);

        // Idempotent: a second restore changes nothing and does not restart again.
        GpuRomOverrideStateDto second = store.SetEnabled(false);
        Assert.Equal(GpuRomOverrideStates.NotUnlocked, second.State);
        Assert.False(second.Restarted);
        Assert.Equal(2, restarter.Calls);
    }

    [Fact]
    public void Restore_RefusesRecordsAnotherToolOwns()
    {
        var registry = new FakeRomRegistry();
        registry.Records["romOverride01"] = RomOverrideRecord.Encode("98.03.5E.00.5D", 0x57EFC, 17021);
        var restarter = new FakeRestarter();

        GpuRomOverrideStateDto state = NewStore(registry, restarter, new FakeCeiling()).SetEnabled(false);

        Assert.Equal(GpuRomOverrideStates.Unsupported, state.State);
        Assert.Single(registry.Records);
        Assert.Empty(registry.Deletes);
        Assert.Equal(0, restarter.Calls);
    }

    [Fact]
    public void Restore_DropsAStaleOriginalsFile()
    {
        // Records already gone (a driver reinstall undoes the patch): the file is no
        // longer a claim on anything, so it is removed instead of kept as a lure.
        var registry = new FakeRomRegistry();
        Directory.CreateDirectory(Path.GetDirectoryName(OriginalsPath)!);
        File.WriteAllLines(OriginalsPath, new[] { "98.03.5E.00.5D", "359478=175000" });

        GpuRomOverrideStateDto state = NewStore(registry, new FakeRestarter(), new FakeCeiling()).SetEnabled(false);

        Assert.Equal(GpuRomOverrideStates.NotUnlocked, state.State);
        Assert.Contains("stale", state.Reason!, StringComparison.Ordinal);
        Assert.False(File.Exists(OriginalsPath));
    }

    [Fact]
    public void Originals_MalformedFileIsRefusedWithoutTouchingTheKey()
    {
        var registry = new FakeRomRegistry();
        registry.Records["romOverride00"] = RomOverrideRecord.Encode("98.03.5E.00.5D", 0x57C36, 250000);
        Directory.CreateDirectory(Path.GetDirectoryName(OriginalsPath)!);
        File.WriteAllLines(OriginalsPath, new[] { "98.03.5E.00.5D", "not-an-offset=175000" });
        var restarter = new FakeRestarter();

        GpuRomOverrideStateDto state = NewStore(registry, restarter, new FakeCeiling()).SetEnabled(false);

        Assert.Equal(GpuRomOverrideStates.Unsupported, state.State);
        Assert.Contains("malformed", state.Reason!, StringComparison.Ordinal);
        Assert.Single(registry.Records);
        Assert.Equal(0, restarter.Calls);
    }

    private void AssertRefusal(Action<FakeRomRegistry> seed, string expected, bool withOriginals = false)
    {
        var registry = new FakeRomRegistry();
        seed(registry);

        if (withOriginals)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(OriginalsPath)!);
            var lines = new List<string> { RomOverrideLayout.PinnedVbiosVersion };
            lines.AddRange(RomOverrideLayout.Fields.Select(f => $"{f.Offset}={f.StockValue}"));
            File.WriteAllLines(OriginalsPath, lines);
        }

        var restarter = new FakeRestarter();

        GpuRomOverrideStateDto state = NewStore(registry, restarter, new FakeCeiling()).SetEnabled(true);

        Assert.Equal(GpuRomOverrideStates.Unsupported, state.State);
        Assert.Contains(expected, state.Reason!, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(registry.Writes);
        Assert.Empty(registry.Deletes);
        Assert.Equal(0, restarter.Calls);
    }

    private GpuRomOverrideStore NewStore(FakeRomRegistry registry, FakeRestarter restarter, FakeCeiling ceiling) =>
        new(registry, restarter, ceiling, OriginalsPath);

    private static string Hex(byte[] data) => BitConverter.ToString(data).ToUpperInvariant();

    private sealed class FakeRomRegistry : IGpuRomRegistry
    {
        public string? InstanceId { get; set; } = GpuRomOverrideTests.InstanceId;

        public string? Version { get; set; } = RomOverrideLayout.PinnedVbiosVersion;

        public Dictionary<string, byte[]> Records { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Writes { get; } = new();

        public List<string> Deletes { get; } = new();

        public bool Flushed { get; private set; }

        public bool DropWrites { get; set; }

        public bool FailFirstWrite { get; set; }

        public string? ReadVbiosVersion() => Version;

        public IReadOnlyDictionary<string, byte[]> ReadRecords() => Records;

        public void WriteRecord(string name, byte[] data)
        {
            if (FailFirstWrite && Writes.Count == 0)
                throw new InvalidOperationException("the driver key refused the write");

            Writes.Add(name);
            if (!DropWrites)
                Records[name] = data;
        }

        public void DeleteRecord(string name)
        {
            Deletes.Add(name);
            Records.Remove(name);
        }

        public void Flush() => Flushed = true;
    }

    private sealed class FakeRestarter : IGpuRestarter
    {
        public int Calls { get; private set; }

        public string? LastInstanceId { get; private set; }

        public Exception? Failure { get; set; }

        public bool Restart(string? instanceId)
        {
            Calls++;
            LastInstanceId = instanceId;
            if (Failure != null)
                throw Failure;

            return true;
        }
    }

    private sealed class FakeCeiling : IGpuPowerCeilingProbe
    {
        public int? Limit { get; set; }

        public int? ReadEnforcedLimitMilliwatts() => Limit;
    }
}
