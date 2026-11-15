using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Serilog;
using UCU.Contracts;
using UCU.Hardware.Devices;

namespace UCU.Hardware.Gpu
{
    /// <summary>
    /// The driver-key side of the GPU ROM override store. Split from the store so the
    /// record logic (encode/decode, ownership, tier→value) runs in a test against a
    /// fake key: only this implementation touches HKLM.
    /// </summary>
    public interface IGpuRomRegistry
    {
        /// <summary>The present NVIDIA adapter's PnP instance id (the restart target), or null.</summary>
        string? InstanceId { get; }

        /// <summary>The adapter's VBIOS version as the records spell it ("98.03.5E.00.5D"), or null.</summary>
        string? ReadVbiosVersion();

        /// <summary>
        /// Every <c>romOverride*</c> / override-shaped value in the driver key, name →
        /// raw data. A value of another type arrives as empty data, which the store
        /// reads as malformed rather than as ours.
        /// </summary>
        IReadOnlyDictionary<string, byte[]> ReadRecords();

        void WriteRecord(string name, byte[] data);

        void DeleteRecord(string name);

        /// <summary>RegFlushKey: the write must be on disk before the GPU restarts.</summary>
        void Flush();
    }

    /// <summary>Restarts the display adapter so the driver re-reads the ROM records.</summary>
    public interface IGpuRestarter
    {
        /// <summary>True when the device restarted and the driver is reachable again.</summary>
        bool Restart(string? instanceId);
    }

    /// <summary>
    /// The driver's enforced board-power ceiling (mW), for "did the unlock land".
    /// Null when the driver answers nothing, which is reported as unknown, never
    /// as "not moved".
    /// </summary>
    public interface IGpuPowerCeilingProbe
    {
        int? ReadEnforcedLimitMilliwatts();
    }

    /// <summary>The two gpu.* ROM verbs' target.</summary>
    public interface IGpuRomOverrideService
    {
        GpuRomOverrideStateDto GetState();

        /// <summary>true stages the tier's records and restarts the GPU; false restores stock.</summary>
        GpuRomOverrideStateDto SetEnabled(bool enabled);
    }

    /// <summary>
    /// The ROM override store for the pinned VBIOS: stage the tier's seven records on
    /// the GPU's display-driver key, restart the GPU, and restore by deleting them.
    ///
    /// Safety rules, all from the measured reference (MechControl-RE nvidia_store.cpp):
    /// the originals file is written (tmp + flush + move) BEFORE the first registry
    /// write so a half-written key is always recoverable; records are read back and
    /// must equal what was written; a key that already carries someone else's override
    /// (a 'VBIOS' marker, an unknown value name, a record from another VBIOS or a
    /// malformed one) is refused untouched; and every failure after the first write is
    /// reported as Uncertain, because the key state is then no longer ours to assume.
    /// </summary>
    public sealed class GpuRomOverrideStore : IGpuRomOverrideService
    {
        private readonly IGpuRomRegistry _registry;
        private readonly IGpuRestarter _restarter;
        private readonly IGpuPowerCeilingProbe _ceiling;
        private readonly GpuPowerTier _tier;
        private readonly string _originalsPath;

        public GpuRomOverrideStore(
            IGpuRomRegistry registry,
            IGpuRestarter restarter,
            IGpuPowerCeilingProbe ceiling,
            string? originalsPath = null,
            GpuPowerTier? tier = null)
        {
            _registry = registry;
            _restarter = restarter;
            _ceiling = ceiling;
            _tier = tier ?? GpuPowerTiers.ExtraGpuPower;
            _originalsPath = originalsPath ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "UCU",
                "gpu-rom-overrides",
                "originals.txt");
        }

