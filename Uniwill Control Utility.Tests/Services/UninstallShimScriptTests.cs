using System;
using System.IO;

namespace Uniwill_Control_Utility.Tests.Services;

/// <summary>
/// Source pins for the Control Panel shim's one-shot SYSTEM task ("UCU Uninstall Verb").
///
/// The shim is compiled at pack time, so nothing else covers it, and the field found both
/// halves broken at once (2026-11-15): the task was created for an engine exe the msiexec
/// pass above had already deleted - so it could never run its sweep - and nothing ever
/// deleted the definition, leaving a task in Task Scheduler that the user removed by hand.
/// </summary>
public sealed class UninstallShimScriptTests
{
    private static string ShimSource([System.Runtime.CompilerServices.CallerFilePath] string source = "") =>
        File.ReadAllText(Path.Combine(
            Path.GetDirectoryName(source)!, "..", "..", "build", "uninstall-shim", "UninstallShim.cs"));

    [Fact]
    public void TheVerbTaskIsOnlyCreatedWhenItsExeStillExists()
    {
        string shim = ShimSource();

        // Both script lines carry the guard: the msiexec pass above deletes the engine exe
        // on a plain machine, and a task created then can never run its sweep.
        Assert.Contains("if exist \\\"", shim, StringComparison.Ordinal);
        Assert.Contains("schtasks /create /tn \\\"UCU Uninstall Verb\\\"", shim, StringComparison.Ordinal);
        Assert.Contains("schtasks /run /tn \\\"UCU Uninstall Verb\\\"", shim, StringComparison.Ordinal);
    }

    [Fact]
    public void TheVerbTaskIsDeletedWhateverHappens()
    {
        string shim = ShimSource();

        Assert.Contains(
            "schtasks /delete /tn \\\"UCU Uninstall Verb\\\" /f", shim, StringComparison.Ordinal);
    }
}
