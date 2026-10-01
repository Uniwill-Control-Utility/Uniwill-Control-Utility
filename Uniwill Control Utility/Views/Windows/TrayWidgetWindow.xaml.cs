using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Uniwill_Control_Utility.Services;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Screen = System.Windows.Forms.Screen;

namespace Uniwill_Control_Utility.Views.Windows
{
    public partial class TrayWidgetWindow : ITrayWidget
    {
        private readonly TrayQuickControlService _quickControl;
        private bool _loading;
        private bool _applying;
        private System.Drawing.Point _placementAnchor;
        private string? _placementScreenDeviceName;

        public TrayWidgetWindow(TrayQuickControlService quickControl)
        {
            _quickControl = quickControl;
            InitializeComponent();
            SystemThemeWatcher.Watch(this, Wpf.Ui.Controls.WindowBackdropType.Mica, true);
        }

        public async Task ToggleAsync()
        {
            if (IsVisible)
            {
                HideWidget();
                return;
            }

            CapturePlacementAnchor();
            _loading = true;
            Show();
            UpdateLayout();
            PositionNearTray();
            Activate();

            try
            {
                TrayQuickControlSnapshot snapshot = await Task.Run(_quickControl.GetSnapshot);
                ApplySnapshot(snapshot);
                UpdateLayout();
                PositionNearTray();
            }
            catch
            {
                // Silently fail — no status text to show
            }
            finally
            {
                _loading = false;
            }
        }

        private void ApplySnapshot(TrayQuickControlSnapshot snapshot)
        {
            var policy = snapshot.Policy;

            ApplyToggle(SlotAdaptiveMode, BtnAdaptiveMode, policy.ShowAdaptiveMode, snapshot.AdaptiveModeRunning);
            ApplyToggle(SlotHydroSmartMode, BtnHydroSmartMode, policy.ShowHydroSmartMode, snapshot.HydroSmartMode);
            ApplyToggle(SlotHydroRgb, BtnHydroRgb, policy.ShowHydroRgb, snapshot.HydroRgbOn);
            ApplyToggle(SlotKeyboardBacklight, BtnKeyboardBacklight, policy.ShowKeyboardBacklight, snapshot.KeyboardRgbOn);
            ApplyToggle(SlotLightBar, BtnLightBar, policy.ShowLightBar, snapshot.LightBarOn);
            ApplyToggle(SlotFanSmartMode, BtnFanSmartMode, policy.ShowFanSmartMode, snapshot.FanSmartMode);
            ApplyToggle(SlotLocalDimming, BtnLocalDimming, policy.ShowLocalDimming, snapshot.LocalDimmingOn);
            ApplyToggle(SlotOverlay, BtnOverlay, policy.ShowOverlay, snapshot.OverlayOn);
            ApplyToggle(SlotFlydigiRgb, BtnFlydigiRgb, policy.ShowFlydigiRgb, snapshot.FlydigiRgbOn);
        }

        private static void ApplyToggle(
            System.Windows.Controls.Panel panel,
            System.Windows.Controls.Primitives.ToggleButton button,
            bool visible,
            bool isChecked)
        {
            panel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            button.IsChecked = isChecked;
        }

        #region Toggle Handlers

        private async void BtnAdaptiveMode_Click(object sender, RoutedEventArgs e)
        {
            if (_loading || _applying || sender is not System.Windows.Controls.Primitives.ToggleButton btn)
                return;

            bool enabled = btn.IsChecked == true;
            await ApplyAsync(async () =>
            {
                _quickControl.SetAdaptiveMode(enabled);
                await Task.CompletedTask;
            });
        }

        private async void BtnHydroSmartMode_Click(object sender, RoutedEventArgs e)
        {
            if (_loading || _applying || sender is not System.Windows.Controls.Primitives.ToggleButton btn)
                return;

            bool enabled = btn.IsChecked == true;
            await ApplyAsync(async () =>
            {
                await _quickControl.SetHydroSmartModeAsync(enabled);
            });
        }

        private async void BtnHydroRgb_Click(object sender, RoutedEventArgs e)
        {
            if (_loading || _applying || sender is not System.Windows.Controls.Primitives.ToggleButton btn)
                return;

            bool enabled = btn.IsChecked == true;
            await ApplyAsync(async () =>
            {
                await _quickControl.SetHydroRgbAsync(enabled);
            });
        }

