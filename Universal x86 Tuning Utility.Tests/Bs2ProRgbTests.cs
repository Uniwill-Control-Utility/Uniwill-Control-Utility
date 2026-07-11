using Universal_x86_Tuning_Utility.Models;

namespace Universal_x86_Tuning_Utility.Tests;

/// <summary>
/// Tests for RGB frame building commands and LightUploadData structure.
/// </summary>
public class Bs2ProRgbCommandTests
{
    [Fact]
    public void RgbOff_Frame_BuildsCorrectly()
    {
        // THRM example: 5A A5 46 03 00 49
        var frame = Bs2ProFrame.Build(Bs2ProCommand.RgbEnable, Bs2ProRgbEnable.Off);
        Assert.Equal(new byte[] { 0x5A, 0xA5, 0x46, 0x03, 0x00, 0x49 }, frame);
    }

    [Fact]
    public void RgbOn_Frame_BuildsCorrectly()
    {
        // THRM example: 5A A5 46 03 01 4A
        var frame = Bs2ProFrame.Build(Bs2ProCommand.RgbEnable, Bs2ProRgbEnable.On);
        Assert.Equal(new byte[] { 0x5A, 0xA5, 0x46, 0x03, 0x01, 0x4A }, frame);
    }

    [Fact]
    public void RgbCommit_Frame_BuildsCorrectly()
    {
        // 0x43 with payload 0x01: 5A A5 43 03 01 47
        var frame = Bs2ProFrame.Build(Bs2ProCommand.RgbCommit, 0x01);
        Assert.Equal(new byte[] { 0x5A, 0xA5, 0x43, 0x03, 0x01, 0x47 }, frame);
    }

    [Fact]
    public void RgbDynamicMode_Frame_BuildsCorrectly()
    {
        // 0x44 with payload 0x01 (activate smart-temp)
        // 5A A5 44 03 01 48
        var frame = Bs2ProFrame.Build(Bs2ProCommand.RgbDynamicMode, 0x01);
        Assert.Equal(new byte[] { 0x5A, 0xA5, 0x44, 0x03, 0x01, 0x48 }, frame);
    }

    [Fact]
    public void RgbStatus_Heartbeat_BuildsCorrectly()
    {
        // 0x45 02
        var frame = Bs2ProFrame.Build(Bs2ProCommand.RgbStatus, 0x02);
        // Verify individually to avoid C# ambiguous overload with string suffixes
        var expected = new byte[] { 0x5A, 0xA5, 0x45, 0x03, 0x02, 0x4A };
        Assert.Equal(expected, frame);
    }

    [Fact]
    public void RgbStatus_HeartbeatParam_BuildsCorrectly()
    {
        // 0x45 with payload 0x03 0x01: checksum = 0x45+0x04+0x03+0x01 = 0x4D
        var frame = Bs2ProFrame.Build(Bs2ProCommand.RgbStatus, 0x03, 0x01);
        Assert.Equal(new byte[] { 0x5A, 0xA5, 0x45, 0x04, 0x03, 0x01, 0x4D }, frame);
    }

    [Fact]
    public void RgbUploadInit_BuildsCorrectly()
    {
        // 0x41 with payload 0x02: checksum = 0x41+0x03+0x02 = 0x46
        var frame = Bs2ProFrame.Build(Bs2ProCommand.RgbUploadInit, 0x02);
        Assert.Equal(new byte[] { 0x5A, 0xA5, 0x41, 0x03, 0x02, 0x46 }, frame);
    }

    [Fact]
    public void RgbUploadInitConfirm_BuildsCorrectly()
    {
        // 0x41 with payload 0x03 0x01: checksum = 0x41+0x04+0x03+0x01 = 0x49
        var frame = Bs2ProFrame.Build(Bs2ProCommand.RgbUploadInit, 0x03, 0x01);
        Assert.Equal(new byte[] { 0x5A, 0xA5, 0x41, 0x04, 0x03, 0x01, 0x49 }, frame);
    }

    [Fact]
    public void RgbFrameWrite_HeaderFrame_BuildsCorrectly()
    {
        // 0x47 with 11-byte payload (frame_index + 10 bytes f0)
        var payload = new byte[11] { 0x00, 0x00, 0x02, 0x00, 0x00, 0x0A, 0x64, 0xFF, 0x00, 0x00, 0x00 };
        var frame = Bs2ProFrame.Build(Bs2ProCommand.RgbFrameWrite, payload);

        // Should produce a valid frame with correct magic and checksum
        Assert.Equal(0x5A, frame[0]);
        Assert.Equal(0xA5, frame[1]);
        Assert.Equal(Bs2ProCommand.RgbFrameWrite, frame[2]);
        Assert.Equal(0x0D, frame[3]); // LEN = 2 + 11 = 13 = 0x0D
    }

    [Fact]
    public void LightReport_Is65Bytes()
    {
        var frame = Bs2ProFrame.Build(Bs2ProCommand.RgbEnable, Bs2ProRgbEnable.Off);
        var report = Bs2ProFrame.BuildReport(frame, Bs2ProReportLength.Light);

        Assert.Equal(Bs2ProReportLength.Light, report.Length);
        Assert.Equal(Bs2ProReportId.Output, report[0]);
    }
}

