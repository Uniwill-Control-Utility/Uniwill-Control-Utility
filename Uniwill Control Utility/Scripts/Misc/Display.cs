using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Uniwill_Control_Utility.Views.Windows;
using Uniwill_Control_Utility.Services;
using static Uniwill_Control_Utility.Scripts.Misc.ScreenInterrogatory;

namespace Uniwill_Control_Utility.Scripts.Misc
{
    internal class Display
    {
#pragma warning disable SYSLIB1054 // Convert to LibraryImport — DISPLAY_DEVICE and DEVMODE structs contain [MarshalAs] string fields not supported by source-generated P/Invoke
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern bool EnumDisplayDevices([MarshalAs(UnmanagedType.LPWStr)] string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern bool EnumDisplaySettings([MarshalAs(UnmanagedType.LPWStr)] string lpszDeviceName, uint iModeNum, ref DEVMODE lpDevMode);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int ChangeDisplaySettingsEx([MarshalAs(UnmanagedType.LPWStr)] string lpszDeviceName, ref DEVMODE lpDevMode, IntPtr hwnd, uint dwflags, IntPtr lParam);

        // XMG Control Center's color engine (GCUUtil.exe, MonitorProfile class) drives the
        // genuine ICC switch through mscms.dll. Mirrored verbatim from the decompiled
        // declarations so the P/Invoke marshalling — and therefore the native call bytes —
        // match what XMG CC ships. AssociateColorProfileWithDeviceA is imported by GCUUtil
        // but never called; the apply path is Uninstall → Install → WcsSetDefaultColorProfile.
        [DllImport("mscms.dll")]
        internal static extern bool InstallColorProfile(IntPtr hdc, string path);

        [DllImport("mscms.dll")]
        internal static extern bool UninstallColorProfile(IntPtr hdc, string path, bool bDelete);

        [DllImport("mscms.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool WcsSetDefaultColorProfile(uint scope, string deviceName, uint profileType, uint subtype, uint profileID, string profileName);

#pragma warning restore SYSLIB1054

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct DISPLAY_DEVICE
        {
            public uint cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceString;
            public uint StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceKey;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct DEVMODE
        {
            private const int CCHDEVICENAME = 32;
            private const int CCHFORMNAME = 32;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHDEVICENAME)]
            public string dmDeviceName;
            public short dmSpecVersion;
            public short dmDriverVersion;
            public short dmSize;
            public short dmDriverExtra;
            public int dmFields;

            public int dmPositionX;
            public int dmPositionY;
            public int dmDisplayOrientation;
            public int dmDisplayFixedOutput;

            public short dmColor;
            public short dmDuplex;
            public short dmYResolution;
            public short dmTTOption;
            public short dmCollate;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHFORMNAME)]
            public string dmFormName;

            public short dmLogPixels;
            public int dmBitsPerPel;
            public int dmPelsWidth;
            public int dmPelsHeight;
            public int dmDisplayFlags;
            public int dmDisplayFrequency;
            public int dmICMMethod;
            public int dmICMIntent;
            public int dmMediaType;
            public int dmDitherType;
            public int dmReserved1;
            public int dmReserved2;
            public int dmPanningWidth;
            public int dmPanningHeight;
        }

        private static string? _targetDisplayName = FindLaptopScreen();
        internal static string? TargetDisplayName => _targetDisplayName;

        private static List<string> _uniqueResolutions = GetSupportedResolutions(_targetDisplayName ?? DefaultDevice);
        internal static List<string> UniqueResolutions => _uniqueResolutions;

        private static List<int> _uniqueRefreshRates = GetSupportedRefreshRates(_targetDisplayName ?? DefaultDevice);
        internal static List<int> UniqueRefreshRates => _uniqueRefreshRates;

        public static void SetUpLists()
        {
            _targetDisplayName = FindLaptopScreen();
            _uniqueRefreshRates = GetSupportedRefreshRates(_targetDisplayName ?? DefaultDevice)
                .Distinct()
                .OrderByDescending(x => x)
                .ToList();
        }

