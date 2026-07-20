namespace Halfway.Runtime;

public sealed class SubmittedInputQueue
{
    public const int DefaultCapacity = 8;

    private readonly Func<string, CancellationToken, Task> _deliver;
    private readonly int _capacity;
    private readonly object _gate = new();
    private readonly Queue<Entry> _entries = [];
    private readonly CancellationTokenSource _closed = new();
    private int _count;
    private bool _processing;
    private Exception? _closeReason;

    public SubmittedInputQueue(Func<string, CancellationToken, Task> deliver, int capacity = DefaultCapacity)
    {
        ArgumentNullException.ThrowIfNull(deliver);
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _deliver = deliver;
        _capacity = capacity;
    }

    public int Capacity => _capacity;

    public int Count
    {
        get { lock (_gate) return _count; }
    }

    public SubmittedInputAcceptance Accept(string input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var entry = new Entry(input, cancellationToken);
        if (cancellationToken.CanBeCanceled)
            entry.CancellationRegistration = cancellationToken.Register(() => entry.Completion.TrySetCanceled(cancellationToken));
        var startWorker = false;
        lock (_gate)
        {
            if (_closeReason is not null)
            {
                entry.CancellationRegistration.Dispose();
                throw _closeReason;
            }
            if (_count >= _capacity)
            {
                entry.CancellationRegistration.Dispose();
                throw new SubmittedInputQueueFullException(_capacity);
            }
            _entries.Enqueue(entry);
            _count++;
            if (!_processing)
            {
                _processing = true;
                startWorker = true;
            }
        }
        if (startWorker) _ = ProcessAsync();
        return new SubmittedInputAcceptance(entry.Completion.Task);
    }

    public Task EnqueueAsync(string input, CancellationToken cancellationToken = default) =>
        Accept(input, cancellationToken).Completion;

    public void Close(Exception reason)
    {
        ArgumentNullException.ThrowIfNull(reason);
        Entry[] queued;
        lock (_gate)
        {
            if (_closeReason is not null) return;
            _closeReason = reason;
            queued = _entries.ToArray();
            _entries.Clear();
            _count -= queued.Length;
        }

        _closed.Cancel();
        foreach (var entry in queued)
        {
            entry.CancellationRegistration.Dispose();
            entry.Completion.TrySetException(reason);
        }
    }

    private async Task ProcessAsync()
    {
        while (true)
        {
            Entry entry;
            lock (_gate)
            {
                if (_entries.Count == 0)
                {
                    _processing = false;
                    return;
                }
                entry = _entries.Dequeue();
            }

            try
            {
                if (!entry.Completion.Task.IsCompleted)
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(entry.CancellationToken, _closed.Token);
                    await _deliver(entry.Input, linked.Token).ConfigureAwait(false);
                    entry.Completion.TrySetResult();
                }
            }
            catch (OperationCanceledException) when (entry.CancellationToken.IsCancellationRequested)
            {
                entry.Completion.TrySetCanceled(entry.CancellationToken);
            }
            catch (OperationCanceledException) when (_closed.IsCancellationRequested)
            {
                entry.Completion.TrySetException(_closeReason!);
            }
            catch (Exception exception)
            {
                entry.Completion.TrySetException(exception);
            }
            finally
            {
                entry.CancellationRegistration.Dispose();
                lock (_gate) _count--;
            }
        }
    }

    private sealed class Entry(string input, CancellationToken cancellationToken)
    {
        public string Input { get; } = input;
        public CancellationToken CancellationToken { get; } = cancellationToken;
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenRegistration CancellationRegistration { get; set; }
    }
}

public sealed record SubmittedInputAcceptance(Task Completion);

public sealed class SubmittedInputQueueFullException(int capacity)
    : InvalidOperationException($"The submitted input queue is full (capacity {capacity}); the newest submission was rejected.");

public sealed class SessionInputUnavailableException(string key)
    : InvalidOperationException($"Session '{key}' cannot accept submitted input because its terminal is no longer available.");
