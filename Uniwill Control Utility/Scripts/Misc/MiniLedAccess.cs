using System;
using Serilog;
using UCU.Contracts;
using UCU.Contracts.Capabilities;

namespace Uniwill_Control_Utility.Scripts.Misc
{
    /// <summary>
    /// MiniLED local dimming: the OEM ACPI WMI handshake
    /// (AcpiTest_MULong.GetSetULong on ACPI\PNP0C14\1_1, the enable pair
    /// 0x50000000002/0x5000000000E expecting reply bytes 0xBB/0x0E, the
    /// disable pair 0x50000000001/0x5000000000D expecting 0xAA/0x0D).
    ///
    /// Executing a custom root\WMI method demands namespace write access an
    /// unelevated process does not have — so on the guarded field machines
    /// the tray and display rows were silently dead: the InvokeMethod threw
    /// access denied into a catch that returned a bare false, with no prompt
    /// to explain. The engine performs the handshake as LocalSystem, so the
    /// engine verb is the FIRST route and the rows work with no prompt
    /// anywhere. The app's original WMI code survives below as the FALLBACK
    /// for a machine whose engine is not answering (where it works exactly
    /// as the old code did: the dev box runs the app elevated). Every switch
    /// of routes logs the reason, so the field can tell which route answered.
    ///
    /// The EC 0x72B bit-0x02 readback verify stays with the callers: it rides
    /// the existing telemetry verb, which is already engine-routed.
    /// </summary>
    internal static class MiniLedAccess
    {
        /// <summary>
        /// The engine's device capability (MiniLED verb). Set from DI at
        /// startup (App phase 1), the same static-singleton idiom as
        /// NvramAccess.Devices.
        /// </summary>
        internal static IDeviceCapability? Devices { get; set; }

        // The OEM entry and the handshake, verbatim from the pages' WMI code;
        // the engine's service carries the same constants and the parity is
        // pinned in tests so the routes cannot drift.
        public const string WmiClassName = "AcpiTest_MULong";
        public const string DefaultInstanceName = @"ACPI\PNP0C14\1_1";

        public const ulong CmdEnableStep1 = 0x50000000002;
        public const ulong CmdEnableStep2 = 0x5000000000E;
        public const ulong CmdDisableStep1 = 0x50000000001;
        public const ulong CmdDisableStep2 = 0x5000000000D;

        public const byte ExpectedEnableStep1 = 0xBB;
        public const byte ExpectedEnableStep2 = 0x0E;
        public const byte ExpectedDisableStep1 = 0xAA;
        public const byte ExpectedDisableStep2 = 0x0D;

        /// <summary>
        /// Runs the enable (or disable) handshake. True when the handshake
        /// verified (the firmware's reply bytes, not the call's success).
        /// </summary>
        public static bool SetLocalDimming(bool enabled)
        {
            if (Devices != null)
            {
                try
                {
                    bool verified = Devices.SetMiniLedDimmingAsync(enabled).GetAwaiter().GetResult();
                    Log.Information("[MiniLed] local dimming {State} via engine: verified={Verified}",
                        enabled ? "on" : "off", verified);
                    return verified;
                }
                catch (EngineException ex) when (ex.Code is EngineErrorCode.HardwareUnavailable
                    or EngineErrorCode.Timeout or EngineErrorCode.Busy)
                {
                    Log.Information("[MiniLed] engine not answering ({Code}) - using the local route", ex.Code);
                }
            }

            return LocalHandshake(enabled);
        }

        #region Local route (fallback: engine not answering; works when the app runs elevated)

        private static bool LocalHandshake(bool enabled)
        {
            try
            {
                string instancePath = $@"{WmiClassName}.InstanceName='{DefaultInstanceName}'";
                using var mo = new System.Management.ManagementObject(@"root\WMI", instancePath, null);

                byte result1 = WmiCall(mo, enabled ? CmdEnableStep1 : CmdDisableStep1);
                if (result1 != (enabled ? ExpectedEnableStep1 : ExpectedDisableStep1))
                {
                    Log.Information("[MiniLed] local {State} step 1 returned 0x{Result:X2}",
                        enabled ? "enable" : "disable", result1);
                    return false;
                }

                byte result2 = WmiCall(mo, enabled ? CmdEnableStep2 : CmdDisableStep2);
                if (result2 != (enabled ? ExpectedEnableStep2 : ExpectedDisableStep2))
                {
                    Log.Information("[MiniLed] local {State} step 2 returned 0x{Result:X2}",
                        enabled ? "enable" : "disable", result2);
                    return false;
                }

                Log.Information("[MiniLed] local {State} handshake verified", enabled ? "enable" : "disable");
                return true;
            }
            catch (Exception ex)
            {
                // The shape the field saw as silence: on an asInvoker token
                // this is the access-denied throw. The breadcrumb names it.
                Log.Information("[MiniLed] local WMI/ACPI call failed: {Message}", ex.Message);
                return false;
            }
        }

        private static byte WmiCall(System.Management.ManagementObject mo, ulong data)
        {
            using var input = mo.GetMethodParameters("GetSetULong");
            input["Data"] = data;

            using var output = (System.Management.ManagementBaseObject)mo.InvokeMethod("GetSetULong", input, null);
            return (byte)(uint)output["Return"];
        }

        #endregion
    }
}
