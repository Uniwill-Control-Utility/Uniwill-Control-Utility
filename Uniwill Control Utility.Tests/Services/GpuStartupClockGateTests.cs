using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Uniwill_Control_Utility.Properties;
using Uniwill_Control_Utility.Services;
using UCU.Core.Supervision;

namespace Uniwill_Control_Utility.Tests.Services;

/// <summary>
/// "Apply GPU clock offsets at startup" (Settings.ApplyGpuClocksOnStartup, default
/// on) is a startup-only gate: off, the boot restore still applies the GPU power
/// limit, the EC limits, the EC activation and the fans, but writes neither the
/// core clock offset, the V-F curve nor the memory offset. A manual apply ignores
/// the setting entirely.
///
/// The --NVIDIA-Clocks token carries core, memory, TGP and Dynamic Boost together,
/// and SetClocks(0, 0) is a real write (it resets the offsets to stock), so the
/// gate cannot be expressed by zeroing the clock fields: it has to emit a
/// clock-free power token instead. These assertions pin both the flag plumbing and
/// that token shape.
/// </summary>
public sealed class GpuStartupClockGateTests
{
    [Fact]
    public async Task Arbiter_MarksOnlyTheBootRestoreAsTheStartupApply()
    {
        var calls = new List<(string Profile, bool Startup)>();
        var arbiter = new PresetArbiter(
            apply: (profile, _, startup) =>
            {
                calls.Add((profile, startup));
                return Task.CompletedTask;
            },
            isAdaptiveRunning: () => false,
            persistSelection: _ => { });

        await arbiter.RequestAsync(PresetIntentSource.BootRestore, "Office");
        await arbiter.RequestAsync(PresetIntentSource.UserInterface, "Gaming");

        Assert.Equal(new[] { ("Office", true), ("Gaming", false) }, calls);
    }

    [Fact]
    public void Settings_AppliesGpuClocksOnStartupByDefault()
    {
        PropertyInfo? property = typeof(Settings).GetProperty(nameof(Settings.ApplyGpuClocksOnStartup));

        Assert.NotNull(property);
        Assert.Equal(
            bool.TrueString,
            property.GetCustomAttribute<DefaultSettingValueAttribute>()?.Value);
    }

    [Fact]
    public void StartupApplier_EmitsAPowerOnlyToken_AndSkipsTheVfCurve()
    {
        string applier = ReadAppFile("Services", "PresetProfileApplier.cs");

        Assert.Contains("private static bool SkipsGpuClocks(bool startup) =>", applier, StringComparison.Ordinal);
        Assert.Contains("startup && !Settings.Default.ApplyGpuClocksOnStartup", applier, StringComparison.Ordinal);

        // The power limit survives as a clock-free token; no clock field is written.
        Assert.Contains("? $\"--NVIDIA-Power={totalWatts}-{dbOffset}\"", applier, StringComparison.Ordinal);
        Assert.DoesNotContain("--NVIDIA-Clocks=0-0-0-", applier, StringComparison.Ordinal);
        // Step 2 of the apply (the V-F curve, which also carries the memory offset)
        // must not run on a startup apply that skipped the clocks.
        int skip = applier.IndexOf("if (skipClocks)", StringComparison.Ordinal);
        int curve = applier.IndexOf("ApplyVfCurveFromPreset(presets[customSlot]);", StringComparison.Ordinal);
        Assert.True(skip >= 0, "The V-F curve branch must be gated on skipClocks.");
        Assert.True(curve > skip, "The V-F curve apply must sit behind the skipClocks gate.");
    }

    [Fact]
    public void StartupCommandBuilder_EmitsAPowerOnlyToken_WhenClocksAreOff()
    {
        string source = ReadAppFile("Views", "Windows", "MainWindow.xaml.cs");
        string body = MethodBody(source, "private static string BuildCustomCommandString");

        Assert.Contains("if (!Settings.Default.ApplyGpuClocksOnStartup)", body, StringComparison.Ordinal);
        Assert.Contains("parts.Add($\"--NVIDIA-Power={totalWatts}-{dbOffset}\");", body, StringComparison.Ordinal);
    }

    [Fact]
    public void BootCustomApply_SkipsTheVfCurve_ButKeepsThePowerLimit()
    {
        string source = ReadAppFile("Views", "Windows", "MainWindow.xaml.cs");
        string body = MethodBody(source, "private static async Task ApplyCustomOnStart");

        // The curve block (its SetPowerLimit writes included) is gated; the power
        // limit arrives through the --NVIDIA-Power token in the command string.
        Assert.Contains(
            "if (p.VfCurveOffsets.Count > 0 && Settings.Default.ApplyGpuClocksOnStartup)",
            body,
            StringComparison.Ordinal);
        Assert.Contains("ApplyFanSettingsFromPreset(p);", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Translator_AppliesThePowerOnlyTokenWithoutTouchingTheClocks()
    {
        string translator = ReadAppFile("Scripts", "RyzenAdj_To_UCU.cs");
        int branch = translator.IndexOf("command == \"NVIDIA-Power\"", StringComparison.Ordinal);

        Assert.True(branch >= 0, "The translator must understand --NVIDIA-Power.");
        int nextBranch = translator.IndexOf("command == \"NVIDIA-Clocks\"", branch, StringComparison.Ordinal);
        Assert.True(nextBranch > branch, "The clocks branch must follow the power-only branch.");
        string body = translator.Substring(branch, nextBranch - branch);
        Assert.Contains("NvTuning.SetPowerLimit(", body, StringComparison.Ordinal);
        Assert.DoesNotContain("SetClocks(", body, StringComparison.Ordinal);
        Assert.DoesNotContain("SetMaxGPUClock(", body, StringComparison.Ordinal);
    }

    [Fact]
    public void ManualApply_IgnoresTheStartupGate()
    {
        // The Presets page's apply is user-initiated: it must keep writing the clocks
        // even with the startup option off.
        Assert.DoesNotContain(
            "ApplyGpuClocksOnStartup",
            ReadAppFile("Views", "Pages", "Presets.xaml.cs"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void BothStartupPaths_LeaveABreadcrumbNamingWhatWasSkipped()
    {
        const string line = "'Apply GPU clock offsets at startup': core clock offset, V-F curve and memory offset not applied";

        Assert.Contains(line, ReadAppFile("Views", "Windows", "MainWindow.xaml.cs"), StringComparison.Ordinal);
        Assert.Contains(line, ReadAppFile("Services", "PresetProfileApplier.cs"), StringComparison.Ordinal);
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
