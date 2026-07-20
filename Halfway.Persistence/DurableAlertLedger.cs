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

    public Task<bool> ReserveAsync(Guid eventId, CancellationToken cancellationToken = default) =>
        _store.ReserveAlertAsync(eventId, DateTimeOffset.UtcNow, cancellationToken);

    public Task<bool> CommitAsync(Guid eventId, CancellationToken cancellationToken = default) =>
        _store.CommitAlertAsync(eventId, DateTimeOffset.UtcNow, cancellationToken);

    public Task<bool> ReleaseAsync(Guid eventId, CancellationToken cancellationToken = default) =>
        _store.ReleaseAlertAsync(eventId, DateTimeOffset.UtcNow, cancellationToken);

    public Task<int> RecoverAsync(CancellationToken cancellationToken = default) =>
        _store.RecoverStaleReservationsAsync(DateTimeOffset.UtcNow, cancellationToken);
}
