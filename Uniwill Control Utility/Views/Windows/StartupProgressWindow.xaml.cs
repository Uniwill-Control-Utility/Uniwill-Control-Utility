using System.Collections.Generic;
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
        StatusText.Text = LocalizationService.Get(state.StatusKey);
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
