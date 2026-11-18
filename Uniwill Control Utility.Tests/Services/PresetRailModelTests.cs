using System;
using System.IO;
using System.Linq;
using Uniwill_Control_Utility.Models;

namespace Uniwill_Control_Utility.Tests.Services;

/// <summary>
/// The rail currents are not part of a preset any more: they are a machine-wide choice written by
/// the sliders' own Apply. Two rules hold across that removal:
///
///   - the model carries no rail field, so Save cannot stamp one;
///   - a preset file written while it did still loads, with the removed members ignored rather
///     than treated as an error.
/// </summary>
public sealed class PresetRailModelTests
{
    [Fact]
    public void ThePresetModelCarriesNoRailCurrents()
    {
        string[] railMembers = typeof(CustomPreset)
            .GetProperties()
            .Select(property => property.Name)
            .Where(name => name.Contains("Rail", StringComparison.Ordinal))
            .ToArray();

        Assert.Empty(railMembers);
    }

    [Fact]
    public void APresetFileWithTheRemovedRailFields_StillLoads()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ucu-preset-rail-legacy-{Guid.NewGuid():N}.json");

        try
        {
            // The shape the rail-carrying build wrote: the fields are unknown to this model now,
            // and the reader ignores members it does not know.
            File.WriteAllText(path,
                "[" + Environment.NewLine +
                "  {" + Environment.NewLine +
                "    \"CpuSustained\": 65," + Environment.NewLine +
                "    \"GpuTgp\": 200.0," + Environment.NewLine +
                "    \"GpuRailNvvddMilliamps\": 212000," + Environment.NewLine +
                "    \"GpuRailMsvddMilliamps\": 100000," + Environment.NewLine +
                "    \"GpuRailBoardInputMilliamps\": 16971" + Environment.NewLine +
                "  }" + Environment.NewLine +
                "]");

            CustomPreset[] loaded = CustomPresetStore.LoadFrom(path);

            Assert.Equal(CustomPreset.MaxSlots, loaded.Length);
            Assert.Equal(65.0, loaded[0].CpuSustained);
            Assert.Equal(200.0, loaded[0].GpuTgp);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
