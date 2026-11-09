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
    /// RTSS names its own image (the truth when present); the uninstall
    /// entries are read in BOTH registry views because the RTSS installer is
    /// 32-bit and the key lands under WOW6432Node (a 64-bit-only read misses
    /// real installs); on that install InstallLocation is EMPTY and
    /// DisplayIcon names uninstall.exe - the Add/Remove Programs icon - so an
    /// unguarded DisplayIcon would launch an uninstaller. Only a path that
    /// exists as a file is returned: a located path is launchable by contract.
    ///
    /// The Add/Remove Programs scan by PRODUCT NAME is load-bearing, not
    /// tidiness: the fixed ...\Uninstall\RTSS key + default-directory probe
    /// was the only shape, and the tester laptop proved its failure mode -
    /// an installed RTSS the probe misses returns null, the Start button
    /// no-ops with zero log lines, and the engine verb is never asked (the
    /// verb logs "no RTSS image located" when it IS asked, so the silent
    /// app-side exit is provably client-side). Scanning the ARP entries in
    /// HKLM and HKCU, both views, for the product name finds installs under
    /// any key name in any directory, per-user or per-machine.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static class RtssLocation
    {
        /// <summary>The only image name this locator will hand out for a start.</summary>
        public const string ExeName = "RTSS.exe";

        private const string UninstallRootPath =
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

        private const string DefaultPath =
            @"C:\Program Files (x86)\RivaTuner Statistics Server\RTSS.exe";

        /// <summary>True when RTSS's own uninstall entry exists (any hive, any
        /// view, any key name), running state irrelevant.</summary>
        public static bool IsInstalled()
        {
            return Locate() != null;
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

            // The classic fixed key first (the probed 7.3.7 shape), then the
            // product-name scan of every uninstall entry in every hive/view,
            // then the default directory.
            foreach (RegistryHive hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
            {
                foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                {
                    try
                    {
                        using RegistryKey? root = RegistryKey.OpenBaseKey(hive, view).OpenSubKey(UninstallRootPath);
                        if (root == null)
                            continue;

                        using (RegistryKey? fixedKey = root.OpenSubKey("RTSS"))
                        {
                            string? fromFixed = LaunchablePathFrom(fixedKey);
                            if (fromFixed != null)
                                return fromFixed;
                        }

                        string? fromScan = ScanUninstallEntries(root);
                        if (fromScan != null)
                            return fromScan;
                    }
                    catch (Exception)
                    {
                        // Location probing never outlives its read - next witness.
                    }
                }
            }

            return IsLaunchableFile(DefaultPath) ? DefaultPath : null;
        }

        /// <summary>
        /// True when an uninstall entry IS RTSS: the key name the installer
        /// writes ("RTSS" on the probed installs), or a display name naming
        /// the product. The product name, not a bare "RTSS" substring, so an
        /// unrelated entry with RTSS in a path value cannot claim the witness.
        /// </summary>
        internal static bool IsRtssInstallName(string keyName, string? displayName)
        {
            if (string.Equals(keyName, "RTSS", StringComparison.OrdinalIgnoreCase))
                return true;

            return displayName != null
                && displayName.IndexOf("RivaTuner", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string? ScanUninstallEntries(RegistryKey root)
        {
            string[] names;
            try
            {
                names = root.GetSubKeyNames();
            }
            catch (Exception)
            {
                return null;
            }

            foreach (string name in names)
            {
                try
                {
                    using RegistryKey? entry = root.OpenSubKey(name);
                    if (entry == null)
                        continue;

                    if (!IsRtssInstallName(name, entry.GetValue("DisplayName") as string))
                        continue;

                    string? path = LaunchablePathFrom(entry);
                    if (path != null)
                        return path;
                }
                catch (Exception)
                {
                    // Unreadable entry: next witness.
                }
            }

            return null;
        }

        /// <summary>
        /// The launchable RTSS image an uninstall entry implies, or null:
        /// InstallLocation + RTSS.exe, DisplayIcon when it is itself the
        /// image (7.3.7 names uninstall.exe - the guard refuses), and the
        /// directory the uninstaller lives in (RTSS ships it beside
        /// uninstall.exe). Every candidate passes the launch guard.
        /// </summary>
        private static string? LaunchablePathFrom(RegistryKey? entry)
        {
            if (entry == null)
                return null;

            string? location = CleanPath(entry.GetValue("InstallLocation") as string);
            if (location != null)
            {
                string candidate = Path.Combine(location, ExeName);
                if (IsLaunchableFile(candidate))
                    return Path.GetFullPath(candidate);
            }

            string? icon = CleanIconPath(entry.GetValue("DisplayIcon") as string);
            if (IsLaunchableFile(icon))
                return Path.GetFullPath(icon!);

            foreach (string valueName in new[] { "UninstallString", "QuietUninstallString" })
            {
                string? dir = UninstallerDirectory(entry.GetValue(valueName) as string);
                if (dir == null)
                    continue;

                string candidate = Path.Combine(dir, ExeName);
                if (IsLaunchableFile(candidate))
                    return Path.GetFullPath(candidate);
            }

            return null;
        }

        /// <summary>
        /// The directory an UninstallString's uninstaller lives in: the
        /// quoted-or-whitespace command line, stripped of its exe name. A
        /// relative or malformed value tells nothing (null).
        /// </summary>
        private static string? UninstallerDirectory(string? raw)
        {
            string? command = CleanPath(raw);
            if (command == null)
                return null;

            // "C:\dir\uninstall.exe /U" or "uninstall.exe /U" (relative: useless).
            string exe = command.StartsWith("\"", StringComparison.Ordinal)
                ? command.Substring(1).Split('"')[0]
                : command.Split(' ')[0];

            string? dir = Path.GetDirectoryName(exe);
            return string.IsNullOrWhiteSpace(dir) || !Path.IsPathRooted(dir) ? null : dir;
        }

        /// <summary>Session ids of every running RTSS, and false when the
        /// snapshot ITSELF is unavailable (a security product refused it) -
        /// the caller needs to know that an empty list means "no RTSS", not
        /// "no information".</summary>
        public static bool TryRunningSessions(out int[] sessions)
        {
            sessions = Array.Empty<int>();

            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName("RTSS");
            }
            catch (Exception)
            {
                return false;
            }

            foreach (Process process in processes)
            {
                using (process)
                {
                    try
                    {
                        int session = process.SessionId;
                        Array.Resize(ref sessions, sessions.Length + 1);
                        sessions[^1] = session;
                    }
                    catch (Exception)
                    {
                        // A process that exited mid-scan tells nothing.
                    }
                }
            }

            return true;
        }

        /// <summary>Session ids of every running RTSS; empty when the snapshot
        /// is unavailable (see <see cref="TryRunningSessions"/>).</summary>
        public static int[] RunningSessions()
        {
            return TryRunningSessions(out int[] sessions) ? sessions : Array.Empty<int>();
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

        /// <summary>
        /// The engine-side launch guard: RTSS.exe *and* under a Program Files root.
        /// The pipe's DACL admits every interactive user, so an HKCU uninstall
        /// witness - which any same-user process can write - must not be able to
        /// point the LocalSystem engine at a user-writable image: that is an
        /// unelevated process starting itself with the console user's linked
        /// (admin) token, with no UAC prompt in between.
        ///
        /// shortcut: a per-user RTSS install outside Program Files (never seen in
        /// the field; the installer writes Program Files (x86)) is refused rather
        /// than signature-verified. Upgrade to an Authenticode signer check if
        /// that install shape ever appears.
        /// </summary>
        public static bool IsTrustedLaunchImage(string? path)
        {
            if (!IsLaunchableImage(path))
                return false;

            string full;
            try
            {
                // Resolve first: "C:\Program Files\..\Users\me\RTSS.exe" passes a
                // prefix test but is not under Program Files.
                full = Path.GetFullPath(path!.Trim().Trim('"'));
            }
            catch (Exception)
            {
                return false;
            }

            return IsUnder(full, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles))
                || IsUnder(full, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
        }

        private static bool IsUnder(string fullPath, string root)
        {
            if (string.IsNullOrEmpty(root))
                return false;

            string prefix;
            try
            {
                // The trailing separator is the point: "C:\Program Files (x86)evil"
                // must not match "C:\Program Files (x86)".
                prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
            }
            catch (Exception)
            {
                return false;
            }

            return fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
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
    }
}