        static List<string> GetSupportedResolutions(string? targetDisplayName)
        {
            List<string> resolutions = new List<string>();
            if (string.IsNullOrEmpty(targetDisplayName))
                return resolutions;

            DISPLAY_DEVICE displayDevice = new DISPLAY_DEVICE();
            displayDevice.cb = (uint)Marshal.SizeOf(displayDevice);

            for (uint deviceIndex = 0; EnumDisplayDevices(null, deviceIndex, ref displayDevice, 0); deviceIndex++)
            {
                if (displayDevice.DeviceName == targetDisplayName)
                {
                    DEVMODE devMode = new DEVMODE();
                    devMode.dmSize = (short)Marshal.SizeOf(devMode);
                    for (uint modeIndex = 0; EnumDisplaySettings(displayDevice.DeviceName, modeIndex, ref devMode); modeIndex++)
                    {
                        string resolution = $"{devMode.dmPelsWidth} x {devMode.dmPelsHeight}";
                        resolutions.Add(resolution);
                    }
                    break;
                }
            }

            return resolutions;
        }

        static List<int> GetSupportedRefreshRates(string? targetDisplayName)
        {
            List<int> refreshRates = new List<int>();
            if (string.IsNullOrEmpty(targetDisplayName))
                return refreshRates;

            DISPLAY_DEVICE displayDevice = new DISPLAY_DEVICE();
            displayDevice.cb = (uint)Marshal.SizeOf(displayDevice);
            for (uint deviceIndex = 0; EnumDisplayDevices(null, deviceIndex, ref displayDevice, 0); deviceIndex++)
            {
                if (displayDevice.DeviceName == targetDisplayName)
                {
                    DEVMODE devMode = new DEVMODE();
                    devMode.dmSize = (short)Marshal.SizeOf(devMode);
                    for (uint modeIndex = 0; EnumDisplaySettings(displayDevice.DeviceName, modeIndex, ref devMode); modeIndex++)
                    {
                        if (devMode.dmDisplayFrequency > 0)
                        {
                            refreshRates.Add(devMode.dmDisplayFrequency);
                        }
                    }
                    break;
                }
            }

            return refreshRates;
        }

        public static void ChangeDisplaySettings(string? targetDisplayName, int newRefreshRate)
        {
            if (string.IsNullOrEmpty(targetDisplayName))
                return;

            DEVMODE devMode = new DEVMODE();
            devMode.dmSize = (short)Marshal.SizeOf(devMode);

            if (EnumDisplaySettings(targetDisplayName, EnumCurrentSettings, ref devMode))
            {
                devMode.dmFields = (int)(DisplaySettingsFlags.DmPelsWidth | DisplaySettingsFlags.DmPelsHeight | DisplaySettingsFlags.DmDisplayFrequency);

                devMode.dmDisplayFrequency = newRefreshRate;

                int result = ChangeDisplaySettingsEx(targetDisplayName, ref devMode, IntPtr.Zero, (uint)ChangeDisplaySettingsFlags.CdsUpdateRegistry, IntPtr.Zero);

                if (result == DispChangeSuccessful)
                {
                    Console.WriteLine("\nDisplay settings changed successfully.");
                }
                else
                {
                    Console.WriteLine("\nFailed to change display settings.");
                }
            }
            else
            {
                Console.WriteLine("\nFailed to retrieve current display settings.");
            }
        }

        public static void ApplySettings(int newHz)
        {
            _targetDisplayName = FindLaptopScreen();
            if (newHz > 0)
            {
                Display.ChangeDisplaySettings(_targetDisplayName, newHz);
            }
            else
            {
                Console.WriteLine("Invalid input format.");
            }
        }

        public static int GetCurrentRefreshRate()
        {
            _targetDisplayName = FindLaptopScreen();
            if (string.IsNullOrEmpty(_targetDisplayName))
                return 0;

            DEVMODE devMode = new DEVMODE
            {
                dmSize = (short)Marshal.SizeOf<DEVMODE>()
            };

            return EnumDisplaySettings(_targetDisplayName, EnumCurrentSettings, ref devMode)
                ? devMode.dmDisplayFrequency
                : 0;
        }

        /// <summary>
        /// The display currently in use — the primary (taskbar) display. Unlike
        /// FindLaptopScreen (always the internal panel), this follows the user to an
        /// external monitor when that monitor is primary, mirroring OpenRevo's
        /// per-monitor is_primary/is_internal monitor model.
        /// </summary>
        public static string? CurrentDisplayName()
        {
            return Screen.PrimaryScreen?.DeviceName ?? DefaultDevice;
        }

