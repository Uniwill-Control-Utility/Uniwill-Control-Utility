using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UCU.Core.Gpu;
using Uniwill_Control_Utility.Scripts.Misc;
using Uniwill_Control_Utility.Services;

namespace Uniwill_Control_Utility.Tests.Services;

/// <summary>
/// The startup clock settle (field, 2026-11-17): a clock write issued seconds after boot is
/// accepted and does not take, while the identical write on a settled GPU lands. The settle
/// acts only on the apply's own readback verdict - NOT in force - and re-issues the last
/// clock write at +5/+15/+30/+60 s, stopping at the first live answer.
/// </summary>
public sealed class GpuClockSettleTests
{
    private static readonly MemoryOffsetVerdict NotInForce =
        new(1000, true, false, "memory reads 14001 MHz at pstate 0, the 14001 MHz stock clock");

    private static readonly MemoryOffsetVerdict Live =
        new(1000, true, true, "memory 14001 -> 15001 MHz (+1000) at pstate 0");

    private static readonly MemoryOffsetVerdict Unverified =
        new(1000, false, false, "the memory pstate moved 0 -> 8 around the write, so the clocks are not comparable");

    [Fact]
    public async Task NotInForce_ReappliesAndStopsAtTheFirstLiveVerdict()
    {
        var reads = new[] { NotInForce, NotInForce, Live };
        int index = 0;
        int writes = 0;
        var waits = new List<double>();

        var settle = new GpuClockSettle(
            () => index < reads.Length ? reads[index++] : reads[^1],
            () => { writes++; return 1; },
            delay => { waits.Add(delay.TotalSeconds); return Task.CompletedTask; });

        int attempts = await settle.RunAsync();

        Assert.Equal(2, attempts);
        Assert.Equal(2, writes);
        // Absolute +5/+15 s means the second wait is the 10 s between them.
        Assert.Equal(new[] { 5d, 10d }, waits);
    }

    [Fact]
    public async Task UnverifiedVerdict_NeverRetries()
    {
        int writes = 0;
        int waits = 0;

        var settle = new GpuClockSettle(
            () => Unverified,
            () => { writes++; return 1; },
            _ => { waits++; return Task.CompletedTask; });

        int attempts = await settle.RunAsync();

        Assert.Equal(0, attempts);
        Assert.Equal(0, writes);
        Assert.Equal(0, waits);
    }

    [Fact]
    public async Task LiveOrMissingVerdict_SchedulesNothing()
    {
        foreach (MemoryOffsetVerdict? startup in new MemoryOffsetVerdict?[] { Live, null })
        {
            int writes = 0;
            var settle = new GpuClockSettle(
                () => startup,
                () => { writes++; return 1; },
                _ => Task.CompletedTask);

            Assert.Equal(0, await settle.RunAsync());
            Assert.Equal(0, writes);
        }
    }

    [Fact]
    public async Task UnverifiedAfterOneAttempt_StopsInsteadOfHammeringTheDriver()
    {
        var reads = new[] { NotInForce, Unverified };
        int index = 0;
        int writes = 0;

        var settle = new GpuClockSettle(
            () => index < reads.Length ? reads[index++] : reads[^1],
            () => { writes++; return 1; },
            _ => Task.CompletedTask);

        Assert.Equal(1, await settle.RunAsync());
        Assert.Equal(1, writes);
    }

    [Fact]
    public async Task ScheduleIsBounded_AndWarnsWithTheNumbersWhenItNeverTakes()
    {
        int writes = 0;
        var waits = new List<double>();

        var settle = new GpuClockSettle(
            () => NotInForce,
            () => { writes++; return 1; },
            delay => { waits.Add(delay.TotalSeconds); return Task.CompletedTask; });

        List<string> lines = await CaptureAsync(settle.RunAsync);

        Assert.Equal(4, writes);
        Assert.Equal(new[] { 5d, 10d, 15d, 30d }, waits);

        string warning = Assert.Single(lines, line => line.Contains("WARNING", StringComparison.Ordinal));
        Assert.Contains("still NOT in force after 4 attempts", warning, StringComparison.Ordinal);
        Assert.Contains("+1000 MHz", warning, StringComparison.Ordinal);
        Assert.Contains("memory reads 14001 MHz at pstate 0", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALateAttempt_LogsThatItLandedLate()
    {
        var reads = new[] { NotInForce, NotInForce, NotInForce, Live };
        int index = 0;

        var settle = new GpuClockSettle(
            () => index < reads.Length ? reads[index++] : reads[^1],
            () => 1,
            _ => Task.CompletedTask);

        List<string> lines = await CaptureAsync(settle.RunAsync);

        Assert.Contains(lines, line => line.Contains("clock settle: attempt 3 at +30s: live", StringComparison.Ordinal));
        string landed = Assert.Single(lines, line => line.Contains("landed on attempt 3 at +30s", StringComparison.Ordinal));
        Assert.Contains("only took effect late", landed, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyTheStartupPathSchedulesTheSettle()
    {
        string window = ReadAppFile("Views", "Windows", "MainWindow.xaml.cs");
        Assert.Equal(1, CountOf(window, "GpuClockSettle.ScheduleAfterStartup"));

        int start = window.IndexOf("internal static async Task ApplyOnStart()", StringComparison.Ordinal);
        Assert.True(start > 0, "ApplyOnStart not found");
        int end = window.IndexOf("\n        }", start, StringComparison.Ordinal);
        Assert.True(end > start, "ApplyOnStart body not terminated");
        Assert.Contains("GpuClockSettle.ScheduleAfterStartup", window.Substring(start, end - start), StringComparison.Ordinal);

        // A manual apply runs on a settled machine and must not schedule anything.
        Assert.DoesNotContain("GpuClockSettle", ReadAppFile("Views", "Pages", "Presets.xaml.cs"), StringComparison.Ordinal);
    }

    private static async Task<List<string>> CaptureAsync(Func<Task<int>> run)
    {
        var lines = new List<string>();
        void Handler(string line)
        {
            if (line.Contains("clock settle:", StringComparison.Ordinal)) lines.Add(line);
        }

        DiagnosticLogger.OnMessage += Handler;
        try
        {
            await run();
        }
        finally
        {
            DiagnosticLogger.OnMessage -= Handler;
        }

        return lines;
    }

    private static int CountOf(string text, string value)
    {
        int count = 0;
        for (int at = text.IndexOf(value, StringComparison.Ordinal); at >= 0;
             at = text.IndexOf(value, at + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
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
