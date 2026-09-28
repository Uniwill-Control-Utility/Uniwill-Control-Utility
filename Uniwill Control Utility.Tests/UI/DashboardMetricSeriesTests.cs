using Uniwill_Control_Utility.Views.Controls;

namespace Uniwill_Control_Utility.Tests.UI;

public sealed class DashboardMetricSeriesTests
{
    [Fact]
    public void AddRetainsOnlyTheMostRecentSamples()
    {
        var series = new DashboardMetricSeries(capacity: 3);

        series.Add(34);
        series.Add(35);
        series.Add(36);
        series.Add(37);

        Assert.Equal([35d, 36d, 37d], series.Samples);
    }

    [Fact]
    public void AddSkipsUnavailableTemperatureReadings()
    {
        var series = new DashboardMetricSeries(capacity: 3);

        series.Add(38);
        series.Add(0);
        series.Add(-1);

        Assert.Equal([38d], series.Samples);
    }
}
