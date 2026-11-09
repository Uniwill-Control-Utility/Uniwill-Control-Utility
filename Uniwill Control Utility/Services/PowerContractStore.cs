using System;
using System.IO;
using System.Text.Json;
using Serilog;
using UCU.Core.Supervision;

namespace Uniwill_Control_Utility.Services
{
    /// <summary>
    /// The persisted record of the power contract the preset pipeline last
    /// APPLIED — what "my tuning is on" means for the guardian (U-107).
    ///
    /// Capture is at the two acts that write the EC power contract:
    /// <see cref="Scripts.GPUs.NVIDIA.NvTuning.SetPowerLimit"/> (the GPU
    /// cTGP/DB activation every preset path funnels through) and the
    /// ApplyCpuPowerLimits call sites (the CPU PL pair, Custom presets on AMD
    /// APUs only). A stock-mode apply CLEARS the whole contract: stock
    /// presets deliberately yield power values to the firmware's per-mode
    /// factory table, and "heal" must never restore custom bytes over a
    /// preset that chose not to own them.
    ///
    /// Persisted to %LOCALAPPDATA%\UCU\power_contract.json (the CustomPreset
    /// store's directory) so a restart keeps the intent: the guardian's
    /// contract is what the LAST apply established, not what this process
    /// performed. Corrupt file = no contract, never a guess.
    /// </summary>
    internal static class PowerContractStore
    {
        private static readonly object _lock = new();
        private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

        private static PowerContract _current = PowerContract.Empty;
        private static string? _filePathOverride;

        /// <summary>Default: %LOCALAPPDATA%\UCU\power_contract.json; tests
        /// point it at a temp file.</summary>
        public static string FilePath => _filePathOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UCU", "power_contract.json");

        public static PowerContract Current
        {
            get { lock (_lock) return _current; }
        }

        /// <summary>Startup load: the file's contract, or empty when absent
        /// or corrupt. Called from App before the guardian starts.</summary>
        public static void Load()
        {
            lock (_lock)
            {
                _current = ReadFile() ?? PowerContract.Empty;
            }

            Log.Information("[PowerGuardian] contract loaded: gpu={Gpu} (ctgp={Ctgp}, db={Db}) cpu={Cpu} (pl1={Pl1}, pl2={Pl2})",
                _current.HasGpu, _current.GpuCtgpOffset, _current.GpuDbOffset, _current.HasCpu, _current.Pl1Watts, _current.Pl2Watts);
        }

        /// <summary>GPU contract note, from the SetPowerLimit chokepoint with
        /// the exact offsets that were written.</summary>
        public static void NoteGpuApplied(int ctgpOffset, int dbOffset)
        {
            Set(c => c with { HasGpu = true, GpuCtgpOffset = ctgpOffset, GpuDbOffset = dbOffset });
            Log.Information("[PowerGuardian] GPU contract noted: cTGP={Ctgp}, DB={Db}", ctgpOffset, dbOffset);
        }

        /// <summary>CPU contract note, from the ApplyCpuPowerLimits call
        /// sites with the exact watts pair.</summary>
        public static void NoteCpuApplied(int pl1Watts, int pl2Watts)
        {
            Set(c => c with { HasCpu = true, Pl1Watts = pl1Watts, Pl2Watts = pl2Watts });
            Log.Information("[PowerGuardian] CPU contract noted: PL1={Pl1}W, PL2={Pl2}W", pl1Watts, pl2Watts);
        }

        /// <summary>Stock apply: the firmware's factory table rules; the
        /// guardian must not heal custom values it no longer owns.</summary>
        public static void ClearAll()
        {
            Set(_ => PowerContract.Empty);
            Log.Information("[PowerGuardian] contract cleared (stock preset applied — power values yield to the firmware)");
        }

        private static void Set(Func<PowerContract, PowerContract> mutate)
        {
            PowerContract next;
            lock (_lock)
            {
                next = mutate(_current);
                _current = next;
            }

            WriteFile(next);
        }

        private static PowerContract? ReadFile()
        {
            try
            {
                if (!File.Exists(FilePath))
                    return null;

                return JsonSerializer.Deserialize<PowerContract>(File.ReadAllText(FilePath), Json);
            }
            catch (Exception ex)
            {
                // "No contract" is the safe reading; a half-parsed contract
                // would heal toward bytes that were never applied.
                Log.Information("[PowerGuardian] contract file unreadable ({Message}) - starting with no contract", ex.Message);
                return null;
            }
        }

        private static void WriteFile(PowerContract contract)
        {
            try
            {
                string path = FilePath;
                AtomicJsonFile.Write(path, JsonSerializer.Serialize(contract, Json));
            }
            catch (Exception ex)
            {
                // Persistence is best-effort: the in-memory contract governs
                // this session's healing even when the disk write fails.
                Log.Information("[PowerGuardian] contract persist failed ({Message})", ex.Message);
            }
        }

        /// <summary>Test seam: point the store at a temp file (null restores
        /// the production path).</summary>
        internal static void OverrideFilePathForTests(string? path)
        {
            lock (_lock)
            {
                _filePathOverride = path;
                _current = PowerContract.Empty;
            }
        }
    }
}
