using Microsoft.Win32.TaskScheduler;
using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Uniwill_Control_Utility.Services
{
    /// <summary>
    /// Shared registration of the logon scheduled task ("UCU Autostart"). It
    /// runs with LIMITED privileges: the app manifest is asInvoker (the
    /// privileged EC work lives in the UCUEngine service), so an unelevated app
    /// registers and runs the task as the plain interactive user — no UAC
    /// prompt at logon. Crash relaunch is the engine's supervisor, not a task
    /// (the retired "UCU Guardian" watchdog is cleaned up by
    /// App.RetireGuardianTask and the uninstall shim).
    ///
    /// The action path is baked in at registration time, so a moved install
    /// (zip → MSI migration) needs the self-heal in App.RunPostStartupTasksAsync.
    /// </summary>
    internal static class ScheduledTaskService
    {
        public static string CurrentExePath
        {
            get
            {
                string path = Assembly.GetEntryAssembly()?.Location
                    ?? Assembly.GetExecutingAssembly().Location;
                return Path.ChangeExtension(path, ".exe");
            }
        }

        /// <param name="exePath">
        /// Action executable; defaults to the main app exe (the parameter
        /// exists for tasks that point at a sibling executable).
        /// </param>
        public static void Register(string name, string description, string arguments, TimeSpan delay, string? exePath = null)
        {
            using var ts = new TaskService();

            // Delete existing task so we can recreate with updated settings.
            if (ts.RootFolder.AllTasks.Any(t => t.Name == name))
                ts.RootFolder.DeleteTask(name);

            TaskDefinition td = ts.NewTask();

            // LUA (the library's name for the limited/filtered token): the app
            // runs unelevated, and a standard user can register and run a
            // limited task for their own logon session without a UAC prompt.
            // Highest would demand elevation the asInvoker app no longer has.
            td.Principal.RunLevel = TaskRunLevel.LUA;
            td.RegistrationInfo.Description = description;
            td.Settings.DisallowStartIfOnBatteries = false;
            td.Settings.StopIfGoingOnBatteries = false;
            td.Settings.DisallowStartOnRemoteAppSession = false;

            td.Triggers.Add(new LogonTrigger { Delay = delay });

            // Working directory = the exe's folder: the app resolves its logs
            // folder (and the graceful-exit flag the guardian and the engine's
            // supervisor read) relative to the CWD, and the task scheduler's
            // default CWD is the system directory.
            string target = exePath ?? CurrentExePath;
            td.Actions.Add(new ExecAction(target,
                string.IsNullOrEmpty(arguments) ? null : arguments,
                Path.GetDirectoryName(target)));

            ts.RootFolder.RegisterTaskDefinition(name, td);
        }

        public static void Unregister(string name)
        {
            using var ts = new TaskService();
            if (ts.RootFolder.AllTasks.Any(t => t.Name == name))
                ts.RootFolder.DeleteTask(name);
        }

        /// <summary>
        /// The executable path the task's action points at, or null when the
        /// task is not registered. Used by the post-migration self-heal.
        /// </summary>
        public static string? GetActionPath(string name)
        {
            using var ts = new TaskService();
            var task = ts.RootFolder.AllTasks.FirstOrDefault(t => t.Name == name);
            return task?.Definition.Actions.OfType<ExecAction>().FirstOrDefault()?.Path;
        }
    }
}
