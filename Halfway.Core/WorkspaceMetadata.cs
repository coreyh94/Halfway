namespace Halfway.Core;

public sealed record WorkspaceMetadata(
    Guid Id,
    string DisplayName,
    string WorkingDirectory,
    Guid? SelectedPrimarySessionId,
    Guid? SelectedSubAgentSessionId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);
