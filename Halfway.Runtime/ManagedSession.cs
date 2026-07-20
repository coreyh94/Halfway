using Halfway.Core;
using Halfway.Terminal;

namespace Halfway.Runtime;

public sealed record ManagedSession(
    string Key,
    Guid Id,
    string DisplayName,
    AgentKind Kind,
    Guid? ParentId,
    AgentStatus Status = AgentStatus.Queued);
