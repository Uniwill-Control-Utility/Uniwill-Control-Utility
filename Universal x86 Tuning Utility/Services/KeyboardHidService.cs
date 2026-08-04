using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Universal_x86_Tuning_Utility.Models;

namespace Universal_x86_Tuning_Utility.Services
{
    /// <summary>
    /// Controls keyboard backlight via HID feature reports sent to the ITE lighting controller.
    /// 
    /// The keyboard backlight on XMG/TUXEDO laptops is NOT controlled through EC registers.
    /// Instead, an ITE HID controller (vid_048d) handles all keyboard lighting via 9-byte
    /// feature reports. The EC registers 0x078C (KBD_STATUS) and 0x0767 (TRIGGER) are
    /// logo-only and do not affect the keyboard backlight.
    /// 
    /// This implementation is based on MechControl (decompiled from MechControl.dll).
    /// </summary>
    public sealed class KeyboardHidService : IDisposable
    {
        #region Constants

        // ITE vendor ID (0x048d) — the lighting controller vendor
        private const ushort ITE_VID = 0x048D;
        private const string ITE_VID_STRING = "vid_048d";

        // HID device zones
        public const int ZoneKeyboard = 0;
        public const int ZoneLightBar = 2;
        public const int ZoneLogo = 3;

        // Zone masks (used in 0x08 brightness/mode reports)
        private const byte ZoneMaskKeyboard = 0x02;
        private const byte ZoneMaskLightBar = 0x22;
        private const byte ZoneMaskLogo = 0x23;

        // HID feature report command types
        private const byte CMD_KEYBOARD_OFF = 0x09;  // Keyboard-only special off command
        private const byte CMD_ZONE_RESET = 0x12;    // Generic zone reset
        private const byte CMD_MODE_BRIGHTNESS = 0x08; // Zone mode/speed/brightness config
        private const byte CMD_SET_COLOR = 0x14;     // Set RGB color
        private const byte CMD_ZONE_ON_OFF = 0x1A;   // Zone on/off enable

        // HID report size
        private const int FEATURE_REPORT_SIZE = 9;

        // ITE 8291 per-key RGB constants (from Linux hid-ite8291r3 driver)
        // Protocol: control via hid_hw_raw_request (feature report), color data via hid_hw_output_report
        private const int ITE_NR_ROWS = 6;
        private const int ITE_LEDS_PER_ROW = 21;
        private const int ITE_OUTPUT_REPORT_SIZE = 65; // 2 header + 3*21 color bytes
        private const byte ITE_PARAM_MODE_USER = 0x33; // Custom/UserMode effect
        public const int MaxPerKeyZones = ITE_NR_ROWS * ITE_LEDS_PER_ROW; // 126

