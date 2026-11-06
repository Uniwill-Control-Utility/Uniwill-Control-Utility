namespace Uniwill_Control_Utility.Tests.UI;

/// <summary>
/// Dashboard card icons are external glyphs (FontFamily "Segoe Fluent Icons" +
/// an explicit Glyph), never WPF-UI's `ui:SymbolIcon`: that control's font
/// fallback renders tofu on machines without the fluent icon font. Each entry
/// below is a card and the glyph it shows.
///
/// The Flydigi card is not listed - its header carries no icon (text only), so
/// it has no glyph to assert. The rule it must still obey is the "no
/// ui:SymbolIcon" assertion below, which runs over every dashboard card.
/// </summary>
public sealed class DashboardExternalFluentIconTests
{
    private static readonly string[] DashboardCards =
    [
        "CpuControl.xaml", "GpuControl.xaml", "MemoryControl.xaml",
        "BatteryControl.xaml", "HydroUiControl.xaml", "FlydigiControl.xaml",
    ];

    [Theory]
    [InlineData("CpuControl.xaml", "EEA1")]
    [InlineData("GpuControl.xaml", "F211")]
    [InlineData("MemoryControl.xaml", "EEA0")]
    [InlineData("BatteryControl.xaml", "EBC0")]
    [InlineData("HydroUiControl.xaml", "EB42")]
    public void DashboardCard_UsesExternalSegoeFluentIcon(string fileName, string glyph)
    {
        var xaml = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Uniwill Control Utility", "Views", "Controls", fileName));

        Assert.DoesNotContain("<ui:SymbolIcon", xaml, StringComparison.Ordinal);
        Assert.Contains("FontFamily=\"Segoe Fluent Icons\"", xaml, StringComparison.Ordinal);
        Assert.Contains($"Glyph=\"&#x{glyph};\"", xaml, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(DashboardCardNames))]
    public void DashboardCard_NeverUsesTheSymbolIconControl(string fileName)
    {
        var xaml = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Uniwill Control Utility", "Views", "Controls", fileName));

        Assert.DoesNotContain("<ui:SymbolIcon", xaml, StringComparison.Ordinal);
    }

    public static TheoryData<string> DashboardCardNames() => new(DashboardCards);

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
