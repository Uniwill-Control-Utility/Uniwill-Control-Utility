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

            // Working directory = the exe's folder: the app pins its CWD to the
            // payload directory on startup (native DLLs load from there), and the
            // task scheduler's default CWD is the system directory. Logs, presets
            // and the graceful-exit signal are per-user/RPC, no longer CWD-relative.
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
        /// The elevation-free route to an action that demands a full administrator
        /// token AND the interactive user session - today only the touchpad's
        /// SystemSettingsAdminFlows.exe, which the Windows Settings app itself
        /// launches through this exact mechanism (its UAC consent is answered by a
        /// scheduled task, not by a prompt). Registered for the current user at
        /// Highest privileges with an on-demand S4U logon, the task is created,
        /// run, and deleted by the unelevated app's owner; the Task Scheduler
        /// service (SYSTEM) supplies the elevation, so no consent dialog appears.
        /// This is the same pattern the uninstall verb's SYSTEM task uses, aimed at
        /// the interactive session instead of SYSTEM.
        ///
        /// Returns the action's exit code, or null when the task could not be
        /// created/run (the caller's fallback path). The task is always deleted.
        /// </summary>
        public static int? RunOneShotHighestPrivileges(string name, string description, string exePath, string? arguments)
        {
            using var ts = new TaskService();

            try
            {
                if (ts.RootFolder.AllTasks.Any(t => t.Name == name))
                    ts.RootFolder.DeleteTask(name);

                TaskDefinition td = ts.NewTask();

                // Highest, not LUA: this is the route for actions whose whole point
                // is the full admin token. Registration by the user who owns the
                // task needs no elevation; the elevation is granted at run time by
                // the Task Scheduler service.
                td.Principal.RunLevel = TaskRunLevel.Highest;
                td.RegistrationInfo.Description = description;
                td.Settings.DisallowStartIfOnBatteries = false;
                td.Settings.StopIfGoingOnBatteries = false;
                td.Settings.MultipleInstances = TaskInstancesPolicy.IgnoreNew;
                td.Settings.Enabled = true;

                // A trigger one second ahead: Task Scheduler fires it on time, which
                // is strictly more reliable than Run() racing the registration
                // commit (the field bug pattern for one-shot tasks is a Run that
                // silently lands before the task exists).
                td.Triggers.Add(new TimeTrigger(DateTime.Now + TimeSpan.FromSeconds(1)));
                td.Actions.Add(new ExecAction(exePath, arguments));

                ts.RootFolder.RegisterTaskDefinition(
                    name, td, Microsoft.Win32.TaskScheduler.TaskCreation.CreateOrUpdate,
                    System.Environment.UserDomainName + "\\" + System.Environment.UserName,
                    null, Microsoft.Win32.TaskScheduler.TaskLogonType.S4U, null);

                // The fire is asynchronous: watch it finish. A machine whose
                // scheduler is wedged costs the caller one fallback, not a hang.
                DateTime deadline = DateTime.Now + TimeSpan.FromSeconds(15);
                while (DateTime.Now < deadline)
                {
                    var task = ts.RootFolder.AllTasks.FirstOrDefault(t => t.Name == name);
                    if (task == null)
                        break;

                    if (task.State != TaskState.Running && task.State != TaskState.Queued)
                        break;

                    System.Threading.Thread.Sleep(250);
                }

                var finished = ts.RootFolder.AllTasks.FirstOrDefault(t => t.Name == name);
                int? result = finished?.LastTaskResult;
                return result;
            }
            catch (Exception ex)
            {
                Serilog.Log.Information("[ScheduledTask] One-shot task {Name} failed: {Message}", name, ex.Message);
                return null;
            }
            finally
            {
                try
                {
                    var task = ts.RootFolder.AllTasks.FirstOrDefault(t => t.Name == name);
                    if (task != null)
                        ts.RootFolder.DeleteTask(name);
                }
                catch
                {
                    // A task left behind by a wedged scheduler: the next one-shot
                    // registration for the same name deletes it first.
                }
            }
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
