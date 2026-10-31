using System;
using Uniwill_Control_Utility.Services.Performance;
using UCU.Core.Rtss;

namespace Uniwill_Control_Utility.Services
{
    /// <summary>
    /// RTSS presence, separate from RTSS liveness, and liveness SCOPED TO THIS
    /// SESSION. The prerequisite witness is RTSS's own uninstall entry, read in
    /// BOTH registry views - the RTSS installer is 32-bit, so on 64-bit Windows
    /// the key lands under WOW6432Node (field-reported: a running-only check
    /// told an installed RTSS that was not in the boot set "Required Software").
    ///
    /// Liveness is a process in the caller's session, not the shared-memory
    /// segment: the segment name is global, so a RTSS started in session 0 (the
    /// scheduled-task route's field-proven failure) answers "running" to a user
    /// who has no tray icon and no overlay. The segment remains the witness
    /// only when no RTSS process can be seen at all.
    ///
    /// Registry reads are swallowed to false - the detection-service rule the
    /// PawnIO detection follows.
    /// </summary>
    public static class RtssDetectionService
    {
        /// <summary>True when RTSS is installed, running state irrelevant.</summary>
        public static bool IsInstalled() => RtssLocation.IsInstalled();

        /// <summary>The RTSS image, launchable by contract, or null.</summary>
        public static string? LocateExe() => RtssLocation.Locate();

        /// <summary>True when RTSS is live IN THIS SESSION (the overlay the user can see).</summary>
        public static bool IsRunning()
        {
            int[] sessions;
            try
            {
                sessions = RtssLocation.RunningSessions();
            }
            catch (Exception)
            {
                // A snapshot that fails tells nothing; the segment decides.
                return RtssReader.IsRunning();
            }

            if (sessions.Length == 0)
                return RtssReader.IsRunning();

            int mine = SafeSessionId();

            foreach (int session in sessions)
            {
                if (session == mine)
                    return true;
            }

            // RTSS is running, just not for this session: the user has no
            // overlay, and the prerequisite row must say so.
            return false;
        }

        private static int SafeSessionId()
        {
            try
            {
                using System.Diagnostics.Process self = System.Diagnostics.Process.GetCurrentProcess();
                return self.SessionId;
            }
            catch (Exception)
            {
                // No self-session: the caller's comparison simply fails safe
                // (RTSS is not reported as running for this session).
                return -1;
            }
        }
    }
}
