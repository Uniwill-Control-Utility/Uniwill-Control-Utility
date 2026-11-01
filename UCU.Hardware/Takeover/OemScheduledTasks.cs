using System;
using System.Collections.Generic;

namespace UCU.Hardware.Takeover
{
    /// <summary>One root-folder task as observed for the takeover gate.
    /// <see cref="ActionPaths"/> is every Exec action's program path: the
    /// task's executable identity, which the publisher gate inspects.</summary>
    public sealed record OemTaskObservation
    {
        public string Name { get; init; } = string.Empty;

        public bool Enabled { get; init; }

        public IReadOnlyList<string> ActionPaths { get; init; } = Array.Empty<string>();
    }

    /// <summary>
    /// The Task Scheduler scope for the takeover (story U-203): root-folder
    /// enumeration, Exec-action paths, and the Enabled toggle - the
    /// reference's twelve-task set (sandbox.rs) driven through the
    /// Schedule.Service COM object.
    ///
    /// Late-bound (dynamic) on purpose: the Task Scheduler 2.0 API is a COM
    /// dual/dispatch surface, so no interop declarations and no extra
    /// package are needed - the same binding PowerShell's Get-ScheduledTask
    /// stack rides. The service runs as LocalSystem, which owns machine
    /// tasks' enable state; an unelevated app cannot toggle them, which is
    /// exactly why this scope is engine-side.
    /// </summary>
    public sealed class OemScheduledTasks
    {
        private readonly dynamic _root;

        public OemScheduledTasks()
        {
            Type? schedulerType = Type.GetTypeFromProgID("Schedule.Service");
            if (schedulerType is null)
                throw new InvalidOperationException("the Task Scheduler COM server (Schedule.Service) is not registered");

            dynamic scheduler = Activator.CreateInstance(schedulerType)
                ?? throw new InvalidOperationException("the Task Scheduler COM server could not be created");

            scheduler.Connect();
            _root = scheduler.GetFolder("\\");
        }

        /// <summary>Every root-folder task (the vocabulary's tasks are all
        /// registered in the root; the reference's scope too). Unreadable
        /// fields degrade to "no paths / disabled-unknown", which the gate
        /// reads as a refusal - a task whose actions cannot be inspected is
        /// never touched.</summary>
        public IEnumerable<OemTaskObservation> EnumerateRootTasks()
        {
            foreach (dynamic task in (IEnumerable<dynamic>)_root.GetTasks(0))
            {
                string name;
                bool enabled;
                try
                {
                    name = (string)task.Name;
                    enabled = (bool)task.Enabled;
                }
                catch (Exception)
                {
                    continue; // a task we cannot even name is not ours to touch
                }

                yield return new OemTaskObservation
                {
                    Name = name,
                    Enabled = enabled,
                    ActionPaths = ReadActionPaths(task),
                };
            }
        }

        /// <summary>Sets the task's Enabled flag (the COM put_enabled, which
        /// the service persists) and answers the readback, so the journal
        /// records the observed state, never an assumption. Null = the task
        /// is gone or the write was refused.</summary>
        public bool? SetEnabled(string taskName, bool enabled)
        {
            try
            {
                dynamic task = _root.GetTask(taskName);
                task.Enabled = enabled;
                return (bool)task.Enabled;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static IReadOnlyList<string> ReadActionPaths(dynamic task)
        {
            var paths = new List<string>();
            try
            {
                dynamic definition = task.Definition;
                dynamic actions = definition.Actions;
                long count = actions.Count;
                for (long i = 1; i <= count; i++) // the COM collection is 1-based
                {
                    try
                    {
                        dynamic action = actions.Item(i);
                        // Only Exec actions carry a Path; COM throws
                        // disp-E UnknownName for the other action types.
                        string path = (string)action.Path;
                        if (!string.IsNullOrWhiteSpace(path))
                            paths.Add(path);
                    }
                    catch (Exception)
                    {
                        // Not an Exec action (Script/Email/...) - no executable
                        // identity to gate; the task stays untouched (the
                        // gate refuses tasks without a verified Exec path).
                    }
                }
            }
            catch (Exception)
            {
                // Definition unreadable: the caller sees zero paths and the
                // gate refuses the task.
            }

            return paths;
        }
    }
}
