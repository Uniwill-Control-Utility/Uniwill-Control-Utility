using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Uniwill_Control_Utility.Services;
using Uniwill_Control_Utility.Views.Windows.Tray;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Wpf.Ui.Animations;
using Serilog;
using Screen = System.Windows.Forms.Screen;

namespace Uniwill_Control_Utility.Views.Windows
{
    public partial class TrayWidgetWindow : ITrayWidget
    {
        // The tray pages (quick settings + display) are instantiated by the
        // NavigationView via reflection; they reach this window through this
        // static for the shared apply/snapshot orchestration.
        public static TrayWidgetWindow? Instance;

        // Duration of the custom tray page transitions (directional fade+slide for the
        // QuickSettings↔Display hops, fade+slide-up for the tray's own show).
        private const int TrayTransitionMs = 300;

        private readonly TrayQuickControlService _quickControl;
        private readonly DispatcherTimer _refreshTimer;
        private bool _loading;
        private bool _applying;
        private bool _showing;
        // The show path produces one spurious Deactivated right after activation; swallow
        // exactly that one so a later real focus change can still hide the tray.
        private bool _ignoreNextDeactivation;
        private System.Drawing.Point _placementAnchor;
        private string? _placementScreenDeviceName;

        public TrayWidgetWindow(TrayQuickControlService quickControl)
        {
            _quickControl = quickControl;
            Instance = this;
            InitializeComponent();
            SystemThemeWatcher.Watch(this, Wpf.Ui.Controls.WindowBackdropType.Mica, true);

            _refreshTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2)
            };
            _refreshTimer.Tick += (s, e) => UpdateMetrics();
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
            _ignoreNextDeactivation = true;

            Show();
            UpdateLayout();
            PositionNearTray();
            Activate();
            _refreshTimer.Start();

            // A re-shown tray always opens on the main QuickSettings page — with the
            // library-style fade + slide-up (the NavigationView's own transition is
            // disabled in XAML, so we drive it here; the directional fade+slide is
            // only for the QuickSettings↔Display page hops).
            _ = TrayNavigation.Navigate(typeof(TrayQuickSettingsPage));
            _ = TransitionAnimationProvider.ApplyTransition(
                TrayQuickSettingsPage.Instance, Transition.FadeInWithSlide, TrayTransitionMs);

