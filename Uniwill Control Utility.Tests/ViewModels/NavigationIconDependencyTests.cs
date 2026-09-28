namespace Uniwill_Control_Utility.Tests.ViewModels;

public sealed class NavigationIconDependencyTests
{
    [Fact]
    public void Navigation_refresh_preserves_external_segoe_fluent_icons()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "Uniwill Control Utility",
            "ViewModels",
            "MainWindowViewModel.cs"));

        Assert.Contains("new System.Windows.Media.FontFamily(\"Segoe Fluent Icons\")", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new SymbolIcon", source, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "Uniwill Control Utility")) &&
                Directory.Exists(Path.Combine(directory.FullName, "Uniwill Control Utility.Tests")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
