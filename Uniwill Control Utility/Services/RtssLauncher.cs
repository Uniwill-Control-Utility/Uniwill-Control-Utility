using System;
using System.Diagnostics;
using System.Threading.Tasks;
using UCU.Contracts.Capabilities;
using UCU.Core.Rtss;

namespace Uniwill_Control_Utility.Services
{
    /// <summary>
    /// Starts RTSS. RTSS is a user-session tray/injection process, and on
    /// machines where it carries the "Run as administrator" launch flag
    /// (field-reported: double-clicking it prompts UAC) the app cannot start it
    /// silently: a plain Process.Start pops consent, and the elevation-free
    /// scheduled-task route places it in session 0 - invisible, injecting
    /// nothing, while the global shared-memory segment still reports "running"
    /// (field-proven). The engine is the starter: LocalSystem places the image
    /// in the ACTIVE session with the privileges its flags demand (the console
    /// user's linked token), and no dialog appears.
    ///
    /// Without an engine (portable layout, engine not installed) the app falls
    /// back to Process.Start, which behaves exactly like double-clicking: one
    /// consent prompt on a flagged machine, none on an unflagged one.
    /// </summary>
    public static class RtssLauncher
    {
        /// <summary>True when the start route is available: RTSS not running
        /// in this session, and an image located. One locate pass (the scan
        /// costs ~30 ms on a full-ARP machine, and the Overlay page's 2 s
        /// status poll calls this): IsInstalled IS a located image, so a
        /// separate IsInstalled() call would just double the scan.</summary>
        public static bool CanStart()
        {
            return !RtssDetectionService.IsRunning()
                && RtssLocation.Locate() != null;
        }

        /// <summary>
        /// Starts RTSS and reports whether it came up in this session within
        /// the poll window - READY, not merely existent: the window also
        /// waits for the hooks loader to register, so a game launched right
        /// after this call is hooked at creation (immediate OSD) instead of
        /// waiting on RTSS's next process scan. Never throws: a refused or
        /// unavailable start is a false answer.
        /// </summary>
        public static async Task<bool> StartAsync()
        {
            if (RtssLocation.Locate() == null)
                return false;

            bool? engineAnswer = await StartThroughEngineAsync();

            if (engineAnswer == null)
            {
                // No engine to ask (portable layout, engine down): the
                // double-click fallback, which the user's own launch does.
                try
                {
                    string? exe = RtssLocation.Locate();
                    if (exe == null)
                        return false;

                    Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
                }
                catch (Exception)
                {
                    // Consent refused, or the launch failed: the caller's false.
                    return false;
                }
            }

            // Readiness, not mere existence: RTSS spawns its hooks loader
            // during its own init, and a game launched while RTSS exists but
            // the loader has not registered is hooked only by RTSS's next
            // process scan, seconds later (field report 2026-11-01: ~12 s to
            // first OSD for a game started 2 s after this button; dev-box
            // chain probe: a game created in that window stayed out of the
            // app array for a minute). The 6 s window caps the wait: hooks-
            // less RTSS (injection disabled in its settings) is answered by
            // liveness, not by the loader that will never come.
            for (int i = 0; i < 12 && !(RtssDetectionService.IsRunning() && RtssDetectionService.AreHooksActive()); i++)
                await Task.Delay(500);

            return RtssDetectionService.IsRunning();
        }

        /// <summary>The engine's answer, or null when there is no engine to ask.</summary>
        private static async Task<bool?> StartThroughEngineAsync()
        {
            IRtssCapability? rtss = App.RtssCapability;
            if (rtss == null)
                return null;

            try
            {
                return await rtss.StartAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // EngineException (not openable, older engine without the
                // verb), timeout, pipe gone: the caller's fallback decides.
                return null;
            }
        }
    }
}
