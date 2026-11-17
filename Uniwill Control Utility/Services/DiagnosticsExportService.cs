using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Serilog;
using UCU.Contracts;
using UCU.Contracts.Capabilities;

namespace Uniwill_Control_Utility.Services
{
    /// <summary>The facts the report is composed from (one record so the pure
    /// composer is testable without touching the machine).</summary>
    public sealed record DiagnosticsSnapshot
    {
        public string UcuVersion { get; init; } = string.Empty;

        public string Os { get; init; } = string.Empty;

        public string Cpu { get; init; } = string.Empty;

        /// <summary>The engine's own answer ("version, protocol, pid, EC"), or
        /// a reason it could not be asked (engine stopped/unreachable).</summary>
        public string Engine { get; init; } = string.Empty;

        public string OemTakeover { get; init; } = string.Empty;

        public IReadOnlyList<string> OemSuspects { get; init; } = Array.Empty<string>();
    }

    /// <summary>
    /// The one-click diagnostics bundle (story U-208, narrowed to the export:
    /// the mini drawer and skins were dropped - the tray flyout already covers
    /// the first, and skins would fight the Fluent/Windows theming the app
    /// follows by design).
    ///
    /// Contents: a plain-text report (versions, OS, CPU, engine, OEM takeover
    /// state) plus the attachments that actually diagnose a field bug - the
    /// app log, the engine log, and both takeover journals. The last three
    /// U-203 field rounds each needed exactly these files, hand-collected.
    /// Files are copied with FileShare.ReadWrite because both logs are open
    /// for writing while the app runs.
    /// </summary>
    public sealed class DiagnosticsExportService
    {
        private const string EngineFolderName = "engine";

        /// <summary>
        /// Budget for the EC dump. The EC is single-locked at ~6 ms per byte and the dump
        /// reads ~20 singles plus six 16-byte tables (~0.7 s) — but it can land behind a
        /// fan plan, so the bound exists and a timeout is reported instead of failing the
        /// export (the field case: a bug report is usually taken while the app is busy).
        /// </summary>
        private static readonly TimeSpan HardwareDumpBudget = TimeSpan.FromSeconds(4);

        private readonly IEngineCapability _engine;
        private readonly IOemCapability _oem;
        private readonly IGpuTuningCapability? _gpu;

        public DiagnosticsExportService(IEngineCapability engine, IOemCapability oem, IGpuTuningCapability? gpu = null)
        {
            _engine = engine;
            _oem = oem;
            _gpu = gpu;
        }

        /// <summary>Writes the bundle to <paramref name="targetZip"/> and answers
        /// the archive path. Every collector is best-effort: a file that cannot
        /// be read is named in the report instead of failing the export.</summary>
        public async Task<string> ExportAsync(string targetZip, CancellationToken cancellationToken = default)
        {
            DiagnosticsSnapshot snapshot = await CollectAsync(cancellationToken);
            List<string> attachments = new();

            string staging = Path.Combine(Path.GetTempPath(), "ucu-diag-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            try
            {
                string reportPath = Path.Combine(staging, "report.txt");
                await File.WriteAllTextAsync(reportPath, ComposeReport(snapshot, attachments: null), cancellationToken);

                // App logs.
                foreach (string log in SafeEnumerate(App.LOGS_FOLDER, "*.txt"))
                    CopyShared(log, Path.Combine(staging, "app-" + Path.GetFileName(log)), attachments);

                // Engine logs + the machine-scope takeover journal.
                string engineFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "UCU", EngineFolderName);
                foreach (string log in SafeEnumerate(engineFolder, "engine*.log"))
                    CopyShared(log, Path.Combine(staging, "engine-" + Path.GetFileName(log)), attachments);

                CopyShared(Path.Combine(engineFolder, "oem-takeover-journal.json"),
                    Path.Combine(staging, "engine-oem-takeover-journal.json"), attachments);

                // The user-scope takeover journal.
                CopyShared(Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UCU", "oem-takeover-journal.json"),
                    Path.Combine(staging, "user-oem-takeover-journal.json"), attachments);

                // The report lists what was actually collected.
                await File.WriteAllTextAsync(reportPath, ComposeReport(snapshot, attachments), cancellationToken);

                // EC hardware state (US-10). Best-effort and bounded: a busy or absent
                // engine is written into the file as the reason, never an export failure.
                EcHardwareDumpDto? dump = null;
                string hardwareNote;
                try
                {
                    using var dumpCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    dumpCts.CancelAfter(HardwareDumpBudget);
                    dump = await _engine.DumpHardwareAsync(dumpCts.Token);
                    hardwareNote = dump is null ? "engine answered null (EC unavailable)" : "collected";
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    hardwareNote = "skipped: engine busy (dump timed out)";
                }
                catch (Exception ex)
                {
                    hardwareNote = "unavailable (" + ex.GetType().Name + ")";
                }

                await File.WriteAllTextAsync(
                    Path.Combine(staging, "hardware.txt"), ComposeHardware(dump, hardwareNote, ReadRomOverride(), ReadRailLimits(),
                        Scripts.GPUs.NVIDIA.NvTuning.LastMemoryOffset), cancellationToken);

                string? directory = Path.GetDirectoryName(targetZip);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                if (File.Exists(targetZip))
                    File.Delete(targetZip);

                ZipFile.CreateFromDirectory(staging, targetZip, CompressionLevel.Optimal, includeBaseDirectory: false);
                return targetZip;
            }
            finally
            {
                try
                {
                    Directory.Delete(staging, recursive: true);
                }
                catch
                {
                    // A leftover temp folder is not worth failing the export.
                }
            }
        }

