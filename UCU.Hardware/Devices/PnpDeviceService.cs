using System.Management;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace UCU.Hardware.Devices
{
    /// <summary>
    /// Selects the device node a <c>device.*</c> verb actuates, mirroring exactly
    /// what the retired PowerShell commands targeted: the present device of a PnP
    /// class whose friendly name (the Win32_PnPEntity Name, the string
    /// Get-PnpDevice -FriendlyName matched) matches one of the patterns. The
    /// patterns are data because the field's machines name their camera
    /// "Webcam", "Camera" or the OEM variant; the class filter keeps a display
    /// adapter out of the webcam row's reach.
    /// </summary>
    public sealed record PnpDeviceFilter
    {
        public string DeviceClass { get; init; } = string.Empty;

        /// <summary>Case-insensitive wildcard patterns for the friendly name.</summary>
        public string[] FriendlyNamePatterns { get; init; } = System.Array.Empty<string>();
    }

    /// <summary>
    /// One device node's PnP state, in the retired Get-PnpDevice vocabulary
    /// ("OK", "Disable", "Error", "Unknown", "Unknown Device"). Local to this
    /// assembly: the contract DTO (PnpDeviceStateDto) is the engine's mapping
    /// target, and the contract assembly deliberately references no hardware.
    /// </summary>
    public sealed record PnpDeviceState
    {
        public string InstanceId { get; init; } = string.Empty;

        public string Status { get; init; } = string.Empty;

        public bool Present { get; init; }
    }

    /// <summary>
    /// The seam the engine's device verbs run on: the production implementation
    /// talks to the Configuration Manager API as LocalSystem; the RPC tests inject a
    /// scripted device tree, because enabling a real camera in a test process is
    /// exactly what no test may do.
    /// </summary>
    public interface IPnpDeviceService
    {
        Task<PnpDeviceState?> GetStateAsync(PnpDeviceFilter filter, CancellationToken cancellationToken = default);

        /// <summary>True when the mutation ran and the verification re-read sees
        /// the requested state.</summary>
        Task<bool> SetEnabledAsync(PnpDeviceFilter filter, bool enabled, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// PnP enable/disable. Device identity resolves through WMI
    /// <c>Win32_PnPEntity</c> - the rows <c>Get-PnpDevice</c> rides, which the
    /// app's elevated fallback queried by name on every field machine and
    /// always found - and the mutation runs on the Configuration Manager
    /// per-node calls (<c>CM_Enable_DevNode</c>/<c>CM_Disable_DevNode</c>, what
    /// <c>Enable-PnpDevice</c>/<c>Disable-PnpDevice</c> call internally). The
    /// app used to shell those out elevated, which meant a UAC prompt on every
    /// webcam/dGPU act - including on the AUTOMATIC GPU-power-saving applies,
    /// where consent is not an acceptable answer. The engine needs no prompt:
    /// it runs as LocalSystem, which holds <c>SeLoadDriverPrivilege</c> by
    /// definition.
    ///
    /// Why identity is WMI and not the CM enumerator: on the field's boxes
    /// (guarded dev box, 2026-10-30) every CM_Get_Device_ID_List call - W and
    /// A, every filter, every documented PMCL flag set - answers CR_FAIL or
    /// CR_NO_MORE_HANDLES, and the FriendlyName/Class devprops answer
    /// CR_NO_SUCH_VALUE even on nodes whose status and mutation calls work.
    /// A service built on that enumerator sees an empty machine: the webcam
    /// row reported "already disabled" and never touched the devnode. The
    /// Win32_PnPEntity rows, SetupAPI, and the CM per-node calls all work on
    /// the same box; this service runs on exactly those.
    ///
    /// Status semantics mirror the retired PowerShell queries' status strings,
    /// derived from the devnode's problem code: 0 = "OK" (running), 
    /// CM_PROB_DISABLED = "Disable", CM_PROB_PHANTOM = not present ("Unknown"),
    /// any other problem = "Error"; a devnode the system cannot open is
    /// "Unknown Device". (The legacy DF_PRESENT/DF_RUNNING/DF_DNE_0 status
    /// bits that the mapping was originally written on are dead on modern
    /// Windows - see Describe.) Mutations act only on present devices whose
    /// running state differs from the target, so already-in-state is a silent
    /// no-op, and the verification re-read answers the verb - the same
    /// verify-by-re-read the elevated path's callers did by hand.
    /// </summary>
    public sealed class PnpDeviceService : IPnpDeviceService
    {
        public async Task<PnpDeviceState?> GetStateAsync(PnpDeviceFilter filter, CancellationToken cancellationToken = default)
        {
            // The CM calls are synchronous and quick (device-node property reads),
            // so the task wrapper exists for the seam's async contract, and the
            // call is off-threaded to keep the domain queue's thread free.
            return await Task.Run(() =>
            {
                foreach (MatchedDevice device in EnumerateMatching(filter))
                {
                    if (device.Present)
                        return ToState(device);
                }

                return null;
            }, cancellationToken).ConfigureAwait(false);
        }

        public async Task<bool> SetEnabledAsync(PnpDeviceFilter filter, bool enabled, CancellationToken cancellationToken = default)
        {
            return await Task.Run(() =>
            {
                MatchedDevice[] targets = EnumerateMatching(filter)
                    .Where(d => d.Present && d.Running != enabled)
                    .ToArray();

                if (targets.Length == 0)
                {
                    // "Already in state" is success, matching the retired command's
                    // no-op filter; a device that is not present at all is not.
                    bool alreadyThere = EnumerateMatching(filter).Any(d => d.Present && d.Running == enabled);
                    Log.Information("[PnpDevice] {Class} already {State}, no mutation (present-matching found: {Found})",
                        filter.DeviceClass, enabled ? "enabled" : "disabled", alreadyThere);
                    return alreadyThere;
                }

                foreach (MatchedDevice device in targets)
                {
                    // The probe-verified shape: ulDisableFlags stays 0
                    // (DICS_DISABLE_GLOBAL_W is CR_FAIL on the field boxes,
                    // see Native.DICS_FLAG_GLOBAL) and the mutation goes to
                    // the base hardware store.
                    uint code = enabled
                        ? Native.CM_Enable_DevNode(device.DevInst, 0)
                        : Native.CM_Disable_DevNode(device.DevInst, 0, Native.DICS_FLAG_GLOBAL);

                    Log.Information("[PnpDevice] {Action} {Instance} returned {Code}",
                        enabled ? "enable" : "disable", device.InstanceId, $"0x{code:X8}");
                }

                return Verify(filter, enabled, cancellationToken);
            }, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// The PnP restart is asynchronous: a devnode reports the new state a beat
        /// after the CM call returns. Poll briefly, then report the truth; the
        /// caller logs the status string either way, and the field's question is
        /// always "which status did it end in".
        /// </summary>
        private bool Verify(PnpDeviceFilter filter, bool enabled, CancellationToken cancellationToken)
        {
            for (int attempt = 0; attempt < 8; attempt++)
            {
                MatchedDevice? present = EnumerateMatching(filter).FirstOrDefault(d => d.Present);
                if (present != null && present.Running == enabled)
                {
                    Log.Information("[PnpDevice] {Class} verified {State} (status {Status}, instance {Instance})",
                        filter.DeviceClass, enabled ? "enabled" : "disabled", present.Status, present.InstanceId);
                    return true;
                }

                cancellationToken.ThrowIfCancellationRequested();
                Thread.Sleep(500);
            }

            MatchedDevice? last = EnumerateMatching(filter).FirstOrDefault(d => d.Present);
            Log.Information("[PnpDevice] {Class} not verified as {State}: last status {Status}",
                filter.DeviceClass, enabled ? "enabled" : "disabled", last?.Status ?? "(no present device)");
            return false;
        }

        private sealed record MatchedDevice(
            uint DevInst,
            string InstanceId,
            string Status,
            bool Present,
            bool Running)
        {
            public PnpDeviceState ToState() => new()
            {
                InstanceId = InstanceId,
                Status = Status,
                Present = Present,
            };
        }

        private static PnpDeviceState ToState(MatchedDevice device) => device.ToState();

        /// <summary>
        /// Every device whose PnP class equals the filter's and whose friendly
        /// name (the Win32_PnPEntity Name, exactly the string the retired
        /// Get-PnpDevice -FriendlyName matched on Camera/Display) matches one
        /// of the patterns. The DeviceClass values are code constants from the
        /// verb layer ("Camera", "Display"), so the WQL string interpolation
        /// carries no user data.
        /// </summary>
        private static IEnumerable<MatchedDevice> EnumerateMatching(PnpDeviceFilter filter)
        {
            using var searcher = new ManagementObjectSearcher(
                "root\\cimv2",
                $"SELECT Name, DeviceID FROM Win32_PnPEntity WHERE PNPClass = '{filter.DeviceClass}'");

            ManagementObjectCollection rows;
            try
            {
                rows = searcher.Get();
            }
            catch (System.Exception ex)
            {
                // The witness shape: a WMI failure reads here as the empty
                // machine it is; the verb answers verified=false and the
                // reason is on the log, never a silent no-op.
                Log.Information("[PnpDevice] WMI enumeration of class {Class} failed: {Message}",
                    filter.DeviceClass, ex.Message);
                yield break;
            }

            using (rows)
            {
                foreach (ManagementObject row in rows)
                {
                    using (row)
                    {
                        string name = row["Name"] as string ?? string.Empty;
                        if (!filter.FriendlyNamePatterns.Any(p => MatchesWildcard(name, p)))
                            continue;

                        string instanceId = row["DeviceID"] as string ?? string.Empty;
                        if (instanceId.Length == 0)
                            continue;

                        yield return Describe(instanceId);
                    }
                }
            }
        }

        /// <summary>
        /// The PnP status string, in the retired query's vocabulary, derived
        /// from the problem code. Field evidence (guarded dev box, 2026-10-30)
        /// says the legacy DF_PRESENT/DF_RUNNING/DF_DNE_0 status BITS do not
        /// reflect state on current Windows: a running, enabled dGPU and camera
        /// answer 0x0180600A - no DF_PRESENT, no DF_RUNNING, the DNE bit set.
        /// The problem code is the live signal, and disabling/re-enabling the
        /// webcam on that box verified its mapping one-for-one with the
        /// Get-PnpDevice status the rows used to consume: 0 = OK,
        /// CM_PROB_DISABLED (22) = "Disable", CM_PROB_PHANTOM (45) = not
        /// present ("Unknown"), any other problem = "Error".
        /// </summary>
        private static MatchedDevice Describe(string instanceId)
        {
            uint locate = Native.CM_Locate_DevNode(out uint devInst, instanceId, 0);
            if (locate != Native.CR_SUCCESS)
                return new MatchedDevice(devInst, instanceId, "Unknown Device", Present: false, Running: false);

            uint code = Native.CM_Get_DevNode_Status(out uint status, out uint problem, devInst, 0);
            if (code == Native.CR_NO_SUCH_DEVNODE)
                return new MatchedDevice(devInst, instanceId, "Unknown Device", Present: false, Running: false);

            if (code != Native.CR_SUCCESS)
                return new MatchedDevice(devInst, instanceId, "Unknown", Present: false, Running: false);

            // The status flags carry no trustworthy presence/run bit on this
            // Windows (see the doc); the problem code is the witness.
            _ = status;

            bool present = problem != Native.CM_PROB_PHANTOM;
            bool running = problem == 0;

            string text = problem switch
            {
                0 => "OK",
                Native.CM_PROB_DISABLED => "Disable",
                Native.CM_PROB_PHANTOM => "Unknown",
                _ => "Error",
            };

            return new MatchedDevice(devInst, instanceId, text, present, running);
        }

        /// <summary>Case-insensitive wildcard match over '*' only - the retired
        /// PowerShell -FriendlyName patterns used exactly this shape (*Webcam*,
        /// *Camera*, *NVIDIA*).</summary>
        internal static bool MatchesWildcard(string value, string pattern)
        {
            string[] pieces = pattern.Split('*');
            int at = 0;

            for (int i = 0; i < pieces.Length; i++)
            {
                string piece = pieces[i];
                if (piece.Length == 0)
                    continue;

                int found = value.IndexOf(piece, at, System.StringComparison.OrdinalIgnoreCase);
                if (found < 0)
                    return false;

                at = found + piece.Length;
            }

            // A pattern not ending in '*' demands the tail piece at the end.
            if (pieces.Length > 0 && pieces[^1].Length > 0 && !pattern.EndsWith('*'))
                return value.EndsWith(pieces[^1], System.StringComparison.OrdinalIgnoreCase);

            return true;
        }
    }
}
