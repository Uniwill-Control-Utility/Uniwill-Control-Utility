using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
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
    public sealed class WaterCoolerService : IDisposable
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

        public event EventHandler<WatercoolerConnectionState>? ConnectionStateChanged;
        public event EventHandler<string>? StatusChanged;

        /// <summary>
        /// Raised when a status response is received from the device.
        /// </summary>
        public event EventHandler<(byte PumpStatus, ushort FanRpm, byte Temperature)>? StatusReceived;

        private WaterCoolerSettings _settings = new();

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

        public WaterCoolerService()
        {
            LoadSettings();

            // Auto-connect on app startup (like THRM does after a brief delay).
            // Only connects if AutoConnect is enabled in settings.
            // The 2-second delay allows the Bluetooth stack to stabilize after cold boot.
            _ = Task.Run(async () =>
            {
                await Task.Delay(2000, CancellationToken.None);
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
                var filePath = Path.Combine(SettingsFolder, "watercooler_settings.json");

                if (File.Exists(filePath))
                {
                    var json = File.ReadAllText(filePath);
                    _settings = JsonConvert.DeserializeObject<WaterCoolerSettings>(json) ?? new WaterCoolerSettings();
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
        /// </summary>
        public async Task<List<WaterCoolerDeviceInfo>> DiscoverDevicesAsync(int timeoutMs = 10000)
        {
            var devices = new List<WaterCoolerDeviceInfo>();

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
                    lock (devices)
                    {
                        devices.Add(new WaterCoolerDeviceInfo
                        {
                            Address = e.BluetoothAddress.ToString(),
                            Name = name,
                            Rssi = e.RawSignalStrengthInDBm,
                            Model = DetectModelFromBleName(name)
                        });
                    }
                }
            };

            watcher.Start();
            await Task.Delay(timeoutMs);
            watcher.Stop();
            _watchers.Remove(watcher);

            // Deduplicate by address
            return devices.DistinctBy(d => d.Address).ToList();
        }

        #region Device Identification

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
        /// Returns "XMG Oasis", "TUXEDO Aquaris", "PC Specialist", "Eluktronics", or "LCT Watercooler".
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

            return "LCT Watercooler";
        }

        /// <summary>
        /// Builds a human-readable device name from the BLE name and chassis brand.
        /// Examples: "XMG Oasis Mk2", "TUXEDO Aquaris Mk1", "LCT Watercooler"
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
            ClearUserDisconnected();
            const int maxRetries = 3;

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
                    _device = await BluetoothLEDevice.FromBluetoothAddressAsync(ulong.Parse(deviceAddress.Replace(":", "")));
                    if (_device == null) return false;

                    Debug.WriteLine($"[WaterCooler] Device acquired, ConnectionStatus={_device.ConnectionStatus}");

                    // Create a GattSession for explicit connection lifecycle control.
                    _gattSession = await GattSession.FromDeviceIdAsync(BluetoothDeviceId.FromId(_device.DeviceId));

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

                        // Store last connected device for auto-connect
                        _settings.LastDeviceAddress = deviceAddress;
                        SaveSettings();

                        // Stop any background reconnection loop — we're connected now.
                        StopReconnectLoop();

                        ConnectionStateChanged?.Invoke(this, WatercoolerConnectionState.Connected);
                        return true;
                    }

                    // Clean up if we got here (device opened but no TX characteristic)
                    await CleanupAfterFailedConnectAsync();
                    return false;
                }
                catch (Exception ex) when (attempt < maxRetries)
                {
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

            ConnectionStateChanged?.Invoke(this, WatercoolerConnectionState.Disconnected);

            // Start background reconnection loop if this wasn't a user-initiated disconnect.
            // This handles cases where the BLE link drops unexpectedly (e.g., Bluetooth
            // off→on, device goes out of range then returns).
            if (!_userDisconnected && _settings.AutoConnect)
            {
                _connectGeneration++; // Invalidate any in-flight connection attempt
                StartReconnectLoop();
            }
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
            return await WriteFrameAsync(frame);
        }

        /// <summary>
        /// Attempts to auto-connect to the last known device on startup.
        /// </summary>
        public async Task<bool> TryAutoConnectAsync()
        {
            if (IsConnected)
                return true;

            if (!_settings.AutoConnect || string.IsNullOrEmpty(_settings.LastDeviceAddress))
                return false;

            StatusChanged?.Invoke(this, "Attempting auto-connect...");
            var connected = await ConnectAsync(_settings.LastDeviceAddress);

            if (connected)
            {
                // Pause after connection for device BLE stack to stabilize.
                // The nRF52 needs time to transition from advertising to connected state
                // before it can reliably accept GATT write commands.
                await Task.Delay(500);

                // Restore saved settings to device.
                // WriteWithResponse ensures each command is acknowledged before the next fires,
                // so no artificial inter-command delays are needed (matches Python reference).
                try
                {
                    var pumpVoltage = _settings.GetPumpVoltage();
                    if (pumpVoltage != PumpVoltage.Off)
                        await WritePumpModeAsync(pumpVoltage);

                    var fanSpeed = _settings.GetFanSpeed();
                    if (fanSpeed != FanSpeed.Off)
                        await WriteFanModeAsync(fanSpeed);

                    var rgbMode = _settings.GetRgbMode();
                    var rgbColor = _settings.GetRgbColor();
                    if (_settings.RgbEnabled && rgbMode != RgbState.Off)
                        await WriteRgbModeAsync(rgbMode, rgbColor);
                }
                catch
                {
                    // Non-critical: failed to restore settings during auto-connect
                }

                StatusChanged?.Invoke(this, "Auto-connected successfully");
            }
            else
            {
                StatusChanged?.Invoke(this, "Auto-connect failed (device not found)");
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
            if (data.Length < 4) return; // Minimum frame: FE cmd enable EF

            // Parse frame: FE [cmd] [enable] [payload...] [checksum] EF
            if (data[0] != WaterCoolerCommand.FrameStart || data[^1] != WaterCoolerCommand.FrameEnd)
                return;

            byte command = data[1];

            // Extract payload (between enable byte and checksum)
            int payloadStart = 3;
            int payloadLength = data.Length - 5; // minus FE, cmd, enable, checksum, EF
            if (payloadLength < 0) return;

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

                case WaterCoolerCommand.Pump:
                case WaterCoolerCommand.Fan:
                case WaterCoolerCommand.Rgb:
                    // Command acknowledgment — could be used for confirmation UI
                    StatusChanged?.Invoke(this, $"Command 0x{command:X2} acknowledged");
                    break;
            }
        }

        /// <summary>
        /// Fires when the OS-level GATT session status changes.
        /// We use this to know when the radio link truly closes before reconnecting.
        /// </summary>
        private void OnGattSessionStatusChanged(GattSession sender, GattSessionStatusChangedEventArgs args)
        {
            Debug.WriteLine($"[WaterCooler] GattSession status: {args.Status}");

            if (args.Status == GattSessionStatus.Closed && _sessionClosedTcs != null)
            {
                // Signal that the OS-level session has truly closed.
                _sessionClosedTcs.TrySetResult(true);
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
            StopReconnectLoop();

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