        /// <summary>
        /// True when the given GDI device name is the laptop's internal panel, per the
        /// DirectX display-device topology (Internal / DisplayportEmbedded output
        /// technology). More robust than OpenRevo's monitor-name/resolution table.
        /// </summary>
        public static bool IsInternalDisplay(string? deviceName)
        {
            if (deviceName is null)
                return false;

            try
            {
                var screens = Screen.AllScreens;
                var devices = GetAllDevices().ToArray();
                for (int i = 0; i < devices.Length && i < screens.Length; i++)
                {
                    if (screens[i].DeviceName == deviceName)
                    {
                        return devices[i].outputTechnology == DisplayConfigOutputTechnology.Internal
                            || devices[i].outputTechnology == DisplayConfigOutputTechnology.DisplayportEmbedded;
                    }
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        public static int GetCurrentRefreshRate(string? targetDisplayName)
        {
            if (string.IsNullOrEmpty(targetDisplayName))
                return 0;

            DEVMODE devMode = new DEVMODE
            {
                dmSize = (short)Marshal.SizeOf<DEVMODE>()
            };

            return EnumDisplaySettings(targetDisplayName, EnumCurrentSettings, ref devMode)
                ? devMode.dmDisplayFrequency
                : 0;
        }

        public static List<int> GetAvailableRefreshRates(string? targetDisplayName)
        {
            return GetSupportedRefreshRates(targetDisplayName)
                .Distinct()
                .OrderByDescending(x => x)
                .ToList();
        }

        /// <summary>Current mode resolution of the given display, e.g. "2560×1600".</summary>
        public static string GetDisplayResolution(string? targetDisplayName)
        {
            if (string.IsNullOrEmpty(targetDisplayName))
                return string.Empty;

            DEVMODE devMode = new DEVMODE
            {
                dmSize = (short)Marshal.SizeOf<DEVMODE>()
            };

            return EnumDisplaySettings(targetDisplayName, EnumCurrentSettings, ref devMode)
                ? $"{devMode.dmPelsWidth}×{devMode.dmPelsHeight}"
                : string.Empty;
        }


        /// <summary>
        /// The laptop's internal panel (null when the device has none). Matches each
        /// GDI screen against the DirectX topology (Internal / DisplayportEmbedded),
        /// so it never follows an external monitor when that one is primary.
        /// </summary>
        public static string? InternalPanelName()
        {
            try
            {
                var screens = Screen.AllScreens;
                var devices = GetAllDevices().ToArray();
                for (int i = 0; i < devices.Length && i < screens.Length; i++)
                {
                    if (devices[i].outputTechnology == DisplayConfigOutputTechnology.Internal
                        || devices[i].outputTechnology == DisplayConfigOutputTechnology.DisplayportEmbedded)
                    {
                        return screens[i].DeviceName;
                    }
                }
                return null;
            }
            catch
            {
                return FindLaptopScreen();
            }
        }

        /// <summary>
        /// Rates offered for a display: the GDI-enumerated set, plus the 300 Hz
        /// internal-panel mode that Windows hides unless dGPU-only mux is active.
        /// </summary>
        public static List<int> GetOfferedRefreshRates(string? targetDisplayName, bool dGpuOnlyActive)
        {
            var rates = GetAvailableRefreshRates(targetDisplayName);
            if (dGpuOnlyActive && IsInternalDisplay(targetDisplayName) && !rates.Contains(DgpuOnlyRefreshRate))
                rates.Insert(0, DgpuOnlyRefreshRate);
            return rates;
        }

        /// <summary>
        /// Refresh rates offered for the internal panel (descending). When the panel
        /// is not enumerable (lid closed / external-only — GDI hides a display that
        /// is off), fall back to the panel's known mode set — the same offering XMG
        /// Control Center shows. 300 Hz only while dGPU-only mux is active.
        /// </summary>
        public static List<int> GetInternalRefreshRates(bool dGpuOnlyActive)
        {
            var rates = GetOfferedRefreshRates(InternalPanelName(), dGpuOnlyActive);
            if (rates.Count == 0)
            {
                rates = new List<int> { InternalPanelHighRefreshRate, InternalPanelBaseRefreshRate };
                if (dGpuOnlyActive)
                    rates.Insert(0, DgpuOnlyRefreshRate);
            }
            return rates;
        }

        /// <summary>Current refresh rate of the internal panel (0 when unavailable).</summary>
        public static int GetInternalRefreshRate()
        {
            return GetCurrentRefreshRate(InternalPanelName());
        }

        /// <summary>
        /// Applies a refresh rate to the internal panel only (0 = disabled, no-op).
        /// Returns the applied rate verified by readback (0 when skipped).
        /// </summary>
        public static int ApplyInternalRefreshRate(int hz)
        {
            if (hz <= 0)
                return 0;

            var panel = InternalPanelName();
            if (string.IsNullOrEmpty(panel))
                return 0;

            ChangeDisplaySettings(panel, hz);
            return GetCurrentRefreshRate(panel);
        }

        /// <summary>
        /// The ICM display-profile association subkey for a GDI device name — the
        /// trailing component of EnumDisplayDevices(dwFlags=1) DeviceKey
        /// ("\Registry\Machine\...\Class\{4d36e96e-...}\0002" → "0002"). Same
        /// numbering Windows' own calibrator and OpenRevo use.
        /// </summary>
        private static string IcmDeviceId(string? deviceName)
        {
            if (string.IsNullOrEmpty(deviceName))
                return string.Empty;

            DISPLAY_DEVICE displayDevice = new DISPLAY_DEVICE();
            displayDevice.cb = (uint)Marshal.SizeOf(displayDevice);
            if (!EnumDisplayDevices(deviceName, 0, ref displayDevice, 1))
                return string.Empty;

            int sep = displayDevice.DeviceKey.LastIndexOf('\\');
            return sep < 0 ? string.Empty : displayDevice.DeviceKey.Substring(sep + 1);
        }

        /// <summary>
        /// The device identifier XMG Control Center passes to WcsSetDefaultColorProfile:
        /// the DeviceKey of the (attached + multi-driver) sub-device found under the
        /// (attached + primary) display — GCUUtil.exe SetMonitorProfile's two-loop
        /// EnumDisplayDevices walk (dwFlags = EDD_GET_DEVICE_INTERFACE_NAME).
        /// </summary>
        /// <summary>
        /// The device identifier to pass to WcsSetDefaultColorProfile for a SPECIFIC display:
        /// the DeviceKey of that display's (attached + multi-driver) sub-device, falling back to
        /// its (attached) sub-device. Generalizes GCUUtil.exe SetMonitorProfile's walk (which
        /// hardcodes the primary) to any GDI device name, so the WCS write lands on the same
        /// association subkey IcmDeviceId(deviceName) reads.
        /// </summary>
        private static string WcsDeviceKeyFor(string? deviceName)
        {
            if (string.IsNullOrEmpty(deviceName))
                return string.Empty;

            DISPLAY_DEVICE dd = new DISPLAY_DEVICE();
            dd.cb = (uint)Marshal.SizeOf(dd);

            string? attached = null;
            for (uint i = 0; ; i++)
            {
                if (!EnumDisplayDevices(deviceName, i, ref dd, 1))
                    break;
                if ((dd.StateFlags & 1u) == 0) // not AttachedToDesktop
                    continue;
                if ((dd.StateFlags & 2u) != 0) // MultiDriver — the WCS target
                    return dd.DeviceKey;
                if (attached is null)
                    attached = dd.DeviceKey;
            }
            return attached ?? string.Empty;
        }

        /// <summary>
        /// The display's active ICC profile as stored in the ICM association
        /// (HKCU\...\ICM\ProfileAssociations\Display\{GUID}\&lt;id&gt; value ICMProfile,
        /// REG_MULTI_SZ single element, bare filename resolved against the spool
        /// color folder — exactly what OpenRevo and XMG CC write). Null when no
        /// association exists (uncalibrated).
        /// </summary>
        public static string? GetCurrentColorProfile(string? deviceName)
        {
            string id = IcmDeviceId(deviceName);
            if (string.IsNullOrEmpty(id))
                return null;

            string? output = RunReg("query", IcmAssociationRoot + @"\" + id, "/v", IcmProfileValueName);
            return output is null ? null : ParseActiveColorProfile(output);
        }

        /// <summary>Current ICC profile filename of the internal panel (null = uncalibrated).</summary>
        public static string? GetInternalColorProfile()
        {
            return GetCurrentColorProfile(InternalPanelName());
        }

        /// <summary>
        /// Associates an ICC profile with the internal panel ("" removes the
        /// association = uncalibrated). Returns the applied profile verified by
        /// readback (null when removed or failed).
        /// </summary>
        public static string? ApplyInternalColorProfile(string profileFileName)
        {
            var panel = InternalPanelName();
            if (string.IsNullOrEmpty(panel))
                return null;

            if (!ApplyColorProfile(panel, profileFileName))
                return null;

            return GetCurrentColorProfile(panel);
        }

        /// <summary>
        /// Associates an ICC profile with a display ("" removes the association).
        /// Returns true when the write/readback succeeded.
        /// </summary>
        public static bool ApplyColorProfile(string? deviceName, string profileFileName)
        {
            // "" = uncalibrated: drop the association. XMG CC has no removal path (it only
            // ever installs a profile); Windows treats "no association" as the uncalibrated
            // state, so a plain delete is the faithful equivalent here.
            if (string.IsNullOrEmpty(profileFileName))
            {
                string id = IcmDeviceId(deviceName);
                if (string.IsNullOrEmpty(id))
                    return false;
                RunReg("delete", IcmAssociationRoot + @"\" + id, "/v", IcmProfileValueName, "/f");
                return GetCurrentColorProfile(deviceName) is null;
            }

            string fullPath = IcmProfilesFolder + @"\" + profileFileName;
            if (!System.IO.File.Exists(fullPath))
                return false;

            string key = WcsDeviceKeyFor(deviceName);
            if (string.IsNullOrEmpty(key))
                return false;

            // XMG Control Center's exact apply sequence (GCUUtil.exe SetMonitorProfile):
            // uninstall the target, install it fresh, then set it as the WCS default profile
            // for the panel at SYSTEM_WIDE then CURRENT_USER scope. The WcsSetDefaultColorProfile
            // pair is what forces Windows' color engine to reload and actually repaint the
            // panel — a bare ProfileAssociations registry write only relabels the selection.
            // Non-destructive. XMG CC's UninstallColorProfile(bDelete=true) DELETES the .icm
            // from the spool — fine for their own bundled profiles, but pointed at Windows'
            // spool files it wipes them one by one. We only need the profile registered, so
            // install (no-op if already present) then set it as the WCS default at both scopes.
            InstallColorProfile(IntPtr.Zero, fullPath);
            WcsSetDefaultColorProfile(0u, key, 0u, 5u, 0u, fullPath); // SYSTEM_WIDE, CPT_ICC, CPST_RGB_WORKING_SPACE
            bool currentUser = WcsSetDefaultColorProfile(1u, key, 0u, 5u, 0u, fullPath); // CURRENT_USER
            return currentUser;
        }

        /// <summary>
        /// Profile filenames offered for the color-calibration control, in display
        /// order. Files are Windows' own (C:\Windows\System32\spool\drivers\color),
        /// the same set OpenRevo and XMG Control Center offer.
        /// </summary>
        public static List<string> GetOfferedColorProfileFiles()
        {
            List<string> offered = new();
            foreach (string file in ColorProfileFiles)
            {
                if (file == DefaultColorProfileFile || System.IO.File.Exists(IcmProfilesFolder + @"\" + file))
                    offered.Add(file);
            }
            return offered;
        }

        /// <summary>Display label for a profile filename ("sRGB.icm" → "sRGB").</summary>
        public static string ColorProfileLabel(string file)
        {
            for (int i = 0; i < ColorProfileFiles.Length; i++)
            {
                if (ColorProfileFiles[i] == file)
                    return ColorProfileLabels[i];
            }
            return file;
        }

        /// <summary>
        /// Runs reg.exe with the given arguments and returns its stdout decoded from
        /// UTF-16LE (reg.exe emits UTF-16LE with a BOM when piped), or null on failure.
        /// </summary>
        private static string? RunReg(params string[] args)
        {
            try
            {
                var startInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = @"C:\Windows\System32\reg.exe",
                    Arguments = string.Join(" ", args.Select(a => a.Contains(' ') ? "\"" + a + "\"" : a)),
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };
                using var process = System.Diagnostics.Process.Start(startInfo);
                if (process is null)
                    return null;
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();
                return output is null ? null : output.Replace("\uFEFF", string.Empty).Replace("\0", string.Empty);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// First element of a reg.exe query line for the ICMProfile value (printed as
        /// "ICMProfile    REG_MULTI_SZ    name\0rest"), null when the value is absent.
        /// </summary>
        private static string? ParseFirstMultiSz(string regQueryOutput)
        {
            foreach (string line in regQueryOutput.Split('\n'))
            {
                string trimmed = line.Trim();
                if (!trimmed.StartsWith(IcmProfileValueName))
                    continue;

                int typePos = trimmed.IndexOf("REG_");
                if (typePos < 0)
                    continue;

                int i = typePos;
                while (i < trimmed.Length && (char.IsLetterOrDigit(trimmed[i]) || trimmed[i] == '_'))
                    i++;
                while (i < trimmed.Length && trimmed[i] == ' ')
                    i++;

                string value = trimmed.Substring(i);
                int sep = value.IndexOf("\\0");
                if (sep >= 0)
                    value = value.Substring(0, sep);
                return value.Length == 0 ? null : value;
            }
            return null;
        }

        /// <summary>
        /// The active ICC profile from a reg.exe query of the ICM association: the LAST
        /// element of the ICMProfile MULTI_SZ (XMG CC appends the requested profile last and
        /// that is the one in effect), reduced to its bare filename so the value matches
        /// ColorProfileFiles whether Windows stored a full path or a name.
        /// </summary>
        private static string? ParseActiveColorProfile(string regQueryOutput)
        {
            foreach (string line in regQueryOutput.Split('\n'))
            {
                string trimmed = line.Trim();
                if (!trimmed.StartsWith(IcmProfileValueName))
                    continue;

                int typePos = trimmed.IndexOf("REG_");
                if (typePos < 0)
                    continue;

                int i = typePos;
                while (i < trimmed.Length && (char.IsLetterOrDigit(trimmed[i]) || trimmed[i] == '_'))
                    i++;
                while (i < trimmed.Length && trimmed[i] == ' ')
                    i++;

                string value = trimmed.Substring(i);
                int lastSep = value.LastIndexOf("\\0");
                if (lastSep >= 0)
                    value = value.Substring(lastSep + 2);

                int slash = value.LastIndexOf('\\');
                if (slash >= 0)
                    value = value.Substring(slash + 1);

                return value.Length == 0 ? null : value;
            }
            return null;
        }

        internal const string DefaultColorProfileFile = "Default.icm";
        internal static readonly string[] ColorProfileLabels = new string[] { "Default", "sRGB", "DCI-P3", "AdobeRGB" };
        internal static readonly string[] ColorProfileFiles = new string[] { DefaultColorProfileFile, "sRGB.icm", "DCIP3.icm", "AdobeRGB.icm" };
        internal const string IcmProfileValueName = "ICMProfile";
        internal const string IcmAssociationRoot = @"HKCU\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ICM\ProfileAssociations\Display\{4d36e96e-e325-11ce-bfc1-08002be10318}";
        internal const string IcmProfilesFolder = @"C:\Windows\System32\spool\drivers\color";

        const int DispChangeSuccessful = 0;
        const int DgpuOnlyRefreshRate = 300;
        const int InternalPanelBaseRefreshRate = 60;
        const int InternalPanelHighRefreshRate = 240;
        internal const uint EnumCurrentSettings = 0xFFFFFFFF;

        [Flags]
        internal enum DisplaySettingsFlags
        {
            DmPelsWidth = 0x00080000,
            DmPelsHeight = 0x00100000,
            DmDisplayFrequency = 0x00400000
        }

        [Flags]
        internal enum ChangeDisplaySettingsFlags
        {
            CdsUpdateRegistry = 0x00000001,
            CdsTest = 0x00000002,
            CdsFullscreen = 0x00000004,
            CdsGlobal = 0x00000008,
            CdsSetPrimary = 0x00000010,
            CdsReset = 0x40000000,
            CdsNoReset = 0x10000000
        }

        internal const string DefaultDevice = @"\\.\DISPLAY1";

        public static string? FindLaptopScreen(bool log = false)
        {
            var screens = Screen.AllScreens;
            try
            {
                var devices = GetAllDevices().ToArray();
                int displayNum = Array.FindLastIndex(devices, device =>
                    device.outputTechnology == DisplayConfigOutputTechnology.Internal ||
                    device.outputTechnology == DisplayConfigOutputTechnology.DisplayportEmbedded);

                return DisplayTargetSelector.Choose(
                    displayNum,
                    screens.Select(screen => screen.DeviceName).ToArray(),
                    Screen.PrimaryScreen?.DeviceName ?? DefaultDevice);
            }
            catch (COMException)
            {
                return Screen.PrimaryScreen?.DeviceName ?? DefaultDevice;
            }
            catch (Exception ex) when (ex is not COMException)
            {
                return Screen.PrimaryScreen?.DeviceName ?? DefaultDevice;
            }
        }
    }
}
