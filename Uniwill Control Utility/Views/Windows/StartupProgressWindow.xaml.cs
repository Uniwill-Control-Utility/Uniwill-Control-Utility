using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using Uniwill_Control_Utility.Services;

namespace Uniwill_Control_Utility.Views.Windows;

public partial class StartupProgressWindow : Window
{
    private const int DwmWindowCornerPreference = 33;
    private const int DwmBorderColor = 34;
    private const int DwmRound = 2;
    private const uint DwmColorNone = 0xFFFFFFFE;

    private readonly Queue<StartupProgressState> _pendingStates = new();
    private Task _playbackTask = Task.CompletedTask;

    public StartupProgressWindow()
    {
        InitializeComponent();
    }

    protected override void OnSourceInitialized(System.EventArgs e)
    {
        base.OnSourceInitialized(e);

        nint windowHandle = new WindowInteropHelper(this).Handle;
        int cornerPreference = DwmRound;
        _ = DwmSetWindowAttribute(
            windowHandle,
            DwmWindowCornerPreference,
            ref cornerPreference,
            Marshal.SizeOf<int>());

        uint borderColor = DwmColorNone;
        _ = DwmSetWindowAttribute(
            windowHandle,
            DwmBorderColor,
            ref borderColor,
            Marshal.SizeOf<uint>());
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

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        nint windowHandle,
        int attribute,
        ref int attributeValue,
        int attributeSize);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        nint windowHandle,
        int attribute,
        ref uint attributeValue,
        int attributeSize);
}
