namespace Halfway.Core;

public sealed class SessionRegistry
{
    private readonly Dictionary<Guid, AgentSession> _sessions = [];
    private readonly List<LifecycleEvent> _events = [];

    public IReadOnlyCollection<AgentSession> Sessions => _sessions.Values;
    public IReadOnlyList<LifecycleEvent> Events => _events;

    public void Register(AgentSession session)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(session.DisplayName);

        if (session.ParentId is not null && !_sessions.ContainsKey(session.ParentId.Value))
        {
            throw new InvalidOperationException($"Parent session {session.ParentId} must be registered first.");
        }

        if (!_sessions.TryAdd(session.Id, session))
        {
            throw new InvalidOperationException($"Session {session.Id} is already registered.");
        }
    }

    public AgentSession Get(Guid id) => _sessions.TryGetValue(id, out var session)
        ? session
        : throw new KeyNotFoundException($"Session {id} is not registered.");

    public LifecycleTransition Transition(Guid sessionId, AgentStatus newStatus, DateTimeOffset? occurredAt = null)
    {
        var current = Get(sessionId);
        if (current.Status == newStatus)
        {
            return LifecycleTransition.Unchanged(current);
        }

        var updated = current with { Status = newStatus };
        _sessions[sessionId] = updated;

        var lifecycleEvent = new LifecycleEvent(
            Guid.NewGuid(),
            sessionId,
            updated.ParentId,
            current.Status,
            newStatus,
            occurredAt ?? DateTimeOffset.UtcNow,
            current.Status != AgentStatus.Completed && newStatus == AgentStatus.Completed);
        _events.Add(lifecycleEvent);

        CompletionAlert? alert = null;
        if (lifecycleEvent.AlertEligible && updated.ParentId is Guid parentId)
        {
            alert = new CompletionAlert(lifecycleEvent.Id, parentId, [updated.DisplayName]);
        }

        return new LifecycleTransition(updated, lifecycleEvent, alert);
    }

    public void MarkAlertDelivered(Guid eventId)
    {
        var index = _events.FindIndex(item => item.Id == eventId);
        if (index < 0)
        {
            throw new KeyNotFoundException($"Lifecycle event {eventId} is not registered.");
        }

        _events[index] = _events[index] with { AlertDelivered = true };
    }

    public IReadOnlyDictionary<AgentStatus, int> CountByStatus() =>
        Enum.GetValues<AgentStatus>().ToDictionary(status => status, status => _sessions.Values.Count(session => session.Status == status));
}

public sealed record LifecycleTransition(
    AgentSession Session,
    LifecycleEvent? Event = null,
    CompletionAlert? Alert = null)
{
    public bool Changed => Event is not null;

    public static LifecycleTransition Unchanged(AgentSession session) => new(session);
}
