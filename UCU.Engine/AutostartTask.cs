using System;
using System.IO;
using System.Linq;
using Microsoft.Win32.TaskScheduler;
using Serilog;

namespace UCU.Engine
{
    /// <summary>
    /// The user's logon autostart entry ("UCU Autostart"), owned by the service.
    ///
    /// Why the service and not the app: the Task Scheduler service denies task
    /// creation to a medium-integrity token, so an unelevated app cannot register
    /// one at all (field 2026-11-06: <c>E_ACCESSDENIED</c> from
    /// <c>RegisterTaskDefinition</c>, surfacing as an error dialog when Settings
    /// was opened). 0.9.6 got away with it because the whole app ran elevated;
    /// 0.9.7 runs <c>asInvoker</c> and the entry has to come from LocalSystem.
    ///
    /// Trust boundary: the caller sends a boolean and a delay, never a path or an
    /// account. The action is the app exe staged beside the engine's own Service
    /// folder, and the principal is the interactive session's user - a
    /// caller-supplied path here would let any authenticated account schedule
    /// arbitrary code to run at its own logon, which is exactly the shape the
    /// service DACL exists to prevent.
    /// </summary>
    internal static class AutostartTask
    {
        internal const string TaskName = "UCU Autostart";
        internal const string Description = "Start UCU";

        /// <summary>The Settings row's own range; the service clamps rather than trusts it.</summary>
        internal const int MaxDelaySeconds = 120;

        /// <summary>
        /// Creates/updates the logon entry (or removes it). Returns false when the
        /// entry cannot exist on this layout (no app exe beside the engine) or
        /// when no interactive user can be resolved; throws nothing - the caller
        /// reports a refusal, it does not crash the engine.
        /// </summary>
        internal static bool Apply(bool enabled, int delaySeconds)
        {
            try
            {
                if (!enabled)
                {
                    using var ts = new TaskService();
                    if (ts.RootFolder.AllTasks.Any(t => t.Name == TaskName))
                        ts.RootFolder.DeleteTask(TaskName);

                    Log.Information("[Autostart] '{Task}' removed", TaskName);
                    return true;
                }

                string? exe = AppSupervision.FindAppExe();
                if (exe is null)
                {
                    Log.Warning("[Autostart] no app exe beside the engine; nothing to schedule");
                    return false;
                }

                string? account = InteractiveUser.AccountName();
                if (account is null)
                {
                    Log.Warning("[Autostart] no interactive user resolved; leaving the entry alone");
                    return false;
                }

                int seconds = Math.Clamp(delaySeconds, 0, MaxDelaySeconds);

                using var service = new TaskService();
                if (service.RootFolder.AllTasks.Any(t => t.Name == TaskName))
                    service.RootFolder.DeleteTask(TaskName);

                TaskDefinition definition = service.NewTask();

                // LIMITED, like the app: the entry must not elevate, or every logon
                // would demand consent. The service supplies the rights the app
                // itself needs - which is exactly why the task exists at all.
                definition.Principal.UserId = account;
                definition.Principal.LogonType = TaskLogonType.InteractiveToken;
                definition.Principal.RunLevel = TaskRunLevel.LUA;

                definition.RegistrationInfo.Description = Description;
                definition.Settings.DisallowStartIfOnBatteries = false;
                definition.Settings.StopIfGoingOnBatteries = false;
                definition.Settings.DisallowStartOnRemoteAppSession = false;

                definition.Triggers.Add(new LogonTrigger { Delay = TimeSpan.FromSeconds(seconds) });

                // Working directory = the app's folder: the app pins its CWD to the
                // payload directory on startup (native DLLs load from there) and the
                // scheduler's default CWD is the system directory.
                definition.Actions.Add(new ExecAction(exe, null, Path.GetDirectoryName(exe)));

                service.RootFolder.RegisterTaskDefinition(
                    TaskName,
                    definition,
                    TaskCreation.CreateOrUpdate,
                    account,
                    null,
                    TaskLogonType.InteractiveToken,
                    null);

                Log.Information("[Autostart] '{Task}' registered for {Account} (delay {Delay}s, exe {Exe})",
                    TaskName, account, seconds, exe);
                return true;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[Autostart] could not {Action} '{Task}'",
                    enabled ? "register" : "remove", TaskName);
                return false;
            }
        }
    }

    /// <summary>
    /// The account a per-user entry belongs to. The running app's session is the
    /// authentic answer (it is the caller); the active console session is the
    /// fallback, so a request that arrives while the app is restarting still
    /// resolves. Both come from the session list, never from the request.
    /// </summary>
    internal static class InteractiveUser
    {
        private const int WtsUserName = 5;
        private const int WtsDomainName = 7;

        internal static string? AccountName()
        {
            uint session = 0;
            bool located = false;

            try
            {
                System.Diagnostics.Process? app = System.Diagnostics.Process
                    .GetProcessesByName(AppSupervision.AppProcessName)
                    .FirstOrDefault();
                if (app is not null)
                {
                    session = (uint)app.SessionId;
                    located = true;
                }
            }
            catch
            {
                // Session enumeration is a nicety; the console fallback covers it.
            }

            if (!located)
            {
                session = WTSGetActiveConsoleSessionId();
                if (session == 0xFFFFFFFF)
                    return null;
            }

            string? user = Query(session, WtsUserName);
            if (string.IsNullOrEmpty(user))
                return null;

            string? domain = Query(session, WtsDomainName);
            return string.IsNullOrEmpty(domain) ? user : domain + "\\" + user;
        }

        private static string? Query(uint session, int infoClass)
        {
            if (!WTSQuerySessionInformationW(IntPtr.Zero, session, infoClass, out IntPtr buffer, out uint _))
                return null;

            try
            {
                return buffer == IntPtr.Zero ? null : System.Runtime.InteropServices.Marshal.PtrToStringUni(buffer);
            }
            finally
            {
                if (buffer != IntPtr.Zero)
                    WTSFreeMemory(buffer);
            }
        }

        // kernel32, not wtsapi32: the export lives there (a wtsapi32 import throws
        // EntryPointNotFoundException at the first call - the field defect that
        // once took the whole engine host down from the supervision tick).
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern uint WTSGetActiveConsoleSessionId();

        [System.Runtime.InteropServices.DllImport("wtsapi32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern bool WTSQuerySessionInformationW(
            IntPtr server, uint sessionId, int infoClass, out IntPtr buffer, out uint bytesReturned);

        [System.Runtime.InteropServices.DllImport("wtsapi32.dll")]
        private static extern void WTSFreeMemory(IntPtr memory);
    }
}
