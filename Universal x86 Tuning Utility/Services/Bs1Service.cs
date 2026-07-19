using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Universal_x86_Tuning_Utility.Models;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace Universal_x86_Tuning_Utility.Services
{
    /// <summary>
    /// BLE communication layer for the Flydigi BS1 cooling pad.
    /// The BS1 uses custom Flydigi GATT service UUIDs (0xFFF0/FFF1/FFF2) — NOT the Nordic UART profile.
    /// Communication uses a custom 5A A5 protocol on top. Unlike BS2+, the BS1 is BLE-only
    /// (no HID), requires heartbeat keepalive packets, and has no RGB support.
    /// </summary>
    public class Bs1Service : IDisposable
    {
        // Flydigi BS1 GATT UUIDs (verified against THRM source)
        private static readonly Guid Bs1ServiceUuid = new(0x0000fff0, 0x0000, 0x1000, 0x80, 0x00, 0x00, 0x80, 0x5f, 0x9b, 0x34, 0xfb);
        private static readonly Guid Bs1WriteCharUuid = new(0x0000fff2, 0x0000, 0x1000, 0x80, 0x00, 0x00, 0x80, 0x5f, 0x9b, 0x34, 0xfb);
        private static readonly Guid Bs1NotifyCharUuid = new(0x0000fff1, 0x0000, 0x1000, 0x80, 0x00, 0x00, 0x80, 0x5f, 0x9b, 0x34, 0xfb);

        private BluetoothLEDevice? _device;
        private GattSession? _gattSession;
        private GattCharacteristic? _txCharacteristic;
        private GattCharacteristic? _rxCharacteristic;

        // Track when the OS-level GATT session actually closes.
        private TaskCompletionSource<bool>? _sessionClosedTcs;

        // Log to both Debug and Trace so output appears in Release builds too.
        private static void Log(string message)
        {
            Debug.WriteLine(message);
            Trace.WriteLine(message);
        }

        // Heartbeat timer to keep the BLE connection alive.
        private Timer? _heartbeatTimer;
        private const int HeartbeatIntervalMs = 2500; // Send every 2.5s (device expects ~3s)

        // Settings persistence
        private Bs1Settings _settings = new();

        // Fan state tracking
        private bool _realtimeMode;
        private ushort _lastCommandedRpm;

        /* ------------------------------------------------------------------ */
        /*  Public events                                                      */
        /* ------------------------------------------------------------------ */

        public event EventHandler<bool>? ConnectionStateChanged;
        public event EventHandler<string>? StatusChanged;
        public event EventHandler<FanRpmData>? FanDataReceived;

        /// <summary>Latest RPM data known to the service.</summary>
        public FanRpmData? FanRpmData { get; private set; }

        public bool IsConnected => _device != null && _txCharacteristic != null;

        /// <summary>Human-readable name of the connected cooler (e.g. "Flydigi BS1").</summary>
        public string ConnectedDeviceName { get; private set; } = string.Empty;

        /* ------------------------------------------------------------------ */
        /*  Constructor / Settings                                             */
        /* ------------------------------------------------------------------ */

        private static readonly string SettingsFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UXTU");

        public Bs1Service()
        {
            LoadSettings();
        }

        /// <summary>
        /// Attempts to auto-reconnect to the last known device on app startup.
        /// </summary>
        public async Task<bool> TryAutoConnectAsync()
        {
            if (!_settings.AutoConnect || string.IsNullOrEmpty(_settings.LastDeviceAddress))
                return false;

            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    var success = await ConnectAsync(_settings.LastDeviceAddress);
                    if (success)
                    {
                        Log($"[BS1] Auto-connect successful");
                        return true;
                    }
                }
                catch
                {
                    // Retry after backoff
                }

                if (attempt < 2)
                {
                    int delay = 1000 * (attempt + 1); // 1s, 2s
                    await Task.Delay(delay);
                }
            }

            Log("[BS1] Auto-connect failed after 3 attempts");
            return false;
        }

        private void LoadSettings()
        {
            try
            {
                var filePath = Path.Combine(SettingsFolder, "bs1_settings.json");
                if (File.Exists(filePath))
                {
                    var json = File.ReadAllText(filePath);
                    _settings = JsonConvert.DeserializeObject<Bs1Settings>(json) ?? new Bs1Settings();
                }
            }
            catch
            {
                _settings = new Bs1Settings();
            }
        }

        public void SaveSettings()
        {
            try
            {
                Directory.CreateDirectory(SettingsFolder);
                var filePath = Path.Combine(SettingsFolder, "bs1_settings.json");
                var json = JsonConvert.SerializeObject(_settings, Newtonsoft.Json.Formatting.Indented);
                File.WriteAllText(filePath, json);
            }
            catch { /* non-critical */ }
        }

        public Bs1Settings GetSettings() => _settings;

        public void PersistSettings() => SaveSettings();

        /* ------------------------------------------------------------------ */
        /*  Device Discovery                                                   */
        /* ------------------------------------------------------------------ */

        /// <summary>
        /// Discovers Flydigi BS1 devices via BLE advertising and paired device enumeration.
        /// </summary>
        public async Task<List<Bs1DeviceInfo>> DiscoverDevicesAsync(int timeoutMs = 10000)
        {
            var devices = new List<Bs1DeviceInfo>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1. Try to reconnect to the last known device (if address is saved)
            if (!string.IsNullOrEmpty(_settings.LastDeviceAddress))
            {
                try
                {
                    Log($"[BS1] Trying last known device: {_settings.LastDeviceAddress}");

                    // The saved address may be a Windows DeviceId or a raw Bluetooth address
                    BluetoothLEDevice? lastDevice = null;
                    bool isDeviceId = _settings.LastDeviceAddress.StartsWith("\\\\?\\") ||
                                      _settings.LastDeviceAddress.Contains("#") ||
                                      _settings.LastDeviceAddress.StartsWith("BluetoothLE");

                    if (isDeviceId)
                    {
                        lastDevice = await BluetoothLEDevice.FromIdAsync(_settings.LastDeviceAddress).AsTask();
                    }
                    else
                    {
                        lastDevice = await BluetoothLEDevice.FromBluetoothAddressAsync(
                            ParseBluetoothAddress(_settings.LastDeviceAddress)).AsTask();
                    }

                    if (lastDevice != null)
                    {
                        var name = lastDevice.Name ?? "Flydigi BS1";
                        Log($"[BS1] Last known device found: {name} ({_settings.LastDeviceAddress})");
                        devices.Add(new Bs1DeviceInfo
                        {
                            Address = _settings.LastDeviceAddress,
                            Name = name,
                            Rssi = 0
                        });
                        seen.Add(_settings.LastDeviceAddress);
                    }
                }
                catch (Exception ex)
                {
                    Log($"[BS1] Failed to connect to last known device: {ex.Message}");
                }
            }

            // 2. Enumerate paired/unpaired BLE devices (may find devices that aren't actively advertising)
            // Skip if we already found the last known device — enumeration can take 20-40 seconds
            if (devices.Count == 0)
            {
                try
                {
                    Log("[BS1] Enumerating paired BLE devices...");
                    var selector = BluetoothLEDevice.GetDeviceSelectorFromPairingState(true);
                    var pairedDevices = await Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(selector);
                    foreach (var deviceInfo in pairedDevices)
                    {
                        var name = deviceInfo.Name ?? string.Empty;

                        Log($"[BS1] Paired BLE device: {name} (Id={deviceInfo.Id})");

                        if (!string.IsNullOrEmpty(name) && IsBs1AdvertisingName(name))
                        {
                            // Store the DeviceId for FromIdAsync connection
                            var bs1Info = new Bs1DeviceInfo
                            {
                                Address = deviceInfo.Id,
                                Name = name,
                                Rssi = 0
                            };
                            devices.Add(bs1Info);
                            seen.Add(deviceInfo.Id);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"[BS1] Paired device enumeration failed: {ex.Message}");
                }

                // 2b. Also enumerate unpaired BLE devices
                try
                {
                    Log("[BS1] Enumerating unpaired BLE devices...");
                    var unpairedSelector = BluetoothLEDevice.GetDeviceSelectorFromPairingState(false);
                var unpairedDevices = await Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(unpairedSelector);
                foreach (var deviceInfo in unpairedDevices)
                {
                    var name = deviceInfo.Name ?? string.Empty;

                    Log($"[BS1] Unpaired BLE device: {name} (Id={deviceInfo.Id})");

                    if (!string.IsNullOrEmpty(name) && IsBs1AdvertisingName(name))
                    {
                        var bs1Info = new Bs1DeviceInfo
                        {
                            Address = deviceInfo.Id,
                            Name = name,
                            Rssi = 0
                        };
                        devices.Add(bs1Info);
                        seen.Add(deviceInfo.Id);
                    }
                }
            }
            catch (Exception ex)
                {
                    Log($"[BS1] Unpaired device enumeration failed: {ex.Message}");
                }
            } // end if (devices.Count == 0)

            // 3. Scan for BLE advertisements (finds unpaired devices)
            var watcher = new BluetoothLEAdvertisementWatcher();
            watcher.Received += (s, e) =>
            {
                var name = e.Advertisement.LocalName;
                var address = e.BluetoothAddress.ToString();

                // Check for Flydigi service UUID (0xFFF0) in advertising data
                bool hasFlydigiService = false;
                if (e.Advertisement.ServiceUuids != null)
                {
                    foreach (var uuid in e.Advertisement.ServiceUuids)
                    {
                        // Check for 16-bit UUID 0xFFF0 (Flydigi BS1 service)
                        // The Bluetooth base UUID is 0000xxxx-0000-1000-8000-00805F9B34FB
                        ushort uuid16 = (ushort)uuid.GetHashCode();
                        if (uuid16 == 0xFFF0)
                        {
                            hasFlydigiService = true;
                            break;
                        }
                    }
                }

                // Match by name OR by Flydigi service UUID
                bool nameMatch = !string.IsNullOrEmpty(name) && IsBs1AdvertisingName(name);

                if (nameMatch || hasFlydigiService)
                {
                    Log($"[BS1] BLE advertisement matched: '{name}' ({address}) RSSI={e.RawSignalStrengthInDBm}");

                    lock (devices)
                    {
                        if (seen.Add(address))
                        {
                            devices.Add(new Bs1DeviceInfo
                            {
                                Address = address,
                                Name = name ?? "Flydigi BS1",
                                Rssi = e.RawSignalStrengthInDBm
                            });
                        }
                    }
                }
            };

            watcher.Start();
            // Shorten scan if we already found devices via last-known or enumeration
            int actualTimeout = devices.Count > 0 ? Math.Min(3000, timeoutMs) : timeoutMs;
            Log($"[BS1] Starting BLE advertisement scan for {actualTimeout}ms...");
            await Task.Delay(actualTimeout);
            watcher.Stop();

            Log($"[BS1] Discovery complete. Found {devices.Count} device(s).");
            return devices;
        }

        /// <summary>
        /// Parses a Bluetooth address string into a ulong.
        /// Handles formats: "0x001A7DDA7113", "00:1A:7D:DA:71:13", "001A7DDA7113".
        /// </summary>
        private static ulong ParseBluetoothAddress(string address)
        {
            string clean = address.Replace(":", "").Replace("-", "");
            if (clean.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                clean = clean.Substring(2);
            return ulong.Parse(clean, System.Globalization.NumberStyles.HexNumber);
        }

        /// <summary>
        /// Checks if a BLE advertising name matches a known Flydigi BS1 device.
        /// </summary>
        private static bool IsBs1AdvertisingName(string name)
        {
            string lower = name.ToLowerInvariant();
            return lower.Contains("flydigi") ||
                   lower.Contains("bs1") ||
                   lower.Contains("cooling pad");
        }

        /// <summary>
        /// Awaits a WinRT connection operation with a timeout by racing against Task.Delay.
        /// Returns default(T) if the operation times out.
        /// NOTE: The operation MUST return immediately when the device isn't responding —
        /// WinRT operations on BLE devices should fail fast when the device is unreachable.
        /// </summary>
        private static async Task<T?> WithTimeoutAsync<T>(Func<Task<T>> operation, TimeSpan timeout, string label)
        {
            var timeoutCts = new CancellationTokenSource(timeout);
            try
            {
                var task = operation();
                var timeoutTask = Task.Delay(timeout, timeoutCts.Token);
                var completed = await Task.WhenAny(task, timeoutTask);
                if (completed == timeoutTask)
                {
                    Log($"[BS1] {label} timed out after {timeout.TotalSeconds}s");
                    return default;
                }
                return await task;
            }
            finally
            {
                timeoutCts.Cancel();
            }
        }

        /* ------------------------------------------------------------------ */
        /*  Connect / Disconnect                                               */
        /* ------------------------------------------------------------------ */

        /// <summary>
        /// Connects to a BLE device and prepares TX/RX characteristics.
        /// </summary>
        public async Task<bool> ConnectAsync(string deviceAddress)
        {
            const int maxRetries = 2;

            // Disconnect first if already connected
            if (_device != null)
            {
                Log("[BS1] Device handle exists before connect — forcing cleanup");
                await DisconnectAsync();
                GC.Collect();
                GC.WaitForPendingFinalizers();
                await Task.Delay(500);
            }

            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    Log($"[BS1] Connect attempt {attempt}/{maxRetries}, address={deviceAddress}");

                    // Determine if this is a Windows DeviceId or a raw Bluetooth address
                    bool isDeviceId = deviceAddress.StartsWith("\\\\?\\") || deviceAddress.Contains("#");

                    if (isDeviceId)
                    {
                        Log("[BS1] Address looks like a DeviceId, using FromIdAsync...");
                        _device = await WithTimeoutAsync<BluetoothLEDevice>(
                            () => BluetoothLEDevice.FromIdAsync(deviceAddress).AsTask(),
                            TimeSpan.FromSeconds(5), "FromIdAsync");
                    }
                    else
                    {
                        Log("[BS1] Address looks like a Bluetooth address, attempting to resolve DeviceId first...");
                        // Parse the address: handle "0x001A7DDA7113", "00:1A:7D:DA:71:13", or raw hex
                        string clean = deviceAddress.Replace(":", "").Replace("-", "");
                        if (clean.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                            clean = clean.Substring(2);

                        ulong address;
                        if (!ulong.TryParse(clean, System.Globalization.NumberStyles.HexNumber, null, out address))
                        {
                            Log($"[BS1] Failed to parse Bluetooth address: {deviceAddress}");
                            return false;
                        }

                        Log($"[BS1] Parsed Bluetooth address: 0x{address:X12}");

                        // Try to find a DeviceId by enumerating BLE devices and matching the Bluetooth address
                        string? resolvedDeviceId = null;
                        try
                        {
                            var allSelector = BluetoothLEDevice.GetDeviceSelector();
                            var allDevices = await Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(allSelector);
                            foreach (var info in allDevices)
                            {
                                // Check if this device's DeviceId contains our Bluetooth address
                                var deviceId = info.Id;
                                if (deviceId.Contains(clean, StringComparison.OrdinalIgnoreCase) ||
                                    deviceId.Contains(address.ToString("X12"), StringComparison.OrdinalIgnoreCase))
                                {
                                    resolvedDeviceId = deviceId;
                                    Log($"[BS1] Found matching DeviceId: {resolvedDeviceId} (name: {info.Name})");
                                    break;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Log($"[BS1] DeviceId resolution failed: {ex.Message}");
                        }

                        if (resolvedDeviceId != null)
                        {
                            Log("[BS1] Using resolved DeviceId for connection...");
                            _device = await WithTimeoutAsync<BluetoothLEDevice>(
                                () => BluetoothLEDevice.FromIdAsync(resolvedDeviceId).AsTask(),
                                TimeSpan.FromSeconds(5), "FromIdAsync (resolved)");
                        }
                        else
                        {
                            Log("[BS1] No DeviceId found, falling back to FromBluetoothAddressAsync...");
                            _device = await WithTimeoutAsync<BluetoothLEDevice>(
                                () => BluetoothLEDevice.FromBluetoothAddressAsync(address).AsTask(),
                                TimeSpan.FromSeconds(5), "FromBluetoothAddressAsync");
                        }
                    }

                    if (_device == null)
                    {
                        Log("[BS1] Connection returned null, will retry...");
                        await Task.Delay(1000);
                        continue;
                    }

                    Log($"[BS1] Device acquired: {_device.Name}, ConnectionStatus={_device.ConnectionStatus}");

                    // Create GattSession for explicit connection lifecycle control (with timeout)
                    Log("[BS1] Creating GattSession...");
                    _gattSession = await WithTimeoutAsync<GattSession>(
                        () => GattSession.FromDeviceIdAsync(BluetoothDeviceId.FromId(_device.DeviceId)).AsTask(),
                        TimeSpan.FromSeconds(5), "GattSession creation");

                    if (_gattSession == null)
                    {
                        Log("[BS1] GattSession creation timed out, retrying...");
                        await CleanupAfterFailedConnectAsync();
                        await Task.Delay(1000);
                        continue;
                    }

                    _sessionClosedTcs = new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    _gattSession.SessionStatusChanged += OnGattSessionStatusChanged;

                    // Discover Flydigi BS1 GATT service (0xFFF0)
                    Log("[BS1] Discovering GATT services...");
                    var services = await _device.GetGattServicesForUuidAsync(Bs1ServiceUuid);
                    Log($"[BS1] Service discovery status: {services.Status}, found {services.Services.Count} service(s)");

                    // Fallback: enumerate ALL services if UUID-filtered discovery found nothing
                    if (services.Services.Count == 0)
                    {
                        Log("[BS1] UUID-filtered discovery returned 0 services, trying GetAllServicesAsync...");
                        services = await _device.GetGattServicesAsync();
                        Log($"[BS1] GetAllServicesAsync status: {services.Status}, found {services.Services.Count} service(s)");

                        foreach (var svc in services.Services)
                            Log($"[BS1]   Service: {svc.Uuid}");
                    }

                    foreach (var deviceService in services.Services)
                    {
                        Log($"[BS1] Found service: {deviceService.Uuid}");

                        // TX characteristic (write commands) - 0xFFF2
                        var txResults = await deviceService.GetCharacteristicsForUuidAsync(Bs1WriteCharUuid);
                        Log($"[BS1] TX characteristic discovery: {txResults.Status}, found {txResults.Characteristics.Count}");

                        // Fallback: enumerate all characteristics if UUID-filtered discovery found nothing
                        if (txResults.Characteristics.Count == 0)
                        {
                            txResults = await deviceService.GetCharacteristicsAsync();
                            Log($"[BS1] GetAllCharacteristicsAsync for TX: {txResults.Status}, found {txResults.Characteristics.Count}");
                        }

                        foreach (var characteristic in txResults.Characteristics)
                        {
                            Log($"[BS1] TX characteristic properties: {characteristic.CharacteristicProperties}, UUID: {characteristic.Uuid}");
                            if ((characteristic.CharacteristicProperties & GattCharacteristicProperties.Write) != 0 ||
                                (characteristic.CharacteristicProperties & GattCharacteristicProperties.WriteWithoutResponse) != 0)
                            {
                                _txCharacteristic = characteristic;
                                Log("[BS1] Found TX characteristic");
                                break;
                            }
                        }

                        // RX characteristic (notifications) - 0xFFF1
                        var rxResults = await deviceService.GetCharacteristicsForUuidAsync(Bs1NotifyCharUuid);
                        Log($"[BS1] RX characteristic discovery: {rxResults.Status}, found {rxResults.Characteristics.Count}");

                        if (rxResults.Characteristics.Count == 0)
                        {
                            rxResults = await deviceService.GetCharacteristicsAsync();
                            Log($"[BS1] GetAllCharacteristicsAsync for RX: {rxResults.Status}, found {rxResults.Characteristics.Count}");
                        }

                        foreach (var characteristic in rxResults.Characteristics)
                        {
                            if ((characteristic.CharacteristicProperties & GattCharacteristicProperties.Notify) != 0)
                            {
                                _rxCharacteristic = characteristic;
                                var status = await _rxCharacteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                                    GattClientCharacteristicConfigurationDescriptorValue.Notify);
                                Log($"[BS1] Found RX characteristic, notify status={status}");
                                if (status == GattCommunicationStatus.Success)
                                    _rxCharacteristic.ValueChanged += OnRxValueChanged;
                                break;
                            }
                        }
                    }

                    Log($"[BS1] GATT discovery complete: TX={(_txCharacteristic != null)}, RX={(_rxCharacteristic != null)}");

                    if (_txCharacteristic != null)
                    {
                        // Verify device is actually responsive by sending a heartbeat
                        // and waiting for a status notification. The BLE GATT connection
                        // succeeds even when the device's fan is off, but an off device
                        // won't respond to protocol commands.
                        bool deviceResponded = await VerifyDeviceResponsiveAsync();
                        if (!deviceResponded)
                        {
                            Log("[BS1] Device connected via BLE but not responding (device may be off)");
                            await CleanupAfterFailedConnectAsync();
                            await Task.Delay(1000);
                            continue;
                        }

                        ConnectedDeviceName = _device.Name ?? "Flydigi BS1";

                        // Store last connected device for auto-connect
                        _settings.LastDeviceAddress = deviceAddress;
                        SaveSettings();

                        // Start heartbeat to keep connection alive
                        StartHeartbeat();

                        ConnectionStateChanged?.Invoke(this, true);
                        StatusChanged?.Invoke(this, $"Connected to {ConnectedDeviceName}");
                        Log("[BS1] Connection successful!");
                        return true;
                    }

                    // Cleanup on failure - TX characteristic not found
                    Log("[BS1] Connection failed: TX characteristic not found");
                    await CleanupAfterFailedConnectAsync();
                    return false;
                }
                catch (Exception ex)
                {
                    Log($"[BS1] Connect attempt {attempt} exception: {ex.GetType().Name} - {ex.Message}");
                    Log($"[BS1] Stack trace: {ex.StackTrace}");

                    if (attempt >= maxRetries)
                    {
                        // Last attempt - propagate the error info
                        StatusChanged?.Invoke(this, $"Connection failed: {ex.Message}");
                        Log($"[BS1] All {maxRetries} connection attempts exhausted");
                    }
                    else
                    {
                        StatusChanged?.Invoke(this, $"Attempt {attempt} failed ({ex.Message}), retrying...");
                    }

                    await CleanupAfterFailedConnectAsync();
                    if (attempt < maxRetries)
                        await Task.Delay(1000);
                }
            }

            return false;
        }

        /// <summary>
        /// Disconnects from the current device cleanly.
        /// </summary>
        public async Task DisconnectAsync()
        {
            Log("[BS1] DisconnectAsync called");

            // Stop heartbeat first
            StopHeartbeat();

            // Unsubscribe from RX notifications
            if (_rxCharacteristic != null)
            {
                _rxCharacteristic.ValueChanged -= OnRxValueChanged;
                try
                {
                    await _rxCharacteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                        GattClientCharacteristicConfigurationDescriptorValue.None);
                }
                catch { /* non-critical */ }
            }

            // Close ALL GATT services to force radio teardown
            if (_device != null)
            {
                try
                {
                    var services = await _device.GetGattServicesAsync();
                    foreach (var service in services.Services)
                        service.Dispose();
                }
                catch { /* non-critical */ }
            }

            await Task.Delay(300);

            // Dispose GattSession
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
                catch { /* non-critical */ }
                sessionToDispose.Dispose();

                await WaitForSessionClosedAsync(3000);
            }
            _sessionClosedTcs = null;

            // Dispose device
            if (_device != null)
            {
                var deviceToDispose = _device;
                _device = null;
                deviceToDispose.Dispose();
            }

            // Clear state
            _txCharacteristic = null;
            _rxCharacteristic = null;
            _realtimeMode = false;
            _lastCommandedRpm = 0;

            ConnectionStateChanged?.Invoke(this, false);
            StatusChanged?.Invoke(this, "Disconnected");
        }

        /* ------------------------------------------------------------------ */
        /*  Heartbeat                                                          */
        /* ------------------------------------------------------------------ */

        private void StartHeartbeat()
        {
            StopHeartbeat();
            _heartbeatTimer = new Timer(
                _ => _ = SendHeartbeatAsync(),
                null,
                TimeSpan.FromSeconds(2), // First heartbeat after 2s
                TimeSpan.FromMilliseconds(HeartbeatIntervalMs));
        }

        private void StopHeartbeat()
        {
            _heartbeatTimer?.Dispose();
            _heartbeatTimer = null;
        }

        private async Task SendHeartbeatAsync()
        {
            if (_txCharacteristic == null)
                return;

            try
            {
                var frame = Bs1Frame.BuildHeartbeat();
                await _txCharacteristic.WriteValueAsync(frame.AsBuffer(), GattWriteOption.WriteWithResponse);
            }
            catch (Exception ex)
            {
                Log($"[BS1] Heartbeat failed: {ex.Message}");
            }
        }

        /* ------------------------------------------------------------------ */
        /*  Fan Commands                                                       */
        /* ------------------------------------------------------------------ */

        /// <summary>
        /// Sets the fan to a gear preset (1=Quiet, 2=Standard, 3=Strong, 4=Overclock).
        /// Exits realtime mode if currently active.
        /// </summary>
        public async Task WriteGearAsync(byte gear)
        {
            if (!IsConnected)
                return;

            // Exit realtime mode if active
            if (_realtimeMode)
            {
                await WriteFrameAsync(Bs1Frame.Build(Bs1Command.ExitRealtimeMode));
                _realtimeMode = false;
            }

            var payload = new[] { (byte)gear };
            await WriteFrameAsync(Bs1Frame.Build(Bs1Command.GearMode, payload));

            Log($"[BS1] Gear set to {gear}");
        }

        /// <summary>
        /// Sets the fan to a specific RPM (1300-3000).
        /// Enters realtime mode if not already active.
        /// </summary>
        public async Task WriteRpmAsync(ushort rpm)
        {
            if (!IsConnected)
                return;

            // Clamp to BS1 range
            rpm = (ushort)Math.Clamp(rpm, Bs1DefaultGearRpm.MinRpm, Bs1DefaultGearRpm.MaxRpm);

            // Enter realtime mode if not already active
            if (!_realtimeMode)
            {
                await WriteFrameAsync(Bs1Frame.Build(Bs1Command.EnterRealtimeMode));
                _realtimeMode = true;
            }

            // RPM as little-endian 16-bit
            var payload = new[]
            {
                (byte)(rpm & 0xFF),
                (byte)((rpm >> 8) & 0xFF)
            };

            await WriteFrameAsync(Bs1Frame.Build(Bs1Command.RealtimeRpm, payload));

            _lastCommandedRpm = rpm;

            // Raise fan data event
            FanRpmData = new FanRpmData
            {
                TargetRpm = rpm,
                CurrentRpm = rpm,
                Mode = "Realtime"
            };
            FanDataReceived?.Invoke(this, FanRpmData.Value);

            Log($"[BS1] RPM set to {rpm}");
        }

        /// <summary>
        /// Turns off the fan (sends 0 RPM).
        /// </summary>
        public async Task WriteFanOffAsync()
        {
            if (!IsConnected)
                return;

            if (_realtimeMode)
            {
                await WriteFrameAsync(Bs1Frame.Build(Bs1Command.ExitRealtimeMode));
                _realtimeMode = false;
            }

            // Send 0 RPM to stop the fan
            var payload = new byte[] { 0x00, 0x00 };
            await WriteFrameAsync(Bs1Frame.Build(Bs1Command.RealtimeRpm, payload));

            _lastCommandedRpm = 0;

            FanRpmData = new FanRpmData
            {
                TargetRpm = 0,
                CurrentRpm = 0,
                Mode = "Off"
            };
            FanDataReceived?.Invoke(this, FanRpmData.Value);
        }

        /// <summary>
        /// Writes a raw protocol frame to the TX characteristic.
        /// The BS1 uses WriteWithoutResponse (packet captures show Write Command 0x52).
        /// Falls back to WriteWithResponse if that fails.
        /// </summary>
        private async Task WriteFrameAsync(byte[] frame)
        {
            if (_txCharacteristic == null)
                return;

            try
            {
                await _txCharacteristic.WriteValueAsync(frame.AsBuffer(), GattWriteOption.WriteWithoutResponse);
            }
            catch
            {
                // Fallback to WriteWithResponse if WriteWithoutResponse fails
                await _txCharacteristic.WriteValueAsync(frame.AsBuffer(), GattWriteOption.WriteWithResponse);
            }
        }

        /* ------------------------------------------------------------------ */
        /*  RX Handler                                                         */
        /* ------------------------------------------------------------------ */

        private void OnRxValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
        {
            try
            {
                var data = args.CharacteristicValue.ToArray();
                var parsed = Bs1Frame.Parse(data);

                if (parsed == null)
                    return;

                if (!parsed.Value.ChecksumValid)
                    return;

                // Handle status notifications
                if (parsed.Value.Command == Bs1Command.StatusNotify)
                {
                    var status = Bs1StatusParser.Parse(parsed.Value.Payload);
                    if (status.HasValue)
                    {
                        var s = status.Value;
                        FanRpmData = new FanRpmData
                        {
                            TargetRpm = s.TargetRpm,
                            CurrentRpm = s.CurrentRpm,
                            Mode = s.IsRealtimeMode ? "Realtime" : "Gear"
                        };
                        FanDataReceived?.Invoke(this, FanRpmData.Value);

                        StatusChanged?.Invoke(this,
                            $"RPM: {s.CurrentRpm} (target: {s.TargetRpm}) [{(s.IsRealtimeMode ? "Realtime" : "Gear")}]");
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"[BS1] RX handler error: {ex.Message}");
            }
        }

        /* ------------------------------------------------------------------ */
        /*  Session Lifecycle                                                  */
        /* ------------------------------------------------------------------ */

        private void OnGattSessionStatusChanged(GattSession sender, GattSessionStatusChangedEventArgs args)
        {
            if (args.Status == GattSessionStatus.Closed)
            {
                Log("[BS1] GattSession closed");
                _sessionClosedTcs?.TrySetResult(true);

                // If we weren't already disconnecting, this is an unexpected disconnect
                if (_device != null)
                {
                    Log("[BS1] Unexpected device disconnect, cleaning up");
                    _ = DisconnectAsync();
                }
            }
        }

        private async Task<bool> WaitForSessionClosedAsync(int timeoutMs)
        {
            var tcs = _sessionClosedTcs;
            if (tcs == null)
                return true;

            try
            {
                return tcs.Task.Wait(timeoutMs);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Sends a heartbeat and waits for a status notification to verify the device
        /// is actually powered on and responsive. Returns false if no response within 3s.
        /// </summary>
        private async Task<bool> VerifyDeviceResponsiveAsync()
        {
            var respondedTcs = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            // Temporary handler to catch the first status notification
            void TempHandler(GattCharacteristic sender, GattValueChangedEventArgs args)
            {
                try
                {
                    var data = args.CharacteristicValue.ToArray();
                    var parsed = Bs1Frame.Parse(data);
                    if (parsed.HasValue && parsed.Value.ChecksumValid && parsed.Value.Command == Bs1Command.StatusNotify)
                        respondedTcs.TrySetResult(true);
                }
                catch
                {
                    // Ignore parse errors during verification
                }
            }

            try
            {
                // Subscribe the temp handler alongside the permanent one
                if (_rxCharacteristic != null)
                    _rxCharacteristic.ValueChanged += TempHandler;

                // Send heartbeat to provoke a status response
                var frame = Bs1Frame.BuildHeartbeat();
                await _txCharacteristic!.WriteValueAsync(frame.AsBuffer(), GattWriteOption.WriteWithResponse);

                // Wait up to 3 seconds for a response
                await respondedTcs.Task.WaitAsync(TimeSpan.FromSeconds(3));
                return respondedTcs.Task.Result;
            }
            catch (TaskCanceledException)
            {
                Log("[BS1] No response from device during verification (device may be off)");
                return false;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (_rxCharacteristic != null)
                    _rxCharacteristic.ValueChanged -= TempHandler;
            }
        }

        private async Task CleanupAfterFailedConnectAsync()
        {
            if (_rxCharacteristic != null)
            {
                _rxCharacteristic.ValueChanged -= OnRxValueChanged;
                _rxCharacteristic = null;
            }

            if (_gattSession != null)
            {
                _gattSession.SessionStatusChanged -= OnGattSessionStatusChanged;
                var session = _gattSession;
                _gattSession = null;
                try { session.Dispose(); } catch { }
                await WaitForSessionClosedAsync(2000);
            }
            _sessionClosedTcs = null;

            if (_device != null)
            {
                var device = _device;
                _device = null;
                device.Dispose();
            }

            _txCharacteristic = null;
        }

        /* ------------------------------------------------------------------ */
        /*  IDisposable                                                        */
        /* ------------------------------------------------------------------ */

        public void Dispose()
        {
            StopHeartbeat();
            _ = DisconnectAsync(); // Fire-and-forget cleanup
        }
    }
}
