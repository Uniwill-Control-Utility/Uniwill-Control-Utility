using Microsoft.Win32;
using System;
using System.ComponentModel;
using System.Diagnostics;
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

        // Event-driven foreground detection: uses SetWinEventHook (EVENT_SYSTEM_FOREGROUND)
        // for instant notification when the foreground window changes. Zero polling overhead,
        // instant response. Replaces the old 50ms DispatcherTimer approach.
        private WinEventDelegate? _foregroundHookCallback;
        private IntPtr _foregroundHook;
        private int _targetPid;

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
                style | (IntPtr)ToolWindowStyle | (IntPtr)NoActivateStyle);

            // Enable input transparency so mouse events pass through to windows below.
            // ACCENT_ENABLE_TRANSPARENTGRADIENT (3) makes the DWM route input to the
            // window behind us. This is a Win32 API — it does NOT force CPU rendering
            // like AllowsTransparency=True does.
            SetWindowInputTransparent(handle);

            if (!_displaySettingsSubscribed)
            {
                SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
                _displaySettingsSubscribed = true;
            }

            // Event-driven foreground detection: SetWinEventHook notifies us instantly
            // when the foreground window changes. This eliminates the 250-750ms latency
            // of relying on FPS service ticks and avoids polling overhead.
            _foregroundHookCallback = OnForegroundChanged;
            _foregroundHook = SetWinEventHook(WinEvent.SystemForeground, WinEvent.SystemForeground,
                IntPtr.Zero, _foregroundHookCallback, 0, 0, WinEvent.SlewExit);

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

            // Also track PID changes so the foreground hook checks the right process
            // even if GameActive stays true (e.g., switching between two games).
            if (e.PropertyName == nameof(PerformanceOverlayViewModel.GameProcessId))
            {
                _targetPid = _viewModel.GameProcessId;
            }
        }

        private void HandleGameActiveChanged()
        {
            if (_viewModel.GameActive)
            {
                if (!IsVisible)
                {
                    _pendingShow = false;
                    _userWantsVisible = true;
                    _targetPid = _viewModel.GameProcessId;
                    PositionRelativeToForegroundWindow();
                    Show();
                }
            }
            else
            {
                if (IsVisible && !_pendingShow)
                {
                    _userWantsVisible = false;
                    _targetPid = 0;
                    Hide();
                }
            }
        }

        private void OnMetricsUpdated(object? sender, EventArgs e)
        {
            if (!_viewModel.GameActive)
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

            // Unregister the WinEvent hook if it was installed.
                if (_foregroundHook != IntPtr.Zero)
                {
                    UnhookWinEvent(_foregroundHook);
                    _foregroundHook = IntPtr.Zero;
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
        // This is a Win32 API that works on any HWND — it does NOT require
        // AllowsTransparency=True (which forces CPU bitmap rendering).
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
            IntPtr accentPtr = Marshal.AllocHGlobal(Marshal.SizeOf<int>());
            try
            {
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

        // Positions the overlay relative to the foreground game window (windowed)
        // or falls back to monitor positioning (fullscreen).
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

            // Determine which monitor the game is on so the overlay stays
            // on the same display. This avoids DWM cross-monitor composition
            // stutter on extended dual-monitor setups.
            IntPtr gameMonitor = MonitorFromWindow(target, MonitorDefaultToNearest);
            MONITORINFO gameMonitorInfo = new() { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (gameMonitor != IntPtr.Zero && GetMonitorInfo(gameMonitor, ref gameMonitorInfo))
            {
                RECT work = gameMonitorInfo.rcWork;
                double gameLeft = work.left;
                double gameTop = work.top;
                double gameRight = work.right;
                double gameBottom = work.bottom;

                if (_hasRelativePosition)
                {
                    // Clamp stored relative position to the game's monitor work area.
                    double rightLimit = Math.Max(gameLeft + 5, gameRight - ActualWidth - 5);
                    double bottomLimit = Math.Max(gameTop + 5, gameBottom - ActualHeight - 5);

                    Left = Math.Clamp(Left, gameLeft + 5, rightLimit);
                    Top = Math.Clamp(Top, gameTop + 5, bottomLimit);
                }
                else
                {
                    Left = gameLeft + 10;
                    Top = gameTop + 10;
                }
            }

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

            // Fullscreen or invalid window — fall back to monitor positioning (already set above).
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

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        private const uint MonitorDefaultToNearest = 0x2;
        private const int MonitorInfoFPrimary = 0x1;

        [LibraryImport("user32.dll")]
        private static partial IntPtr MonitorFromWindow(IntPtr window, uint dwFlags);

        [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

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

        [LibraryImport("user32.dll")]
        private static partial uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        /// <summary>
        /// Returns true if the foreground window belongs to the given process ID.
        /// Also returns true if the foreground is the alt-tab overlay (TaskSwitcherWnd),
        /// in which case the caller should hide the overlay.
        /// </summary>
        private static bool IsForegroundProcess(int pid)
        {
            IntPtr window = GetForegroundWindow();
            if (window == IntPtr.Zero)
                return false;

            // If the foreground is the alt-tab overlay, treat as "not the game"
            // so the overlay hides immediately.
            string className = GetWindowClassName(window);
            if (className.Equals("TaskSwitcherWnd", StringComparison.OrdinalIgnoreCase) ||
                className.Equals("TaskSwitcherMPHost", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            GetWindowThreadProcessId(window, out uint foregroundPid);
            int foregroundPidInt = unchecked((int)foregroundPid);

            // The foreground might be a child/owned window of the game.
            // Walk up the owner chain to find a match.
            IntPtr current = window;
            while (current != IntPtr.Zero)
            {
                GetWindowThreadProcessId(current, out uint pidCheck);
                if (unchecked((int)pidCheck) == pid)
                    return true;

                // Walk to owner window.
                current = GetWindow(current, GW_OWNER);
            }

            return false;
        }

        private static string GetWindowClassName(IntPtr hWnd)
        {
            char[] buffer = new char[256];
            int len = GetClassName(hWnd, buffer, buffer.Length);
            return len > 0 ? new string(buffer, 0, len).TrimEnd('\0') : string.Empty;
        }

        [LibraryImport("user32.dll")]
        private static partial IntPtr GetWindow(IntPtr hWnd, uint uCmd);

        [LibraryImport("user32.dll", SetLastError = true, EntryPoint = "GetClassNameW", StringMarshalling = StringMarshalling.Utf16)]
        private static partial int GetClassName(IntPtr hWnd, char[]? lpClassName, int nMaxCount);

        private const uint GW_OWNER = 4;

        #region WinEvent hook for foreground detection

        private static class WinEvent
        {
            public const uint SystemForeground = 3;
            public const uint SlewExit = 0x0004;
        }

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

        [LibraryImport("user32.dll")]
        private static partial IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
            WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool UnhookWinEvent(IntPtr hWinEventHook);

        private void OnForegroundChanged(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
        {
            // The hook callback runs on a background thread — marshal to UI thread.
            Dispatcher.Invoke(() =>
            {
                if (IsVisible && _userWantsVisible && _targetPid > 0 && !IsForegroundProcess(_targetPid))
                {
                    _userWantsVisible = false;
                    Hide();
                }
            });
        }

        #endregion

        #endregion
    }
}
