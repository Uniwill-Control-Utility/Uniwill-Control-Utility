using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Reflection;
using Uniwill_Control_Utility.Models;
using Uniwill_Control_Utility.Properties;
using Uniwill_Control_Utility.Services;
using Uniwill_Control_Utility.Views.Windows;

namespace Uniwill_Control_Utility.Tests.Services;

/// <summary>
/// "Apply CPU Curve Optimizer at startup" (Settings.ApplyCpuCurveOptimizerOnStartup,
/// default on) is the CPU twin of the GPU-clock startup switch: off, the boot restore
/// still writes the power limits, the thermal limits, the Intel PL pair, the EC limits
/// and the fans, but emits no --set-coall / --set-coper / --set-coper-batch. A manual
/// apply ignores the setting.
///
/// A Curve Optimizer offset of 0 is a real write (it resets the curve), so the gate
/// cannot be expressed by zeroing the offsets: it has to emit no token at all. The
/// legacy CommandString replay carries both token families, and each switch strips
/// only its own.
/// </summary>
public sealed class CpuCurveOptimizerStartupGateTests
{
    private const long Mb = 1024L * 1024L;

    private const string ReplayCommand =
        "--slow-limit=75000 --fast-limit=85000 --tctl-temp=95 --cHTC-temp=95 " +
        "--set-coall=16777196 --set-cogfx=5 --NVIDIA-Clocks=0-100-200-245-20 " +
        "--set-coper-batch=1048561,1048562 ";

    private static CpuTopology TwoByEight() =>
        CpuTopologyReader.Build(
            Enumerable.Range(0, 16).Select(i => (0b11UL << (i * 2), true)),
            new List<(ulong, long)> { (0x0000_FFFFUL, 96 * Mb), (0xFFFF_0000UL, 32 * Mb) });

    [Fact]
    public void Settings_AppliesCpuCurveOptimizerOnStartupByDefault()
    {
        PropertyInfo? property = typeof(Settings).GetProperty(nameof(Settings.ApplyCpuCurveOptimizerOnStartup));

        Assert.NotNull(property);
        Assert.Equal(
            bool.TrueString,
            property.GetCustomAttribute<DefaultSettingValueAttribute>()?.Value);
    }

    [Fact]
    public void SkippedStartupApply_EmitsNoCurveOptimizerToken_InsteadOfAZeroOffset()
    {
        var skipped = new List<string>();
        PresetProfileApplier.AppendCoCommands(
            skipped,
            new CustomPreset { CpuCoMode = CpuCoMode.Unified, CpuCoUnifiedValue = -20 },
            TwoByEight(),
            skip: true);

        Assert.Empty(skipped);

        var applied = new List<string>();
        PresetProfileApplier.AppendCoCommands(
            applied,
            new CustomPreset { CpuCoMode = CpuCoMode.Unified, CpuCoUnifiedValue = -20 },
            TwoByEight());

        Assert.Equal("--set-coall=16777196", Assert.Single(applied));
    }

    [Fact]
    public void SkippedStartupApply_EmitsNoPerCoreTokens()
    {
        var parts = new List<string>();
        PresetProfileApplier.AppendCoCommands(
            parts,
            new CustomPreset { CpuCoMode = CpuCoMode.PerCcd, CpuCoCcd0 = -15, CpuCoCcd1 = -25 },
            TwoByEight(),
            skip: true);

        Assert.Empty(parts);
    }

    [Fact]
    public void StartupApplier_ThreadsTheGateThroughBothCustomBuilders()
    {
        string applier = ReadAppFile("Services", "PresetProfileApplier.cs");

        Assert.Contains("private static bool SkipsCurveOptimizer(bool startup) =>", applier, StringComparison.Ordinal);
        Assert.Contains("startup && !Settings.Default.ApplyCpuCurveOptimizerOnStartup", applier, StringComparison.Ordinal);
        Assert.Contains("bool skipCo = SkipsCurveOptimizer(startup);", applier, StringComparison.Ordinal);
        // Both the slot builder and the legacy settings builder carry the flag, and the
        // legacy Curve Optimizer block is inside the gate.
        Assert.Contains("bool skipCo = false", applier, StringComparison.Ordinal);
        Assert.Contains("if (!skipCo && coUnified)", applier, StringComparison.Ordinal);
        Assert.Contains("else if (!skipCo)", applier, StringComparison.Ordinal);
        // Neither a zeroed token nor a zero offset fallback.
        Assert.DoesNotContain("--set-coall=0\"", applier, StringComparison.Ordinal);
        Assert.DoesNotContain("--set-coper=0\"", applier, StringComparison.Ordinal);
    }

