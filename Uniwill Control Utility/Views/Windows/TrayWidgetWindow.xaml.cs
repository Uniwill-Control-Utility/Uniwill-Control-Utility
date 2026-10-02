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
        private readonly TrayMetricsWindow _metricsWindow;
        private bool _loading;
        private bool _applying;
        private bool _showing;
        private System.Drawing.Point _placementAnchor;
        private string? _placementScreenDeviceName;

        public TrayWidgetWindow(TrayQuickControlService quickControl)
        {
            _quickControl = quickControl;
            _metricsWindow = new TrayMetricsWindow(quickControl);
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
            _showing = true;

            Show();
            UpdateLayout();
            _metricsWindow.Show();
            _metricsWindow.StartRefresh();
            _metricsWindow.UpdateLayout();
            PositionStack();
            Activate();

            try
            {
                TrayQuickControlSnapshot snapshot = await Task.Run(_quickControl.GetSnapshot);
                ApplySnapshot(snapshot);
                UpdateLayout();
                PositionStack();
            }
            catch
            {
                // Silently fail — no status text to show
            }
            finally
            {
                _loading = false;
            }

             // Small delay before enabling deactivate-to-hide, so the window
            // doesn't immediately close on the initial activation/deactivation flicker.
            await Task.Delay(200);
            _showing = false;
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

        /// <summary>Positions the metrics window above and this window below, stacked near the tray.</summary>
        private void PositionStack()
        {
            IntPtr handle = new WindowInteropHelper(this).Handle;
            IntPtr metricsHandle = new WindowInteropHelper(_metricsWindow).Handle;
            if (handle == IntPtr.Zero || !GetWindowRect(handle, out NativeRect controlRect))
                return;

            // Get metrics window dimensions
            System.Drawing.Size metricsSize = GetMetricsSize();
            var controlSize = new System.Drawing.Size(
                controlRect.Right - controlRect.Left,
                controlRect.Bottom - controlRect.Top);

            // Total stack height = metrics + gap + controls
            int stackHeight = metricsSize.Height + 8 + controlSize.Height;
            var stackSize = new System.Drawing.Size(
                Math.Max(controlSize.Width, metricsSize.Width),
                stackHeight);

            Screen screen = Screen.AllScreens.FirstOrDefault(candidate =>
                string.Equals(
                    candidate.DeviceName,
                    _placementScreenDeviceName,
                    StringComparison.OrdinalIgnoreCase)) ?? Screen.FromHandle(handle);

            TrayWidgetPlacementResult placement = TrayWidgetPlacement.Calculate(
                screen.Bounds,
                screen.WorkingArea,
                _placementAnchor,
                stackSize,
                gap: 10,
                taskbarBounds: FindTaskbarBounds(screen.Bounds));

            // Metrics window sits above the controls window
            int metricsX = placement.Location.X;
            int metricsY = placement.Location.Y;
            int controlX = placement.Location.X;
            int controlY = placement.Location.Y + metricsSize.Height + 8;

            SetWindowPos(
                metricsHandle,
                IntPtr.Zero,
                metricsX,
                metricsY,
                0,
                0,
                SetWindowPosFlags.NoSize |
                SetWindowPosFlags.NoZOrder |
                SetWindowPosFlags.NoActivate);

            SetWindowPos(
                handle,
                IntPtr.Zero,
                controlX,
                controlY,
                0,
                0,
                SetWindowPosFlags.NoSize |
                SetWindowPosFlags.NoZOrder |
                SetWindowPosFlags.NoActivate);
        }

        private System.Drawing.Size GetMetricsSize()
        {
            if (_metricsWindow.IsVisible)
            {
                if (GetWindowRect(new WindowInteropHelper(_metricsWindow).Handle, out NativeRect r))
                    return new System.Drawing.Size(r.Right - r.Left, r.Bottom - r.Top);
            }
            // Fallback estimated size
            return new System.Drawing.Size(360, 100);
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
            Dispatcher.BeginInvoke(PositionStack, DispatcherPriority.Loaded);

        private void HideWidget()
        {
            _metricsWindow.HideAndStop();
            Hide();
        }

        private void Window_Deactivated(object sender, EventArgs e)
        {
            if (_showing || _applying)
                return;

            // Defer the check so both windows' Deactivated events have fired and the
            // activated window has been set. Only close if neither window in the stack
            // is the active one (i.e. focus went elsewhere).
            Dispatcher.BeginInvoke(
                (Action)(() =>
                {
                    if (!IsActive && !_metricsWindow.IsActive)
                        HideWidget();
                }),
                DispatcherPriority.Background);
        }

        private void MetricsWindow_Deactivated(object sender, EventArgs e)
        {
            // The metrics window is part of the stacked widget — let the control window
            // own the hide decision. Do nothing here.
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

        private void NavigateContextMenu_Opened(object sender, RoutedEventArgs e)
        {
            // Store the source button's Tag in the context menu so the click handler
            // knows which page to navigate to.
            if (sender is System.Windows.Controls.ContextMenu menu && menu.PlacementTarget is System.Windows.Controls.Primitives.ToggleButton btn)
                menu.Tag = btn.Tag;
        }

        private void GoToUcuMenuItem_Click(object sender, RoutedEventArgs e)
        {
            HideWidget();

            if (sender is not System.Windows.Controls.MenuItem item || item.Parent is not System.Windows.Controls.ContextMenu menu)
                return;
            if (menu.Tag is not string tagName)
                return;

            MainWindow? mainWindow = MainWindow.Instance;
            if (mainWindow is null)
                return;

            mainWindow.Show();
            mainWindow.WindowState = WindowState.Normal;
            mainWindow.Activate();

            // Navigate to the page matching the button's Tag.
            Type? pageType = tagName switch
            {
                "Adaptive" => typeof(Views.Pages.Adaptive),
                "Watercooler" => typeof(Views.Pages.Watercooler),
                "Keyboard" => typeof(Views.Pages.Keyboard),
                "LightBar" => typeof(Views.Pages.LightBar),
                "FanControl" => typeof(Views.Pages.FanControl),
                "SettingsPage" => typeof(Views.Pages.SettingsPage),
                "OverlaySettingsPage" => typeof(Views.Pages.OverlaySettingsPage),
                "FlydigiCooler" => typeof(Views.Pages.FlydigiCooler),
                _ => null
            };

            if (pageType is not null)
                mainWindow.Navigate(pageType);
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
