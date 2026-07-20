using Halfway.Core;

namespace Halfway.Persistence;

public sealed class DurableAlertLedger
{
    private readonly IWorkspaceStore _store;

    public DurableAlertLedger(IWorkspaceStore store) => _store = store;

    public async Task RecordAsync(LifecycleTransition transition, CancellationToken cancellationToken = default)
    {
        if (transition.Event is not { } item) return;
        if (transition.Alert is { } alert)
            await _store.EnsureAlertDeliveryAsync(item, alert.Message, cancellationToken);
        else
            await _store.InsertLifecycleEventAsync(item, cancellationToken);
    }

    public Task<IReadOnlyList<AlertDelivery>> LoadPendingAsync(Guid parentSessionId, CancellationToken cancellationToken = default) =>
        _store.LoadPendingAlertsAsync(parentSessionId, cancellationToken);

    public Task<IReadOnlyList<AlertDelivery>> LoadPendingBeforeAsync(Guid parentSessionId, DateTimeOffset occurredBeforeUtc, CancellationToken cancellationToken = default) =>
        _store.LoadPendingAlertsBeforeAsync(parentSessionId, occurredBeforeUtc, cancellationToken);

    public Task<bool> ReserveAsync(Guid eventId, CancellationToken cancellationToken = default) =>
        _store.ReserveAlertAsync(eventId, DateTimeOffset.UtcNow, cancellationToken);

    public Task<bool> ReserveAsync(IReadOnlyCollection<Guid> eventIds, CancellationToken cancellationToken = default) =>
        _store.ReserveAlertsAsync(eventIds, DateTimeOffset.UtcNow, cancellationToken);

    public Task<bool> MarkWriteSucceededAsync(IReadOnlyCollection<Guid> eventIds, CancellationToken cancellationToken = default) =>
        _store.MarkAlertWriteSucceededAsync(eventIds, DateTimeOffset.UtcNow, cancellationToken);

    public Task<bool> CommitAsync(Guid eventId, CancellationToken cancellationToken = default) =>
        _store.CommitAlertAsync(eventId, DateTimeOffset.UtcNow, cancellationToken);

    public Task<bool> CommitAsync(IReadOnlyCollection<Guid> eventIds, CancellationToken cancellationToken = default) =>
        _store.CommitAlertsAsync(eventIds, DateTimeOffset.UtcNow, cancellationToken);

    public Task<bool> ReleaseAsync(Guid eventId, CancellationToken cancellationToken = default) =>
        _store.ReleaseAlertAsync(eventId, DateTimeOffset.UtcNow, cancellationToken);

    public Task<bool> ReleaseAsync(IReadOnlyCollection<Guid> eventIds, CancellationToken cancellationToken = default) =>
        _store.ReleaseAlertsAsync(eventIds, DateTimeOffset.UtcNow, cancellationToken);

    public async Task<DurableAlertBatch?> CreatePendingBatchAsync(Guid parentSessionId, CancellationToken cancellationToken = default)
    {
        var pending = await LoadPendingAsync(parentSessionId, cancellationToken);
        if (pending.Count == 0) return null;
        var eventIds = pending.Select(item => item.EventId).ToArray();
        var message = new CompletionAlert(eventIds[0], parentSessionId, pending.Select(item => item.SessionDisplayName).ToArray()).Message;
        return new DurableAlertBatch(eventIds, parentSessionId, message);
    }

    public async Task<DurableAlertBatch?> CreatePendingBatchAsync(Guid parentSessionId, DateTimeOffset occurredBeforeUtc, CancellationToken cancellationToken = default)
    {
        var pending = await LoadPendingBeforeAsync(parentSessionId, occurredBeforeUtc, cancellationToken);
        if (pending.Count == 0) return null;
        var eventIds = pending.Select(item => item.EventId).ToArray();
        var message = new CompletionAlert(eventIds[0], parentSessionId, pending.Select(item => item.SessionDisplayName).ToArray()).Message;
        return new DurableAlertBatch(eventIds, parentSessionId, message);
    }

    public async Task<DurableAlertBatch?> CreatePendingBatchAsync(Guid parentSessionId, DateTimeOffset occurredBeforeUtc, IReadOnlyCollection<Guid> excludedEventIds, CancellationToken cancellationToken = default)
    {
        var excluded = excludedEventIds.ToHashSet();
        var pending = (await LoadPendingBeforeAsync(parentSessionId, occurredBeforeUtc, cancellationToken))
            .Where(item => !excluded.Contains(item.EventId))
            .ToArray();
        if (pending.Length == 0) return null;
        var eventIds = pending.Select(item => item.EventId).ToArray();
        var message = new CompletionAlert(eventIds[0], parentSessionId, pending.Select(item => item.SessionDisplayName).ToArray()).Message;
        return new DurableAlertBatch(eventIds, parentSessionId, message);
    }

    public Task<int> RecoverAsync(CancellationToken cancellationToken = default) =>
        _store.RecoverStaleReservationsAsync(DateTimeOffset.UtcNow, cancellationToken);
}

public sealed record DurableAlertBatch(IReadOnlyList<Guid> EventIds, Guid ParentSessionId, string Message)
{
    public Guid ReservationId => EventIds[0];
}
