using System;
using System.Collections.Generic;

namespace Uniwill_Control_Utility.Views.Controls;

internal sealed class DashboardMetricSeries
{
    private readonly int _capacity;
    private readonly List<double> _samples = new();

    public DashboardMetricSeries(int capacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));

        _capacity = capacity;
    }

    public IReadOnlyList<double> Samples => _samples;

    public void Add(double value)
    {
        _samples.Add(double.IsFinite(value) && value > 0 ? value : double.NaN);
        if (_samples.Count > _capacity)
            _samples.RemoveAt(0);
    }
}
