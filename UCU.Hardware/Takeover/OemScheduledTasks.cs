using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Win32.TaskScheduler;
using Task = Microsoft.Win32.TaskScheduler.Task;

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
    /// Microsoft.Win32.TaskScheduler wrapper (the app's existing TaskScheduler
    /// package; the dependency rides the LocalSystem side of the boundary
    /// for the same reason System.Management rides it for the MiniLED row:
    /// an unelevated app cannot toggle machine tasks' enable state, which
    /// is exactly why this scope is engine-side).
    ///
    /// The wrapper, not raw dynamic COM: the raw binding is a field-proven
    /// dead end - casting the Schedule.Service collection to
    /// IEnumerable&lt;dynamic&gt; answers E_NOINTERFACE (0x80004002) because
    /// the collection's enumeration lives on IDispatch _NewEnum, which only
    /// the PowerShell adapter speaks; the first GetStatus on the staged
    /// engine faulted exactly there (pack #22, dev-box probe).
    /// </summary>
    public sealed class OemScheduledTasks : IDisposable
    {
        private readonly TaskService _service = new();

        public void Dispose() => _service.Dispose();

        /// <summary>Every root-folder task (the vocabulary's tasks are all
        /// registered in the root; the reference's scope too). A task whose
        /// definition cannot be read degrades to "no paths", which the gate
        /// reads as a refusal - a task whose actions cannot be inspected is
        /// never touched.</summary>
        public IEnumerable<OemTaskObservation> EnumerateRootTasks()
        {
            foreach (Task task in _service.RootFolder.Tasks)
            {
                string name;
                bool enabled;
                IReadOnlyList<string> paths;
                try
                {
                    name = task.Name;
                    enabled = task.Enabled;
                    paths = task.Definition.Actions
                        .OfType<ExecAction>()
                        .Select(action => action.Path)
                        .Where(path => !string.IsNullOrWhiteSpace(path))
                        .ToList();
                }
                catch (Exception)
                {
                    // A task we cannot even name is not ours to touch; a
                    // definition read failure leaves it (the gate refuses
                    // tasks with no Exec paths).
                    if (string.IsNullOrEmpty(task.Name))
                        continue;

                    name = task.Name;
                    enabled = false;
                    paths = Array.Empty<string>();
                }

                yield return new OemTaskObservation
                {
                    Name = name,
                    Enabled = enabled,
                    ActionPaths = paths,
                };
            }
        }

        /// <summary>Sets the task's Enabled flag and answers the readback,
        /// so the journal records the observed state, never an assumption.
        /// Null = the task is gone or the write was refused.</summary>
        public bool? SetEnabled(string taskName, bool enabled)
        {
            try
            {
                Task task = _service.RootFolder.Tasks[taskName];
                task.Enabled = enabled;
                return task.Enabled;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
