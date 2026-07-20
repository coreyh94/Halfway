namespace Halfway.Core;

public sealed class SessionAttentionTracker
{
    private readonly HashSet<Guid> _unreadSessionIds = [];

    public Guid? FocusedSessionId { get; private set; }

    public bool RecordActivity(Guid sessionId)
    {
        if (sessionId == FocusedSessionId) return false;
        return _unreadSessionIds.Add(sessionId);
    }

    public bool Focus(Guid sessionId)
    {
        FocusedSessionId = sessionId;
        return _unreadSessionIds.Remove(sessionId);
    }

    public bool IsUnread(Guid sessionId) => _unreadSessionIds.Contains(sessionId);
}
