using Uniwill_Control_Utility.Views.Controls;

namespace Uniwill_Control_Utility.Tests.UI;

public sealed class DashboardMemoryInfoFormatterTests
{
    [Theory]
    [InlineData("SK hynix", "  HMCG88AGBSA095N  ", "SK hynix · HMCG88AGBSA095N")]
    [InlineData("", "HMCG88AGBSA095N", "HMCG88AGBSA095N")]
    [InlineData("Crucial", "Crucial CT16G56C46S5", "Crucial CT16G56C46S5")]
    [InlineData("Kingston/Crucial", "Kingston KF564S38-16/Crucial CT16G56C46S5", "Kingston KF564S38-16 / Crucial CT16G56C46S5")]
    [InlineData("Kingston/Crucial", "KF564S38-16/CT16G56C46S5", "Kingston KF564S38-16 / Crucial CT16G56C46S5")]
    [InlineData("", "", "Not available")]
    public void FormatModelProducesACompactModuleIdentity(
        string manufacturer,
        string model,
        string expected)
    {
        string actual = DashboardMemoryInfoFormatter.FormatModel(manufacturer, model);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(7200, "7200 MT/s")]
    [InlineData(0, "Not available")]
    public void FormatSpeedUsesEffectiveTransferRate(int speed, string expected)
    {
        Assert.Equal(expected, DashboardMemoryInfoFormatter.FormatSpeed(speed));
    }
}
