using System.Text;
using System.Windows.Media;
using Uniwill_Control_Utility.Models;

namespace Uniwill_Control_Utility.Tests.Models;

/// <summary>
/// Tests for the 512-byte interleaved BetterRGB renderer (the reference's BUF_SIZE
/// frame: KEYMAP offsets + AUX mirrors for wide Shift/Enter keys).
/// </summary>
public sealed class BetterRgbFrameRendererTests
{
    private static BetterRgbFrameParams StaticParams(Color[] zoneColors, int zones)
    {
        var p = new BetterRgbFrameParams();
        p.StaticZoneColors = zoneColors;
        p.StaticZones = zones;
        return p;
    }

    private static Color[] SolidZones(byte r, byte g, byte b)
    {
        var zones = new Color[10];
        for (int i = 0; i < zones.Length; i++)
            zones[i] = Color.FromRgb(r, g, b);
        return zones;
    }

    [Fact]
    public void Static_FillsKeymapOffsets_LeavesPaddingZero()
    {
        var frame = new byte[BetterRgbFrameRenderer.FrameSize];
        BetterRgbFrameRenderer.Render(BetterRgbEffect.Static,
            StaticParams(SolidZones(255, 255, 255), 1), new BetterRgbFrameState(), 0L, 0.0, frame);

        // Protocol offsets (KEYMAP, better_rgb.c:227-253): Esc 21, CtrlL 1,
        // ShiftL 5, Backslash 349, End 477 — every triple white.
        Assert.Equal(255, (int)frame[21]);
        Assert.Equal(255, (int)frame[22]);
        Assert.Equal(255, (int)frame[23]);
        Assert.Equal(255, (int)frame[1]);
        Assert.Equal(255, (int)frame[477]);
        Assert.Equal(255, (int)frame[478]);
        Assert.Equal(255, (int)frame[479]);

        // Padding bytes outside any KEYMAP triple stay zero (set_key bounds).
        Assert.Equal(0, (int)frame[0]);
        Assert.Equal(0, (int)frame[4]);
        Assert.Equal(0, (int)frame[511]);
    }

    [Fact]
    public void Static_AuxMirrorForWideShiftAndEnter()
    {
        var frame = new byte[BetterRgbFrameRenderer.FrameSize];
        BetterRgbFrameRenderer.Render(BetterRgbEffect.Static,
            StaticParams(SolidZones(255, 255, 255), 1), new BetterRgbFrameState(), 0L, 0.0, frame);

        // set_key (1755-1766): OFFSET_SHIFT_LEFT 5 -> AUX 29, OFFSET_ENTER 321 -> AUX 345.
        Assert.Equal(255, (int)frame[29]);
        Assert.Equal(255, (int)frame[30]);
        Assert.Equal(255, (int)frame[31]);
        Assert.Equal(255, (int)frame[345]);
        Assert.Equal(255, (int)frame[346]);
        Assert.Equal(255, (int)frame[347]);
    }

    [Fact]
    public void Static_TwoHorizontalZones_SplitRowAtRow3()
    {
        var zones = SolidZones(255, 0, 0);
        zones[1] = Color.FromRgb(0, 0, 255);
        var p = StaticParams(zones, 2);

        var frame = new byte[BetterRgbFrameRenderer.FrameSize];
        BetterRgbFrameRenderer.Render(BetterRgbEffect.Static, p, new BetterRgbFrameState(), 0L, 0.0, frame);

        // horizontal_zone_for_row (2669-2679): zoneCount 2 -> row <= 3 ? 0 : 1.
        // Esc (row 0, offset 21) red; CtrlL (row 5, offset 1) blue.
        Assert.Equal(255, (int)frame[21]);
        Assert.Equal(0, (int)frame[23]);
        Assert.Equal(0, (int)frame[1]);
        Assert.Equal(255, (int)frame[3]);
    }