        private async void BtnKeyboardBacklight_Click(object sender, RoutedEventArgs e)
        {
            if (_loading || _applying || sender is not System.Windows.Controls.Primitives.ToggleButton btn)
                return;

            bool enabled = btn.IsChecked == true;
            await ApplyAsync(async () =>
            {
                _quickControl.SetKeyboardBacklight(enabled);
                await Task.CompletedTask;
            });
        }

        private async void BtnLightBar_Click(object sender, RoutedEventArgs e)
        {
            if (_loading || _applying || sender is not System.Windows.Controls.Primitives.ToggleButton btn)
                return;

            bool enabled = btn.IsChecked == true;
            await ApplyAsync(async () =>
            {
                _quickControl.SetLightBar(enabled);
                await Task.CompletedTask;
            });
        }

        private async void BtnFanSmartMode_Click(object sender, RoutedEventArgs e)
        {
            if (_loading || _applying || sender is not System.Windows.Controls.Primitives.ToggleButton btn)
                return;

            bool enabled = btn.IsChecked == true;
            await ApplyAsync(async () =>
            {
                _quickControl.SetFanSmartMode(enabled);
                await Task.CompletedTask;
            });
        }

        private async void BtnLocalDimming_Click(object sender, RoutedEventArgs e)
        {
            if (_loading || _applying || sender is not System.Windows.Controls.Primitives.ToggleButton btn)
                return;

            bool enabled = btn.IsChecked == true;
            await ApplyAsync(async () =>
            {
                bool success = await _quickControl.SetLocalDimmingAsync(enabled);
                if (!success)
                {
                    // Revert toggle on failure
                    Application.Current.Dispatcher.Invoke(() => btn.IsChecked = !enabled);
                }
            });
        }

        private async void BtnOverlay_Click(object sender, RoutedEventArgs e)
        {
            if (_loading || _applying || sender is not System.Windows.Controls.Primitives.ToggleButton btn)
                return;

            bool enabled = btn.IsChecked == true;
            await ApplyAsync(async () =>
            {
                _quickControl.SetOverlay(enabled);
                await Task.CompletedTask;
            });
        }

        private async void BtnFlydigiRgb_Click(object sender, RoutedEventArgs e)
        {
            if (_loading || _applying || sender is not System.Windows.Controls.Primitives.ToggleButton btn)
                return;

            bool enabled = btn.IsChecked == true;
            await ApplyAsync(async () =>
            {
                await _quickControl.SetFlydigiRgbAsync(enabled);
            });
        }

        #endregion

        private async Task ApplyAsync(Func<Task> action)
        {
            _applying = true;
            try
            {
                await action();
            }
            catch
            {
                // Silently fail
            }
            finally
            {
                _applying = false;
            }
        }

        private void PositionNearTray()
        {
            IntPtr handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero || !GetWindowRect(handle, out NativeRect windowRect))
                return;

            Screen screen = Screen.AllScreens.FirstOrDefault(candidate =>
                string.Equals(
                    candidate.DeviceName,
                    _placementScreenDeviceName,
                    StringComparison.OrdinalIgnoreCase)) ?? Screen.FromHandle(handle);
            var windowSize = new System.Drawing.Size(
                windowRect.Right - windowRect.Left,
                windowRect.Bottom - windowRect.Top);
            TrayWidgetPlacementResult placement = TrayWidgetPlacement.Calculate(
                screen.Bounds,
                screen.WorkingArea,
                _placementAnchor,
                windowSize,
                gap: 10,
                taskbarBounds: FindTaskbarBounds(screen.Bounds));

            SetWindowPos(
                handle,
                IntPtr.Zero,
                placement.Location.X,
                placement.Location.Y,
                0,
                0,
                SetWindowPosFlags.NoSize |
                SetWindowPosFlags.NoZOrder |
                SetWindowPosFlags.NoActivate);
        }

        private void CapturePlacementAnchor()
        {
            _placementAnchor = System.Windows.Forms.Cursor.Position;
            _placementScreenDeviceName = Screen.FromPoint(_placementAnchor).DeviceName;
        }