        /// <summary>
        /// Maps each KeyboardZone index (0-125) to ITE row/col (0-5, 0-20).
        /// ITE row 0 = bottom (modifiers), row 5 = top (Esc/F-row).
        /// Columns 0-20 left to right across the full keyboard including numpad.
        ///
        /// Row 5 (top) is the reference grid with uniform columns.
        /// Other rows have variable spacing — columns were calibrated empirically
        /// from user feedback (clicking key A lights up key B → A's col = B's firmware col).
        ///
        /// Row 0 firmware layout (calibrated):
        ///   col 0=LCtrl, 1=unused, 2=Fn, 3=Win, 4=LAlt, 5=Space,
        ///   10=RAlt, 12=RCtrl, 13=←, 14=↑, 15=→, 18=↓,
        ///   16-17=numpad0, 19=numpad.
        ///
        /// Numpad rows 1-4 use cols 15-18 (shifted -1 from row 5 which uses 16-19).
        /// </summary>
        private static readonly Dictionary<int, (int Row, int Col)> ZoneToIteMapping = new()
        {
            // === ITE Row 5 (top): Esc + F1-F12 + PrtSc/Ins/Del + Home/End/PgUp/PgDn ===
            // Reference grid: X=0,36,72,...,690 → cols 0-19
            { 105, (5, 0) },  // Esc  X=0
            { 106, (5, 1) },  // F1   X=36
            { 107, (5, 2) },  // F2   X=72
            { 108, (5, 3) },  // F3   X=108
            { 109, (5, 4) },  // F4   X=144
            { 110, (5, 5) },  // F5   X=180
            { 111, (5, 6) },  // F6   X=216
            { 112, (5, 7) },  // F7   X=252
            { 113, (5, 8) },  // F8   X=288
            { 114, (5, 9) },  // F9   X=324
            { 115, (5, 10) }, // F10  X=360
            { 116, (5, 11) }, // F11  X=396
            { 117, (5, 12) }, // F12  X=432
            { 118, (5, 13) }, // PrtSc X=468
            { 119, (5, 14) }, // Ins  X=504
            { 120, (5, 15) }, // Del  X=538
            { 121, (5, 16) }, // Home X=576
            { 122, (5, 17) }, // End  X=614
            { 123, (5, 18) }, // PgUp X=652
            { 124, (5, 19) }, // PgDn X=690

            // === ITE Row 4: ` 1-0 - = Backspace + Numpad top ===
            // Main KB: cols 0-12 as visual position, col 13 unused, Bksp=14.
            // Numpad rows 1-4 use cols 15-18 (shifted -1 from row 5).
            { 84, (4, 0) },   // `    X=0
            { 85, (4, 1) },   // 1    X=30
            { 86, (4, 2) },   // 2    X=68
            { 87, (4, 3) },   // 3    X=106
            { 88, (4, 4) },   // 4    X=144
            { 89, (4, 5) },   // 5    X=182
            { 90, (4, 6) },   // 6    X=220
            { 91, (4, 7) },   // 7    X=258
            { 92, (4, 8) },   // 8    X=296
            { 93, (4, 9) },   // 9    X=334
            { 94, (4, 10) },  // 0    X=372
            { 95, (4, 11) },  // -    X=410
            { 96, (4, 12) },  // =    X=448
            { 98, (4, 14) },  // ⌫    X=487
            { 36, (4, 15) },  // Num  X=576 (numpad col 15)
            { 37, (4, 16) },  // /    X=614 (numpad col 16)
            { 38, (4, 17) },  // *    X=652 (numpad col 17)
            { 39, (4, 18) },  // -    X=690 (numpad col 18)

            // === ITE Row 3: Tab + QWERTY + Enter + Numpad ===
            // Tab spans cols 0-1 (48px wide). Q-P at cols 2-11.
            // [=12, ]=13, Enter=14 (X=511, right edge at 571 = RIGHT_EDGE)
            // Numpad rows 1-4 use cols 15-18.
            { 63, (3, 0) },   // Tab  X=0 (spans 0-1)
            { 65, (3, 2) },   // Q    X=50
            { 66, (3, 3) },   // W    X=88
            { 67, (3, 4) },   // E    X=126
            { 68, (3, 5) },   // R    X=164
            { 69, (3, 6) },   // T    X=202
            { 70, (3, 7) },   // Y    X=240
            { 71, (3, 8) },   // U    X=278
            { 72, (3, 9) },   // I    X=316
            { 73, (3, 10) },  // O    X=354
            { 74, (3, 11) },  // P    X=392
            { 75, (3, 12) },  // [    X=430
            { 76, (3, 13) },  // ]    X=468
            { 77, (3, 14) },  // ↵    X=511 (spans 14-15, right edge at RIGHT_EDGE)
            { 40, (3, 15) },  // 7    X=576 (numpad col 15)
            { 41, (3, 16) },  // 8    X=614 (numpad col 16)
            { 56, (3, 17) },  // 9    X=652 (numpad col 17)
            { 64, (3, 18) },  // +    X=690 (numpad col 18, spans 2 rows)

            // === ITE Row 2: Caps + ASDF + ;' \ + Numpad ===
            // Caps spans cols 0-1 (60px wide). A-L at cols 2-10.
            // ;=11, '=12, \=13. Numpad cols 15-17.
            { 42, (2, 0) },   // Caps X=0 (spans 0-1)
            { 44, (2, 2) },   // A    X=62
            { 45, (2, 3) },   // S    X=100
            { 46, (2, 4) },   // D    X=138
            { 47, (2, 5) },   // F    X=176
            { 48, (2, 6) },   // G    X=214
            { 49, (2, 7) },   // H    X=252
            { 50, (2, 8) },   // J    X=290
            { 51, (2, 9) },   // K    X=328
            { 52, (2, 10) },  // L    X=366
            { 53, (2, 11) },  // ;    X=404
            { 54, (2, 12) },  // '    X=442
            { 55, (2, 13) },  // \\   X=476
            { 57, (2, 15) },  // 4    X=576 (numpad col 15)
            { 58, (2, 16) },  // 5    X=614 (numpad col 16)
            { 59, (2, 17) },  // 6    X=652 (numpad col 17)

            // === ITE Row 1: LShift + ZXCV + RShift + Numpad ===
            // LShift=1, \\=2, Z=3, X=4, C=5, V=6, B=7, N=8, M=9
            // ,=10, .=11, /=12, col 13 unused, RShift=14 (spans 14-15)
            // col 0 is unused in firmware for this row.
            // Numpad cols 15-18.
            { 22, (1, 1) },   // ⇧    X=0 (LShift)
            { 23, (1, 2) },   // \\   X=48
            { 24, (1, 3) },   // Z    X=86
            { 25, (1, 4) },   // X    X=124
            { 26, (1, 5) },   // C    X=162
            { 27, (1, 6) },   // V    X=200
            { 28, (1, 7) },   // B    X=238
            { 29, (1, 8) },   // N    X=276
            { 30, (1, 9) },   // M    X=314
            { 31, (1, 10) },  // ,    X=352
            { 32, (1, 11) },  // .    X=390
            { 33, (1, 12) },  // /    X=428
            { 35, (1, 14) },  // ⇧    X=466 (RShift, spans 14-15)
            { 60, (1, 15) },  // 1    X=576 (numpad col 15)
            { 61, (1, 16) },  // 2    X=614 (numpad col 16)
            { 62, (1, 17) },  // 3    X=652 (numpad col 17)
            { 97, (1, 18) },  // ↵    X=690 (numpad col 18, spans 2 rows)

            // === ITE Row 0 (bottom): Modifiers + Space + Arrows + Numpad ===
            // Firmware layout (calibrated from user feedback):
            //   col 0=LCtrl, 1=unused, 2=Fn, 3=Win, 4=LAlt, 5=Space,
            //   10=RAlt, 12=RCtrl, 13=←, 14=↑, 15=→, 18=↓,
            //   16-17=numpad0, 19=numpad.
            { 0, (0, 0) },    // Ctrl X=0
            { 2, (0, 2) },    // Fn   X=48
            { 3, (0, 3) },    // ⊞    X=86
            { 4, (0, 4) },    // Alt  X=124
            { 7, (0, 7) },    // Space X=162 (center of space bar)
            { 10, (0, 10) },  // Alt  X=352
            { 12, (0, 12) },  // Ctrl X=390
            { 13, (0, 13) },  // ←    X=452
            { 14, (0, 14) },  // ↑    X=492
            { 15, (0, 15) },  // →    X=532
            { 18, (0, 18) },  // ↓    X=492 (firmware col 18)
            { 99, (0, 16) },  // 0    X=576 (numpad)
            { 100, (0, 17) }, // .    X=652 (numpad col 17)
        };

        // Win32 constants
        private const int HIDP_STATUS_SUCCESS = 0x110000;
        private const uint DIGCF_PRESENT = 0x00000002;
        private const uint DIGCF_DEVICEINTERFACE = 0x00000010;
        private const uint GENERIC_READ = 0x80000000;
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint FILE_SHARE_READ = 0x00000001;
        private const uint FILE_SHARE_WRITE = 0x00000002;
        private const uint OPEN_EXISTING = 3;

        #endregion

        #region P/Invoke

