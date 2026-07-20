using System.Globalization;
using Halfway.Core;
using Microsoft.Data.Sqlite;

namespace Halfway.Persistence;

public sealed class SqliteWorkspaceStore : IWorkspaceStore
{
    public const int SchemaVersion = 1;
    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public SqliteWorkspaceStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (directory is not null) Directory.CreateDirectory(directory);
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
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
                PRAGMA user_version = 1;
                """, cancellationToken, transaction);
            await transaction.CommitAsync(cancellationToken);
        }
    }, cancellationToken);

    public Task<WorkspaceMetadata?> FindWorkspaceAsync(string workingDirectory, CancellationToken cancellationToken = default) => LockedAsync(async () =>
    {
        await using var command = Command("SELECT Id,Name,WorkingDirectory,SelectedPrimarySessionId,SelectedSubAgentSessionId,CreatedAtUtc,UpdatedAtUtc FROM Workspaces WHERE WorkingDirectory=$path;");
        command.Parameters.AddWithValue("$path", Path.GetFullPath(workingDirectory));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadWorkspace(reader) : null;
    }, cancellationToken);

    public Task InsertWorkspaceAsync(WorkspaceMetadata workspace, CancellationToken cancellationToken = default) => LockedAsync(async () =>
    {
        await using var command = Command("INSERT INTO Workspaces VALUES($id,$name,$path,$primary,$sub,$created,$updated);");
        Add(command, workspace);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }, cancellationToken);

    public Task InsertInitialWorkspaceAsync(WorkspaceMetadata workspace, IReadOnlyList<SessionMetadata> sessions, CancellationToken cancellationToken = default) => LockedAsync(async () =>
    {
        await using var transaction = await _connection.BeginTransactionAsync(cancellationToken);
        await using (var workspaceCommand = Command("INSERT INTO Workspaces VALUES($id,$name,$path,$primary,$sub,$created,$updated);"))
        {
            workspaceCommand.Transaction = (SqliteTransaction)transaction; Add(workspaceCommand, workspace); await workspaceCommand.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var session in sessions)
        {
            await using var sessionCommand = Command("INSERT INTO Sessions VALUES($id,$workspace,$key,$name,$kind,$parent,$profile,$order,$status,$created,$updated);");
            sessionCommand.Transaction = (SqliteTransaction)transaction; Add(sessionCommand, session); await sessionCommand.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }, cancellationToken);

    public Task<IReadOnlyList<SessionMetadata>> LoadSessionsAsync(Guid workspaceId, CancellationToken cancellationToken = default) => LockedAsync(async () =>
    {
        await using var command = Command("SELECT Id,WorkspaceId,SessionKey,DisplayName,AgentKind,ParentSessionId,LaunchProfile,DisplayOrder,LastStatus,CreatedAtUtc,UpdatedAtUtc FROM Sessions WHERE WorkspaceId=$workspace ORDER BY DisplayOrder,CreatedAtUtc;");
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
    private static WorkspaceMetadata ReadWorkspace(SqliteDataReader r) => new(Guid.Parse(r.GetString(0)),r.GetString(1),r.GetString(2),NullableGuid(r,3),NullableGuid(r,4),DateTimeOffset.Parse(r.GetString(5),CultureInfo.InvariantCulture),DateTimeOffset.Parse(r.GetString(6),CultureInfo.InvariantCulture));
    private static SessionMetadata ReadSession(SqliteDataReader r) { var status=(AgentStatus)r.GetInt32(8); if(status is AgentStatus.Queued or AgentStatus.Running or AgentStatus.Waiting) status=AgentStatus.Disconnected; return new(Guid.Parse(r.GetString(0)),Guid.Parse(r.GetString(1)),r.GetString(2),r.GetString(3),(AgentKind)r.GetInt32(4),NullableGuid(r,5),(LaunchProfile)r.GetInt32(6),r.GetInt32(7),status,DateTimeOffset.Parse(r.GetString(9),CultureInfo.InvariantCulture),DateTimeOffset.Parse(r.GetString(10),CultureInfo.InvariantCulture)); }
    private static void Add(SqliteCommand c, WorkspaceMetadata w) { c.Parameters.AddWithValue("$id",w.Id.ToString());c.Parameters.AddWithValue("$name",w.DisplayName);c.Parameters.AddWithValue("$path",Path.GetFullPath(w.WorkingDirectory));c.Parameters.AddWithValue("$primary",Db(w.SelectedPrimarySessionId));c.Parameters.AddWithValue("$sub",Db(w.SelectedSubAgentSessionId));c.Parameters.AddWithValue("$created",Format(w.CreatedAtUtc));c.Parameters.AddWithValue("$updated",Format(w.UpdatedAtUtc)); }
    private static void Add(SqliteCommand c, SessionMetadata s) { c.Parameters.AddWithValue("$id",s.Id.ToString());c.Parameters.AddWithValue("$workspace",s.WorkspaceId.ToString());c.Parameters.AddWithValue("$key",s.SessionKey);c.Parameters.AddWithValue("$name",s.DisplayName);c.Parameters.AddWithValue("$kind",(int)s.Kind);c.Parameters.AddWithValue("$parent",Db(s.ParentSessionId));c.Parameters.AddWithValue("$profile",(int)s.LaunchProfile);c.Parameters.AddWithValue("$order",s.DisplayOrder);c.Parameters.AddWithValue("$status",(int)s.LastStatus);c.Parameters.AddWithValue("$created",Format(s.CreatedAtUtc));c.Parameters.AddWithValue("$updated",Format(s.UpdatedAtUtc)); }
}
