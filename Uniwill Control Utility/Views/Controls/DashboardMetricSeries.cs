using System;
using System.Collections.Generic;

namespace Uniwill_Control_Utility.Views.Controls;

internal sealed class DashboardMetricSeries
{
    private readonly int _capacity;
    private readonly TimeSpan _maximumSampleInterval;
    private readonly List<double> _samples = new();
    private DateTimeOffset? _lastSampleAt;

    public DashboardMetricSeries(int capacity, TimeSpan? maximumSampleInterval = null)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));

        _capacity = capacity;
        _maximumSampleInterval = maximumSampleInterval ?? TimeSpan.FromSeconds(3);
        if (_maximumSampleInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maximumSampleInterval));
    }

    public IReadOnlyList<double> Samples => _samples;

    public void Add(double value)
        => Add(value, DateTimeOffset.UtcNow);

    internal void Add(double value, DateTimeOffset sampledAt)
    {
        if (_lastSampleAt.HasValue && sampledAt - _lastSampleAt.Value > _maximumSampleInterval)
            _samples.Clear();

        _lastSampleAt = sampledAt;
        _samples.Add(double.IsFinite(value) && value > 0 ? value : double.NaN);
        if (_samples.Count > _capacity)
            _samples.RemoveAt(0);
    }
}
