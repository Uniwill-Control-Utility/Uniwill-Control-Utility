using UCU.Contracts;
using UCU.Core.Takeover;
using UCU.Hardware.Takeover;

namespace UCU.Engine
{
    /// <summary>
    /// OEM takeover verbs (story U-203): the machine-scope pause. The
    /// privilege-boundary justification: HKLM run-key renames, service
    /// start-type changes, machine task disables and cross-session process
    /// kills are LocalSystem acts an unelevated app cannot perform (and an
    /// elevated prompt on an automatic path is a prompt the user cannot
    /// anticipate). The gate lives Core-side (UCU.Core.Takeover): the
    /// executor observes, the policy decides, this layer only maps records
    /// to the wire and names the state from the counts.
    ///
    /// These verbs run on the default domain queue (the unknown-prefix
    /// fallback): pause and restore sequence against each other and against
    /// nothing on the EC, and a wedged queue is exactly when the app must
    /// NOT be starting and killing processes at a second, unsynchronized
    /// pace.
    /// </summary>
    internal static class OemVerbs
    {
        public static void Register(Action<string, VerbHandler> add, IOemTakeoverService oem)
        {
            add(EngineVerbs.Oem.GetStatus, (p, ct) =>
                Task.FromResult<object?>(ToStatusDto(oem.GetStatus())));

            add(EngineVerbs.Oem.Pause, (p, ct) =>
                Task.FromResult<object?>(ToActionDto(oem.Pause())));

            add(EngineVerbs.Oem.Resume, (p, ct) =>
                Task.FromResult<object?>(ToActionDto(oem.Resume())));
        }

        private static OemStatusDto ToStatusDto(OemMachineStatus status) => new()
        {
            State = TakeoverPolicy.DecideState(new TakeoverPolicy.Inventory
            {
                ServicesInstalled = status.ServicesInstalled,
                ServicesRunning = status.ServicesRunning,
                OemProcessesRunning = status.OemProcessesRunning,
                JournalCount = status.JournalCount,
                AutostartNeutralized = status.AutostartNeutralized,
            }).ToString(),
            ServicesInstalled = status.ServicesInstalled,
            ServicesRunning = status.ServicesRunning,
            OemProcessesRunning = status.OemProcessesRunning,
            JournalCount = status.JournalCount,
            AutostartNeutralized = status.AutostartNeutralized,
            SquatSuspects = new List<string>(status.SquatSuspects),
            NeutralizedTargets = new List<string>(status.NeutralizedTargets),
        };

        private static OemActionDto ToActionDto(OemActionResult result)
        {
            OemStatusDto after = ToStatusDto(result.StatusAfter);
            return new OemActionDto
            {
                State = after.State,
                MutationsApplied = result.MutationsApplied,
                SquatSuspects = result.StatusAfter.SquatSuspects.Count,
                Actions = new List<string>(result.Actions),
            };
        }
    }
}