        private async Task<DiagnosticsSnapshot> CollectAsync(CancellationToken cancellationToken)
        {
            string engineLine;
            try
            {
                EngineInfoDto info = await _engine.GetInfoAsync(cancellationToken);
                engineLine = $"version {info.EngineVersion}, protocol {info.ProtocolVersion}, pid {info.ProcessId}, EC available: {info.EcAvailable}";
            }
            catch (Exception ex)
            {
                // The most common field state: the engine is not running. That
                // fact is itself diagnostic, never an export failure.
                engineLine = "unreachable (" + ex.GetType().Name + ")";
            }

            string oemLine;
            IReadOnlyList<string> suspects = Array.Empty<string>();
            try
            {
                OemStatusDto oem = await _oem.GetStatusAsync(cancellationToken);
                oemLine =
                    $"state {oem.State}, services {oem.ServicesInstalled} (running {oem.ServicesRunning}), " +
                    $"processes {oem.OemProcessesRunning}, journal {oem.JournalCount}, " +
                    $"autostart neutralized {oem.AutostartNeutralized}, suspects {oem.SquatSuspects.Count}";
                suspects = oem.SquatSuspects;
            }
            catch (Exception ex)
            {
                oemLine = "unreachable (" + ex.GetType().Name + ")";
            }

            return new DiagnosticsSnapshot
            {
                UcuVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown",
                Os = $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})",
                Cpu = ReadCpuName(),
                Engine = engineLine,
                OemTakeover = oemLine,
                OemSuspects = suspects,
            };
        }

        /// <summary>
        /// The hardware file's text. Pure: the same dump produces the same bytes, and a
        /// missing dump renders the reason instead of throwing.
        /// </summary>
        public static string ComposeHardware(
            EcHardwareDumpDto? dump,
            string note,
            GpuRomOverrideStateDto? romOverride = null,
            GpuRailLimitsDto? railLimits = null,
            UCU.Core.Gpu.MemoryOffsetVerdict? memoryOffset = null)
        {
            var text = new StringBuilder();
            text.AppendLine("Uniwill Control Utility - EC hardware state");
            text.AppendLine("Generated: " + DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"));
            text.AppendLine("Collection: " + (string.IsNullOrWhiteSpace(note) ? "unknown" : note));

            // The driver accepts clock writes and then erases the memory offset with the next
            // pstate write (field, 2026-11-16), so the only truthful account is the readback
            // the last write took: the offset and whether it is in force.
            text.AppendLine("GPU memory offset: " +
                            (memoryOffset is UCU.Core.Gpu.MemoryOffsetVerdict verdict
                                ? verdict.Summary
                                : "unknown (no clock or curve write this session)"));

            // The laptop's own power budget (the driver's F7 value): the driver enforces
            // min(request, budget), so without this number a report cannot say why a
            // raised ceiling still enforced 150 W.
            text.AppendLine("GPU power budget (F7): " +
                            (romOverride?.BudgetMilliwatts is int budget ? Watts(budget) : "unknown (engine unreachable)"));

            // The staged unlock and the live half: after a reboot the records are still
            // staged while the EC offset, the rails, the budget and the driver's request
            // are not, so "enabled" alone does not mean the card is drawing the watts.
            text.AppendLine("GPU power override: " + OverrideLine(romOverride));

            // The runtime rail currents: the records only widen the policy, so with
            // everything else identical these instead of the stock ones are what took the
            // board from 175-179 W to 205-214 W. The range is the driver's live policy.
            text.AppendLine("GPU rail currents: " + RailsLine(railLimits));

            // The VBIOS image's own power-policy table: an unpinned ROM (a tester's VBIOS)
            // can only be pinned on evidence, so the report says whether the seven record
            // offsets the unlock patches still land on this image's policy fields.
            AppendVbiosPolicy(text, romOverride?.VbiosPolicy);

            if (dump is null)
            {
                text.AppendLine();
                text.AppendLine("No EC state was read. The engine log names the reason; a busy engine");
                text.AppendLine("(a fan curve or preset apply in flight) is the common case.");
                return text.ToString();
            }

            text.AppendLine("EC version: " + dump.EcVersionMajor + "." + dump.EcVersionMinor);
            text.AppendLine("Project ID 0x0740: 0x" + dump.ProjectIdRaw.ToString("X2"));
            text.AppendLine("Sensors: CPU " + dump.CpuTemperature + " C, GPU " + dump.GpuTemperature +
                            " C, fans " + dump.MainFanRpm + " / " + dump.SecondFanRpm +
                            " rpm, duty " + dump.CpuFanDuty + " / " + dump.GpuFanDuty + " of 200");
            text.AppendLine("CPU limits: PL1 " + dump.Pl1 + " W, PL2 " + dump.Pl2 + " W, PL4 " + dump.Pl4 + " W");
            text.AppendLine("GPU bytes: enable 0x" + dump.GpuEnable.ToString("X2") +
                            ", cTGP 0x" + dump.GpuCtgp.ToString("X2") +
                            ", TPP 0x" + dump.GpuTpp.ToString("X2") +
                            ", DB 0x" + dump.GpuDb.ToString("X2"));
            text.AppendLine("Cooling: 0x07C7 0x" + dump.CoolingMode.ToString("X2") +
                            " (LCSE " + ((dump.CoolingMode & 0x02) != 0) +
                            ", OCPL " + ((dump.CoolingMode >> 2) & 0x07) + ")" +
                            ", 0x07A4 0x" + dump.LiquidCoolingSupport.ToString("X2") +
                            ", 0x0788 " + dump.ThermalBudget +
                            ", 0x0787 0x" + dump.FanSwitchSpeed.ToString("X2"));
            text.AppendLine("Mode: " + dump.PerformanceMode +
                            ", custom profile " + dump.CustomProfileActive +
                            ", fan control supported " + dump.FanControlSupported);
            text.AppendLine(EcSpecLine(dump));
            text.AppendLine();
            text.AppendLine("Fan tables (16 bytes each; duty 0-200, temp 255 = inactive):");
            text.AppendLine("  CPU tempUp   " + HexBytes(dump.CpuTempUp));
            text.AppendLine("  CPU tempDown " + HexBytes(dump.CpuTempDown));
            text.AppendLine("  CPU duty     " + HexBytes(dump.CpuDuty));
            text.AppendLine("  GPU tempUp   " + HexBytes(dump.GpuTempUp));
            text.AppendLine("  GPU tempDown " + HexBytes(dump.GpuTempDown));
            text.AppendLine("  GPU duty     " + HexBytes(dump.GpuDuty));
            return text.ToString();
        }

        /// <summary>
        /// The override state the engine sees, read-only and best-effort: it carries both
        /// the F7 budget and the staged unlock, so the report can say the unlock is enabled
        /// while the live half is not in force. An unreachable engine renders "unknown".
        /// </summary>
        private GpuRomOverrideStateDto? ReadRomOverride()
        {
            if (_gpu is null)
                return null;

            try
            {
                using var gpuCts = new CancellationTokenSource(HardwareDumpBudget);
                return _gpu.GetRomOverrideStateAsync(gpuCts.Token).GetAwaiter().GetResult();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// The rails the card carries and the live policy each is bounded by, read-only and
        /// best-effort: a report without them cannot say why the unlock held 175 W where the
        /// same records, budget and request reached 205-214 W.
        /// </summary>
        private GpuRailLimitsDto? ReadRailLimits()
        {
            if (_gpu is null)
                return null;

            try
            {
                using var gpuCts = new CancellationTokenSource(HardwareDumpBudget);
                return _gpu.GetRailLimitsAsync(gpuCts.Token).GetAwaiter().GetResult();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>One line: whether an unlock is staged, and whether the driver-side half is in force.</summary>
        private static string OverrideLine(GpuRomOverrideStateDto? romOverride)
        {
            if (romOverride is null)
                return "unknown (engine unreachable)";

            string live = romOverride.State == GpuRomOverrideStates.Unlocked
                ? "live half in force"
                : "live half not verified" + (romOverride.Reason is string reason ? " (" + reason + ")" : string.Empty);

            return (romOverride.Enabled ? "enabled" : "not enabled") + ", state " + romOverride.State + ": " + live;
        }

        /// <summary>One line: each rail's current and the policy range it was checked against.</summary>
        private static string RailsLine(GpuRailLimitsDto? rails)
        {
            if (rails is null)
                return "unknown (engine unreachable)";

            if (rails.Reason is string reason)
                return "unknown (" + reason + ")";

            return "NVVDD " + Rail(rails.NvvddMilliamps, rails.NvvddMinMilliamps, rails.NvvddMaxMilliamps) +
                   ", MSVDD " + Rail(rails.MsvddMilliamps, rails.MsvddMinMilliamps, rails.MsvddMaxMilliamps) +
                   ", board input " + Rail(rails.BoardInputMilliamps, rails.BoardInputMinMilliamps, rails.BoardInputMaxMilliamps);
        }

        private static string Rail(int? milliamps, int? min, int? max) =>
            (milliamps is int value ? value + " mA" : "unknown") +
            (min is int low && max is int high ? " (policy " + low + "-" + high + " mA)" : string.Empty);

        /// <summary>
        /// The VBIOS policy block: a handful of lines, or the one reason the image could
        /// not be read. When every pinned offset lands on its field the line says so;
        /// otherwise each differing record carries both numbers.
        /// </summary>
        private static void AppendVbiosPolicy(StringBuilder text, GpuVbiosPolicyDto? policy)
        {
            if (policy is null)
            {
                text.AppendLine("VBIOS power policy: unknown (engine unreachable)");
                return;
            }

            if (policy.Reason is string reason)
            {
                text.AppendLine("VBIOS power policy: unavailable (" + reason + ")");
                return;
            }

            text.AppendLine("VBIOS power policy: " + policy.VbiosVersion +
                            ", pinned " + (policy.Pinned ? "yes" : "no") +
                            ", table 0x" + Hex(policy.TableOffset) + " header " + policy.TableHeader +
                            ", PCI device 0x" + (policy.PciDevice is int device ? device.ToString("X4", CultureInfo.InvariantCulture) : "----"));
            text.AppendLine("  power " + PolicyLine(policy.Power, "mW"));
            text.AppendLine("  NVVDD " + PolicyLine(policy.NvVdd, "mA"));
            text.AppendLine("  MSVDD " + PolicyLine(policy.MsVdd, "mA"));
            text.AppendLine("  board input " + PolicyLine(policy.BoardInput, "mA"));
            text.AppendLine("  pinned offsets: " + PinnedOffsetsLine(policy));
        }

        private static string PolicyLine(GpuPolicyLimitsDto? field, string unit) =>
            field is GpuPolicyLimitsDto limits
                ? "min " + limits.Min + " def " + limits.Default + " max " + limits.Max +
                  " " + unit + " (at 0x" + Hex(limits.Offset) + ")"
                : "absent";

        private static string PinnedOffsetsLine(GpuVbiosPolicyDto policy)
        {
            if (policy.PinnedOffsets.Count == 0)
                return "not checked";

            GpuPinnedOffsetDto[] differing = policy.PinnedOffsets.Where(check => !check.Match).ToArray();
            if (differing.Length == 0)
                return "MATCH this image";

            return "DIFFER (" + string.Join("; ", differing.Select(check =>
                "0x" + Hex(check.Offset) + " image " + check.ImageValue +
                ", table " + check.Component + " " + check.TableValue + " at 0x" + Hex(check.TableValueOffset))) + ")";
        }

        private static string Hex(int? offset) => offset is int value ? value.ToString("X", CultureInfo.InvariantCulture) : "----";

        private static string Hex(int offset) => offset.ToString("X", CultureInfo.InvariantCulture);

        private static string Hex(uint offset) => offset.ToString("X", CultureInfo.InvariantCulture);

        private static string Watts(int milliwatts) =>            (milliwatts / 1000.0).ToString("0.##", CultureInfo.InvariantCulture) + " W (" + milliwatts + " mW)";

        /// <summary>
        /// The EC's per-model power table (ECSpec, IOCTL 0x9C40A500) as one line: the numbers a
        /// GPU-power bug report cannot be reconstructed without - above all DefaultTGP, the base
        /// the cTGP offset is measured from. A table the EC did not publish says so.
        /// </summary>
        private static string EcSpecLine(EcHardwareDumpDto dump)
        {
            if (!dump.EcSpecValid)
                return "ECSpec 0x9C40A500: unavailable (the EC refused the table or echoed garbage)";

            return "ECSpec 0x9C40A500: PL1 max " + dump.SpecPl1Max + " W, PL2 max " + dump.SpecPl2Max +
                   " W, PL4 max " + dump.SpecPl4Max + " W, TCC raw " + dump.SpecTccOffsetMax +
                   ", TGP offset max " + dump.SpecTgpOffsetMax +
                   " W, DefaultTGP " + dump.SpecDefaultTgp + " W, DB max " + dump.SpecDynamicBoostMax +
                   " W, GPU target " + dump.SpecGpuTargetTemp + " C, DB CPU TDP " +
                   (dump.SpecDynamicBoostCpuTdp == 0xFF ? "n/a" : dump.SpecDynamicBoostCpuTdp + " W");
        }

        private static string HexBytes(IReadOnlyList<byte> values)
        {
            var text = new StringBuilder();
            for (int i = 0; i < values.Count; i++)
            {
                if (i > 0)
                    text.Append(' ');
                text.Append(values[i].ToString("X2"));
            }
            return text.ToString();
        }

        /// <summary>The report text. Pure: the same snapshot produces the same
        /// bytes, and every missing fact renders "unknown" instead of throwing.</summary>
        public static string ComposeReport(DiagnosticsSnapshot snapshot, IReadOnlyList<string>? attachments)
        {
            var text = new StringBuilder();
            text.AppendLine("Uniwill Control Utility diagnostics");
            text.AppendLine("Generated: " + DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"));
            text.AppendLine("UCU version: " + OrUnknown(snapshot.UcuVersion));
            text.AppendLine("OS: " + OrUnknown(snapshot.Os));
            text.AppendLine("CPU: " + OrUnknown(snapshot.Cpu));
            text.AppendLine("Engine: " + OrUnknown(snapshot.Engine));
            text.AppendLine("OEM Control Center: " + OrUnknown(snapshot.OemTakeover));

            if (snapshot.OemSuspects.Count > 0)
            {
                text.AppendLine("Gate refusals (name-squat suspects, never touched):");
                foreach (string suspect in snapshot.OemSuspects)
                    text.AppendLine("  - " + suspect);
            }

            if (attachments is not null)
            {
                text.AppendLine();
                text.AppendLine("Attachments:");
                foreach (string file in attachments)
                    text.AppendLine("  - " + file);
            }

            return text.ToString();
        }

        private static string OrUnknown(string? value) =>
            string.IsNullOrWhiteSpace(value) ? "unknown" : value!;

        private static string ReadCpuName()
        {
            try
            {
                using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
                    @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                return Convert.ToString(key?.GetValue("ProcessorNameString"))?.Trim() ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static IEnumerable<string> SafeEnumerate(string folder, string pattern)
        {
            try
            {
                return Directory.Exists(folder) ? Directory.GetFiles(folder, pattern) : Array.Empty<string>();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        private static void CopyShared(string source, string destination, List<string> collected)
        {
            try
            {
                if (!File.Exists(source))
                    return;

                // Both logs are held open for writing; FileShare.ReadWrite is
                // what lets the bundle include the tail instead of skipping it.
                using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
                input.CopyTo(output);
                collected.Add(Path.GetFileName(destination));
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[Diagnostics] could not attach {File}", source);
            }
        }
    }
}
