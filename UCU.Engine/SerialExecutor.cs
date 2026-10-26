using System.Threading.Channels;
using UCU.Contracts;

namespace UCU.Engine
{
    /// <summary>
    /// A single-threaded FIFO queue for one hardware domain.
    ///
    /// Two properties matter to the EC and neither survives a thread pool: writes that
    /// touch the same registers must stay in the order the caller issued them (a fan
    /// curve is "table, then SPLIT, then toggle ENABLE_CUSTOM" - a write interleaved
    /// from another page leaves the EC half-configured), and the delays the firmware
    /// needs must elapse without a thread pool thread being parked on them.
    ///
    /// One queue per domain, so a multi-second curve upload cannot sit in front of a
    /// tray temperature read. The lock inside <c>EcEngine</c> still serialises the
    /// hardware itself; these queues provide fairness, a depth limit, and a timeout for
    /// the caller without cancelling a hardware write that cannot be cancelled.
    /// </summary>
    public sealed class SerialExecutor : IAsyncDisposable
    {
        private sealed class WorkItem
        {
            public WorkItem(string label, Func<object?> work)
            {
                Label = label;
                Work = work;
                Completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
                EnqueuedUtc = DateTime.UtcNow;
            }

            public string Label { get; }

            public Func<object?> Work { get; }

            public TaskCompletionSource<object?> Completion { get; }

            public DateTime EnqueuedUtc { get; }
        }

        private readonly Channel<WorkItem> _queue = Channel.CreateUnbounded<WorkItem>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

        private readonly EngineRuntimeOptions _options;
        private readonly Task _worker;
        private int _queued;
        private int _disposed;
        private DateTime? _currentStartedUtc;
        private string? _currentLabel;

        public SerialExecutor(string name, EngineRuntimeOptions options)
        {
            Name = name;
            _options = options;
            _worker = Task.Run(RunLoopAsync);
        }

        public string Name { get; }

        public int QueuedCount => Volatile.Read(ref _queued);

        /// <summary>Label of the call currently on the hardware, for logs and the watchdog.</summary>
        public string? CurrentCall => _currentLabel;

        /// <summary>How long the in-flight call has been running; zero when idle.</summary>
        public TimeSpan CurrentCallAge
        {
            get
            {
                DateTime? started = _currentStartedUtc;
                return started == null ? TimeSpan.Zero : DateTime.UtcNow - started.Value;
            }
        }

        /// <summary>Age of the work at the head: the in-flight call, or "waiting" when queued.</summary>
        public TimeSpan HeadAge => CurrentCallAge > TimeSpan.Zero
            ? CurrentCallAge
            : Volatile.Read(ref _queued) > 0 ? TimeSpan.FromTicks(1) : TimeSpan.Zero;

        public Task<T> RunAsync<T>(string label, Func<T> work, CancellationToken cancellationToken) =>
            AwaitResultAsync<T>(label, Enqueue(label, () => work()), cancellationToken);

        public Task RunAsync(string label, Action work, CancellationToken cancellationToken) =>
            AwaitResultAsync<object?>(label, Enqueue(label, () =>
            {
                work();
                return null;
            }), cancellationToken);

        public async ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _disposed, 1);
            _queue.Writer.TryComplete();

            await Task.WhenAny(_worker, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);

            // Anything still queued when the engine stops is answered, not left hanging.
            while (_queue.Reader.TryRead(out WorkItem? item))
                item.Completion.TrySetException(new EngineException(EngineErrorCode.Busy, $"{Name} is shutting down"));
        }

        private TaskCompletionSource<object?> Enqueue(string label, Func<object?> work)
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new EngineException(EngineErrorCode.Busy, $"{Name} is shutting down");

            if (Volatile.Read(ref _queued) >= _options.MaxQueuedCallsPerDomain)
                throw new EngineException(EngineErrorCode.Busy, $"{Name} queue is saturated ({_options.MaxQueuedCallsPerDomain} calls)");

            var item = new WorkItem(label, work);
            Interlocked.Increment(ref _queued);

            if (!_queue.Writer.TryWrite(item))
            {
                Interlocked.Decrement(ref _queued);
                throw new EngineException(EngineErrorCode.Busy, $"{Name} is shutting down");
            }

            return item.Completion;
        }

        private async Task<T> AwaitResultAsync<T>(string label, TaskCompletionSource<object?> completion, CancellationToken cancellationToken)
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(_options.HardwareCallBudget);

            Task winner = await Task.WhenAny(completion.Task, Task.Delay(Timeout.Infinite, budget.Token)).ConfigureAwait(false);

            if (winner != completion.Task)
            {
                // The hardware call cannot be cancelled (the IOCTL takes no cancellation,
                // and a half-written EC register is worse than a late answer), so the
                // caller is released and the write finishes on the queue.
                throw new EngineException(EngineErrorCode.Timeout,
                    $"{Name}.{label} did not finish within {_options.HardwareCallBudget.TotalSeconds:0.0}s");
            }

            cancellationToken.ThrowIfCancellationRequested();

            object? result = await completion.Task.ConfigureAwait(false);
            return result is T typed ? typed : default!;
        }

        private async Task RunLoopAsync()
        {
            try
            {
                while (await _queue.Reader.WaitToReadAsync().ConfigureAwait(false))
                {
                    while (_queue.Reader.TryRead(out WorkItem item))
                    {
                        Interlocked.Decrement(ref _queued);
                        _currentStartedUtc = DateTime.UtcNow;
                        _currentLabel = item.Label;

                        try
                        {
                            item.Completion.TrySetResult(item.Work());
                        }
                        catch (Exception ex)
                        {
                            // Validation faults (InvalidValue) and hardware faults reach
                            // the caller as exceptions; the dispatcher maps them to codes.
                            item.Completion.TrySetException(ex);
                        }
                        finally
                        {
                            _currentStartedUtc = null;
                            _currentLabel = null;
                        }
                    }
                }
            }
            catch (ChannelClosedException)
            {
                // normal completion
            }
        }
    }
}
