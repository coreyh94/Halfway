using Halfway.Terminal;

namespace Halfway.Runtime;

public sealed class AlertDeliveryCoordinator(SessionCoordinator sessions)
{
    public async Task<AlertDeliveryOutcome> TryDeliverAsync(
        Guid parentSessionId,
        string parentSessionKey,
        IReadOnlyCollection<Guid> eventIds,
        AlertInputCoordinator alerts,
        Func<IReadOnlyCollection<Guid>, CancellationToken, Task<bool>> reserveAsync,
        Func<IReadOnlyCollection<Guid>, CancellationToken, Task<bool>> markWriteSucceededAsync,
        Func<IReadOnlyCollection<Guid>, CancellationToken, Task<bool>> commitAsync,
        Func<IReadOnlyCollection<Guid>, CancellationToken, Task<bool>> releaseAsync,
        CancellationToken cancellationToken = default)
    {
        var alert = alerts.TakeReadyAlertReservation();
        if (alert is null) return AlertDeliveryOutcome.NotReady;
        if (alert.ParentSessionId != parentSessionId)
        {
            alerts.ReleaseAlertDelivery();
            return AlertDeliveryOutcome.TargetRejected;
        }
        if (eventIds.Count > 0 && !eventIds.Contains(alert.EventId))
        {
            alerts.ReleaseAlertDelivery();
            return AlertDeliveryOutcome.TargetRejected;
        }

        SessionOwnership ownership;
        try { ownership = sessions.CaptureOwnership(parentSessionKey, parentSessionId); }
        catch (KeyNotFoundException)
        {
            alerts.ReleaseAlertDelivery();
            return AlertDeliveryOutcome.TargetRejected;
        }

        bool reserved;
        try { reserved = eventIds.Count == 0 || await reserveAsync(eventIds, cancellationToken).ConfigureAwait(false); }
        catch
        {
            alerts.CommitAlertDelivery();
            return AlertDeliveryOutcome.ReservationIndeterminate;
        }
        if (!reserved)
        {
            alerts.CommitAlertDelivery();
            return AlertDeliveryOutcome.ReservationUnavailable;
        }

        OwnedWriteOutcome write;
        try
        {
            write = await sessions.TryWriteAlertAsync(
                ownership,
                alert.Message + "\r",
                () => alerts.CanWrite(alert) && alert.ParentSessionId == parentSessionId,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            bool released;
            try { released = eventIds.Count == 0 || await releaseAsync(eventIds, CancellationToken.None).ConfigureAwait(false); }
            catch { released = false; }
            alerts.ReleaseAlertDelivery();
            return released ? AlertDeliveryOutcome.RejectedBeforeWrite : AlertDeliveryOutcome.ReleasePending;
        }

        if (write == OwnedWriteOutcome.RejectedBeforeWrite)
        {
            bool released;
            try { released = eventIds.Count == 0 || await releaseAsync(eventIds, cancellationToken).ConfigureAwait(false); }
            catch { released = false; }
            alerts.ReleaseAlertDelivery();
            return released ? AlertDeliveryOutcome.RejectedBeforeWrite : AlertDeliveryOutcome.ReleasePending;
        }

        if (write == OwnedWriteOutcome.IndeterminateFailure)
        {
            if (eventIds.Count > 0)
            {
                try { await markWriteSucceededAsync(eventIds, CancellationToken.None).ConfigureAwait(false); }
                catch { }
            }
            alerts.CommitAlertDelivery();
            return AlertDeliveryOutcome.WriteIndeterminate;
        }

        if (eventIds.Count > 0)
        {
            bool writeMarked;
            try { writeMarked = await markWriteSucceededAsync(eventIds, CancellationToken.None).ConfigureAwait(false); }
            catch
            {
                alerts.CommitAlertDelivery();
                return AlertDeliveryOutcome.CommitIndeterminate;
            }
            if (!writeMarked)
            {
                alerts.CommitAlertDelivery();
                return AlertDeliveryOutcome.CommitIndeterminate;
            }

            bool committed;
            try { committed = await commitAsync(eventIds, CancellationToken.None).ConfigureAwait(false); }
            catch
            {
                alerts.CommitAlertDelivery();
                return AlertDeliveryOutcome.CommitPending;
            }
            if (!committed)
            {
                alerts.CommitAlertDelivery();
                return AlertDeliveryOutcome.CommitPending;
            }
        }

        alerts.CommitAlertDelivery();
        return AlertDeliveryOutcome.Delivered;
    }
}

public enum AlertDeliveryOutcome
{
    NotReady,
    TargetRejected,
    ReservationUnavailable,
    ReservationIndeterminate,
    RejectedBeforeWrite,
    ReleasePending,
    WriteIndeterminate,
    CommitIndeterminate,
    CommitPending,
    Delivered,
}
