using System;
using System.Collections.Generic;
using System.Linq;
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
    ConnectingDevices,
    RestoringAdaptiveMode,
    LoadingGameLibrary,
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
        new StartupProgressState(58, "Initializing sensors"),
        new StartupProgressState(66, "Applying startup settings"),
        new StartupProgressState(74, "Starting services"),
        new StartupProgressState(82, "Connecting devices"),
        new StartupProgressState(88, "Restoring adaptive mode"),
        new StartupProgressState(92, "Loading game library"),
        new StartupProgressState(96, "Preparing interface"),
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

/// <summary>
/// Tracks parallel Phase 1 tasks and reports the current blocker via callbacks.
/// Each task is identified by its original index in the tasks array passed to the constructor.
/// When a task completes, the label callback fires with remaining tasks as a bullet list.
/// The progress callback fires with the updated percent.
/// Uses callbacks to avoid a circular dependency on the Views layer.
/// </summary>
internal sealed class ParallelPhaseTracker
{
    private readonly Action<string, string?> _setLabel;
    private readonly Action<int> _setProgress;
    private readonly string _phaseLabel;
    // Maps original index -> display label (null for invisible tasks)
    private readonly Dictionary<int, string> _taskMap;
    private readonly HashSet<int> _done = new();
    private readonly int _visibleCount;
    private int _startPercent;
    private int _endPercent;

    internal ParallelPhaseTracker(
        string phaseLabel,
        IEnumerable<(string LabelKey, bool Visible)> tasks,
        int startPercent,
        int endPercent,
        Action<string, string?> setLabel,
        Action<int> setProgress)
    {
        _setLabel = setLabel;
        _setProgress = setProgress;
        _phaseLabel = phaseLabel;
        _startPercent = startPercent;
        _endPercent = endPercent;

        int index = 0;
        _taskMap = new Dictionary<int, string>();
        foreach (var t in tasks)
        {
            if (t.Visible)
            {
                _taskMap[index] = LocalizationService.Get(t.LabelKey);
            }
            index++;
        }
        _visibleCount = _taskMap.Count;

        UpdateLabel();
        _setProgress(_startPercent);
    }

    /// <summary>
    /// Call when a tracked task completes. The index must match the original position
    /// in the tasks array passed to the constructor (0-based).
    /// Invisible tasks are no-ops.
    /// </summary>
    internal void TaskCompleted(int originalIndex)
    {
        if (_taskMap.ContainsKey(originalIndex))
        {
            _done.Add(originalIndex);
        }
        UpdateLabel();
        UpdateProgress();
    }

    private void UpdateLabel()
    {
        var remainingList = _taskMap
            .OrderBy(kv => kv.Key)
            .Where(kv => !_done.Contains(kv.Key))
            .Select(kv => kv.Value)
            .ToList();

        _setLabel(_phaseLabel, FormatRemaining(remainingList));
    }

    private static string FormatRemaining(IList<string> remaining)
    {
        if (remaining.Count == 0)
            return string.Empty;

        var lines = remaining.Select(t => $"- {t}").ToArray();
        return string.Join("\n", lines);
    }

    private void UpdateProgress()
    {
        var range = _endPercent - _startPercent;
        var percent = _startPercent + (int)((double)_done.Count / _visibleCount * range);
        _setProgress(percent);
    }
}