            try
            {
                TrayQuickControlSnapshot snapshot = await Task.Run(_quickControl.GetSnapshot);
                ApplySnapshot(snapshot);
                UpdateMetrics();
                UpdateLayout();
                PositionNearTray();
            }
            catch (Exception ex)
            {
                // Silently fail — no status text to show, but keep the cause visible
                // in the logs (a failed startup snapshot leaves the Display page's
                // Collapsed-by-default sections un-applied = an empty page).
                Log.Warning("[Tray] Startup snapshot failed: {Message}", ex.Message);
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

        /// <summary>
        /// Orchestration only: each page applies its own half of the snapshot.
        /// A throw in either half leaves that page's Collapsed-by-default sections
        /// un-applied, so the page renders empty — the catches in the callers log
        /// the cause.
        /// </summary>
        private void ApplySnapshot(TrayQuickControlSnapshot snapshot)
        {
            TrayQuickSettingsPage.Instance?.ApplySnapshot(snapshot);
            TrayDisplayPage.Instance?.ApplySnapshot(snapshot);
        }

        /// <summary>
        /// Navigates the tray's NavigationView to a page class. The NavigationView
        /// instantiates the page by reflection (parameterless constructor) and
        /// animates the transition; navigating to the already-shown page is a no-op.
        /// </summary>
        /// <summary>
        /// Navigates the tray's NavigationView to a page class and animates the shown
        /// page with a custom fade+slide for the QuickSettings↔Display page hops:
        /// entering the Display page slides in from the right, returning to the
        /// quick-settings page slides in from the left — both layered with a fade
        /// (the NavigationView's own transition is disabled in XAML, and its
        /// TemplateBinding is one-time, so the library provider is applied here
        /// instead). Navigate() is synchronous, so the page instance (and its static)
        /// exists when it returns.
        /// </summary>
        public bool NavigateTo(Type pageType)
        {
            var toDisplay = pageType == typeof(TrayDisplayPage);
            var navigated = TrayNavigation.Navigate(pageType);

            Page? page = null;
            if (toDisplay)
                page = TrayDisplayPage.Instance;
            else
                page = TrayQuickSettingsPage.Instance;
            if (page is not null)
            {
                var slide = toDisplay ? Transition.SlideRight : Transition.SlideLeft;
                _ = TransitionAnimationProvider.ApplyTransition(page, slide, TrayTransitionMs);
                _ = TransitionAnimationProvider.ApplyTransition(page, Transition.FadeIn, TrayTransitionMs);
            }

            return navigated;
        }

        /// <summary>
        /// Re-fetches the live snapshot and re-applies it to both pages. Used after
        /// navigation, where the destination page is a fresh instance whose elements
        /// still carry default state.
        /// </summary>
        public void RefreshSnapshot() => _ = ApplyAsync(async () => { });

        public bool IsBusy => _loading || _applying;

        public TrayQuickControlService QuickControl => _quickControl;

        public async Task ApplyAsync(Func<Task> action)
        {
            _applying = true;
            try
            {
                await action();

                // Re-fetch snapshot to update button enabled states after any change.
                try
                {
                    var snapshot = await Task.Run(_quickControl.GetSnapshot);
                    ApplySnapshot(snapshot);
                }
                catch (Exception ex)
                {
                    // Ignore — snapshot refresh is best-effort, but surface the cause:
                    // a throw here leaves the Display page's Collapsed-by-default
                    // sections un-applied (an empty page).
                    Log.Warning("[Tray] Snapshot refresh failed: {Message}", ex.Message);
                }
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

        private void UpdateMetrics()
        {
            try
            {
                var snapshot = _quickControl.GetTelemetrySnapshot();
                ApplyMetrics(snapshot);
            }
            catch
            {
                // Silently fail — metrics are best-effort
            }
        }

        private void ApplyMetrics(TrayQuickTelemetrySnapshot s)
        {
            // CPU hero values
            CpuTempText.Text = $"{s.CpuTemperature}°C";
            CpuUsageText.Text = $"{s.CpuUsage}%";

            // CPU segmented bars
            CpuPowerBar.Value = s.CpuPowerWatts;
            CpuPowerText.Text = $"{s.CpuPowerWatts} W";
            CpuClockBar.Value = s.CpuClockMhz;
            CpuClockText.Text = $"{s.CpuClockMhz / 1000.0:F1} GHz";
            CpuFanBar.Value = s.CpuFanPercent;
            CpuFanText.Text = $"{s.CpuFanPercent}%";

            // GPU hero values
            GpuTempText.Text = $"{s.GpuTemperature}°C";
            GpuUsageText.Text = $"{(int)s.GpuUsage}%";

            // GPU segmented bars
            GpuPowerBar.Value = s.GpuPowerWatts;
            GpuPowerText.Text = $"{s.GpuPowerWatts} W";
            GpuClockBar.Value = s.GpuClockMhz;
            GpuClockText.Text = $"{s.GpuClockMhz / 1000.0:F1} GHz";
            GpuFanBar.Value = s.GpuFanPercent;
            GpuFanText.Text = $"{s.GpuFanPercent}%";
        }

        private void PositionNearTray()
        {
            IntPtr handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero || !GetWindowRect(handle, out NativeRect windowRect))
                return;

            var windowSize = new System.Drawing.Size(
                windowRect.Right - windowRect.Left,
                windowRect.Bottom - windowRect.Top);

            Screen screen = Screen.AllScreens.FirstOrDefault(candidate =>
                string.Equals(
                    candidate.DeviceName,
                    _placementScreenDeviceName,
                    StringComparison.OrdinalIgnoreCase)) ?? Screen.FromHandle(handle);

            var tbBounds = FindTaskbarBounds(screen.Bounds);
            System.Drawing.Point location = TrayWidgetPlacement.CalculateCorner(
                screen.Bounds,
                screen.WorkingArea,
                _placementAnchor,
                windowSize,
                gap: 10,
                taskbarBounds: tbBounds);

            SetWindowPos(
                handle,
                IntPtr.Zero,
                location.X,
                location.Y,
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
            // Prefer FindWindow to get the actual taskbar HWND and read its real
            // bounds via GetWindowRect. SHAppBarMessage(GetAutoHideBarEx) returns
            // monitor-sized bounds for non-auto-hide taskbars, which corrupts
            // the placement calculation.
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

        private void HideWidget()
        {
            _refreshTimer.Stop();
            Hide();
        }

        private void Window_Deactivated(object sender, EventArgs e)
        {
            if (_ignoreNextDeactivation)
            {
                _ignoreNextDeactivation = false;
                return;
            }

            if (_showing || _applying)
                return;

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

        /// <summary>
        /// Shared by both tray pages' context menus: hides the tray, opens UCU and
        /// navigates to the page matching the source button's Tag.
        /// </summary>
        public void GoToUcuMenuItem_Click(object sender, RoutedEventArgs e)
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
                "HydroUI" => typeof(Views.Pages.HydroUI),
                "Keyboard" => typeof(Views.Pages.Keyboard),
                "LightBar" => typeof(Views.Pages.LightBar),
                "FanControl" => typeof(Views.Pages.FanControl),
                "SettingsPage" => typeof(Views.Pages.SettingsPage),
                "OverlaySettingsPage" => typeof(Views.Pages.OverlaySettingsPage),
                "Display" => typeof(Views.Pages.Display),
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
