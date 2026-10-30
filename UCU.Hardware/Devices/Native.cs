using System.Runtime.InteropServices;

namespace UCU.Hardware.Devices
{
    /// <summary>
    /// The Configuration Manager (CfgMgr32) calls the PnP verbs need. These are the
    /// APIs <c>Enable-PnpDevice</c>/<c>Disable-PnpDevice</c> are built on, documented
    /// as administrator operations: LocalSystem satisfies them unconditionally, which
    /// is the reason the device verbs run in the engine and the unelevated app never
    /// prompts again. Only the six calls the device rows use are declared - the
    /// surface stays reviewable, which the PowerShell string it replaced never was.
    /// </summary>
    internal static class Native
    {
        public const uint CR_SUCCESS = 0x00000000;
        public const uint CR_BUFFER_SMALL = 0x0000001A;
        public const uint CR_NO_SUCH_DEVNODE = 0x0000000D;

        /// <summary>Devnode is physically present in the system.</summary>
        public const uint DF_PRESENT = 0x1;

        /// <summary>Devnode is disabled (the DNE flag; PowerShell reports "Disable").</summary>
        public const uint DF_DNE_0 = 0x2;

        /// <summary>Devnode is running.</summary>
        public const uint DF_RUNNING = 0x4;

        /// <summary>Disable visible to all (new) processes - the flag Device Manager's
        /// "disable this device" checkbox sets, the same one Disable-PnpDevice passes.</summary>
        public const uint DICS_DISABLE_GLOBAL_W = 0x02000000;

        public const uint PROFILE_ALL_PROBLEMS = 0xFFFFFFFF;
        public const uint CLDIGSR_FLAGS_NONE = 0;
        public const int MAX_DEVICE_ID_LEN = 200;

        [StructLayout(LayoutKind.Sequential)]
        public struct DEVPROPKEY
        {
            public System.Guid Fmtid;
            public uint Pid;

            public DEVPROPKEY(System.Guid fmtid, uint pid)
            {
                Fmtid = fmtid;
                Pid = pid;
            }
        }

        // DEVPKEY_Device_Class / _FriendlyName / _DeviceDesc, from the published
        // devpkey.h values; the device rows' identity properties. Properties, not
        // fields: the P/Invoke takes a mutable ref, and a static field the marshaller
        // pins would be shared state between concurrent enumeration threads.
        public static DEVPROPKEY DEVPKEY_Device_Class =>
            new(new System.Guid("dd52e150-1921-4157-9a0b-3b5b40f3f882"), 1);

        public static DEVPROPKEY DEVPKEY_Device_FriendlyName =>
            new(new System.Guid("b725f130-47ef-101a-a5f1-02608c9e8f60"), 15);

        public static DEVPROPKEY DEVPKEY_Device_DeviceDesc =>
            new(new System.Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 2);

        [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
        public static extern uint CM_Get_Device_ID_List(
            string? pszFilter, byte[] Buffer, ref uint ulBufferLen, uint ulFlags);

        [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
        public static extern uint CM_Locate_DevNode(out uint pdwDevInst, string pDeviceID, uint ulFlags);

        [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
        public static extern uint CM_Get_Device_ID(uint dnDevInst, byte[] Buffer, uint ulBufferLen, uint ulFlags);

        [DllImport("CfgMgr32.dll")]
        public static extern uint CM_Get_DevNode_Status(out uint pulStatus, out uint pulProblemNumber, uint dnDevInst, uint ulFlags);

        [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
        public static extern uint CM_Get_DevNode_Property(
            uint dnDevInst, ref DEVPROPKEY pPropertyKey, ref uint pulPropertyType,
            byte[] pBuffer, ref uint pulBufferSize, uint ulFlags);

        [DllImport("CfgMgr32.dll")]
        public static extern uint CM_Enable_DevNode(uint dnDevInst, uint ulFlags);

        [DllImport("CfgMgr32.dll")]
        public static extern uint CM_Disable_DevNode(uint dnDevInst, uint ulDisableFlags, uint ulFlags);
    }
}
