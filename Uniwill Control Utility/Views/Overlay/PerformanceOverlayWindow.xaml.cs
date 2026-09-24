using Microsoft.Win32;
using System;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Uniwill_Control_Utility.ViewModels;

namespace Uniwill_Control_Utility.Views.Overlay
{
    public partial class PerformanceOverlayWindow : Window
    {
        private const int ExtendedStyleIndex = -20;
        private const int TransparentStyle = 0x20;
        private const int LayeredStyle = 0x80000;
        private const int ToolWindowStyle = 0x80;
        private const int NoActivateStyle = 0x08000000;

        private readonly PerformanceOverlayViewModel _viewModel;
        private bool _allowClose;
        private bool _displaySettingsSubscribed;
        private bool _sizeUpdatePending;
        private bool _pendingShow;

        // Window-relative positioning: stored as offset from the target window's origin.
        private double _relativeLeft;
        private double _relativeTop;
        private bool _hasRelativePosition;

        // Drag detection: track the target window's last known bounds to spot movement.
        private IntPtr _lastTargetWindow;
        private RECT _lastTargetBounds;
        private bool _userWantsVisible; // true while the user has asked for the overlay to show
        private bool _wasMovingLastTick;  // true if the window moved on the previous tick

        public PerformanceOverlayWindow(PerformanceOverlayViewModel viewModel)
        {
            _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            DataContext = viewModel;
            InitializeComponent();

            Loaded += OnLoaded;
            Closing += OnClosing;
            SizeChanged += OnSizeChanged;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            _viewModel.MetricsUpdated += OnMetricsUpdated;
        }

        public void ClosePermanently()
        {
            _allowClose = true;
            Close();
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            IntPtr handle = new WindowInteropHelper(this).Handle;
            IntPtr style = GetWindowLongPtr(handle, ExtendedStyleIndex);
            _ = SetWindowLongPtr(handle, ExtendedStyleIndex,
                style | (IntPtr)TransparentStyle | (IntPtr)LayeredStyle | (IntPtr)ToolWindowStyle | (IntPtr)NoActivateStyle);

            // Enable input transparency so mouse events pass through to windows below.
            // ACCENT_ENABLE_TRANSPARENTGRADIENT (3) makes the DWM route input to the window behind us.
            SetWindowInputTransparent(handle);

            if (!_displaySettingsSubscribed)
            {
                SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
                _displaySettingsSubscribed = true;
            }

            ApplyViewModelSize();
        }

        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(PerformanceOverlayViewModel.WindowWidth) ||
                e.PropertyName == nameof(PerformanceOverlayViewModel.WindowHeight))
            {
                QueueSizeUpdate();
            }

