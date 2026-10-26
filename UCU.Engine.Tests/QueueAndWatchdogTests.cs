using Microsoft.Extensions.Logging.Abstractions;
using UCU.Contracts;

namespace UCU.Engine.Tests;

/// <summary>
/// The queueing guarantees the design rests on: FIFO order per domain, a hard depth
/// limit that answers Busy instead of growing, a caller timeout that releases the
/// client without cancelling the hardware write, and the watchdog that terminates a
/// process parked on a wedged call.
/// </summary>
public sealed class QueueAndWatchdogTests
{
    [Fact]
    public async Task RunAsync_ExecutesFifoInSingleFileOrder()
    {
        var options = new EngineRuntimeOptions();
        await using var queue = new SerialExecutor("test", options);

        var order = new List<int>();
        Task<int>[] tasks = Enumerable.Range(0, 20)
            .Select(i => queue.RunAsync($"item{i}", () => { lock (order) order.Add(i); return i; }, CancellationToken.None))
            .ToArray();

        int[] results = await Task.WhenAll(tasks);

        Assert.Equal(Enumerable.Range(0, 20), results);
        Assert.Equal(Enumerable.Range(0, 20), order);
    }

    [Fact]
    public async Task SaturatedQueueAnswersBusy_WithoutUnboundedGrowth()
    {
        var options = new EngineRuntimeOptions { MaxQueuedCallsPerDomain = 2 };
        await using var queue = new SerialExecutor("test", options);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task blocker = queue.RunAsync("blocker", () => gate.Task.GetAwaiter().GetResult(), CancellationToken.None);

        // The blocker is picked up (running, not queued); then exactly two calls fit.
        await Task.Delay(50);
        Task<int> second = queue.RunAsync("second", () => 1, CancellationToken.None);
        Task<int> third = queue.RunAsync("third", () => 2, CancellationToken.None);

        EngineException ex = await Assert.ThrowsAsync<EngineException>(
            () => queue.RunAsync("fourth", () => 3, CancellationToken.None));

        Assert.Equal(EngineErrorCode.Busy, ex.Code);

        gate.SetResult();
        await blocker;
        Assert.Equal(1, await second);
        Assert.Equal(2, await third);
    }

    [Fact]
    public async Task CallerBudgetReleasesTheClient_ButTheWorkFinishes()
    {
        var options = new EngineRuntimeOptions { HardwareCallBudget = TimeSpan.FromMilliseconds(100) };
        await using var queue = new SerialExecutor("test", options);

        int completed = 0;
        EngineException ex = await Assert.ThrowsAsync<EngineException>(() => queue.RunAsync("slow", () =>
        {
            Thread.Sleep(400);
            Interlocked.Increment(ref completed);
            return 0;
        }, CancellationToken.None));

        Assert.Equal(EngineErrorCode.Timeout, ex.Code);
        Assert.Equal(0, completed);

        // A timed-out caller does not cancel the hardware write; the queue drains it
        // (order preserved, register not abandoned half-written) and stays usable.
        await WaitUntilAsync(() => Volatile.Read(ref completed) == 1, TimeSpan.FromSeconds(2));
        Assert.Equal(7, await queue.RunAsync("next", () => 7, CancellationToken.None));
    }

    [Fact]
    public async Task Watchdog_TerminatesOnACallParkedPastTheThreshold()
    {
        var options = new EngineRuntimeOptions { StuckCallThreshold = TimeSpan.FromMilliseconds(100) };
        await using var queue = new SerialExecutor("fan", options);
        var blocked = new ManualResetEventSlim();

        string? terminated = null;
        var watchdog = new EngineWatchdog(
            new Dictionary<string, SerialExecutor> { ["fan"] = queue },
            options,
            NullLogger<EngineWatchdog>.Instance,
            message => terminated = message);

        Task<bool> work = queue.RunAsync("wedged-io", () => blocked.Wait(TimeSpan.FromSeconds(5)), CancellationToken.None);

        // Park past the threshold, then check: the watchdog must name the domain and call.
        await Task.Delay(300);
        watchdog.Check(CancellationToken.None);

        Assert.NotNull(terminated);
        Assert.Contains("fan", terminated);
        Assert.Contains("wedged-io", terminated);

        blocked.Set();
        work.GetAwaiter().GetResult();
    }

    [Fact]
    public void Watchdog_StaysSilentWhenQueuesAreHealthy()
    {
        var options = new EngineRuntimeOptions { StuckCallThreshold = TimeSpan.FromMinutes(2) };
        using var cts = new CancellationTokenSource();
        var queue = new SerialExecutor("fan", options);

        string? terminated = null;
        var watchdog = new EngineWatchdog(
            new Dictionary<string, SerialExecutor> { ["fan"] = queue },
            options,
            NullLogger<EngineWatchdog>.Instance,
            message => terminated = message);

        watchdog.Check(CancellationToken.None);

        Assert.Null(terminated);
        queue.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    [Fact]
    public async Task ShutdownAnswersQueuedCallsBusy_NotHanging()
    {
        var options = new EngineRuntimeOptions();
        var queue = new SerialExecutor("fan", options);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task blocker = queue.RunAsync("blocker", () => gate.Task.GetAwaiter().GetResult(), CancellationToken.None);
        await Task.Delay(50);
        Task<int> queued = queue.RunAsync("queued", () => 1, CancellationToken.None);

        await queue.DisposeAsync();
        gate.SetResult();

        // The in-flight blocker completes; anything that never ran is answered Busy,
        // so a service shutdown never leaves clients hanging until their own timeout.
        await blocker;
        EngineException ex = await Assert.ThrowsAsync<EngineException>(() => queued);
        Assert.Equal(EngineErrorCode.Busy, ex.Code);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("condition never became true");
            await Task.Delay(20);
        }
    }
}
