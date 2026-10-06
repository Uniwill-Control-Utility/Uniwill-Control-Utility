using Gma.System.MouseKeyHook;
using Microsoft.Extensions.Hosting;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using System.Windows.Forms;
using Uniwill_Control_Utility.Models;
using Uniwill_Control_Utility.Services.Performance;

namespace Uniwill_Control_Utility.Services
{
    public sealed partial class OverlayManagerService :
        IHostedService,
        IDisposable
    {
        private readonly OverlaySettingsStore _settingsStore;
        private readonly IHardwareMonitoringService _hardware;
        private readonly Performance.IFpsMonitoringService _fps;

        private readonly object _settingsSaveLock = new();
        private readonly object _hookLock = new();

        private IKeyboardMouseEvents? _hook;

        private DateTime _lastHotkeyUtc = DateTime.MinValue;
        private bool _disposed;

        // RTSS OSD injection: timer-based update loop that writes metrics
        // directly into RTSS shared memory for in-game rendering.
        private System.Timers.Timer? _rtssOsdTimer;
        private IDisposable? _rtssOsdHardwareLease;
        private bool _rtssOsdWasShowing; // tracks if OSD was last showing for focus-loss detection

        public OverlayOptions CurrentOptions =>
            _settingsStore.Current;

        public OverlayManagerService(
            OverlaySettingsStore settingsStore,
            IHardwareMonitoringService hardware,
            Performance.IFpsMonitoringService fps)
        {
            _settingsStore =
                settingsStore ??
                throw new ArgumentNullException(
                    nameof(settingsStore));

            _hardware =
                hardware ??
                throw new ArgumentNullException(
                    nameof(hardware));

            _fps =
                fps ??
                throw new ArgumentNullException(
                    nameof(fps));
        }

        public Task StartAsync(
            CancellationToken cancellationToken)
        {
            ThrowIfDisposed();

            cancellationToken.ThrowIfCancellationRequested();

            OverlayOptions options = _settingsStore.Current;

            if (options.Enabled)
            {
                /*
                 * MouseKeyHook installs WH_KEYBOARD_LL / WH_MOUSE_LL hooks via
                 * SetWindowsHookEx.  Although low-level hooks don't require a
                 * message pump on the calling thread, the hook is scoped to the
                 * desktop of the thread that calls SetWindowsHookEx.  When
                 * IHostedService.StartAsync fires on a thread-pool thread the
                 * hook may end up on a different desktop or fail silently, so
                 * keyboard events never reach the callback.  Installing on the
                 * UI thread (which is guaranteed to be on WinSta0\Default)
                 * avoids this problem.
                 */
                RunOnUiThread(EnsureHook);

                // Start RTSS OSD timer.
                StartRtssOsd(options);
            }

            if (options.Enabled && options.ShowOnStartup)
                ShowOverlay();

            return Task.CompletedTask;
        }

        public Task StopAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            StopRtssOsd();

            RunOnUiThread(RemoveHook);

            return Task.CompletedTask;
        }

        public void ApplyOptions(OverlayOptions options)
        {
            ThrowIfDisposed();

            OverlayOptions normalisedOptions =
                NormaliseForStorage(options);

            SaveOptionsIfChanged(normalisedOptions);

            RunOnUiThread(() =>
            {
                if (normalisedOptions.Enabled)
                    EnsureHook();
                else
                    RemoveHook();

                // Manage RTSS OSD timer based on enabled state.
                if (normalisedOptions.Enabled)
                    StartRtssOsd(normalisedOptions);
                else
                    StopRtssOsd();
            });
        }

        /// <summary>
        /// Show the RTSS in-game OSD. Starts the shared-memory timer if not
        /// already running. RTSS handles all rendering inside the game.
        /// </summary>
        public void ShowOverlay()
        {
            ThrowIfDisposed();

            OverlayOptions options = _settingsStore.Current;

            if (!options.Enabled)
                return;

            StartRtssOsd(options);
        }

        /// <summary>
        /// Hide the RTSS in-game OSD. Releases the OSD slot so RTSS stops
        /// rendering UCU metrics.
        /// </summary>
        public void HideOverlay()
        {
            if (_disposed)
                return;

            StopRtssOsd();
        }

        /// <summary>
        /// Toggle the RTSS in-game OSD on/off.
        /// </summary>
        public void ToggleOverlay()
        {
            ThrowIfDisposed();

            if (_rtssOsdTimer is not null)
                HideOverlay();
            else
                ShowOverlay();
        }

        /// <summary>
        /// Alias for ToggleOverlay — kept for hotkey compatibility.
        /// </summary>
        public void ToggleContents()
        {
            ToggleOverlay();
        }

        public static bool IsValidHotkey(string? text)
        {
            return Hotkey.TryParse(text, out _);
        }

