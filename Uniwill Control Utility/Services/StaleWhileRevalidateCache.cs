using System;
using System.Threading;
using System.Threading.Tasks;

namespace Uniwill_Control_Utility.Services
{
    /// <summary>
    /// Returns the last completed value immediately and refreshes stale data once in
    /// the background. Concurrent readers share the same refresh operation.
    /// </summary>
    internal sealed class StaleWhileRevalidateCache<T> : IDisposable
    {
        private readonly object _refreshSync = new();
        private readonly TimeSpan _maxAge;
        private readonly Func<CancellationToken, Task<T>> _refresh;
        private readonly TimeProvider _timeProvider;
        private readonly Action<Exception>? _onRefreshError;
        private readonly CancellationTokenSource _disposeToken = new();
        private CacheEntry _entry;
        private Task? _refreshTask;
        private long _generation;
        private bool _disposed;

        public StaleWhileRevalidateCache(
            T initialValue,
            TimeSpan maxAge,
            Func<CancellationToken, Task<T>> refresh,
            TimeProvider? timeProvider = null,
            Action<Exception>? onRefreshError = null)
        {
            if (maxAge <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(maxAge));

            _maxAge = maxAge;
            _refresh = refresh ?? throw new ArgumentNullException(nameof(refresh));
            _timeProvider = timeProvider ?? TimeProvider.System;
            _onRefreshError = onRefreshError;
            _entry = new CacheEntry(initialValue, DateTimeOffset.MinValue);
        }

        public T Read()
        {
            ThrowIfDisposed();

            CacheEntry entry = Volatile.Read(ref _entry);
            if (_timeProvider.GetUtcNow() - entry.RefreshedAt >= _maxAge)
                _ = RefreshAsync();

            return entry.Value;
        }

        public Task RefreshAsync()
        {
            lock (_refreshSync)
            {
                ThrowIfDisposed();
                if (_refreshTask is not null)
                    return _refreshTask;

                CacheEntry entry = Volatile.Read(ref _entry);
                if (_timeProvider.GetUtcNow() - entry.RefreshedAt < _maxAge)
                    return Task.CompletedTask;

                Task refreshTask = RunRefreshAsync();
                _refreshTask = refreshTask;
                _ = refreshTask.ContinueWith(
                    completedTask => ClearRefresh(completedTask),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                return refreshTask;
            }
        }

        public void Invalidate()
        {
            lock (_refreshSync)
            {
                ThrowIfDisposed();
                _generation++;
                CacheEntry entry = Volatile.Read(ref _entry);
                Volatile.Write(ref _entry, new CacheEntry(entry.Value, DateTimeOffset.MinValue));
            }
        }

        private async Task RunRefreshAsync()
        {
            long generation;
            lock (_refreshSync)
                generation = _generation;

            try
            {
                T value = await Task.Run(
                    () => _refresh(_disposeToken.Token),
                    _disposeToken.Token).ConfigureAwait(false);

                lock (_refreshSync)
                {
                    if (!_disposed && generation == _generation)
                    {
                        Volatile.Write(
                            ref _entry,
                            new CacheEntry(value, _timeProvider.GetUtcNow()));
                    }
                }
            }
            catch (OperationCanceledException) when (_disposeToken.IsCancellationRequested)
            {
                // Disposal stops an in-flight refresh without replacing the last value.
            }
            catch (Exception ex)
            {
                try { _onRefreshError?.Invoke(ex); }
                catch { /* A diagnostic callback must not fault the refresh task. */ }
            }
        }

        private void ClearRefresh(Task completedTask)
        {
            lock (_refreshSync)
            {
                if (ReferenceEquals(_refreshTask, completedTask))
                    _refreshTask = null;
            }
        }

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }

        public void Dispose()
        {
            lock (_refreshSync)
            {
                if (_disposed)
                    return;

                _disposed = true;
                _disposeToken.Cancel();
            }

            _disposeToken.Dispose();
        }

        private sealed class CacheEntry
        {
            public CacheEntry(T value, DateTimeOffset refreshedAt)
            {
                Value = value;
                RefreshedAt = refreshedAt;
            }

            public T Value { get; }
            public DateTimeOffset RefreshedAt { get; }
        }
    }
}
