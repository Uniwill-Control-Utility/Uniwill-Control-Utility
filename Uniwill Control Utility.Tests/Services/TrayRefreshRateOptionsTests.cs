using Uniwill_Control_Utility.Services;

namespace Uniwill_Control_Utility.Tests.Services;

public sealed class TrayRefreshRateOptionsTests
{
    [Fact]
    public void Build_KeepsCurrentRefreshRateWhenEnumerationReturnsNothing()
    {
        int[] options = TrayRefreshRateOptions.Build([], currentRefreshRate: 165);

        Assert.Equal([165], options);
    }

    [Fact]
    public void Build_DeduplicatesAndSortsRatesWithCurrentIncluded()
    {
        int[] options = TrayRefreshRateOptions.Build([60, 165, 60], currentRefreshRate: 240);

        Assert.Equal([240, 165, 60], options);
    }
}
