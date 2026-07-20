namespace Halfway.Core;

public sealed record AgentRelationship(
    Guid WorkspaceId,
    Guid ParentSessionId,
    Guid ChildSessionId,
    DateTimeOffset RegisteredAtUtc);
