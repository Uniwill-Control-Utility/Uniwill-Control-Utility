using System.Runtime.InteropServices;

namespace UCU.Hardware.Devices
{
    /// <summary>
    /// The Configuration Manager (CfgMgr32) calls the PnP verbs need: locate,
    /// status, enable, disable. These are the APIs <c>Enable-PnpDevice</c>/
    /// <c>Disable-PnpDevice</c> are built on, documented as administrator
    /// operations: LocalSystem satisfies them unconditionally, which is the
    /// reason the device verbs run in the engine and the unelevated app never
    /// prompts again.
    ///
    /// What is deliberately NOT declared: CM_Get_Device_ID_List and
    /// CM_Get_DevNode_Property - the CM *enumerator* and CM property reads.
    /// Field evidence (guarded dev box, 2026-10-30): every CM_Get_Device_ID_List
    /// call - W and A, every filter, every documented PMCL flag set, size probe
    /// and full call - answers CR_FAIL (0x4) or CR_NO_MORE_HANDLES (0x1F), and
    /// the FriendlyName/Class devprops answer CR_NO_SUCH_VALUE (0x25) on the
    /// very nodes whose status and mutation calls work. A service built on the
    /// enumerator therefore sees an empty machine on the field's boxes: the
    /// webcam row reported "already disabled" and never touched the devnode.
    /// Device identity moved to WMI Win32_PnPEntity (the source the app's
    /// Get-PnpDevice fallback rides, proven on the same box); the per-node
    /// calls below work there and stay.
    /// </summary>
    internal static class Native
    {
        public const uint CR_SUCCESS = 0x00000000;
        public const uint CR_NO_SUCH_DEVNODE = 0x0000000D;

        /// <summary>Problem code: the devnode is disabled (Device Manager's
        /// "disable this device"; what Disable-PnpDevice leaves behind).</summary>
        public const uint CM_PROB_DISABLED = 22;

        /// <summary>Problem code: phantom devnode - registered, not physically
        /// present (Get-PnpDevice reports these as "Unknown").</summary>
        public const uint CM_PROB_PHANTOM = 45;

        /// <summary>Apply the enable/disable to the base hardware store - the
        /// shape Disable-PnpDevice lands with on the field box. Verified by
        /// call-shape probe (2026-10-30): CM_Disable_DevNode(inst, 0,
        /// DICS_FLAG_GLOBAL) returns CR_SUCCESS and the node answers
        /// CM_PROB_DISABLED, and CM_Enable_DevNode(inst, 0) restores it.
        /// DICS_DISABLE_GLOBAL_W as ulDisableFlags is REJECTED on this
        /// Windows - CR_FAIL (0x4) from every caller, admin shell included;
        /// the engine's webcam row returned verified=false because of it.</summary>
        public const uint DICS_FLAG_GLOBAL = 0x1;

        [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
        public static extern uint CM_Locate_DevNode(out uint pdwDevInst, string pDeviceID, uint ulFlags);

        [DllImport("CfgMgr32.dll")]
        public static extern uint CM_Get_DevNode_Status(out uint pulStatus, out uint pulProblemNumber, uint dnDevInst, uint ulFlags);

        [DllImport("CfgMgr32.dll")]
        public static extern uint CM_Enable_DevNode(uint dnDevInst, uint ulFlags);

        [DllImport("CfgMgr32.dll")]
        public static extern uint CM_Disable_DevNode(uint dnDevInst, uint ulDisableFlags, uint ulFlags);
    }
}
