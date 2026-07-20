using Halfway.Core;

namespace Halfway.Persistence;

public interface IWorkspaceStore : IAsyncDisposable
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<WorkspaceMetadata?> FindWorkspaceAsync(string workingDirectory, CancellationToken cancellationToken = default);
    Task<WorkspaceMetadata?> FindMostRecentWorkspaceAsync(CancellationToken cancellationToken = default);
    Task InsertWorkspaceAsync(WorkspaceMetadata workspace, CancellationToken cancellationToken = default);
    Task InsertInitialWorkspaceAsync(WorkspaceMetadata workspace, IReadOnlyList<SessionMetadata> sessions, IReadOnlyList<AgentRelationship> relationships, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SessionMetadata>> LoadSessionsAsync(Guid workspaceId, CancellationToken cancellationToken = default);
    Task InsertSessionAsync(SessionMetadata session, CancellationToken cancellationToken = default);
    Task InsertSessionWithRelationshipAsync(SessionMetadata session, AgentRelationship relationship, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AgentRelationship>> LoadRelationshipsAsync(Guid workspaceId, CancellationToken cancellationToken = default);
    Task UpdateSessionAsync(SessionMetadata session, CancellationToken cancellationToken = default);
    Task UpdateSelectionsAsync(Guid workspaceId, Guid? primaryId, Guid? subAgentId, CancellationToken cancellationToken = default);
    Task UpdateStatusAsync(Guid sessionId, AgentStatus status, CancellationToken cancellationToken = default);
    Task<bool> InsertLifecycleEventAsync(LifecycleEvent lifecycleEvent, CancellationToken cancellationToken = default);
    Task<LifecycleEvent?> FindLifecycleEventAsync(Guid eventId, CancellationToken cancellationToken = default);
    Task<AlertDelivery> EnsureAlertDeliveryAsync(LifecycleEvent lifecycleEvent, string message, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AlertDelivery>> LoadPendingAlertsAsync(Guid parentSessionId, CancellationToken cancellationToken = default);
    Task<bool> ReserveAlertAsync(Guid eventId, DateTimeOffset reservedAtUtc, CancellationToken cancellationToken = default);
    Task<bool> ReserveAlertsAsync(IReadOnlyCollection<Guid> eventIds, DateTimeOffset reservedAtUtc, CancellationToken cancellationToken = default);
    Task<bool> CommitAlertAsync(Guid eventId, DateTimeOffset deliveredAtUtc, CancellationToken cancellationToken = default);
    Task<bool> CommitAlertsAsync(IReadOnlyCollection<Guid> eventIds, DateTimeOffset deliveredAtUtc, CancellationToken cancellationToken = default);
    Task<bool> ReleaseAlertAsync(Guid eventId, DateTimeOffset updatedAtUtc, CancellationToken cancellationToken = default);
    Task<bool> ReleaseAlertsAsync(IReadOnlyCollection<Guid> eventIds, DateTimeOffset updatedAtUtc, CancellationToken cancellationToken = default);
    Task<int> RecoverStaleReservationsAsync(DateTimeOffset updatedAtUtc, CancellationToken cancellationToken = default);
    Task<AlertDelivery?> FindAlertDeliveryAsync(Guid eventId, CancellationToken cancellationToken = default);
}
