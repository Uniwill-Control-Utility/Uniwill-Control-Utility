using Gma.System.MouseKeyHook;
using Microsoft.Extensions.Hosting;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using System.Windows.Forms;
using System.Windows.Threading;
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
        private readonly object _hookThreadLock = new();

        private IKeyboardMouseEvents? _hook;

        // The global hook's owning thread: a dedicated pumped dispatcher thread.
        // SetWindowsHookEx(WH_KEYBOARD_LL) can stall in win32k - proven from a
        // process dump of a frozen app: the UI thread sat inside the native call
        // (installing the hook during the secure-desktop transition of a UAC
        // prompt), and the frozen dispatcher left the splash overlay unable to
        // dismiss and the dashboard never rendered. Installing on a dedicated
        // thread, and posting rather than invoking, makes a stalled install cost
        // a late-activating hotkey - never a frozen window. The pump is required:
        // low-level hook callbacks are delivered to the installing thread's
        // message queue, and an unpumped thread gets the hook silently removed
        // after LowLevelHooksTimeout.
        private Thread? _hookThread;
        private Dispatcher? _hookDispatcher;

        private DateTime _lastHotkeyUtc = DateTime.MinValue;
        private bool _disposed;

        // RTSS OSD injection: timer-based update loop that writes metrics
        // directly into RTSS shared memory for in-game rendering.
        private System.Timers.Timer? _rtssOsdTimer;
        private IDisposable? _rtssOsdHardwareLease;
        private IDisposable? _rtssOsdFpsLease;
        private bool _rtssOsdWasShowing; // tracks if OSD was last showing for focus-loss detection
        private DateTime _rtssOsdLastBeatUtc;

        /// <summary>
        /// In-focus heartbeat cadence for the field diagnostics (see
        /// OnRtssOsdTick). 10 s: a 2-minute field repro yields ~12 lines, and
        /// the fingerprint resolution is plenty to tell streaming values from
        /// frozen ones.
        /// </summary>
        private static readonly TimeSpan OsdHeartbeatInterval = TimeSpan.FromSeconds(10);

        /// <summary>
        /// Tick duration that earns a stage-time line (see the tick's
        /// finally). Healthy ticks run sub-millisecond work every
        /// RefreshIntervalMs; 500 ms is ~2 refresh intervals - the field
        /// hiccup report (the OSD skips an update every ~3 ticks) lives
        /// there, and the stage times name the slow call. Debounced to one
        /// line per 5 s so a persistently slow chain stays readable.
        /// </summary>
        private const long SlowTickMs = 500;

        private DateTime _rtssOsdLastSlowLogUtc;

        /// <summary>Stage segment for the slow-tick line: "-" when the tick
        /// never reached that stage (it is parked inside the previous one).</summary>
        private static string Seg(long end, long start) =>
            end < 0 || start < 0 ? "-" : (end - start).ToString();

        /// <summary>
        /// OSD diagnostics at Warning severity - the app log's field level.
        /// Transitions and failures only, plus one bounded heartbeat line per
        /// <see cref="OsdHeartbeatInterval"/> while in focus. The whole OSD
        /// chain was invisible on guarded boxes (the writer logs at
        /// Information, the reader debounces into Debug, both filtered), and
        /// the freeze report (values stop 1-2 s after the game takes the
        /// foreground) cannot be closed without field evidence. The
        /// fingerprints separate the faces: heartbeat lines STOP = the tick
        /// thread is blocked; fingerprint stops CHANGING = the serving path
        /// froze; fingerprint streams while the render stays frozen = the
        /// writes land and RTSS rendering is the frozen side; the slot NUMBER
        /// changes every line = the claimed owner keeps getting dropped.
        /// </summary>
        private static void LogOsd(string message)
        {
            try { Serilog.Log.Warning("[Overlay] OSD " + message); } catch { }
        }

        private static string BuildHeartbeat(int slot, HardwareMetricsSnapshot h, FpsMetricsSnapshot f) =>
            $"in-game slot {slot}: fps {f.Fps} cpu {h.CpuTemperature}C {h.CpuUsage}% {h.CpuPowerWatts}W {h.CpuClockMhz}MHz " +
            $"gpu {h.GpuTemperature}C {(int)h.GpuUsage}% {h.GpuPowerWatts}W {h.GpuClockMhz}MHz " +
            $"mem {h.SystemMemoryUsedGb:F1}/{h.SystemMemoryTotalGb:F1}GB";
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
                 * The global hook goes to its dedicated pumped thread (field-
                 * proven): installing WH_KEYBOARD_LL on the UI thread stalled
                 * inside native SetWindowsHookEx during a UAC secure-desktop
                 * transition, and the frozen dispatcher froze the whole window
                 * (splash overlay unable to close at 92 %, dashboard never
                 * rendered). The hook is scoped to the installing thread's
                 * desktop, and every thread of an interactive-session process is
                 * on WinSta0\Default, so the dedicated thread keeps the events
                 * flowing without owning the UI.
                 */
                PostToHookThread(EnsureHook);

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

            // Uninstall on the hook thread (the callbacks' delivering thread), then
            // end its pump. BeginInvoke ordering: the removal runs before shutdown.
            PostToHookThread(RemoveHook);
            lock (_hookThreadLock)
            {
                _hookDispatcher?.BeginInvokeShutdown(DispatcherPriority.Normal);
                _hookDispatcher = null;
                _hookThread = null;
            }

            return Task.CompletedTask;
        }

        public void ApplyOptions(OverlayOptions options)
        {
            ThrowIfDisposed();

            OverlayOptions normalisedOptions =
                NormaliseForStorage(options);

            SaveOptionsIfChanged(normalisedOptions);

            // Hook enable/disable belongs to the hook thread; the RTSS timer keeps
            // the dispatcher it has always run on.
            PostToHookThread(() =>
            {
                if (normalisedOptions.Enabled)
                    EnsureHook();
                else
                    RemoveHook();
            });

            RunOnUiThread(() =>
            {
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
                if (_hook is not null)
                {
                    _hook.KeyDown -= OnGlobalKeyDown;
                    _hook.Dispose();
                    _hook = null;
                }
            }
        }

        /// <summary>
        /// Queues hook work onto the dedicated pumped hook thread, starting it on
        /// first use. Post (BeginInvoke), never Invoke: the point of the thread is
        /// that a stalled SetWindowsHookEx cannot block the caller - least of all
        /// the UI thread. The thread is on the interactive session's window
        /// station like every thread of this process, so the hook sees the same
        /// desktop it always did.
        /// </summary>
        private void PostToHookThread(Action action)
        {
            ArgumentNullException.ThrowIfNull(action);

            Dispatcher dispatcher;
            lock (_hookThreadLock)
            {
                if (_hookDispatcher is null)
                {
                    var pumpReady = new ManualResetEventSlim(false);
                    _hookThread = new Thread(() =>
                    {
                        _hookDispatcher = Dispatcher.CurrentDispatcher;
                        pumpReady.Set();
                        Dispatcher.Run();
                    })
                    {
                        IsBackground = true,
                        Name = "UCU Global Hook",
                    };
                    _hookThread.SetApartmentState(ApartmentState.STA);
                    _hookThread.Start();
                    pumpReady.Wait();
                }

                dispatcher = _hookDispatcher!;
            }

            if (dispatcher.CheckAccess())
                action();
            else
                dispatcher.BeginInvoke(action, DispatcherPriority.Input);
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

            // Uninstall on the hook thread if there is one (never start a thread
            // to tear one down), then end its pump.
            lock (_hookThreadLock)
            {
                if (_hookDispatcher is not null)
                {
                    Dispatcher dispatcher = _hookDispatcher;
                    _hookDispatcher = null;
                    _hookThread = null;

                    if (dispatcher.CheckAccess())
                        RemoveHook();
                    else
                        dispatcher.BeginInvoke(RemoveHook, DispatcherPriority.Normal);

                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal);
                }
            }

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
                Stopwatch sw = Stopwatch.StartNew();
                long tSettings = -1, tFocus = -1, tEnrich = -1, tWrite = -1;
                try
                {
                    FpsMetricsSnapshot fps = _fps.Latest;
                    OverlayOptions options = _settingsStore.Current;
                    tSettings = sw.ElapsedMilliseconds;

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
                int foregroundPid = GetForegroundProcessId();

                if (fps.IsAvailable && fps.ProcessId > 0)
                {
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
                    if (foregroundPid > 0 && RtssReader.TryRead(foregroundPid, out RtssFpsData fallbackData) && fallbackData.Fps > 0
                        && fallbackData.ProcessId == foregroundPid)
                    {
                        gameInFocus = true;
                    }
                }

                tFocus = sw.ElapsedMilliseconds;

                // If the game is not in focus, write a single-space OSD to hide
                // the metrics while keeping the slot claimed. RTSS renders this
                // as a blank line, effectively hiding the overlay. The slot stays
                // claimed so the next Update() can overwrite without re-claiming.
                // After hiding, also call Release() to fully clear the slot.
                if (!gameInFocus)
                {
                    if (_rtssOsdWasShowing)
                    {
                        LogOsd(
                            $"focus OFF: foreground pid={foregroundPid}, " +
                            $"fps-service pid={fps.ProcessId} available={fps.IsAvailable}");
                    }

                    // First write a space so RTSS renders a blank frame, then
                    // release the slot. The two-step approach ensures RTSS sees
                    // the buffer change before the slot goes unowned.
                    RtssOsdWriter.Update(" ");
                    RtssOsdWriter.Release();
                    tWrite = sw.ElapsedMilliseconds;
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

                tEnrich = sw.ElapsedMilliseconds;

                string? osdText = RtssOsdFormatter.Build(hardware, fps, options, hydroUi, flydigi);
                if (osdText is null)
                {
                    if (_rtssOsdWasShowing)
                        LogOsd("formatter produced no text (no rows enabled)");
                    tWrite = tEnrich;
                    _rtssOsdWasShowing = false;
                    return;
                }

                int slot = RtssOsdWriter.Update(osdText);
                if (slot >= 0)
                {
                    DateTime nowUtc = DateTime.UtcNow;
                    if (!_rtssOsdWasShowing)
                    {
                        LogOsd(
                            $"focus ON: game pid={foregroundPid}, " +
                            $"source={(fps.IsAvailable ? "fps-service" : "rtss-fallback")}, slot {slot}, fps {fps.Fps}");
                        _rtssOsdLastBeatUtc = nowUtc;
                    }
                    else if (nowUtc - _rtssOsdLastBeatUtc >= OsdHeartbeatInterval)
                    {
                        LogOsd(BuildHeartbeat(slot, hardware, fps));
                        _rtssOsdLastBeatUtc = nowUtc;
                    }

                    _rtssOsdWasShowing = true;
                }
                else
                {
                    if (_rtssOsdWasShowing)
                        LogOsd("writer failed: no slot/mapping (RTSS gone, slot table full, owner dropped)");
                    _rtssOsdWasShowing = false;
                }

                tWrite = sw.ElapsedMilliseconds;
            }
            catch (Exception ex)
            {
                // Transient read failure must not crash the timer loop.
                try { Serilog.Log.Warning(ex, "[Overlay] RtssOsdTick failed"); } catch { }
            }
            finally
            {
                // Stage breadcrumbs (field report 2026-10-29: the OSD skips
                // an update every ~3 ticks). Each stage is timed; a tick over
                // SlowTickMs names where the time went: fps+settings
                // (_fps.Latest lock, settings store), focus-detect
                // (GetForegroundProcessId, RtssReader.TryRead shared memory),
                // read+enrich (ReadSnapshot, EC fan cache, Hydro UI, Flydigi
                // HID), format+write (RtssOsdWriter shared memory). If the
                // stages stay fast while the visible OSD hiccups, the cadence
                // is RTSS's own render/update side, not this chain.
                if (sw.ElapsedMilliseconds >= SlowTickMs &&
                    DateTime.UtcNow - _rtssOsdLastSlowLogUtc >= TimeSpan.FromSeconds(5))
                {
                    _rtssOsdLastSlowLogUtc = DateTime.UtcNow;
                    LogOsd(
                        $"slow tick {sw.ElapsedMilliseconds}ms: " +
                        $"fps+settings {Seg(tSettings, 0)}ms, " +
                        $"focus-detect {Seg(tFocus, tSettings)}ms, " +
                        $"read+enrich {Seg(tEnrich, tFocus)}ms, " +
                        $"format+write {Seg(tWrite, tEnrich)}ms");
                }
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