        public GpuRomOverrideStateDto GetState()
        {
            int? ceiling = _ceiling.ReadEnforcedLimitMilliwatts();

            try
            {
                string? version = _registry.ReadVbiosVersion();
                if (version == null)
                    return State(GpuRomOverrideStates.Unsupported, "the display driver key carries no VBIOS version", null, ceiling);

                if (!RomOverrideLayout.IsPinned(version))
                    return State(GpuRomOverrideStates.Unsupported, UnpinnedReason(version), version, ceiling);

                IReadOnlyDictionary<string, byte[]> records = _registry.ReadRecords();
                if (records.Count == 0)
                    return State(GpuRomOverrideStates.NotUnlocked, null, version, ceiling);

                if (!TryReadOriginals(out Originals? originals, out string? fileError))
                    return State(GpuRomOverrideStates.Unsupported, fileError, version, ceiling);

                if (ForeignRecordReason(records, originals) is string foreign)
                    return State(GpuRomOverrideStates.Unsupported, foreign, version, ceiling);

                return State(GpuRomOverrideStates.Unlocked, null, version, ceiling);
            }
            catch (Exception ex)
            {
                Log.Information("gpu.get_rom_override_state: {Type} {Message}", ex.GetType().Name, ex.Message);
                return State(GpuRomOverrideStates.Uncertain, ex.Message, null, ceiling);
            }
        }

        public GpuRomOverrideStateDto SetEnabled(bool enabled) => enabled ? Apply() : Restore();

        private GpuRomOverrideStateDto Apply()
        {
            int? ceiling = null;
            string? version = null;

            try
            {
                version = _registry.ReadVbiosVersion();
                if (version == null)
                    return State(GpuRomOverrideStates.Unsupported, "the display driver key carries no VBIOS version", null, null);

                if (!RomOverrideLayout.IsPinned(version))
                    return State(GpuRomOverrideStates.Unsupported, UnpinnedReason(version), version, null);

                IReadOnlyDictionary<string, byte[]> records = _registry.ReadRecords();
                if (!TryReadOriginals(out Originals? originals, out string? fileError))
                    return State(GpuRomOverrideStates.Unsupported, fileError, version, null);

                if (ForeignRecordReason(records, originals) is string foreign)
                    return State(GpuRomOverrideStates.Unsupported, foreign, version, null);

                IReadOnlyList<(string Name, byte[] Data)> desired = RomOverrideLayout.BuildRecords(_tier, version);

                if (Matches(records, desired))
                {
                    // Idempotent: the key already carries exactly our tier.
                    ceiling = _ceiling.ReadEnforcedLimitMilliwatts();
                    Log.Information("gpu.set_rom_override: {Tier} records already staged on {Version}", _tier.Id, version);
                    return State(GpuRomOverrideStates.Unlocked, "the records were already staged; the GPU was not restarted", version, ceiling, restarted: false);
                }

                if (originals == null)
                    WriteOriginals(version);

                foreach ((string name, byte[] data) in desired)
                    _registry.WriteRecord(name, data);

                _registry.Flush();

                IReadOnlyDictionary<string, byte[]> readback = _registry.ReadRecords();
                foreach ((string name, byte[] data) in desired)
                {
                    if (!readback.TryGetValue(name, out byte[]? written) || !written.AsSpan().SequenceEqual(data))
                        return State(GpuRomOverrideStates.Uncertain, $"the '{name}' record did not read back as written", version, null);
                }

                bool restarted = _restarter.Restart(_registry.InstanceId);
                ceiling = _ceiling.ReadEnforcedLimitMilliwatts();

                Log.Information(
                    "gpu.set_rom_override: {Tier} staged on {Version} ({Records} records), GPU restart {Restart}, enforced ceiling {Ceiling} mW",
                    _tier.Id, version, desired.Count, restarted ? "yes" : "no", ceiling?.ToString(CultureInfo.InvariantCulture) ?? "unknown");

                if (ceiling is int limit && limit <= RomOverrideLayout.StockPowerMilliwatts)
                {
                    return State(
                        GpuRomOverrideStates.Uncertain,
                        $"the records are staged but the driver still enforces {limit} mW; a reboot may be required",
                        version, ceiling, restarted);
                }

                return State(GpuRomOverrideStates.Unlocked, null, version, ceiling, restarted);
            }
            catch (Exception ex)
            {
                // Past the first registry write the key state is unknown: say so.
                Log.Information("gpu.set_rom_override: {Type} {Message}", ex.GetType().Name, ex.Message);
                return State(GpuRomOverrideStates.Uncertain, ex.Message, version, ceiling);
            }
        }

