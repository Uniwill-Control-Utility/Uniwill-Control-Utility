using Uniwill_Control_Utility.Services.Performance;

namespace Uniwill_Control_Utility.Tests.Services;

/// <summary>
/// The bounds contract the shared-memory reader enforces BEFORE any pointer
/// arithmetic. Field-proven shape: RTSS force-killed mid-write leaves the
/// section with a valid 'RTSS' signature and array geometry that runs past
/// the mapping; the reader's old zero-only validation walked the garbage
/// offsets, and the resulting AccessViolationException is uncatchable - the
/// app died at the 92% splash mark on every launch (2026-10-31). So every
/// header-derived offset must fail closed against the real view size.
/// </summary>
public sealed class RtssReaderLayoutTests
{
    // A real RTSS v2 section: 40-byte header, a 64-entry app array of ~288-byte
    // entries, in a generously sized mapping.
    private static RtssHeader RealHeader() => new()
    {
        Signature = 0x52545353,
        Version = 0x00020000,
        AppEntrySize = 288,
        AppArrOffset = 40,
        AppArrSize = 64,
    };

    [Fact]
    public void TheRealLayoutFits()
    {
        Assert.True(RtssReader.LayoutFits(RealHeader(), 40960));
    }

    [Fact]
    public void TheExactFitFits()
    {
        RtssHeader h = RealHeader();
        ulong exact = h.AppArrOffset + (ulong)h.AppArrSize * h.AppEntrySize;
        Assert.True(RtssReader.LayoutFits(h, exact));
    }

    [Fact]
    public void OneByteShortArrayIsRefused()
    {
        RtssHeader h = RealHeader();
        ulong oneShort = h.AppArrOffset + (ulong)h.AppArrSize * h.AppEntrySize - 1;
        Assert.False(RtssReader.LayoutFits(h, oneShort));
    }

    [Fact]
    public void TornGarbageArrayBoundsAreRefused()
    {
        // The field shape: signature valid, geometry garbage (force-killed RTSS).
        Assert.False(RtssReader.LayoutFits(new RtssHeader { AppArrOffset = 40, AppEntrySize = 288, AppArrSize = 0xFFFF_FFFF }, 40960));
        Assert.False(RtssReader.LayoutFits(new RtssHeader { AppArrOffset = 40, AppEntrySize = 0xFFFF_FFFF, AppArrSize = 64 }, 40960));
        Assert.False(RtssReader.LayoutFits(new RtssHeader { AppArrOffset = 0xFFFF_FFFF, AppEntrySize = 288, AppArrSize = 64 }, 40960));
    }

    [Fact]
    public void ZeroGeometryIsRefused()
    {
        Assert.False(RtssReader.LayoutFits(new RtssHeader { AppArrOffset = 0, AppEntrySize = 288, AppArrSize = 64 }, 40960));
        Assert.False(RtssReader.LayoutFits(new RtssHeader { AppArrOffset = 40, AppEntrySize = 0, AppArrSize = 64 }, 40960));
        Assert.False(RtssReader.LayoutFits(new RtssHeader { AppArrOffset = 40, AppEntrySize = 288, AppArrSize = 0 }, 40960));
    }

    [Fact]
    public void EntriesTooSmallToCarryTheReadFieldsAreRefused()
    {
        // The reader touches entry bytes through dwFrameTime (offset 280) + 4.
        Assert.False(RtssReader.LayoutFits(new RtssHeader { AppArrOffset = 40, AppEntrySize = 260, AppArrSize = 64 }, 40960));
        Assert.True(RtssReader.LayoutFits(new RtssHeader { AppArrOffset = 40, AppEntrySize = 284, AppArrSize = 64 }, 40960));
    }

    [Fact]
    public void ArrayOverlappingTheHeaderIsRefused()
    {
        Assert.False(RtssReader.LayoutFits(new RtssHeader { AppArrOffset = 20, AppEntrySize = 288, AppArrSize = 64 }, 40960));
    }

    [Fact]
    public void DegenerateViewSizesAreRefused()
    {
        RtssHeader h = RealHeader();
        Assert.False(RtssReader.LayoutFits(h, 0));
        Assert.False(RtssReader.LayoutFits(h, 39));   // header itself does not fit
        Assert.False(RtssReader.LayoutFits(h, (ulong)uint.MaxValue + 16)); // unaddressable by the int scan
    }
}
