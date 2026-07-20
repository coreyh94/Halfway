namespace Halfway.Core;

public sealed class FailureNotificationPolicy
{
    private readonly HashSet<Guid> _notifiedEventIds = [];

    public FailureNotification? Evaluate(LifecycleTransition transition, bool isWindowActive, Guid? focusedSessionId)
    {
        if (transition.Event is not { NewStatus: AgentStatus.Failed } lifecycleEvent) return null;
        if (isWindowActive && focusedSessionId == transition.Session.Id) return null;
        if (!_notifiedEventIds.Add(lifecycleEvent.Id)) return null;
        return new FailureNotification(
            lifecycleEvent.Id,
            "Halfway session failed",
            $"{transition.Session.DisplayName} failed. Return to Halfway to review the terminal.");
    }
}

public sealed record FailureNotification(Guid EventId, string Title, string Message);
