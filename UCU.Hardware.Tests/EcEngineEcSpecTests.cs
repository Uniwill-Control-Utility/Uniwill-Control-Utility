using UCU.Core.Ec;
using UCU.Hardware.Ec;

namespace UCU.Hardware.Tests;

/// <summary>
/// ECSpec at the engine seam: the per-model power table is read through the transport
/// (fake here), cached, and used to answer two questions the GPU power path cannot work
/// without - what base TGP the cTGP offset is measured from, and whether this machine may
/// take a GPU power-control write at all.
///
/// The old code assumed a 55 W base, so a 175 W request wrote offset 120 on a 95 W-base
/// board (95 + 120 = 215 W enforced). Every test below pins the replacement: the board's
/// own DefaultTGP when the table is there, a LOGGED fallback to 55 only when it is not,
/// and a refusal with a reason instead of a blind write.
/// </summary>
public sealed class EcEngineEcSpecTests
{
    /// <summary>The reference machine (IDY_7Y): PL1/PL2 210 W, PL4 420 W, DefaultTGP 95 W.</summary>
    private static byte[] Idy7Y() => new byte[]
    {
        0xD2, // PL1 210 W
        0xD2, // PL2 210 W
        0xD2, // PL4 420 W (doubled)
        0x0A, // TCC offset 10 -> 95 C
        0x37, // TGP offset max 55 W
        0x19, // DB max 25 W
        0x57, // GPU target 87 C
        0x5F, // DefaultTGP 95 W
        0xFF, // DB CPU TDP: not applicable
    };

    private static EcEngine EngineWith(IEcTransport transport)
    {
        var engine = new EcEngine(transport, EcPlatform.Amd);
        Assert.True(engine.Initialize());
        return engine;
    }

    [Fact]
    public void GetEcSpec_ValidTable_DecodesTheEngineFields()
    {
        var transport = new FakeEcTransport();
        transport.PresetSpecTable(Idy7Y());
        using EcEngine engine = EngineWith(transport);

        EcSpec? spec = engine.GetEcSpec();

        Assert.NotNull(spec);
        Assert.Equal(210, spec!.Pl1MaxWatts);
        Assert.Equal(210, spec.Pl2MaxWatts);
        Assert.Equal(420, spec.Pl4MaxWatts);
        Assert.Equal(10, spec.TccOffsetMax);
        Assert.Equal(55, spec.TgpOffsetMaxWatts);
        Assert.Equal(25, spec.DynamicBoostMaxWatts);
        Assert.Equal(87, spec.GpuTargetTemperatureC);
        Assert.Equal(95, spec.DefaultTgpWatts);
        Assert.Null(spec.DynamicBoostCpuTdpWatts);
    }

    [Fact]
    public void GetEcSpec_IsCached_AfterASuccessfulRead()
    {
        var transport = new FakeEcTransport();
        transport.PresetSpecTable(Idy7Y());
        using EcEngine engine = EngineWith(transport);

        Assert.NotNull(engine.GetEcSpec());
        // A second call must not re-read: the table is firmware data for the boot.
        transport.PresetSpecTable(0x50, 0x50, 0x50, 0, 0, 0, 0, 0x50, 0xFF);
        Assert.Equal(95, engine.GetEcSpec()!.DefaultTgpWatts);
    }

    [Fact]
    public void GetEcSpec_NoTableOnThisEc_IsUnavailableInsteadOfThrowing()
    {
        var transport = new FakeEcTransport(); // answers garbage: no 0xBB echo, no table
        using EcEngine engine = EngineWith(transport);

        Assert.Null(engine.GetEcSpec());
    }

    [Fact]
    public void GetEcSpec_TransportFailure_IsUnavailableInsteadOfThrowing()
    {
        var transport = new FailingSpecTransport();
        using EcEngine engine = EngineWith(transport);

        Assert.Null(engine.GetEcSpec());          // bounded: a verb never sees the Win32 error
        Assert.False(engine.GetGpuPowerInfo().Supported);
    }

    [Fact]
    public void GetEcSpec_BeforeInitialize_IsUnavailable()
    {
        var transport = new FakeEcTransport();
        transport.PresetSpecTable(Idy7Y());
        using var engine = new EcEngine(transport, EcPlatform.Amd);

        Assert.Null(engine.GetEcSpec());
    }

    [Fact]
    public void GetGpuPowerInfo_ValidTable_IsSupportedWithTheBoardBase()
    {
        var transport = new FakeEcTransport();
        transport.PresetSpecTable(Idy7Y());
        using EcEngine engine = EngineWith(transport);

        var info = engine.GetGpuPowerInfo();

        Assert.True(info.Supported);
        Assert.Equal(95, info.DefaultTgpWatts);
        Assert.Equal(string.Empty, info.Reason);
    }