/// <summary>
/// Tests for the RGB frame generator that produces animation data for each mode.
/// </summary>
public class Bs2ProRgbFrameGeneratorTests
{
    #region Static Mode Tests

    [Fact]
    public void Static_Generates30Frames()
    {
        var data = Bs2ProRgbFrameGenerator.Generate("static", 255, 0, 0, Bs2ProRgbSpeed.Medium, 100);

        Assert.NotNull(data.Frames);
        Assert.Equal(30, data.Frames.GetLength(0));
        Assert.Equal(10, data.Frames.GetLength(1));
    }

    [Fact]
    public void Static_HeaderFrame_Is10Bytes()
    {
        var data = Bs2ProRgbFrameGenerator.Generate("static", 255, 0, 0, Bs2ProRgbSpeed.Medium, 100);
        Assert.Equal(10, data.Header.Length);
    }

    [Fact]
    public void Static_HeaderModeCode_IsZero()
    {
        var data = Bs2ProRgbFrameGenerator.Generate("static", 255, 0, 0, Bs2ProRgbSpeed.Medium, 100);
        Assert.Equal(0x00, data.Header[3]); // mode code
    }

    [Fact]
    public void Static_ColorWrittenAtTargetIndices()
    {
        var data = Bs2ProRgbFrameGenerator.Generate("static", 255, 128, 64, Bs2ProRgbSpeed.Medium, 100);

        var targets = new[] { 2, 5, 8, 11, 14 };
        foreach (int idx in targets)
        {
            Assert.Equal((byte)255, data.Frames[idx, 6]);
            Assert.Equal((byte)128, data.Frames[idx, 7]);
            Assert.Equal((byte)64, data.Frames[idx, 8]);
        }
    }

    [Fact]
    public void Static_NonTargetFrames_AreZero()
    {
        var data = Bs2ProRgbFrameGenerator.Generate("static", 255, 0, 0, Bs2ProRgbSpeed.Medium, 100);
        var targets = new HashSet<int> { 2, 5, 8, 11, 14 };

        for (int fi = 0; fi < 30; fi++)
        {
            if (!targets.Contains(fi))
            {
                for (int j = 0; j < 10; j++)
                {
                    byte val = data.Frames[fi, j];
                    Assert.Equal(0, val);
                }
            }
        }
    }

    [Fact]
    public void Static_BrightnessScalesColor()
    {
        // 50% brightness should halve the RGB values
        var data = Bs2ProRgbFrameGenerator.Generate("static", 200, 100, 50, Bs2ProRgbSpeed.Medium, 50);

        Assert.Equal((byte)100, data.Frames[2, 6]); // 200 * 0.5
        Assert.Equal((byte)50, data.Frames[2, 7]);  // 100 * 0.5
        Assert.Equal((byte)25, data.Frames[2, 8]);  // 50 * 0.5
    }

    [Fact]
    public void Static_ZeroBrightness_ProducesBlackFrames()
    {
        var data = Bs2ProRgbFrameGenerator.Generate("static", 255, 255, 255, Bs2ProRgbSpeed.Medium, 0);

        for (int fi = 0; fi < 30; fi++)
        {
            for (int j = 0; j < 10; j++)
                Assert.Equal((byte)0, data.Frames[fi, j]);
        }
    }

    #endregion

    #region Rotation Mode Tests

    [Fact]
    public void Rotation_Generates30Frames()
    {
        var data = Bs2ProRgbFrameGenerator.Generate("rotation", 255, 0, 0, Bs2ProRgbSpeed.Fast, 100);
        Assert.Equal(30, data.Frames.GetLength(0));
    }

    [Fact]
    public void Rotation_HeaderModeCode_Is0x05()
    {
        var data = Bs2ProRgbFrameGenerator.Generate("rotation", 255, 0, 0, Bs2ProRgbSpeed.Fast, 100);
        Assert.Equal((byte)0x05, data.Header[3]);
    }

    [Fact]
    public void Rotation_FramesAreNonZero()
    {
        var data = Bs2ProRgbFrameGenerator.Generate("rotation", 255, 0, 0, Bs2ProRgbSpeed.Fast, 100);

        // Rotation should produce non-trivial frame data (not all zeros)
        bool hasNonZero = false;
        for (int fi = 0; fi < 30; fi++)
        {
            for (int j = 0; j < 10; j++)
            {
                if (data.Frames[fi, j] != 0)
                {
                    hasNonZero = true;
                    break;
                }
            }
            if (hasNonZero) break;
        }
        Assert.True(hasNonZero, "Rotation frames should contain non-zero data");
    }

    [Fact]
    public void Rotation_f0Bytes6to9_FromStream()
    {
        var data = Bs2ProRgbFrameGenerator.Generate("rotation", 255, 128, 64, Bs2ProRgbSpeed.Fast, 100);
        // f0[6..9] should be populated from the color stream (first 4 bytes)
        Assert.Equal((byte)255, data.Header[6]); // R from first color
        Assert.Equal((byte)128, data.Header[7]); // G
        Assert.Equal((byte)64, data.Header[8]);  // B
    }

