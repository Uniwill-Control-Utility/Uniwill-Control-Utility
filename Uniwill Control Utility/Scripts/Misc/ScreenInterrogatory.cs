using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Uniwill_Control_Utility.Scripts.Misc
{
    internal class ScreenInterrogatory
    {
        public const int ERROR_SUCCESS = 0;

        #region enums

        public enum QueryDeviceConfig
        {
            AllPaths = 0x00000001,
            OnlyActivePaths = 0x00000002,
            DatabaseCurrent = 0x00000004
        }

        public enum DisplayConfigOutputTechnology : uint
        {
            Other = 0xFFFFFFFF,
            Hd15 = 0,
            Svideo = 1,
            CompositeVideo = 2,
            ComponentVideo = 3,
            Dvi = 4,
            Hdmi = 5,
            Lvds = 6,
            DJpn = 8,
            Sdi = 9,
            DisplayportExternal = 10,
            DisplayportEmbedded = 11,
            UdiExternal = 12,
            UdiEmbedded = 13,
            Sdtdongle = 14,
            Miracast = 15,
            Internal = 0x80000000
            // NOTE: ForceUint32 removed — it had the same value as Other (CA1069).
            // Use 0xFFFFFFFF explicitly when a sentinel is needed.
        }

        public enum DisplayConfigScanlineOrdering : uint
        {
            Unspecified = 0,
            Progressive = 1,
            Interlaced = 2,
            InterlacedUpperFieldFirst = Interlaced,
            InterlacedLowerFieldFirst = 3,
            ForceUint32 = 0xFFFFFFFF
        }

        public enum DisplayConfigRotation : uint
        {
            Identity = 1,
            Rotate90 = 2,
            Rotate180 = 3,
            Rotate270 = 4,
            ForceUint32 = 0xFFFFFFFF
        }

        public enum DisplayConfigScaling : uint
        {
            Identity = 1,
            Centered = 2,
            Stretched = 3,
            AspectRatioCenteredMax = 4,
            Custom = 5,
            Preferred = 128,
            ForceUint32 = 0xFFFFFFFF
        }

        public enum DisplayConfigPixelformat : uint
        {
            Bpp8 = 1,
            Bpp16 = 2,
            Bpp24 = 3,
            Bpp32 = 4,
            NonGdi = 5,
            ForceUint32 = 0xffffffff
        }

        public enum DisplayConfigModeInfoType : uint
        {
            Source = 1,
            Target = 2,
            ForceUint32 = 0xFFFFFFFF
        }

        public enum DisplayConfigDeviceInfoType : uint
        {
            GetSourceName = 1,
            GetTargetName = 2,
            GetTargetPreferredMode = 3,
            GetAdapterName = 4,
            SetTargetPersistence = 5,
            GetTargetBaseType = 6,
            ForceUint32 = 0xFFFFFFFF
        }

        #endregion

        #region structs

        [StructLayout(LayoutKind.Sequential)]
        public struct Luid
        {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DisplayConfigPathSourceInfo
        {
            public Luid adapterId;
            public uint id;
            public uint modeInfoIdx;
            public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DisplayConfigPathTargetInfo
        {
            public Luid adapterId;
            public uint id;
            public uint modeInfoIdx;
            private DisplayConfigOutputTechnology outputTechnology;
            private DisplayConfigRotation rotation;
            private DisplayConfigScaling scaling;
            private DisplayConfigRational refreshRate;
            private DisplayConfigScanlineOrdering scanLineOrdering;
            public bool targetAvailable;
            public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DisplayConfigRational
        {
            public uint Numerator;
            public uint Denominator;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DisplayConfigPathInfo
        {
            public DisplayConfigPathSourceInfo sourceInfo;
            public DisplayConfigPathTargetInfo targetInfo;
            public uint flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DisplayConfig2dRegion
        {
            public uint cx;
            public uint cy;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DisplayConfigVideoSignalInfo
        {
            public ulong pixelRate;
            public DisplayConfigRational hSyncFreq;
            public DisplayConfigRational vSyncFreq;
            public DisplayConfig2dRegion activeSize;
            public DisplayConfig2dRegion totalSize;
            public uint videoStandard;
            public DisplayConfigScanlineOrdering scanLineOrdering;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DisplayConfigTargetMode
        {
            public DisplayConfigVideoSignalInfo targetVideoSignalInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Pointl
        {
            private int x;
            private int y;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DisplayConfigSourceMode
        {
            public uint width;
            public uint height;
            public DisplayConfigPixelformat pixelFormat;
            public Pointl position;
        }

        [StructLayout(LayoutKind.Explicit)]
        public struct DisplayConfigModeInfoUnion
        {
            [FieldOffset(0)]
            public DisplayConfigTargetMode targetMode;

            [FieldOffset(0)]
            public DisplayConfigSourceMode sourceMode;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DisplayConfigModeInfo
        {
            public DisplayConfigModeInfoType infoType;
            public uint id;
            public Luid adapterId;
            public DisplayConfigModeInfoUnion modeInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DisplayConfigTargetDeviceNameFlags
        {
            public uint value;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DisplayConfigDeviceInfoHeader
        {
            public DisplayConfigDeviceInfoType type;
            public uint size;
            public Luid adapterId;
            public uint id;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DisplayConfigTargetDeviceName
        {
            public DisplayConfigDeviceInfoHeader header;
            public DisplayConfigTargetDeviceNameFlags flags;
            public DisplayConfigOutputTechnology outputTechnology;
            public ushort edidManufactureId;
            public ushort edidProductCodeId;
            public uint connectorInstance;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string monitorFriendlyDeviceName;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string monitorDevicePath;
        }

        #endregion

#pragma warning disable SYSLIB1054 // LibraryImport not compatible with structs using [MarshalAs] on string fields

        #region DLL-Imports

        [DllImport("user32.dll")]
        public static extern int GetDisplayConfigBufferSizes(
            QueryDeviceConfig flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

        [DllImport("user32.dll")]
        public static extern int QueryDisplayConfig(
            QueryDeviceConfig flags,
            ref uint numPathArrayElements, [Out] DisplayConfigPathInfo[] PathInfoArray,
            ref uint numModeInfoArrayElements, [Out] DisplayConfigModeInfo[] ModeInfoArray,
            nint currentTopologyId
            );

        [DllImport("user32.dll")]
        public static extern int DisplayConfigGetDeviceInfo(ref DisplayConfigTargetDeviceName deviceName);

        #endregion

#pragma warning restore SYSLIB1054

        private static DisplayConfigTargetDeviceName DeviceName(Luid adapterId, uint targetId)
        {
            var deviceName = new DisplayConfigTargetDeviceName
            {
                header =
                {
                    size = (uint)Marshal.SizeOf<DisplayConfigTargetDeviceName>(),
                    adapterId = adapterId,
                    id = targetId,
                    type = DisplayConfigDeviceInfoType.GetTargetName
                }
            };
            var error = DisplayConfigGetDeviceInfo(ref deviceName);
            if (error != ERROR_SUCCESS)
                throw new Win32Exception(error);

            return deviceName;
        }

        public static IEnumerable<DisplayConfigTargetDeviceName> GetAllDevices()
        {
            uint pathCount, modeCount;
            var error = GetDisplayConfigBufferSizes(QueryDeviceConfig.OnlyActivePaths, out pathCount, out modeCount);
            if (error != ERROR_SUCCESS)
                throw new Win32Exception(error);

            var displayPaths = new DisplayConfigPathInfo[pathCount];
            var displayModes = new DisplayConfigModeInfo[modeCount];
            error = QueryDisplayConfig(QueryDeviceConfig.OnlyActivePaths,
                ref pathCount, displayPaths, ref modeCount, displayModes, nint.Zero);
            if (error != ERROR_SUCCESS)
                throw new Win32Exception(error);

            for (var i = 0; i < modeCount; i++)
                if (displayModes[i].infoType == DisplayConfigModeInfoType.Target)
                    yield return DeviceName(displayModes[i].adapterId, displayModes[i].id);
        }
    }
}
