using System;
using System.Threading;
using System.Threading.Tasks;
using Uniwill_Control_Utility.Services;

namespace Uniwill_Control_Utility.Tests.Services;

public sealed class StaleWhileRevalidateCacheTests
{
    [Fact]
    public async Task Read_returns_cached_value_while_one_refresh_runs_in_background()
    {
        var refreshStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int refreshCount = 0;

        using var cache = new StaleWhileRevalidateCache<int>(
            initialValue: 7,
            maxAge: TimeSpan.FromSeconds(1),
            refresh: async cancellationToken =>
            {
                Interlocked.Increment(ref refreshCount);
                refreshStarted.SetResult();
                await releaseRefresh.Task.WaitAsync(cancellationToken);
                return 42;
            });

        Assert.Equal(7, cache.Read());
        await refreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(7, cache.Read());
        Assert.Equal(1, Volatile.Read(ref refreshCount));

        releaseRefresh.SetResult();
        await cache.RefreshAsync();

        Assert.Equal(42, cache.Read());
        Assert.Equal(1, Volatile.Read(ref refreshCount));
    }

    [Fact]
    public async Task Fresh_value_is_reused_until_the_cache_expires()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-09-27T00:00:00Z"));
        int refreshCount = 0;

        using var cache = new StaleWhileRevalidateCache<int>(
            initialValue: 0,
            maxAge: TimeSpan.FromSeconds(1),
            refresh: _ => Task.FromResult(Interlocked.Increment(ref refreshCount)),
            timeProvider: clock);

        await cache.RefreshAsync();
        Assert.Equal(1, cache.Read());
        Assert.Equal(1, Volatile.Read(ref refreshCount));

        clock.Advance(TimeSpan.FromMilliseconds(999));
        Assert.Equal(1, cache.Read());
        Assert.Equal(1, Volatile.Read(ref refreshCount));

        clock.Advance(TimeSpan.FromMilliseconds(2));
        await cache.RefreshAsync();
        Assert.Equal(2, cache.Read());
        Assert.Equal(2, Volatile.Read(ref refreshCount));
    }

    [Fact]
    public async Task Failed_refresh_keeps_last_good_value_and_can_retry()
    {
        int refreshCount = 0;

        using var cache = new StaleWhileRevalidateCache<int>(
            initialValue: 11,
            maxAge: TimeSpan.FromSeconds(1),
            refresh: _ =>
            {
                int attempt = Interlocked.Increment(ref refreshCount);
                return attempt == 1
                    ? Task.FromException<int>(new InvalidOperationException("transient"))
                    : Task.FromResult(23);
            });

        await cache.RefreshAsync();
        Assert.Equal(11, cache.Read());

        await cache.RefreshAsync();
        Assert.Equal(23, cache.Read());
        Assert.Equal(2, Volatile.Read(ref refreshCount));
    }

    [Fact]
    public async Task Invalidation_during_refresh_discards_the_obsolete_result()
    {
        var firstRefreshStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int refreshCount = 0;

        using var cache = new StaleWhileRevalidateCache<int>(
            initialValue: 0,
            maxAge: TimeSpan.FromMinutes(1),
            refresh: async cancellationToken =>
            {
                int attempt = Interlocked.Increment(ref refreshCount);
                if (attempt == 1)
                {
                    firstRefreshStarted.SetResult();
                    await releaseFirstRefresh.Task.WaitAsync(cancellationToken);
                }

                return attempt;
            });

        Task obsoleteRefresh = cache.RefreshAsync();
        await firstRefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cache.Invalidate();
        releaseFirstRefresh.SetResult();
        await obsoleteRefresh;

        Assert.Equal(0, cache.Read());
        await cache.RefreshAsync();
        Assert.Equal(2, cache.Read());
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;

        public ManualTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan amount) => _utcNow += amount;
    }
}
