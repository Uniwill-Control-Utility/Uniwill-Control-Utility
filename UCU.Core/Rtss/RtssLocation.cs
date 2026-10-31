using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;

namespace UCU.Core.Rtss
{
    /// <summary>
    /// Where RTSS lives and what may be launched as RTSS. Shared by the app
    /// (the prerequisite row's Start button) and the engine (the session
    /// launcher): both need the same witnesses and the same guard, and a
    /// launch that picks the wrong image is not a cosmetic bug.
    ///
    /// The witness order comes from probing a real 7.3.7 install: a running
    /// RTSS names its own image (the truth when present); the uninstall entry
    /// is read in BOTH registry views because the RTSS installer is 32-bit and
    /// the key lands under WOW6432Node (a 64-bit-only read misses real
    /// installs); on that install InstallLocation is EMPTY and DisplayIcon
    /// names uninstall.exe - the Add/Remove Programs icon - so an unguarded
    /// DisplayIcon would launch an uninstaller. Only a path that exists as a
    /// file is returned: a located path is launchable by contract.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static class RtssLocation
    {
        /// <summary>The only image name this locator will hand out for a start.</summary>
        public const string ExeName = "RTSS.exe";

        private const string UninstallKeyPath =
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\RTSS";

        private const string DefaultPath =
            @"C:\Program Files (x86)\RivaTuner Statistics Server\RTSS.exe";

        /// <summary>True when RTSS's own uninstall entry exists, running state irrelevant.</summary>
        public static bool IsInstalled()
        {
            return ExistsIn(RegistryView.Registry64) || ExistsIn(RegistryView.Registry32);
        }

        /// <summary>
        /// The RTSS image, launchable by contract (it exists, and it is named
        /// RTSS.exe), or null when no witness produced one.
        /// </summary>
        public static string? Locate()
        {
            foreach (string running in RunningPaths())
            {
                if (IsLaunchableImage(running))
                    return running;
            }

            foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using RegistryKey? key = RegistryKey
                        .OpenBaseKey(RegistryHive.LocalMachine, view)
                        .OpenSubKey(UninstallKeyPath);

                    if (key == null)
                        continue;

                    string? location = CleanPath(key.GetValue("InstallLocation") as string);
                    if (location != null)
                    {
                        string candidate = Path.Combine(location, ExeName);

                        if (IsLaunchableFile(candidate))
                            return Path.GetFullPath(candidate);
                    }

                    string? icon = CleanIconPath(key.GetValue("DisplayIcon") as string);
                    if (IsLaunchableFile(icon))
                        return Path.GetFullPath(icon!);
                }
                catch (Exception)
                {
                    // Location probing never outlives its read - next witness.
                }
            }

            return IsLaunchableFile(DefaultPath) ? DefaultPath : null;
        }

        /// <summary>Session ids of every running RTSS. RTSS is a per-session
        /// overlay, so a caller in the user's session must not read a
        /// session-0 instance as its own (the shared-memory segment is
        /// global and lies: field-proven).</summary>
        public static int[] RunningSessions()
        {
            int[] sessions = Array.Empty<int>();

            foreach (Process process in SafeProcesses())
            {
                using (process)
                {
                    try
                    {
                        int session = process.SessionId;
                        Array.Resize(ref sessions, sessions.Length + 1);
                        sessions[sessions.Length - 1] = session;
                    }
                    catch (Exception)
                    {
                        // A process that exited mid-scan tells nothing.
                    }
                }
            }

            return sessions;
        }

        /// <summary>Images of every running RTSS, best-effort (a security
        /// product can deny a module read; that process is simply skipped).</summary>
        public static string[] RunningPaths()
        {
            string[] paths = Array.Empty<string>();

            foreach (Process process in SafeProcesses())
            {
                using (process)
                {
                    try
                    {
                        string? path = process.MainModule?.FileName;

                        if (!string.IsNullOrWhiteSpace(path))
                        {
                            Array.Resize(ref paths, paths.Length + 1);
                            paths[paths.Length - 1] = path;
                        }
                    }
                    catch (Exception)
                    {
                        // Denied module read: next process.
                    }
                }
            }

            return paths;
        }

        /// <summary>True when a running RTSS lives in <paramref name="sessionId"/>.</summary>
        public static bool IsRunningInSession(int sessionId)
        {
            foreach (int session in RunningSessions())
            {
                if (session == sessionId)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// The launch guard: the path's file name must be RTSS.exe. The engine
        /// verb takes no path parameter and the app must launch only what this
        /// accepts - an uninstall entry's icon pointing at uninstall.exe is the
        /// real-world shape this refuses.
        /// </summary>
        public static bool IsLaunchableImage(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            string name;
            try
            {
                name = Path.GetFileName(path.Trim().Trim('"'));
            }
            catch (Exception)
            {
                return false;
            }

            return string.Equals(name, ExeName, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsLaunchableFile(string? path)
        {
            return IsLaunchableImage(path) && File.Exists(path!.Trim().Trim('"'));
        }

        private static Process[] SafeProcesses()
        {
            try
            {
                return Process.GetProcessesByName("RTSS");
            }
            catch (Exception)
            {
                // A snapshot that fails tells nothing.
                return Array.Empty<Process>();
            }
        }

        private static string? CleanPath(string? raw)
        {
            string? path = raw == null ? null : raw.Trim().Trim('"');
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }

        /// <summary>"C:\...\RTSS.exe,0" - the icon-index suffix is not part of the path.</summary>
        private static string? CleanIconPath(string? raw)
        {
            string? path = CleanPath(raw);
            if (path == null)
                return null;

            int comma = path.LastIndexOf(',');
            if (comma > 1)
                path = path.Substring(0, comma).Trim();

            return string.IsNullOrWhiteSpace(path) ? null : path;
        }

        private static bool ExistsIn(RegistryView view)
        {
            try
            {
                using RegistryKey? key = RegistryKey
                    .OpenBaseKey(RegistryHive.LocalMachine, view)
                    .OpenSubKey(UninstallKeyPath);

                return key != null;
            }
            catch (Exception)
            {
                // Detection never outlives its read.
                return false;
            }
        }
    }
}