        private GpuRomOverrideStateDto Restore()
        {
            string? version = null;

            try
            {
                version = _registry.ReadVbiosVersion();
                IReadOnlyDictionary<string, byte[]> records = _registry.ReadRecords();

                if (!TryReadOriginals(out Originals? originals, out string? fileError))
                    return State(GpuRomOverrideStates.Unsupported, fileError, version, _ceiling.ReadEnforcedLimitMilliwatts());

                if (records.Count == 0)
                {
                    // Nothing staged: a no-op, and a stale originals file is dropped so the
                    // next apply writes fresh stock values.
                    if (originals != null)
                    {
                        DiscardOriginals();
                        return State(GpuRomOverrideStates.NotUnlocked, "no overrides were staged; the stale originals file was removed", version, _ceiling.ReadEnforcedLimitMilliwatts());
                    }

                    return State(GpuRomOverrideStates.NotUnlocked, null, version, _ceiling.ReadEnforcedLimitMilliwatts());
                }

                if (ForeignRecordReason(records, originals) is string foreign)
                    return State(GpuRomOverrideStates.Unsupported, foreign, version, _ceiling.ReadEnforcedLimitMilliwatts());

                foreach (string name in records.Keys)
                    _registry.DeleteRecord(name);

                _registry.Flush();

                IReadOnlyDictionary<string, byte[]> readback = _registry.ReadRecords();
                if (readback.Count != 0)
                    return State(GpuRomOverrideStates.Uncertain, "not every override record disappeared from the driver key", version, _ceiling.ReadEnforcedLimitMilliwatts());

                bool restarted = _restarter.Restart(_registry.InstanceId);
                int? ceiling = _ceiling.ReadEnforcedLimitMilliwatts();

                Log.Information(
                    "gpu.set_rom_override: stock restored on {Version}, GPU restart {Restart}, enforced ceiling {Ceiling} mW",
                    version, restarted ? "yes" : "no", ceiling?.ToString(CultureInfo.InvariantCulture) ?? "unknown");

                if (ceiling is int limit && limit > RomOverrideLayout.StockPowerMilliwatts)
                {
                    return State(
                        GpuRomOverrideStates.Uncertain,
                        $"the records are gone but the driver still enforces {limit} mW; a reboot may be required",
                        version, ceiling, restarted);
                }

                DiscardOriginals();
                return State(GpuRomOverrideStates.NotUnlocked, null, version, ceiling, restarted);
            }
            catch (Exception ex)
            {
                Log.Information("gpu.set_rom_override: {Type} {Message}", ex.GetType().Name, ex.Message);
                return State(GpuRomOverrideStates.Uncertain, ex.Message, version, null);
            }
        }

        private GpuRomOverrideStateDto State(
            string state, string? reason, string? version, int? ceiling, bool restarted = false) => new()
        {
            State = state,
            Reason = reason,
            VbiosVersion = version,
            TierId = _tier.Id,
            PowerMilliwatts = _tier.PowerMilliwatts,
            NvVddMilliAmps = _tier.NvVddMilliAmps,
            MsVddMilliAmps = _tier.MsVddMilliAmps,
            BoardInputMilliAmps = _tier.BoardInputMilliAmps,
            EnforcedLimitMilliwatts = ceiling,
            EnforcedLimitMoved = ceiling is int limit && limit > RomOverrideLayout.StockPowerMilliwatts,
            Restarted = restarted,
        };