    #endregion

    #region Flowing Mode Tests

    [Fact]
    public void Flowing_Generates30Frames()
    {
        var data = Bs2ProRgbFrameGenerator.Generate("flowing", 0, 255, 0, Bs2ProRgbSpeed.Medium, 100);
        Assert.Equal(30, data.Frames.GetLength(0));
    }

    [Fact]
    public void Flowing_HeaderUsesGreenBaseColor()
    {
        var data = Bs2ProRgbFrameGenerator.Generate("flowing", 0, 255, 0, Bs2ProRgbSpeed.Medium, 100);
        Assert.Equal((byte)0x00, data.Header[6]);
        Assert.Equal((byte)0xFF, data.Header[7]);
        Assert.Equal((byte)0x00, data.Header[8]);
    }

    [Fact]
    public void Flowing_HeaderModeCode_Is0x05()
    {
        var data = Bs2ProRgbFrameGenerator.Generate("flowing", 0, 255, 0, Bs2ProRgbSpeed.Medium, 100);
        Assert.Equal((byte)0x05, data.Header[3]);
    }

    [Fact]
    public void Flowing_FramesAreNonZero()
    {
        var data = Bs2ProRgbFrameGenerator.Generate("flowing", 0, 255, 0, Bs2ProRgbSpeed.Medium, 100);

        bool hasNonZero = false;
        for (int fi = 0; fi < 30; fi++)
        {
            for (int j = 0; j < 10; j++)
            {
                if (data.Frames[fi, j] != 0)
                {
                    hasNonZero = true;
                    break;
                }
            }
            if (hasNonZero) break;
        }
        Assert.True(hasNonZero, "Flowing frames should contain non-zero template data");
    }

    #endregion

    #region Breathing Mode Tests

    [Fact]
    public void Breathing_Generates30Frames()
    {
        var data = Bs2ProRgbFrameGenerator.Generate("breathing", 0, 0, 255, Bs2ProRgbSpeed.Medium, 100);
        Assert.Equal(30, data.Frames.GetLength(0));
    }

    [Fact]
    public void Breathing_HeaderModeCode_Is0x01_ForSingleColor()
    {
        // 1 color → mode code = 1*2 - 1 = 1
        var data = Bs2ProRgbFrameGenerator.Generate("breathing", 0, 0, 255, Bs2ProRgbSpeed.Medium, 100);
        Assert.Equal((byte)0x01, data.Header[3]);
    }

    [Fact]
    public void Breathing_f0Bytes6to9_FromStream()
    {
        var data = Bs2ProRgbFrameGenerator.Generate("breathing", 100, 50, 200, Bs2ProRgbSpeed.Medium, 100);
        Assert.Equal((byte)100, data.Header[6]);
        Assert.Equal((byte)50, data.Header[7]);
        Assert.Equal((byte)200, data.Header[8]);
        Assert.Equal((byte)0, data.Header[9]); // padding zero
    }

    [Fact]
    public void Breathing_FramesAreNonZero()
    {
        var data = Bs2ProRgbFrameGenerator.Generate("breathing", 255, 0, 0, Bs2ProRgbSpeed.Medium, 100);

        bool hasNonZero = false;
        for (int fi = 0; fi < 30; fi++)
        {
            for (int j = 0; j < 10; j++)
            {
                if (data.Frames[fi, j] != 0)
                {
                    hasNonZero = true;
                    break;
                }
            }
            if (hasNonZero) break;
        }
        Assert.True(hasNonZero, "Breathing frames should contain non-zero data");
    }

    #endregion

    #region Speed Constants

    [Fact]
    public void RgbSpeed_Constants_MatchThrmValues()
    {
        Assert.Equal((byte)0x05, Bs2ProRgbSpeed.Fast);
        Assert.Equal((byte)0x0A, Bs2ProRgbSpeed.Medium);
        Assert.Equal((byte)0x0F, Bs2ProRgbSpeed.Slow);
    }

    #endregion

    #region Header Frame Structure

    [Fact]
    public void HeaderFrame_FixedBytes_AreCorrect()
    {
        var data = Bs2ProRgbFrameGenerator.Generate("static", 255, 0, 0, Bs2ProRgbSpeed.Medium, 100);
        Assert.Equal((byte)0x00, data.Header[0]);
        Assert.Equal((byte)0x02, data.Header[1]);
        Assert.Equal((byte)0x00, data.Header[2]);
    }

    [Fact]
    public void HeaderFrame_SpeedAndBrightness_AreSet()
    {
        var data = Bs2ProRgbFrameGenerator.Generate("static", 255, 0, 0, Bs2ProRgbSpeed.Fast, 75);
        Assert.Equal(Bs2ProRgbSpeed.Medium, data.Header[4]); // static always uses medium
        Assert.Equal((byte)75, data.Header[5]);
    }

    #endregion
}