        [DllImport("hid.dll", SetLastError = true)]
        private static extern void HidD_GetHidGuid(out Guid hidGuid);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_SetFeature(SafeFileHandle handle, byte[] report, int length);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_GetFeature(SafeFileHandle handle, byte[] report, int length);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out nint preparsedData);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_FreePreparsedData(nint preparsedData);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern int HidP_GetCaps(nint preparsedData, out HidpCaps caps);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern nint SetupDiGetClassDevs(ref Guid classGuid, nint enumerator, nint hwndParent, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInterfaces(nint deviceInfoSet, nint deviceInfoData, ref Guid interfaceClassGuid, uint memberIndex, ref SpDeviceInterfaceData deviceInterfaceData);

        [DllImport("setupapi.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern bool SetupDiGetDeviceInterfaceDetail(nint deviceInfoSet, ref SpDeviceInterfaceData deviceInterfaceData, nint detailData, uint detailDataSize, out uint requiredSize, nint deviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(nint deviceInfoSet);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string path, uint desiredAccess, uint shareMode, nint securityAttributes, uint creationDisposition, uint flagsAndAttributes, nint templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(SafeFileHandle hDevice, uint dwIoControlCode, nint lpInBuffer, uint nInBufferSize, nint lpOutBuffer, uint nOutBufferSize, out uint lpBytesReturned, nint lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteFile(SafeFileHandle hFile, byte[] lpBuffer, uint nNumberOfBytesToWrite, out uint lpNumberOfBytesWritten, nint lpOverlapped);

        #endregion

        #region IOCTL Constants

        // IOCTL_HID_SET_OUTPUT_REPORT = HID_IN_CTL_CODE(101)
        // CTL_CODE(FILE_DEVICE_KEYBOARD, 101, METHOD_IN_DIRECT, FILE_ANY_ACCESS)
        // FILE_DEVICE_KEYBOARD = 0x000B, METHOD_IN_DIRECT = 1, FILE_ANY_ACCESS = 0
        // (0x000B << 16) | (0 << 14) | (101 << 2) | 1 = 0x000B0195
        private const uint IOCTL_HID_SET_OUTPUT_REPORT = 0x000B0195;

        #endregion

       #region Structs

        [StructLayout(LayoutKind.Sequential)]
        private struct HidOutputReport
        {
            public byte ReportId;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 65)]
            public byte[] Data;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SpDeviceInterfaceData
        {
            public int CbSize;
            public Guid InterfaceClassGuid;
            public int Flags;
            public nint Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HidpCaps
        {
            public ushort Usage;
            public ushort UsagePage;
            public ushort InputReportByteLength;
            public ushort OutputReportByteLength;
            public ushort FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
            public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes;
            public ushort NumberInputButtonCaps;
            public ushort NumberInputValueCaps;
            public ushort NumberInputDataIndices;
            public ushort NumberOutputButtonCaps;
            public ushort NumberOutputValueCaps;
            public ushort NumberOutputDataIndices;
            public ushort NumberFeatureButtonCaps;
            public ushort NumberFeatureValueCaps;
            public ushort NumberFeatureDataIndices;
        }

        #endregion

        #region State

        private readonly object _lock = new object();
        private SafeFileHandle? _keyboardHandle;
        private bool _disposed;

        #endregion

        /// <summary>
        /// Gets the current keyboard brightness level (0-100).
        /// </summary>
        public int Brightness { get; private set; }

        /// <summary>
        /// Gets the current keyboard color.
        /// </summary>
        public (byte R, byte G, byte B) Color { get; private set; }

        /// <summary>
        /// Gets whether the keyboard backlight is currently on.
        /// </summary>
        public bool IsOn { get; private set; }

        /// <summary>
        /// Gets whether the HID keyboard zone is available on this hardware.
        /// </summary>
        public bool IsAvailable { get; private set; }

        #region Initialization

        /// <summary>
        /// Discovers and opens the ITE HID lighting controller for keyboard (zone 0).
        /// Returns true if a keyboard lighting controller was found and opened.
        /// </summary>
        public bool Open()
        {
            lock (_lock)
            {
                if (_keyboardHandle != null && !_keyboardHandle.IsInvalid)
                    return IsAvailable;

                var devicePaths = FindIteDevicePaths();
                DebugLog($"[KBD-HID] Found {devicePaths.Count} ITE HID device paths");

                // Find devices with 9-byte feature reports
                var lightingDevices = new List<(string Path, ushort Usage)>();
                foreach (var path in devicePaths)
                {
                    DebugLog($"[KBD-HID] Probing: {path}");
                    var handle = TryOpenHandle(path);
                    if (handle == null)
                    {
                        DebugLog($"[KBD-HID]   Cannot open (error {Marshal.GetLastWin32Error()})");
                        continue;
                    }

                    try
                    {
                        if (!HidD_GetPreparsedData(handle, out nint preparsedData))
                        {
                            DebugLog("[KBD-HID]   GetPreparsedData failed");
                            continue;
                        }

                        try
                        {
                            int status = HidP_GetCaps(preparsedData, out HidpCaps caps);
                            DebugLog($"[KBD-HID]   GetCaps status=0x{status:X8} feature={caps.FeatureReportByteLength} usage=0x{caps.Usage:X4}");

                            if (status == HIDP_STATUS_SUCCESS && caps.FeatureReportByteLength == FEATURE_REPORT_SIZE)
                            {
                                lightingDevices.Add((path, caps.Usage));
                                DebugLog("[KBD-HID]   -> Lighting controller");
                            }
                        }
                        finally
                        {
                            HidD_FreePreparsedData(preparsedData);
                        }
                    }
                    finally
                    {
                        handle.Dispose();
                    }
                }

                DebugLog($"[KBD-HID] {lightingDevices.Count} lighting devices (9-byte feature reports)");

                // Map by usage: usage 1 = keyboard (zone 0), usage 2 = light bar + logo
                var usageMap = new Dictionary<ushort, string>();
                foreach (var (path, usage) in lightingDevices)
                {
                    usageMap.TryAdd(usage, path);
                }

                bool found = false;

                if (usageMap.ContainsKey(1) || usageMap.ContainsKey(2))
                {
                    found = true;
                    if (usageMap.TryGetValue(1, out string? keyboardPath))
                    {
                        _keyboardHandle = OpenWriteHandle(keyboardPath);
                        DebugLog($"[KBD-HID] Zone 0 (keyboard): {(_keyboardHandle != null && !_keyboardHandle.IsInvalid ? "opened" : "FAILED")}");
                    }
                }

                // Fallback: try first available device
                if (!found && lightingDevices.Count > 0)
                {
                    _keyboardHandle = OpenWriteHandle(lightingDevices[0].Path);
                    DebugLog($"[KBD-HID] Fallback zone 0: {(_keyboardHandle != null && !_keyboardHandle.IsInvalid ? "opened" : "FAILED")}");
                }

                IsAvailable = _keyboardHandle != null && !_keyboardHandle.IsInvalid;
                return IsAvailable;
            }
        }

        private static List<string> FindIteDevicePaths()
        {
            var list = new List<string>();
            HidD_GetHidGuid(out Guid hidGuid);
            nint deviceInfoSet = SetupDiGetClassDevs(ref hidGuid, nint.Zero, nint.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);

            if (deviceInfoSet == nint.Zero || deviceInfoSet == new IntPtr(-1))
                return list;

            try
            {
                SpDeviceInterfaceData ifaceData = default;
                ifaceData.CbSize = Marshal.SizeOf(ifaceData);

                for (uint i = 0; SetupDiEnumDeviceInterfaces(deviceInfoSet, nint.Zero, ref hidGuid, i, ref ifaceData); i++)
                {
                    SetupDiGetDeviceInterfaceDetail(deviceInfoSet, ref ifaceData, nint.Zero, 0, out uint requiredSize, nint.Zero);
                    nint buffer = Marshal.AllocHGlobal((int)requiredSize);

                    try
                    {
                        // Write the CSP_DEVICE_INTERFACE_DETAIL_DATA_A/W header size
                        Marshal.WriteInt32(buffer, Environment.Is64BitProcess ? 8 : 6);

                        if (SetupDiGetDeviceInterfaceDetail(deviceInfoSet, ref ifaceData, buffer, requiredSize, out _, nint.Zero))
                        {
                            string? path = Marshal.PtrToStringAnsi(buffer + 4);
                            if (!string.IsNullOrEmpty(path) && path.Contains(ITE_VID_STRING, StringComparison.OrdinalIgnoreCase))
                            {
                                list.Add(path);
                            }
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(buffer);
                    }
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(deviceInfoSet);
            }

            return list;
        }

        private static SafeFileHandle? TryOpenHandle(string path)
        {
            // Try different access modes: no access, read, write
            foreach (uint access in new[] { 0u, GENERIC_READ, GENERIC_WRITE })
            {
                var handle = CreateFile(path, access, FILE_SHARE_READ | FILE_SHARE_WRITE, nint.Zero, OPEN_EXISTING, 0, nint.Zero);
                if (!handle.IsInvalid)
                    return handle;
                handle.Dispose();
            }
            return null;
        }

        private static SafeFileHandle OpenWriteHandle(string path)
        {
            // Need READ|WRITE for DeviceIoControl IOCTL_HID_WRITE_REPORT (output reports)
            var handle = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, nint.Zero, OPEN_EXISTING, 0, nint.Zero);
            if (handle.IsInvalid)
            {
                handle.Dispose();
                handle = CreateFile(path, GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, nint.Zero, OPEN_EXISTING, 0, nint.Zero);
            }
            if (handle.IsInvalid)
            {
                handle.Dispose();
                handle = CreateFile(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, nint.Zero, OPEN_EXISTING, 0, nint.Zero);
            }
            return handle;
        }

        #endregion

        #region Keyboard Control

        /// <summary>
        /// Turns on the keyboard backlight with the specified color and brightness.
        /// Sends zone enable, RGB color, and CMD_MODE_BRIGHTNESS initialization reports.
        /// The CMD_MODE_BRIGHTNESS reports are required to initialize the controller's
        /// effect engine — without them, certain effects (0x0F, 0x10) produce black output.
        /// The effect byte here is Static (0x01) as a placeholder; callers should follow
        /// with SetEffect() to apply the actual desired effect.
        /// </summary>
        public void TurnOn(byte r, byte g, byte b, int brightness)
        {
            EnsureAvailable();
            brightness = Math.Clamp(brightness, 0, 100);

            lock (_lock)
            {
                // Report 1: Zone enable
                SendReport(new byte[] { 0x00, CMD_ZONE_ON_OFF, (byte)ZoneKeyboard, 0x01, 0x04, 0x00, 0x00, 0x00, 0x01 });

                // Report 2: Set RGB color
                SendReport(new byte[] { 0x00, CMD_SET_COLOR, (byte)ZoneKeyboard, 0x01, r, g, b, 0x00, 0x00 });

                // Report 3: Initialize effect engine (no zone mask)
                SendReport(new byte[] { 0x00, CMD_MODE_BRIGHTNESS, 0x00, 0x01, 0x05, (byte)brightness, 0x00, 0x00, 0x00 });

                // Report 4: Initialize effect engine (with zone mask)
                SendReport(new byte[] { 0x00, CMD_MODE_BRIGHTNESS, ZoneMaskKeyboard, 0x01, 0x05, (byte)brightness, 0x08, 0x00, 0x00 });

                _color = (r, g, b);
                _brightness = brightness;
                IsOn = true;

                DebugLog($"[KBD-HID] Keyboard ON — color=({r},{g},{b}), brightness={brightness}");
            }
        }

        /// <summary>
        /// Turns off the keyboard backlight.
        /// </summary>
        public void TurnOff()
        {
            EnsureAvailable();

            lock (_lock)
            {
                // Keyboard (zone 0) has a special off command
                SendReport(new byte[] { 0x00, CMD_KEYBOARD_OFF, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 });

                // Generic zone reset
                SendReport(new byte[] { 0x00, CMD_ZONE_RESET, 0x00, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00 });

                // Mode/brightness clear
                SendReport(new byte[] { 0x00, CMD_MODE_BRIGHTNESS, 0x05, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 });

                // Zone-specific clear
                SendReport(new byte[] { 0x00, CMD_MODE_BRIGHTNESS, 0x01, (byte)ZoneKeyboard, 0x00, 0x00, 0x00, 0x00, 0x00 });

                // Zone off
                SendReport(new byte[] { 0x00, CMD_ZONE_ON_OFF, (byte)ZoneKeyboard, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01 });

                IsOn = false;
                DebugLog("[KBD-HID] Keyboard OFF");
            }
        }

        /// <summary>
        /// Updates the keyboard color and brightness without turning it off.
        /// Report format: 00 08 02 [effect] [speed] [brightness] 08 00 00
        /// </summary>
        public void SetColor(byte r, byte g, byte b, int brightness)
        {
            EnsureAvailable();
            brightness = Math.Clamp(brightness, 0, 100);

            lock (_lock)
            {
                // Report 1: Set RGB color
                SendReport(new byte[] { 0x00, CMD_SET_COLOR, (byte)ZoneKeyboard, 0x01, r, g, b, 0x00, 0x00 });

                // Report 2: Set brightness (effect=Static 0x01, speed=0x05 medium)
                SendReport(new byte[] { 0x00, CMD_MODE_BRIGHTNESS, ZoneMaskKeyboard, 0x01, 0x05, (byte)brightness, 0x08, 0x00, 0x00 });

                _color = (r, g, b);
                _brightness = brightness;

                DebugLog($"[KBD-HID] Color update — color=({r},{g},{b}), brightness={brightness}");
            }
        }

        /// <summary>
        /// Saves the current keyboard settings to BIOS so they persist across reboots.
        /// </summary>
        public void SaveToBios(bool enabled)
        {
            EnsureAvailable();

            lock (_lock)
            {
                if (enabled)
                {
                    // Persistent color report (byte[3]=9 for BIOS persistence, byte[8]=1)
                    SendReport(new byte[] { 0x00, CMD_SET_COLOR, (byte)ZoneKeyboard, 0x09, _color.R, _color.G, _color.B, 0x00, 0x00 });

                    // Persistent zone enable
                    SendReport(new byte[] { 0x00, CMD_ZONE_ON_OFF, 0x00, 0x01, 0x04, 0x00, 0x00, 0x00, 0x01 });

                    // Persistent color with byte[8]=1
                    SendReport(new byte[] { 0x00, CMD_SET_COLOR, (byte)ZoneKeyboard, 0x01, _color.R, _color.G, _color.B, 0x00, 0x01 });

                    // Persistent brightness
                    SendReport(new byte[] { 0x00, CMD_MODE_BRIGHTNESS, ZoneMaskKeyboard, 0x01, 0x01, (byte)_brightness, 0x08, 0x00, 0x01 });
                }
                else
                {
                    // Save disabled state
                    SendReport(new byte[] { 0x00, CMD_ZONE_ON_OFF, (byte)ZoneKeyboard, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01 });
                }

                DebugLog($"[KBD-HID] Saved to BIOS (enabled={enabled})");
            }
        }

        private void SendReport(byte[] report)
        {
            if (report.Length != FEATURE_REPORT_SIZE)
                throw new ArgumentException($"Feature report must be exactly {FEATURE_REPORT_SIZE} bytes");

            if (_keyboardHandle == null || _keyboardHandle.IsInvalid)
                throw new InvalidOperationException("HID keyboard zone is not available");

            bool success = HidD_SetFeature(_keyboardHandle, report, report.Length);
            if (!success)
            {
                int error = Marshal.GetLastWin32Error();
                DebugLog($"[KBD-HID] HidD_SetFeature failed (error {error}): {string.Join(", ", report.Select(b => $"0x{b:X2}"))}");
            }

            DebugLog($"[KBD-HID] Sent: {string.Join(" ", report.Select(b => $"0x{b:X2}"))}");
        }

        /// <summary>
        /// Sends an 8-byte feature report matching the Linux driver protocol.
        /// Linux: hid_hw_raw_request(hdev, reportId, buf, 8, HID_FEATURE_REPORT, HID_REQ_SET_REPORT)
        /// On Windows this maps to HidD_SetFeature with the report ID as byte[0].
        /// </summary>
        private void SendControlReport(byte reportId, byte[] data)
        {
            if (data.Length != 8)
                throw new ArgumentException($"Control report must be exactly 8 bytes");

            if (_keyboardHandle == null || _keyboardHandle.IsInvalid)
                throw new InvalidOperationException("HID keyboard zone is not available");

            // Build 9-byte report: [reportId] + 8 bytes of data
            byte[] report = new byte[9];
            report[0] = reportId;
            Array.Copy(data, 0, report, 1, 8);

            bool success = HidD_SetFeature(_keyboardHandle, report, report.Length);
            if (!success)
            {
                int error = Marshal.GetLastWin32Error();
                DebugLog($"[KBD-HID] SendControlReport(0x{reportId:X2}) failed (error {error})");
            }
            else
            {
                DebugLog($"[KBD-HID] SendControlReport(0x{reportId:X2}) sent: {string.Join(" ", report.Select(b => $"0x{b:X2}"))}");
            }
        }

        /// <summary>
        /// Sends a 65-byte output report to the ITE controller.
        /// Uses WriteFile to bypass HID translation layer (equivalent to Linux hid_hw_output_report).
        /// Format: [0x00, 0x00][B0..B20][G0..G20][R0..R20] (BGR-planar)
        /// </summary>
        private void SendOutputReport(byte[] data)
        {
            if (_keyboardHandle == null || _keyboardHandle.IsInvalid)
                throw new InvalidOperationException("HID keyboard zone is not available");

            if (data.Length != ITE_OUTPUT_REPORT_SIZE)
                throw new ArgumentException($"Output report must be exactly {ITE_OUTPUT_REPORT_SIZE} bytes");

            // Try WriteFile first (bypasses HID translation, equivalent to Linux output_report)
            bool success = WriteFile(_keyboardHandle, data, (uint)ITE_OUTPUT_REPORT_SIZE, out uint bytesWritten, nint.Zero);
            if (success)
            {
                DebugLog($"[KBD-HID] Output report sent via WriteFile ({bytesWritten} bytes)");
                return;
            }

            // Fallback to DeviceIoControl
            int writeFileError = Marshal.GetLastWin32Error();
            nint ptr = Marshal.AllocHGlobal(ITE_OUTPUT_REPORT_SIZE);
            try
            {
                Marshal.Copy(data, 0, ptr, ITE_OUTPUT_REPORT_SIZE);
                success = DeviceIoControl(_keyboardHandle, IOCTL_HID_SET_OUTPUT_REPORT, ptr, (uint)ITE_OUTPUT_REPORT_SIZE, nint.Zero, 0, out uint bytesReturned, nint.Zero);
                if (!success)
                {
                    int error = Marshal.GetLastWin32Error();
                    DebugLog($"[KBD-HID] Output report failed: WriteFile error {writeFileError}, IOCTL error {error}, bytes={bytesReturned}");
                }
                else
                {
                    DebugLog($"[KBD-HID] Output report sent via IOCTL ({bytesReturned} bytes)");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }

        /// <summary>
        /// Sets the keyboard lighting effect mode.
        /// Sends a CMD_MODE_BRIGHTNESS (0x08) report with the effect in byte[3], speed in byte[4],
        /// and direction in byte[7].
        /// Format: 00 08 02 [effect] [speed] [brightness] 08 [direction] 00
        /// Speed (byte[4]): 0x00 = fastest, 0x0B = frozen (effect active but no movement).
        /// Higher value = slower animation.
        /// Direction (byte[7]): 0x00=Left→Right, 0x02=Right→Left, 0x03=Down→Up, etc.
        /// </summary>
        public void SetEffect(KeyboardEffect effect, byte speed = 5, KeyboardDirection direction = KeyboardDirection.LeftRight)
        {
            EnsureAvailable();

            lock (_lock)
            {
                // Clamp speed to 0-0x0B range
                byte clampedSpeed = (byte)Math.Clamp((int)speed, 0, 0x0B);
                byte directionByte = (byte)direction;

                // For variant effects (TouchAurora, TouchSpark), the ITE
                // controller requires the base effect to be sent first, then the variant.
                // A delay is needed between primer and variant for the firmware to process.
                if (s_baseEffectForVariant.TryGetValue(effect, out var baseEffect))
                {
                    byte[] primer = new byte[]
                    {
                        0x00, CMD_MODE_BRIGHTNESS, ZoneMaskKeyboard,
                        (byte)baseEffect, clampedSpeed, (byte)_brightness,
                        0x08, directionByte, 0x00
                    };
                    SendReport(primer);
                    DebugLog($"[KBD-HID] Effect primer {baseEffect} (0x{(byte)baseEffect:X2}) for {effect}");

                    // The ITE controller needs time to process the primer before
                    // accepting the variant byte. Without this delay, the primer
                    // is overwritten and the variant falls back to Static or black.
                    System.Threading.Thread.Sleep(50);
                }

                byte[] report = new byte[]
                {
                    0x00,                    // Report ID
                    CMD_MODE_BRIGHTNESS,     // Command
                    ZoneMaskKeyboard,        // Zone mask (keyboard)
                    (byte)effect,            // Effect mode
                    clampedSpeed,            // Speed: 0=fastest, 0x0B=frozen
                    (byte)_brightness,       // Current brightness
                    0x08,                    // Unknown constant
                    directionByte,           // Direction: byte[7]
                    0x00                     // Reserved
                };

                SendReport(report);
                DebugLog($"[KBD-HID] Effect set to {effect} (0x{(byte)effect:X2}) speed={clampedSpeed} direction={direction}");
            }
        }

        /// <summary>
        /// Sends a direction override report that mimics the TurnOn() initialization
        /// format. This is needed because the firmware locks in byte[7] (direction)
        /// from the first CMD_MODE_BRIGHTNESS report with zone mask during initialization.
        /// Calling this immediately after TurnOn() but before SetEffect() ensures the
        /// firmware accepts the direction byte.
        /// </summary>
        public void SetDirection(KeyboardDirection direction)
        {
            EnsureAvailable();

            lock (_lock)
            {
                byte directionByte = (byte)direction;
                // Same format as TurnOn() report 4 but with the actual direction.
                byte[] report = new byte[]
                {
                    0x00, CMD_MODE_BRIGHTNESS, ZoneMaskKeyboard,
                    0x01,          // Static (safe init effect)
                    0x05,          // default speed
                    (byte)_brightness,
                    0x08, directionByte, 0x00
                };
                SendReport(report);
                DebugLog($"[KBD-HID] Direction override set to {direction} (0x{directionByte:X2})");
            }
        }

        /// <summary>
        /// Maps variant effects to their base effect that must be sent first.
        /// The ITE controller firmware requires the base effect to be armed before
        /// accepting the variant byte.
        /// </summary>
        private static readonly Dictionary<KeyboardEffect, KeyboardEffect> s_baseEffectForVariant = new()
        {
            { KeyboardEffect.TouchRipple, KeyboardEffect.Ripple },        // 0x07 needs 0x06
            { KeyboardEffect.TouchAurora, KeyboardEffect.Aurora },        // 0x0F needs 0x0E
            { KeyboardEffect.TouchSpark, KeyboardEffect.Spark },          // 0x10 needs 0x11
        };

        /// <summary>
        /// Sends up to 7 colors to the HID controller using CMD_SET_COLOR (0x14).
        /// Each color is sent with an index (1-7) so multi-color effects can reference them.
        /// Format per report: 00 14 00 [index] [R] [G] [B] 00 00
        /// </summary>
        public void SetMultiColor(IEnumerable<System.Windows.Media.Color> colors)
        {
            EnsureAvailable();

            lock (_lock)
            {
                int index = 1;
                foreach (var color in colors.Take(7))
                {
                    byte[] report = new byte[]
                    {
                        0x00,                       // Report ID
                        CMD_SET_COLOR,              // Command
                        (byte)ZoneKeyboard,         // Zone
                        (byte)index,                // Color index (1-7)
                        color.R, color.G, color.B,  // RGB
                        0x00, 0x00                  // Reserved
                    };
                    SendReport(report);
                    index++;
                }
                DebugLog($"[KBD-HID] Set {index - 1} multi-colors");
            }
        }

        /// <summary>
        /// Sets a single per-key zone color.
        /// </summary>
        public void SetPerKeyColor(int index, byte r, byte g, byte b)
        {
            EnsureAvailable();
            index = Math.Clamp(index, 0, MaxPerKeyZones - 1);

            lock (_lock)
            {
                SendReport(new byte[]
                {
                    0x00, CMD_SET_COLOR, (byte)ZoneKeyboard, (byte)index, r, g, b, 0x00, 0x00
                });
            }
        }

        /// <summary>
        /// Sends an arbitrary effect byte via CMD_MODE_BRIGHTNESS.
        /// </summary>
        public void SetEffectRaw(byte effect)
        {
            EnsureAvailable();

            lock (_lock)
            {
                SendReport(new byte[]
                {
                    0x00, CMD_MODE_BRIGHTNESS, ZoneMaskKeyboard,
                    effect, 0x05, (byte)_brightness, 0x08, 0x00, 0x00
                });
                DebugLog($"[KBD-HID] Raw effect 0x{effect:X2} sent");
            }
        }

        /// <summary>
        /// Exits per-key/User mode by sending the full keyboard off sequence.
        /// The ITE controller ignores standard effect commands while in UserMode.
        /// A full off sequence (CMD_KEYBOARD_OFF + CMD_ZONE_RESET + zone off) is
        /// required to clear UserMode state before effects commands take effect.
        /// </summary>
        public void ExitPerKeyMode()
        {
            EnsureAvailable();

            lock (_lock)
            {
                SendReport(new byte[] { 0x00, CMD_KEYBOARD_OFF, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 });
                SendReport(new byte[] { 0x00, CMD_ZONE_RESET, 0x00, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00 });
                SendReport(new byte[] { 0x00, CMD_ZONE_ON_OFF, (byte)ZoneKeyboard, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01 });
                DebugLog("[KBD-HID] Exited per-key/User mode");
            }
        }

        /// <summary>
        /// Updates brightness for per-key/User mode. Re-sends the UserMode command
        /// with the new brightness. The caller should then re-send per-key colors.
        /// Linux driver format: 08 02 33 00 [brightness 0-50] 00 00 00
        /// </summary>
        public void SetPerKeyBrightness(int brightness)
        {
            EnsureAvailable();
            brightness = Math.Clamp(brightness, 0, 100);
            _brightness = brightness;

            lock (_lock)
            {
                byte scaledBrightness = (byte)Math.Min(_brightness, 0x32);
                SendControlReport(0x08, new byte[]
                {
                    0x08, 0x02, ITE_PARAM_MODE_USER, 0x00,
                    scaledBrightness, 0x00, 0x00, 0x00
                });
                DebugLog($"[KBD-HID] Per-key brightness set to {brightness}");
            }
        }

        /// <summary>
        /// Enters per-key/User mode on the ITE controller.
        /// Linux driver format: 08 02 33 00 [brightness 0-50] 00 00 00
        /// </summary>
        public void SetPerKeyMode()
        {
            EnsureAvailable();

            lock (_lock)
            {
                byte scaledBrightness = (byte)Math.Min(_brightness, 0x32);
                SendControlReport(0x08, new byte[]
                {
                    0x08, 0x02, ITE_PARAM_MODE_USER, 0x00,
                    scaledBrightness, 0x00, 0x00, 0x00
                });
                DebugLog("[KBD-HID] Per-key mode (UserMode 0x33) sent");
            }
        }

        /// <summary>
        /// Sends a per-key row of colors. Used for testing individual rows.
        /// Protocol: SET_ROW_INDEX (0x16) via feature report, then 65-byte output report.
        /// </summary>
        public void SendPerKeyRow(byte rowIndex, byte[] colors)
        {
            EnsureAvailable();

            lock (_lock)
            {
                // Set row index via 0x16 (hid_hw_raw_request, feature report)
                SendControlReport(0x16, new byte[]
                {
                    0x16, 0x00, rowIndex, 0x00, 0x00, 0x00, 0x00, 0x00
                });

                // Build 65-byte output report: [0x00, 0x00][B0..B20][G0..G20][R0..R20]
                byte[] rowData = new byte[ITE_OUTPUT_REPORT_SIZE];
                int offset = 2;

                // colors is expected as BGR triples (3 bytes per key)
                int numKeys = Math.Min(colors.Length / 3, ITE_LEDS_PER_ROW);
                for (int i = 0; i < numKeys; i++)
                    rowData[offset + i] = colors[i * 3];       // Blue
                for (int i = 0; i < numKeys; i++)
                    rowData[offset + numKeys + i] = colors[i * 3 + 1]; // Green
                for (int i = 0; i < numKeys; i++)
                    rowData[offset + 2 * numKeys + i] = colors[i * 3 + 2]; // Red

                SendOutputReport(rowData);
                DebugLog($"[KBD-HID] Per-key row {rowIndex} sent ({numKeys} keys)");
            }
        }

        /// <summary>
        /// Sends a test pattern to all 126 positions so the user can verify which
        /// physical key lights up at which row/col. Each row gets a distinct color.
        /// Row 0 (bottom) = Red, Row 1 = Green, Row 2 = Blue, Row 3 = Yellow,
        /// Row 4 = Cyan, Row 5 (top) = Magenta.
        ///
        /// Uses the exact Linux driver protocol (ite_8291.c):
        /// - ctrl_params: [08 02 33 00 brightness 00 00 00] via report ID 0x08
        /// - ctrl_announce: [16 00 00 row 00 00 00 00] via report ID 0x16
        /// - output_report: 65 bytes via hdev->ll_driver->output_report
        /// </summary>
        public void SendTestPattern()
        {
            EnsureAvailable();

            lock (_lock)
            {
                byte scaledBrightness = (byte)Math.Min(_brightness, 0x32);

                // Linux driver ctrl_params: [08 02 33 00 brightness 00 00 00]
                // Report ID = buf[0] = 0x08
                SendControlReport(0x08, new byte[]
                {
                    0x08, 0x02, ITE_PARAM_MODE_USER, 0x00,
                    scaledBrightness, 0x00, 0x00, 0x00
                });

                // Distinct color per row: (R, G, B)
                var rowColors = new (byte R, byte G, byte B)[]
                {
                    (255, 0, 0),     // Row 0 (bottom) - Red
                    (0, 255, 0),     // Row 1 - Green
                    (0, 0, 255),     // Row 2 - Blue
                    (255, 255, 0),   // Row 3 - Yellow
                    (0, 255, 255),   // Row 4 - Cyan
                    (255, 0, 255),   // Row 5 (top) - Magenta
                };

                for (int row = 0; row < ITE_NR_ROWS; row++)
                {
                    // Linux driver ctrl_announce_row_data: [16 00 row 00 00 00 00 00]
                    // Report ID = buf[0] = 0x16
                    SendControlReport(0x16, new byte[]
                    {
                        0x16, 0x00, (byte)row, 0x00, 0x00, 0x00, 0x00, 0x00
                    });

                    // Build 65-byte output report
                    byte[] rowData = new byte[ITE_OUTPUT_REPORT_SIZE];
                    // Linux: ITE8291_ROW_DATA_PADDING = 2 (bytes 0 and 1 are padding)
                    // Then B[0..20], G[0..20], R[0..20]
                    int bOffset = 2;
                    int gOffset = 2 + ITE_LEDS_PER_ROW;
                    int rOffset = 2 + 2 * ITE_LEDS_PER_ROW;

                    var c = rowColors[row];
                    for (int col = 0; col < ITE_LEDS_PER_ROW; col++)
                    {
                        rowData[bOffset + col] = c.B;
                        rowData[gOffset + col] = c.G;
                        rowData[rOffset + col] = c.R;
                    }

                    SendOutputReport(rowData);
                    DebugLog($"[KBD-HID-TEST] Row {row} sent ({(row == 0 ? "Red" : row == 1 ? "Green" : row == 2 ? "Blue" : row == 3 ? "Yellow" : row == 4 ? "Cyan" : "Magenta")})");
                }

                DebugLog("[KBD-HID-TEST] Test pattern sent - each row should be a solid color");
            }
        }

        /// <summary>
        /// Sends per-key colors using the ite_8291r3 protocol from the Linux kernel driver.
        ///
        /// Protocol (hid-ite8291r3.c):
        /// 1. SET_EFFECT (cmd 8): [00 08 02 33 00 brightness 00 00 00] — enter UserMode
        /// 2. For each row 0..5:
        ///    a. SET_ROW_INDEX (cmd 22/0x16): [00 16 00 row 00 00 00 00 00]
        ///    b. Output report (65 bytes): [00 00][B0..B20][G0..G20][R0..R20]
        ///
        /// Control commands use hid_hw_raw_request (HID_FEATURE_REPORT) → HidD_SetFeature
        /// Color data uses hid_hw_output_report → DeviceIoControl IOCTL_HID_WRITE_REPORT
        /// </summary>
        public void SendAllPerKeyColorsFromDict(Dictionary<int, (byte R, byte G, byte B)> colorsDict)
        {
            EnsureAvailable();

            if (colorsDict == null || colorsDict.Count == 0)
                return;

            lock (_lock)
            {
                // Build a 6x21 color array (all black by default)
                var rows = new (byte R, byte G, byte B)[ITE_NR_ROWS, ITE_LEDS_PER_ROW];
                for (int r = 0; r < ITE_NR_ROWS; r++)
                    for (int c = 0; c < ITE_LEDS_PER_ROW; c++)
                        rows[r, c] = (0, 0, 0);

                // Map zone index to ITE row/col using the lookup table
                foreach (var kvp in colorsDict)
                {
                    int zoneIndex = kvp.Key;
                    if (ZoneToIteMapping.TryGetValue(zoneIndex, out var mapping))
                    {
                        rows[mapping.Row, mapping.Col] = kvp.Value;
                    }
                }

                // Step 1: Enter UserMode (Linux driver ctrl_params)
                // [08 02 33 00 brightness 00 00 00] via report ID 0x08
                byte scaledBrightness = (byte)Math.Min(_brightness, 0x32);
                SendControlReport(0x08, new byte[]
                {
                    0x08, 0x02, ITE_PARAM_MODE_USER, 0x00,
                    scaledBrightness, 0x00, 0x00, 0x00
                });

                // Step 2: For each row, announce row index then send 65-byte output report
                for (int row = 0; row < ITE_NR_ROWS; row++)
                {
                    // Linux driver ctrl_announce_row_data: [16 00 row 00 00 00 00 00]
                    SendControlReport(0x16, new byte[]
                    {
                        0x16, 0x00, (byte)row, 0x00, 0x00, 0x00, 0x00, 0x00
                    });

                    // Build 65-byte output report: [00 00][B0..B20][G0..G20][R0..R20]
                    byte[] rowData = new byte[ITE_OUTPUT_REPORT_SIZE];
                    // rowData[0..1] = 0x00, 0x00 (header)
                    int bOffset = 2;
                    int gOffset = 2 + ITE_LEDS_PER_ROW;
                    int rOffset = 2 + 2 * ITE_LEDS_PER_ROW;

                    for (int col = 0; col < ITE_LEDS_PER_ROW; col++)
                    {
                        var c = rows[row, col];
                        rowData[bOffset + col] = c.B;
                        rowData[gOffset + col] = c.G;
                        rowData[rOffset + col] = c.R;
                    }

                    SendOutputReport(rowData);
                }

                // Log which zones were mapped
                var mappedZones = new List<string>();
                foreach (var kvp in colorsDict)
                {
                    if (ZoneToIteMapping.TryGetValue(kvp.Key, out var m))
                        mappedZones.Add($"zone{kvp.Key}->r{m.Row},c{m.Col}");
                }
                DebugLog($"[KBD-HID] Per-key colors sent ({colorsDict.Count} zones, {mappedZones.Count} mapped, {ITE_NR_ROWS} rows)");
                if (mappedZones.Count <= 5)
                    DebugLog($"[KBD-HID] Mapped: {string.Join("; ", mappedZones)}");
            }
        }

        /// <summary>
        /// Sends an arbitrary 9-byte feature report to the HID controller.
        /// Use for testing/reverse-engineering effects.
        /// </summary>
        public void SendRawReport(byte[] report)
        {
            EnsureAvailable();
            SendReport(report);
        }

        /// <summary>
        /// Tries to read back the current feature report from the HID controller.
        /// Returns null if the device doesn't support reading features.
        /// </summary>
        public byte[]? ReadFeatureReport(byte reportId)
        {
            EnsureAvailable();

            lock (_lock)
            {
                byte[] buffer = new byte[FEATURE_REPORT_SIZE];
                buffer[0] = reportId;

                bool success = HidD_GetFeature(_keyboardHandle, buffer, buffer.Length);
                if (success)
                {
                    DebugLog($"[KBD-HID] ReadFeatureReport(0x{reportId:X2}) = {string.Join(", ", buffer.Select(b => $"0x{b:X2}"))}");
                    return buffer;
                }

                int error = Marshal.GetLastWin32Error();
                DebugLog($"[KBD-HID] HidD_GetFeature failed (error {error}) for reportId 0x{reportId:X2}");
                return null;
            }
        }

        #endregion

        #region State Tracking

        private (byte R, byte G, byte B) _color = (0, 255, 255); // Default aqua
        private int _brightness = 50;

        #endregion

        #region Helpers

        private void EnsureAvailable()
        {
            if (!IsAvailable)
                throw new InvalidOperationException("Keyboard HID controller is not available. This feature requires an ITE HID lighting controller (vid_048d).");
            if (_disposed)
                throw new ObjectDisposedException(nameof(KeyboardHidService));
        }

        #endregion

        #region Logging

        private static void DebugLog(string message)
        {
            Debug.WriteLine(message);
            System.Diagnostics.Trace.WriteLine(message);
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _keyboardHandle?.Dispose();
        }

        #endregion
    }
}