        private static string UnpinnedReason(string version) =>
            $"VBIOS {version} is not pinned; the ROM offsets are only known for {RomOverrideLayout.PinnedVbiosVersion}";

        /// <summary>
        /// Why the key carries a record we do not own, or null when every record is ours.
        /// Mirrors the reference's require_owned: a 'VBIOS' marker, an unknown value
        /// name, a record with a different version prefix or offset, or a malformed one
        /// all mean another writer owns this key.
        /// </summary>
        private static string? ForeignRecordReason(IReadOnlyDictionary<string, byte[]> records, Originals? originals)
        {
            foreach ((string name, byte[] data) in records)
            {
                if (string.Equals(name, "VBIOS", StringComparison.OrdinalIgnoreCase))
                    return "another tool left a 'VBIOS' override marker in the GPU driver key; restore it with its owning tool and restart the GPU first";

                if (name.Length != 13 ||
                    !name.StartsWith("romOverride", StringComparison.OrdinalIgnoreCase) ||
                    !Uri.IsHexDigit(name[11]) ||
                    !Uri.IsHexDigit(name[12]))
                {
                    return $"the GPU driver key carries the unexpected override value '{name}'; it was not written by UCU";
                }

                if (originals == null)
                    return $"the GPU driver key already carries '{name}' and no UCU originals file owns it; restore it with its owning tool first";

                if (!RomOverrideRecord.TryDecode(data, out string version, out uint offset, out _))
                    return $"'{name}' is malformed ({(data.Length == RomOverrideRecord.Length ? $"patch length {data[9]}" : $"{data.Length} bytes")})";

                if (!string.Equals(version, originals.Version, StringComparison.Ordinal) || !originals.Values.ContainsKey(offset))
                    return $"'{name}' belongs to another VBIOS or field ({version} at 0x{offset:X}); UCU will not take the key over";
            }

            return null;
        }

        private static bool Matches(IReadOnlyDictionary<string, byte[]> records, IReadOnlyList<(string Name, byte[] Data)> desired)
        {
            if (records.Count != desired.Count)
                return false;

            foreach ((string name, byte[] data) in desired)
            {
                if (!records.TryGetValue(name, out byte[]? current) || !current.AsSpan().SequenceEqual(data))
                    return false;
            }

            return true;
        }

        private bool TryReadOriginals(out Originals? originals, out string? error)
        {
            originals = null;
            error = null;

            if (!File.Exists(_originalsPath))
                return true;

            string[] lines = File.ReadAllLines(_originalsPath);
            if (lines.Length == 0)
            {
                error = "the UCU GPU originals file is empty";
                return false;
            }

            string version = lines[0].Trim();
            if (RomOverrideLayout.NormalizeVbiosVersion(version) != version)
            {
                error = "the UCU GPU originals file does not start with a VBIOS version";
                return false;
            }

            var values = new Dictionary<uint, uint>();
            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0)
                    continue;