        private static System.Drawing.Rectangle? FindTaskbarBounds(
            System.Drawing.Rectangle monitorBounds)
        {
            foreach (TrayEdge edge in Enum.GetValues<TrayEdge>())
            {
                var appBarData = new AppBarData
                {
                    Size = (uint)Marshal.SizeOf<AppBarData>(),
                    Edge = (uint)edge,
                    Bounds = new NativeRect
                    {
                        Left = monitorBounds.Left,
                        Top = monitorBounds.Top,
                        Right = monitorBounds.Right,
                        Bottom = monitorBounds.Bottom
                    }
                };
                IntPtr autoHideTaskbar = SHAppBarMessage(GetAutoHideBarEx, ref appBarData);
                if (TryGetTaskbarBounds(
                        autoHideTaskbar,
                        monitorBounds,
                        edge,
                        requireIntersection: false,
                        out System.Drawing.Rectangle autoHideBounds))
                {
                    return autoHideBounds;
                }
            }

            IntPtr primaryTaskbar = FindWindow("Shell_TrayWnd", null);
            if (TryGetTaskbarBounds(
                    primaryTaskbar,
                    monitorBounds,
                    edge: null,
                    requireIntersection: true,
                    out System.Drawing.Rectangle bounds))
                return bounds;

            IntPtr secondaryTaskbar = IntPtr.Zero;
            while ((secondaryTaskbar = FindWindowEx(
                       IntPtr.Zero,
                       secondaryTaskbar,
                       "Shell_SecondaryTrayWnd",
                       null)) != IntPtr.Zero)
            {
                if (TryGetTaskbarBounds(
                        secondaryTaskbar,
                        monitorBounds,
                        edge: null,
                        requireIntersection: true,
                        out bounds))
                    return bounds;
            }

            return null;
        }

        private static bool TryGetTaskbarBounds(
            IntPtr taskbar,
            System.Drawing.Rectangle monitorBounds,
            TrayEdge? edge,
            bool requireIntersection,
            out System.Drawing.Rectangle bounds)
        {
            bounds = default;
            if (taskbar == IntPtr.Zero || !GetWindowRect(taskbar, out NativeRect rect))
                return false;

            bounds = System.Drawing.Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
            System.Drawing.Rectangle intersection = System.Drawing.Rectangle.Intersect(bounds, monitorBounds);
            if (requireIntersection && (intersection.Width <= 0 || intersection.Height <= 0))
                return false;

            bounds = edge is TrayEdge knownEdge
                ? TrayWidgetPlacement.NormalizeTaskbarBounds(monitorBounds, bounds, knownEdge)
                : TrayWidgetPlacement.NormalizeTaskbarBounds(monitorBounds, bounds);
            return true;
        }

        private void Window_DpiChanged(object sender, DpiChangedEventArgs e) =>
            Dispatcher.BeginInvoke(PositionNearTray, DispatcherPriority.Loaded);

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Intentionally empty — window is position-only near the tray.
        }

        private void HideWidget() => Hide();

        private void Window_Deactivated(object sender, EventArgs e)
        {
            if (!_applying)
                HideWidget();
        }

        private void Window_Closed(object? sender, EventArgs e) { }

        private void OpenUcuButton_Click(object sender, RoutedEventArgs e)
        {
            HideWidget();
            MainWindow? mainWindow = MainWindow.Instance;
            if (mainWindow is null)
                return;

            mainWindow.Show();
            mainWindow.WindowState = WindowState.Normal;
            mainWindow.Activate();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            internal int Left;
            internal int Top;
            internal int Right;
            internal int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct AppBarData
        {
            internal uint Size;
            internal IntPtr Window;
            internal uint CallbackMessage;
            internal uint Edge;
            internal NativeRect Bounds;
            internal IntPtr Parameter;
        }

        private const uint GetAutoHideBarEx = 0x0000000B;

        [Flags]
        private enum SetWindowPosFlags : uint
        {
            NoSize = 0x0001,
            NoZOrder = 0x0004,
            NoActivate = 0x0010
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string className, string? windowName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowEx(
            IntPtr parent,
            IntPtr childAfter,
            string className,
            string? windowName);

        [DllImport("shell32.dll")]
        private static extern IntPtr SHAppBarMessage(uint message, ref AppBarData data);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(
            IntPtr window,
            IntPtr insertAfter,
            int x,
            int y,
            int width,
            int height,
            SetWindowPosFlags flags);
    }
}
