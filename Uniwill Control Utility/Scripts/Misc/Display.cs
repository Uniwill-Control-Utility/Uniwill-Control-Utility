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

        const int DispChangeSuccessful = 0;
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
