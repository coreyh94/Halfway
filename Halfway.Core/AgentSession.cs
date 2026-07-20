namespace Halfway.Core;

public sealed record AgentSession(
    Guid Id,
    string DisplayName,
    AgentKind Kind,
    Guid? ParentId,
    AgentStatus Status = AgentStatus.Queued,
    int? ProcessId = null);
