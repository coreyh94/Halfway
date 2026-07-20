using Halfway.Core;

namespace Halfway.Persistence;

public interface IWorkspaceStore : IAsyncDisposable
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<WorkspaceMetadata?> FindWorkspaceAsync(string workingDirectory, CancellationToken cancellationToken = default);
    Task InsertWorkspaceAsync(WorkspaceMetadata workspace, CancellationToken cancellationToken = default);
    Task InsertInitialWorkspaceAsync(WorkspaceMetadata workspace, IReadOnlyList<SessionMetadata> sessions, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SessionMetadata>> LoadSessionsAsync(Guid workspaceId, CancellationToken cancellationToken = default);
    Task InsertSessionAsync(SessionMetadata session, CancellationToken cancellationToken = default);
    Task UpdateSessionAsync(SessionMetadata session, CancellationToken cancellationToken = default);
    Task UpdateSelectionsAsync(Guid workspaceId, Guid? primaryId, Guid? subAgentId, CancellationToken cancellationToken = default);
    Task UpdateStatusAsync(Guid sessionId, AgentStatus status, CancellationToken cancellationToken = default);
}
