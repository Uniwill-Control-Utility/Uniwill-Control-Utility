namespace Uniwill_Control_Utility.Tests.UI;

public sealed class TrayWidgetFullFanInteractionTests
{
    [Fact]
    public void FullFan_UsesOneExplicitClickCommandInsteadOfStateChangeEvents()
    {
        string xaml = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "Uniwill Control Utility",
            "Views",
            "Windows",
            "TrayWidgetWindow.xaml"));

        Assert.Contains("x:Name=\"FullFanButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"FullFanButton_Click\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Checked=\"FullFan", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Unchecked=\"FullFan", xaml, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "Uniwill Control Utility")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
