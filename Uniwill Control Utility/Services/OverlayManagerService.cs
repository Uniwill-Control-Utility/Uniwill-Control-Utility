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
        private readonly EcTelemetryService? _ecTelemetry;
        private readonly HydroUIService? _hydroUi;
        private readonly FlydigiCoolerHidService? _flydigi;

        private readonly object _settingsSaveLock = new();
        private readonly object _hookLock = new();

        private IKeyboardMouseEvents? _hook;

        private DateTime _lastHotkeyUtc = DateTime.MinValue;
        private bool _disposed;

        // RTSS OSD injection: timer-based update loop that writes metrics
        // directly into RTSS shared memory for in-game rendering.
        private System.Timers.Timer? _rtssOsdTimer;
        private IDisposable? _rtssOsdHardwareLease;
        private IDisposable? _rtssOsdFpsLease;
        private bool _rtssOsdWasShowing; // tracks if OSD was last showing for focus-loss detection
        private readonly object _rtssOsdLock = new(); // prevents concurrent ticks from System.Timers.Timer thread-pool dispatch

        public OverlayOptions CurrentOptions =>
            _settingsStore.Current;

        public OverlayManagerService(
            OverlaySettingsStore settingsStore,
            IHardwareMonitoringService hardware,
            Performance.IFpsMonitoringService fps,
            EcTelemetryService? ecTelemetry = null,
            HydroUIService? hydroUi = null,
            FlydigiCoolerHidService? flydigi = null)
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

            _ecTelemetry = ecTelemetry;
            _hydroUi = hydroUi;
            _flydigi = flydigi;
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
            HardwareMonitoringCategory categories = HardwareMonitoringCategory.None;

            if (options.ShowCpuRow)
                categories |= HardwareMonitoringCategory.Cpu;

            if (options.ShowRamRow)
                categories |= HardwareMonitoringCategory.Memory;

            if (options.ShowGpuRow || options.ShowVramRow)
                categories |= HardwareMonitoringCategory.Gpu;

            if (categories != HardwareMonitoringCategory.None)
                _rtssOsdHardwareLease = _hardware.Acquire(categories);

            // Always acquire FPS monitoring — we need fps.IsAvailable and
            // fps.ProcessId for game focus detection even when no FPS rows
            // are shown in the OSD. Store the lease so it isn't GC'd.
            _rtssOsdFpsLease = _fps.Acquire();

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

            _rtssOsdFpsLease?.Dispose();
            _rtssOsdFpsLease = null;

            lock (_rtssOsdLock)
            {
                _rtssOsdWasShowing = false;
                RtssOsdWriter.Release();
            }
        }

        private void OnRtssOsdTick(object? sender, ElapsedEventArgs e)
        {
            lock (_rtssOsdLock)
            {
                try
                {
                    FpsMetricsSnapshot fps = _fps.Latest;
                    OverlayOptions options = _settingsStore.Current;

                // Determine whether the target game is currently in focus.
                // The OSD should only render when the game window is foreground.
                //
                // fps.IsAvailable may be false even when a game is running, because
                // IsRecognizedGame() in FpsMonitoringService blocks processes not in
                // the installed game library (e.g. Wallpaper Engine). In that case
                // fall back to a direct RTSS read for the foreground PID so the OSD
                // still shows hardware metrics for hooked processes outside the
                // game library.
                bool gameInFocus = false;

                if (fps.IsAvailable && fps.ProcessId > 0)
                {
                    int foregroundPid = GetForegroundProcessId();
                    gameInFocus = foregroundPid == fps.ProcessId;
                }
                else
                {
                    // FpsMonitoringService rejected the process (IsRecognizedGame
                    // returned false). Fall back to a direct RTSS read for the
                    // foreground PID — but only accept an exact PID match.
                    // TryRead has an internal fallback to any hooked process with
                    // FPS > 0, which would make gameInFocus always true when any
                    // game is running. We verify the returned PID matches the
                    // foreground to avoid that.
                    int foregroundPid = GetForegroundProcessId();
                    if (foregroundPid > 0 && RtssReader.TryRead(foregroundPid, out RtssFpsData fallbackData) && fallbackData.Fps > 0
                        && fallbackData.ProcessId == foregroundPid)
                    {
                        gameInFocus = true;
                    }
                }

                // If the game is not in focus, write a single-space OSD to hide
                // the metrics while keeping the slot claimed. RTSS renders this
                // as a blank line, effectively hiding the overlay. The slot stays
                // claimed so the next Update() can overwrite without re-claiming.
                // After hiding, also call Release() to fully clear the slot.
                if (!gameInFocus)
                {
                    // First write a space so RTSS renders a blank frame, then
                    // release the slot. The two-step approach ensures RTSS sees
                    // the buffer change before the slot goes unowned.
                    RtssOsdWriter.Update(" ");
                    RtssOsdWriter.Release();
                    _rtssOsdWasShowing = false;
                    return;
                }

                // Game is in focus — build and write the OSD.
                HardwareMetricsSnapshot hardware = _hardware.ReadSnapshot();

                // Enrich with EC fan duty data (cached, non-blocking read).
                if (_ecTelemetry is not null)
                {
                    EcFanDutySnapshot ecFan = _ecTelemetry.ReadFanDuty();
                    hardware = hardware with
                    {
                        CpuFanSpeed = ecFan.CpuFanPercent,
                        GpuFanSpeed = ecFan.GpuFanPercent
                    };
                }

                // Gather Hydro UI data (null when not connected or disabled in options).
                HydroUiSnapshot? hydroUi = null;
                if (options.ShowHydroUi && _hydroUi?.IsConnected == true)
                {
                    Models.PumpVoltage pump = _hydroUi.IsSmartModeActive
                        ? _hydroUi.SmartModeCurrentPump
                        : _hydroUi.GetSettings().GetPumpVoltage();
                    Models.FanSpeed fan = _hydroUi.IsSmartModeActive
                        ? _hydroUi.SmartModeCurrentFan
                        : _hydroUi.GetSettings().GetFanSpeed();
                    hydroUi = new HydroUiSnapshot(pump, fan);
                }

                // Gather Flydigi data (null when not connected or disabled in options).
                FlydigiSnapshot? flydigi = null;
                if (options.ShowFlydigi && _flydigi?.IsConnected == true && _flydigi.FanRpmData is { } fanData)
                {
                    string model = _flydigi.ConnectedDeviceInfo?.ModelName ?? "FLY";
                    flydigi = new FlydigiSnapshot(fanData.CurrentRpm, model);
                }

                string? osdText = RtssOsdFormatter.Build(hardware, fps, options, hydroUi, flydigi);
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

        /// <summary>Snapshot of Hydro UI pump and fan state for the RTSS OSD formatter.</summary>
        internal readonly record struct HydroUiSnapshot(
            Models.PumpVoltage EffectivePumpVoltage,
            Models.FanSpeed EffectiveFanSpeed);

        /// <summary>Snapshot of Flydigi cooler state for the RTSS OSD formatter.</summary>
        internal readonly record struct FlydigiSnapshot(int FanRpm, string ModelName);
    }
}
