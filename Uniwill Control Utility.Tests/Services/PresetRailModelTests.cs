using System;
using System.IO;
using Uniwill_Control_Utility.Models;

namespace Uniwill_Control_Utility.Tests.Services;

/// <summary>
/// The Extra GPU power rail currents are a preset field now, so three rules have to hold:
///
///   - Save stamps the slot and Load reads it back through the real file path pair
///     (<see cref="CustomPresetStore.SaveTo"/> / <see cref="CustomPresetStore.LoadFrom"/>);
///   - a preset file written before the fields existed loads with the tested tier's values
///     (250 000 / 70 000 / 14 026 mA), so an old slot never arrives with zeroed rails;
///   - a hand-edited zero is shaped back to the tier's value instead of being written.
/// </summary>
public sealed class PresetRailModelTests
{
    [Fact]
    public void RailCurrents_SurviveASaveAndLoadRoundTrip()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ucu-preset-rails-{Guid.NewGuid():N}.json");

        try
        {
            CustomPreset[] slots = NewSlots();
            slots[2].GpuRailNvvddMilliamps = 212_000;
            slots[2].GpuRailMsvddMilliamps = 100_000;
            slots[2].GpuRailBoardInputMilliamps = 16_971;

            CustomPresetStore.SaveTo(path, slots);
            CustomPreset[] loaded = CustomPresetStore.LoadFrom(path);

            Assert.Equal(212_000, loaded[2].GpuRailNvvddMilliamps);
            Assert.Equal(100_000, loaded[2].GpuRailMsvddMilliamps);
            Assert.Equal(16_971, loaded[2].GpuRailBoardInputMilliamps);

            // The untouched slots keep the tier's values.
            Assert.Equal(250_000, loaded[0].GpuRailNvvddMilliamps);
            Assert.Equal(70_000, loaded[0].GpuRailMsvddMilliamps);
            Assert.Equal(14_026, loaded[0].GpuRailBoardInputMilliamps);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void APresetFileWithoutTheRailFields_LoadsTheTierDefaults()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ucu-preset-legacy-{Guid.NewGuid():N}.json");

        try
        {
            // The shape this app wrote before the fields existed: no rail members at all.
            File.WriteAllText(path,
                "[" + Environment.NewLine +
                "  {" + Environment.NewLine +
                "    \"CpuSustained\": 65," + Environment.NewLine +
                "    \"GpuTgp\": 150.0" + Environment.NewLine +
                "  }" + Environment.NewLine +
                "]");

            CustomPreset[] loaded = CustomPresetStore.LoadFrom(path);

            Assert.Equal(65.0, loaded[0].CpuSustained);
            Assert.Equal(250_000, loaded[0].GpuRailNvvddMilliamps);
            Assert.Equal(70_000, loaded[0].GpuRailMsvddMilliamps);
            Assert.Equal(14_026, loaded[0].GpuRailBoardInputMilliamps);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AZeroedRailInTheFile_LoadsAsTheTierValue()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ucu-preset-zero-{Guid.NewGuid():N}.json");

        try
        {
            File.WriteAllText(path,
                "[" + Environment.NewLine +
                "  {" + Environment.NewLine +
                "    \"GpuRailNvvddMilliamps\": 0," + Environment.NewLine +
                "    \"GpuRailMsvddMilliamps\": -1," + Environment.NewLine +
                "    \"GpuRailBoardInputMilliamps\": 16971" + Environment.NewLine +
                "  }" + Environment.NewLine +
                "]");

            CustomPreset[] loaded = CustomPresetStore.LoadFrom(path);

            Assert.Equal(CustomPreset.DefaultRailNvvddMilliamps, loaded[0].GpuRailNvvddMilliamps);
            Assert.Equal(CustomPreset.DefaultRailMsvddMilliamps, loaded[0].GpuRailMsvddMilliamps);
            Assert.Equal(16_971, loaded[0].GpuRailBoardInputMilliamps);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static CustomPreset[] NewSlots()
    {
        var slots = new CustomPreset[CustomPreset.MaxSlots];
        for (int i = 0; i < slots.Length; i++)
            slots[i] = new CustomPreset();
        return slots;
    }
}
