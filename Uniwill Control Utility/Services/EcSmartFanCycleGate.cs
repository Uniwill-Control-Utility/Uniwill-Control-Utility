using System;
using System.Threading;

namespace Uniwill_Control_Utility.Services;

/// <summary>
/// Serializes smart-fan timer callbacks, enforces the minimum cycle period,
/// and temporarily suppresses EC traffic after repeated write failures.
/// </summary>
public sealed class EcSmartFanCycleGate
{
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan DefaultFailurePause = TimeSpan.FromSeconds(5);
    private const int DefaultFailureThreshold = 3;

    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _failurePause;
    private readonly int _failureThreshold;
    private int _isRunning;
    private int _consecutiveFailures;
    private long _notBeforeUtcTicks;

    public EcSmartFanCycleGate()
        : this(DefaultPollInterval, DefaultFailureThreshold, DefaultFailurePause)
    {
    }

    public TimeSpan PollInterval => _pollInterval;

    internal EcSmartFanCycleGate(TimeSpan pollInterval, int failureThreshold, TimeSpan failurePause)
    {
        _pollInterval = pollInterval;
        _failureThreshold = failureThreshold;
        _failurePause = failurePause;
    }

    public bool TryEnter(DateTimeOffset now)
    {
        if (now.UtcTicks < Volatile.Read(ref _notBeforeUtcTicks))
            return false;

        if (Interlocked.CompareExchange(ref _isRunning, 1, 0) != 0)
            return false;

        if (now.UtcTicks < Volatile.Read(ref _notBeforeUtcTicks))
        {
            Interlocked.Exchange(ref _isRunning, 0);
            return false;
        }

        Volatile.Write(ref _notBeforeUtcTicks, now.Add(_pollInterval).UtcTicks);
        return true;
    }

    public void CompleteSuccess()
    {
        Interlocked.Exchange(ref _consecutiveFailures, 0);
        Interlocked.Exchange(ref _isRunning, 0);
    }

    public void CompleteWithoutWrite()
    {
        Interlocked.Exchange(ref _isRunning, 0);
    }

    public void CompleteFailure(DateTimeOffset now)
    {
        int failures = Interlocked.Increment(ref _consecutiveFailures);
        if (failures >= _failureThreshold)
        {
            Volatile.Write(ref _notBeforeUtcTicks, now.Add(_failurePause).UtcTicks);
            Interlocked.Exchange(ref _consecutiveFailures, 0);
        }

        Interlocked.Exchange(ref _isRunning, 0);
    }
}
