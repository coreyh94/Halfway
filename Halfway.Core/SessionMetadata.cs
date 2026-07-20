namespace Halfway.Core;

public sealed record SessionMetadata(
    Guid Id,
    Guid WorkspaceId,
    string SessionKey,
    string DisplayName,
    AgentKind Kind,
    Guid? ParentSessionId,
    LaunchProfile LaunchProfile,
    int DisplayOrder,
    AgentStatus LastStatus,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);