    [Fact]
    public void GetGpuPowerInfo_NoTable_IsUnsupportedWithAReasonAndTheFallbackBase()
    {
        var transport = new FakeEcTransport();
        using EcEngine engine = EngineWith(transport);

        var info = engine.GetGpuPowerInfo();

        Assert.False(info.Supported);
        Assert.Equal(EcSpecCodec.FallbackDefaultTgpWatts, info.DefaultTgpWatts);
        Assert.Contains("ECSpec unavailable", info.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void GetGpuPowerInfo_OutOfRangeDefaultTgp_IsUnsupportedWithTheValueInTheReason()
    {
        // A table that answers (valid echo) but whose DefaultTGP is 20 W: not a board base.
        var transport = new FakeEcTransport();
        transport.PresetSpecTable(0xD2, 0xD2, 0xD2, 0x0A, 0x37, 0x19, 0x57, 20, 0xFF);
        using EcEngine engine = EngineWith(transport);

        var info = engine.GetGpuPowerInfo();

        Assert.False(info.Supported);
        Assert.Equal(EcSpecCodec.FallbackDefaultTgpWatts, info.DefaultTgpWatts);
        Assert.Contains("20 W", info.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void GetGpuPowerInfo_DefaultTgpNotApplicableMarker_IsUnsupported()
    {
        // 0xFF at index 7 is the EC's "not applicable" marker, not a 255 W board.
        var transport = new FakeEcTransport();
        transport.PresetSpecTable(0xD2, 0xD2, 0xD2, 0x0A, 0x37, 0x19, 0x57, 0xFF, 0xFF);
        using EcEngine engine = EngineWith(transport);

        Assert.False(engine.GetGpuPowerInfo().Supported);
    }

    [Fact]
    public void CtgpOffsetForTgpWatts_UsesTheBoardDefault_NotAConstant()
    {
        var transport = new FakeEcTransport();
        transport.PresetSpecTable(Idy7Y()); // DefaultTGP 95 W (the dev machine)
        using EcEngine engine = EngineWith(transport);

        // The bug this replaces: 175 - 55 = 120 written, 95 + 120 = 215 W enforced.
        Assert.Equal(80, engine.CtgpOffsetForTgpWatts(175));
        Assert.Equal(55, engine.CtgpOffsetForTgpWatts(150));
        Assert.Equal(0, engine.CtgpOffsetForTgpWatts(95));
    }

    [Fact]
    public void CtgpOffsetForTgpWatts_NoTable_FallsBackToTheLoggedFiftyFive()
    {
        var transport = new FakeEcTransport();
        using EcEngine engine = EngineWith(transport);

        // The DTO answers the fallback base, so the offset is a guess the caller logs.
        Assert.Equal(EcSpecCodec.FallbackDefaultTgpWatts, engine.GetGpuPowerInfo().DefaultTgpWatts);
        Assert.Equal(175 - EcSpecCodec.FallbackDefaultTgpWatts, engine.CtgpOffsetForTgpWatts(175));
    }

    [Fact]
    public void DumpHardwareState_CarriesTheDecodedSpec()
    {
        var transport = new FakeEcTransport();
        transport.PresetSpecTable(Idy7Y());
        using EcEngine engine = EngineWith(transport);

        var dump = engine.DumpHardwareState();

        Assert.True(dump.EcSpecValid);
        Assert.Equal(210, dump.SpecPl1Max);
        Assert.Equal(210, dump.SpecPl2Max);
        Assert.Equal(420, dump.SpecPl4Max);
        Assert.Equal(10, dump.SpecTccOffsetMax);
        Assert.Equal(55, dump.SpecTgpOffsetMax);
        Assert.Equal(25, dump.SpecDynamicBoostMax);
        Assert.Equal(87, dump.SpecGpuTargetTemp);
        Assert.Equal(95, dump.SpecDefaultTgp);
        Assert.Equal(0xFF, dump.SpecDynamicBoostCpuTdp); // raw: "not applicable"
    }

    [Fact]
    public void DumpHardwareState_NoTable_ReportsUnavailableAndWritesNothing()
    {
        var transport = new FakeEcTransport();
        using EcEngine engine = EngineWith(transport);
        transport.ClearLog();

        var dump = engine.DumpHardwareState();

        Assert.False(dump.EcSpecValid);
        Assert.Equal(0, dump.SpecDefaultTgp);
        Assert.Empty(transport.Writes);
    }

    /// <summary>An EC that answers registers but fails the ECSpec IOCTL.</summary>
    private sealed class FailingSpecTransport : IEcTransport
    {
        private readonly FakeEcTransport _inner = new();

        public bool IsOpen => _inner.IsOpen;

        public void Open() => _inner.Open();

        public void Reconnect() => _inner.Reconnect();

        public byte Read(ushort address) => _inner.Read(address);

        public void Write(ushort address, byte value) => _inner.Write(address, value);

        public uint ReadEcSpec(int index) =>
            throw new System.ComponentModel.Win32Exception(1, "simulated ECSpec IOCTL failure");

        public void Dispose() => _inner.Dispose();
    }
}
