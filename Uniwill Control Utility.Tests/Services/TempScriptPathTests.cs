namespace Uniwill_Control_Utility.Tests.Services;

/// <summary>
/// The one-shot cmd scripts written for the elevated uninstall verb and the
/// SYSTEM layout tail must not sit at a fixed %TEMP% name: any same-user
/// process could pre-place or swap the file between write and execution
/// (CWE-367) and it would run as admin / SYSTEM. Each run gets a fresh
/// private directory.
/// </summary>
public sealed class TempScriptPathTests
{
    [Fact]
    public void NewScriptPath_PutsFileInNewDirectoryUnderTemp()
    {
        string? directory = null;
        try
        {
            string path = UninstallSentinel.NewScriptPath("ucu-verb-task.cmd");
            directory = Path.GetDirectoryName(path);

            Assert.NotNull(directory);
            Assert.True(Directory.Exists(directory));
            // A child of %TEMP%, never %TEMP% itself.
            Assert.False(string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetTempPath()),
                directory,
                StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [Fact]
    public void NewScriptPath_TwoRunsGetDifferentDirectories()
    {
        string? first = null;
        string? second = null;
        try
        {
            first = Path.GetDirectoryName(UninstallSentinel.NewScriptPath("ucu-sentinel-verb.cmd"));
            second = Path.GetDirectoryName(UninstallSentinel.NewScriptPath("ucu-sentinel-verb.cmd"));

            Assert.NotNull(first);
            Assert.NotNull(second);
            Assert.NotEqual(first, second, StringComparer.OrdinalIgnoreCase);
            Assert.True(Directory.Exists(first));
            Assert.True(Directory.Exists(second));
        }
        finally
        {
            TryDelete(first);
            TryDelete(second);
        }
    }

    [Fact]
    public void NewScriptPath_KeepsTheRequestedFileName()
    {
        string? directory = null;
        try
        {
            string path = UninstallSentinel.NewScriptPath("ucu-verb-tail.cmd");
            directory = Path.GetDirectoryName(path);

            Assert.Equal("ucu-verb-tail.cmd", Path.GetFileName(path));
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [Fact]
    public void TheScriptsCloseTheirOwnHandleBeforeCleaningUp()
    {
        // Measured on Win11: a bare `del "%~f0"` deletes the RUNNING script, cmd
        // fails the next line ("The batch file cannot be found.") and the random
        // directory is never removed. (goto) closes the handle first, so del and rd
        // both run. Pin the idiom, not the comment around it.
        Assert.Contains("(goto) 2>nul & del", ReadSource("Uniwill Control Utility", "UninstallSentinel.cs"), StringComparison.Ordinal);
        Assert.Contains("(goto) 2>nul & del", ReadSource("UCU.Engine", "EngineInstaller.cs"), StringComparison.Ordinal);
    }

    private static string ReadSource(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { RepositoryRoot() }.Concat(parts).ToArray()));

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "Uniwill Control Utility")) &&
                Directory.Exists(Path.Combine(directory.FullName, "Uniwill Control Utility.Tests")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("repository root not found above " + AppContext.BaseDirectory);
    }

    private static void TryDelete(string? directory)
    {
        if (directory is null)
            return;

        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
