using UCU.Core.Ec;

namespace UCU.Core.Tests.Ec;

/// <summary>
/// The ECSpec table decoder: the EC's per-model power table is the only place the board's
/// base TGP exists, and the cTGP offset written to 0x0744 is measured from it. Three rules
/// are pinned here because a wrong one silently mis-sets every GPU power limit:
/// <list type="bullet">
///   <item>the dword window is a byte read (upper three bytes are neighbours, not data);</item>
///   <item>PL4 is stored at half its wattage (the documented x2);</item>
///   <item>index 9 echoing 0xBB is what proves a table exists at all.</item>
/// </list>
/// Field values come from the reference machine (IDY_7Y) in re/notes/ECSpec.md, which
/// matched 6/6 known parameters.
/// </summary>
public sealed class EcSpecCodecTests
{
    /// <summary>The reference machine's raw table, as re/notes/ECSpec.md prints it.</summary>
    private static uint[] Idy7YTable() => new uint[]
    {
        0x0AD2D2D2, // [0] PL1 = 0xD2
        0x370AD2D2, // [1] PL2 = 0xD2
        0x19370AD2, // [2] PL4 raw = 0xD2
        0x5719370A, // [3] TCC offset = 0x0A
        0x5F571937, // [4] TGP offset max = 0x37
        0xFF5F5719, // [5] DB max = 0x19
        0xFFFF5F57, // [6] GPU target = 0x57
        0xFFFFFF5F, // [7] DefaultTGP = 0x5F
        0xFFFFFFFF, // [8] DB CPU TDP = 0xFF (not applicable)
        0x000009BB, // [9] out-of-range echo
        0x00000ABB, // [10]
        0x00000BBB, // [11]
    };

    [Fact]
    public void Decode_ReferenceTable_MatchesTheDocumentedFields()
    {
        EcSpec? spec = EcSpecCodec.Decode(Idy7YTable());

        Assert.NotNull(spec);
        Assert.Equal(210, spec!.Pl1MaxWatts);
        Assert.Equal(210, spec.Pl2MaxWatts);
        Assert.Equal(420, spec.Pl4MaxWatts);       // raw 0xD2 doubled, per the encoding
        Assert.Equal(10, spec.TccOffsetMax);
        Assert.Equal(55, spec.TgpOffsetMaxWatts);  // an offset: TGP max = 55 + 95
        Assert.Equal(25, spec.DynamicBoostMaxWatts);
        Assert.Equal(87, spec.GpuTargetTemperatureC);
        Assert.Equal(95, spec.DefaultTgpWatts);
        Assert.Null(spec.DynamicBoostCpuTdpWatts); // 0xFF = not applicable
    }

    [Fact]
    public void ByteAt_TakesTheLowByte_NotTheNeighboursLeakingThrough()
    {
        // The whole point of the window: 0x370AD2D2 is PL2 in byte 0, and only 0xD2 is data.
        Assert.Equal(0xD2, EcSpecCodec.ByteAt(Idy7YTable(), 1));
        Assert.Equal(0x37, EcSpecCodec.ByteAt(Idy7YTable(), 4));
    }

    [Fact]
    public void Pl4_DoublesTheRawEntry()
    {
        Assert.Equal(420, EcSpecCodec.Pl4Watts(0xD2));
        Assert.Equal(0, EcSpecCodec.Pl4Watts(0));
        Assert.Equal(510, EcSpecCodec.Pl4Watts(0xFF));
    }

    [Fact]
    public void IsValid_RequiresTheOutOfRangeEchoAtIndexNine()
    {
        Assert.True(EcSpecCodec.IsValid(Idy7YTable()));

        // An EC that refuses the table answers garbage at every index - no echo at 9.
        var refused = new uint[EcSpecCodec.DwordCount];
        for (int i = 0; i < refused.Length; i++)
            refused[i] = 0xFFFFFFFF;
        Assert.False(EcSpecCodec.IsValid(refused));

        // Index 9 answering data (a real table byte) is not an echo either.
        uint[] truncated = Idy7YTable();
        truncated[9] = 0xD2;
        Assert.False(EcSpecCodec.IsValid(truncated));

        // Fewer dwords than the request: the read did not complete.
        Assert.False(EcSpecCodec.IsValid(new uint[EcSpecCodec.EchoIndex]));
        Assert.False(EcSpecCodec.IsValid(Array.Empty<uint>()));
    }

    [Fact]
    public void Decode_RefusesATableWithoutTheEcho()
    {
        uint[] unavailable = Idy7YTable();
        unavailable[9] = 0x000009BB & ~0xFFu; // echo byte gone

        Assert.Null(EcSpecCodec.Decode(unavailable));
    }

    [Theory]
    [InlineData(0, false)]     // 0x00: an empty entry, not a TGP
    [InlineData(49, false)]
    [InlineData(50, true)]     // the documented lower bound
    [InlineData(95, true)]     // the reference machine
    [InlineData(150, true)]
    [InlineData(200, true)]    // the documented upper bound
    [InlineData(201, false)]
    [InlineData(0xFF, false)]  // the "not applicable" marker read as a wattage
    public void IsPlausibleDefaultTgp_PinsTheBoardRange(int watts, bool expected) =>
        Assert.Equal(expected, EcSpecCodec.IsPlausibleDefaultTgp(watts));

    [Fact]
    public void CpuTemperatureLimit_IsTjMaxMinusTheOffset()
    {
        Assert.Equal(95, EcSpecCodec.CpuTemperatureLimitC(10)); // the reference machine
        Assert.Equal(105, EcSpecCodec.CpuTemperatureLimitC(0));
    }

    [Theory]
    [InlineData(175, 95, 80)]   // the dev machine: THE bug this replaces (175 - 55 wrote 120)
    [InlineData(175, 80, 95)]
    [InlineData(150, 95, 55)]
    [InlineData(95, 95, 0)]
    [InlineData(50, 95, 0)]     // below base: the register cannot go negative
    [InlineData(400, 95, 255)]  // above the register's byte
    public void CtgpOffsetForTgp_IsMeasuredFromTheBoardBase(int target, int defaultTgp, int expected) =>
        Assert.Equal(expected, EcSpecCodec.CtgpOffsetForTgp(target, defaultTgp));
}
