namespace Halfway.Core;

public sealed record LifecycleEvent(
    Guid Id,
    Guid SessionId,
    Guid? ParentSessionId,
    AgentStatus PreviousStatus,
    AgentStatus NewStatus,
    DateTimeOffset OccurredAt,
    bool AlertEligible,
    bool AlertDelivered = false);
