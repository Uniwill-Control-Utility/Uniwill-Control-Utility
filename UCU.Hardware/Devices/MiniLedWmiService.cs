using System.Management;
using Serilog;

namespace UCU.Hardware.Devices
{
    /// <summary>
    /// The seam the MiniLED dimming verb runs on: the production implementation
    /// executes the OEM ACPI WMI handshake as LocalSystem; the RPC tests inject
    /// a scripted handshake, because no test may write a real panel's
    /// local-dimming controller.
    /// </summary>
    public interface IMiniLedService
    {
        /// <summary>Runs the enable (or disable) two-step handshake, each step
        /// verified against the firmware's expected reply byte. True when both
        /// steps verified.</summary>
        bool SetLocalDimming(bool enabled);
    }

    /// <summary>
    /// MiniLED local dimming through the OEM ACPI WMI class:
    /// AcpiTest_MULong.GetSetULong on instance ACPI\PNP0C14\1_1. The enable
    /// path is a two-step handshake (0x50000000002 expecting the reply byte
    /// 0xBB, then 0x5000000000E expecting 0x0E); the disable path mirrors it
    /// (0x...1 expecting 0xAA, 0x...D expecting 0x0D). Executing a custom
    /// root\WMI method demands the namespace write access an unelevated
    /// process does not have, so on the guarded field machines the app-side
    /// InvokeMethod threw access denied into a catch that returned a bare
    /// false: the tray and display rows were silently dead. The engine runs
    /// LocalSystem, the handshake works there, and the reply-byte verification
    /// is kept (the firmware's answer is the witness, not the call's success).
    ///
    /// The method-call shape (GetMethodParameters/InvokeMethod, the "Return"
    /// unboxing) is verbatim from the app's pages, which took it from
    /// MechControl (Heavyfail) — proven on Uniwill IDY / Tongfang X6 boards.
    /// The EC 0x72B bit-0x02 readback stays app-side: it rides the existing
    /// telemetry verb.
    /// </summary>
    public sealed class MiniLedWmiService : IMiniLedService
    {
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

        public bool SetLocalDimming(bool enabled)
        {
            try
            {
                string instancePath = $@"{WmiClassName}.InstanceName='{DefaultInstanceName}'";
                using var mo = new ManagementObject(@"root\WMI", instancePath, null);

                ulong step1 = enabled ? CmdEnableStep1 : CmdDisableStep1;
                ulong step2 = enabled ? CmdEnableStep2 : CmdDisableStep2;
                byte expected1 = enabled ? ExpectedEnableStep1 : ExpectedDisableStep1;
                byte expected2 = enabled ? ExpectedEnableStep2 : ExpectedDisableStep2;

                byte result1 = WmiCall(mo, step1);
                if (result1 != expected1)
                {
                    Log.Information("[MiniLed] {State} step 1 returned 0x{Result:X2} (expected 0x{Expected:X2})",
                        enabled ? "enable" : "disable", result1, expected1);
                    return false;
                }

                byte result2 = WmiCall(mo, step2);
                if (result2 != expected2)
                {
                    Log.Information("[MiniLed] {State} step 2 returned 0x{Result:X2} (expected 0x{Expected:X2})",
                        enabled ? "enable" : "disable", result2, expected2);
                    return false;
                }

                Log.Information("[MiniLed] {State} handshake verified", enabled ? "enable" : "disable");
                return true;
            }
            catch (Exception ex)
            {
                // The witness the app-side catch never logged: an access-denied
                // WMI security exception (the unelevated shape) versus a
                // missing ACPI instance versus a WMI-service fault all read
                // differently here, and the EC readback's caller asks which.
                Log.Information("[MiniLed] WMI/ACPI call failed: {Message}", ex.Message);
                return false;
            }
        }

        private static byte WmiCall(ManagementObject mo, ulong data)
        {
            using var input = mo.GetMethodParameters("GetSetULong");
            input["Data"] = data;

            using var output = (ManagementBaseObject)mo.InvokeMethod("GetSetULong", input, null);
            return (byte)(uint)output["Return"];
        }
    }
}
