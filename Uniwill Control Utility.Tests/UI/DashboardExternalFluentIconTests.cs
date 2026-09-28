namespace Uniwill_Control_Utility.Tests.UI;

public sealed class DashboardExternalFluentIconTests
{
    [Theory]
    [InlineData("CpuControl.xaml", "EEA1")]
    [InlineData("GpuControl.xaml", "F211")]
    [InlineData("MemoryControl.xaml", "EEA0")]
    [InlineData("BatteryControl.xaml", "EBC0")]
    [InlineData("HydroUiControl.xaml", "EB42")]
    [InlineData("FlydigiControl.xaml", "EC4A")]
    public void DashboardCard_UsesExternalSegoeFluentIcon(string fileName, string glyph)
    {
        var xaml = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Uniwill Control Utility", "Views", "Controls", fileName));

        Assert.DoesNotContain("<ui:SymbolIcon", xaml, StringComparison.Ordinal);
        Assert.Contains("FontFamily=\"Segoe Fluent Icons\"", xaml, StringComparison.Ordinal);
        Assert.Contains($"Glyph=\"&#x{glyph};\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void BatteryHealth_UsesExternalSegoeFluentHeartIcon()
    {
        var xaml = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Uniwill Control Utility", "Views", "Controls", "BatteryControl.xaml"));

        Assert.Contains("Glyph=\"&#xEB51;\"", xaml, StringComparison.Ordinal);
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