        private void EnsureHook()
        {
            ThrowIfDisposed();

            lock (_hookLock)
            {
                if (_hook is not null)
                    return;

                _hook = Hook.GlobalEvents();
                _hook.KeyDown += OnGlobalKeyDown;
            }
        }

        private void RemoveHook()
        {
            lock (_hookLock)
            {
                if (_hook is null)
                    return;

                _hook.KeyDown -= OnGlobalKeyDown;
                _hook.Dispose();
                _hook = null;
            }
        }

        private void OnGlobalKeyDown(
            object? sender,
            KeyEventArgs e)
        {
            if (_disposed)
                return;

            DateTime now = DateTime.UtcNow;

            if (now - _lastHotkeyUtc <
                TimeSpan.FromMilliseconds(350))
            {
                return;
            }

            OverlayOptions options =
                _settingsStore.Current;

            if (Hotkey.TryParse(
                    options.ToggleOverlayHotkey,
                    out Hotkey toggleOverlay) &&
                toggleOverlay.Matches(e))
            {
                _lastHotkeyUtc = now;
                ToggleOverlay();
                return;
            }

            if (Hotkey.TryParse(
                    options.ToggleContentsHotkey,
                    out Hotkey toggleContents) &&
                toggleContents.Matches(e))
            {
                _lastHotkeyUtc = now;
                ToggleContents();
            }
        }

        private OverlayOptions NormaliseForStorage(
            OverlayOptions options)
        {
            int refreshInterval =
                options.RefreshIntervalMs > 0
                    ? options.RefreshIntervalMs
                    : 250;

            return options with
            {
                RefreshIntervalMs = refreshInterval,

                ToggleOverlayHotkey =
                    options.ToggleOverlayHotkey?
                        .Trim() ??
                    string.Empty,

                ToggleContentsHotkey =
                    options.ToggleContentsHotkey?
                        .Trim() ??
                    string.Empty
            };
        }

