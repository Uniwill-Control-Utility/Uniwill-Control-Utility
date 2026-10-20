using System;
using System.Collections.Generic;
using System.Threading;
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
/// Tracks parallel Phase 1 tasks and reports their state via callbacks.
/// The title always shows the phase label; the detail row stays hidden unless
/// a tracked task is still running past the slow-task threshold, in which case
/// the detail row shows that task's label (e.g. "Connecting devices…").
/// Each task is identified by its original index in the tasks array passed to
/// the constructor. Uses callbacks to avoid a circular dependency on the Views layer.
/// </summary>
internal sealed class ParallelPhaseTracker
{
    private readonly Action<string, string?> _setLabel;
    private readonly Action<int> _setProgress;
    private readonly Action<Action> _postToUiThread;
    private readonly string _phaseLabel;
    // Maps original index -> display label (no entry for invisible tasks)
    private readonly Dictionary<int, string> _taskMap;
    private readonly object _stateLock = new();
    private readonly HashSet<int> _done = new();
    private readonly int _visibleCount;
    private readonly int _startPercent;
    private readonly int _endPercent;
    private readonly int? _slowTaskIndex;
    private readonly string? _slowDetail;
    private string? _activeDetail;
    private Timer? _slowWatchdog;

    private static readonly TimeSpan DefaultSlowTaskThreshold = TimeSpan.FromSeconds(3);

    internal ParallelPhaseTracker(
        string phaseLabel,
        IEnumerable<(string LabelKey, bool Visible)> tasks,
        int startPercent,
        int endPercent,
        Action<string, string?> setLabel,
        Action<int> setProgress,
        Action<Action> postToUiThread,
        string? slowTaskLabelKey = null,
        TimeSpan? slowTaskThreshold = null)
    {
        var threshold = slowTaskThreshold ?? DefaultSlowTaskThreshold;

        _setLabel = setLabel;
        _setProgress = setProgress;
        _postToUiThread = postToUiThread;
        _phaseLabel = LocalizationService.Get(phaseLabel);
        _startPercent = startPercent;
        _endPercent = endPercent;

        int index = 0;
        _taskMap = new Dictionary<int, string>();
        int? slowTaskIndex = null;
        foreach (var t in tasks)
        {
            if (t.Visible)
            {
                _taskMap[index] = LocalizationService.Get(t.LabelKey);
            }
            if (slowTaskLabelKey is not null && t.LabelKey == slowTaskLabelKey)
            {
                slowTaskIndex = index;
            }
            index++;
        }
        _visibleCount = _taskMap.Count;

        // Arm the watchdog only for visible slow tasks so disabled features
        // (e.g. both device integrations off) can never surface a stale label.
        if (slowTaskIndex is int slowIndex && _taskMap.TryGetValue(slowIndex, out string slowLabel))
        {
            _slowTaskIndex = slowIndex;
            _slowDetail = slowLabel + "…";
            _slowWatchdog = new Timer(
                _ => OnSlowTaskThreshold(),
                null,
                threshold,
                Timeout.InfiniteTimeSpan);
        }

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
        bool slowCompleted;
        lock (_stateLock)
        {
            slowCompleted = originalIndex == _slowTaskIndex;
            if (_taskMap.ContainsKey(originalIndex))
            {
                _done.Add(originalIndex);
            }
        }

        if (slowCompleted)
        {
            _slowWatchdog?.Dispose();
            _slowWatchdog = null;
            _activeDetail = null;
        }

        UpdateLabel();
        UpdateProgress();
    }

    private void OnSlowTaskThreshold()
    {
        // Re-check on the UI thread so a task that completed between the timer
        // firing and this callback running cannot revive the slow-task label.
        _postToUiThread(() =>
        {
            bool pending;
            lock (_stateLock)
            {
                pending = _slowTaskIndex is int idx && !_done.Contains(idx);
            }
            if (pending)
            {
                _activeDetail = _slowDetail;
                UpdateLabel();
            }
        });
    }

    private void UpdateLabel()
    {
        _setLabel(_phaseLabel, _activeDetail);
    }

    private void UpdateProgress()
    {
        int doneCount;
        lock (_stateLock)
        {
            doneCount = _done.Count;
        }

        var range = _endPercent - _startPercent;
        var percent = _startPercent + (int)((double)doneCount / _visibleCount * range);
        _setProgress(percent);
    }
}
