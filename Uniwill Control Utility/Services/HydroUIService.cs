using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Newtonsoft.Json;
using Serilog;
using Uniwill_Control_Utility.Models;
using System.Diagnostics;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace Uniwill_Control_Utility.Services
{
    /// <summary>
    /// BLE communication layer for LCT laptop water coolers (LCT21001/LCT22002).
    /// Uses Nordic nRF52 UART GATT profile.
    /// </summary>
    public sealed class HydroUIService : IDisposable
    {
        private BluetoothLEDevice? _device;
        private GattSession? _gattSession;
        private GattCharacteristic? _txCharacteristic;
        private GattCharacteristic? _rxCharacteristic;
        private readonly List<BluetoothLEAdvertisementWatcher> _watchers = new();

        // Track when the OS-level GATT session actually closes.
        // Windows BLE is reference-counted — disposing our GattSession doesn't guarantee
        // immediate teardown if other processes hold references. We need to wait for
        // SessionStatus == Closed before reconnecting, otherwise we get a zombie link.
        private TaskCompletionSource<bool>? _sessionClosedTcs;

        /* ------------------------------------------------------------------ */
        /*  Background reconnect loop (THRM-style exponential backoff)           */
        /* ------------------------------------------------------------------ */

        /// <summary>
        /// Exponential backoff delays for reconnect attempts.
        /// 2s, 5s, 10s, 15s, 20s, 30s, 30s, 60s, 60s, 60s.
        /// After exhausting all delays, the loop exits and restarts on next disconnect.
        /// </summary>
        private static readonly int[] ReconnectDelaysMs =
        {
            2000, 5000, 10000, 15000, 20000,
            30000, 30000, 60000, 60000, 60000
        };

        private CancellationTokenSource? _reconnectCts;
        private Thread? _reconnectThread;
        private readonly object _reconnectLock = new();
        private ManualResetEvent? _reconnectWakeEvent;
        private int _connectGeneration; // Incremented on user disconnect to invalidate stale attempts

        private bool _userDisconnected;
        private int _isDisconnectingInt; // 0 = not disconnecting, 1 = in progress (for Interlocked.Exchange)
        private bool _isConnecting; // True while ConnectAsync is in flight — prevents GATT callback from triggering cleanup

        /* ------------------------------------------------------------------ */
        /*  Pending device watcher (auto-connect when the cooler powers on)     */
        /* ------------------------------------------------------------------ */

        private BluetoothLEAdvertisementWatcher? _pendingWatcher;
        private CancellationTokenSource? _pendingWatcherCts;
        private readonly object _pendingWatcherLock = new();
        private long _lastPendingWatcherAttemptTick; // Environment.TickCount64 of the last watcher-triggered connect
        private int _pendingWatcherConnectInProgress; // 0/1 — de-duplicates rapid advertisement events

        private static readonly long PendingWatcherAttemptCooldownMs = 30_000;
        private static readonly int PendingWatcherRearmDelayMs = 10_000;

        public event EventHandler<WatercoolerConnectionState>? ConnectionStateChanged;
        public event EventHandler<string>? StatusChanged;
        public event EventHandler<bool>? SmartModeStateChanged;

        /// <summary>
        /// Raised when a status response is received from the device.
        /// </summary>
        public event EventHandler<(byte PumpStatus, ushort FanRpm, byte Temperature)>? StatusReceived;

        /// <summary>
        /// Raised when the flow meter status changes.
        /// </summary>
        public event EventHandler<FlowStatus>? FlowStatusChanged;

        /// <summary>
        /// Raised whenever settings are saved. Used by the HydroUI page to
        /// sync its UI when changes come from external callers (e.g., tray menu).
        /// </summary>
        public event EventHandler? SettingsChanged;

        /// <summary>
        /// Raised when the firmware version is received from the device.
        /// </summary>
        public event EventHandler<string>? FirmwareVersionReceived;

        private WaterCoolerSettings _settings = new();

        /// <summary>Firmware version string reported by the device on connect.</summary>
        public string? FirmwareVersion { get; private set; }

        /// <summary>Current flow meter status.</summary>
        public FlowStatus FlowStatus { get; private set; }

        /// <summary>True when Standby mode is active (all outputs stopped, BLE connected).</summary>
        public bool IsStandby { get; private set; }

        /// <summary>Detected device model for the current connection (LCT21001, LCT22002, or null).</summary>
        public string? ConnectedModel { get; private set; }

        public bool IsConnected => _device != null && _txCharacteristic != null;

        /// <summary>
        /// True when both TX (command) and RX (notification) characteristics are ready.
        /// </summary>
        public bool IsFullyConnected => _device != null && _txCharacteristic != null && _rxCharacteristic != null;

        /// <summary>
        /// Human-readable name of the connected cooler (e.g. "XMG Oasis Mk2 (LCT22002)").
        /// Empty string when disconnected or unknown.
        /// </summary>
        public string ConnectedDeviceName { get; private set; } = string.Empty;

        public HydroUIService()
        {
            LoadSettings();
        }

        #region Settings Persistence

        private static readonly string SettingsFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UCU");

        private void LoadSettings()
        {
            try
            {
                var filePath = Path.Combine(SettingsFolder, "watercooler_settings.json");

                if (File.Exists(filePath))
                {
                    var json = File.ReadAllText(filePath);
                    _settings = JsonConvert.DeserializeObject<WaterCoolerSettings>(json) ?? new WaterCoolerSettings();
                    _settings.MigrateLegacySettings();
                }
            }
            catch
            {
                // Corrupted or unreadable settings file — reset to defaults
                _settings = new WaterCoolerSettings();
            }
        }

        public void SaveSettings()
        {
            try
            {
                Directory.CreateDirectory(SettingsFolder);
                var filePath = Path.Combine(SettingsFolder, "watercooler_settings.json");

                var json = JsonConvert.SerializeObject(_settings, Newtonsoft.Json.Formatting.Indented);
                File.WriteAllText(filePath, json);
                SettingsChanged?.Invoke(this, EventArgs.Empty);
            }
            catch
            {
                // Non-critical: failed to persist settings file
            }
        }

        public WaterCoolerSettings GetSettings() => _settings;

        public void UpdatePumpVoltage(PumpVoltage voltage)
        {
            _settings.PumpVoltage = voltage.ToString();
            SaveSettings();
        }

        public void UpdateFanSpeed(FanSpeed speed)
        {
            _settings.FanSpeed = speed.ToString();
            SaveSettings();
        }

        public void UpdateRgbMode(RgbState mode)
        {
            _settings.RgbMode = mode.ToString();
            SaveSettings();
        }

        public void UpdateRgbColor(RgbColor color)
        {
            _settings.RgbColor = color.ToString();
            SaveSettings();
        }

        public void UpdateAutoConnect(bool enabled)
        {
            _settings.AutoConnect = enabled;
            SaveSettings();
        }

        #endregion

        /// <summary>
        /// Discovers LCT watercooler devices via BLE advertising.
        /// When <paramref name="targetAddress"/> is provided the scan keeps
        /// running (recording other LCT devices) until that specific device
        /// advertises or the timeout elapses. Without a target, the scan stops
        /// as soon as any LCT device is found.
        /// </summary>
        public async Task<List<WaterCoolerDeviceInfo>> DiscoverDevicesAsync(int timeoutMs = 10000, string? targetAddress = null)
        {
            var devices = new List<WaterCoolerDeviceInfo>();
            var foundAddresses = new HashSet<string>();
            var completed = new TaskCompletionSource<bool>();
            var target = NormalizeAddress(targetAddress);

            var watcher = new BluetoothLEAdvertisementWatcher();
            watcher.Received += (s, e) =>
            {
                var name = e.Advertisement.LocalName;
                if (string.IsNullOrEmpty(name)) return;

                // Match known LCT watercooler device names
                if (name.Contains("lct", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("be quiet!", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("oasis", StringComparison.OrdinalIgnoreCase))
                {
                    var address = e.BluetoothAddress.ToString();
                    lock (devices)
                    {
                        if (foundAddresses.Add(address))
                        {
                            devices.Add(new WaterCoolerDeviceInfo
                            {
                                Address = address,
                                Name = name,
                                Rssi = e.RawSignalStrengthInDBm,
                                Model = DetectModelFromBleName(name)
                            });
                        }

                        // Without a target, stop on the first LCT device (the
                        // caller can re-scan for more results). With a target,
                        // only that device ends the scan early — a different
                        // cooler advertising first must not mask the target.
                        bool isTarget = target != null &&
                            string.Equals(NormalizeAddress(address), target, StringComparison.OrdinalIgnoreCase);
                        if (target == null || isTarget)
                        {
                            watcher.Stop();
                            completed.TrySetResult(true);
                        }
                    }
                }
            };

            watcher.Start();

            // Wait for either a device to be found (watcher.Stop above) or the timeout.
            await Task.WhenAny(
                completed.Task,
                Task.Delay(timeoutMs));

            watcher.Stop();
            _watchers.Remove(watcher);

            // Deduplicate by address
            return devices.DistinctBy(d => d.Address).ToList();
        }

        #region Device Identification

        /// <summary>
        /// Normalizes a BLE address for comparison (strips colons; the Windows
        /// BLE stack reports "aabbccddeeff" while saved addresses may use "AA:BB:..").
        /// </summary>
        private static string? NormalizeAddress(string? address) =>
            string.IsNullOrEmpty(address) ? null : address.Replace(":", "");

        /// <summary>
        /// Detects the LCT hardware model from a BLE advertising name.
        /// Returns "LCT22002", "LCT21001", or null if unknown.
        /// </summary>
        private static string? DetectModelFromBleName(string bleName)
        {
            if (string.IsNullOrEmpty(bleName))
                return null;

            string upper = bleName.ToUpperInvariant();

            if (upper.Contains("22002"))
                return LctDeviceModel.LCT22002;

            if (upper.Contains("21001"))
                return LctDeviceModel.LCT21001;

            return null;
        }

        /// <summary>
        /// Resolves a human-readable cooler brand name from the laptop chassis WMI data.
        /// Returns "XMG Oasis", "TUXEDO Aquaris", "PC Specialist", "Eluktronics", or "LCT HydroUI".
        /// </summary>
        private static string DetectChassisBrand()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher("root\\CIMV2", "SELECT * FROM Win32_ComputerSystem");

                foreach (ManagementBaseObject obj in searcher.Get())
                {
                    var manufacturer = obj["Manufacturer"]?.ToString() ?? "";
                    var model = obj["Model"]?.ToString() ?? "";
                    var productName = obj["Name"]?.ToString() ?? "";

                    var combined = (manufacturer + " " + model + " " + productName).ToLowerInvariant();

                    if (combined.Contains("xmg") || combined.Contains("schenker"))
                        return "XMG Oasis";

                    if (combined.Contains("tuxedo") || combined.Contains("aquaris"))
                        return "TUXEDO Aquaris";

                    if (combined.Contains("specialist"))
                        return "PC Specialist";

                    if (combined.Contains("eluktronics"))
                        return "Eluktronics";
                }
            }
            catch
            {
                // WMI not available
            }

            return "LCT HydroUI";
        }

        /// <summary>
        /// Builds a human-readable device name from the BLE name and chassis brand.
        /// Examples: "XMG Oasis Mk2", "TUXEDO Aquaris Mk1", "LCT HydroUI"
        /// </summary>
        private static string ResolveDeviceName(string bleName)
        {
            string? model = DetectModelFromBleName(bleName);
            string brand = DetectChassisBrand();

            if (model == LctDeviceModel.LCT22002)
                return $"{brand} Mk2";

            if (model == LctDeviceModel.LCT21001)
                return $"{brand} Mk1";

            // Unknown model — show brand with raw BLE name
            if (!string.IsNullOrEmpty(bleName) && bleName != brand)
                return $"{brand} ({bleName})";

            return brand;
        }

        #endregion

        /// <summary>
        /// Connects to a BLE device and prepares the TX characteristic.
        /// On reconnect, verifies the previous connection is truly dead before creating a new one —
        /// Windows BLE keeps zombie connections alive if we don't wait for full teardown.
        /// </summary>
        public async Task<bool> ConnectAsync(string deviceAddress)
        {
            _isConnecting = true;
            try
            {
                return await ConnectAsyncCore(deviceAddress);
            }
            finally
            {
                _isConnecting = false;
            }
        }

        private async Task<bool> ConnectAsyncCore(string deviceAddress)
        {
            ClearUserDisconnected();
            // Limited attempts for pre-loading auto-connect. Background reconnect
            // loops handle retries later if the device appears after startup.
            const int maxRetries = 2;

            // If somehow still connected, disconnect first to avoid stacking connections.
            // Windows BLE creates a NEW connection handle per FromBluetoothAddressAsync call —
            // if the old one is still alive (zombie), we end up with TWO active links and can't
            /// cleanly disconnect either because disposing only releases OUR reference.
            if (_device != null)
            {
                Debug.WriteLine("[WaterCooler] Device handle exists before connect — forcing cleanup first");
                await DisconnectAsync();
                await Task.Delay(500);
            }

            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    _device = await BluetoothLEDevice.FromBluetoothAddressAsync(
                        ulong.Parse(deviceAddress.Replace(":", "")));
                    if (_device == null)
                    {
                        Log.Warning("[WaterCooler] Connect attempt {Attempt}: FromBluetoothAddressAsync returned null for {Address}",
                            attempt, deviceAddress);
                        return false;
                    }

                    Debug.WriteLine($"[WaterCooler] Device acquired, ConnectionStatus={_device.ConnectionStatus}");

                    // Create a GattSession for explicit connection lifecycle control.
                    _gattSession = await GattSession.FromDeviceIdAsync(BluetoothDeviceId.FromId(_device.DeviceId));

                    // MaintainConnection=true is what asks Windows to hold — and INITIATE —
                    // the BLE link. After hibernation resume the cooler advertises but is NOT
                    // OS-connected (wake does not always re-establish the link), so without
                    // this the GATT discovery below throws and reconnect fails forever until
                    // the user power-cycles the cooler. DisconnectAsync flips it back to false
                    // to tear the link down.
                    try
                    {
                        if (_gattSession.CanMaintainConnection)
                            _gattSession.MaintainConnection = true;
                    }
                    catch
                    {
                        // Some stacks report CanMaintainConnection=false; discovery proceeds.
                    }

                    // Track when the OS-level session actually closes (not just our reference).
                    _sessionClosedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _gattSession.SessionStatusChanged += OnGattSessionStatusChanged;

                    // Discover services — use GetGattServicesAsync to get ALL services so we can close them all on disconnect.
                    var services = await _device.GetGattServicesForUuidAsync(new Guid(NordicUart.ServiceUuid));

                    foreach (var deviceService in services.Services)
                    {
                        // Find TX characteristic (write commands to device)
                        var txResults = await deviceService.GetCharacteristicsForUuidAsync(
                            new Guid(NordicUart.CharacteristicTx));

                        foreach (var characteristic in txResults.Characteristics)
                        {
                            if ((characteristic.CharacteristicProperties & GattCharacteristicProperties.Write) != 0)
                            {
                                _txCharacteristic = characteristic;
                                break;
                            }
                        }

                        // Find RX characteristic (read notifications from device)
                        var rxResults = await deviceService.GetCharacteristicsForUuidAsync(
                            new Guid(NordicUart.CharacteristicRx));

                        foreach (var characteristic in rxResults.Characteristics)
                        {
                            if ((characteristic.CharacteristicProperties & GattCharacteristicProperties.Notify) != 0)
                            {
                                _rxCharacteristic = characteristic;
                                var status = await _rxCharacteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                                    GattClientCharacteristicConfigurationDescriptorValue.Notify);
                                if (status == GattCommunicationStatus.Success)
                                    _rxCharacteristic.ValueChanged += OnRxValueChanged;
                                break;
                            }
                        }
                    }

                    if (_txCharacteristic != null)
                    {
                        // Resolve and store the human-readable device name
                        string bleName = _device.Name ?? string.Empty;
                        ConnectedDeviceName = ResolveDeviceName(bleName);

                        // Store detected device model
                        ConnectedModel = DetectModelFromBleName(bleName);

                        // Store last connected device for auto-connect
                        _settings.LastDeviceAddress = deviceAddress;
                        SaveSettings();

                        // Stop any background reconnection mechanisms — we're connected now.
                        StopReconnectLoop();
                        StopPendingDeviceWatcher();

                        // Query firmware version (raw "sw" bytes, no framing)
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await QueryFirmwareVersionAsync();
                            }
                            catch { /* non-critical — older firmware may not respond */ }
                        });

                        // Start flow meter polling (every 15s)
                        StartMeterPolling();

                        ConnectionStateChanged?.Invoke(this, WatercoolerConnectionState.Connected);
                        return true;
                    }

                    // Clean up if we got here (device opened but no TX characteristic)
                    Log.Warning("[WaterCooler] Connect attempt {Attempt}: device opened (ConnectionStatus={Status}) but no TX characteristic found",
                        attempt, _device.ConnectionStatus);
                    await CleanupAfterFailedConnectAsync();
                    return false;
                }
                catch (Exception ex) when (attempt < maxRetries)
                {
                    Log.Warning("[WaterCooler] Connect attempt {Attempt}/{MaxRetries} failed: {Message}",
                        attempt, maxRetries, ex.Message);
                    StatusChanged?.Invoke(this, $"Connection attempt {attempt} failed ({ex.Message}), retrying...");

                    // Clean up partial state before retry
                    await CleanupAfterFailedConnectAsync();
                    await Task.Delay(1000);
                }
            }

            return false;
        }

        /// <summary>
        /// Cleans up all BLE resources after a failed connect attempt.
        /// Centralized to avoid code duplication and ensure consistent cleanup order.
        /// </summary>
        private async Task CleanupAfterFailedConnectAsync()
        {
            // Unsubscribe RX if it was set up
            if (_rxCharacteristic != null)
            {
                _rxCharacteristic.ValueChanged -= OnRxValueChanged;
                try { await _rxCharacteristic.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.None); }
                catch
                {
                    // Unsubscribe failed during cleanup; continue disposing remaining resources
                }
                _rxCharacteristic = null;
            }

            // Dispose GattSession and wait for radio link to close
            if (_gattSession != null)
            {
                _gattSession.SessionStatusChanged -= OnGattSessionStatusChanged;
                var staleSession = _gattSession;
                _gattSession = null;
                try { staleSession.Dispose(); }
                catch
                {
                    // Stale session dispose failed; harmless during cleanup
                }
                await WaitForSessionClosedAsync(2000);
            }
            _sessionClosedTcs = null;

            // Dispose device
            if (_device != null)
            {
                var staleDevice = _device;
                _device = null;
                staleDevice.Dispose();
            }

            _txCharacteristic = null;
        }

        /// <summary>
        /// Disconnects from the current device cleanly.
        /// Strategy: close ALL GATT services, dispose session + device, then force Windows to release
        /// the radio link by closing the device at every level — matching what happens on app shutdown.
        /// </summary>
        public async Task DisconnectAsync()
        {
            // Prevent concurrent disconnect calls (e.g., user click + GATT session callback).
            if (System.Threading.Interlocked.Exchange(ref _isDisconnectingInt, 1) == 1)
            {
                Debug.WriteLine("[WaterCooler] DisconnectAsync already in progress — skipping");
                return;
            }

            Debug.WriteLine("[WaterCooler] DisconnectAsync called");

            // --- Step 1: Send reset command (matches Python reference) ---
            if (_txCharacteristic != null)
            {
                try
                {
                    await WriteResetCommandAsync();
                    await Task.Delay(500);
                }
                catch (Exception ex) { Debug.WriteLine($"[WaterCooler] Reset command failed: {ex.Message}"); }
            }

            // --- Step 2: Unsubscribe from RX notifications ---
            if (_rxCharacteristic != null)
            {
                _rxCharacteristic.ValueChanged -= OnRxValueChanged;
                try
                {
                    await _rxCharacteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                        GattClientCharacteristicConfigurationDescriptorValue.None);
                }
                catch (Exception ex) { Debug.WriteLine($"[WaterCooler] CCCD disable failed: {ex.Message}"); }
            }

            // --- Step 3: Close ALL discovered GATT services to force radio teardown ---
            // This is the critical step that DisconnectAsync was missing.
            // Disposing BluetoothLEDevice only releases OUR reference — Windows keeps the
            // underlying connection alive if any GattDeviceService handles are still open.
            // Closing them explicitly forces the OS to tear down the BLE link NOW, not on process exit.
            if (_device != null)
            {
                try
                {
                    var services = await _device.GetGattServicesAsync();
                    foreach (var service in services.Services)
                    {
                        Debug.WriteLine($"[WaterCooler] Closing GATT service {service.Uuid}");
                        service.Dispose(); // Explicitly close each service handle
                    }
                }
                catch (Exception ex) { Debug.WriteLine($"[WaterCooler] Service cleanup error: {ex.Message}"); }
            }

            await Task.Delay(300); // Let the OS process service closures

            // --- Step 4: Dispose GattSession and wait for radio link to close ---
            if (_gattSession != null)
            {
                _gattSession.SessionStatusChanged -= OnGattSessionStatusChanged;
                var sessionToDispose = _gattSession;
                _gattSession = null;
                try
                {
                    if (sessionToDispose.CanMaintainConnection)
                        sessionToDispose.MaintainConnection = false;
                }
                catch
                {
                    // Failed to disable MaintainConnection flag; dispose proceeds anyway
                }
                sessionToDispose.Dispose();

                // Wait for the OS-level radio link to actually close.
                bool closed = await WaitForSessionClosedAsync(timeoutMs: 3000);
                if (!closed)
                    Debug.WriteLine("[WaterCooler] WARNING: Session did not close within timeout");
            }
            _sessionClosedTcs = null;

            // --- Step 5: Dispose the device handle ---
            if (_device != null)
            {
                var deviceToDispose = _device;
                _device = null;
                deviceToDispose.Dispose();
            }

            // Clear all characteristic references
            _rxCharacteristic = null;
            _txCharacteristic = null;
            ConnectedDeviceName = string.Empty;
            ConnectedModel = null;
            FirmwareVersion = null;
            FlowStatus = FlowStatus.Off;
            IsStandby = false;

            // Stop meter polling on disconnect
            StopMeterPolling();

            ConnectionStateChanged?.Invoke(this, WatercoolerConnectionState.Disconnected);

            // Start background reconnection loop if this wasn't a user-initiated disconnect.
            // This handles cases where the BLE link drops unexpectedly (e.g., Bluetooth
            // off→on, device goes out of range then returns). The pending-device
            // watcher complements the bounded reconnect loop: it keeps listening
            // indefinitely, so a cooler that is powered on much later still
            // auto-connects.
            if (!_userDisconnected && _settings.AutoConnect)
            {
                _connectGeneration++; // Invalidate any in-flight connection attempt
                StartReconnectLoop();
                StartPendingDeviceWatcher();
            }

            // Stop Smart Mode on disconnect — it can't operate without a live link.
            StopSmartMode();

            // Allow future disconnect calls now that this one has completed.
            _isDisconnectingInt = 0;
        }

        /// <summary>
        /// User-initiated disconnect. This prevents all auto-reconnect logic
        /// from firing until the user explicitly connects again.
        /// </summary>
        public void SetUserDisconnected()
        {
            _connectGeneration++; // Invalidate any in-flight reconnect
            _userDisconnected = true;
            StopReconnectLoop();
            StopPendingDeviceWatcher();
        }

        /// <summary>
        /// Clears the user-disconnected flag. Called when the user initiates a new connect.
        /// </summary>
        private void ClearUserDisconnected()
        {
            _userDisconnected = false;
        }

        #region Background Reconnect Loop

        /// <summary>
        /// Starts a reconnect thread that runs exponential backoff attempts.
        /// </summary>
        private void StartReconnectLoop()
        {
            lock (_reconnectLock)
            {
                if (_reconnectCts != null)
                    return; // Already running

                _reconnectCts = new CancellationTokenSource();
                _reconnectWakeEvent = new ManualResetEvent(false);
                var token = _reconnectCts.Token;
                int generation = _connectGeneration;

                _reconnectThread = new Thread(() =>
                {
                    try
                    {
                        Debug.WriteLine($"[WaterCooler] Reconnect loop started (generation={generation}, {ReconnectDelaysMs.Length} delays)");

                        for (int i = 0; i < ReconnectDelaysMs.Length; i++)
                        {
                            if (token.IsCancellationRequested)
                            {
                                Debug.WriteLine("[WaterCooler] Reconnect loop cancelled");
                                return;
                            }

                            if (IsConnected || _userDisconnected)
                            {
                                Debug.WriteLine(
                                    "[WaterCooler] Reconnect loop stopping (already connected or user disconnected)");
                                return;
                            }

                            // Check if a newer generation has started a fresh reconnect
                            if (generation != _connectGeneration)
                            {
                                Debug.WriteLine(
                                    $"[WaterCooler] Reconnect loop stale (generation {generation} vs {_connectGeneration}), stopping");
                                return;
                            }

                            int delayMs = ReconnectDelaysMs[i];

                            Debug.WriteLine(
                                $"[WaterCooler] Waiting {delayMs}ms before reconnect attempt {i + 1}/{ReconnectDelaysMs.Length}");

                            var waitResult = WaitHandle.WaitAny(
                                new[] {
                                    _reconnectCts.Token.WaitHandle,
                                    _reconnectWakeEvent
                                },
                                delayMs);

                            if (waitResult == 0) // Cancelled
                            {
                                Debug.WriteLine("[WaterCooler] Reconnect loop cancelled during wait");
                                return;
                            }

                            // Clear wake event if triggered
                            if (waitResult == 1)
                                _reconnectWakeEvent.Reset();

                            // Attempt reconnect
                            try
                            {
                                Task.Run(async () =>
                                {
                                    bool ok = await TryAutoConnectAsync();
                                    if (ok)
                                    {
                                        Debug.WriteLine(
                                            $"[WaterCooler] Reconnected on attempt {i + 1}");
                                    }
                                }).GetAwaiter().GetResult();
                            }
                            catch
                            {
                                // Non-critical: reconnect attempt failed, will retry
                            }
                        }

                        Debug.WriteLine(
                            "[WaterCooler] Reconnect loop exhausted all delays");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[WaterCooler] Reconnect loop error: {ex.Message}");
                    }
                })
                {
                    IsBackground = true,
                    Name = "WaterCoolerReconnectLoop"
                };
                _reconnectThread.Start();
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
                catch { /* Non-critical */ }

                _reconnectThread?.Join(2000);

                _reconnectCts?.Dispose();
                _reconnectCts = null;
                _reconnectThread = null;
                _reconnectWakeEvent?.Dispose();
                _reconnectWakeEvent = null;
            }
        }

        /// <summary>
        /// Wakes the reconnect loop early. Returns true if a loop was running.
        /// </summary>
        private bool WakeReconnect()
        {
            lock (_reconnectLock)
            {
                if (_reconnectCts != null)
                {
                    _reconnectWakeEvent?.Set();
                    return true;
                }
                return false;
            }
        }

        #endregion

        #region Pending Device Watcher

        /// <summary>
        /// Starts a persistent, passive BLE advertisement watcher that
        /// auto-connects the last known watercooler as soon as it advertises
        /// (e.g. the user powers it on after a failed startup connect). The
        /// watcher receives advertisements pushed by the OS BLE radio — no
        /// polling, no outbound probes. No-op when auto-connect is disabled,
        /// no device is known, or the user disconnected manually.
        /// </summary>
        public void StartPendingDeviceWatcher()
        {
            lock (_pendingWatcherLock)
            {
                if (_pendingWatcherCts != null)
                    return; // Supervisor already running

                if (!IsPendingWatcherEligible())
                    return;

                var cts = new CancellationTokenSource();
                _pendingWatcherCts = cts;
                _ = RunPendingWatcherSupervisorAsync(cts);
            }
        }

        /// <summary>
        /// Stops the pending-device watcher and its supervisor (no re-arm).
        /// </summary>
        public void StopPendingDeviceWatcher()
        {
            CancellationTokenSource? cts;
            lock (_pendingWatcherLock)
            {
                cts = _pendingWatcherCts;
                _pendingWatcherCts = null;

                var watcher = _pendingWatcher;
                _pendingWatcher = null;
                if (watcher != null)
                {
                    try { watcher.Stop(); }
                    catch
                    {
                        // Non-critical: watcher stop failed during teardown
                    }
                }
            }

            if (cts != null)
            {
                try
                {
                    cts.Cancel();
                    cts.Dispose();
                }
                catch
                {
                    // Already disposed by the supervisor's exit path
                }
            }
        }

        private bool IsPendingWatcherEligible() =>
            _settings.AutoConnect &&
            !string.IsNullOrEmpty(_settings.LastDeviceAddress) &&
            !_userDisconnected &&
            !IsConnected;

        /// <summary>
        /// Keeps a pending-device watcher alive: starts it, re-arms it when the
        /// OS stops it (e.g. Bluetooth radio off), and exits when the target
        /// handled a connect or conditions are no longer met.
        /// </summary>
        private async Task RunPendingWatcherSupervisorAsync(CancellationTokenSource cts)
        {
            var token = cts.Token;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    if (!IsPendingWatcherEligible())
                        return;

                    var endedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var targetHandled = new[] { false };

                    var watcher = new BluetoothLEAdvertisementWatcher();
                    watcher.Received += (s, e) =>
                    {
                        if (!TryHandlePendingWatcherReceived(e.BluetoothAddress.ToString()))
                            return;

                        // The target advertised and the connect flow took over —
                        // end this watcher without re-arming.
                        targetHandled[0] = true;
                        try { watcher.Stop(); }
                        catch
                        {
                            // Non-critical: the connect flow also stops the watcher explicitly
                        }
                    };
                    // Radio loss (e.g. Bluetooth off) surfaces as Stopped in
                    // this WinRT projection — the supervisor re-arms on it.
                    watcher.Stopped += (s, e) => endedTcs.TrySetResult(targetHandled[0]);

                    // Cancellation (app exit / feature disable) must unblock the
                    // await on endedTcs even if Stop() was never delivered.
                    using var cancellationStop = token.Register(() =>
                    {
                        try { watcher.Stop(); }
                        catch
                        {
                            // Non-critical: watcher already gone
                        }
                    });

                    try
                    {
                        watcher.Start();
                    }
                    catch (Exception ex)
                    {
                        // Radio off/unavailable — retry after a delay.
                        Log.Information("[WaterCooler] Pending device watcher unavailable ({Error}), retrying", ex.Message);
                        await Task.Delay(PendingWatcherRearmDelayMs, token);
                        continue;
                    }

                    lock (_pendingWatcherLock)
                    {
                        if (token.IsCancellationRequested)
                        {
                            try { watcher.Stop(); }
                            catch
                            {
                                // Non-critical
                            }
                            return;
                        }
                        _pendingWatcher = watcher;
                    }
                    Log.Information("[WaterCooler] Pending device watcher active for {Address}", _settings.LastDeviceAddress);

                    await endedTcs.Task;

                    lock (_pendingWatcherLock)
                    {
                        if (ReferenceEquals(_pendingWatcher, watcher))
                            _pendingWatcher = null;
                    }

                    if (targetHandled[0] || token.IsCancellationRequested)
                        return;

                    // The watcher stopped on its own (e.g. radio off) — re-arm.
                    await Task.Delay(PendingWatcherRearmDelayMs, token);
                }
            }
            catch (OperationCanceledException)
            {
                // Shutdown requested — nothing to clean up here
            }
            finally
            {
                lock (_pendingWatcherLock)
                {
                    if (ReferenceEquals(_pendingWatcherCts, cts))
                    {
                        _pendingWatcherCts = null;
                        try { cts.Dispose(); }
                        catch
                        {
                            // Already disposed by StopPendingDeviceWatcher
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Handles an advertisement received by the pending-device watcher.
        /// Returns true when the target matched and the connect flow was
        /// launched (the watcher should then stop without re-arming).
        /// </summary>
        private bool TryHandlePendingWatcherReceived(string address)
        {
            var target = NormalizeAddress(_settings.LastDeviceAddress);
            if (target == null ||
                !string.Equals(NormalizeAddress(address), target, StringComparison.OrdinalIgnoreCase))
                return false;

            if (IsConnected || _userDisconnected || _isConnecting)
                return false;

            // Cooldown after a failed attempt — the device keeps advertising,
            // so without this we would hammer ConnectAsync on every
            // advertisement (~1/s) while it stays unreachable.
            if (Environment.TickCount64 - _lastPendingWatcherAttemptTick < PendingWatcherAttemptCooldownMs)
                return false;

            if (Interlocked.Exchange(ref _pendingWatcherConnectInProgress, 1) == 1)
                return false;

            _lastPendingWatcherAttemptTick = Environment.TickCount64;
            Log.Information("[WaterCooler] Watercooler {Address} advertising — connecting", target);
            _ = ConnectPendingDeviceAsync();
            return true;
        }

        /// <summary>
        /// Connects to the last known watercooler after the pending-device
        /// watcher saw it advertise. The scan gate is satisfied by the
        /// advertisement itself, so no discovery scan is needed.
        /// </summary>
        private async Task ConnectPendingDeviceAsync()
        {
            try
            {
                StopPendingDeviceWatcher();

                StatusChanged?.Invoke(this, "Watercooler detected — connecting...");
                if (await ConnectAsync(_settings.LastDeviceAddress))
                {
                    await RestoreSettingsAfterConnectAsync();
                    StatusChanged?.Invoke(this, "Auto-connected successfully");
                }
                else
                {
                    StatusChanged?.Invoke(this, "Watercooler detected — connection failed");
                    StartPendingDeviceWatcher();
                }
            }
            catch (Exception ex)
            {
                Log.Information("[WaterCooler] Pending device connect failed: {Error}", ex.Message);
                StartPendingDeviceWatcher();
            }
            finally
            {
                Interlocked.Exchange(ref _pendingWatcherConnectInProgress, 0);
            }
        }

        #endregion

        /* ------------------------------------------------------------------ */
        /*  Smart Mode — temperature-linked pump and fan speed                   */
        /* ------------------------------------------------------------------ */

        private System.Threading.Timer? _smartModeTimer;
        private PumpVoltage _smartModeCurrentPump;
        private FanSpeed _smartModeCurrentFan;
        private double _smartModeFilteredTemp;
        private bool _smartModeLastWasBoost; // For hysteresis direction tracking
        private RgbState _smartModeCurrentRgbMode;
        private RgbColor _smartModeCurrentRgbColor;

        /// <summary>
        /// Starts the Smart Mode control loop.
        /// Reads temperatures at 2-second intervals and adjusts pump voltage
        /// and fan speed based on thresholds with hysteresis to prevent oscillation.
        /// Idempotent — calling while already running is a no-op (navigation re-syncs trigger this).
        /// </summary>
        public void StartSmartMode()
        {
            // Already running — don't restart. The HydroUI page calls this on every
            // navigation (IsVisibleChanged → OnConnectionStateChanged), which would reset
            // hysteresis state and the EWMA filter, causing unwanted pump/fan speed changes.
            if (_smartModeTimer != null)
                return;

            StopSmartMode();

            var settings = _settings;
            _smartModeLastWasBoost = false;

            // Initialize filtered temperature to current reading so the first tick
            // doesn't treat the initial EWMA state as a temperature change.
            try
            {
                var temp = ReadTemperatureForSource(settings.SmartModeTempSource);
                if (temp.HasValue)
                    _smartModeFilteredTemp = temp.Value;
            }
            catch { /* temperature unavailable at startup — first tick will initialize */ }

            // Immediately apply idle speeds so the device is in the correct state.
            // This is necessary because the tick loop only writes when the target
            // differs from the tracked current speed, and on start we don't know
            // what the device's actual pump/fan speed is.
            _smartModeCurrentPump = settings.GetSmartModeIdlePump();
            _smartModeCurrentFan = settings.GetSmartModeIdleFan();
            _smartModeCurrentRgbMode = default;
            _smartModeCurrentRgbColor = default;

            _ = Task.Run(async () =>
            {
                try
                {
                    if (IsConnected)
                    {
                        if (_smartModeCurrentPump != PumpVoltage.Off)
                            await WritePumpModeRampedAsync(_smartModeCurrentPump);
                        else
                            await WritePumpModeAsync(PumpVoltage.Off);

                        if (_smartModeCurrentFan != FanSpeed.Off)
                            await WriteFanModeAsync(_smartModeCurrentFan);

                        // Apply initial temperature-based RGB if enabled
                        if (settings.SmartModeTemperatureRgb)
                        {
                            var rgbColor = _smartModeFilteredTemp < settings.SmartModeLowThreshold
                                ? RgbColor.Green
                                : _smartModeFilteredTemp > settings.SmartModeHighThreshold
                                    ? RgbColor.Red
                                    : RgbColor.White;
                            _smartModeCurrentRgbMode = RgbState.Static;
                            _smartModeCurrentRgbColor = rgbColor;
                            await WriteRgbModeAsync(RgbState.Static, rgbColor);
                        }

                        Debug.WriteLine($"[WaterCooler] Smart mode initial: pump {_smartModeCurrentPump}, fan {_smartModeCurrentFan}");
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[WaterCooler] Smart mode initial apply error: {ex.Message}");
                }
            });

            _smartModeTimer = new System.Threading.Timer(async _ =>
            {
                try
                {
                    await SmartModeTick();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[WaterCooler] Smart mode tick error: {ex.Message}");
                }
            }, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));

            Debug.WriteLine("[WaterCooler] Smart Mode started");
            SmartModeStateChanged?.Invoke(this, true);
        }

        /// <summary>
        /// Stops the Smart Mode control loop.
        /// </summary>
        public void StopSmartMode()
        {
            var timer = _smartModeTimer;
            _smartModeTimer = null;
            timer?.Dispose();
            _smartModeCurrentRgbMode = default;
            _smartModeCurrentRgbColor = default;
            Debug.WriteLine("[WaterCooler] Smart Mode stopped");
            SmartModeStateChanged?.Invoke(this, false);
        }

        /// <summary>True when Smart Mode is currently running.</summary>
        public bool IsSmartModeActive => _smartModeTimer != null;

        /// <summary>Current pump voltage being applied by Smart Mode (only meaningful when IsSmartModeActive is true).</summary>
        public PumpVoltage SmartModeCurrentPump => _smartModeCurrentPump;

        /// <summary>Current fan speed being applied by Smart Mode (only meaningful when IsSmartModeActive is true).</summary>
        public FanSpeed SmartModeCurrentFan => _smartModeCurrentFan;

        private async Task SmartModeTick()
        {
            if (!IsConnected) return;

            var settings = _settings;
            double? rawTemp = ReadTemperatureForSource(settings.SmartModeTempSource);

            if (!rawTemp.HasValue)
                return; // No temperature available — skip this tick

            // EWMA filter (alpha = 0.5) to smooth transient spikes
            double filtered = _smartModeFilteredTemp * 0.5 + rawTemp.Value * 0.5;
            _smartModeFilteredTemp = filtered;

            int lowThreshold = settings.SmartModeLowThreshold;
            int highThreshold = settings.SmartModeHighThreshold;

            // Two-zone hysteresis:
            //   - below lowThreshold → idle speed
            //   - above highThreshold → boost speed
            //   - between them → keep current state (the gap IS the hysteresis deadzone)
            // This prevents oscillation: once in idle, temp must exceed highThreshold to
            // switch to boost. Once in boost, temp must drop below lowThreshold to switch back.
            PumpVoltage targetPump;
            FanSpeed targetFan;

            if (_smartModeLastWasBoost)
            {
                // Currently in boost — switch to idle when temp drops below lowThreshold
                if (filtered < lowThreshold)
                {
                    targetPump = settings.GetSmartModeIdlePump();
                    targetFan = settings.GetSmartModeIdleFan();
                    _smartModeLastWasBoost = false;
                }
                else
                {
                    targetPump = settings.GetSmartModeBoostPump();
                    targetFan = settings.GetSmartModeBoostFan();
                }
            }
            else
            {
                // Currently in idle — switch to boost when temp rises above highThreshold
                if (filtered > highThreshold)
                {
                    targetPump = settings.GetSmartModeBoostPump();
                    targetFan = settings.GetSmartModeBoostFan();
                    _smartModeLastWasBoost = true;
                }
                else
                {
                    targetPump = settings.GetSmartModeIdlePump();
                    targetFan = settings.GetSmartModeIdleFan();
                }
            }

            // Only write if the target differs from current — avoids unnecessary BLE traffic.
            bool pumpChanged = targetPump != _smartModeCurrentPump;
            bool fanChanged = targetFan != _smartModeCurrentFan;

            if (pumpChanged)
            {
                _smartModeCurrentPump = targetPump;

                // If transitioning from Off to an active speed, use a gentler ramp
                // to avoid the pump maxing out on startup.
                if (targetPump != PumpVoltage.Off)
                {
                    await WritePumpModeRampedAsync(targetPump);
                }
                else
                {
                    await WritePumpModeAsync(targetPump);
                }
            }

            if (fanChanged)
            {
                _smartModeCurrentFan = targetFan;
                await WriteFanModeAsync(targetFan);
            }

            // Temperature-based RGB (only when SmartModeTemperatureRgb is enabled)
            if (settings.SmartModeTemperatureRgb)
            {
                RgbState rgbMode;
                RgbColor rgbColor;

                if (filtered < lowThreshold)
                {
                    rgbMode = RgbState.Static;
                    rgbColor = RgbColor.Green; // Idle - cool
                }
                else if (filtered > highThreshold)
                {
                    rgbMode = RgbState.Static;
                    rgbColor = RgbColor.Red; // Boost - hot
                }
                else
                {
                    rgbMode = RgbState.Static;
                    rgbColor = RgbColor.White; // Transition - between thresholds
                }

                // Write RGB if it differs from current
                if (_smartModeCurrentRgbMode != rgbMode || _smartModeCurrentRgbColor != rgbColor)
                {
                    _smartModeCurrentRgbMode = rgbMode;
                    _smartModeCurrentRgbColor = rgbColor;
                    Application.Current.Dispatcher.InvokeAsync(async () =>
                    {
                        await WriteRgbModeAsync(rgbMode, rgbColor);
                    });
                }
            }

            if (pumpChanged || fanChanged)
                Debug.WriteLine($"[WaterCooler] Smart mode: {rawTemp.Value:F1}°C → pump {targetPump}, fan {targetFan}");
        }

        /// <summary>
        /// Reads the current temperature for the configured temperature source.
        /// Returns CPU, GPU, or max of the two depending on settings.
        /// Returns null if no temperature is available.
        /// </summary>
        private double? ReadTemperatureForSource(string tempSource)
        {
            try
            {
                var provider = new Models.FlydigiTemperatureProvider();
                double? temp = tempSource switch
                {
                    "cpu" => provider.GetCpuTemperature(),
                    "gpu" => provider.GetGpuTemperature(),
                    _ => provider.GetMaxTemperature(), // "max" or unknown
                };
                provider.Dispose();
                return temp;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Sends a pump command with a gentler ramp to avoid the pump maxing out
        /// when transitioning from Off to an active speed.
        /// Strategy: send at 30% duty cycle first, then ramp to 60% after a brief delay.
        /// </summary>
        private async Task WritePumpModeRampedAsync(PumpVoltage voltage)
        {
            if (!IsConnected) return;

            // Track pump state for flow meter grace period — the ramped path
            // is only called when transitioning to an active voltage (never Off).
            OnPumpStateChanged(true);

            byte voltageCode = (byte)voltage;

            // Phase 1: Send at reduced duty cycle (30%) to let the pump start gently
            var frameLow = BuildFrame(WaterCoolerCommand.Pump, true, 30, voltageCode, WaterCoolerCommand.Disabled);
            await WriteFrameAsync(frameLow);
            await Task.Delay(300);

            // Phase 2: Ramp to normal duty cycle (60%)
            var frameNormal = BuildFrame(WaterCoolerCommand.Pump, true, 60, voltageCode, WaterCoolerCommand.Disabled);
            await WriteFrameAsync(frameNormal);
        }

        /// <summary>
        /// Sends pump voltage command to the device.
        /// Frame: FE 1C [EN] [duty_cycle%] [voltage_code] 00 00 EF
        /// </summary>
        public async Task<bool> WritePumpModeAsync(PumpVoltage voltage, byte dutyCyclePercent = 60)
        {
            if (!IsConnected) throw new InvalidOperationException("Not connected");

            byte[] frame;
            if (voltage == PumpVoltage.Off)
            {
                frame = BuildFrame(WaterCoolerCommand.Pump, false, WaterCoolerCommand.Disabled, WaterCoolerCommand.Disabled, WaterCoolerCommand.Disabled);
            }
            else
            {
                frame = BuildFrame(WaterCoolerCommand.Pump, true, dutyCyclePercent, (byte)voltage, WaterCoolerCommand.Disabled);
            }

            // Track pump state for flow meter grace period
            OnPumpStateChanged(voltage != PumpVoltage.Off);

            StatusChanged?.Invoke(this, $"Sending pump command: {string.Join(" ", frame.Select(b => b.ToString("X2")))}");
            return await WriteFrameAsync(frame);
        }

        /// <summary>
        /// Sends fan speed command to the device.
        /// Frame: FE 1B [EN] [duty_cycle%] 00 00 00 EF
        /// </summary>
        public async Task<bool> WriteFanModeAsync(FanSpeed speed)
        {
            if (!IsConnected) throw new InvalidOperationException("Not connected");

            byte[] frame;
            if (speed == FanSpeed.Off)
            {
                frame = BuildFrame(WaterCoolerCommand.Fan, false, 0x00, 0x00, 0x00);
            }
            else
            {
                frame = BuildFrame(WaterCoolerCommand.Fan, true, (byte)speed, 0x00, 0x00);
            }

            StatusChanged?.Invoke(this, $"Sending fan command: {string.Join(" ", frame.Select(b => b.ToString("X2")))}");
            return await WriteFrameAsync(frame);
        }

        /// <summary>
        /// Sends RGB mode command to the device.
        /// Frame: FE 1E [EN] [R] [G] [B] [state] EF
        /// </summary>
        public async Task<bool> WriteRgbModeAsync(RgbState mode, RgbColor color = RgbColor.Red)
        {
            if (!IsConnected) throw new InvalidOperationException("Not connected");

            byte[] frame;
            if (mode == RgbState.Off)
            {
                frame = new byte[]
                {
                    WaterCoolerCommand.FrameStart,
                    WaterCoolerCommand.Rgb,
                    WaterCoolerCommand.Disabled,
                    0x00, 0x00, 0x00, 0x00,
                    WaterCoolerCommand.FrameEnd
                };
            }
            else
            {
                // Expand color preset to RGB values
                (byte r, byte g, byte b) rgb = color switch
                {
                    RgbColor.Red   => (byte.MaxValue, (byte)0, (byte)0),
                    RgbColor.Green => ((byte)0, byte.MaxValue, (byte)0),
                    RgbColor.Blue  => ((byte)0, (byte)0, byte.MaxValue),
                    RgbColor.White => (byte.MaxValue, byte.MaxValue, byte.MaxValue),
                    _              => (byte.MaxValue, (byte)0, (byte)0),
                };
                var (r, g, b) = rgb;

                frame = new byte[]
                {
                    WaterCoolerCommand.FrameStart,
                    WaterCoolerCommand.Rgb,
                    WaterCoolerCommand.Enabled,
                    r, g, b, (byte)mode,
                    WaterCoolerCommand.FrameEnd
                };
            }

           Debug.WriteLine($"[WaterCooler] RGB frame: {string.Join(" ", frame.Select(b => b.ToString("X2")))}");
            StatusChanged?.Invoke(this, $"Sending RGB command: mode={mode}, color={color}");

            await WriteFrameAsync(frame);
            return true;
        }

        /* ------------------------------------------------------------------ */
        /*  Firmware version query                                               */
        /* ------------------------------------------------------------------ */

        /// <summary>
        /// Sends raw "sw" bytes to query firmware version.
        /// The device responds with a UTF-8 string such as "MCU F/W Version: 2.0.0.4".
        /// </summary>
        private async Task QueryFirmwareVersionAsync()
        {
            if (_txCharacteristic == null) return;

            byte[] sw = new byte[] { 0x73, 0x77 }; // "sw"
            var status = await _txCharacteristic.WriteValueAsync(
                sw.AsBuffer(), GattWriteOption.WriteWithoutResponse);

            if (status == GattCommunicationStatus.Success)
                Debug.WriteLine("[WaterCooler] Firmware version query sent");
        }

        /* ------------------------------------------------------------------ */
        /*  Flow meter polling                                                   */
        /* ------------------------------------------------------------------ */

        private System.Threading.Timer? _meterPollTimer;
        private byte? _lastMeterState;
        private DateTime _flowStartGraceEnd;
        private bool _pumpRunning;

        private void StartMeterPolling()
        {
            StopMeterPolling();
            _pumpRunning = false;
            _lastMeterState = null;
            FlowStatus = FlowStatus.Off;

            // Initial query immediately, then every 15s
            _ = Task.Run(async () =>
            {
                try { await QueryMeterAsync(); }
                catch { /* non-critical */ }
            });

            _meterPollTimer = new System.Threading.Timer(_ =>
            {
                _ = Task.Run(async () =>
                {
                    try { await QueryMeterAsync(); }
                    catch { /* non-critical — meter polling is best-effort */ }
                });
            }, null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));

            Debug.WriteLine("[WaterCooler] Meter polling started");
        }

        private void StopMeterPolling()
        {
            var timer = _meterPollTimer;
            _meterPollTimer = null;
            timer?.Dispose();
        }

        /// <summary>
        /// Sends QUERY_METER (0x32) to poll for flow status.
        /// </summary>
        public async Task QueryMeterAsync()
        {
            if (!IsConnected) return;

            byte[] frame = new byte[]
            {
                WaterCoolerCommand.FrameStart,
                WaterCoolerCommand.QueryMeter,
                WaterCoolerCommand.Disabled,
                0x00, 0x00, 0x00, 0x00,
                WaterCoolerCommand.FrameEnd
            };
            await WriteFrameAsync(frame);
        }

        /// <summary>
        /// Updates flow status based on the latest meter state byte.
        /// Call from OnRxValueChanged when a meter response arrives.
        /// </summary>
        private void UpdateFlowStatus(byte meterState)
        {
            _lastMeterState = meterState;

            if (!_pumpRunning)
            {
                // Pump is off — keep FlowStatus.Off set by OnPumpStateChanged.
                // Do not override to Unknown; the meter may still report stale state.
                return;
            }

            // State byte 2 = flow OK
            if (meterState == 2)
            {
                _flowStartGraceEnd = DateTime.MinValue;
                SetFlowStatus(FlowStatus.Ok);
                return;
            }

            // Within 5-second grace period after pump start → "starting"
            if (DateTime.UtcNow < _flowStartGraceEnd)
            {
                SetFlowStatus(FlowStatus.Starting);
                return;
            }

            // Pump running but meter reports no flow (state 1 or other)
            if (_lastMeterState is null)
            {
                SetFlowStatus(FlowStatus.Unknown);
            }
            else
            {
                SetFlowStatus(FlowStatus.Fault);
            }
        }

        private void SetFlowStatus(FlowStatus status)
        {
            if (FlowStatus == status) return;
            FlowStatus = status;
            Debug.WriteLine($"[WaterCooler] Flow status: {status}");
            FlowStatusChanged?.Invoke(this, status);
        }

        /// <summary>
        /// Called when pump state changes (on/off) to track grace period for flow detection.
        /// </summary>
        private void OnPumpStateChanged(bool running)
        {
            if (running && !_pumpRunning)
            {
                // Pump just started — 5 second grace period before fault detection
                _flowStartGraceEnd = DateTime.UtcNow.AddSeconds(5);
                _lastMeterState = null;
                SetFlowStatus(FlowStatus.Starting);
            }
            else if (!running)
            {
                SetFlowStatus(FlowStatus.Off);
            }
            _pumpRunning = running;
        }

        /* ------------------------------------------------------------------ */
        /*  Standby / Resume                                                     */
        /* ------------------------------------------------------------------ */

        /// <summary>
        /// Enters standby mode — stops all outputs while keeping BLE connected.
        /// Frame: FE 19 00 01 00 00 00 EF (SYSTEM_MODE sleep)
        /// </summary>
        public async Task EnterStandbyAsync()
        {
            if (!IsConnected) throw new InvalidOperationException("Not connected");

            byte[] frame = new byte[]
            {
                WaterCoolerCommand.FrameStart,
                WaterCoolerCommand.Reset,
                WaterCoolerCommand.Disabled,
                0x01, 0x00, 0x00, 0x00,
                WaterCoolerCommand.FrameEnd
            };
            await WriteFrameAsync(frame);
            IsStandby = true;
            _pumpRunning = false;
            SetFlowStatus(FlowStatus.Off);
            Debug.WriteLine("[WaterCooler] Standby mode entered");
        }

        /// <summary>
        /// Exits standby mode — re-applies saved settings to the device.
        /// </summary>
        public async Task ExitStandbyAsync()
        {
            if (!IsConnected) throw new InvalidOperationException("Not connected");

            IsStandby = false;

            try
            {
                // Brief pause to let the device wake from sleep before accepting commands
                await Task.Delay(200);

                var settings = _settings;

                // Re-apply pump
                var pumpVoltage = settings.GetPumpVoltage();
                if (pumpVoltage != PumpVoltage.Off)
                    await WritePumpModeAsync(pumpVoltage);

                // Re-apply fan
                var fanSpeed = settings.GetFanSpeed();
                if (fanSpeed != FanSpeed.Off)
                    await WriteFanModeAsync(fanSpeed);

                // Re-apply RGB
                var rgbMode = settings.GetRgbMode();
                var rgbColor = settings.GetRgbColor();
                if (settings.RgbEnabled)
                    await WriteRgbModeAsync(rgbMode, rgbColor);
                else
                    await WriteRgbModeAsync(RgbState.Off, RgbColor.Red);

                Debug.WriteLine("[WaterCooler] Standby mode exited — settings restored");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WaterCooler] Standby exit error: {ex.Message}");
            }
        }

        /* ------------------------------------------------------------------ */
        /*  Priming (water filling cycle)                                        */
        /* ------------------------------------------------------------------ */

        /// <summary>
        /// Starts or stops the OEM priming (water filling) cycle.
        /// Start: FE 19 01 04 50 00 00 EF
        /// Stop:  FE 19 00 04 00 00 00 EF
        /// </summary>
        public async Task WritePrimingAsync(bool enabled)
        {
            if (!IsConnected) throw new InvalidOperationException("Not connected");

            byte[] frame = new byte[]
            {
                WaterCoolerCommand.FrameStart,
                WaterCoolerCommand.Reset,
                enabled ? WaterCoolerCommand.Enabled : WaterCoolerCommand.Disabled,
                0x04,
                enabled ? (byte)0x50 : WaterCoolerCommand.Disabled,
                0x00, 0x00,
                WaterCoolerCommand.FrameEnd
            };
            await WriteFrameAsync(frame);
            Debug.WriteLine($"[WaterCooler] Priming: {(enabled ? "started" : "stopped")}");
        }

        /// <summary>
        /// Runs the full 8-cycle water filling sequence.
        /// Each cycle: pump off 500ms → pump on (V8, 100%) 5.5s → pump off 2.5s.
        /// Fan runs at 50% during the entire cycle.
        /// </summary>
        /// <param name="cancellationToken">Cancel the cycle early.</param>
        /// <param name="onProgress">Callback for each completed cycle (1-8).</param>
        public async Task RunPrimingCycleAsync(
            CancellationToken cancellationToken = default,
            System.Action<int>? onProgress = null)
        {
            if (!IsConnected) throw new InvalidOperationException("Not connected");

            try
            {
                await WritePrimingAsync(true);
                await WriteFanModeAsync(FanSpeed.Percent50);

                for (int cycle = 1; cycle <= 8; cycle++)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    onProgress?.Invoke(cycle);

                    await WritePumpModeAsync(PumpVoltage.Off);
                    await Task.Delay(500, cancellationToken);

                    await WritePumpModeAsync(PumpVoltage.V8, 100);
                    await Task.Delay(5500, cancellationToken);

                    await WritePumpModeAsync(PumpVoltage.Off);
                    await Task.Delay(2500, cancellationToken);
                }
            }
            finally
            {
                // Always stop priming mode, even on cancellation
                try { await WritePrimingAsync(false); }
                catch { /* non-critical teardown */ }
            }
        }

        /// <summary>
        /// Attempts to auto-connect to the last known device on startup.
        /// The connect is gated on BLE advertising: a powered-off cooler that is
        /// still in the Windows BLE cache would make FromBluetoothAddressAsync
        /// stall for a long time before failing, which used to hang startup.
        /// If the device does not advertise within the scan window (its radio
        /// may still be powering on after cold boot), one more scan is given
        /// before giving up. Background reconnect handles the device turning
        /// on later.
        /// </summary>
        public async Task<bool> TryAutoConnectAsync()
        {
            if (IsConnected)
                return true;

            if (!_settings.AutoConnect || string.IsNullOrEmpty(_settings.LastDeviceAddress))
                return false;

            StatusChanged?.Invoke(this, "Attempting auto-connect...");

            // LCT coolers advertise on a short interval, so a 5s scan reliably
            // catches a powered-on device.
            bool advertising = await ScanForTargetAsync(5000, _settings.LastDeviceAddress);

            if (!advertising)
            {
                // The cooler's BLE radio may still be starting up after cold boot.
                // Give it one more chance before giving up.
                await Task.Delay(3000);
                StatusChanged?.Invoke(this, "Auto-connect: cooler not advertising yet, trying again...");
                advertising = await ScanForTargetAsync(5000, _settings.LastDeviceAddress);
            }

            bool connected = false;
            if (advertising)
            {
                // The device is actively advertising, so the attempt succeeds or
                // fails quickly — no stall on a stale cache entry. A THROW from the
                // last attempt must not skip the watcher start below, so it is caught
                // here and treated as a failed connect.
                try
                {
                    connected = await ConnectAsync(_settings.LastDeviceAddress);
                }
                catch (Exception ex)
                {
                    Log.Warning("[WaterCooler] Auto-connect scan found the device but ConnectAsync threw: {Message}", ex.Message);
                    connected = false;
                }
            }

            if (connected)
            {
                await RestoreSettingsAfterConnectAsync();
                StatusChanged?.Invoke(this, "Auto-connected successfully");
            }
            else
            {
                StatusChanged?.Invoke(this, advertising
                    ? "Auto-connect failed (connection error)"
                    : "Auto-connect failed (device not found)");

                // Nothing connected — keep a passive advertisement watcher
                // running so the cooler connects automatically when it is
                // powered on later.
                StartPendingDeviceWatcher();
            }

            return connected;
        }

        /// <summary>
        /// Pauses briefly for the nRF52 BLE stack to stabilize after connecting,
        /// then restores saved pump/fan/RGB settings to the device.
        /// WriteWithResponse ensures each command is acknowledged before the
        /// next fires, so no artificial inter-command delays are needed
        /// (matches Python reference). Smart Mode is intentionally not started
        /// here — temperature sources may not be ready yet; it starts later in
        /// DashboardViewModel.OnNavigatedToAsync() once temperature data is
        /// confirmed. If Smart Mode is enabled, pump/fan writes are skipped
        /// entirely (Smart Mode applies the correct speeds once it starts).
        /// </summary>
        private async Task RestoreSettingsAfterConnectAsync()
        {
            // Pause after connection for device BLE stack to stabilize.
            // The nRF52 needs time to transition from advertising to connected state
            // before it can reliably accept GATT write commands.
            await Task.Delay(500);

            try
            {
                if (!_settings.SmartModeEnabled)
                {
                    var pumpVoltage = _settings.GetPumpVoltage();
                    if (pumpVoltage != PumpVoltage.Off)
                        await WritePumpModeAsync(pumpVoltage);

                    var fanSpeed = _settings.GetFanSpeed();
                    if (fanSpeed != FanSpeed.Off)
                        await WriteFanModeAsync(fanSpeed);
                }

                var rgbMode = _settings.GetRgbMode();
                var rgbColor = _settings.GetRgbColor();
                if (_settings.RgbEnabled)
                    await WriteRgbModeAsync(rgbMode, rgbColor);
                else
                    await WriteRgbModeAsync(RgbState.Off, RgbColor.Red);
            }
            catch
            {
                // Non-critical: failed to restore settings after connect
            }
        }

        /// <summary>
        /// Scans for a specific device's advertisement. Returns false when the
        /// scan times out or fails — a blind FromBluetoothAddressAsync to a
        /// stale cache entry would stall startup, so callers must gate any
        /// connect on this result.
        /// </summary>
        private async Task<bool> ScanForTargetAsync(int timeoutMs, string targetAddress)
        {
            try
            {
                var target = NormalizeAddress(targetAddress);
                var devices = await DiscoverDevicesAsync(timeoutMs, targetAddress);
                return devices.Any(d =>
                    string.Equals(NormalizeAddress(d.Address), target, StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                // Non-critical: scan failed (e.g. Bluetooth radio off).
                // Treat as not advertising rather than attempting a blind connect.
                return false;
            }
        }

        /// <summary>
        /// Attempts to reconnect when the Hydro UI feature is re-enabled in Settings.
        /// Unlike <see cref="TryAutoConnectAsync"/> this does not require the per-device
        /// AutoConnect flag — it always tries. Strategy:
        ///  1. If already connected, return true immediately.
        ///  2. Scan for the last known device (or any LCT cooler) — the scan
        ///     gates the connect so a powered-off cooler fails fast instead of
        ///     stalling on a stale Windows BLE cache entry.
        ///  3. On success, restore saved pump/fan/RGB settings.
        /// </summary>
        public async Task<bool> TryReconnectOnFeatureEnableAsync()
        {
            if (IsConnected)
                return true;

            // Re-enabling the feature is explicit intent to connect — lift the
            // user-disconnected flag set when the feature was disabled, so the
            // pending-device watcher (started below on failure) can still fire.
            ClearUserDisconnected();

            StatusChanged?.Invoke(this, "Re-enabling Hydro UI — attempting connection...");

            bool connected = false;
            string? connectAddress = null;
            string? connectName = null;

            // Scan first: a powered-on cooler advertises within the window and
            // connecting is quick. A blind cache lookup (FromBluetoothAddressAsync)
            // can stall for a long time when the cooler is powered off but still
            // in the Windows BLE cache.
            StatusChanged?.Invoke(this, "Scanning for LCT coolers...");
            try
            {
                var devices = await DiscoverDevicesAsync(10000, _settings.LastDeviceAddress);
                var target = NormalizeAddress(_settings.LastDeviceAddress);

                bool lastKnownAdvertising = target != null && devices.Any(d =>
                    string.Equals(NormalizeAddress(d.Address), target, StringComparison.OrdinalIgnoreCase));

                if (lastKnownAdvertising)
                {
                    // The last known device is advertising — connect to the saved address.
                    var match = devices.First(d =>
                        string.Equals(NormalizeAddress(d.Address), target, StringComparison.OrdinalIgnoreCase));
                    connectAddress = _settings.LastDeviceAddress;
                    connectName = match.Name;
                }
                else if (devices.Count > 0)
                {
                    // A different/unknown cooler is advertising — connect to the first found.
                    connectAddress = devices[0].Address;
                    connectName = devices[0].Name;
                }
            }
            catch
            {
                // Scan failed — nothing to connect to.
            }

            if (connectAddress != null)
            {
                StatusChanged?.Invoke(this, $"Found {connectName} — connecting...");
                connected = await ConnectAsync(connectAddress);
            }

            if (connected)
            {
                // Ensure the per-device AutoConnect flag is set so that future
                // startup reconnects (via TryAutoConnectAsync) also work.
                _settings.AutoConnect = true;
                SaveSettings();

                await Task.Delay(500);

                // Restore saved pump/fan/RGB settings.
                try
                {
                    if (!_settings.SmartModeEnabled)
                    {
                        var pumpVoltage = _settings.GetPumpVoltage();
                        if (pumpVoltage != PumpVoltage.Off)
                            await WritePumpModeAsync(pumpVoltage);

                        var fanSpeed = _settings.GetFanSpeed();
                        if (fanSpeed != FanSpeed.Off)
                            await WriteFanModeAsync(fanSpeed);
                    }

                    var rgbMode = _settings.GetRgbMode();
                    var rgbColor = _settings.GetRgbColor();
                    if (_settings.RgbEnabled)
                        await WriteRgbModeAsync(rgbMode, rgbColor);
                    else
                        await WriteRgbModeAsync(RgbState.Off, RgbColor.Red);
                }
                catch
                {
                    // Non-critical: failed to restore settings after reconnect.
                }

                StatusChanged?.Invoke(this, "Hydro UI re-enabled — connected successfully");
            }
            else
            {
                StatusChanged?.Invoke(this, "Hydro UI re-enabled — no cooler found");

                // No cooler found — keep a passive advertisement watcher
                // running so it connects automatically when powered on later.
                StartPendingDeviceWatcher();
            }

            return connected;
        }

        private async Task<bool> WriteFrameAsync(byte[] frame)
        {
            if (_txCharacteristic == null)
                return false;

            try
            {
                // Use WriteWithResponse for reliable delivery.
                // The nRF52 UART TX characteristic drops WriteWithoutResponse packets when
                // multiple commands are queued rapidly after connection (BLE radio TX buffer overflow).
                // Python reference uses Bleak's default reliable write — match that behavior here.
                var status = await _txCharacteristic.WriteValueAsync(
                    frame.AsBuffer(), GattWriteOption.WriteWithResponse);
                return status == GattCommunicationStatus.Success;
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke(this, $"Write failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Builds a fixed 8-byte protocol frame.
        /// Format: FE [CMD] [EN/DIS] [PARAM_A] [PARAM_B] [PARAM_C] [PAD] EF
        /// No checksum — the device parses fixed byte offsets.
        /// </summary>
        private static byte[] BuildFrame(byte command, bool enabled, byte paramA, byte paramB, byte paramC)
        {
            return new byte[]
            {
                WaterCoolerCommand.FrameStart,
                command,
                enabled ? WaterCoolerCommand.Enabled : WaterCoolerCommand.Disabled,
                paramA,
                paramB,
                paramC,
                WaterCoolerCommand.Disabled, // padding
                WaterCoolerCommand.FrameEnd
            };
        }

        private void OnRxValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
        {
            var data = args.CharacteristicValue.ToArray();
            if (data.Length < 2) return;

            // Check for framed protocol message (FE ... EF)
            if (data[0] == WaterCoolerCommand.FrameStart && data[^1] == WaterCoolerCommand.FrameEnd && data.Length >= 4)
            {
                byte command = data[1];

                // Extract payload (between enable byte and checksum)
                int payloadStart = 3;
                int payloadLength = data.Length - 5; // minus FE, cmd, enable, checksum, EF
                if (payloadLength >= 0)
                {
                    byte[] payload = new byte[payloadLength];
                    Array.Copy(data, payloadStart, payload, 0, payloadLength);

                    switch (command)
                    {
                        case WaterCoolerCommand.Status:
                            // Status response: pump_status(1), fan_rpm_low(1), fan_rpm_high(1), temperature(1)
                            if (payload.Length >= 4)
                            {
                                byte pumpStatus = payload[0];
                                ushort fanRpm = (ushort)((payload[2] << 8) | payload[1]);
                                byte temperature = payload[3];
                                StatusReceived?.Invoke(this, (pumpStatus, fanRpm, temperature));
                            }
                            break;

                        case WaterCoolerCommand.MeterPush:
                        case WaterCoolerCommand.QueryMeter:
                            // Meter response: compact 5-byte frame FE 31/32 [?] [state] EF
                            // data[3] is the state byte (2 = flow OK, 1 = fault)
                            if (data.Length >= 4)
                            {
                                byte stateByte = data[3];
                                Debug.WriteLine($"[WaterCooler] Meter response cmd=0x{command:X2}, state={stateByte}, frame={{ {string.Join(" ", data.Select(b => b.ToString("X2"))) } }}");
                                UpdateFlowStatus(stateByte);
                            }
                            break;

                        case WaterCoolerCommand.Pump:
                            // Track pump state for flow grace period
                            bool pumpOn = data.Length >= 3 && data[2] == WaterCoolerCommand.Enabled;
                            OnPumpStateChanged(pumpOn);
                            StatusChanged?.Invoke(this, $"Command 0x{command:X2} acknowledged");
                            break;

                        case WaterCoolerCommand.Fan:
                        case WaterCoolerCommand.Rgb:
                            // Command acknowledgment
                            StatusChanged?.Invoke(this, $"Command 0x{command:X2} acknowledged");
                            break;
                    }
                }
            }
            else
            {
                // Unframed text message — firmware version response
                try
                {
                    string text = System.Text.Encoding.UTF8.GetString(data).Trim('\0', '\r', '\n', ' ');
                    if (!string.IsNullOrEmpty(text) && (text.Contains("FW") || text.Contains("F/W") || text.Contains("Version")))
                    {
                        FirmwareVersion = text;
                        Debug.WriteLine($"[WaterCooler] Firmware version: {text}");
                        FirmwareVersionReceived?.Invoke(this, text);
                    }
                }
                catch { /* not a UTF-8 string — ignore */ }
            }
        }

        /// <summary>
        /// Fires when the OS-level GATT session status changes.
        /// We use this to know when the radio link truly closes before reconnecting.
        /// When the session closes unexpectedly (BLE drops while the user is on another
        /// page), this triggers cleanup and starts the background reconnect loop so the
        /// user sees the correct state on next navigation to the Hydro UI page.
        /// </summary>
        private void OnGattSessionStatusChanged(GattSession sender, GattSessionStatusChangedEventArgs args)
        {
            Debug.WriteLine($"[WaterCooler] GattSession status: {args.Status}");

            if (args.Status == GattSessionStatus.Closed && _sessionClosedTcs != null)
            {
                // Signal that the OS-level session has truly closed.
                _sessionClosedTcs.TrySetResult(true);
            }

            if (args.Status == GattSessionStatus.Closed)
            {
                // If we still hold a device AND tx characteristic reference, and we're NOT
                // in the middle of ConnectAsync or DisconnectAsync, this is an unexpected
                // disconnect (e.g., BLE radio dropped the link while user was on another page).
                // A Closed event during ConnectAsync is part of normal session lifecycle.
                // A Closed event during DisconnectAsync is expected (we're disposing the session).
                bool isConnected = _device != null && _txCharacteristic != null;
                bool isConnecting = _isConnecting;
                bool isDisconnecting = _isDisconnectingInt == 1;
                if (isConnected && !isConnecting && !isDisconnecting)
                {
                    Debug.WriteLine("[WaterCooler] Unexpected GATT session close — cleaning up and reconnecting");
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await DisconnectAsync();
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[WaterCooler] Unexpected disconnect cleanup failed: {ex.Message}");
                        }
                    });
                }
            }
        }

        /// <summary>
        /// Waits for the current GATT session to actually close at the OS level, with a timeout.
        /// Returns true if it closed within the timeout; false otherwise (zombie link likely).
        /// </summary>
        private async Task<bool> WaitForSessionClosedAsync(int timeoutMs = 3000)
        {
            if (_sessionClosedTcs == null || _sessionClosedTcs.Task.IsCompleted)
                return true; // No active session to wait for

            try
            {
                using var cts = new System.Threading.CancellationTokenSource(timeoutMs);
                cts.Token.Register(() => _sessionClosedTcs?.TrySetResult(false));
                return await _sessionClosedTcs.Task.WaitAsync(cts.Token);
            }
            catch (System.Threading.Tasks.TaskCanceledException)
            {
                Debug.WriteLine("[WaterCooler] Session close timed out — zombie link likely");
                return false;
            }
            catch (OperationCanceledException)
            {
                Debug.WriteLine("[WaterCooler] Session close timed out — zombie link likely");
                return false;
            }
        }

        private async Task WriteResetCommandAsync()
        {
            if (_txCharacteristic == null) return;
            try
            {
                var frame = new byte[]
                {
                    WaterCoolerCommand.FrameStart,
                    WaterCoolerCommand.Reset,
                    WaterCoolerCommand.Disabled,
                    0x01, 0x00, 0x00, 0x00,
                    WaterCoolerCommand.FrameEnd
                };
                // Use WriteWithoutResponse for disconnect teardown — the device BLE radio may
                // already be powering down and won't acknowledge a WriteWithResponse, which would
                // block indefinitely. Fire-and-forget is correct here (matches Python reference).
                await _txCharacteristic.WriteValueAsync(
                    frame.AsBuffer(), GattWriteOption.WriteWithoutResponse);
            }
            catch
            {
                // Non-critical: reset command failed during disconnect teardown
            }
        }

        public void Dispose()
        {
            StopSmartMode();
            StopReconnectLoop();
            StopPendingDeviceWatcher();

            // Fire-and-forget disconnect to avoid deadlock on UI thread
            _ = DisconnectAsync().ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    try { t.Exception?.Flatten().GetBaseException(); }
                    catch
                    {
                        // Non-critical: failed to inspect exception during fire-and-forget disconnect
                    }
                }
            }, TaskContinuationOptions.OnlyOnFaulted);

            foreach (var watcher in _watchers)
                watcher.Stop();

            // Synchronous fallback cleanup in case async disconnect doesn't complete
            try { _gattSession?.Dispose(); }
            catch
            {
                // GATT session dispose failed; harmless during Dispose
            }
            try { _device?.Dispose(); }
            catch
            {
                // Device dispose failed; harmless during Dispose
            }

            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Connection state changes raised by the service.
        /// </summary>
        public enum WatercoolerConnectionState
        {
            Connected,
            Disconnected,
            Scanning
        }
    }
}