        private void SaveOptionsIfChanged(
            OverlayOptions options)
        {
            lock (_settingsSaveLock)
            {
                OverlayOptions current =
                    _settingsStore.Current;

                if (Equals(current, options))
                    return;

                _settingsStore.Save(options);
            }
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) &&
                   !double.IsInfinity(value);
        }

        private static void RunOnUiThread(
            Action action)
        {
            System.Windows.Application? application =
                System.Windows.Application.Current;

            if (application?.Dispatcher is null ||
                application.Dispatcher.CheckAccess())
            {
                action();
                return;
            }

            application.Dispatcher.Invoke(action);
        }

        private static T RunOnUiThread<T>(
            Func<T> action)
        {
            System.Windows.Application? application =
                System.Windows.Application.Current;

            if (application?.Dispatcher is null ||
                application.Dispatcher.CheckAccess())
            {
                return action();
            }

            return application.Dispatcher.Invoke(action);
        }

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(_disposed, nameof(OverlayManagerService));
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            StopRtssOsd();

            RunOnUiThread(RemoveHook);

            GC.SuppressFinalize(this);
        }

        #region RTSS OSD

        private void StartRtssOsd(OverlayOptions options)
        {
            if (_rtssOsdTimer is not null)
            {
                // Already running — just update the interval if it changed.
                if (_rtssOsdTimer.Interval != options.RefreshIntervalMs)
                    _rtssOsdTimer.Interval = options.RefreshIntervalMs;
                return;
            }

            // Acquire hardware monitoring categories needed.
            // Check if any per-metric toggle within each category is enabled.
            HardwareMonitoringCategory categories = HardwareMonitoringCategory.None;

            bool anyCpu = options.ShowCpuTemperature || options.ShowCpuUsage ||
                          options.ShowCpuPower || options.ShowCpuClock;
            if (anyCpu)
                categories |= HardwareMonitoringCategory.Cpu;

            if (options.ShowMemory)
                categories |= HardwareMonitoringCategory.Memory;

            bool anyGpu = options.ShowGpuTemperature || options.ShowGpuUsage ||
                          options.ShowGpuPower || options.ShowGpuClock ||
                          options.ShowGpuVram;
            if (anyGpu)
                categories |= HardwareMonitoringCategory.Gpu;

            bool anyBattery = options.ShowBatteryPercent || options.ShowBatteryStatus ||
                              options.ShowBatteryTime || options.ShowBatteryPower;
            if (anyBattery)
                categories |= HardwareMonitoringCategory.Battery;

            if (categories != HardwareMonitoringCategory.None)
                _rtssOsdHardwareLease = _hardware.Acquire(categories);

            bool anyFps = options.ShowFps || options.ShowFpsOnePercentLow ||
                          options.ShowFpsPointOnePercentLow;
            bool anyFrameTime = options.ShowFrameTimeCurrent || options.ShowFrameTimeOnePercent ||
                                options.ShowFrameTimePointOnePercent;
            if (anyFps || anyFrameTime)
                _fps.Acquire();

            _rtssOsdTimer = new System.Timers.Timer(options.RefreshIntervalMs > 0 ? options.RefreshIntervalMs : 250);
            _rtssOsdTimer.AutoReset = true;
            _rtssOsdTimer.Elapsed += OnRtssOsdTick;
            _rtssOsdTimer.Start();
        }

        private void StopRtssOsd()
        {
            if (_rtssOsdTimer is not null)
            {
                _rtssOsdTimer.Stop();
                _rtssOsdTimer.Elapsed -= OnRtssOsdTick;
                _rtssOsdTimer.Dispose();
                _rtssOsdTimer = null;
            }

            _rtssOsdHardwareLease?.Dispose();
            _rtssOsdHardwareLease = null;

            _rtssOsdWasShowing = false;
            RtssOsdWriter.Release();
        }

        private void OnRtssOsdTick(object? sender, ElapsedEventArgs e)
        {
            try
            {
                FpsMetricsSnapshot fps = _fps.Latest;
                OverlayOptions options = _settingsStore.Current;
                HardwareMetricsSnapshot hardware = _hardware.ReadSnapshot();

                // Check if the game lost focus while OSD was showing.
                // Only release when we were previously showing — don't block
                // the OSD from starting when the user explicitly clicks Show.
                if (_rtssOsdWasShowing && fps.IsAvailable && fps.ProcessId > 0)
                {
                    int foregroundPid = GetForegroundProcessId();
                    if (foregroundPid != fps.ProcessId)
                    {
                        // Game lost focus — clear the OSD.
                        RtssOsdWriter.Release();
                        _rtssOsdWasShowing = false;
                        return;
                    }
                }

                string? osdText = RtssOsdFormatter.Build(hardware, fps, options);
                if (osdText is not null)
                {
                    bool wrote = RtssOsdWriter.Update(osdText);
                    if (wrote)
                        _rtssOsdWasShowing = true;
                }
                else
                {
                    _rtssOsdWasShowing = false;
                }
            }
            catch (Exception ex)
            {
                // Transient read failure must not crash the timer loop.
                try { Serilog.Log.Warning(ex, "[Overlay] RtssOsdTick failed"); } catch { }
            }
        }

        private static int GetForegroundProcessId()
        {
            IntPtr window = GetForegroundWindow();
            if (window == IntPtr.Zero)
                return 0;

            _ = GetWindowThreadProcessId(window, out uint processId);
            return unchecked((int)processId);
        }

        [System.Runtime.InteropServices.LibraryImport("user32.dll")]
        private static partial IntPtr GetForegroundWindow();

        [System.Runtime.InteropServices.LibraryImport("user32.dll")]
        private static partial uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        #endregion

        private readonly record struct Hotkey(
            bool Control,
            bool Alt,
            bool Shift,
            Keys Key)
        {
            public bool Matches(KeyEventArgs e)
            {
                return e.Control == Control &&
                       e.Alt == Alt &&
                       e.Shift == Shift &&
                       e.KeyCode == Key;
            }

            public static bool TryParse(
                string? text,
                out Hotkey hotkey)
            {
                hotkey = default;

                if (string.IsNullOrWhiteSpace(text))
                    return false;

                bool control = false;
                bool alt = false;
                bool shift = false;
                bool hasPrimaryKey = false;

                Keys key = Keys.None;

                string[] tokens = text.Split(
                    '+',
                    StringSplitOptions.TrimEntries |
                    StringSplitOptions.RemoveEmptyEntries);

                foreach (string token in tokens)
                {
                    if (token.Equals(
                            "Ctrl",
                            StringComparison.OrdinalIgnoreCase) ||
                        token.Equals(
                            "Control",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        control = true;
                        continue;
                    }

                    if (token.Equals(
                            "Alt",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        alt = true;
                        continue;
                    }

                    if (token.Equals(
                            "Shift",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        shift = true;
                        continue;
                    }

                    if (hasPrimaryKey)
                        return false;

                    if (!Enum.TryParse(
                            token,
                            true,
                            out key))
                    {
                        return false;
                    }

                    hasPrimaryKey = true;
                }

                if (!hasPrimaryKey ||
                    key == Keys.None ||
                    key is
                        Keys.ControlKey or
                        Keys.LControlKey or
                        Keys.RControlKey or
                        Keys.Menu or
                        Keys.LMenu or
                        Keys.RMenu or
                        Keys.ShiftKey or
                        Keys.LShiftKey or
                        Keys.RShiftKey)
                {
                    return false;
                }

                hotkey = new Hotkey(
                    control,
                    alt,
                    shift,
                    key);

                return true;
            }
        }
    }
}
