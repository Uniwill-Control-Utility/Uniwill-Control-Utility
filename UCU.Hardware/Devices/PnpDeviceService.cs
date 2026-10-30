using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace UCU.Hardware.Devices
{
    /// <summary>
    /// Selects the device node a <c>device.*</c> verb actuates, mirroring exactly
    /// what the retired PowerShell commands targeted: the present device of a PnP
    /// class whose friendly name matches one of the patterns. The patterns are
    /// data because the field's machines name their camera "Webcam", "Camera" or
    /// the OEM variant; the class filter keeps a display adapter out of the
    /// webcam row's reach.
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
    /// PnP enable/disable through the Configuration Manager API (CfgMgr32). This is
    /// what <c>Enable-PnpDevice</c>/<c>Disable-PnpDevice</c> call internally; the
    /// app used to shell them out elevated, which meant a UAC prompt on every
    /// webcam/dGPU act - including on the AUTOMATIC GPU-power-saving applies, where
    /// consent is not an acceptable answer. The engine needs no prompt: it runs as
    /// LocalSystem, which holds <c>SeLoadDriverPrivilege</c> by definition.
    ///
    /// Status semantics mirror the retired PowerShell queries one-for-one, so the
    /// rows' verified behaviour is unchanged: "OK" is DF_RUNNING with no problem
    /// code; a disabled node is "Disable" (DF_DNE_0); present-but-not-running is
    /// "Unknown"; a devnode the system cannot open is "Unknown Device". Mutations
    /// act only on PRESENT devices (DF_PRESENT) whose state differs from the
    /// target, so already-in-state is a silent no-op, and the verification re-read
    /// answers the verb - the same verify-by-re-read the elevated path's callers
    /// did by hand.
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
                    uint code = enabled
                        ? Native.CM_Enable_DevNode(device.DevInst, 0)
                        : Native.CM_Disable_DevNode(device.DevInst, Native.DICS_DISABLE_GLOBAL_W, 0);

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
        /// Every devnode whose class equals the filter's and whose friendly name
        /// (falling back to the device description, the same value the retired
        /// Get-PnpDevice -FriendlyName matched on Camera/Display) matches one of
        /// the patterns.
        /// </summary>
        private static IEnumerable<MatchedDevice> EnumerateMatching(PnpDeviceFilter filter)
        {
            uint[] ids = GetDeviceIdList();

            foreach (uint devInst in ids)
            {
                string deviceClass = ReadStringProperty(devInst, Native.DEVPKEY_Device_Class);
                if (!string.Equals(deviceClass, filter.DeviceClass, System.StringComparison.OrdinalIgnoreCase))
                    continue;

                string name = ReadStringProperty(devInst, Native.DEVPKEY_Device_FriendlyName);
                if (string.IsNullOrEmpty(name))
                    name = ReadStringProperty(devInst, Native.DEVPKEY_Device_DeviceDesc);

                if (!filter.FriendlyNamePatterns.Any(p => MatchesWildcard(name, p)))
                    continue;

                string instanceId = GetDeviceId(devInst);
                if (instanceId.Length == 0)
                    continue;

                yield return Describe(devInst, instanceId);
            }
        }

        /// <summary>
        /// The PnP status string, in the retired query's vocabulary: present +
        /// running + no problem = OK; the DNE flag = Disable; present, not running,
        /// no DNE = Unknown; an unopenable node = Unknown Device.
        /// </summary>
        private static MatchedDevice Describe(uint devInst, string instanceId)
        {
            uint code = Native.CM_Get_DevNode_Status(out uint status, out uint problem, devInst, 0);
            if (code == Native.CR_NO_SUCH_DEVNODE)
                return new MatchedDevice(devInst, instanceId, "Unknown Device", Present: false, Running: false);

            if (code != Native.CR_SUCCESS)
                return new MatchedDevice(devInst, instanceId, "Unknown", Present: false, Running: false);

            bool present = (status & Native.DF_PRESENT) != 0;
            bool running = (status & Native.DF_RUNNING) != 0;
            bool disabled = (status & Native.DF_DNE_0) != 0;

            string text = disabled
                ? "Disable"
                : running && problem == 0
                    ? "OK"
                    : running
                        ? "Error"
                        : "Unknown";

            return new MatchedDevice(devInst, instanceId, text, present, running);
        }

        private static uint[] GetDeviceIdList()
        {
            uint listSize = 0;
            uint code = Native.CM_Get_Device_ID_List(
                null, System.Array.Empty<byte>(), ref listSize, Native.PROFILE_ALL_PROBLEMS);

            if (code == Native.CR_SUCCESS && listSize > 2)
            {
                byte[] buffer = new byte[listSize];
                code = Native.CM_Get_Device_ID_List(null, buffer, ref listSize, Native.PROFILE_ALL_PROBLEMS);
                if (code == Native.CR_SUCCESS)
                    return ParseDoubleZ(buffer);
            }

            Log.Information("[PnpDevice] CM_Get_Device_ID_List failed: 0x{Code:X8}", code);
            return System.Array.Empty<uint>();
        }

        /// <summary>The double-z-delimited id list, the same shape pnputil prints.</summary>
        private static uint[] ParseDoubleZ(byte[] buffer)
        {
            var ids = new System.Collections.Generic.List<uint>();
            int offset = 0;
            while (offset < buffer.Length - 2)
            {
                int end = offset;
                while (end < buffer.Length - 1 && (buffer[end] != 0 || buffer[end + 1] != 0))
                    end += 2;

                string id = System.Text.Encoding.Unicode.GetString(buffer, offset, end - offset);
                if (id.Length > 0)
                {
                    if (Native.CM_Locate_DevNode(out uint devInst, id, Native.CLDIGSR_FLAGS_NONE) == Native.CR_SUCCESS)
                        ids.Add(devInst);
                }

                offset = end + 4;
            }

            return ids.ToArray();
        }

        private static string GetDeviceId(uint devInst)
        {
            byte[] buffer = new byte[Native.MAX_DEVICE_ID_LEN * 2 + 2];
            if (Native.CM_Get_Device_ID(devInst, buffer, (uint)buffer.Length, 0) != Native.CR_SUCCESS)
                return string.Empty;

            return System.Text.Encoding.Unicode.GetString(buffer).Split('\0')[0];
        }

        private static string ReadStringProperty(uint devInst, Native.DEVPROPKEY key)
        {
            Native.DEVPROPKEY local = key; // the native call wants a mutable ref to the key

            uint type = 0;
            uint size = 0;
            uint code = Native.CM_Get_DevNode_Property(devInst, ref local, ref type, System.Array.Empty<byte>(), ref size, 0);
            if ((code != Native.CR_SUCCESS && code != Native.CR_BUFFER_SMALL) || size == 0 || size > 32 * 1024)
                return string.Empty;

            byte[] buffer = new byte[size];
            code = Native.CM_Get_DevNode_Property(devInst, ref local, ref type, buffer, ref size, 0);
            if (code != Native.CR_SUCCESS)
                return string.Empty;

            return System.Text.Encoding.Unicode.GetString(buffer).Split('\0')[0];
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