                int equal = line.IndexOf('=');
                if (equal <= 0 ||
                    !uint.TryParse(line.AsSpan(0, equal), NumberStyles.None, CultureInfo.InvariantCulture, out uint offset) ||
                    !uint.TryParse(line.AsSpan(equal + 1), NumberStyles.None, CultureInfo.InvariantCulture, out uint value) ||
                    !values.TryAdd(offset, value))
                {
                    error = "the UCU GPU originals file is malformed";
                    return false;
                }
            }

            originals = new Originals(version, values);
            return true;
        }

        /// <summary>
        /// The stock values, written tmp → fsync → move BEFORE the first registry write:
        /// a torn key can always be traced back to the values the ROM had.
        /// </summary>
        private void WriteOriginals(string version)
        {
            string? directory = Path.GetDirectoryName(_originalsPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var text = new StringBuilder(version);
            foreach (RomOverrideField field in RomOverrideLayout.Fields)
            {
                text.Append('\n')
                    .Append(field.Offset.ToString(CultureInfo.InvariantCulture))
                    .Append('=')
                    .Append(field.StockValue.ToString(CultureInfo.InvariantCulture));
            }

            string temporary = _originalsPath + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                byte[] bytes = new UTF8Encoding(false).GetBytes(text.ToString());
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, _originalsPath, overwrite: true);
            Log.Information("gpu.set_rom_override: originals written to {Path}", _originalsPath);
        }

        private void DiscardOriginals()
        {
            if (File.Exists(_originalsPath))
                File.Delete(_originalsPath);
        }

        private sealed record Originals(string Version, Dictionary<uint, uint> Values);
    }

    /// <summary>
    /// The production registry: the present NVIDIA adapter's display-driver key,
    /// located the way the reference does it — the device's own <c>Driver</c> value in
    /// the Enum key names the class subkey, so a stale duplicate adapter key on the same
    /// machine is never written by mistake.
    /// </summary>
    public sealed class DisplayDriverRomRegistry : IGpuRomRegistry
    {
        internal const string DisplayClassKey =
            @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

        private static readonly PnpDeviceFilter NvidiaDisplay = new()
        {
            DeviceClass = "Display",
            FriendlyNamePatterns = new[] { "*NVIDIA*" },
        };

        private readonly IPnpDeviceService _devices;
        private Target? _target;

        public DisplayDriverRomRegistry(IPnpDeviceService devices) => _devices = devices;

        public string? InstanceId => ResolveTarget()?.InstanceId;

        public string? ReadVbiosVersion()
        {
            // The string is a VALUE on the driver key named "HardwareInformation.BiosString"
            // (REG_SZ "Version98.3.5e.0.5d"), not a value of a HardwareInformation subkey.
            using RegistryKey? key = OpenKey(writable: false);
            return RomOverrideLayout.NormalizeVbiosVersion(key?.GetValue("HardwareInformation.BiosString") as string);
        }

        public IReadOnlyDictionary<string, byte[]> ReadRecords()
        {
            var records = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

            using RegistryKey? key = OpenKey(writable: false);
            if (key == null)
                return records;

            foreach (string name in key.GetValueNames())
            {
                if (!name.StartsWith("romOverride", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(name, "VBIOS", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // A value of a non-binary type arrives as empty data: the store reads
                // that as malformed, which is the refusal we want.
                records[name] = key.GetValue(name) as byte[] ?? Array.Empty<byte>();
            }

            return records;
        }

        public void WriteRecord(string name, byte[] data)
        {
            using RegistryKey? key = OpenKey(writable: true);
            if (key == null)
                throw new InvalidOperationException("the NVIDIA display driver key is not writable");

            key.SetValue(name, data, RegistryValueKind.Binary);
        }

        public void DeleteRecord(string name)
        {
            using RegistryKey? key = OpenKey(writable: true);
            if (key == null)
                throw new InvalidOperationException("the NVIDIA display driver key is not writable");

            key.DeleteValue(name, throwOnMissingValue: false);
        }

        public void Flush()
        {
            using RegistryKey? key = OpenKey(writable: true);
            if (key == null)
                throw new InvalidOperationException("the NVIDIA display driver key is not writable");

            // A silent no-flush would break the one guarantee the restart relies on:
            // the records are on disk before the driver re-reads them.
            key.Flush();
        }

        private RegistryKey? OpenKey(bool writable)
        {
            Target? target = ResolveTarget();
            if (target == null)
                return null;

            using RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Default);
            return baseKey.OpenSubKey(target.DriverKey, writable);
        }

        /// <summary>
        /// The resolved adapter, cached: one WMI device query per store operation, not
        /// one per record write. A null resolution is not cached, so a transient PnP
        /// enumeration failure does not disable the verbs for the process lifetime.
        /// </summary>
        private Target? ResolveTarget()
        {
            if (_target != null)
                return _target;

            return _target = Resolve();
        }

        /// <summary>
        /// The present adapter's class key. Two NVIDIA class keys exist on the field
        /// machine (one per subsystem id the ROM has reported); only the present
        /// adapter's own <c>Driver</c> pointer names the live one.
        /// </summary>
        private Target? Resolve()
        {
            PnpDeviceState? state = _devices.GetStateAsync(NvidiaDisplay).GetAwaiter().GetResult();
            if (state == null || !state.Present || state.InstanceId.Length == 0)
                return null;

            using RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Default);
            using RegistryKey? deviceKey = baseKey.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\" + state.InstanceId);
            if (deviceKey?.GetValue("Driver") is not string driver)
                return null;

            int slash = driver.IndexOf('\\');
            if (slash <= 0 || slash + 1 >= driver.Length)
                return null;

            return new Target(state.InstanceId, DisplayClassKey + @"\" + driver.Substring(slash + 1));
        }

        private sealed record Target(string InstanceId, string DriverKey);
    }

    /// <summary>
    /// <c>pnputil /restart-device</c> with a bounded wait. The display adapter only
    /// re-reads the ROM records when the driver restarts the device, and the restart
    /// blanks the screen, so it is never guessed at: a timeout or a non-zero exit is
    /// reported as a failure the caller can hand to the user.
    /// </summary>
    public sealed class PnputilGpuRestarter : IGpuRestarter
    {
        internal const int RestartTimeoutMilliseconds = 60_000;

        public bool Restart(string? instanceId)
        {
            if (string.IsNullOrEmpty(instanceId))
                throw new InvalidOperationException("no present NVIDIA display adapter to restart");

            var startInfo = new ProcessStartInfo(
                Path.Combine(Environment.SystemDirectory, "pnputil.exe"),
                $"/restart-device \"{instanceId}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
            };

            using Process? process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("pnputil.exe did not start");

            if (!process.WaitForExit(RestartTimeoutMilliseconds))
                throw new TimeoutException("the GPU restart did not finish within 60 s; restart Windows to activate the staged limits");

            if (process.ExitCode != 0)
                throw new InvalidOperationException($"pnputil /restart-device exited with {process.ExitCode}");

            return true;
        }
    }

    /// <summary>
    /// The driver's enforced board-power ceiling through NVML (mW). Read-only, and the
    /// only witness that the new ROM maximum was actually consumed: the records can be
    /// staged and read back perfectly while the driver keeps running the old ceiling.
    /// </summary>
    public sealed class NvmlPowerCeilingProbe : IGpuPowerCeilingProbe
    {
        public int? ReadEnforcedLimitMilliwatts()
        {
            try
            {
                if (NvmlInit() != 0)
                    return null;

                try
                {
                    // shortcut: the first NVML device; this fork's boxes are single-GPU.
                    if (NvmlDeviceGetHandleByIndex(0, out IntPtr device) != 0)
                        return null;

                    return NvmlDeviceGetEnforcedPowerLimit(device, out uint limit) == 0 ? (int)limit : null;
                }
                finally
                {
                    _ = NvmlShutdown();
                }
            }
            catch (DllNotFoundException)
            {
                return null;
            }
            catch (EntryPointNotFoundException)
            {
                return null;
            }
        }

        [DllImport("nvml.dll", EntryPoint = "nvmlInit_v2")]
        private static extern int NvmlInit();

        [DllImport("nvml.dll", EntryPoint = "nvmlShutdown")]
        private static extern int NvmlShutdown();

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetHandleByIndex_v2")]
        private static extern int NvmlDeviceGetHandleByIndex(uint index, out IntPtr device);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetEnforcedPowerLimit")]
        private static extern int NvmlDeviceGetEnforcedPowerLimit(IntPtr device, out uint limit);
    }
}
