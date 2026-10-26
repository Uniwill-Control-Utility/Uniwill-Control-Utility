using System.Reflection;
using UCU.Core.Ec;
using Uniwill_Control_Utility.Models;
using Uniwill_Control_Utility.Scripts;
using Uniwill_Control_Utility.Services;
using Xunit;

namespace Uniwill_Control_Utility.Tests.Services;

/// <summary>
/// Pins the new UCU.Core encoders to the behaviour that ships today, so phase 0's
/// extraction is provably a move and not a rewrite. The private statics in
/// <see cref="UniwillECService"/> are the reference implementation; they are deleted
/// once the app consumes the engine instead of the EC directly.
/// </summary>
public sealed class EcCoreParityTests
{
    private static object? InvokeEc(string methodName, params object?[] args)
    {
        MethodInfo method = typeof(UniwillECService).GetMethod(methodName,
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!;
        return method.Invoke(null, args);
    }

    [Theory]
    [InlineData(new int[] { 0, 55, 60, 65, 70, 75, 80, 85, 90, 95, 97 })]
    [InlineData(new int[] { 0, 40, 50, 60, 70, 80, 90, 95 })]
    [InlineData(new int[] { 0, 300, 400 })]
    [InlineData(new int[] { 500, 55, 60, 65, 70, 75, 80, 85, 90, 95, 97, 99, 101 })]
    [InlineData(new int[] { 0 })]
    public void TemperatureUp_MatchesTheAppEncoder(int[] temps)
    {
        byte[] expected = (byte[])InvokeEc("ToEcTempUp", temps)!;

        Assert.Equal(expected, EcTemperatureCodec.ToEcTempUp(temps));
    }

    [Theory]
    [InlineData(new int[] { 0, 55, 60, 65, 70, 75, 80, 85, 90, 95, 97 })]
    [InlineData(new int[] { 0, 1, 2, 3 })]
    [InlineData(new int[] { 1, 55 })]
    [InlineData(new int[] { 0, 300, 400 })]
    [InlineData(new int[] { 500, 55, 60, 65, 70, 75, 80, 85, 90, 95, 97, 99, 101 })]
    public void TemperatureDown_MatchesTheAppEncoder(int[] temps)
    {
        byte[] expected = (byte[])InvokeEc("ToEcTempDown", temps)!;

        Assert.Equal(expected, EcTemperatureCodec.ToEcTempDown(temps));
    }

    [Theory]
    [InlineData(180)]
    [InlineData(120)]
    [InlineData(90)]
    [InlineData(60)]
    [InlineData(0)]
    [InlineData(200)]
    public void ScaleDutyGradient_MatchesTheAppEncoder(byte targetMax)
    {
        byte[] oemGradient = { 0, 20, 40, 60, 80, 100, 120, 140, 160, 180 };

        byte[] expected = (byte[])InvokeEc("ScaleDutyGradient", oemGradient, targetMax)!;

        Assert.Equal(expected, EcDutyCodec.ScaleDutyGradient(oemGradient, targetMax));
    }

    [Theory]
    [InlineData(100)]
    [InlineData(17)]
    public void ScaleDutyGradient_MatchesOnAFlatGradient(byte targetMax)
    {
        byte[] flatGradient = { 0, 180, 1, 1, 1, 1, 1, 1, 1, 180 };

        byte[] expected = (byte[])InvokeEc("ScaleDutyGradient", flatGradient, targetMax)!;

        Assert.Equal(expected, EcDutyCodec.ScaleDutyGradient(flatGradient, targetMax));
    }

    [Fact]
    public void DutyTable_MatchesTheFanCurveModel()
    {
        EcFanCurve[] curves =
        {
            EcFanCurve.CreateSilent(),
            EcFanCurve.CreateBalanced(),
            EcFanCurve.CreatePerformance(),
            EcFanCurve.CreateFullSpeed(),
            EcFanCurve.CreateOff(),
        };

        foreach (EcFanCurve curve in curves)
        {
            Assert.Equal(curve.ToEcDutyTable(), EcDutyCodec.BuildDutyTable(curve.Duties));
        }
    }

    [Fact]
    public void DutyTable_MatchesOnPartialAndOverRangeCurves()
    {
        var shortCurve = new EcFanCurve { Name = "Short", Duties = new List<int> { 0, 50 } };
        var overRange = new EcFanCurve { Name = "Over", Duties = new List<int> { 0, 100, 100, 100, 100, 100, 100, 100, 100, 100, 100 } };

        Assert.Equal(shortCurve.ToEcDutyTable(), EcDutyCodec.BuildDutyTable(shortCurve.Duties));
        Assert.Equal(overRange.ToEcDutyTable(), EcDutyCodec.BuildDutyTable(overRange.Duties));
    }

    /// <summary>
    /// The app's MUX helpers read the platform from global CPU detection, which is
    /// unresolved in a unit-test process; the Core codec takes it as an argument.
    /// Parity is asserted on the AMD mapping, which is what the app's unresolved
    /// global resolves to, and the Intel mapping is covered directly in Core.
    /// </summary>
    [Fact]
    public void MuxEncoding_MatchesTheAppEncoderOnAmd()
    {
        // The app reads the platform from global CPU detection, which is unresolved
        // (=> the AMD mapping) in a unit-test process. If a run ever resolves Intel
        // here, the AMD parity is not the right comparison and the Core Intel tests
        // cover that mapping directly.
        if (Family.TYPE == Family.ProcessorType.Intel)
            return;

        foreach (UniwillECService.GpuMuxMode appMode in Enum.GetValues<UniwillECService.GpuMuxMode>())
        {
            var coreMode = (GpuMuxMode)(int)appMode;
            byte expected = UniwillECService.ModeToRaw(appMode);

            Assert.Equal(expected, EcMuxCodec.ModeToRaw(coreMode, EcPlatform.Amd));
            Assert.Equal((int)UniwillECService.RawToMode(expected), (int)EcMuxCodec.RawToMode(expected, EcPlatform.Amd));
        }
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
}
