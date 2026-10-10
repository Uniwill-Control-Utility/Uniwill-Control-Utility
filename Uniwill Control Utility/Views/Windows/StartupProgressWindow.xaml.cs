using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Animation;
using Uniwill_Control_Utility.Services;
using Wpf.Ui.Controls;

namespace Uniwill_Control_Utility.Views.Windows;

public partial class StartupProgressWindow : FluentWindow
{
    private readonly Queue<StartupProgressState> _pendingStates = new();
    private Task _playbackTask = Task.CompletedTask;

    public StartupProgressWindow()
    {
        InitializeComponent();
    }

    internal void QueueProgress(StartupProgressState state)
    {
        _pendingStates.Enqueue(state);
        if (_playbackTask.IsCompleted)
        {
            _playbackTask = PlayPendingStatesAsync();
        }
    }

    internal Task WaitForProgressPlaybackAsync() => _playbackTask;

    /// <summary>
    /// Updates the title and detail rows directly, bypassing the queued animation.
    /// Used for dynamic blocker labels during parallel startup phases.
    /// </summary>
    internal void SetStatusLabel(string title, string? detail = null)
    {
        StatusTitle.Text = title;
        StatusDetail.Text = detail ?? string.Empty;
    }

    /// <summary>
    /// Single-string overload for non-tracker status updates (sets title only, clears detail).
    /// </summary>
    internal void SetStatusLabelSingle(string text)
    {
        StatusTitle.Text = text;
        StatusDetail.Text = string.Empty;
    }

    /// <summary>
    /// Updates the progress bar value directly without animation.
    /// Used for phase-level progress tracking during parallel startup.
    /// </summary>
    internal void SetProgressValue(int percent)
    {
        StartupProgressBar.BeginAnimation(RangeBase.ValueProperty, null);
        StartupProgressBar.Value = percent;
        PercentageText.Text = $"{percent}%";
    }

    private async Task PlayPendingStatesAsync()
    {
        while (_pendingStates.TryDequeue(out var state))
        {
            ApplyProgress(state);
            await Task.Delay(StartupProgressAnimation.StageDisplayDuration);
        }
    }

    private void ApplyProgress(StartupProgressState state)
    {
        StatusTitle.Text = LocalizationService.Get(state.StatusKey);
        StatusDetail.Text = string.Empty;
        PercentageText.Text = $"{state.Percent}%";

        var currentValue = StartupProgressBar.Value;
        var transition = StartupProgressAnimation.Create(currentValue, state.Percent);

        StartupProgressBar.BeginAnimation(RangeBase.ValueProperty, null);
        StartupProgressBar.Value = state.Percent;

        var animation = new DoubleAnimation
        {
            From = transition.From,
            To = transition.To,
            Duration = transition.Duration,
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };

        StartupProgressBar.BeginAnimation(
            RangeBase.ValueProperty,
            animation,
            HandoffBehavior.SnapshotAndReplace);
    }
}
