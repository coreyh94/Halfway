using System.Globalization;
using Halfway.Core;
using Microsoft.Data.Sqlite;

namespace Halfway.Persistence;

public sealed class SqliteWorkspaceStore : IWorkspaceStore
{
    public const int SchemaVersion = 4;
    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<CancellationToken, Task>? _initialSchemaCreated;
    private bool _disposed;

    public SqliteWorkspaceStore(string databasePath) : this(databasePath, null)
    {
    }

    internal SqliteWorkspaceStore(string databasePath, Func<CancellationToken, Task>? initialSchemaCreated)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (directory is not null) Directory.CreateDirectory(directory);
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
        _initialSchemaCreated = initialSchemaCreated;
    }

    public static string ProductionDatabasePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Halfway", "halfway.db");

    public async Task InitializeAsync(CancellationToken cancellationToken = default) => await LockedAsync(async () =>
    {
        await _connection.OpenAsync(cancellationToken);
        await ExecuteAsync("PRAGMA foreign_keys = ON;", cancellationToken);
        var version = Convert.ToInt32(await ScalarAsync("PRAGMA user_version;", cancellationToken), CultureInfo.InvariantCulture);
        if (version > SchemaVersion) throw new InvalidOperationException($"Database schema version {version} is newer than supported version {SchemaVersion}.");
        if (version == 0)
        {
            await using var transaction = await _connection.BeginTransactionAsync(cancellationToken);
            await ExecuteAsync("""
                CREATE TABLE Workspaces (
                    Id TEXT PRIMARY KEY, Name TEXT NOT NULL, WorkingDirectory TEXT NOT NULL UNIQUE,
                    SelectedPrimarySessionId TEXT NULL, SelectedSubAgentSessionId TEXT NULL,
                    CreatedAtUtc TEXT NOT NULL, UpdatedAtUtc TEXT NOT NULL);
                CREATE TABLE Sessions (
                    Id TEXT PRIMARY KEY, WorkspaceId TEXT NOT NULL, SessionKey TEXT NOT NULL,
                    DisplayName TEXT NOT NULL, AgentKind INTEGER NOT NULL, ParentSessionId TEXT NULL,
                    LaunchProfile INTEGER NOT NULL, DisplayOrder INTEGER NOT NULL, LastStatus INTEGER NOT NULL,
                    CreatedAtUtc TEXT NOT NULL, UpdatedAtUtc TEXT NOT NULL,
                    UNIQUE(WorkspaceId, SessionKey),
                    FOREIGN KEY(WorkspaceId) REFERENCES Workspaces(Id),
                    FOREIGN KEY(ParentSessionId) REFERENCES Sessions(Id));
                """, cancellationToken, transaction);
            if (_initialSchemaCreated is not null)
            {
                await _initialSchemaCreated(cancellationToken);
            }
            await ExecuteAsync("PRAGMA user_version = 1;", cancellationToken, transaction);
            await transaction.CommitAsync(cancellationToken);
            version = 1;
        }
        if (version == 1)
        {
            await using var transaction = await _connection.BeginTransactionAsync(cancellationToken);
            await ExecuteAsync("""
                CREATE TABLE IF NOT EXISTS LifecycleEvents (
                    Id TEXT PRIMARY KEY,
                    SessionId TEXT NOT NULL,
                    ParentSessionId TEXT NULL,
                    PreviousStatus INTEGER NOT NULL,
                    NewStatus INTEGER NOT NULL,
                    OccurredAtUtc TEXT NOT NULL,
                    AlertEligible INTEGER NOT NULL,
                    FOREIGN KEY(SessionId) REFERENCES Sessions(Id),
                    FOREIGN KEY(ParentSessionId) REFERENCES Sessions(Id));
                CREATE TABLE IF NOT EXISTS AlertDeliveries (
                    EventId TEXT PRIMARY KEY,
                    Message TEXT NOT NULL,
                    State INTEGER NOT NULL,
                    ReservedAtUtc TEXT NULL,
                    DeliveredAtUtc TEXT NULL,
                    UpdatedAtUtc TEXT NOT NULL,
                    FOREIGN KEY(EventId) REFERENCES LifecycleEvents(Id));
                PRAGMA user_version = 2;
                """, cancellationToken, transaction);
            await transaction.CommitAsync(cancellationToken);
            version = 2;
        }
        if (version == 2)
        {
            await using var transaction = await _connection.BeginTransactionAsync(cancellationToken);
            await ExecuteAsync("""
                CREATE UNIQUE INDEX IF NOT EXISTS IX_Sessions_WorkspaceId_Id ON Sessions(WorkspaceId,Id);
                CREATE TABLE IF NOT EXISTS AgentRelationships (
                    ChildSessionId TEXT PRIMARY KEY,
                    WorkspaceId TEXT NOT NULL,
                    ParentSessionId TEXT NOT NULL,
                    RegisteredAtUtc TEXT NOT NULL,
                    CHECK(ChildSessionId <> ParentSessionId),
                    FOREIGN KEY(WorkspaceId,ParentSessionId) REFERENCES Sessions(WorkspaceId,Id),
                    FOREIGN KEY(WorkspaceId,ChildSessionId) REFERENCES Sessions(WorkspaceId,Id));
                INSERT OR IGNORE INTO AgentRelationships(ChildSessionId,WorkspaceId,ParentSessionId,RegisteredAtUtc)
                    SELECT Id,WorkspaceId,ParentSessionId,CreatedAtUtc FROM Sessions WHERE ParentSessionId IS NOT NULL;
                PRAGMA user_version = 3;
                """, cancellationToken, transaction);
            await transaction.CommitAsync(cancellationToken);
            version = 3;
        }
        if (version == 3)
        {
            await using var transaction = await _connection.BeginTransactionAsync(cancellationToken);
            await ExecuteAsync("""
                CREATE TABLE IF NOT EXISTS ApplicationRuns (
                    Id TEXT PRIMARY KEY,
                    StartedAtUtc TEXT NOT NULL,
                    CleanShutdownAtUtc TEXT NULL,
                    ApplicationVersion TEXT NOT NULL);
                PRAGMA user_version = 4;
                """, cancellationToken, transaction);
            await transaction.CommitAsync(cancellationToken);
        }
        await ReconcileWorkspacePathsAsync(cancellationToken);
    }, cancellationToken);

    public Task<ApplicationRunStart> StartApplicationRunAsync(ApplicationRun applicationRun, CancellationToken cancellationToken = default) => LockedAsync(async () =>
    {
        if (applicationRun.CleanShutdownAtUtc is not null)
            throw new ArgumentException("A new application run cannot already have a clean shutdown timestamp.", nameof(applicationRun));
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationRun.ApplicationVersion);

        await using var transaction = await _connection.BeginTransactionAsync(cancellationToken);
        ApplicationRun? previousRun;
        await using (var previousCommand = Command("SELECT Id,StartedAtUtc,CleanShutdownAtUtc,ApplicationVersion FROM ApplicationRuns ORDER BY StartedAtUtc DESC,Id DESC LIMIT 1;"))
        {
            previousCommand.Transaction = (SqliteTransaction)transaction;
            await using var reader = await previousCommand.ExecuteReaderAsync(cancellationToken);
            previousRun = await reader.ReadAsync(cancellationToken) ? ReadApplicationRun(reader) : null;
        }
        await using (var insertCommand = Command("INSERT INTO ApplicationRuns(Id,StartedAtUtc,CleanShutdownAtUtc,ApplicationVersion) VALUES($id,$started,NULL,$version);"))
        {
            insertCommand.Transaction = (SqliteTransaction)transaction;
            insertCommand.Parameters.AddWithValue("$id", applicationRun.Id.ToString());
            insertCommand.Parameters.AddWithValue("$started", Format(applicationRun.StartedAtUtc));
            insertCommand.Parameters.AddWithValue("$version", applicationRun.ApplicationVersion);
            await insertCommand.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return new ApplicationRunStart(applicationRun, previousRun);
    }, cancellationToken);

    public Task<bool> CompleteApplicationRunAsync(Guid runId, DateTimeOffset cleanShutdownAtUtc, CancellationToken cancellationToken = default) => LockedAsync(async () =>
    {
        await using var command = Command("UPDATE ApplicationRuns SET CleanShutdownAtUtc=$stopped WHERE Id=$id AND CleanShutdownAtUtc IS NULL;");
        command.Parameters.AddWithValue("$id", runId.ToString());
        command.Parameters.AddWithValue("$stopped", Format(cleanShutdownAtUtc));
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }, cancellationToken);

    public Task<IReadOnlyList<ApplicationRun>> LoadApplicationRunsAsync(CancellationToken cancellationToken = default) => LockedAsync(async () =>
    {
        await using var command = Command("SELECT Id,StartedAtUtc,CleanShutdownAtUtc,ApplicationVersion FROM ApplicationRuns ORDER BY StartedAtUtc,Id;");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var runs = new List<ApplicationRun>();
        while (await reader.ReadAsync(cancellationToken)) runs.Add(ReadApplicationRun(reader));
        return (IReadOnlyList<ApplicationRun>)runs;
    }, cancellationToken);

    public Task<WorkspaceMetadata?> FindWorkspaceAsync(string workingDirectory, CancellationToken cancellationToken = default) => LockedAsync(async () =>
    {
        var identity = CanonicalWorkspacePath(workingDirectory);
        await using var command = Command("SELECT Id,Name,WorkingDirectory,SelectedPrimarySessionId,SelectedSubAgentSessionId,CreatedAtUtc,UpdatedAtUtc FROM Workspaces ORDER BY CreatedAtUtc,Id;");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var workspace = ReadWorkspace(reader);
            if (string.Equals(CanonicalWorkspacePath(workspace.WorkingDirectory), identity, StringComparison.Ordinal)) return workspace;
        }
        return null;
    }, cancellationToken);

    public Task<WorkspaceMetadata?> FindMostRecentWorkspaceAsync(CancellationToken cancellationToken = default) => LockedAsync(async () =>
    {
        await using var command = Command("SELECT Id,Name,WorkingDirectory,SelectedPrimarySessionId,SelectedSubAgentSessionId,CreatedAtUtc,UpdatedAtUtc FROM Workspaces ORDER BY UpdatedAtUtc DESC,CreatedAtUtc DESC,Id LIMIT 1;");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadWorkspace(reader) : null;
    }, cancellationToken);

    public Task InsertWorkspaceAsync(WorkspaceMetadata workspace, CancellationToken cancellationToken = default) => LockedAsync(async () =>
    {
        await EnsureWorkspacePathAvailableAsync(workspace.WorkingDirectory, null, cancellationToken);
        await using var command = Command("INSERT INTO Workspaces VALUES($id,$name,$path,$primary,$sub,$created,$updated);");
        Add(command, workspace);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }, cancellationToken);

    public Task InsertInitialWorkspaceAsync(WorkspaceMetadata workspace, IReadOnlyList<SessionMetadata> sessions, IReadOnlyList<AgentRelationship> relationships, CancellationToken cancellationToken = default) => LockedAsync(async () =>
    {
        await using var transaction = await _connection.BeginTransactionAsync(cancellationToken);
        await EnsureWorkspacePathAvailableAsync(workspace.WorkingDirectory, (SqliteTransaction)transaction, cancellationToken);
        await using (var workspaceCommand = Command("INSERT INTO Workspaces VALUES($id,$name,$path,$primary,$sub,$created,$updated);"))
        {
            workspaceCommand.Transaction = (SqliteTransaction)transaction; Add(workspaceCommand, workspace); await workspaceCommand.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var session in sessions)
        {
            await using var sessionCommand = Command("INSERT INTO Sessions VALUES($id,$workspace,$key,$name,$kind,$parent,$profile,$order,$status,$created,$updated);");
            sessionCommand.Transaction = (SqliteTransaction)transaction; Add(sessionCommand, session); await sessionCommand.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var relationship in relationships)
        {
            await using var relationshipCommand = Command("INSERT INTO AgentRelationships(ChildSessionId,WorkspaceId,ParentSessionId,RegisteredAtUtc) VALUES($child,$workspace,$parent,$registered);");
            relationshipCommand.Transaction = (SqliteTransaction)transaction; Add(relationshipCommand, relationship); await relationshipCommand.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }, cancellationToken);

    public Task<IReadOnlyList<SessionMetadata>> LoadSessionsAsync(Guid workspaceId, CancellationToken cancellationToken = default) => LockedAsync(async () =>
    {
        await using var command = Command("SELECT Id,WorkspaceId,SessionKey,DisplayName,AgentKind,ParentSessionId,LaunchProfile,DisplayOrder,LastStatus,CreatedAtUtc,UpdatedAtUtc FROM Sessions WHERE WorkspaceId=$workspace ORDER BY AgentKind,DisplayOrder,CreatedAtUtc;");
        command.Parameters.AddWithValue("$workspace", workspaceId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var sessions = new List<SessionMetadata>();
        while (await reader.ReadAsync(cancellationToken)) sessions.Add(ReadSession(reader));
        return (IReadOnlyList<SessionMetadata>)sessions;
    }, cancellationToken);

    public Task InsertSessionAsync(SessionMetadata session, CancellationToken cancellationToken = default) => LockedAsync(async () =>
    {
        await using var command = Command("INSERT INTO Sessions VALUES($id,$workspace,$key,$name,$kind,$parent,$profile,$order,$status,$created,$updated);");
        Add(command, session);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }, cancellationToken);

    public Task InsertSessionWithRelationshipAsync(SessionMetadata session, AgentRelationship relationship, CancellationToken cancellationToken = default) => LockedAsync(async () =>
    {
        if (session.Id != relationship.ChildSessionId || session.WorkspaceId != relationship.WorkspaceId || session.ParentSessionId != relationship.ParentSessionId)
            throw new ArgumentException("Session and relationship identity must match.", nameof(relationship));
        await using var transaction = await _connection.BeginTransactionAsync(cancellationToken);
        await using (var sessionCommand = Command("INSERT INTO Sessions VALUES($id,$workspace,$key,$name,$kind,$parent,$profile,$order,$status,$created,$updated);"))
        {
            sessionCommand.Transaction = (SqliteTransaction)transaction; Add(sessionCommand, session); await sessionCommand.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var relationshipCommand = Command("INSERT INTO AgentRelationships(ChildSessionId,WorkspaceId,ParentSessionId,RegisteredAtUtc) VALUES($child,$workspace,$parent,$registered);"))
        {
            relationshipCommand.Transaction = (SqliteTransaction)transaction; Add(relationshipCommand, relationship); await relationshipCommand.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }, cancellationToken);

    public Task<IReadOnlyList<AgentRelationship>> LoadRelationshipsAsync(Guid workspaceId, CancellationToken cancellationToken = default) => LockedAsync(async () =>
    {
        await using var command = Command("SELECT WorkspaceId,ParentSessionId,ChildSessionId,RegisteredAtUtc FROM AgentRelationships WHERE WorkspaceId=$workspace ORDER BY RegisteredAtUtc,ChildSessionId;");
        command.Parameters.AddWithValue("$workspace", workspaceId.ToString()); await using var reader = await command.ExecuteReaderAsync(cancellationToken); var relationships = new List<AgentRelationship>();
        while (await reader.ReadAsync(cancellationToken)) relationships.Add(new AgentRelationship(Guid.Parse(reader.GetString(0)),Guid.Parse(reader.GetString(1)),Guid.Parse(reader.GetString(2)),DateTimeOffset.Parse(reader.GetString(3),CultureInfo.InvariantCulture)));
        return (IReadOnlyList<AgentRelationship>)relationships;
    }, cancellationToken);

    public Task UpdateSessionAsync(SessionMetadata session, CancellationToken cancellationToken = default) => LockedAsync(async () =>
    {
        await using var command = Command("UPDATE Sessions SET DisplayName=$name,LaunchProfile=$profile,DisplayOrder=$order,LastStatus=$status,UpdatedAtUtc=$updated WHERE Id=$id;");
        command.Parameters.AddWithValue("$id", session.Id.ToString()); command.Parameters.AddWithValue("$name", session.DisplayName);
        command.Parameters.AddWithValue("$profile", (int)session.LaunchProfile); command.Parameters.AddWithValue("$order", session.DisplayOrder);
        command.Parameters.AddWithValue("$status", (int)session.LastStatus); command.Parameters.AddWithValue("$updated", Format(session.UpdatedAtUtc));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }, cancellationToken);

    public Task UpdateSelectionsAsync(Guid workspaceId, Guid? primaryId, Guid? subAgentId, CancellationToken cancellationToken = default) => LockedAsync(async () =>
    {
        await using var command = Command("UPDATE Workspaces SET SelectedPrimarySessionId=$primary,SelectedSubAgentSessionId=$sub,UpdatedAtUtc=$updated WHERE Id=$id;");
        command.Parameters.AddWithValue("$id", workspaceId.ToString()); command.Parameters.AddWithValue("$primary", Db(primaryId));
        command.Parameters.AddWithValue("$sub", Db(subAgentId)); command.Parameters.AddWithValue("$updated", Format(DateTimeOffset.UtcNow));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }, cancellationToken);

    public Task UpdateStatusAsync(Guid sessionId, AgentStatus status, CancellationToken cancellationToken = default) => LockedAsync(async () =>
    {
        await using var command = Command("UPDATE Sessions SET LastStatus=$status,UpdatedAtUtc=$updated WHERE Id=$id;");
        command.Parameters.AddWithValue("$id", sessionId.ToString()); command.Parameters.AddWithValue("$status", (int)status);
        command.Parameters.AddWithValue("$updated", Format(DateTimeOffset.UtcNow)); await command.ExecuteNonQueryAsync(cancellationToken);
    }, cancellationToken);

    public Task<bool> InsertLifecycleEventAsync(LifecycleEvent item, CancellationToken cancellationToken = default) => LockedAsync(async () =>
    {
        await using var command = Command("INSERT OR IGNORE INTO LifecycleEvents(Id,SessionId,ParentSessionId,PreviousStatus,NewStatus,OccurredAtUtc,AlertEligible) VALUES($id,$session,$parent,$previous,$new,$occurred,$eligible);");
        Add(command, item);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }, cancellationToken);

    public Task<LifecycleEvent?> FindLifecycleEventAsync(Guid eventId, CancellationToken cancellationToken = default) => LockedAsync(async () =>
    {
        await using var command = Command("SELECT Id,SessionId,ParentSessionId,PreviousStatus,NewStatus,OccurredAtUtc,AlertEligible FROM LifecycleEvents WHERE Id=$id;");
        command.Parameters.AddWithValue("$id", eventId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadLifecycleEvent(reader) : null;
    }, cancellationToken);

    public Task<AlertDelivery> EnsureAlertDeliveryAsync(LifecycleEvent item, string message, CancellationToken cancellationToken = default) => LockedAsync(async () =>
    {
        if (!item.AlertEligible || item.ParentSessionId is null) throw new InvalidOperationException("Only eligible parented events can have alert deliveries.");
        await using var transaction = await _connection.BeginTransactionAsync(cancellationToken);
        await using (var eventCommand = Command("INSERT OR IGNORE INTO LifecycleEvents(Id,SessionId,ParentSessionId,PreviousStatus,NewStatus,OccurredAtUtc,AlertEligible) VALUES($id,$session,$parent,$previous,$new,$occurred,$eligible);"))
        {
            eventCommand.Transaction = (SqliteTransaction)transaction; Add(eventCommand, item); await eventCommand.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var deliveryCommand = Command("INSERT OR IGNORE INTO AlertDeliveries(EventId,Message,State,ReservedAtUtc,DeliveredAtUtc,UpdatedAtUtc) VALUES($id,$message,$state,NULL,NULL,$updated);"))
        {
            deliveryCommand.Transaction = (SqliteTransaction)transaction; deliveryCommand.Parameters.AddWithValue("$id", item.Id.ToString());
            deliveryCommand.Parameters.AddWithValue("$message", message); deliveryCommand.Parameters.AddWithValue("$state", (int)AlertDeliveryState.Pending);
            deliveryCommand.Parameters.AddWithValue("$updated", Format(item.OccurredAt)); await deliveryCommand.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return (await FindAlertDeliveryUnlockedAsync(item.Id, cancellationToken))!;
    }, cancellationToken);

    public Task<IReadOnlyList<AlertDelivery>> LoadPendingAlertsAsync(Guid parentSessionId, CancellationToken cancellationToken = default) => LockedAsync(async () =>
    {
        await using var command = Command("SELECT d.EventId,e.ParentSessionId,s.DisplayName,d.Message,d.State,d.ReservedAtUtc,d.DeliveredAtUtc,d.UpdatedAtUtc FROM AlertDeliveries d JOIN LifecycleEvents e ON e.Id=d.EventId JOIN Sessions s ON s.Id=e.SessionId WHERE e.ParentSessionId=$parent AND d.State=$pending ORDER BY e.OccurredAtUtc,e.Id;");
        command.Parameters.AddWithValue("$parent", parentSessionId.ToString()); command.Parameters.AddWithValue("$pending", (int)AlertDeliveryState.Pending);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken); var items = new List<AlertDelivery>();
        while (await reader.ReadAsync(cancellationToken)) items.Add(ReadAlertDelivery(reader));
        return (IReadOnlyList<AlertDelivery>)items;
    }, cancellationToken);

    public Task<bool> ReserveAlertAsync(Guid eventId, DateTimeOffset reservedAtUtc, CancellationToken cancellationToken = default) =>
        ChangeStateAsync(eventId, AlertDeliveryState.Pending, AlertDeliveryState.Reserved, reservedAtUtc, "ReservedAtUtc=$time", cancellationToken);

    public Task<bool> ReserveAlertsAsync(IReadOnlyCollection<Guid> eventIds, DateTimeOffset reservedAtUtc, CancellationToken cancellationToken = default) =>
        ChangeStatesAsync(eventIds, AlertDeliveryState.Pending, AlertDeliveryState.Reserved, reservedAtUtc, "ReservedAtUtc=$time", cancellationToken);

    public Task<bool> CommitAlertAsync(Guid eventId, DateTimeOffset deliveredAtUtc, CancellationToken cancellationToken = default) =>
        ChangeStateAsync(eventId, AlertDeliveryState.Reserved, AlertDeliveryState.Delivered, deliveredAtUtc, "DeliveredAtUtc=$time", cancellationToken);

    public Task<bool> CommitAlertsAsync(IReadOnlyCollection<Guid> eventIds, DateTimeOffset deliveredAtUtc, CancellationToken cancellationToken = default) =>
        ChangeStatesAsync(eventIds, AlertDeliveryState.Reserved, AlertDeliveryState.Delivered, deliveredAtUtc, "DeliveredAtUtc=$time", cancellationToken);

    public Task<bool> ReleaseAlertAsync(Guid eventId, DateTimeOffset updatedAtUtc, CancellationToken cancellationToken = default) =>
        ChangeStateAsync(eventId, AlertDeliveryState.Reserved, AlertDeliveryState.Pending, updatedAtUtc, "ReservedAtUtc=NULL", cancellationToken);

    public Task<bool> ReleaseAlertsAsync(IReadOnlyCollection<Guid> eventIds, DateTimeOffset updatedAtUtc, CancellationToken cancellationToken = default) =>
        ChangeStatesAsync(eventIds, AlertDeliveryState.Reserved, AlertDeliveryState.Pending, updatedAtUtc, "ReservedAtUtc=NULL", cancellationToken);

    public Task<int> RecoverStaleReservationsAsync(DateTimeOffset updatedAtUtc, CancellationToken cancellationToken = default) => LockedAsync(async () =>
    {
        await using var command = Command("UPDATE AlertDeliveries SET State=$pending,ReservedAtUtc=NULL,UpdatedAtUtc=$updated WHERE State=$reserved;");
        command.Parameters.AddWithValue("$pending", (int)AlertDeliveryState.Pending); command.Parameters.AddWithValue("$reserved", (int)AlertDeliveryState.Reserved);
        command.Parameters.AddWithValue("$updated", Format(updatedAtUtc)); return await command.ExecuteNonQueryAsync(cancellationToken);
    }, cancellationToken);

    public Task<AlertDelivery?> FindAlertDeliveryAsync(Guid eventId, CancellationToken cancellationToken = default) =>
        LockedAsync(() => FindAlertDeliveryUnlockedAsync(eventId, cancellationToken), cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return; _disposed = true;
        await _gate.WaitAsync();
        try { await _connection.DisposeAsync(); } finally { _gate.Release(); _gate.Dispose(); }
    }

    private async Task LockedAsync(Func<Task> action, CancellationToken token) { await LockedAsync(async () => { await action(); return true; }, token); }
    private async Task<T> LockedAsync<T>(Func<Task<T>> action, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this); await _gate.WaitAsync(token);
        try { return await action(); } finally { _gate.Release(); }
    }
    private SqliteCommand Command(string text) { var command = _connection.CreateCommand(); command.CommandText = text; return command; }
    private async Task ExecuteAsync(string text, CancellationToken token, System.Data.Common.DbTransaction? transaction = null) { await using var command = Command(text); command.Transaction = (SqliteTransaction?)transaction; await command.ExecuteNonQueryAsync(token); }
    private async Task<object?> ScalarAsync(string text, CancellationToken token) { await using var command = Command(text); return await command.ExecuteScalarAsync(token); }
    private static object Db(Guid? value) => value is Guid id ? id.ToString() : DBNull.Value;
    private static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static Guid? NullableGuid(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : Guid.Parse(reader.GetString(index));
    private static DateTimeOffset? NullableDate(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : DateTimeOffset.Parse(reader.GetString(index), CultureInfo.InvariantCulture);
    private static WorkspaceMetadata ReadWorkspace(SqliteDataReader r) => new(Guid.Parse(r.GetString(0)),r.GetString(1),r.GetString(2),NullableGuid(r,3),NullableGuid(r,4),DateTimeOffset.Parse(r.GetString(5),CultureInfo.InvariantCulture),DateTimeOffset.Parse(r.GetString(6),CultureInfo.InvariantCulture));
    private static SessionMetadata ReadSession(SqliteDataReader r) { var status=(AgentStatus)r.GetInt32(8); if(status is AgentStatus.Queued or AgentStatus.Running or AgentStatus.Waiting) status=AgentStatus.Disconnected; return new(Guid.Parse(r.GetString(0)),Guid.Parse(r.GetString(1)),r.GetString(2),r.GetString(3),(AgentKind)r.GetInt32(4),NullableGuid(r,5),(LaunchProfile)r.GetInt32(6),r.GetInt32(7),status,DateTimeOffset.Parse(r.GetString(9),CultureInfo.InvariantCulture),DateTimeOffset.Parse(r.GetString(10),CultureInfo.InvariantCulture)); }
    private static LifecycleEvent ReadLifecycleEvent(SqliteDataReader r) => new(Guid.Parse(r.GetString(0)),Guid.Parse(r.GetString(1)),NullableGuid(r,2),(AgentStatus)r.GetInt32(3),(AgentStatus)r.GetInt32(4),DateTimeOffset.Parse(r.GetString(5),CultureInfo.InvariantCulture),r.GetInt32(6) != 0);
    private static AlertDelivery ReadAlertDelivery(SqliteDataReader r) => new(Guid.Parse(r.GetString(0)),Guid.Parse(r.GetString(1)),r.GetString(2),r.GetString(3),(AlertDeliveryState)r.GetInt32(4),NullableDate(r,5),NullableDate(r,6),DateTimeOffset.Parse(r.GetString(7),CultureInfo.InvariantCulture));
    private static ApplicationRun ReadApplicationRun(SqliteDataReader r) => new(Guid.Parse(r.GetString(0)),DateTimeOffset.Parse(r.GetString(1),CultureInfo.InvariantCulture),NullableDate(r,2),r.GetString(3));
    private static void Add(SqliteCommand c, WorkspaceMetadata w) { c.Parameters.AddWithValue("$id",w.Id.ToString());c.Parameters.AddWithValue("$name",w.DisplayName);c.Parameters.AddWithValue("$path",StoredWorkspacePath(w.WorkingDirectory));c.Parameters.AddWithValue("$primary",Db(w.SelectedPrimarySessionId));c.Parameters.AddWithValue("$sub",Db(w.SelectedSubAgentSessionId));c.Parameters.AddWithValue("$created",Format(w.CreatedAtUtc));c.Parameters.AddWithValue("$updated",Format(w.UpdatedAtUtc)); }
    private static void Add(SqliteCommand c, SessionMetadata s) { c.Parameters.AddWithValue("$id",s.Id.ToString());c.Parameters.AddWithValue("$workspace",s.WorkspaceId.ToString());c.Parameters.AddWithValue("$key",s.SessionKey);c.Parameters.AddWithValue("$name",s.DisplayName);c.Parameters.AddWithValue("$kind",(int)s.Kind);c.Parameters.AddWithValue("$parent",Db(s.ParentSessionId));c.Parameters.AddWithValue("$profile",(int)s.LaunchProfile);c.Parameters.AddWithValue("$order",s.DisplayOrder);c.Parameters.AddWithValue("$status",(int)s.LastStatus);c.Parameters.AddWithValue("$created",Format(s.CreatedAtUtc));c.Parameters.AddWithValue("$updated",Format(s.UpdatedAtUtc)); }
    private static void Add(SqliteCommand c, LifecycleEvent e) { c.Parameters.AddWithValue("$id",e.Id.ToString());c.Parameters.AddWithValue("$session",e.SessionId.ToString());c.Parameters.AddWithValue("$parent",Db(e.ParentSessionId));c.Parameters.AddWithValue("$previous",(int)e.PreviousStatus);c.Parameters.AddWithValue("$new",(int)e.NewStatus);c.Parameters.AddWithValue("$occurred",Format(e.OccurredAt));c.Parameters.AddWithValue("$eligible",e.AlertEligible ? 1 : 0); }
    private static void Add(SqliteCommand c, AgentRelationship r) { c.Parameters.AddWithValue("$child",r.ChildSessionId.ToString());c.Parameters.AddWithValue("$workspace",r.WorkspaceId.ToString());c.Parameters.AddWithValue("$parent",r.ParentSessionId.ToString());c.Parameters.AddWithValue("$registered",Format(r.RegisteredAtUtc)); }

    private Task<bool> ChangeStateAsync(Guid eventId, AlertDeliveryState expected, AlertDeliveryState next, DateTimeOffset at, string timestampAssignment, CancellationToken token) => LockedAsync(async () =>
    {
        await using var command = Command($"UPDATE AlertDeliveries SET State=$next,{timestampAssignment},UpdatedAtUtc=$time WHERE EventId=$id AND State=$expected;");
        command.Parameters.AddWithValue("$id", eventId.ToString()); command.Parameters.AddWithValue("$expected", (int)expected); command.Parameters.AddWithValue("$next", (int)next); command.Parameters.AddWithValue("$time", Format(at));
        return await command.ExecuteNonQueryAsync(token) == 1;
    }, token);

    private Task<bool> ChangeStatesAsync(IReadOnlyCollection<Guid> eventIds, AlertDeliveryState expected, AlertDeliveryState next, DateTimeOffset at, string timestampAssignment, CancellationToken token) => LockedAsync(async () =>
    {
        if (eventIds.Count == 0) return false;
        await using var transaction = await _connection.BeginTransactionAsync(token);
        foreach (var eventId in eventIds.Distinct())
        {
            await using var command = Command($"UPDATE AlertDeliveries SET State=$next,{timestampAssignment},UpdatedAtUtc=$time WHERE EventId=$id AND State=$expected;");
            command.Transaction = (SqliteTransaction)transaction; command.Parameters.AddWithValue("$id", eventId.ToString());
            command.Parameters.AddWithValue("$expected", (int)expected); command.Parameters.AddWithValue("$next", (int)next); command.Parameters.AddWithValue("$time", Format(at));
            if (await command.ExecuteNonQueryAsync(token) != 1) { await transaction.RollbackAsync(token); return false; }
        }
        await transaction.CommitAsync(token); return true;
    }, token);

    private async Task<AlertDelivery?> FindAlertDeliveryUnlockedAsync(Guid eventId, CancellationToken token)
    {
        await using var command = Command("SELECT d.EventId,e.ParentSessionId,s.DisplayName,d.Message,d.State,d.ReservedAtUtc,d.DeliveredAtUtc,d.UpdatedAtUtc FROM AlertDeliveries d JOIN LifecycleEvents e ON e.Id=d.EventId JOIN Sessions s ON s.Id=e.SessionId WHERE d.EventId=$id;");
        command.Parameters.AddWithValue("$id", eventId.ToString()); await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? ReadAlertDelivery(reader) : null;
    }

    private async Task ReconcileWorkspacePathsAsync(CancellationToken cancellationToken)
    {
        var workspaces = new List<WorkspaceMetadata>();
        await using (var command = Command("SELECT Id,Name,WorkingDirectory,SelectedPrimarySessionId,SelectedSubAgentSessionId,CreatedAtUtc,UpdatedAtUtc FROM Workspaces;"))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                workspaces.Add(ReadWorkspace(reader));
            }
        }

        foreach (var group in workspaces
            .GroupBy(workspace => CanonicalWorkspacePath(workspace.WorkingDirectory), StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var ordered = group.OrderBy(workspace => workspace.CreatedAtUtc).ThenBy(workspace => workspace.Id).ToArray();
            var winner = ordered[0];
            if (ordered.Length == 1 && string.Equals(winner.WorkingDirectory, StoredWorkspacePath(winner.WorkingDirectory), StringComparison.Ordinal))
            {
                continue;
            }
            await using var transaction = (SqliteTransaction)await _connection.BeginTransactionAsync(cancellationToken);
            await ExecuteAsync("PRAGMA defer_foreign_keys = ON;", cancellationToken, transaction);

            var sessionKeys = new HashSet<string>(StringComparer.Ordinal);
            await using (var keysCommand = Command("SELECT SessionKey FROM Sessions WHERE WorkspaceId=$workspace ORDER BY Id;"))
            {
                keysCommand.Transaction = transaction;
                keysCommand.Parameters.AddWithValue("$workspace", winner.Id.ToString());
                await using var reader = await keysCommand.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken)) sessionKeys.Add(reader.GetString(0));
            }

            foreach (var duplicate in ordered.Skip(1))
            {
                var duplicateSessions = new List<(Guid Id, string Key)>();
                await using (var sessionsCommand = Command("SELECT Id,SessionKey FROM Sessions WHERE WorkspaceId=$workspace ORDER BY Id;"))
                {
                    sessionsCommand.Transaction = transaction;
                    sessionsCommand.Parameters.AddWithValue("$workspace", duplicate.Id.ToString());
                    await using var reader = await sessionsCommand.ExecuteReaderAsync(cancellationToken);
                    while (await reader.ReadAsync(cancellationToken)) duplicateSessions.Add((Guid.Parse(reader.GetString(0)), reader.GetString(1)));
                }

                foreach (var session in duplicateSessions)
                {
                    var key = session.Key;
                    if (!sessionKeys.Add(key))
                    {
                        key = $"{key}-{session.Id:N}";
                        sessionKeys.Add(key);
                        await using var keyCommand = Command("UPDATE Sessions SET SessionKey=$key WHERE Id=$id;");
                        keyCommand.Transaction = transaction;
                        keyCommand.Parameters.AddWithValue("$key", key);
                        keyCommand.Parameters.AddWithValue("$id", session.Id.ToString());
                        await keyCommand.ExecuteNonQueryAsync(cancellationToken);
                    }
                }

                await using (var sessionsCommand = Command("UPDATE Sessions SET WorkspaceId=$winner WHERE WorkspaceId=$duplicate;"))
                {
                    sessionsCommand.Transaction = transaction;
                    sessionsCommand.Parameters.AddWithValue("$winner", winner.Id.ToString());
                    sessionsCommand.Parameters.AddWithValue("$duplicate", duplicate.Id.ToString());
                    await sessionsCommand.ExecuteNonQueryAsync(cancellationToken);
                }
                await using (var relationshipsCommand = Command("UPDATE AgentRelationships SET WorkspaceId=$winner WHERE WorkspaceId=$duplicate;"))
                {
                    relationshipsCommand.Transaction = transaction;
                    relationshipsCommand.Parameters.AddWithValue("$winner", winner.Id.ToString());
                    relationshipsCommand.Parameters.AddWithValue("$duplicate", duplicate.Id.ToString());
                    await relationshipsCommand.ExecuteNonQueryAsync(cancellationToken);
                }
                await using (var deleteCommand = Command("DELETE FROM Workspaces WHERE Id=$duplicate;"))
                {
                    deleteCommand.Transaction = transaction;
                    deleteCommand.Parameters.AddWithValue("$duplicate", duplicate.Id.ToString());
                    await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
                }
            }

            await using (var normalizeCommand = Command("UPDATE Workspaces SET WorkingDirectory=$path WHERE Id=$id;"))
            {
                normalizeCommand.Transaction = transaction;
                normalizeCommand.Parameters.AddWithValue("$path", StoredWorkspacePath(winner.WorkingDirectory));
                normalizeCommand.Parameters.AddWithValue("$id", winner.Id.ToString());
                await normalizeCommand.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }
    }

    private static string CanonicalWorkspacePath(string path) =>
        StoredWorkspacePath(path).ToUpperInvariant();

    private static string StoredWorkspacePath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private async Task EnsureWorkspacePathAvailableAsync(string path, SqliteTransaction? transaction, CancellationToken cancellationToken)
    {
        var identity = CanonicalWorkspacePath(path);
        await using var command = Command("SELECT WorkingDirectory FROM Workspaces;");
        command.Transaction = transaction;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (string.Equals(CanonicalWorkspacePath(reader.GetString(0)), identity, StringComparison.Ordinal))
                throw new InvalidOperationException($"A workspace already exists for '{StoredWorkspacePath(path)}'.");
        }
    }
}
