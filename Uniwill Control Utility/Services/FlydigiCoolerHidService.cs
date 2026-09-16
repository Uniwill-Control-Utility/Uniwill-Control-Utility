using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HidLibrary;
using Newtonsoft.Json;
using Uniwill_Control_Utility.Models;
using Uniwill_Control_Utility.Scripts;

namespace Uniwill_Control_Utility.Services
{
    /// <summary>
    /// HID communication layer for Flydigi BS series cooling pads (BS2, BS2 PRO, BS3, BS3 PRO).
    /// Uses HidLibrary for device I/O with the 5A A5 frame protocol.
    /// </summary>
    public class FlydigiCoolerHidService : IDisposable
    {
        /* ------------------------------------------------------------------ */
        /*  Windows power notification P/Invoke                                 */
        /* ------------------------------------------------------------------ */

        [DllImport("powrprof.dll", SetLastError = true)]
        private static extern nint PowerRegisterSuspendResumeNotification(
            int Flags,
            nint EventHandle);

        [DllImport("kernel32.dll")]
        private static extern nint CreateEvent(
            nint lpEventAttributes,
            int bManualReset,
            int bInitialState,
            string? lpName);

        [DllImport("kernel32.dll")]
        private static extern int WaitForSingleObject(
            nint hHandle,
            int dwMilliseconds);

        private const int WAIT_OBJECT_0 = 0;
        private const int INFINITE = -1;

        /* ------------------------------------------------------------------ */
        /*  HID interface arrival notification (cfgmgr32.dll)                    */
        /* ------------------------------------------------------------------ */

        // THRM uses CM_Register_Notification for instant HID device arrival detection.
        // This replaces the slow 5-second polling device monitor.
        [DllImport("cfgmgr32.dll", SetLastError = true)]
        private static extern int CM_Register_Notification(
            ref CmNotifyFilter filter,
            nint context,
            CmNotifyCallback callback,
            out nint handle);

        [DllImport("cfgmgr32.dll", SetLastError = true)]
        private static extern int CM_UnregisterNotification(nint handle);

        // GUID_DEVINTERFACE_HID = {4d1e55b2-f16f-11cf-88cb-001111000030}
        private static readonly Guid HidDeviceInterfaceGuid = new(
            0x4d1e55b2, 0xf16f, 0x11cf, 0x88, 0xcb, 0x00, 0x11, 0x11, 0x00, 0x00, 0x30);

        private const int CmNotifyFilterTypeDeviceInterface = 0;
        private const int CmNotifyActionInterfaceArrival = 0;
        private const int CmNotifyActionInterfaceRemoval = 1;
        private const int CmNotifyEventSymbolicLinkOffset = 24;

