namespace Halfway.Persistence;

public sealed class OrderlyShutdownPersistence
{
    private readonly object _gate = new();
    private readonly List<Exception> _failures = [];
    private Task _tail = Task.CompletedTask;

    public void Enqueue(Func<Task> operation, Action<Exception>? onFailure = null)
    {
        ArgumentNullException.ThrowIfNull(operation);
        lock (_gate)
        {
            _tail = RunAsync(_tail, operation, onFailure);
        }
    }

    public async Task CompleteAsync(
        Func<ValueTask> stopOwnedSessions,
        Func<Task> markRunClean,
        Func<ValueTask> disposePersistence)
    {
        ArgumentNullException.ThrowIfNull(stopOwnedSessions);
        ArgumentNullException.ThrowIfNull(markRunClean);
        ArgumentNullException.ThrowIfNull(disposePersistence);

        var failures = new List<Exception>();
        try
        {
            await stopOwnedSessions().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        try
        {
            await DrainAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        if (failures.Count == 0)
        {
            try
            {
                await markRunClean().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        try
        {
            await disposePersistence().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        if (failures.Count > 0)
        {
            throw new AggregateException("Orderly shutdown did not complete cleanly.", failures);
        }
    }

    private async Task RunAsync(Task previous, Func<Task> operation, Action<Exception>? onFailure)
    {
        await previous.ConfigureAwait(false);
        try
        {
            await operation().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            lock (_gate)
            {
                _failures.Add(exception);
            }

            if (onFailure is not null)
            {
                try
                {
                    onFailure(exception);
                }
                catch (Exception reportingException)
                {
                    lock (_gate)
                    {
                        _failures.Add(reportingException);
                    }
                }
            }
        }
    }

    private async Task DrainAsync()
    {
        while (true)
        {
            Task pending;
            lock (_gate)
            {
                pending = _tail;
            }

            await pending.ConfigureAwait(false);

            lock (_gate)
            {
                if (!ReferenceEquals(pending, _tail))
                {
                    continue;
                }

                if (_failures.Count > 0)
                {
                    throw new AggregateException("One or more persistence writes failed.", _failures.ToArray());
                }

                return;
            }
        }
    }
}
