using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Serilog;

namespace Uniwill_Control_Utility.Scripts.Misc
{
    /// <summary>
    /// Runs the operations that still need administrator rights now that the app
    /// launches unelevated (the EC moved to the engine service; these features
    /// never did). Each is a short-lived elevated process started with a
    /// per-operation UAC prompt - the XMG Control Center model: the shell stays
    /// unprivileged and only the privileged act is consented to.
    ///
    /// Nothing here captures output: the shell-execute path that carries the
    /// verb cannot redirect streams, and every caller verifies its result by
    /// re-reading the device state (PnP status, admin-flow exit code), which is
    /// what the elevated helpers here return.
    /// </summary>
    internal static class ElevatedProcess
    {
        /// <summary>
        /// Starts <paramref name="fileName"/> elevated and hidden, and waits for
        /// it to exit. Returns the exit code, or -1 when the process could not
        /// start (no shell) or the user declined the consent prompt.
        /// </summary>
        public static async Task<int> RunAsync(string fileName, string arguments, string label)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            try
            {
                using var proc = Process.Start(psi);
                if (proc == null)
                    return -1;

                await proc.WaitForExitAsync().ConfigureAwait(false);

                Log.Information("[Elevation] {Label} elevated process exited with {ExitCode}", label, proc.ExitCode);
                return proc.ExitCode;
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                // 1223 = the user pressed "No" at the consent prompt (or cancelled
                // the UAC secure-desktop). A normal outcome, not a crash.
                Log.Information("[Elevation] {Label} elevation failed: {Message} (code {Code})",
                    label, ex.Message, ex.NativeErrorCode);
                return -1;
            }
        }

        /// <summary>Runs a PowerShell command elevated and hidden; see <see cref="RunAsync"/>.</summary>
        public static Task<int> RunPowershellAsync(string command, string label) =>
            RunAsync("powershell.exe", $"-NoProfile -WindowStyle Hidden -Command \"{command}\"", label);
    }
}
