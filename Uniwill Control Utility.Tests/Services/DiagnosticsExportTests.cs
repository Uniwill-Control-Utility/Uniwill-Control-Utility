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

            // US-10: the hardware file rides the same bundle, even when the engine
            // cannot answer (this fake returns no dump) - it explains itself instead.
            ZipArchiveEntry hardware = Assert.Single(zip.Entries, e => e.FullName == "hardware.txt");
            using var hardwareReader = new StreamReader(hardware.Open());
            string hardwareText = hardwareReader.ReadToEnd();
            Assert.Contains("EC hardware state", hardwareText);
            Assert.Contains("No EC state was read", hardwareText);
            Assert.Contains("GPU power budget (F7): unknown", hardwareText);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void HardwareComposer_NamesTheMemoryOffsetAndWhetherItIsLive()
    {
        // A driver accepts the clock write and erases it with the next pstate write (field,
        // 2026-11-16), so the report must carry the readback verdict, not the request.
        Assert.Contains(
            "GPU memory offset: +500 MHz (live) - memory 1750 -> 1812 MHz (+62) at pstate 0",
            DiagnosticsExportService.ComposeHardware(Dump(), "collected", memoryOffset: new UCU.Core.Gpu.MemoryOffsetVerdict(
                500, true, true, "memory 1750 -> 1812 MHz (+62) at pstate 0")));

        Assert.Contains(
            "GPU memory offset: +500 MHz (NOT in force)",
            DiagnosticsExportService.ComposeHardware(Dump(), "collected", memoryOffset: new UCU.Core.Gpu.MemoryOffsetVerdict(
                500, true, false, "memory reads 1750 MHz at pstate 0, the 1750 MHz stock clock")));

        Assert.Contains(
            "GPU memory offset: unknown (no clock or curve write this session)",
            DiagnosticsExportService.ComposeHardware(Dump(), "collected"));
    }

    private static EcHardwareDumpDto Dump() => new()
    {
        EcVersionMajor = 116,
        EcVersionMinor = 0,
        ProjectIdRaw = 0x1A,
        CpuTemperature = 61,
        GpuTemperature = 44,
        MainFanRpm = 1300,
        SecondFanRpm = 1350,
        CpuFanDuty = 50,
        GpuFanDuty = 50,
        Pl1 = 95,
        Pl2 = 135,
        Pl4 = 135,
        CoolingMode = 0x0E,
        LiquidCoolingSupport = 0x80,
        ThermalBudget = 80,
        FanSwitchSpeed = 0x83,
        PerformanceMode = 2,
        CustomProfileActive = true,
        FanControlSupported = true,
        EcSpecValid = true,
        SpecPl1Max = 210,
        SpecPl2Max = 210,
        SpecPl4Max = 420,
        SpecTccOffsetMax = 10,
        SpecTgpOffsetMax = 55,
        SpecDynamicBoostMax = 25,
        SpecGpuTargetTemp = 87,
        SpecDefaultTgp = 95,
        SpecDynamicBoostCpuTdp = 0xFF,
        CpuDuty = new byte[] { 0, 60, 64, 72 },
        GpuDuty = new byte[] { 0, 60, 64, 72 },
    };

    [Fact]
    public void HardwareComposer_RendersIdentitySensorsAndTables()
    {
        string text = DiagnosticsExportService.ComposeHardware(Dump(), "collected");

        Assert.Contains("EC version: 116.0", text);
        Assert.Contains("Collection: collected", text);
        Assert.Contains("CPU 61 C", text);
        Assert.Contains("LCSE True", text);
        Assert.Contains("OCPL 3", text);
        Assert.Contains("CPU duty     00 3C 40 48", text);
    }

    [Fact]
    public void HardwareComposer_RendersTheEcSpecTable()
    {
        string text = DiagnosticsExportService.ComposeHardware(Dump(), "collected");

        Assert.Contains("ECSpec 0x9C40A500", text);
        Assert.Contains("DefaultTGP 95 W", text);
        Assert.Contains("PL4 max 420 W", text);
        Assert.Contains("TCC raw 10", text);               // the raw byte: its meaning is machine-dependent
        Assert.DoesNotContain("95 C)", text);               // never a derived ceiling - 95 would read as 10 C
        Assert.Contains("DB CPU TDP n/a", text);           // 0xFF is "not applicable", not a wattage
    }

    [Fact]
    public void HardwareComposer_RendersTheLaptopPowerBudget()
    {
        // The driver enforces min(request, budget): a report without this number cannot
        // explain why a raised ceiling still enforced 150 W.
        Assert.Contains("GPU power budget (F7): 225 W (225000 mW)", DiagnosticsExportService.ComposeHardware(Dump(), "collected", 225_000));
        Assert.Contains("GPU power budget (F7): unknown", DiagnosticsExportService.ComposeHardware(Dump(), "collected"));
    }

    [Fact]
    public void HardwareComposer_RendersTheThreeRuntimeRailsAndTheirPolicyRanges()
    {
        // The rails are the lever that took the board from 175-179 W to 205-214 W with every
        // other number identical, so the report has to name them next to the budget.
        var rails = new GpuRailLimitsDto
        {
            NvvddMilliamps = 250_000,
            NvvddMinMilliamps = 210_000,
            NvvddMaxMilliamps = 294_000,
            MsvddMilliamps = 70_000,
            MsvddMinMilliamps = 60_000,
            MsvddMaxMilliamps = 84_000,
            BoardInputMilliamps = 14_026,
            BoardInputMinMilliamps = 11_053,
            BoardInputMaxMilliamps = 14_026,
        };

        Assert.Contains(
            "GPU rail currents: NVVDD 250000 mA (policy 210000-294000 mA), " +
            "MSVDD 70000 mA (policy 60000-84000 mA), board input 14026 mA (policy 11053-14026 mA)",
            DiagnosticsExportService.ComposeHardware(Dump(), "collected", 225_000, rails));

        Assert.Contains(
            "GPU rail currents: unknown (engine unreachable)",
            DiagnosticsExportService.ComposeHardware(Dump(), "collected"));

        Assert.Contains(
            "GPU rail currents: unknown (NVAPI interface 0x67F31384 unavailable)",
            DiagnosticsExportService.ComposeHardware(
                Dump(), "collected", railLimits: new GpuRailLimitsDto { Reason = "NVAPI interface 0x67F31384 unavailable" }));
    }

    [Fact]
    public void HardwareComposer_SaysWhenTheEcPublishesNoSpec()
    {
        EcHardwareDumpDto dump = Dump() with { EcSpecValid = false };

        string text = DiagnosticsExportService.ComposeHardware(dump, "collected");

        Assert.Contains("ECSpec 0x9C40A500: unavailable", text);
        Assert.DoesNotContain("DefaultTGP", text);
    }

    [Fact]
    public void HardwareComposer_ExplainsAMissingDumpInsteadOfThrowing()    {
        string text = DiagnosticsExportService.ComposeHardware(null, "skipped: engine busy (dump timed out)");

        Assert.Contains("skipped: engine busy", text);
        Assert.Contains("No EC state was read", text);
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

        public Task<EcHardwareDumpDto?> DumpHardwareAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<EcHardwareDumpDto?>(null);

        public Task<bool> NotifyAppExitAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<bool> SetAutostartAsync(bool enabled, int delaySeconds, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
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
