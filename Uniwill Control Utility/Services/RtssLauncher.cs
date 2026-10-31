using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Uniwill_Control_Utility.Services
{
    /// <summary>
    /// Starts RTSS without a consent dialog. On machines where RTSS carries
    /// the "Run as administrator" AppCompat flag (the common overlay setup for
    /// elevated games - field-reported: double-clicking RTSS prompts UAC),
    /// a plain Process.Start from the asInvoker app pops that prompt. The
    /// elevation-free route is the one-shot highest-privileges scheduled task
    /// (ScheduledTaskService.RunOneShotHighestPrivileges - the touchpad
    /// settings flow and the uninstall verb's proven pattern): the Task
    /// Scheduler service supplies the full token, the process lands in the
    /// INTERACTIVE session (the tray icon and overlay work; the SYSTEM engine
    /// must not be the starter - session 0 would render RTSS invisible), and
    /// no dialog appears.
    ///
    /// The task route failing falls back to Process.Start, which behaves like
    /// double-clicking (one consent prompt, honoured on unguarded machines
    /// without one).
    /// </summary>
    public static class RtssLauncher
    {
        private const string TaskName = "UCU RTSS Launcher";
        private const string TaskDescription = "Start RTSS with the privileges its launch flags demand";

        /// <summary>True when the start route is available: RTSS installed, not running, exe located.</summary>
        public static bool CanStart()
        {
            return RtssDetectionService.IsInstalled()
                && !RtssDetectionService.IsRunning()
                && RtssDetectionService.LocateExe() != null;
        }

        /// <summary>
        /// Starts RTSS and reports whether it came up within the poll window.
        /// Never throws: a refused or unavailable start is a false answer.
        /// </summary>
        public static async Task<bool> StartAsync()
        {
            string? exe = RtssDetectionService.LocateExe();
            if (exe == null)
                return false;

            int? taskResult = await Task.Run(() =>
                ScheduledTaskService.RunOneShotHighestPrivileges(
                    TaskName, TaskDescription, exe, null));

            if (taskResult == null)
            {
                // Task route unavailable - the double-click fallback.
                try
                {
                    Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
                }
                catch (Exception)
                {
                    // Consent refused or the launch failed: the caller's false.
                    return false;
                }
            }

            for (int i = 0; i < 12 && !RtssDetectionService.IsRunning(); i++)
                await Task.Delay(500);

            return RtssDetectionService.IsRunning();
        }
    }
}