    [Fact]
    public void ApplyBrightness_ScalesFinishedFrameLikeSendFrame()
    {
        var frame = new byte[BetterRgbFrameRenderer.FrameSize];
        BetterRgbFrameRenderer.Render(BetterRgbEffect.Static,
            StaticParams(SolidZones(255, 255, 255), 1), new BetterRgbFrameState(), 0L, 0.0, frame);

        // C send_frame 1711-1715: byte * bright / 100 (integer division).
        BetterRgbFrameRenderer.ApplyBrightness(frame, 50);
        Assert.Equal(127, (int)frame[21]);

        BetterRgbFrameRenderer.ApplyBrightness(frame, 100); // no-op above 100
        Assert.Equal(127, (int)frame[21]);
    }

    [Fact]
    public void Render_IsDeterministic_ForIdenticalInputs()
    {
        // Wave has no randomness: two fresh states with identical inputs must
        // produce byte-identical frames (reconnect determinism).
        var p = new BetterRgbFrameParams();
        p.WaveAngle = 90;
        p.WaveSpeed = 5;

        var first = new byte[BetterRgbFrameRenderer.FrameSize];
        var second = new byte[BetterRgbFrameRenderer.FrameSize];
        BetterRgbFrameRenderer.Render(BetterRgbEffect.Wave, p, new BetterRgbFrameState(), 0L, 0.0, first);
        BetterRgbFrameRenderer.Render(BetterRgbEffect.Wave, p, new BetterRgbFrameState(), 0L, 0.0, second);

        Assert.Equal(BytesToString(first), BytesToString(second));
    }

    [Fact]
    public void Sparkle_FirstFrameSpawnsExactlyDensityKeys()
    {
        var p = new BetterRgbFrameParams(); // SparkleDensity default 8
        var st = new BetterRgbFrameState();

        var frame = new byte[BetterRgbFrameRenderer.FrameSize];
        BetterRgbFrameRenderer.Render(BetterRgbEffect.Sparkle, p, st, 0L, 0.0, frame);

        // The first frame spawns exactly `density` sparks (deficit = target - 0);
        // age increments after drawing, so every spawned key sits at age 2.
        int alive = 0;
        for (int i = 0; i < BetterRgbFrameState.KeyCount; i++)
            if (st.SparkleAge[i] > 0)
                alive++;
        Assert.Equal(8, alive);
    }

    [Fact]
    public void Sparkle_LifeDurationClampsToFloor18()
    {
        var p = new BetterRgbFrameParams();
        p.SparkleSpeed = 20; // 70 - 20*5 = -30 -> clamped to 18 (2738-2739)
        var st = new BetterRgbFrameState();

        var frame = new byte[BetterRgbFrameRenderer.FrameSize];
        BetterRgbFrameRenderer.Render(BetterRgbEffect.Sparkle, p, st, 0L, 0.0, frame);

        for (int i = 0; i < BetterRgbFrameState.KeyCount; i++)
        {
            if (st.SparkleAge[i] > 0)
            {
                Assert.True(st.SparkleDuration[i] >= 18 && st.SparkleDuration[i] < 30); // 18 + rand()%12
            }
        }
    }

    [Fact]
    public void Rainbow_FirstFramePaintsEveryKey_HueZeroRed()
    {
        var frame = new byte[BetterRgbFrameRenderer.FrameSize];
        BetterRgbFrameRenderer.Render(BetterRgbEffect.Rainbow,
            new BetterRgbFrameParams(), new BetterRgbFrameState(), 1L, 0.0, frame);

        // hue 0 -> HSV red on every KEYMAP triple.
        Assert.Equal(255, (int)frame[21]);
        Assert.Equal(0, (int)frame[22]);
        Assert.Equal(255, (int)frame[1]);
        Assert.Equal(255, (int)frame[477]);
    }

    private static string BytesToString(byte[] frame)
    {
        var hex = new StringBuilder();
        foreach (var b in frame)
            hex.Append(b.ToString("x2"));
        return hex.ToString();
    }
}
