using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
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

        private readonly IEngineCapability _engine;
        private readonly IOemCapability _oem;

        public DiagnosticsExportService(IEngineCapability engine, IOemCapability oem)
        {
            _engine = engine;
            _oem = oem;
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
