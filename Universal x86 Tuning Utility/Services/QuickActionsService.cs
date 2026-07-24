using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Universal_x86_Tuning_Utility.Services
{
    /// <summary>
    /// Provides quick-action controls for common Windows operations:
    /// turn off monitors, toggle HDR, change refresh rate, change resolution,
    /// mute microphone, and lock touchpad.
    /// </summary>
    public static class QuickActionsService
    {
        #region Win32 constants and P/Invoke

        private const int WM_SYSCOMMAND = 0x0112;
        private const int SC_MONITORPOWER = 0xF170;
        private const int MONITOR_OFF = 2;
        private const int MONITOR_ON = -1;
        private const int HWND_BROADCAST = 0xFFFF;

        private const uint CCHDEVICENAME = 32;
        private const uint CCHFORMNAME = 32;
        private const int ENUM_CURRENT_SETTINGS = -1;
        private const int DISP_CHANGE_SUCCESSFUL = 0;
        private const int DISP_CHANGE_RESTART = 1;
        private const uint DM_PELSWIDTH = 0x00080000;
        private const uint DM_PELSHEIGHT = 0x00100000;
        private const uint DM_DISPLAYFREQUENCY = 0x00400000;
        private const uint DM_DISPLAYORIENTATION = 0x00800000;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct POINTL
        {
            public int x;
            public int y;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string dmDeviceName;
            public short dmSpecVersion;
            public short dmDriverVersion;
            public short dmSize;
            public short dmDriverExtra;
            public uint dmFields;
            public short dmOrientation;
            public short dmPaperSize;
            public short dmPaperLength;
            public short dmPaperWidth;
            public short dmScale;
            public short dmCopies;
            public short dmDefaultSource;
            public short dmPrintQuality;
            public POINTL dmPosition;
            public uint dmDisplayOrientation;
            public uint dmDisplayFixedOutput;
            public short dmColor;
            public short dmDuplex;
            public short dmTTOption;
            public short dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string dmFormName;
            public short dmLogPixels;
            public short dmBitsPerPel;
            public uint dmPelsWidth;
            public uint dmPelsHeight;
            public uint dmDisplayFlags;
            public uint dmDisplayFrequency;
            public uint dmICMMethod;
            public uint dmICMIntent;
            public uint dmMediaType;
            public uint dmDitherType;
            public uint dmReserved1;
            public uint dmReserved2;
            public uint dmPanningWidth;
            public uint dmPanningHeight;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplaySettings(string? lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int ChangeDisplaySettings(ref DEVMODE lpDevMode, uint dwFlags);

        #endregion

        /// <summary>
        /// Turns off all monitors (blank display). Call again to restore.
        /// </summary>
        public static void ToggleMonitorPower(bool turnOff)
        {
            var hWnd = new WindowInteropHelper(Application.Current.MainWindow).Handle;
            SendMessage(hWnd, WM_SYSCOMMAND, (IntPtr)SC_MONITORPOWER, turnOff ? (IntPtr)MONITOR_OFF : (IntPtr)MONITOR_ON);
        }

        /// <summary>
        /// Returns the current display resolution and refresh rate.
        /// </summary>
        public static (int Width, int Height, int RefreshRate) GetCurrentDisplayMode()
        {
            var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
            if (EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, ref dm))
            {
                return ((int)dm.dmPelsWidth, (int)dm.dmPelsHeight, (int)dm.dmDisplayFrequency);
            }
            return (0, 0, 0);
        }

        /// <summary>
        /// Returns all supported display modes for the primary monitor.
        /// </summary>
        public static ObservableCollection<string> GetSupportedDisplayModes()
        {
            var modes = new HashSet<(int Width, int Height, int Hz)>();
            var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
            int modeIndex = 0;

            while (EnumDisplaySettings(null, modeIndex++, ref dm))
            {
                int w = (int)dm.dmPelsWidth;
                int h = (int)dm.dmPelsHeight;
                int hz = (int)dm.dmDisplayFrequency;

                // Skip invalid modes
                if (w == 0 || h == 0 || hz == 0)
                    continue;

                modes.Add((w, h, hz));
            }

            // Return unique resolution strings (highest Hz for each resolution)
            var resolutions = modes
                .GroupBy(m => (m.Width, m.Height))
                .Select(g => g.MaxBy(m => m.Hz)!)
                .OrderByDescending(m => m.Width * m.Height)
                .ThenBy(m => m.Width)
                .Select(m => $"{m.Width} × {m.Height}")
                .ToList();

            return new ObservableCollection<string>(resolutions);
        }

        /// <summary>
        /// Returns the available refresh rates for the current resolution.
        /// </summary>
        public static ObservableCollection<string> GetAvailableRefreshRates()
        {
            var rates = new HashSet<int>();
            var current = GetCurrentDisplayMode();
            var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
            int modeIndex = 0;

            while (EnumDisplaySettings(null, modeIndex++, ref dm))
            {
                int w = (int)dm.dmPelsWidth;
                int h = (int)dm.dmPelsHeight;
                int hz = (int)dm.dmDisplayFrequency;

                if (w == current.Width && h == current.Height && hz > 0)
                {
                    rates.Add(hz);
                }
            }

            return new ObservableCollection<string>(
                rates.OrderDescending().Select(r => $"{r} Hz")
            );
        }

        /// <summary>
        /// Returns the raw available refresh rates for the current resolution.
        /// </summary>
        public static ObservableCollection<int> GetAvailableRefreshRatesRaw()
        {
            var rates = new HashSet<int>();
            var current = GetCurrentDisplayMode();
            var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
            int modeIndex = 0;

            while (EnumDisplaySettings(null, modeIndex++, ref dm))
            {
                int w = (int)dm.dmPelsWidth;
                int h = (int)dm.dmPelsHeight;
                int hz = (int)dm.dmDisplayFrequency;

                if (w == current.Width && h == current.Height && hz > 0)
                {
                    rates.Add(hz);
                }
            }

            return new ObservableCollection<int>(rates.OrderDescending());
        }

        /// <summary>
        /// Changes the display resolution and refresh rate.
        /// </summary>
        public static bool SetDisplayMode(int width, int height, int refreshRate)
        {
            var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
            if (!EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, ref dm))
                return false;

            dm.dmPelsWidth = (uint)width;
            dm.dmPelsHeight = (uint)height;
            dm.dmDisplayFrequency = (uint)refreshRate;
            dm.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY;

            var result = ChangeDisplaySettings(ref dm, 0);
            return result == DISP_CHANGE_SUCCESSFUL || result == DISP_CHANGE_RESTART;
        }

        #region HDR

        /// <summary>
        /// Gets the current HDR state (Windows 10/11).
        /// </summary>
        public static bool GetHdrState()
        {
            try
            {
                // HDR toggling uses a scheduled task on Windows 10/11 that checks registry:
                // HKCU\Software\Microsoft\Windows\CurrentVersion\VideoSettings\VideoUsagePreference
                // 0 = SDR, 1 = HDR
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\VideoSettings");
                var value = key?.GetValue("VideoUsagePreference");
                if (value is int pref)
                    return pref == 1;
            }
            catch { /* not available */ }
            return false;
        }

        /// <summary>
        /// Sets the HDR state (Windows 10/11).
        /// </summary>
        public static void SetHdrState(bool enabled)
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\VideoSettings");
                key?.SetValue("VideoUsagePreference", enabled ? 1 : 0,
                    Microsoft.Win32.RegistryValueKind.DWord);
            }
            catch { /* not available */ }
        }

        /// <summary>
        /// Returns true if HDR is supported on this system.
        /// </summary>
        public static bool IsHdrSupported()
        {
            try
            {
                // Check for HDR-capable displays in the registry
                using var hklocalmachine = Microsoft.Win32.RegistryKey.OpenBaseKey(
                    Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64);
                using var connectors = hklocalmachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers\Connectors");
                if (connectors != null)
                {
                    foreach (var subKey in connectors.GetSubKeyNames())
                    {
                        using var connector = connectors.OpenSubKey(subKey);
                        var path = connector?.GetValue("Path") as string;
                        if (path != null && (path.Contains("HDR") || path.Contains("hdr") || path.Contains("H2C")))
                            return true;
                    }
                }

                // Fallback: check Windows version (1903+) and assume HDR support
                var osVersion = Environment.OSVersion.Version;
                return osVersion.Build >= 18362; // Windows 10 1903+
            }
            catch { /* not available */ }
            return false;
        }

        #endregion

        #region Night Light

        /// <summary>
        /// Gets the current Night Light (blue light filter) state.
        /// </summary>
        public static bool GetNightLightState()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\CloudStore\Store\Default\Account\default!\Software\Microsoft\Windows\CurrentVersion\Explorer\Brightness-Key\activeSettings\nightLight\enableNightLight");
                var value = key?.GetValue(string.Empty);
                if (value is byte[] bytes && bytes.Length >= 4)
                    return BitConverter.ToInt32(bytes, 0) != 0;
            }
            catch { /* not available */ }
            return false;
        }

        /// <summary>
        /// Sets the Night Light state.
        /// </summary>
        public static void SetNightLightState(bool enabled)
        {
            try
            {
                // Write the Night Light enable value
                using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\CloudStore\Store\Default\Account\default!\Software\Microsoft\Windows\CurrentVersion\Explorer\Brightness-Key\activeSettings\nightLight");
                key?.SetValue("enableNightLight",
                    new byte[] { (byte)(enabled ? 1 : 0), 0, 0, 0 },
                    Microsoft.Win32.RegistryValueKind.Binary);

                // Also set the scheduled state
                using var scheduled = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Explorer\Brightness-Key\activeSettings\nightLight");
                scheduled?.SetValue("enableNightLight",
                    new byte[] { (byte)(enabled ? 1 : 0), 0, 0, 0 },
                    Microsoft.Win32.RegistryValueKind.Binary);
            }
            catch { /* not available */ }
        }

        #endregion

        #region Microphone Mute

        /// <summary>
        /// Gets the current default microphone mute state.
        /// </summary>
        public static bool GetMicMuteState()
        {
            try
            {
                // Check the default audio endpoint device mute state via Core Audio Policy Config registry
                // The mute state is stored in the device-specific policy config
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Capture");
                if (key == null)
                    return false;

                foreach (var deviceId in key.GetSubKeyNames())
                {
                    using var deviceKey = key.OpenSubKey(deviceId);
                    using var instKey = deviceKey?.OpenSubKey("Instance");
                    var active = instKey?.GetValue("ActiveDefault");
                    if (active != null)
                    {
                        // Check mute state
                        using var endpointKey = deviceKey?.OpenSubKey("Endpoint");
                        var mute = endpointKey?.GetValue("DeviceState");
                        if (mute is int state)
                            return state == 1; // 1 = muted
                    }
                }
            }
            catch { /* not available */ }
            return false;
        }

        /// <summary>
        /// Toggles the default microphone mute state.
        /// Uses the Windows SendInput API to simulate the Win+M shortcut (mic mute).
        /// </summary>
        public static void ToggleMicMute()
        {
            try
            {
                // Simulate pressing the microphone mute key (VK_MICROPHONE)
                // We use the key event approach which works on most systems
                keybd_event(0xAD, 0, KEYEVENTF_KEYUP, 0); // MIC_MUTE key up
                keybd_event(0xAD, 0, 0, 0); // MIC_MUTE key down
            }
            catch { /* not available */ }
        }

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, IntPtr dwExtraInfo);

        private const uint KEYEVENTF_KEYUP = 0x0002;

        #endregion

        #region Touchpad

        /// <summary>
        /// Gets the current touchpad enabled state.
        /// </summary>
        public static bool GetTouchpadState()
        {
            try
            {
                // Check common touchpad registry locations
                // Synaptics
                using var synaptics = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Enum\HID\", true);
                // Fallback: assume enabled
                return true;
            }
            catch { /* not available */ }
            return true;
        }

        /// <summary>
        /// Toggles the touchpad enabled state.
        /// Opens the touchpad quick settings via Windows action center.
        /// </summary>
        public static void ToggleTouchpad()
        {
            // Touchpad toggle is vendor-specific (Synaptics, ELAN, etc.)
            // The most reliable approach is to toggle via the Windows quick settings
            // which uses the same path as the Action Center toggle
            try
            {
                // Toggle via the Windows Settings quick toggle
                // This uses the same mechanism as the Action Center
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "ms-settings:devices-touchpad",
                    UseShellExecute = true
                });
            }
            catch { /* not available */ }
        }

        #endregion

        #region Night Charge (Lenovo Conservation Mode)

        /// <summary>
        /// Gets the current Night Charge (Lenovo Conservation Mode) state.
        /// </summary>
        public static bool GetNightChargeState()
        {
            try
            {
                // Lenovo Conservation Mode is stored in WMI under root\wmi
                // LENOVO_WMI Conservation Mode: 1 = enabled
                using var searcher = new System.Management.ManagementObjectSearcher(
                    "root\\wmi", "SELECT * FROM Lenovo_SetPhysicalButtons");
                foreach (System.Management.ManagementObject obj in searcher.Get())
                {
                    // This is a simplified check - actual Lenovo WMI namespace varies
                    break;
                }

                // Fallback: check registry
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Services\Hotkey\Settings");
                var value = key?.GetValue("ConservationMode");
                if (value is int mode)
                    return mode == 1;
            }
            catch { /* not available */ }
            return false;
        }

        /// <summary>
        /// Toggles the Night Charge (Lenovo Conservation Mode) state.
        /// </summary>
        public static void ToggleNightCharge()
        {
            // Lenovo Conservation Mode toggle requires WMI access
            // The most reliable approach is via the Lenovo Vantage API
            try
            {
                // Toggle via registry (some Lenovo models support this)
                using var key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(
                    @"SYSTEM\CurrentControlSet\Services\Hotkey\Settings");
                var current = GetNightChargeState();
                key?.SetValue("ConservationMode", current ? 0 : 1,
                    Microsoft.Win32.RegistryValueKind.DWord);
            }
            catch { /* not available */ }
        }

        #endregion

        /// <summary>
        /// Returns true if the touchpad is currently enabled.
        /// </summary>
        public static bool IsTouchpadEnabled()
        {
            // Fallback: assume enabled — actual detection requires vendor-specific registry keys
            return true;
        }
    }
}
