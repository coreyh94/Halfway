using Halfway.Core;
using Microsoft.Data.Sqlite;

namespace Halfway.Persistence.Tests;

public sealed class PersistenceTests : IAsyncDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Halfway.Tests", Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(_directory, "test.db");

    [Fact]
    public async Task EmptyDatabaseInitializesAndReopensAtVersionOne()
    {
        await using (var store = new SqliteWorkspaceStore(Database)) await store.InitializeAsync();
        await using var reopened = new SqliteWorkspaceStore(Database); await reopened.InitializeAsync();
        await using var connection = new SqliteConnection($"Data Source={Database}"); await connection.OpenAsync();
        await using var command = connection.CreateCommand(); command.CommandText = "PRAGMA user_version;";
        Assert.Equal(1L, await command.ExecuteScalarAsync());
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
    public async Task InvalidSelectionsFallBackInDisplayOrderAndPersist()
    {
        await using (var store = new SqliteWorkspaceStore(Database)) { var catalog = new WorkspaceCatalog(store); await catalog.InitializeAsync(_directory, LaunchProfile.PowerShell); await store.UpdateSelectionsAsync(catalog.Workspace.Id, Guid.NewGuid(), Guid.NewGuid()); }
        await using var reopened = new SqliteWorkspaceStore(Database); var restored = new WorkspaceCatalog(reopened); await restored.InitializeAsync(_directory, LaunchProfile.PowerShell);
        Assert.Equal("Planner", restored.SelectedPrimary!.DisplayName); Assert.Equal("Runtime", restored.SelectedSubAgent!.DisplayName);
    }

    public ValueTask DisposeAsync() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); return ValueTask.CompletedTask; }
}
