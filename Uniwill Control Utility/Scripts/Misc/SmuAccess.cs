using System;
using Serilog;
using UCU.Contracts;
using UCU.Contracts.Capabilities;
using RyzenSmu;

namespace Uniwill_Control_Utility.Scripts.Misc
{
    /// <summary>
    /// AMD SMU mailbox access (Curve Optimizer, PBO, the preset power
    /// vocabulary) for the RyzenAdj command interpreter.
    ///
    /// The SMU commands drive PawnIO, whose device ACL admits System and
    /// Administrators — an unelevated app's opens of the driver are
    /// access-denied, so every SMU preset apply on the field machines was
    /// silently dead: the row saved, the register never moved, no prompt and
    /// no error (the initialisation returns false and the apply throws into
    /// the batch's log line, one notch under what the user reads). The engine
    /// runs LocalSystem: the same PawnIO module loads there, the mailbox
    /// protocol runs there, and the verb answers with the SMU's own status
    /// code. The engine verb is therefore the FIRST route.
    ///
    /// The app's own SMU stack survives as the FALLBACK for a machine whose
    /// engine is not answering — where it works (the app run elevated, e.g.
    /// the dev box), it works exactly as the old code did. The duplication of
    /// the SMU stack between app and engine is the architecture: the app can
    /// never reference UCU.Hardware, so each side owns a copy. Every switch
    /// of routes logs the reason, so the field can tell which route answered.
    /// </summary>
    internal static class SmuAccess
    {
        /// <summary>
        /// The engine's SMU capability (cpu.execute_amd_smu_command). Set from
        /// DI at startup (App phase 1), the same static-singleton idiom as
        /// NvramAccess.Devices.
        /// </summary>
        internal static IAmdSmuCapability? Cpu { get; set; }

        /// <summary>
        /// Execute one SMU command with the packed value. Returns the SMU
        /// status code (0x01 = OK) from whichever route answered.
        ///
        /// An engine reply of HardwareUnavailable (the service stopped, no
        /// pipe) moves to the local route: that is the fallback-first design.
        /// An engine reply of HardwareFault (PawnIO failed to load THERE, the
        /// SMU answered a non-OK status) is the truth and rethrows: falling
        /// back to an unelevated PawnIO open would reproduce the silent death
        /// this class exists to route around.
        /// </summary>
        public static int Execute(string commandName, uint value)
        {
            IAmdSmuCapability? cpu = Cpu;
            if (cpu != null)
            {
                try
                {
                    int status = cpu.ExecuteCommandAsync(commandName, value).GetAwaiter().GetResult();
                    Log.Information("[Smu] {Command} = 0x{Value:X} via engine: status=0x{Status:X}",
                        commandName, value, status);
                    return status;
                }
                catch (EngineException ex) when (ex.Code is EngineErrorCode.HardwareUnavailable
                    or EngineErrorCode.Timeout or EngineErrorCode.Busy)
                {
                    Log.Information("[Smu] engine not answering ({Code}) - using the local route", ex.Code);
                }
            }

            // Local route: the app's own SMU stack. On an asInvoker token the
            // PawnIO open fails and this throws InvalidOperationException
            // ("AMD PawnIO failed to initialise") into the batch's error log —
            // the pre-engine behaviour, kept verbatim as the fallback.
            SMUCommands.applySettings(commandName, value);
            Log.Information("[Smu] {Command} = 0x{Value:X} via local route", commandName, value);
            return 0x01;
        }
    }
}