        // CM_NOTIFY_FILTER struct (sized for device-interface notifications)
        [StructLayout(LayoutKind.Sequential)]
        private struct CmNotifyFilter
        {
            public uint cbSize;
            public uint Flags;
            public uint FilterType;
            public uint Reserved;
            public Guid ClassGuid;
            // Union tail padded to max device ID length (200 chars * 2 bytes)
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 400 - 16)] // 400 total minus header size
            public byte[] UnionTail;
        }

        // Callback delegate for CM_NOTIFY_CALLBACK
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void CmNotifyCallback(
            nint context,
            uint notificationType,
            nint eventData,
            uint eventDataSize);

        private nint _hidArrivalHandle = nint.Zero;
        private CmNotifyCallback? _hidArrivalCallback; // Keep alive to prevent GC

        // Flydigi vendor/product identifiers to match
        private static readonly string[] FlydigiHidIdentifiers =
        {
            "vid_37d7&pid_1001", // BS2
            "vid_37d7&pid_1002", // BS2 PRO
            "vid_37d7&pid_1003", // BS3
            "vid_37d7&pid_1004", // BS3 PRO
        };

        private nint _powerEventHandle = nint.Zero;
        private nint _powerNotificationHandle = nint.Zero;
        private CancellationTokenSource? _powerMonitorCts;
        private Thread? _powerMonitorThread;

        /* ------------------------------------------------------------------ */
        /*  Device plug/unplug monitor                                         */
        /* ------------------------------------------------------------------ */

        private CancellationTokenSource? _deviceMonitorCts;
        private Thread? _deviceMonitorThread;
        private bool _lastDeviceDetected;

        /// <summary>
        /// Set to true when the user explicitly clicks Disconnect.
        /// Prevents all auto-reconnect logic (startup, HID arrival, device monitor)
        /// from reconnecting until the user clicks Connect again.
        /// Reset to false on Connect.
        /// </summary>
        private bool _userDisconnected;

        /// <summary>
        /// Background reconnection loop that runs when disconnected (not by user).
        /// Uses exponential backoff with a wake mechanism (HID arrival wakes the delay).
        /// After exhausting all delays, gives up and waits for the health check to
        /// trigger a new round. This matches THRM's reconnect strategy.
        /// </summary>
        private CancellationTokenSource? _reconnectCts;
        private Thread? _reconnectThread;
        private readonly object _reconnectLock = new();

        /// <summary>ManualResetEvent used to wake the reconnect loop from HID arrival.</summary>
        private ManualResetEvent? _reconnectWakeEvent;

        /// <summary>
        /// Monitors device health every 30 seconds. If disconnected, triggers a
        /// new reconnect round. This ensures we keep trying even after the
        /// exponential backoff has exhausted all its delays.
        /// </summary>
        private CancellationTokenSource? _healthCheckCts;
        private Thread? _healthCheckThread;

        /// <summary>
        /// Incremented on each disconnect or suspend. Used to prevent stale
        /// connection attempts from overriding newer ones (e.g., if two reconnect
        /// rounds are in-flight simultaneously).
        /// </summary>
        private int _connectGeneration;

        /* ------------------------------------------------------------------ */
        /*  Time-based curve schedule monitor                                  */
        /* ------------------------------------------------------------------ */

        private CancellationTokenSource? _scheduleMonitorCts;
        private Thread? _scheduleMonitorThread;
        private string _lastScheduledProfile = string.Empty;
        private HidDevice? _device;
        private CancellationTokenSource? _readCts;
        private Task? _readTask;
        private readonly object _writeLock = new();
        private DateTime _lastWriteTime = DateTime.MinValue;

        private Bs2ProSettings _settings = new();

        /* ------------------------------------------------------------------ */
        /*  Fan speed control state                                            */
        /* ------------------------------------------------------------------ */

        /// <summary>
        /// Whether the device is currently in realtime RPM mode (0x23 has been sent
        /// without a corresponding 0x24 exit).  When true, subsequent RPM changes
        /// only need to send 0x21 — no re-entry handshake.
        /// </summary>
        private bool _realtimeMode;

        /// <summary>Last RPM value we successfully commanded (0 = fan off / not set).</summary>
        private ushort _lastCommandedRpm;

        /// <summary>Whether _lastCommandedRpm is meaningful.</summary>
        private bool _hasCommandedRpm;

        /// <summary>
        /// Raised when fan RPM data changes (either from device notification or local command).
        /// </summary>
        public event EventHandler<FanRpmData>? FanDataReceived;

        /// <summary>Latest RPM data known to the service.</summary>
        public FanRpmData? FanRpmData { get; private set; }

        /// <summary>Last known current RPM from a 0xEF device notification.</summary>
        private ushort _lastKnownCurrentRpm;

        /// <summary>Last known target RPM from either a 0xEF notification or a write command.</summary>
        private ushort _lastKnownTargetRpm;

        /* ------------------------------------------------------------------ */
        /*  Query response waiting                                             */
        /* ------------------------------------------------------------------ */

        /// <summary>Pending query waiters keyed by command byte.</summary>
        private readonly Dictionary<byte, TaskCompletionSource<Bs2ProFrame.ParsedFrame?>> _pendingQueries = new();

        /// <summary>Lock for _pendingQueries access.</summary>
        private readonly object _queryLock = new();

        /// <summary>
        /// Sends a query command and waits for a matching response frame.
        /// Returns null if no response arrives within 300ms.
        /// </summary>
        private async Task<Bs2ProFrame.ParsedFrame?> SendQueryAsync(byte cmd)
        {
            var tcs = new TaskCompletionSource<Bs2ProFrame.ParsedFrame?>();

            lock (_queryLock)
            {
                _pendingQueries[cmd] = tcs;
            }

            // Fire and forget cleanup after timeout
            _ = Task.Delay(300, CancellationToken.None).ContinueWith(_ =>
            {
                lock (_queryLock)
                {
                    if (_pendingQueries.TryGetValue(cmd, out var existing) && existing == tcs)
                    {
                        _pendingQueries.Remove(cmd);
                        tcs.TrySetResult(null);
                    }
                }
            });

            // Send the query
            bool sent = WriteReport(Bs2ProFrame.Build(cmd));
            if (!sent)
            {
                lock (_queryLock)
                {
                    _pendingQueries.Remove(cmd);
                }
                tcs.TrySetResult(null);
            }

            return await tcs.Task;
        }

        /// <summary>
        /// Called from OnInputReport when a non-notification frame arrives.
        /// Attempts to deliver it to a waiting query.
        /// </summary>
        private void TryDeliverQueryResponse(Bs2ProFrame.ParsedFrame parsed)
        {
            lock (_queryLock)
            {
                if (_pendingQueries.TryGetValue(parsed.Command, out var tcs))
                {
                    _pendingQueries.Remove(parsed.Command);
                    tcs.TrySetResult(parsed);
                }
            }
        }

        /// <summary>
        /// Raised when the connection state changes.
        /// </summary>
        public event EventHandler<bool>? ConnectionStateChanged;

        /// <summary>
        /// Raised when a status notification is received from the device.
        /// </summary>
        public event EventHandler<Bs2ProStatusNotification>? StatusReceived;

        /// <summary>
        /// Raised when a status message should be displayed to the user.
        /// </summary>
        public event EventHandler<string>? StatusChanged;

        /// <summary>
        /// Raised when device settings are updated (from query or on-connect).
        /// </summary>
        public event EventHandler<Bs2ProDeviceSettings>? DeviceSettingsUpdated;

        /// <summary>Last known device settings from query or notifications.</summary>
        public Bs2ProDeviceSettings? DeviceSettings { get; private set; }

        public bool IsConnected => _device != null && _device.IsOpen;

        /// <summary>
        /// Stops all background threads and disconnects the device.
        /// Called when the user disables Flydigi cooler functionality in Settings.
        /// Use <see cref="ResumeBackgroundServices"/> to re-enable.
        /// </summary>
        public void SuspendAllServices()
        {
            _userDisconnected = true; // Block any auto-reconnect in-flight
            _connectGeneration++; // Invalidate any in-flight reconnect
            DisconnectAsync();
            StopReconnectLoop();
            StopHealthCheck();
            StopDeviceMonitor();
            StopHidArrivalNotification();
            StopPowerNotificationMonitor();
            StopScheduleMonitor();
        }

        /// <summary>
        /// Restarts all background threads after a prior <see cref="SuspendAllServices"/> call.
        /// Called when the user re-enables Flydigi cooler functionality in Settings.
        /// </summary>
        public void ResumeBackgroundServices()
        {
            LoadSettings();
            _userDisconnected = false;
            _connectGeneration++; // Invalidate any stale reconnect from before suspend

            StartPowerNotificationMonitor();
            StartHidArrivalNotification();
            StartDeviceMonitor();
            StartHealthCheck();
            StartTimeScheduleMonitor();

            // Auto-connect if enabled
            if (_settings.AutoConnect)
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(1000, CancellationToken.None);
                    await TryAutoConnectAsync();
                });
            }
        }

        public FlydigiCoolerDeviceInfo? ConnectedDeviceInfo { get; private set; }

        public FlydigiCoolerHidService()
        {
            LoadSettings();

            // If the user has disabled Flydigi functionality entirely, skip all
            // background initialization to avoid any unnecessary HID operations.
            if (Properties.Settings.Default.HideFlydigi)
                return;

            StartPowerNotificationMonitor();
            StartHidArrivalNotification();
            StartDeviceMonitor();
            StartHealthCheck();
            StartTimeScheduleMonitor();

            // Auto-connect on app startup (like THRM does 1 second after launch).
            // Only connects if AutoConnect is enabled in settings.
            _ = Task.Run(async () =>
            {
                await Task.Delay(1000, CancellationToken.None); // Brief delay for system to stabilize
                if (_settings.AutoConnect)
                    await TryAutoConnectAsync();
            });
        }

        #region Settings Persistence

        private static readonly string SettingsFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UCU");

        private void LoadSettings()
        {
            try
            {
                var filePath = Path.Combine(SettingsFolder, "bs2pro_settings.json");

                if (File.Exists(filePath))
                {
                    var json = File.ReadAllText(filePath);
                    _settings = JsonConvert.DeserializeObject<Bs2ProSettings>(json) ?? new Bs2ProSettings();
                }
            }
            catch (Exception)
            {
                _settings = new Bs2ProSettings();
            }
        }

        private void SaveSettings()
        {
            try
            {
                Directory.CreateDirectory(SettingsFolder);
                var filePath = Path.Combine(SettingsFolder, "bs2pro_settings.json");

                var json = JsonConvert.SerializeObject(_settings, Formatting.Indented);
                File.WriteAllText(filePath, json);
            }
            catch (Exception ex)
            {
                RaiseStatus($"Settings save failed: {ex.Message}");
            }
        }

        public Bs2ProSettings GetSettings() => _settings;

        /// <summary>Persists the current settings to disk.</summary>
        public void PersistSettings() => SaveSettings();

        #endregion

        #region Device Discovery

        /// <summary>
        /// Enumerates all connected Flydigi BS series devices.
        /// Opens each device briefly to read capabilities, then closes it.
        /// </summary>
        public static List<FlydigiCoolerDeviceInfo> DiscoverDevices()
        {
            var devices = HidDevices.Enumerate(
                Bs2ProProductId.VendorId,
                Bs2ProProductId.AllProductIds);

            var result = new List<FlydigiCoolerDeviceInfo>();

            foreach (var d in devices)
            {
                try
                {
                    d.OpenDevice();

                    var productName = d.ReadProduct(out byte[] productBytes)
                        ? Encoding.Unicode.GetString(productBytes).Trim('\0')
                        : string.Empty;

                    var manufacturer = d.ReadManufacturer(out byte[] manBytes)
                        ? Encoding.Unicode.GetString(manBytes).Trim('\0')
                        : string.Empty;

                    var serial = d.ReadSerialNumber(out byte[] serialBytes)
                        ? Encoding.Unicode.GetString(serialBytes).Trim('\0')
                        : string.Empty;

                    var productId = (ushort)d.Attributes.ProductId;

                    d.CloseDevice();

                    result.Add(new FlydigiCoolerDeviceInfo
                    {
                        DevicePath = d.DevicePath,
                        ProductName = productName,
                        Manufacturer = manufacturer,
                        SerialNumber = serial,
                        ProductId = productId
                    });
                }
                catch (Exception)
                {
                    // Skip devices that can't be opened or read
                    try { d.CloseDevice(); } catch (Exception) { /* Device close failed during error handling; skipping */ }
                }
            }

            return result;
        }

        /// <summary>
        /// Returns whether any Flydigi BS series device is currently available.
        /// </summary>
        public static bool IsDeviceAvailable()
        {
            return DiscoverDevices().Count > 0;
        }

        /// <summary>
        /// Discovers Flydigi BS series devices, using BLE advertisement scanning as a
        /// fallback when HID enumeration finds nothing (e.g., device is nearby but not
        /// yet paired or the HID driver hasn't bound).
        /// </summary>
        public static async Task<List<FlydigiCoolerDeviceInfo>> DiscoverDevicesAsync(int timeoutMs = 8000)
        {
            // 1. Try HID enumeration first (fastest path for already-connected devices)
            var devices = DiscoverDevices();
            if (devices.Count > 0)
                return devices;

            // 2. BLE advertisement scan: look for BS2+ advertising names, then try to
            //    pair (which triggers Windows HID driver binding), then retry HID enum.
            try
            {
                var watcher = new Windows.Devices.Bluetooth.Advertisement.BluetoothLEAdvertisementWatcher();
                var matchedDevices = new System.Collections.Concurrent.ConcurrentBag<Windows.Devices.Enumeration.DeviceInformation>();

                watcher.Received += async (s, e) =>
                {
                    var name = e.Advertisement.LocalName;
                    if (!string.IsNullOrEmpty(name) && IsBsPlusAdvertisingName(name))
                    {
                        // Try to resolve to a DeviceInformation so we can pair
                        try
                        {
                            var bleDevice = await Windows.Devices.Bluetooth.BluetoothLEDevice
                                .FromBluetoothAddressAsync(e.BluetoothAddress).AsTask();
                            if (bleDevice != null && !string.IsNullOrEmpty(bleDevice.DeviceId))
                            {
                                var info = await Windows.Devices.Enumeration.DeviceInformation
                                    .CreateFromIdAsync(bleDevice.DeviceId).AsTask();
                                if (info != null)
                                    matchedDevices.Add(info);
                                bleDevice.Dispose();
                            }
                        }
                        catch { /* Non-critical: resolution failure during scan */ }
                    }
                };

                watcher.Start();
                await Task.Delay(timeoutMs);
                watcher.Stop();

                // Also try enumerating all known BLE devices (paired and unpaired) for matches
                try
                {
                    var selector = Windows.Devices.Bluetooth.BluetoothLEDevice.GetDeviceSelector();
                    var allDevices = await Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(selector);
                    foreach (var info in allDevices)
                    {
                        if (!string.IsNullOrEmpty(info.Name) && IsBsPlusAdvertisingName(info.Name))
                        {
                            // Check if not already in matchedDevices by DeviceId
                            bool alreadyMatched = matchedDevices.Any(m => m.Id == info.Id);
                            if (!alreadyMatched)
                                matchedDevices.Add(info);
                        }
                    }
                }
                catch { /* Non-critical: enumeration failure */ }

                foreach (var info in matchedDevices.DistinctBy(d => d.Id))
                {
                    try
                    {
                        // If already paired, just wait for HID binding
                        if (info.Pairing.IsPaired)
                        {
                            await Task.Delay(500);
                        }
                        else
                        {
                            // Attempt to pair — this triggers Windows HID driver binding
                            var pairResult = await info.Pairing.PairAsync().AsTask();
                            if (pairResult.Status != Windows.Devices.Enumeration.DevicePairingResultStatus.Paired &&
                                pairResult.Status != Windows.Devices.Enumeration.DevicePairingResultStatus.AlreadyPaired)
                            {
                                continue; /* Pairing failed, skip this device */
                            }
                            await Task.Delay(1500);
                        }

                        // Retry HID enumeration after pairing
                        devices = DiscoverDevices();
                        if (devices.Count > 0)
                            return devices;
                    }
                    catch
                    {
                        // Non-critical: individual pairing failures during discovery
                    }
                }
            }
            catch
            {
                // BLE not available or permission denied — fall through to empty result
            }

            return devices;
        }

        /// <summary>
        /// Returns true if the advertising name matches a known Flydigi BS2+ device.
        /// These devices advertise as e.g. "FlyDigi BS2PRO", "FlyDigi BS3PRO".
        /// </summary>
        private static bool IsBsPlusAdvertisingName(string name)
        {
            string lower = name.ToLowerInvariant();
            // Exclude BS1 (BLE-only, non-HID) — we only want BS2+ HID devices here.
            if (lower.Contains("bs1"))
                return false;

            return lower.Contains("bs2") || lower.Contains("bs3");
        }

        #endregion

        #region Connection Management

        /// <summary>
        /// Connects to the device at the specified HID path.
        /// Starts a background read loop for status notifications.
        /// </summary>
        public async Task<bool> ConnectAsync(string devicePath)
        {
            ClearUserDisconnected();
            var devices = HidDevices.Enumerate(Bs2ProProductId.VendorId, Bs2ProProductId.AllProductIds).ToList();
            var target = devices.FirstOrDefault(d => d.DevicePath == devicePath);

            // If the saved path is stale (e.g., after a Bluetooth reconnect where
            // Windows may not change the device path), fall back to any available device.
            if (target == null && devices.Count > 0)
            {
                target = devices[0];
                System.Diagnostics.Debug.WriteLine($"[FlydigiCoolerHidService] Saved path {devicePath} not found, falling back to {target.DevicePath}");
            }

            if (target == null)
            {
                RaiseStatus("Device not found at specified path");
                return false;
            }

            // Disconnect any existing connection first
            DisconnectAsync();

            try
            {
                target.OpenDevice();
                _device = target;

                var productName = target.ReadProduct(out byte[] productBytes)
                    ? Encoding.Unicode.GetString(productBytes).Trim('\0')
                    : string.Empty;

                var manufacturer = target.ReadManufacturer(out byte[] manBytes)
                    ? Encoding.Unicode.GetString(manBytes).Trim('\0')
                    : string.Empty;

                var serial = target.ReadSerialNumber(out byte[] serialBytes)
                    ? Encoding.Unicode.GetString(serialBytes).Trim('\0')
                    : string.Empty;

                var productId = (ushort)target.Attributes.ProductId;

                ConnectedDeviceInfo = new FlydigiCoolerDeviceInfo
                {
                    DevicePath = target.DevicePath,
                    ProductName = productName,
                    Manufacturer = manufacturer,
                    SerialNumber = serial,
                    ProductId = productId
                };

                // Cache device path for auto-reconnect
                _settings.LastDevicePath = devicePath;
                SaveSettings();

                // Stop any background reconnection loop — we're connected now.
                StopReconnectLoop();

                // Restart the device monitor (stopped when reconnect loop started)
                // so it can detect future plug/unplug transitions while connected.
                StartDeviceMonitor();

                // Start health check if not already running
                StartHealthCheck();

                // Start background read loop
                StartReadLoop();

                // Query device to initialize communication and trigger 0xEF status notifications.
                // QueryConfigSnapshot (0x04) tells the device to start pushing periodic status updates.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        // QueryConfigSnapshot triggers the device to begin sending 0xEF notifications
                        bool snapshotSent = WriteReport(Bs2ProFrame.Build(Bs2ProCommand.QueryConfigSnapshot));
                        if (!snapshotSent)
                        {
                            RaiseStatus("Failed to send config snapshot query");
                        }
                        await Task.Delay(80, CancellationToken.None);

                        await QueryDeviceSettingsAsync();
                    }
                    catch (Exception)
                    {
                        // Non-critical: device may not respond to queries
                    }
                });

                RaiseConnected(true);
                RaiseStatus($"Connected to {ConnectedDeviceInfo.ModelName}");

                return true;
            }
            catch (Exception ex)
            {
                RaiseStatus($"Connection failed: {ex.Message}");
                _device = null;
                return false;
            }
        }

        /// <summary>
        /// Disconnects from the current device, stopping the read loop.
        /// </summary>
        public void DisconnectAsync()
        {
            StopReadLoop();

            if (_device != null)
            {
                try
                {
                    // Tell the device to exit realtime mode so it reverts to its default/manual
                    // fan state instead of staying locked in software-controlled mode after
                    // the app closes. This is a best-effort write — if the device is already
                    // unreachable the write will fail silently.
                    WriteReport(Bs2ProFrame.Build(Bs2ProCommand.ExitRealtimeMode));
                }
                catch { /* Non-critical: exit command may fail if device is already gone */ }

                try
                {
                    _device.CloseDevice();
                }
                catch (Exception) { /* ignore close errors */ }

                _device = null;
            }

            ConnectedDeviceInfo = null;
            ResetRealtimeControlState();
            FanRpmData = null;
            _lastKnownCurrentRpm = 0;
            _lastKnownTargetRpm = 0;
            RaiseConnected(false);
            RaiseStatus("Disconnected");

            // Start background reconnection loop if this wasn't a user-initiated disconnect.
            // This handles cases where the HID arrival callback doesn't fire
            // (e.g., Bluetooth off→on, device reconnects through Windows settings).
            if (!_userDisconnected && _settings.AutoConnect)
            {
                _connectGeneration++; // Invalidate any in-flight connection attempt
                // Stop the device monitor to avoid two threads doing HID handle operations.
                // The reconnect loop handles device discovery with the same capability.
                StopDeviceMonitor();
                StartReconnectLoop();
            }
        }

        /// <summary>
        /// Disconnects from the device and marks the session as user-disconnected.
        /// This prevents all auto-reconnect logic (startup, HID arrival, device monitor)
        /// from reconnecting until the user explicitly clicks Connect again.
        /// </summary>
        public void DisconnectByUser()
        {
            _userDisconnected = true;
            _connectGeneration++; // Invalidate any in-flight reconnect
            DisconnectAsync();
        }

        /// <summary>
        /// Clears the user-disconnected flag. Called when the user clicks Connect.
        /// </summary>
        private void ClearUserDisconnected()
        {
            _userDisconnected = false;
        }

        /// <summary>
        /// Attempts to reconnect to the last-known device path.
        /// Uses exponential backoff retry (up to 3 attempts).
        /// </summary>
        public async Task<bool> TryAutoConnectAsync()
        {
            if (!_settings.AutoConnect || string.IsNullOrEmpty(_settings.LastDevicePath) || _userDisconnected)
                return false;

            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    var success = await ConnectAsync(_settings.LastDevicePath);
                    if (success)
                        return true;
                }
                catch (Exception)
                {
                    // Retry after backoff
                }

                if (attempt < 2)
                {
                    int delay = 1000 * (attempt + 1); // 1s, 2s
                    await Task.Delay(delay, CancellationToken.None);
                }
            }

            return false;
        }

        #endregion

        #region Background Read Loop

        private void StartReadLoop()
        {
            _readCts = new CancellationTokenSource();
            var token = _readCts.Token;

            _readTask = Task.Run(() =>
            {
                int consecutiveErrors = 0;
                const int maxConsecutiveErrors = 10; // 10 errors × 200ms = ~2s detection

                while (!token.IsCancellationRequested && _device != null && _device.IsOpen)
                {
                    try
                    {
                        var data = _device.Read(200); // 200ms timeout

                        if (data.Status == HidDeviceData.ReadStatus.Success)
                        {
                            if (data.Data != null && data.Data.Length > 0)
                            {
                                OnInputReport(data.Data);
                            }
                            // Success with empty data = timeout (normal idle behavior).
                            // Success with data = actual report. Either way, reset errors.
                            consecutiveErrors = 0;
                        }
                        else
                        {
                            // Error status (device removed, handle invalid, etc.) — count as error.
                            consecutiveErrors++;
                            if (consecutiveErrors >= maxConsecutiveErrors)
                            {
                                break;
                            }
                        }
                    }
                    catch (ObjectDisposedException)
                    {
                        // Device was closed, exit loop
                        break;
                    }
                    catch (Exception)
                    {
                        consecutiveErrors++;
                        if (consecutiveErrors >= maxConsecutiveErrors)
                        {
                            // Device likely disconnected (e.g., user disconnected via Windows Settings)
                            // Break out and let the cleanup below handle it.
                            break;
                        }
                        Thread.Sleep(50);
                    }
                }

                // If the read loop exited unexpectedly (not from StopReadLoop cancellation
                // and not from ObjectDisposedException), the device was disconnected at the OS level.
                // Clean up and fire disconnect event so the UI updates instantly.
                if (!token.IsCancellationRequested && consecutiveErrors >= maxConsecutiveErrors)
                {
                    DisconnectAsync();
                }
            }, token);
        }

        private void StopReadLoop()
        {
            try
            {
                _readCts?.Cancel();
            }
            catch (Exception) { /* ignore cancellation errors */ }

            // Wait for the read loop to exit before closing the device handle.
            // The read uses a 200ms timeout, so this should complete quickly.
            // We allow up to 2 seconds to avoid hanging indefinitely.
            _readTask?.Wait(2000);

            _readCts?.Dispose();
            _readCts = null;
            _readTask = null;
        }

        private void OnInputReport(byte[] data)
        {
            var parsed = Bs2ProFrame.Parse(data);
            if (parsed == null)
                return;

            if (!parsed.Value.ChecksumValid)
                return;

            // Route to status handler for 0xEF notifications
            if (parsed.Value.Command == Bs2ProCommand.StatusNotify)
            {
                var status = Bs2ProStatusParser.Parse(parsed.Value.Payload);
                if (status.HasValue)
                {
                    var statusNotif = status.Value;
                    StatusReceived?.Invoke(this, statusNotif);

                    // If the device reports it's no longer in realtime mode (e.g., physical
                    // button press switched to gear mode), reset our tracking state.
                    if (_realtimeMode && !statusNotif.IsRealtimeMode)
                    {
                        ResetRealtimeControlState();
                    }

                    // Raise FanDataReceived with current RPM from device notification
                    RaiseFanData(new FanRpmData
                    {
                        CurrentRpm = statusNotif.CurrentRpm,
                        TargetRpm = statusNotif.TargetRpm,
                        Mode = statusNotif.IsRealtimeMode ? "Realtime" : "Gear"
                    });
                }
            }
            else
            {
                // Try to deliver to a pending query
                TryDeliverQueryResponse(parsed.Value);
            }
        }

        #endregion

        #region HID Write with Throttling

        /// <summary>
        /// Writes a HID output report to the device with write throttling (80ms minimum gap).
        /// </summary>
        private bool WriteReport(byte[] frame)
        {
            lock (_writeLock)
            {
                if (_device == null || !_device.IsOpen)
                    return false;

                // Enforce 80ms minimum write gap to prevent HID buffer overflow
                var elapsed = (DateTime.UtcNow - _lastWriteTime).TotalMilliseconds;
                if (elapsed < 80)
                {
                    Thread.Sleep((int)(80 - elapsed));
                }

                var report = Bs2ProFrame.BuildReport(frame, Bs2ProReportLength.Control);

                try
                {
                    bool success = _device.Write(report);
                    if (success)
                        _lastWriteTime = DateTime.UtcNow;
                    return success;
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// Writes a HID output report without throttling. Used for bulk RGB uploads
        /// that require rapid sequential writes. The caller is responsible for spacing.
        /// </summary>
        private bool WriteReportUnthrottled(byte[] frame, int reportLength = Bs2ProReportLength.Control)
        {
            lock (_writeLock)
            {
                if (_device == null || !_device.IsOpen)
                    return false;

                var report = Bs2ProFrame.BuildReport(frame, reportLength);

                try
                {
                    bool success = _device.Write(report);
                    if (success)
                        _lastWriteTime = DateTime.UtcNow;
                    return success;
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        #endregion

        #region RGB LED Control

        /// <summary>
        /// Turns off the RGB LED strip.
        /// Sends: 0x46 00
        /// </summary>
        public async Task<bool> WriteRgbOffAsync()
        {
            if (!IsConnected)
            {
                RaiseStatus("RGB Off: not connected");
                return false;
            }

            bool success = WriteReportUnthrottled(Bs2ProFrame.Build(Bs2ProCommand.RgbEnable, Bs2ProRgbEnable.Off));
            if (!success)
                RaiseStatus("RGB Off: write failed");
            await Task.CompletedTask;
            return success;
        }

        /// <summary>
        /// Activates smart-temp mode (temperature-reactive lighting, no frame upload).
        /// Sequence: 0x46 01 ×2 → 0x45 02 → 0x45 03 01 → 0x41 02 → 0x41 03 01 → 0x44 01 → 0x43 01
        /// The upload init handshake (0x41) is required before the device accepts 0x44 mode activation.
        /// </summary>
        public async Task<bool> WriteRgbSmartTempAsync()
        {
            if (!IsConnected)
            {
                RaiseStatus("RGB Smart-Temp: not connected");
                return false;
            }

            bool ok = true;

            // Enable dynamic lighting (×2)
            ok &= WriteReportUnthrottled(Bs2ProFrame.Build(Bs2ProCommand.RgbEnable, Bs2ProRgbEnable.On));
            await Task.Delay(5, CancellationToken.None);
            ok &= WriteReportUnthrottled(Bs2ProFrame.Build(Bs2ProCommand.RgbEnable, Bs2ProRgbEnable.On));
            await Task.Delay(5, CancellationToken.None);

            // Heartbeat
            ok &= WriteReportUnthrottled(Bs2ProFrame.Build(Bs2ProCommand.RgbStatus, Bs2ProRgbHandshake.Heartbeat));
            await Task.Delay(5, CancellationToken.None);
            ok &= WriteReportUnthrottled(Bs2ProFrame.Build(Bs2ProCommand.RgbStatus, Bs2ProRgbHandshake.HeartbeatAck, Bs2ProRgbHandshake.AckParam));
            await Task.Delay(5, CancellationToken.None);

            // Upload init handshake (required before 0x44 mode activation)
            ok &= WriteReportUnthrottled(Bs2ProFrame.Build(Bs2ProCommand.RgbUploadInit, Bs2ProRgbUpload.Init));
            await Task.Delay(5, CancellationToken.None);
            ok &= WriteReportUnthrottled(Bs2ProFrame.Build(Bs2ProCommand.RgbUploadInit, Bs2ProRgbUpload.Confirm, Bs2ProRgbUpload.ConfirmParam));
            await Task.Delay(5, CancellationToken.None);

            // Activate smart-temp mode
            ok &= WriteReportUnthrottled(Bs2ProFrame.Build(Bs2ProCommand.RgbDynamicMode, Bs2ProRgbDynamicMode.SmartTemp));
            await Task.Delay(5, CancellationToken.None);

            // Commit
            ok &= WriteReportUnthrottled(Bs2ProFrame.Build(Bs2ProCommand.RgbCommit, Bs2ProRgbCommit.Apply));

            return ok;
        }

        /// <summary>
        /// Uploads a full 30-frame RGB animation to the device.
        /// Supports: static, rotation, flowing, breathing.
        ///
        /// Upload sequence:
        ///   1. Handshake: 0x46 01 ×2 → 0x45 02 → 0x45 03 01 → 0x41 02 → 0x41 03 01
        ///   2. Header frame (0x47 with f0 data)
        ///   3. 30 animation frames (0x47 with frame data, 1ms between)
        ///   4. 0x43 01 (commit/apply)
        /// </summary>
        private async Task<bool> WriteRgbAnimationAsync(
            string mode, byte r, byte g, byte b, byte speed, byte brightness)
        {
            if (!IsConnected)
            {
                RaiseStatus("RGB: not connected");
                return false;
            }

            var upload = Bs2ProRgbFrameGenerator.Generate(mode, r, g, b, speed, brightness);
            return await WriteRgbUploadAsync(upload);
        }

        /// <summary>
        /// Uploads pre-generated LightUploadData to the device.
        /// Handshake → header frame → 30 animation frames → commit.
        /// </summary>
        private async Task<bool> WriteRgbUploadAsync(LightUploadData upload)
        {
            // Handshake
            bool ok = true;
            ok &= WriteReportUnthrottled(Bs2ProFrame.Build(Bs2ProCommand.RgbEnable, Bs2ProRgbEnable.On));
            await Task.Delay(5, CancellationToken.None);
            ok &= WriteReportUnthrottled(Bs2ProFrame.Build(Bs2ProCommand.RgbEnable, Bs2ProRgbEnable.On));
            await Task.Delay(5, CancellationToken.None);
            ok &= WriteReportUnthrottled(Bs2ProFrame.Build(Bs2ProCommand.RgbStatus, Bs2ProRgbHandshake.Heartbeat));
            await Task.Delay(5, CancellationToken.None);
            ok &= WriteReportUnthrottled(Bs2ProFrame.Build(Bs2ProCommand.RgbStatus, Bs2ProRgbHandshake.HeartbeatAck, Bs2ProRgbHandshake.AckParam));
            await Task.Delay(5, CancellationToken.None);
            ok &= WriteReportUnthrottled(Bs2ProFrame.Build(Bs2ProCommand.RgbUploadInit, Bs2ProRgbUpload.Init));
            await Task.Delay(5, CancellationToken.None);
            ok &= WriteReportUnthrottled(Bs2ProFrame.Build(Bs2ProCommand.RgbUploadInit, Bs2ProRgbUpload.Confirm, Bs2ProRgbUpload.ConfirmParam));
            await Task.Delay(5, CancellationToken.None);

            if (!ok)
            {
                RaiseStatus("RGB: handshake failed");
                return false;
            }

            // Header frame (frame index 0)
            var headerPayload = new byte[11];
            headerPayload[0] = Bs2ProRgbFrameIndex.Header;
            Array.Copy(upload.Header, 0, headerPayload, 1, 10);
            ok &= WriteReportUnthrottled(Bs2ProFrame.Build(Bs2ProCommand.RgbFrameWrite, headerPayload), Bs2ProReportLength.Light);
            await Task.Delay(1, CancellationToken.None);

            // 30 animation frames (frame indices 1-30)
            for (int fi = 0; fi < 30; fi++)
            {
                var framePayload = new byte[11];
                framePayload[0] = (byte)(fi + 1);
                for (int j = 0; j < 10; j++)
                    framePayload[1 + j] = upload.Frames[fi, j];

                ok &= WriteReportUnthrottled(Bs2ProFrame.Build(Bs2ProCommand.RgbFrameWrite, framePayload), Bs2ProReportLength.Light);
                await Task.Delay(1, CancellationToken.None);
            }

            // Commit
            ok &= WriteReportUnthrottled(Bs2ProFrame.Build(Bs2ProCommand.RgbCommit, Bs2ProRgbCommit.Apply));

            if (!ok)
                RaiseStatus("RGB: write failed during upload");

            return ok;
        }

        /// <summary>
        /// Sets the RGB strip to a static color.
        /// </summary>
        public Task<bool> WriteRgbStaticAsync(byte r, byte g, byte b, byte brightness = 100)
            => WriteRgbAnimationAsync("static", r, g, b, Bs2ProRgbSpeed.Medium, brightness);

        /// <summary>
        /// Sets the RGB strip to a rotation animation.
        /// </summary>
        public async Task<bool> WriteRgbRotationAsync(byte r, byte g, byte b, string speed = "Medium", byte brightness = 100)
        {
            byte speedVal = speed switch
            {
                "Fast" => Bs2ProRgbSpeed.Fast,
                "Slow" => Bs2ProRgbSpeed.Slow,
                _ => Bs2ProRgbSpeed.Medium
            };
            return await WriteRgbAnimationAsync("rotation", r, g, b, speedVal, brightness);
        }

        /// <summary>
        /// Sets the RGB strip to a rotation animation with multiple colors (1-6).
        /// </summary>
        public async Task<bool> WriteRgbRotationMultiAsync(
            System.Collections.Generic.IEnumerable<System.Windows.Media.Color> colors, string speed, byte brightness)
        {
            if (!IsConnected)
            {
                RaiseStatus("RGB: not connected");
                return false;
            }

            byte speedVal = speed switch
            {
                "Fast" => Bs2ProRgbSpeed.Fast,
                "Slow" => Bs2ProRgbSpeed.Slow,
                _ => Bs2ProRgbSpeed.Medium
            };

            // Generate frame data with multi-color
            var upload = Bs2ProRgbFrameGenerator.GenerateRotation(
                new System.Collections.Generic.List<System.Windows.Media.Color>(colors), speedVal, brightness);

            return await WriteRgbUploadAsync(upload);
        }

        /// <summary>
        /// Sets the RGB strip to a flowing animation.
        /// Color is ignored (flowing uses a fixed green base).
        /// </summary>
        public async Task<bool> WriteRgbFlowingAsync(string speed = "Medium", byte brightness = 100)
        {
            byte speedVal = speed switch
            {
                "Fast" => Bs2ProRgbSpeed.Fast,
                "Slow" => Bs2ProRgbSpeed.Slow,
                _ => Bs2ProRgbSpeed.Medium
            };
            return await WriteRgbAnimationAsync("flowing", 0, 255, 0, speedVal, brightness);
        }

        /// <summary>
        /// Sets the RGB strip to a breathing animation.
        /// </summary>
        public Task<bool> WriteRgbBreathingAsync(byte r, byte g, byte b, byte brightness = 100)
            => WriteRgbAnimationAsync("breathing", r, g, b, Bs2ProRgbSpeed.Medium, brightness);

        #endregion

        #region Event Helpers

        private void RaiseConnected(bool connected)
        {
            ConnectionStateChanged?.Invoke(this, connected);
        }

        private void RaiseStatus(string message)
        {
            StatusChanged?.Invoke(this, message);
        }

        #endregion

        #region Device Settings

        /// <summary>
        /// Sets the Power-On Start toggle.
        /// When enabled, the fan starts automatically when the device is powered on.
        /// Sends: 0x0C with payload 01 (on) or 02 (off).
        /// </summary>
        public async Task<bool> WritePowerOnStartAsync(bool enabled)
        {
            if (!IsConnected)
                return false;

            byte payload = enabled ? Bs2ProPowerOnStart.Enabled : Bs2ProPowerOnStart.Disabled;
            bool success = WriteReport(Bs2ProFrame.Build(Bs2ProCommand.PowerOnStart, payload));

            if (success)
            {
                _settings.PowerOnStart = enabled;
                SaveSettings();
            }

            await Task.CompletedTask;
            return success;
        }

        /// <summary>
        /// Sets the Smart Start/Stop mode.
        /// 0 = Off, 1 = Immediate start/stop, 2 = Delayed start/stop.
        /// Sends: 0x0D with mode byte.
        /// </summary>
        public async Task<bool> WriteSmartStartStopAsync(byte mode)
        {
            if (!IsConnected)
                return false;

            if (mode > Bs2ProSmartStartStop.Delayed)
            {
                RaiseStatus($"Invalid smart start/stop mode: {mode} (must be 0-2)");
                return false;
            }

            bool success = WriteReport(Bs2ProFrame.Build(Bs2ProCommand.SmartStartStop, mode));

            if (success)
            {
                _settings.SmartStartStopMode = mode;
                SaveSettings();
            }

            await Task.CompletedTask;
            return success;
        }

        /// <summary>
        /// Sets the gear indicator light toggle.
        /// Sends: 0x48 with payload 01 (on) or 00 (off).
        /// </summary>
        public async Task<bool> WriteGearLightAsync(bool enabled)
        {
            if (!IsConnected)
                return false;

            byte payload = enabled ? Bs2ProGearLight.On : Bs2ProGearLight.Off;
            bool success = WriteReport(Bs2ProFrame.Build(Bs2ProCommand.GearLight, payload));

            if (success)
            {
                _settings.GearLightEnabled = enabled;
                SaveSettings();
            }

            await Task.CompletedTask;
            return success;
        }

        /// <summary>
        /// Queries the device for its current settings: work mode and gear RPM table.
        /// Sends 0x25 (query work mode) and 0x27 (query gear table), then waits for responses.
        /// </summary>
        public async Task<Bs2ProDeviceSettings> QueryDeviceSettingsAsync()
        {
            var settings = new Bs2ProDeviceSettings
            {
                PowerOnStart = _settings.PowerOnStart,
                SmartStartStopMode = _settings.SmartStartStopMode,
                GearLightEnabled = _settings.GearLightEnabled
            };

            // Query work mode (0x25)
            var workModeResponse = await SendQueryAsync(Bs2ProCommand.QueryWorkMode);
            if (workModeResponse.HasValue && workModeResponse.Value.Payload.Length >= 1)
            {
                settings.WorkMode = workModeResponse.Value.Payload[0];
            }

            await Task.Delay(80, CancellationToken.None); // spacing between queries

            // Query gear RPM table (0x27)
            var gearTableResponse = await SendQueryAsync(Bs2ProCommand.QueryGearTable);
            if (gearTableResponse.HasValue)
            {
                var table = Bs2ProGearTableParser.Parse(gearTableResponse.Value.Payload);
                if (table != null)
                {
                    settings.GearRpmTable = table;
                }
            }

            DeviceSettings = settings;
            DeviceSettingsUpdated?.Invoke(this, settings);

            return settings;
        }

        #endregion

        #region Fan Speed Control

        /// <summary>
        /// Validates that an RPM value is within the hardware-accepted range (1300-4000).
        /// RPM of 0 is also accepted (means "turn fan off").
        /// </summary>
        private static bool IsValidRpm(ushort rpm)
        {
            return rpm == 0 || (rpm >= Bs2ProDefaultGearRpm.MinRpm && rpm <= Bs2ProDefaultGearRpm.MaxRpm);
        }

        /// <summary>
        /// Resets the realtime mode tracking state.  Called after gear preset commands
        /// or when the device reports it has left realtime mode.
        /// </summary>
        private void ResetRealtimeControlState()
        {
            _realtimeMode = false;
            _hasCommandedRpm = false;
            _lastCommandedRpm = 0;
        }

        /// <summary>
        /// Clears all cached RPM values on disconnect so the UI resets to "--".
        /// </summary>
        public void ResetCachedRpm()
        {
            _lastKnownCurrentRpm = 0;
            _lastKnownTargetRpm = 0;
        }

        /// <summary>
        /// Sets the fan to a preset gear mode (1=Quiet, 2=Standard, 3=Strong, 4=Overclock).
        /// This is a single-frame write with no mode handshake required.
        /// </summary>
        public async Task<bool> WriteGearAsync(byte gear)
        {
            if (!IsConnected)
                return false;

            if (gear < Bs2ProGearMode.Quiet || gear > Bs2ProGearMode.Overclock)
            {
                RaiseStatus($"Invalid gear: {gear} (must be 1-4)");
                return false;
            }

            bool success = WriteReport(Bs2ProFrame.Build(Bs2ProCommand.GearMode, gear));

            if (success)
            {
                ResetRealtimeControlState();
                RaiseFanData(new FanRpmData { TargetRpm = 0, Mode = "Gear" });
            }

            await Task.CompletedTask;
            return success;
        }

        /// <summary>
        /// Sets a custom RPM for a specific gear index (0-3).
        /// Uses the 0x26 command: 5A A5 26 05 &lt;gear_idx&gt; &lt;rpm_lo&gt; &lt;rpm_hi&gt; &lt;checksum&gt;.
        /// </summary>
        /// <param name="gearIndex">Gear index 0=Quiet, 1=Standard, 2=Strong, 3=Overclock.</param>
        /// <param name="rpm">Target RPM (validated against 1300-4000 range).</param>
        public async Task<bool> WriteGearRpmAsync(byte gearIndex, ushort rpm)
        {
            if (!IsConnected)
                return false;

            if (gearIndex > 3)
            {
                RaiseStatus($"Invalid gear index: {gearIndex} (must be 0-3)");
                return false;
            }

            if (!IsValidRpm(rpm))
            {
                RaiseStatus($"RPM {rpm} out of range ({Bs2ProDefaultGearRpm.MinRpm}-{Bs2ProDefaultGearRpm.MaxRpm})");
                return false;
            }

            byte rpmLo = (byte)(rpm & 0xFF);
            byte rpmHi = (byte)((rpm >> 8) & 0xFF);

            bool success = WriteReport(Bs2ProFrame.Build(Bs2ProCommand.SetGearRpm, gearIndex, rpmLo, rpmHi));

            if (success)
            {
                ResetRealtimeControlState();
                RaiseFanData(new FanRpmData { TargetRpm = rpm, Mode = "Gear" });
            }

            await Task.CompletedTask;
            return success;
        }

        /// <summary>
        /// Sets the fan to an arbitrary RPM using realtime mode.
        /// Protocol sequence (first call or after mode reset):
        ///   1. 0x23 (enter realtime mode) → 50ms delay
        ///   2. 0x21 rpm_lo rpm_hi (set target RPM)
        ///
        /// Subsequent calls skip step 1 if already in realtime mode.
        /// RPM of 0 turns the fan off and exits realtime mode:
        ///   1. 0x23 (if needed) → 50ms
        ///   2. 0x21 00 00 → 50ms
        ///   3. 0x24 (exit realtime mode)
        /// </summary>
        /// <param name="rpm">Target RPM (0 = off).</param>
        /// <param name="force">If true, sends the command even if RPM hasn't changed.
        /// Use for keepalive to prevent the device from drifting out of realtime mode.</param>
        public async Task<bool> WriteRealtimeRpmAsync(ushort rpm, bool force = false)
        {
            if (!IsConnected)
                return false;

            if (!IsValidRpm(rpm))
            {
                RaiseStatus($"RPM {rpm} out of range (0 or {Bs2ProDefaultGearRpm.MinRpm}-{Bs2ProDefaultGearRpm.MaxRpm})");
                return false;
            }

            // Skip redundant writes when RPM hasn't changed and we're already in realtime mode.
            // Bypass when force=true (keepalive) to prevent the device from drifting.
            if (!force && _hasCommandedRpm && _lastCommandedRpm == rpm && _realtimeMode)
                return true;

            bool success;

            if (rpm == 0)
            {
                // Turn fan off: enter mode → set 0 RPM → exit mode
                if (!_realtimeMode)
                {
                    success = WriteReport(Bs2ProFrame.Build(Bs2ProCommand.EnterRealtimeMode));
                    if (!success)
                        return false;

                    await Task.Delay(50, CancellationToken.None);
                    _realtimeMode = true;
                }

                success = WriteReport(Bs2ProFrame.Build(
                    Bs2ProCommand.RealtimeRpm, (byte)(rpm & 0xFF), (byte)((rpm >> 8) & 0xFF)));
                if (!success)
                {
                    // Write failed, force fresh handshake on retry
                    ResetRealtimeControlState();
                    return false;
                }

                await Task.Delay(50, CancellationToken.None);

                // Exit realtime mode to stop fan control session
                success = WriteReport(Bs2ProFrame.Build(Bs2ProCommand.ExitRealtimeMode));
                if (success)
                {
                    ResetRealtimeControlState();
                    _lastCommandedRpm = 0;
                    _hasCommandedRpm = true;
                    RaiseFanData(new FanRpmData { TargetRpm = 0, Mode = "Off" });
                }
            }
            else
            {
                // Set RPM: enter mode if needed, then set target
                if (!_realtimeMode)
                {
                    success = WriteReport(Bs2ProFrame.Build(Bs2ProCommand.EnterRealtimeMode));
                    if (!success)
                        return false;

                    await Task.Delay(50, CancellationToken.None);
                    _realtimeMode = true;
                }

                // Same-value skip: device already at this RPM, no need to resend.
                // Bypass when force=true (keepalive) to prevent the device from drifting.
                if (!force && _hasCommandedRpm && _lastCommandedRpm == rpm)
                    return true;

                byte rpmLo = (byte)(rpm & 0xFF);
                byte rpmHi = (byte)((rpm >> 8) & 0xFF);

                success = WriteReport(Bs2ProFrame.Build(
                    Bs2ProCommand.RealtimeRpm, rpmLo, rpmHi));
                if (!success)
                {
                    // Write failed, force fresh handshake on retry
                    ResetRealtimeControlState();
                    return false;
                }

                _lastCommandedRpm = rpm;
                _hasCommandedRpm = true;
                RaiseFanData(new FanRpmData { TargetRpm = rpm, Mode = "Realtime" });
            }

            return success;
        }

        /// <summary>
        /// Exits realtime mode, returning the device to gear mode.
        /// Sends the 0x24 command.
        /// </summary>
        public async Task<bool> ExitRealtimeModeAsync()
        {
            if (!IsConnected)
                return false;

            bool success = WriteReport(Bs2ProFrame.Build(Bs2ProCommand.ExitRealtimeMode));

            if (success)
            {
                ResetRealtimeControlState();
                RaiseFanData(new FanRpmData { TargetRpm = 0, Mode = "Gear" });
            }

            await Task.CompletedTask;
            return success;
        }

        #endregion

        #region Event Helpers

        private void RaiseFanData(FanRpmData data)
        {
            // Cache real RPM values from device notifications and write commands.
            // When raising updates with missing data, fall back to the last known
            // values so the UI doesn't flicker between a real value and 0/--.
            if (data.CurrentRpm > 0)
                _lastKnownCurrentRpm = data.CurrentRpm;
            else if (_lastKnownCurrentRpm > 0)
                data = new FanRpmData { CurrentRpm = _lastKnownCurrentRpm, TargetRpm = data.TargetRpm, Mode = data.Mode };

            if (data.TargetRpm > 0)
                _lastKnownTargetRpm = data.TargetRpm;
            else if (_lastKnownTargetRpm > 0)
                data = new FanRpmData { CurrentRpm = data.CurrentRpm, TargetRpm = _lastKnownTargetRpm, Mode = data.Mode };

            FanRpmData = data;
            FanDataReceived?.Invoke(this, data);
        }

        #endregion

        #region Power Notification (Suspend/Resume)

        private void StartPowerNotificationMonitor()
        {
            try
            {
                _powerEventHandle = CreateEvent(nint.Zero, 1, 0, null); // manual-reset event
                if (_powerEventHandle == nint.Zero)
                    return;

                _powerNotificationHandle = PowerRegisterSuspendResumeNotification(0, _powerEventHandle);
                if (_powerNotificationHandle == nint.Zero)
                    return; // Registration failed, not a critical error

                _powerMonitorCts = new CancellationTokenSource();
                var token = _powerMonitorCts.Token;

                _powerMonitorThread = new Thread(() =>
                {
                    while (!token.IsCancellationRequested)
                    {
                        int result = WaitForSingleObject(_powerEventHandle, INFINITE);
                        if (result == WAIT_OBJECT_0)
                        {
                            try
                            {
                                _ = HandlePowerEvent();
                            }
                            catch (Exception)
                            {
                                // Non-critical: power event handling failure shouldn't crash the app
                            }
                        }
                    }
                })
                {
                    IsBackground = true,
                    Name = "FlydigiPowerMonitor"
                };
                _powerMonitorThread.Start();
            }
            catch (Exception)
            {
                // Power notifications are optional; don't fail construction
            }
        }

        private void StopPowerNotificationMonitor()
        {
            try
            {
                _powerMonitorCts?.Cancel();
            }
            catch (Exception) { /* ignore */ }

            _powerMonitorThread?.Join(2000);

            try
            {
                if (_powerNotificationHandle != nint.Zero)
                {
                    _ = PowerUnregisterSuspendResumeNotification(0, _powerNotificationHandle);
                    _powerNotificationHandle = nint.Zero;
                }
            }
            catch (Exception) { /* ignore */ }

            try
            {
                if (_powerEventHandle != nint.Zero)
                {
                    _ = CloseHandle(_powerEventHandle);
                    _powerEventHandle = nint.Zero;
                }
            }
            catch (Exception) { /* ignore */ }

            _powerMonitorCts?.Dispose();
            _powerMonitorCts = null;
        }

        private async Task HandlePowerEvent()
        {
            // The event fires for both suspend and resume. We determine which by
            // checking the connection state. If connected → suspend. If disconnected → resume.
            if (IsConnected)
            {
                // System is about to suspend
                try
                {
                    if (_settings.SuspendFanOff)
                    {
                        await WriteRealtimeRpmAsync(0);
                    }
                }
                catch (Exception) { /* non-critical during suspend */ }

                _connectGeneration++; // Invalidate any in-flight reconnect
                DisconnectAsync();
            }
            else
            {
                // System just resumed from suspend
                _ = Task.Run(async () =>
                {
                    await Task.Delay(2000, CancellationToken.None); // Wait for system to stabilize after resume
                    if (_settings.AutoConnect && !_userDisconnected)
                    {
                        _connectGeneration++; // Invalidate stale reconnect from before suspend
                        StopReconnectLoop();
                        await TryAutoConnectAsync();
                    }
                });
            }
        }

        [DllImport("powrprof.dll", SetLastError = true)]
        private static extern int PowerUnregisterSuspendResumeNotification(
            int Flags,
            nint Handle);

        [DllImport("kernel32.dll")]
        private static extern int CloseHandle(nint hHandle);

        #endregion

        #region HID Interface Arrival Notification (CM_Register_Notification)

        /// <summary>
        /// Registers for instant HID device interface arrival notifications.
        /// Uses cfgmgr32.dll CM_Register_Notification (same approach as THRM).
        /// Fires immediately when a Flydigi HID device appears (e.g., after Bluetooth pairing).
        /// </summary>
        private void StartHidArrivalNotification()
        {
            try
            {
                var filter = new CmNotifyFilter
                {
                    cbSize = (uint)Marshal.SizeOf(typeof(CmNotifyFilter)),
                    FilterType = (uint)CmNotifyFilterTypeDeviceInterface,
                    ClassGuid = HidDeviceInterfaceGuid,
                    UnionTail = new byte[384] // 400 - 16 header bytes
                };

                _hidArrivalCallback = HidArrivalCallback;
                int result = CM_Register_Notification(ref filter, nint.Zero, _hidArrivalCallback, out _hidArrivalHandle);

                if (result == 0) // CR_SUCCESS
                {
                    System.Diagnostics.Debug.WriteLine("[FlydigiCoolerHidService] HID arrival notification registered");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[FlydigiCoolerHidService] HID arrival notification failed: {result}");
                }
            }
            catch (Exception ex)
            {
                // Non-critical: fall back to the polling device monitor
                System.Diagnostics.Debug.WriteLine($"[FlydigiCoolerHidService] HID arrival notification error: {ex.Message}");
            }
        }

        private void StopHidArrivalNotification()
        {
            if (_hidArrivalHandle != nint.Zero)
            {
                try
                {
                    CM_UnregisterNotification(_hidArrivalHandle);
                }
                catch { /* Non-critical: cleanup during teardown */ }
                _hidArrivalHandle = nint.Zero;
            }
            _hidArrivalCallback = null;
        }

        private void HidArrivalCallback(nint context, uint notificationType, nint eventData, uint eventDataSize)
        {
            try
            {
                // Handle removal: instantly disconnect if a Flydigi HID interface disappears
                if (notificationType == CmNotifyActionInterfaceRemoval)
                {
                    if (IsConnected)
                    {
                        System.Diagnostics.Debug.WriteLine("[FlydigiCoolerHidService] Flydigi HID device removed");
                        DisconnectAsync();
                    }
                    return;
                }

                // Handle arrival
                if (notificationType != CmNotifyActionInterfaceArrival || eventData == nint.Zero)
                    return;

                // Extract the symbolic link path from the event data (offset 24, UTF-16)
                if (eventDataSize <= CmNotifyEventSymbolicLinkOffset)
                    return;

                int length = (int)(eventDataSize - CmNotifyEventSymbolicLinkOffset) / 2;
                string path = Marshal.PtrToStringUni(eventData + CmNotifyEventSymbolicLinkOffset, length) ?? string.Empty;

                if (string.IsNullOrEmpty(path))
                    return;

                // Check if this is a Flydigi device
                string lowerPath = path.ToLowerInvariant();
                foreach (var identifier in FlydigiHidIdentifiers)
                {
                    if (lowerPath.Contains(identifier))
                    {
                        System.Diagnostics.Debug.WriteLine($"[FlydigiCoolerHidService] Flydigi HID device arrived: {path}");
                        FlydigiHardwareDetector.InvalidateCache();

                        // Auto-connect if not already connected and user didn't explicitly disconnect.
                        // Use the device path from the event data (fresh path from Windows),
                        // not the saved LastDevicePath which may be stale after a reconnect.
                        if (!IsConnected && !_userDisconnected && _settings.AutoConnect)
                        {
                            // If a reconnect loop is already running, wake it to skip the
                            // current backoff delay. Otherwise, start a fresh reconnect with
                            // 0 initial delay (THRM's approach for HID arrival).
                            bool woke = WakeReconnect();
                            if (!woke)
                            {
                                // No reconnect loop running — start one with immediate attempt
                                _connectGeneration++;
                                StartReconnectLoop();
                            }

                            // Also try a direct connect in case the reconnect loop hasn't
                            // started yet or is in a backoff delay. This handles the race
                            // where HID arrival fires before the reconnect loop is alive.
                            _ = Task.Run(async () =>
                            {
                                await Task.Delay(1000, CancellationToken.None); // Wait for HID to stabilize
                                if (IsConnected || _userDisconnected)
                                    return;
                                bool ok = await ConnectAsync(path);
                                if (ok)
                                {
                                    System.Diagnostics.Debug.WriteLine(
                                        $"[FlydigiCoolerHidService] Auto-connected via HID arrival: {path}");
                                }
                            });
                        }
                        return;
                    }
                }
            }
            catch { /* Non-critical: callback errors shouldn't crash the app */ }
        }

        #endregion

        #region Device Plug/Unplug Monitor

        private void StartDeviceMonitor()
        {
            // Guard against double-start
            if (_deviceMonitorCts != null)
                return;

            try
            {
                _deviceMonitorCts = new CancellationTokenSource();
                var token = _deviceMonitorCts.Token;

                // Initial detection state
                _lastDeviceDetected = IsDeviceAvailable();

                _deviceMonitorThread = new Thread(() =>
                {
                    while (!token.IsCancellationRequested)
                    {
                        // Poll every 5 seconds
                        for (int i = 0; i < 50; i++)
                        {
                            if (token.IsCancellationRequested)
                                return;
                            Thread.Sleep(100);
                        }

                        try
                        {
                            // Skip polling when already connected — DiscoverDevices() tries to
                            // open the HID handle which conflicts with our own open handle,
                            // causing a false "device unplugged" detection and a connect/disconnect loop.
                            bool currentlyDetected = IsConnected || IsDeviceAvailable();

                            if (currentlyDetected && !_lastDeviceDetected)
                            {
                                // Device just plugged in
                                _lastDeviceDetected = true;
                                FlydigiHardwareDetector.InvalidateCache();

                                if (!_userDisconnected && _settings.AutoConnect && !IsConnected)
                                {
                                    // Discover fresh devices to get the current device path
                                    // (the saved LastDevicePath may be stale after a reconnect)
                                    var freshDevices = DiscoverDevices();
                                    if (freshDevices.Count > 0)
                                    {
                                        var freshPath = freshDevices[0].DevicePath;
                                        _ = Task.Run(async () =>
                                        {
                                            await Task.Delay(500, CancellationToken.None);
                                            bool ok = await ConnectAsync(freshPath);
                                            if (ok)
                                                System.Diagnostics.Debug.WriteLine($"[FlydigiCoolerHidService] Auto-connected via device monitor: {freshPath}");
                                        });
                                    }
                                }
                            }
                            else if (!currentlyDetected && _lastDeviceDetected)
                            {
                                // Device just unplugged
                                _lastDeviceDetected = false;
                                FlydigiHardwareDetector.InvalidateCache();

                                if (IsConnected)
                                {
                                    DisconnectAsync();
                                    RaiseStatus("Device unplugged");

                                    // Attempt reconnect with exponential backoff in case of hotplug re-enumeration
                                    _ = Task.Run(async () => await ReconnectWithBackoffAsync(token));
                                }
                            }
                        }
                        catch (Exception)
                        {
                            // Non-critical: monitoring failures shouldn't crash the app
                        }
                    }
                })
                {
                    IsBackground = true,
                    Name = "FlydigiDeviceMonitor"
                };
                _deviceMonitorThread.Start();
            }
            catch (Exception)
            {
                // Device monitoring is optional
            }
        }

        private async Task ReconnectWithBackoffAsync(CancellationToken token)
        {
            int[] delays = { 1000, 2000, 4000, 8000, 15000 };
            for (int i = 0; i < delays.Length; i++)
            {
                if (token.IsCancellationRequested)
                    return;
                if (IsConnected)
                    return; // Successfully reconnected

                // Wait for device to appear
                for (int j = 0; j < 10; j++)
                {
                    if (token.IsCancellationRequested)
                        return;
                    if (IsDeviceAvailable())
                        break;
                    Thread.Sleep(100);
                }

                if (!IsDeviceAvailable())
                {
                    // Device still not present, wait for backoff delay
                    Thread.Sleep(delays[i]);
                    continue;
                }

                // Device is present, try to connect
                await TryAutoConnectAsync();
            }
        }

        private void StopDeviceMonitor()
        {
            try
            {
                _deviceMonitorCts?.Cancel();
            }
            catch (Exception) { /* ignore */ }

            _deviceMonitorThread?.Join(2000);

            _deviceMonitorCts?.Dispose();
            _deviceMonitorCts = null;
        }

        #endregion

        #region Background Reconnect Loop (THRM-style exponential backoff)

        /// <summary>
        /// Exponential backoff delays for reconnect attempts (matches THRM).
        /// 2s, 5s, 10s, 15s, 20s, 30s, 30s, 60s, 60s, 60s.
        /// After exhausting all delays, the loop exits and the health check
        /// (running every 30s) will trigger a new round if still disconnected.
        /// </summary>
        private static readonly int[] ReconnectDelaysMs =
        {
            2000, 5000, 10000, 15000, 20000,
            30000, 30000, 60000, 60000, 60000
        };

        /// <summary>
        /// Starts a reconnect thread that runs exponential backoff attempts.
        /// HID arrival can wake the backoff early via WakeReconnect().
        /// After all delays are exhausted, the thread exits — the health check
        /// will restart a new round if the device is still disconnected.
        /// </summary>
        private void StartReconnectLoop()
        {
            lock (_reconnectLock)
            {
                // Don't start a second loop if one is already running
                if (_reconnectCts != null)
                    return;

                try
                {
                    _reconnectCts = new CancellationTokenSource();
                    _reconnectWakeEvent = new ManualResetEvent(false);
                    var token = _reconnectCts.Token;
                    int generation = _connectGeneration;

                    _reconnectThread = new Thread(() =>
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[FlydigiCoolerHidService] Reconnect loop started (generation={generation}, {ReconnectDelaysMs.Length} delays)");

                        for (int i = 0; i < ReconnectDelaysMs.Length; i++)
                        {
                            if (token.IsCancellationRequested)
                            {
                                System.Diagnostics.Debug.WriteLine("[FlydigiCoolerHidService] Reconnect loop cancelled");
                                return;
                            }

                            if (IsConnected || _userDisconnected)
                            {
                                System.Diagnostics.Debug.WriteLine(
                                    "[FlydigiCoolerHidService] Reconnect loop stopping (already connected or user disconnected)");
                                return;
                            }

                            // Check if a newer generation has started a fresh reconnect
                            if (generation != _connectGeneration)
                            {
                                System.Diagnostics.Debug.WriteLine(
                                    $"[FlydigiCoolerHidService] Reconnect loop stale (generation {generation} vs {_connectGeneration}), stopping");
                                return;
                            }

                            int delayMs = ReconnectDelaysMs[i];
                            if (delayMs > 0)
                            {
                                System.Diagnostics.Debug.WriteLine(
                                    $"[FlydigiCoolerHidService] Waiting {delayMs}ms before reconnect attempt {i + 1}/{ReconnectDelaysMs.Length}");

                                // Wait for delay, cancellation, or wake signal from HID arrival
                                WaitHandle[] waitHandles =
                                {
                                    _reconnectCts.Token.WaitHandle,
                                    _reconnectWakeEvent
                                };
                                int result = WaitHandle.WaitAny(waitHandles, delayMs);

                                if (result == 0) // cancelled
                                {
                                    System.Diagnostics.Debug.WriteLine("[FlydigiCoolerHidService] Reconnect loop cancelled during wait");
                                    return;
                                }
                                // result == 1 means woken by HID arrival — skip to connect attempt immediately
                                // result == 2 means timeout — proceed normally
                            }

                            // Check again before attempting connect
                            if (IsConnected || _userDisconnected || generation != _connectGeneration)
                                return;

                            try
                            {
                                var devices = DiscoverDevices();
                                if (devices.Count > 0)
                                {
                                    var path = devices[0].DevicePath;
                                    System.Diagnostics.Debug.WriteLine(
                                        $"[FlydigiCoolerHidService] Reconnect attempt {i + 1}: {path}");

                                    _settings.LastDevicePath = path;
                                    SaveSettings();

                                    _ = Task.Run(async () =>
                                    {
                                        await Task.Delay(1000, CancellationToken.None);
                                        if (IsConnected || _userDisconnected || generation != _connectGeneration)
                                            return;
                                        bool ok = await ConnectAsync(path);
                                        if (ok)
                                        {
                                            System.Diagnostics.Debug.WriteLine(
                                                $"[FlydigiCoolerHidService] Reconnected on attempt {i + 1}: {path}");
                                        }
                                        else
                                        {
                                            System.Diagnostics.Debug.WriteLine(
                                                $"[FlydigiCoolerHidService] Reconnect attempt {i + 1} failed: {path}");
                                        }
                                    });
                                }
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine(
                                    $"[FlydigiCoolerHidService] Reconnect discovery failed: {ex.Message}");
                            }
                        }

                        System.Diagnostics.Debug.WriteLine(
                            "[FlydigiCoolerHidService] Reconnect loop exhausted all delays, health check will trigger new round");
                    })
                    {
                        IsBackground = true,
                        Name = "FlydigiReconnectLoop"
                    };
                    _reconnectThread.Start();
                }
                catch (Exception)
                {
                    // Non-critical: reconnect loop is optional
                }
            }
        }

        /// <summary>
        /// Stops the background reconnection loop.
        /// </summary>
        private void StopReconnectLoop()
        {
            lock (_reconnectLock)
            {
                try
                {
                    _reconnectCts?.Cancel();
                }
                catch (Exception) { /* ignore */ }

                _reconnectThread?.Join(2000);

                _reconnectCts?.Dispose();
                _reconnectCts = null;
                _reconnectThread = null;
                _reconnectWakeEvent?.Dispose();
                _reconnectWakeEvent = null;
            }
        }

        /// <summary>
        /// Wakes the reconnect loop early when a HID device arrives.
        /// Returns true if a reconnect loop was running and was woken.
        /// </summary>
        private bool WakeReconnect()
        {
            try
            {
                if (_reconnectWakeEvent != null)
                {
                    _reconnectWakeEvent.Set();
                    return true;
                }
            }
            catch { /* Non-critical */ }
            return false;
        }

        #endregion

        #region Health Check (30s interval, triggers reconnect if disconnected)

        /// <summary>
        /// Starts a health check thread that runs every 30 seconds. If the device
        /// is disconnected (and not user-disconnected), it starts a new reconnect
        /// round. This ensures we keep trying even after the exponential backoff
        /// has exhausted all its delays.
        /// </summary>
        private void StartHealthCheck()
        {
            // Don't start a second health check if one is already running
            if (_healthCheckCts != null)
                return;

            try
            {
                _healthCheckCts = new CancellationTokenSource();
                var token = _healthCheckCts.Token;

                _healthCheckThread = new Thread(() =>
                {
                    System.Diagnostics.Debug.WriteLine("[FlydigiCoolerHidService] Health check started (30s interval)");

                    while (!token.IsCancellationRequested)
                    {
                        // Wait 30 seconds
                        for (int i = 0; i < 300; i++)
                        {
                            if (token.IsCancellationRequested)
                                return;
                            Thread.Sleep(100);
                        }

                        try
                        {
                            if (IsConnected || _userDisconnected)
                                continue;

                            System.Diagnostics.Debug.WriteLine(
                                "[FlydigiCoolerHidService] Health check: disconnected, starting reconnect round");
                            StartReconnectLoop();
                        }
                        catch { /* Non-critical */ }
                    }
                })
                {
                    IsBackground = true,
                    Name = "FlydigiHealthCheck"
                };
                _healthCheckThread.Start();
            }
            catch (Exception)
            {
                // Non-critical: health check is optional
            }
        }

        /// <summary>
        /// Stops the health check thread.
        /// </summary>
        private void StopHealthCheck()
        {
            try
            {
                _healthCheckCts?.Cancel();
            }
            catch (Exception) { /* ignore */ }

            _healthCheckThread?.Join(2000);

            _healthCheckCts?.Dispose();
            _healthCheckCts = null;
            _healthCheckThread = null;
        }

        #endregion

        #region Time-Based Curve Schedule

        private void StartTimeScheduleMonitor()
        {
            try
            {
                _scheduleMonitorCts = new CancellationTokenSource();
                var token = _scheduleMonitorCts.Token;

                _scheduleMonitorThread = new Thread(() =>
                {
                    while (!token.IsCancellationRequested)
                    {
                        // Poll every 60 seconds
                        for (int i = 0; i < 600; i++)
                        {
                            if (token.IsCancellationRequested)
                                return;
                            Thread.Sleep(100);
                        }

                        try
                        {
                            EvaluateTimeSchedule();
                        }
                        catch (Exception)
                        {
                            // Non-critical: schedule evaluation failures shouldn't crash the app
                        }
                    }
                })
                {
                    IsBackground = true,
                    Name = "FlydigiScheduleMonitor"
                };
                _scheduleMonitorThread.Start();
            }
            catch (Exception)
            {
                // Schedule monitoring is optional
            }
        }

        private void EvaluateTimeSchedule()
        {
            if (!_settings.TimeScheduleEnabled)
                return;

            if (_settings.TimeScheduleEntries.Count == 0)
                return;

            var now = DateTime.Now.TimeOfDay;
            string? activeProfile = null;

            foreach (var entry in _settings.TimeScheduleEntries)
            {
                if (!TimeSpan.TryParse(entry.StartTime, System.Globalization.CultureInfo.InvariantCulture, out var start) ||
                    !TimeSpan.TryParse(entry.EndTime, System.Globalization.CultureInfo.InvariantCulture, out var end))
                    continue;

                bool inRange;
                if (start <= end)
                {
                    // Normal range (e.g. 06:00 - 22:00)
                    inRange = now >= start && now <= end;
                }
                else
                {
                    // Overnight range (e.g. 22:00 - 06:00)
                    inRange = now >= start || now <= end;
                }

                if (inRange)
                {
                    activeProfile = entry.ProfileName;
                    break;
                }
            }

            // No matching schedule — fall back to default (do nothing, let user control take over)
            if (string.IsNullOrEmpty(activeProfile))
            {
                _lastScheduledProfile = string.Empty;
                return;
            }

            // Only switch if the profile changed
            if (activeProfile == _lastScheduledProfile)
                return;

            _lastScheduledProfile = activeProfile;
            RaiseStatus($"Time schedule: switched to {activeProfile} curve");

            // Apply the curve profile by updating the saved settings.
            // The UI page reads this when smart control is active.
            // We don't push RPM directly here — smart control on the page handles
            // the temperature-to-RPM mapping. We just signal the profile change.
        }

        private void StopScheduleMonitor()
        {
            try
            {
                _scheduleMonitorCts?.Cancel();
            }
            catch (Exception) { /* ignore */ }

            _scheduleMonitorThread?.Join(2000);

            _scheduleMonitorCts?.Dispose();
            _scheduleMonitorCts = null;
        }

        #endregion

        #region IDisposable

        private bool _disposed;

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    StopReconnectLoop();
                    StopHealthCheck();
                    StopScheduleMonitor();
                    StopHidArrivalNotification();
                    StopDeviceMonitor();
                    StopPowerNotificationMonitor();
                    DisconnectAsync();
                }

                _disposed = true;
            }
        }

        #endregion
    }

    /// <summary>
    /// Lightweight fan RPM data snapshot raised by the service.
    /// </summary>
    public readonly struct FanRpmData
    {
        /// <summary>Target RPM commanded by the host. 0 = fan off.</summary>
        public ushort TargetRpm { get; init; }

        /// <summary>Current RPM reported by the device (from 0xEF notifications).</summary>
        public ushort CurrentRpm { get; init; }

        /// <summary>Current mode: "Off", "Gear", "Realtime".</summary>
        public string Mode { get; init; }
    }
}
