using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using UCU.Contracts;
using UCU.Contracts.Capabilities;
using Uniwill_Control_Utility.Services;

namespace Uniwill_Control_Utility.Tests.Services;

/// <summary>
/// The diagnostics report composer (U-208, narrowed to the export). The
/// bundle's value is that a field report arrives complete: versions, OS, CPU,
/// engine answer, OEM takeover state and the gate's refusals - the exact facts
/// three U-203 field rounds had to be hand-collected for. The composer is pure:
/// the same snapshot always produces the same text, and a missing fact renders
/// "unknown" instead of failing the export.
/// </summary>
public sealed class DiagnosticsExportTests
{
    private static DiagnosticsSnapshot Snapshot() => new()
    {
        UcuVersion = "0.9.7.0",
        Os = "Microsoft Windows 11 Pro 10.0.22631 (X64)",
        Cpu = "13th Gen Intel(R) Core(TM) i7-13700H",
        Engine = "version 0.9.7.0, protocol 1, pid 4242, EC available: True",
        OemTakeover = "state Paused, services 1 (running 0), processes 0, journal 2, autostart neutralized 1, suspects 1",
        OemSuspects = new[] { "service 'GCUBridge': publisher 'CN=Contoso, C=US' is not on the OEM allowlist" },
    };

    [Fact]
    public void Report_CarriesEverySection()
    {
        string text = DiagnosticsExportService.ComposeReport(Snapshot(), attachments: null);

        Assert.Contains("Uniwill Control Utility diagnostics", text);
        Assert.Contains("UCU version: 0.9.7.0", text);
        Assert.Contains("OS: Microsoft Windows 11 Pro", text);
        Assert.Contains("CPU: 13th Gen Intel", text);
        Assert.Contains("Engine: version 0.9.7.0, protocol 1", text);
        Assert.Contains("OEM Control Center: state Paused", text);
        Assert.Contains("service 'GCUBridge'", text); // the gate's refusal, verbatim
    }

    [Fact]
    public void Report_RendersUnknownForMissingFacts()
    {
        string text = DiagnosticsExportService.ComposeReport(new DiagnosticsSnapshot(), attachments: null);

        Assert.Contains("UCU version: unknown", text);
        Assert.Contains("OS: unknown", text);
        Assert.Contains("CPU: unknown", text);
        Assert.Contains("Engine: unknown", text);
        Assert.Contains("OEM Control Center: unknown", text);
        Assert.DoesNotContain("Gate refusals", text);
    }

    [Fact]
    public void Report_ListsAttachmentsOnlyWhenCollected()
    {
        string none = DiagnosticsExportService.ComposeReport(Snapshot(), attachments: null);
        Assert.DoesNotContain("Attachments:", none);

        string some = DiagnosticsExportService.ComposeReport(
            Snapshot(), new[] { "ucu_log.txt", "engine-20261101.log" });
        Assert.Contains("Attachments:", some);
        Assert.Contains("  - ucu_log.txt", some);
        Assert.Contains("  - engine-20261101.log", some);
    }

    [Fact]
    public async Task Export_WritesAZipWithTheReport_WithoutRequiringAnyAttachment()
    {
        string path = Path.Combine(Path.GetTempPath(), "ucu-diag-test-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            var service = new DiagnosticsExportService(new FakeEngine(), new FakeOem());

            await service.ExportAsync(path);

            Assert.True(File.Exists(path));
            using ZipArchive zip = ZipFile.OpenRead(path);
            ZipArchiveEntry report = Assert.Single(zip.Entries, e => e.FullName == "report.txt");
            using var reader = new StreamReader(report.Open());
            string text = reader.ReadToEnd();
            Assert.Contains("Uniwill Control Utility diagnostics", text);
            Assert.Contains("OEM Control Center: state Active", text);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private sealed class FakeEngine : IEngineCapability
    {
        public Task<bool> PingAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<EngineInfoDto> GetInfoAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new EngineInfoDto
            {
                EngineVersion = "0.9.7.0",
                ProtocolVersion = 1,
                ProcessId = 42,
                EcAvailable = true,
            });

        public Task<bool> IsHardwareAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<bool> NotifyAppExitAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class FakeOem : IOemCapability
    {
        public Task<OemStatusDto> GetStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new OemStatusDto
            {
                State = "Active",
                ServicesInstalled = 1,
                ServicesRunning = 1,
                OemProcessesRunning = 2,
            });

        public Task<OemActionDto> PauseAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new OemActionDto());

        public Task<OemActionDto> ResumeAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new OemActionDto());
    }
}
