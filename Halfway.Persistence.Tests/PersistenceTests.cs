using Halfway.Core;
using Microsoft.Data.Sqlite;

namespace Halfway.Persistence.Tests;

public sealed class PersistenceTests : IAsyncDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Halfway.Tests", Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(_directory, "test.db");

    [Fact]
    public async Task EmptyDatabaseInitializesAndReopensAtVersionFour()
    {
        await using (var store = new SqliteWorkspaceStore(Database)) await store.InitializeAsync();
        await using var reopened = new SqliteWorkspaceStore(Database); await reopened.InitializeAsync();
        await using var connection = new SqliteConnection($"Data Source={Database}"); await connection.OpenAsync();
        await using var command = connection.CreateCommand(); command.CommandText = "PRAGMA user_version;";
        Assert.Equal(4L, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task InterruptedInitialSchemaCreationRollsBackAndReopensSafely()
    {
        var interrupted = new SqliteWorkspaceStore(Database, _ => throw new IOException("injected initialization failure"));
        await Assert.ThrowsAsync<IOException>(() => interrupted.InitializeAsync());
        await interrupted.DisposeAsync();

        await using (var connection = new SqliteConnection($"Data Source={Database}"))
        {
            await connection.OpenAsync();
            await using var versionCommand = connection.CreateCommand();
            versionCommand.CommandText = "PRAGMA user_version;";
            Assert.Equal(0L, await versionCommand.ExecuteScalarAsync());
            await using var schemaCommand = connection.CreateCommand();
            schemaCommand.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type IN ('table','index') AND name NOT LIKE 'sqlite_%';";
            Assert.Equal(0L, await schemaCommand.ExecuteScalarAsync());
        }

        await using var reopened = new SqliteWorkspaceStore(Database);
        await reopened.InitializeAsync();
        Assert.Equal(4L, await ScalarAsync("PRAGMA user_version;"));
    }

    [Fact]
    public async Task WorkspaceIdentityIsCaseInsensitiveAndPreservesSessionsAndRelationships()
    {
        Guid workspaceId;
        Guid[] sessionIds;
        await using (var store = new SqliteWorkspaceStore(Database))
        {
            var catalog = new WorkspaceCatalog(store);
            await catalog.InitializeAsync(_directory.ToLowerInvariant(), LaunchProfile.PowerShell);
            workspaceId = catalog.Workspace.Id;
            await catalog.CreateSubAgentAsync("Tests", LaunchProfile.Codex);
            sessionIds = catalog.Sessions.Select(session => session.Id).Order().ToArray();

            Assert.Equal(workspaceId, (await store.FindWorkspaceAsync(_directory.ToUpperInvariant()))!.Id);
        }

        await using var reopened = new SqliteWorkspaceStore(Database);
        var restored = new WorkspaceCatalog(reopened);
        await restored.InitializeAsync(_directory.ToUpperInvariant(), LaunchProfile.PowerShell);

        Assert.Equal(workspaceId, restored.Workspace.Id);
        Assert.Equal(sessionIds, restored.Sessions.Select(session => session.Id).Order());
        Assert.Equal(2, restored.Relationships.Count);
    }

    [Fact]
    public async Task ExistingCaseDuplicateWorkspacesMergeTransactionallyWithoutSessionLoss()
    {
        Guid winnerId;
        var duplicateId = Guid.NewGuid();
        var duplicateParentId = Guid.NewGuid();
        var duplicateChildId = Guid.NewGuid();
        var now = DateTimeOffset.MaxValue.AddDays(-1).ToString("O");
        await using (var store = new SqliteWorkspaceStore(Database))
        {
            var catalog = new WorkspaceCatalog(store);
            await catalog.InitializeAsync(_directory.ToLowerInvariant(), LaunchProfile.PowerShell);
            winnerId = catalog.Workspace.Id;
        }

        await using (var connection = new SqliteConnection($"Data Source={Database}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA foreign_keys=ON;
                INSERT INTO Workspaces VALUES($workspace,'Duplicate',$path,$parent,$child,$now,$now);
                INSERT INTO Sessions VALUES($parent,$workspace,'duplicate-parent','Duplicate Planner',0,NULL,0,0,5,$now,$now);
                INSERT INTO Sessions VALUES($child,$workspace,'duplicate-child','Duplicate Child',1,$parent,1,0,5,$now,$now);
                INSERT INTO AgentRelationships VALUES($child,$workspace,$parent,$now);
                """;
            command.Parameters.AddWithValue("$workspace", duplicateId.ToString());
            command.Parameters.AddWithValue("$path", _directory.ToUpperInvariant());
            command.Parameters.AddWithValue("$parent", duplicateParentId.ToString());
            command.Parameters.AddWithValue("$child", duplicateChildId.ToString());
            command.Parameters.AddWithValue("$now", now);
            await command.ExecuteNonQueryAsync();
        }

        await using var reopened = new SqliteWorkspaceStore(Database);
        await reopened.InitializeAsync();
        var restored = new WorkspaceCatalog(reopened);
        await restored.InitializeAsync(_directory.ToUpperInvariant(), LaunchProfile.PowerShell);

        Assert.Equal(winnerId, restored.Workspace.Id);
        Assert.Contains(restored.Sessions, session => session.Id == duplicateParentId);
        Assert.Contains(restored.Sessions, session => session.Id == duplicateChildId && session.ParentSessionId == duplicateParentId);
        Assert.Contains(restored.Relationships, relationship => relationship.ChildSessionId == duplicateChildId && relationship.ParentSessionId == duplicateParentId);
        Assert.Equal(1L, await ScalarAsync("SELECT COUNT(*) FROM Workspaces;"));
        Assert.Equal(0L, await ScalarAsync($"SELECT COUNT(*) FROM Sessions WHERE WorkspaceId='{duplicateId}';"));
    }

    [Fact]
    public async Task ApplicationRunStartsAndCompletesCleanly()
    {
        var startedAt = DateTimeOffset.UnixEpoch.AddHours(1);
        var stoppedAt = startedAt.AddMinutes(5);
        var run = new ApplicationRun(Guid.NewGuid(), startedAt, null, "1.2.3");
        await using var store = new SqliteWorkspaceStore(Database); await store.InitializeAsync();

        var start = await store.StartApplicationRunAsync(run);
        Assert.Null(start.PreviousRun);
        Assert.False(start.PreviousRunWasUnclean);
        Assert.True(await store.CompleteApplicationRunAsync(run.Id, stoppedAt));
        Assert.False(await store.CompleteApplicationRunAsync(run.Id, stoppedAt.AddSeconds(1)));

        var saved = Assert.Single(await store.LoadApplicationRunsAsync());
        Assert.Equal(run with { CleanShutdownAtUtc = stoppedAt }, saved);
        Assert.True(saved.EndedCleanly);
    }

    [Fact]
    public async Task ReopeningDetectsOnlyAnUnfinishedImmediatelyPreviousRun()
    {
        var first = new ApplicationRun(Guid.NewGuid(), DateTimeOffset.UnixEpoch.AddHours(1), null, "1.0");
        await using (var store = new SqliteWorkspaceStore(Database))
        {
            await store.InitializeAsync();
            await store.StartApplicationRunAsync(first);
        }

        var second = new ApplicationRun(Guid.NewGuid(), DateTimeOffset.UnixEpoch.AddHours(2), null, "1.1");
        await using (var reopened = new SqliteWorkspaceStore(Database))
        {
            await reopened.InitializeAsync();
            var detected = await reopened.StartApplicationRunAsync(second);
            Assert.True(detected.PreviousRunWasUnclean);
            Assert.Equal(first, detected.PreviousRun);
            Assert.True(await reopened.CompleteApplicationRunAsync(second.Id, second.StartedAtUtc.AddMinutes(1)));
        }

        await using var thirdStore = new SqliteWorkspaceStore(Database); await thirdStore.InitializeAsync();
        var third = new ApplicationRun(Guid.NewGuid(), DateTimeOffset.UnixEpoch.AddHours(3), null, "1.2");
        var cleanPrevious = await thirdStore.StartApplicationRunAsync(third);
        Assert.False(cleanPrevious.PreviousRunWasUnclean);
        Assert.Equal(second.Id, cleanPrevious.PreviousRun!.Id);
        Assert.Equal([first.Id, second.Id, third.Id], (await thirdStore.LoadApplicationRunsAsync()).Select(run => run.Id));
    }

    [Fact]
    public async Task CrashDetectionDoesNotChangeLifecycleAlertsOrRestoredActiveStatus()
    {
        Guid parentId, childId, reservedId, deliveredId;
        await using (var store = new SqliteWorkspaceStore(Database))
        {
            var catalog = new WorkspaceCatalog(store); await catalog.InitializeAsync(_directory, LaunchProfile.PowerShell);
            parentId = catalog.SelectedPrimary!.Id; childId = catalog.SelectedSubAgent!.Id;
            await catalog.UpdateStatusAsync(childId, AgentStatus.Running);
            var reserved = Event(childId, parentId); reservedId = reserved.Id;
            await store.EnsureAlertDeliveryAsync(reserved, "reserved"); await store.ReserveAlertAsync(reservedId, DateTimeOffset.UtcNow);
            var delivered = Event(childId, parentId); deliveredId = delivered.Id;
            await store.EnsureAlertDeliveryAsync(delivered, "delivered"); await store.ReserveAlertAsync(deliveredId, DateTimeOffset.UtcNow); await store.CommitAlertAsync(deliveredId, DateTimeOffset.UtcNow);
            await store.StartApplicationRunAsync(new ApplicationRun(Guid.NewGuid(), DateTimeOffset.UnixEpoch, null, "1.0"));
        }

        await using var reopened = new SqliteWorkspaceStore(Database); var restored = new WorkspaceCatalog(reopened);
        await restored.InitializeAsync(_directory, LaunchProfile.PowerShell);
        var detected = await reopened.StartApplicationRunAsync(new ApplicationRun(Guid.NewGuid(), DateTimeOffset.UnixEpoch.AddHours(1), null, "1.1"));
        Assert.True(detected.PreviousRunWasUnclean);
        Assert.Equal(AgentStatus.Disconnected, restored.Sessions.Single(session => session.Id == childId).LastStatus);
        Assert.Equal(1, await reopened.RecoverStaleReservationsAsync(DateTimeOffset.UtcNow));
        Assert.Equal(reservedId, Assert.Single(await reopened.LoadPendingAlertsAsync(parentId)).EventId);
        Assert.Equal(AlertDeliveryState.Delivered, (await reopened.FindAlertDeliveryAsync(deliveredId))!.State);
        Assert.Equal(2L, await ScalarAsync("SELECT COUNT(*) FROM LifecycleEvents;"));
        Assert.Equal(2L, await ScalarAsync("SELECT COUNT(*) FROM AlertDeliveries;"));
    }

    [Fact]
    public async Task VersionThreeMigrationPreservesAllExistingDataAndAddsApplicationRuns()
    {
        Guid workspaceId, parentId, childId, eventId;
        await using (var store = new SqliteWorkspaceStore(Database))
        {
            var catalog = new WorkspaceCatalog(store); await catalog.InitializeAsync(_directory, LaunchProfile.Codex);
            workspaceId = catalog.Workspace.Id; parentId = catalog.SelectedPrimary!.Id; childId = catalog.SelectedSubAgent!.Id;
            var lifecycleEvent = Event(childId, parentId); eventId = lifecycleEvent.Id;
            await store.EnsureAlertDeliveryAsync(lifecycleEvent, "preserved");
        }
        await using (var connection = new SqliteConnection($"Data Source={Database}"))
        {
            await connection.OpenAsync(); await using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE ApplicationRuns; PRAGMA user_version=3;";
            await command.ExecuteNonQueryAsync();
        }

        await using var migrated = new SqliteWorkspaceStore(Database); await migrated.InitializeAsync();
        Assert.Equal(workspaceId, (await migrated.FindWorkspaceAsync(_directory))!.Id);
        Assert.Equal([parentId, childId], (await migrated.LoadSessionsAsync(workspaceId)).Select(session => session.Id));
        Assert.Equal(childId, Assert.Single(await migrated.LoadRelationshipsAsync(workspaceId)).ChildSessionId);
        Assert.Equal(eventId, (await migrated.FindLifecycleEventAsync(eventId))!.Id);
        Assert.Equal("preserved", (await migrated.FindAlertDeliveryAsync(eventId))!.Message);
        Assert.Empty(await migrated.LoadApplicationRunsAsync());
        Assert.Equal(4L, await ScalarAsync("PRAGMA user_version;"));
    }

    [Fact]
    public async Task VersionTwoMigrationPreservesSessionsAndBackfillsRelationships()
    {
        Guid workspaceId;
        Guid parentId;
        Guid childId;
        await using (var store = new SqliteWorkspaceStore(Database))
        {
            var catalog = new WorkspaceCatalog(store);
            await catalog.InitializeAsync(_directory, LaunchProfile.PowerShell);
            workspaceId = catalog.Workspace.Id;
            parentId = catalog.SelectedPrimary!.Id;
            childId = catalog.SelectedSubAgent!.Id;
        }
        await using (var connection = new SqliteConnection($"Data Source={Database}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE ApplicationRuns; DROP TABLE AgentRelationships; DROP INDEX IX_Sessions_WorkspaceId_Id; PRAGMA user_version=2;";
            await command.ExecuteNonQueryAsync();
        }

        await using var migrated = new SqliteWorkspaceStore(Database);
        await migrated.InitializeAsync();

        Assert.Equal([parentId, childId], (await migrated.LoadSessionsAsync(workspaceId)).Select(session => session.Id));
        Assert.Equal(new AgentRelationship(workspaceId, parentId, childId, (await migrated.LoadSessionsAsync(workspaceId))[1].CreatedAtUtc), Assert.Single(await migrated.LoadRelationshipsAsync(workspaceId)));
        Assert.Equal(4L, await ScalarAsync("PRAGMA user_version;"));
    }

    [Fact]
    public async Task PopulatedVersionOneDatabaseMigratesWithoutChangingMetadata()
    {
        var workspaceId = Guid.NewGuid(); var plannerId = Guid.NewGuid(); var runtimeId = Guid.NewGuid(); var now = DateTimeOffset.UtcNow.ToString("O");
        Directory.CreateDirectory(_directory);
        await using (var connection = new SqliteConnection($"Data Source={Database}"))
        {
            await connection.OpenAsync(); await using var command = connection.CreateCommand(); command.CommandText = """
                PRAGMA foreign_keys=ON;
                CREATE TABLE Workspaces (Id TEXT PRIMARY KEY,Name TEXT NOT NULL,WorkingDirectory TEXT NOT NULL UNIQUE,SelectedPrimarySessionId TEXT NULL,SelectedSubAgentSessionId TEXT NULL,CreatedAtUtc TEXT NOT NULL,UpdatedAtUtc TEXT NOT NULL);
                CREATE TABLE Sessions (Id TEXT PRIMARY KEY,WorkspaceId TEXT NOT NULL,SessionKey TEXT NOT NULL,DisplayName TEXT NOT NULL,AgentKind INTEGER NOT NULL,ParentSessionId TEXT NULL,LaunchProfile INTEGER NOT NULL,DisplayOrder INTEGER NOT NULL,LastStatus INTEGER NOT NULL,CreatedAtUtc TEXT NOT NULL,UpdatedAtUtc TEXT NOT NULL,UNIQUE(WorkspaceId,SessionKey),FOREIGN KEY(WorkspaceId) REFERENCES Workspaces(Id),FOREIGN KEY(ParentSessionId) REFERENCES Sessions(Id));
                INSERT INTO Workspaces VALUES($workspace,'Existing',$path,$planner,$runtime,$now,$now);
                INSERT INTO Sessions VALUES($planner,$workspace,'planner','Planner',0,NULL,0,0,1,$now,$now);
                INSERT INTO Sessions VALUES($runtime,$workspace,'runtime','Runtime',1,$planner,0,0,3,$now,$now);
                PRAGMA user_version=1;
                """;
            command.Parameters.AddWithValue("$workspace", workspaceId.ToString()); command.Parameters.AddWithValue("$path", _directory);
            command.Parameters.AddWithValue("$planner", plannerId.ToString()); command.Parameters.AddWithValue("$runtime", runtimeId.ToString()); command.Parameters.AddWithValue("$now", now);
            await command.ExecuteNonQueryAsync();
        }
        await using (var store = new SqliteWorkspaceStore(Database)) await store.InitializeAsync();
        await using var reopened = new SqliteWorkspaceStore(Database); await reopened.InitializeAsync();
        var workspace = await reopened.FindWorkspaceAsync(_directory); var sessions = await reopened.LoadSessionsAsync(workspaceId);
        Assert.Equal(workspaceId, workspace!.Id); Assert.Equal(plannerId, workspace.SelectedPrimarySessionId); Assert.Equal(runtimeId, workspace.SelectedSubAgentSessionId);
        Assert.Equal([plannerId, runtimeId], sessions.Select(x => x.Id));
        Assert.Equal(new AgentRelationship(workspaceId, plannerId, runtimeId, sessions[1].CreatedAtUtc), Assert.Single(await reopened.LoadRelationshipsAsync(workspaceId)));
    }

    [Fact]
    public async Task LifecycleAndDeliveryStatesRoundTripAndEnforceForeignKeys()
    {
        await using var store = new SqliteWorkspaceStore(Database); var catalog = new WorkspaceCatalog(store); await catalog.InitializeAsync(_directory, LaunchProfile.PowerShell);
        var child = catalog.SelectedSubAgent!; var item = Event(child.Id, child.ParentSessionId!.Value); var message = "[Halfway Alert!] Runtime completed. Continue orchestration.";
        Assert.True(await store.InsertLifecycleEventAsync(item)); Assert.False(await store.InsertLifecycleEventAsync(item));
        Assert.Equal(item, await store.FindLifecycleEventAsync(item.Id));
        var pending = await store.EnsureAlertDeliveryAsync(item, message); Assert.Equal(AlertDeliveryState.Pending, pending.State);
        Assert.Single(await store.LoadPendingAlertsAsync(child.ParentSessionId.Value));
        Assert.True(await store.ReserveAlertAsync(item.Id, DateTimeOffset.UtcNow)); Assert.False(await store.ReserveAlertAsync(item.Id, DateTimeOffset.UtcNow));
        Assert.True(await store.ReleaseAlertAsync(item.Id, DateTimeOffset.UtcNow)); Assert.Equal(AlertDeliveryState.Pending, (await store.FindAlertDeliveryAsync(item.Id))!.State);
        Assert.True(await store.ReserveAlertAsync(item.Id, DateTimeOffset.UtcNow)); Assert.True(await store.CommitAlertAsync(item.Id, DateTimeOffset.UtcNow));
        Assert.Equal(AlertDeliveryState.Delivered, (await store.FindAlertDeliveryAsync(item.Id))!.State);
        await Assert.ThrowsAsync<SqliteException>(() => store.InsertLifecycleEventAsync(Event(Guid.NewGuid(), child.ParentSessionId.Value)));
        await Assert.ThrowsAsync<SqliteException>(() => store.InsertLifecycleEventAsync(Event(child.Id, Guid.NewGuid())));
    }

    [Fact]
    public async Task ReopeningRecoversReservedButNeverDeliveredAlerts()
    {
        Guid pendingId, deliveredId, parentId;
        await using (var store = new SqliteWorkspaceStore(Database))
        {
            var catalog = new WorkspaceCatalog(store); await catalog.InitializeAsync(_directory, LaunchProfile.PowerShell); var child = catalog.SelectedSubAgent!; parentId = child.ParentSessionId!.Value;
            var pending = Event(child.Id, parentId); var delivered = Event(child.Id, parentId); pendingId = pending.Id; deliveredId = delivered.Id;
            await store.EnsureAlertDeliveryAsync(pending, "pending"); await store.ReserveAlertAsync(pendingId, DateTimeOffset.UtcNow);
            await store.EnsureAlertDeliveryAsync(delivered, "delivered"); await store.ReserveAlertAsync(deliveredId, DateTimeOffset.UtcNow); await store.CommitAlertAsync(deliveredId, DateTimeOffset.UtcNow);
        }
        await using var reopened = new SqliteWorkspaceStore(Database); await reopened.InitializeAsync(); Assert.Equal(1, await reopened.RecoverStaleReservationsAsync(DateTimeOffset.UtcNow));
        Assert.Equal(pendingId, Assert.Single(await reopened.LoadPendingAlertsAsync(parentId)).EventId);
        Assert.Equal(AlertDeliveryState.Delivered, (await reopened.FindAlertDeliveryAsync(deliveredId))!.State);
    }

    [Fact]
    public async Task SchemaContainsNoTerminalPromptsTranscriptsOrSecrets()
    {
        await using (var store = new SqliteWorkspaceStore(Database)) await store.InitializeAsync();
        await using var connection = new SqliteConnection($"Data Source={Database}"); await connection.OpenAsync(); await using var command = connection.CreateCommand();
        command.CommandText = "SELECT group_concat(sql, ' ') FROM sqlite_master WHERE sql IS NOT NULL;"; var schema = ((string)(await command.ExecuteScalarAsync())!).ToLowerInvariant();
        Assert.DoesNotContain("transcript", schema); Assert.DoesNotContain("prompt", schema); Assert.DoesNotContain("partialinput", schema); Assert.DoesNotContain("submittedinput", schema);
        Assert.DoesNotContain("environment", schema); Assert.DoesNotContain("apikey", schema); Assert.DoesNotContain("token", schema); Assert.DoesNotContain("secret", schema);
        Assert.DoesNotContain("terminaloutput", schema); Assert.DoesNotContain("processid", schema); Assert.DoesNotContain("processhandle", schema);
    }

    [Fact]
    public async Task CatalogSeedsStablePlannerAndRuntimeAndRestoresSelections()
    {
        Guid workspaceId, plannerId, runtimeId;
        await using (var store = new SqliteWorkspaceStore(Database))
        {
            var catalog = new WorkspaceCatalog(store); await catalog.InitializeAsync(_directory, LaunchProfile.Codex);
            workspaceId = catalog.Workspace.Id; plannerId = catalog.SelectedPrimary!.Id; runtimeId = catalog.SelectedSubAgent!.Id;
            Assert.Equal("Planner", catalog.SelectedPrimary.DisplayName); Assert.Equal("Runtime", catalog.SelectedSubAgent.DisplayName);
            Assert.Equal(LaunchProfile.Codex, catalog.SelectedSubAgent.LaunchProfile); Assert.Equal(plannerId, catalog.SelectedSubAgent.ParentSessionId);
        }
        await using var reopened = new SqliteWorkspaceStore(Database); var restored = new WorkspaceCatalog(reopened);
        await restored.InitializeAsync(_directory, LaunchProfile.PowerShell);
        Assert.Equal(workspaceId, restored.Workspace.Id); Assert.Equal(plannerId, restored.SelectedPrimary!.Id); Assert.Equal(runtimeId, restored.SelectedSubAgent!.Id);
        Assert.All(restored.Sessions, session => Assert.Equal(AgentStatus.Disconnected, session.LastStatus));
    }

    [Theory]
    [InlineData(AgentStatus.Completed)]
    [InlineData(AgentStatus.Failed)]
    [InlineData(AgentStatus.Running)]
    public async Task StatusRoundTripsWithoutRestoringActiveState(AgentStatus status)
    {
        await using (var store = new SqliteWorkspaceStore(Database)) { var catalog = new WorkspaceCatalog(store); await catalog.InitializeAsync(_directory, LaunchProfile.PowerShell); await catalog.UpdateStatusAsync(catalog.SelectedSubAgent!.Id, status); }
        await using var reopened = new SqliteWorkspaceStore(Database); var restored = new WorkspaceCatalog(reopened); await restored.InitializeAsync(_directory, LaunchProfile.PowerShell);
        Assert.Equal(status == AgentStatus.Running ? AgentStatus.Disconnected : status, restored.SelectedSubAgent!.LastStatus);
    }

    [Fact]
    public async Task SessionRoundTripPreservesMetadataAndDuplicateKeyIsRejected()
    {
        await using var store = new SqliteWorkspaceStore(Database); var catalog = new WorkspaceCatalog(store); await catalog.InitializeAsync(_directory, LaunchProfile.PowerShell);
        var session = await catalog.CreateSubAgentAsync("Tests", LaunchProfile.Codex);
        var loaded = (await store.LoadSessionsAsync(catalog.Workspace.Id)).Single(x => x.Id == session.Id);
        Assert.Equal(session with { LastStatus = AgentStatus.Disconnected }, loaded with { LastStatus = AgentStatus.Disconnected });
        await Assert.ThrowsAsync<SqliteException>(() => store.InsertSessionAsync(session with { Id = Guid.NewGuid() }));
    }

    [Fact]
    public async Task ForeignKeysAreEnforced()
    {
        await using var store = new SqliteWorkspaceStore(Database); await store.InitializeAsync(); var now = DateTimeOffset.UtcNow;
        var orphan = new SessionMetadata(Guid.NewGuid(), Guid.NewGuid(), "orphan", "Orphan", AgentKind.SubAgent, null, LaunchProfile.PowerShell, 0, AgentStatus.Queued, now, now);
        await Assert.ThrowsAsync<SqliteException>(() => store.InsertSessionAsync(orphan));
    }

    [Fact]
    public async Task RelationshipRegistrationIsAtomicAndEnforcesWorkspaceIdentity()
    {
        await using var store = new SqliteWorkspaceStore(Database); var catalog = new WorkspaceCatalog(store); await catalog.InitializeAsync(_directory, LaunchProfile.PowerShell);
        var now = DateTimeOffset.UtcNow; var childId = Guid.NewGuid(); var invalidParent = Guid.NewGuid();
        var session = new SessionMetadata(childId, catalog.Workspace.Id, $"session-{childId:N}", "Invalid", AgentKind.SubAgent, invalidParent, LaunchProfile.PowerShell, 10, AgentStatus.Queued, now, now);
        var relationship = new AgentRelationship(catalog.Workspace.Id, invalidParent, childId, now);

        await Assert.ThrowsAsync<SqliteException>(() => store.InsertSessionWithRelationshipAsync(session, relationship));

        Assert.DoesNotContain(await store.LoadSessionsAsync(catalog.Workspace.Id), x => x.Id == childId);
        Assert.DoesNotContain(await store.LoadRelationshipsAsync(catalog.Workspace.Id), x => x.ChildSessionId == childId);
    }

    [Fact]
    public async Task InvalidSelectionsFallBackInDisplayOrderAndPersist()
    {
        await using (var store = new SqliteWorkspaceStore(Database)) { var catalog = new WorkspaceCatalog(store); await catalog.InitializeAsync(_directory, LaunchProfile.PowerShell); await store.UpdateSelectionsAsync(catalog.Workspace.Id, Guid.NewGuid(), Guid.NewGuid()); }
        await using var reopened = new SqliteWorkspaceStore(Database); var restored = new WorkspaceCatalog(reopened); await restored.InitializeAsync(_directory, LaunchProfile.PowerShell);
        Assert.Equal("Planner", restored.SelectedPrimary!.DisplayName); Assert.Equal("Runtime", restored.SelectedSubAgent!.DisplayName);
    }

    public ValueTask DisposeAsync() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); return ValueTask.CompletedTask; }

    private async Task<long> ScalarAsync(string commandText)
    {
        await using var connection = new SqliteConnection($"Data Source={Database}"); await connection.OpenAsync();
        await using var command = connection.CreateCommand(); command.CommandText = commandText;
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static LifecycleEvent Event(Guid sessionId, Guid parentId) => new(Guid.NewGuid(), sessionId, parentId, AgentStatus.Running, AgentStatus.Completed, DateTimeOffset.UtcNow, true);
}
