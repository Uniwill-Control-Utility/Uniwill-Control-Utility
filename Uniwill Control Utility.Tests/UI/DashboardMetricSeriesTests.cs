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
    public void AddRepresentsUnavailableTemperatureReadingsAsTimelineGaps()
    {
        var series = new DashboardMetricSeries(capacity: 5);

        series.Add(38);
        series.Add(0);
        series.Add(-1);
        series.Add(double.NaN);
        series.Add(double.PositiveInfinity);

        Assert.Equal(5, series.Samples.Count);
        Assert.Equal(38d, series.Samples[0]);
        Assert.All(series.Samples.Skip(1), sample => Assert.True(double.IsNaN(sample)));
    }

    [Fact]
    public void UnavailableReadingsAdvanceTheBoundedTimeline()
    {
        var series = new DashboardMetricSeries(capacity: 3);

        series.Add(38);
        series.Add(0);
        series.Add(0);
        series.Add(0);

        Assert.Equal(3, series.Samples.Count);
        Assert.All(series.Samples, sample => Assert.True(double.IsNaN(sample)));
    }
}
