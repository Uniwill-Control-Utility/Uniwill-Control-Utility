using Microsoft.Win32.TaskScheduler;
using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Uniwill_Control_Utility.Services
{
    /// <summary>
    /// Shared registration of the logon scheduled tasks ("UCU" autostart and
    /// "UCU Guardian" watchdog). Both run with highest privileges so they can
    /// launch the requireAdministrator app without a UAC prompt.
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
        /// Action executable; defaults to the main app exe. The guardian task
        /// points at Uniwill-Control-Utility-Guardian.exe instead.
        /// </param>
        public static void Register(string name, string description, string arguments, TimeSpan delay, string? exePath = null)
        {
            using var ts = new TaskService();

            // Delete existing task so we can recreate with updated settings.
            if (ts.RootFolder.AllTasks.Any(t => t.Name == name))
                ts.RootFolder.DeleteTask(name);

            TaskDefinition td = ts.NewTask();
            td.Principal.RunLevel = TaskRunLevel.Highest;
            td.RegistrationInfo.Description = description;
            td.Settings.DisallowStartIfOnBatteries = false;
            td.Settings.StopIfGoingOnBatteries = false;
            td.Settings.DisallowStartOnRemoteAppSession = false;

            td.Triggers.Add(new LogonTrigger { Delay = delay });

            td.Actions.Add(new ExecAction(exePath ?? CurrentExePath, string.IsNullOrEmpty(arguments) ? null : arguments));

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
