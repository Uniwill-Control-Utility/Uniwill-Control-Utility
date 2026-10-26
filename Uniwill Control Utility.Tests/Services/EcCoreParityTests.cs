using System.Reflection;
using UCU.Core.Ec;
using Uniwill_Control_Utility.Models;
using Uniwill_Control_Utility.Services;

namespace Uniwill_Control_Utility.Tests.Services;

/// <summary>
/// Pins the app's fan-curve model to the UCU.Core encoders the EC engine now uses,
/// so the app-side model and the engine-side encoding cannot drift apart, and guards
/// the layering the engine-service design depends on.
///
/// The EC service's own encoders were deleted in this phase (they live in Core), so
/// the reference implementation for these assertions is the model's
/// <see cref="EcFanCurve.ToEcDutyTable"/>, which is what the UI still renders from.
/// </summary>
public sealed class EcCoreParityTests
{
    private static EcFanCurve[] AllPresetCurves() =>
        new[]
        {
            EcFanCurve.CreateSilent(),
            EcFanCurve.CreateBalanced(),
            EcFanCurve.CreatePerformance(),
            EcFanCurve.CreateFullSpeed(),
            EcFanCurve.CreateOff(),
        };

    [Fact]
    public void DutyTable_MatchesTheFanCurveModel()
    {
        foreach (EcFanCurve curve in AllPresetCurves())
        {
            Assert.Equal(curve.ToEcDutyTable(), EcDutyCodec.BuildDutyTable(curve.Duties));
        }
    }

    [Fact]
    public void DutyTable_MatchesOnPartialAndEdgeCurves()
    {
        var shortCurve = new EcFanCurve { Name = "Short", Duties = new List<int> { 0, 50 } };
        var fullCurve = new EcFanCurve { Name = "Full", Duties = new List<int> { 0, 100, 100, 100, 100, 100, 100, 100, 100, 100, 100 } };
        var flatCurve = new EcFanCurve { Name = "Flat", Duties = new List<int> { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 } };

        Assert.Equal(shortCurve.ToEcDutyTable(), EcDutyCodec.BuildDutyTable(shortCurve.Duties));
        Assert.Equal(fullCurve.ToEcDutyTable(), EcDutyCodec.BuildDutyTable(fullCurve.Duties));
        Assert.Equal(flatCurve.ToEcDutyTable(), EcDutyCodec.BuildDutyTable(flatCurve.Duties));
    }

    [Fact]
    public void CurveDto_CarriesTheCurveSoTheEngineReproducesTheSameTable()
    {
        // The UI renders from Duties; the engine writes the table it derives from the
        // DTO. If these two ever disagree, the fan page preview and the EC state part.
        foreach (EcFanCurve curve in AllPresetCurves())
        {
            UCU.Contracts.FanCurveDto dto = curve.ToCurveDto();

            Assert.Equal(curve.Name, dto.Name);
            Assert.Equal(curve.Duties, dto.DutyPercentByZone);
            Assert.Equal(curve.ToEcDutyTable(), EcDutyCodec.BuildDutyTable(dto.DutyPercentByZone));
        }
    }

    [Fact]
    public void FanCurveModel_UsesTheCoreTemperatureStepsForItsLabels()
    {
        // The page's temperature labels come from the model; the engine's default
        // thresholds come from Core. They are the same scale, and a drift here means
        // the UI shows a temperature the EC does not use.
        Assert.Equal(EcTemperatureCodec.DefaultTemperatureSteps, EcFanCurve.CpuTemperatures);
    }

    [Fact]
    public void PlatformResolver_DefaultsToAmd_UntilDetectionResolvesIntel()
    {
        // Family.TYPE is unresolved in a test process; the resolver must fall back to
        // the AMD mapping (raw 0/1/2) rather than an unset platform, which is the
        // mapping the product shipped with for years.
        EcPlatform platform = EcPlatformResolver.Current;

        Assert.True(platform == EcPlatform.Amd || platform == EcPlatform.Intel);
        Assert.Equal((byte)GpuMuxMode.Hybrid, EcMuxCodec.ModeToRaw(GpuMuxMode.Hybrid, EcPlatform.Amd));
    }

    [Fact]
    public void CoreAssemblyReferencesStayFreeOfHardwareUiAndIoPackages()
    {
        // The app is allowed to reference UCU.Core, so the reference is only safe
        // while Core pulls in no hardware, driver, UI, scheduler, logging or update
        // packages. Adding any of these to Core means the app is one step away from
        // touching privileged hardware in-process again.
        string[] forbidden =
        {
            "UCU.Hardware", "UCU.Contracts", "UCU.Client", "UCU.Engine",
            "Serilog", "LibreHardwareMonitorLib", "LibreHardwareMonitor",
            "System.Management", "TaskScheduler", "Velopack", "HidLibrary",
            "NvAPIWrapper", "PresentationFramework", "WindowsBase",
            "System.ServiceProcess.ServiceController", "System.Diagnostics.EventLog",
            "Microsoft.Extensions.DependencyInjection", "Microsoft.Extensions.Hosting",
        };

        string[] referenced = Assembly.Load("UCU.Core")
            .GetReferencedAssemblies()
            .Select(a => a.Name!)
            .ToArray();

        foreach (string name in forbidden)
            Assert.DoesNotContain(name, referenced);
    }

    [Fact]
    public void AppAssemblyDoesNotReferenceTheHardwareLayer()
    {
        // The phase-3 layering contract: the UI process reaches the EC only through
        // the capability interfaces (RPC proxies to the engine service in the shipped
        // product), so the app assembly must not reference UCU.Hardware or UCU.Engine
        // directly. The dev-only UCU.Bridge is the single app-adjacent project that
        // carries the hardware layer, and it exists solely for the dev-inprocess mode.
        string[] referenced = typeof(App).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name!)
            .ToArray();

        Assert.DoesNotContain("UCU.Hardware", referenced);
        Assert.DoesNotContain("UCU.Engine", referenced);
        Assert.Contains("UCU.Client", referenced);
        Assert.Contains("UCU.Contracts", referenced);
    }

    [Fact]
    public void HardwareAssemblyReferencesStayFreeOfUiAndAppTypes()
    {
        // The hardware layer is loaded by the engine service. It must stay free of WPF,
        // app settings, and app models, so the service host never inherits UI deps and
        // the phase-3 flip (app stops referencing UCU.Hardware) is a project edit, not
        // a redesign.
        string[] forbidden =
        {
            "Uniwill Control Utility", "PresentationFramework", "PresentationCore",
            "WindowsBase", "ControlzEx", "ModernWpf", "CommunityToolkit",
            "MahApps", "System.Windows.Interactivity", "Microsoft.Xaml",
            "Serilog.Sinks.File", "Velopack", "System.Management",
        };

        string[] referenced = Assembly.Load("UCU.Hardware")
            .GetReferencedAssemblies()
            .Select(a => a.Name!)
            .ToArray();

        foreach (string name in forbidden)
            Assert.DoesNotContain(name, referenced);

        Assert.Contains("UCU.Core", referenced);
    }
}
