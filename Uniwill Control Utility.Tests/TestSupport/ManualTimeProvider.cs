using System;

namespace Uniwill_Control_Utility.Tests;

/// <summary>
/// Shared fake clock for cache-freshness tests: time advances only when the
/// test says so, so staleness budgets are asserted deterministically instead
/// of by sleeping (the app-suite clock budget is the engine-metrics freshness
/// gate, the snapshot max age, and every stale-while-revalidate revalidation).
/// </summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _utcNow;

    public ManualTimeProvider(DateTimeOffset utcNow)
    {
        _utcNow = utcNow;
    }

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void Advance(TimeSpan amount) => _utcNow += amount;
}