            if (e.PropertyName == nameof(PerformanceOverlayViewModel.GameActive))
            {
                HandleGameActiveChanged();
            }
        }

        private void HandleGameActiveChanged()
        {
            if (!_viewModel.HideWhenNoGame)
            {
                _userWantsVisible = false;
                return;
            }

            if (_viewModel.GameActive)
            {
                if (!IsVisible)
                {
                    _pendingShow = false;
                    _userWantsVisible = true;
                    PositionRelativeToForegroundWindow();
                    Show();
                }
            }
            else
            {
                if (IsVisible && !_pendingShow)
                {
                    _userWantsVisible = false;
                    Hide();
                }
            }
        }

        private void OnMetricsUpdated(object? sender, EventArgs e)
        {
            if (!_viewModel.HideWhenNoGame || !_viewModel.GameActive)
                return;

            bool moving = IsTargetWindowMoving();

            // Hide immediately when movement is detected.
            if (IsVisible && moving)
            {
                _wasMovingLastTick = true;
                Hide();
                return;
            }

            // Only re-show after the window has been stable for one full tick
            // (moving just stopped on this tick). This prevents the overlay from
            // flickering while the user is still dragging.
            if (!IsVisible && _userWantsVisible)
            {
                if (_wasMovingLastTick && moving)
                {
                    // Still moving — keep hidden.
                    return;
                }

                // Window has settled (was stable this tick, and either was stable
                // last tick or we just transitioned from moving→stable).
                _wasMovingLastTick = false;
                PositionRelativeToForegroundWindow();
                Show();
                return;
            }

            _wasMovingLastTick = moving;
            PositionRelativeToForegroundWindow();
        }

        // Returns true if the target game window's bounds changed since the last check.
        private bool IsTargetWindowMoving()
        {
            IntPtr target = GetForegroundWindow();
            if (target == IntPtr.Zero)
                return false;

            IntPtr ownHandle = new WindowInteropHelper(this).Handle;
            while (target == ownHandle)
            {
                target = GetNextWindow(target, GW_HWNDPREV);
                if (target == IntPtr.Zero)
                    return false;
            }

            // New target window — count as stable (switching apps isn't "dragging").
            if (target != _lastTargetWindow)
            {
                _lastTargetWindow = target;
                if (!GetWindowRect(target, out RECT initialBounds))
                    return false;
                _lastTargetBounds = initialBounds;
                return false;
            }

            if (!GetWindowRect(target, out RECT currentBounds))
                return false;

            bool boundsChanged = !currentBounds.Equals(_lastTargetBounds);
            _lastTargetBounds = currentBounds;
            return boundsChanged;
        }

        public void SetPendingShow(bool value)
        {
            _pendingShow = value;
        }

        public bool HasPendingShow()
        {
            return _pendingShow;
        }

        public void ClearUserWantsVisible()
        {
            _userWantsVisible = false;
        }

        public void SetUserWantsVisible(bool value)
        {
            _userWantsVisible = value;
        }

        private void QueueSizeUpdate()
        {
            if (_sizeUpdatePending)
                return;

            _sizeUpdatePending = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
            {
                _sizeUpdatePending = false;
                ApplyViewModelSize();
            }));
        }

        private void ApplyViewModelSize()
        {
            double width = _viewModel.WindowWidth;
            double height = _viewModel.WindowHeight;

            if (!IsFinitePositive(width) || !IsFinitePositive(height))
                return;

            SetCurrentValue(WidthProperty, width);
            SetCurrentValue(HeightProperty, height);
            InvalidateMeasure();
            InvalidateArrange();
            UpdateLayout();
            ClampToWorkArea();
        }

        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            ClampToWorkArea();
        }

        private void OnDisplaySettingsChanged(object? sender, EventArgs e)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
            {
                ApplyViewModelSize();
                ClampToWorkArea();
            }));
        }

        private void ClampToWorkArea()
        {
            if (!IsLoaded || !IsFinitePositive(ActualWidth) || !IsFinitePositive(ActualHeight))
                return;

            Rect area = SystemParameters.WorkArea;
            double rightLimit = Math.Max(area.Left + 5, area.Right - ActualWidth - 5);
            double bottomLimit = Math.Max(area.Top + 5, area.Bottom - ActualHeight - 5);

            Left = Math.Clamp(IsFinite(Left) ? Left : area.Left + 5, area.Left + 5, rightLimit);
            Top = Math.Clamp(IsFinite(Top) ? Top : area.Top + 5, area.Top + 5, bottomLimit);
        }

        private void OnClosing(object? sender, CancelEventArgs e)
        {
            if (!_allowClose)
            {
                e.Cancel = true;
                Hide();
                _viewModel.StopMonitoring();
                return;
            }

            Loaded -= OnLoaded;
            Closing -= OnClosing;
            SizeChanged -= OnSizeChanged;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.MetricsUpdated -= OnMetricsUpdated;

            if (_displaySettingsSubscribed)
            {
                SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
                _displaySettingsSubscribed = false;
            }

            _viewModel.Dispose();
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static bool IsFinitePositive(double value)
        {
            return IsFinite(value) && value > 0;
        }

        [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        private static partial IntPtr GetWindowLongPtr(IntPtr window, int index);

        [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        private static partial IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr newStyle);

        // SetWindowCompositionAttribute: ACCENT_ENABLE_TRANSPARENTGRADIENT (3)
        // makes the DWM route mouse input through the window to whatever is behind it.
        private const int WcaAccentPolicy = 19;
        private const int AccentTransparentGradient = 3;

        [StructLayout(LayoutKind.Sequential)]
        private struct WindowCompositionAttributeData
        {
            public int Attribute;
            public IntPtr Data;
            public int Size;
        }

      [LibraryImport("user32.dll")]
        private static partial void SetWindowCompositionAttribute(IntPtr window, ref WindowCompositionAttributeData data);

        private static void SetWindowInputTransparent(IntPtr handle)
        {
            int accent = AccentTransparentGradient;
            IntPtr accentPtr = IntPtr.Zero;
            try
            {
                accentPtr = Marshal.AllocHGlobal(Marshal.SizeOf<int>());
                Marshal.WriteInt32(accentPtr, accent);

                var data = new WindowCompositionAttributeData
                {
                    Attribute = WcaAccentPolicy,
                    Data = accentPtr,
                    Size = Marshal.SizeOf<int>()
                };

                SetWindowCompositionAttribute(handle, ref data);
            }
            finally
            {
                Marshal.FreeHGlobal(accentPtr);
            }
        }

        #region Window-relative positioning

        // Positions the overlay relative to the foreground game window when the game
        // is running in a window (not fullscreen). Falls back to monitor-relative
        // positioning when the game is fullscreen or no foreground window is found.
        public void PositionRelativeToForegroundWindow()
        {
            IntPtr target = GetForegroundWindow();
            if (target == IntPtr.Zero)
                return;

            // The overlay is Topmost so it can report itself as foreground.
            // Skip past it to find the actual target window behind us.
            IntPtr ownHandle = new WindowInteropHelper(this).Handle;
            while (target == ownHandle)
            {
                target = GetNextWindow(target, GW_HWNDPREV);
                if (target == IntPtr.Zero)
                    return;
            }

            // Get the target window's outer bounds (for fullscreen detection).
            if (!GetWindowRect(target, out RECT outerRect))
                return;

            bool targetFullscreen = IsZoomed(target);

            if (!targetFullscreen &&
                (outerRect.right - outerRect.left > 0) &&
                (outerRect.bottom - outerRect.top > 0))
            {
                // Windowed mode — map the client-area top-left corner to screen
                // coordinates so the overlay renders below the title bar.
                POINT clientOrigin = new(0, 0);
                ClientToScreen(target, ref clientOrigin);

                if (_hasRelativePosition)
                {
                    Left = clientOrigin.x + _relativeLeft;
                    Top = clientOrigin.y + _relativeTop;
                }
                else
                {
                    Left = clientOrigin.x + 10;
                    Top = clientOrigin.y + 10;
                    _relativeLeft = 10;
                    _relativeTop = 10;
                    _hasRelativePosition = true;
                }
                return;
            }

            // Fullscreen or invalid window — fall back to monitor positioning.
            _hasRelativePosition = false;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int left;
            public int top;
            public int right;
            public int bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int x;
            public int y;

            public POINT(int x, int y)
            {
                this.x = x;
                this.y = y;
            }
        }

        private const uint GW_HWNDPREV = 3;

        [LibraryImport("user32.dll")]
        private static partial IntPtr GetForegroundWindow();

        [LibraryImport("user32.dll")]
        private static partial IntPtr GetNextWindow(IntPtr window, uint cmd);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetWindowRect(IntPtr window, out RECT rect);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool ClientToScreen(IntPtr window, ref POINT point);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool IsZoomed(IntPtr window);

        #endregion
    }
}
