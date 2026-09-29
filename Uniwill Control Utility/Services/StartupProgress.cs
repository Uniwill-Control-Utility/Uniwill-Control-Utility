using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Uniwill_Control_Utility.Services;

internal sealed record StartupProgressState(int Percent, string StatusKey);

internal enum StartupStage
{
    InitializingApplication,
    PreparingDiagnostics,
    CheckingPrerequisites,
    ReadingSystemProfile,
    ConfiguringServices,
    LoadingSettings,
    DetectingHardware,
    InitializingSensors,
    ApplyingStartupSettings,
    StartingServices,
    PreparingInterface,
    OpeningDashboard,
    Complete
}

internal static class StartupProgress
{
    internal static IReadOnlyList<StartupProgressState> Timeline { get; } = new[]
    {
        new StartupProgressState(4, "Initializing application"),
        new StartupProgressState(10, "Preparing diagnostics"),
        new StartupProgressState(18, "Checking prerequisites"),
        new StartupProgressState(26, "Reading system profile"),
        new StartupProgressState(34, "Configuring services"),
        new StartupProgressState(42, "Loading settings"),
        new StartupProgressState(50, "Detecting hardware"),
        new StartupProgressState(66, "Initializing sensors"),
        new StartupProgressState(76, "Applying startup settings"),
        new StartupProgressState(84, "Starting services"),
        new StartupProgressState(92, "Preparing interface"),
        new StartupProgressState(98, "Opening dashboard"),
        new StartupProgressState(100, "Ready")
    };

    internal static StartupProgressState For(StartupStage stage) => Timeline[(int)stage];
}

internal sealed record StartupProgressTransition(double From, double To, TimeSpan Duration);

internal static class StartupProgressAnimation
{
    internal static TimeSpan StageDisplayDuration { get; } = TimeSpan.FromMilliseconds(140);
    private static TimeSpan TransitionDuration { get; } = TimeSpan.FromMilliseconds(110);

    internal static StartupProgressTransition Create(double currentValue, int targetValue) =>
        new(currentValue, targetValue, TransitionDuration);
}

internal sealed class StartupProgressReporter
{
    private Func<StartupStage, Task>? _report;

    internal StartupProgressReporter(Func<StartupStage, Task> report)
    {
        _report = report;
    }

    private StartupProgressReporter()
    {
    }

    internal static StartupProgressReporter Create(
        bool enabled,
        Func<StartupStage, Task> report) =>
        enabled ? new StartupProgressReporter(report) : new StartupProgressReporter();

    internal Task ReportAsync(StartupStage stage) =>
        _report?.Invoke(stage) ?? Task.CompletedTask;

    internal void Detach() => _report = null;
}
