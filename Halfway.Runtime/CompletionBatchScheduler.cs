namespace Halfway.Runtime;

public interface ICompletionBatchTimer
{
    DateTimeOffset UtcNow { get; }
    Task DelayUntilAsync(DateTimeOffset deadlineUtc, CancellationToken cancellationToken);
}

public sealed class SystemCompletionBatchTimer : ICompletionBatchTimer
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public Task DelayUntilAsync(DateTimeOffset deadlineUtc, CancellationToken cancellationToken)
    {
        var delay = deadlineUtc - UtcNow;
        return delay <= TimeSpan.Zero ? Task.CompletedTask : Task.Delay(delay, cancellationToken);
    }
}

public sealed class CompletionBatchScheduler : IDisposable
{
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMilliseconds(250);

    private readonly ICompletionBatchTimer _timer;
    private readonly Func<Guid, DateTimeOffset, DateTimeOffset, Task> _batchDueAsync;
    private readonly TimeSpan _window;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, BatchWindow> _windows = [];
    private readonly CancellationTokenSource _stopping = new();
    private bool _disposed;

    public CompletionBatchScheduler(
        ICompletionBatchTimer timer,
        Func<Guid, DateTimeOffset, DateTimeOffset, Task> batchDueAsync,
        TimeSpan? window = null)
    {
        _timer = timer;
        _batchDueAsync = batchDueAsync;
        _window = window ?? DefaultWindow;
        if (_window <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(window));
    }

    public DateTimeOffset Schedule(Guid parentSessionId) => Schedule(parentSessionId, _timer.UtcNow);

    public DateTimeOffset Schedule(Guid parentSessionId, DateTimeOffset firstCompletionUtc)
    {
        BatchWindow batch;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_windows.TryGetValue(parentSessionId, out var current) && firstCompletionUtc < current.DeadlineUtc)
                return current.DeadlineUtc;

            batch = new BatchWindow(firstCompletionUtc, firstCompletionUtc + _window);
            _windows[parentSessionId] = batch;
            batch.Runner = RunAsync(parentSessionId, batch);
        }
        return batch.DeadlineUtc;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _stopping.Cancel();
            _windows.Clear();
        }
        _stopping.Dispose();
    }

    private async Task RunAsync(Guid parentSessionId, BatchWindow batch)
    {
        try
        {
            await _timer.DelayUntilAsync(batch.DeadlineUtc, _stopping.Token).ConfigureAwait(false);
            await _batchDueAsync(parentSessionId, batch.StartUtc, batch.DeadlineUtc).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
        finally
        {
            lock (_gate)
            {
                if (_windows.TryGetValue(parentSessionId, out var current) && ReferenceEquals(current, batch))
                    _windows.Remove(parentSessionId);
            }
        }
    }

    private sealed class BatchWindow(DateTimeOffset startUtc, DateTimeOffset deadlineUtc)
    {
        public DateTimeOffset StartUtc { get; } = startUtc;
        public DateTimeOffset DeadlineUtc { get; } = deadlineUtc;
        public Task Runner { get; set; } = Task.CompletedTask;
    }
}