    [Fact]
    public void StartupCommandBuilder_EmitsTheCurveOptimizerOnlyWhenAllowed()
    {
        string source = ReadAppFile("Views", "Windows", "MainWindow.xaml.cs");
        string body = MethodBody(source, "private static string BuildCustomCommandString");

        Assert.Contains("if (Settings.Default.ApplyCpuCurveOptimizerOnStartup)", body, StringComparison.Ordinal);
        Assert.Contains("PresetProfileApplier.AppendCoCommands(parts, p, CpuTopologyReader.Read());", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Replay_StripsOnlyTheTokensEachSwitchOwns()
    {
        // Both off: no curve writes, no clock fields; the power limit survives as --NVIDIA-Power
        // and every CPU limit plus the unrelated --set-cogfx token replay untouched.
        string both = MainWindow.StripStartupGatedOffsets(ReplayCommand, stripClocks: true, stripCurveOptimizer: true);

        Assert.DoesNotContain("--set-coall=", both, StringComparison.Ordinal);
        Assert.DoesNotContain("--set-coper", both, StringComparison.Ordinal);
        Assert.DoesNotContain("--NVIDIA-Clocks=", both, StringComparison.Ordinal);
        Assert.Contains("--NVIDIA-Power=245-20", both, StringComparison.Ordinal);
        Assert.Contains("--set-cogfx=5", both, StringComparison.Ordinal);
        Assert.Contains("--slow-limit=75000", both, StringComparison.Ordinal);
        Assert.Contains("--fast-limit=85000", both, StringComparison.Ordinal);
        Assert.Contains("--tctl-temp=95", both, StringComparison.Ordinal);
        Assert.Contains("--cHTC-temp=95", both, StringComparison.Ordinal);
    }

    [Fact]
    public void Replay_KeepsTheCurveOptimizerWhenOnlyTheGpuSwitchIsOff()
    {
        string stripped = MainWindow.StripStartupGatedOffsets(ReplayCommand, stripClocks: true, stripCurveOptimizer: false);

        Assert.Contains("--set-coall=16777196", stripped, StringComparison.Ordinal);
        Assert.Contains("--set-coper-batch=1048561,1048562", stripped, StringComparison.Ordinal);
        Assert.DoesNotContain("--NVIDIA-Clocks=", stripped, StringComparison.Ordinal);
    }

    [Fact]
    public void Replay_KeepsTheGpuClocksWhenOnlyTheCpuSwitchIsOff()
    {
        string stripped = MainWindow.StripStartupGatedOffsets(ReplayCommand, stripClocks: false, stripCurveOptimizer: true);

        Assert.Contains("--NVIDIA-Clocks=0-100-200-245-20", stripped, StringComparison.Ordinal);
        Assert.DoesNotContain("--NVIDIA-Power=", stripped, StringComparison.Ordinal);
        Assert.DoesNotContain("--set-coall=", stripped, StringComparison.Ordinal);
        Assert.DoesNotContain("--set-coper", stripped, StringComparison.Ordinal);
    }

    [Fact]
    public void ManualApply_IgnoresTheStartupGate()
    {
        // The Presets and Adaptive pages' applies are user-initiated: they must keep
        // writing the Curve Optimizer with the startup option off.
        Assert.DoesNotContain(
            "ApplyCpuCurveOptimizerOnStartup",
            ReadAppFile("Views", "Pages", "Presets.xaml.cs"),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ApplyCpuCurveOptimizerOnStartup",
            ReadAppFile("Views", "Pages", "Adaptive.xaml.cs"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void BothStartupPaths_LeaveABreadcrumbNamingWhatWasSkipped()
    {
        const string line = "'Apply CPU Curve Optimizer at startup': per-core and unified CO offsets not applied";

        Assert.Contains(line, ReadAppFile("Views", "Windows", "MainWindow.xaml.cs"), StringComparison.Ordinal);
        Assert.Contains(line, ReadAppFile("Services", "PresetProfileApplier.cs"), StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsPage_ReflectsAndSavesTheSwitch()
    {
        string code = ReadAppFile("Views", "Pages", "SettingsPage.xaml.cs");

        Assert.Contains("cbApplyCpuCurveOptimizerOnStartup.IsChecked = Settings.Default.ApplyCpuCurveOptimizerOnStartup;", code, StringComparison.Ordinal);
        Assert.Contains("Settings.Default.ApplyCpuCurveOptimizerOnStartup = true;", code, StringComparison.Ordinal);
        Assert.Contains("Settings.Default.ApplyCpuCurveOptimizerOnStartup = false;", code, StringComparison.Ordinal);
    }

    private static string MethodBody(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find {signature}.");

        int end = source.IndexOf("\n        }", start, StringComparison.Ordinal);
        Assert.True(end > start, $"Could not find the end of {signature}.");

        return source.Substring(start, end - start);
    }

    private static string ReadAppFile(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { RepositoryRoot(), "Uniwill Control Utility" }.Concat(parts).ToArray()));

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "Uniwill Control Utility")) &&
                File.Exists(Path.Combine(directory.FullName, "Uniwill Control Utility.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Repository root not found from " + AppContext.BaseDirectory);
    }
}
