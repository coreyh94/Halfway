using Halfway.Core;

namespace Halfway.Persistence.Tests;

public sealed class WorkspaceCatalogTests
{
    [Fact]
    public async Task CreationValidatesNameParentIdentitySelectionAndOrdering()
    {
        await using var fixture = new CatalogFixture(); var catalog = await fixture.CreateAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => catalog.CreateSubAgentAsync("  ", LaunchProfile.PowerShell));
        await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.CreateSubAgentAsync("runtime", LaunchProfile.PowerShell));
        var first = await catalog.CreateSubAgentAsync("Tests", LaunchProfile.Codex);
        var second = await catalog.CreateSubAgentAsync("Docs", LaunchProfile.PowerShell);
        Assert.NotEqual(first.Id, second.Id); Assert.StartsWith("session-", first.SessionKey); Assert.Equal(catalog.SelectedPrimary!.Id, first.ParentSessionId);
        Assert.Equal(catalog.SelectedPrimary.Id, catalog.GetParentSessionId(first.Id));
        Assert.Contains(catalog.Relationships, x => x.ChildSessionId == first.Id && x.ParentSessionId == catalog.SelectedPrimary.Id);
        Assert.Equal(second.Id, catalog.SelectedSubAgent!.Id); Assert.True(second.DisplayOrder > first.DisplayOrder);
    }

    [Fact]
    public async Task SelectionAndStatusPersist()
    {
        await using var fixture = new CatalogFixture(); var catalog = await fixture.CreateAsync(); var created = await catalog.CreateSubAgentAsync("Tests", LaunchProfile.PowerShell);
        await catalog.UpdateStatusAsync(created.Id, AgentStatus.Completed); await catalog.SelectSubAgentAsync(created.Id);
        var restored = new WorkspaceCatalog(fixture.Store); await restored.InitializeAsync(fixture.Directory, LaunchProfile.PowerShell);
        Assert.Equal(created.Id, restored.SelectedSubAgent!.Id); Assert.Equal(AgentStatus.Completed, restored.SelectedSubAgent.LastStatus);
    }

    [Fact]
    public async Task PlannerLaunchProfileSurvivesSameProcessRestartAndFreshCatalogRestore()
    {
        await using var fixture = new CatalogFixture();
        var catalog = await fixture.CreateAsync();
        var plannerId = catalog.SelectedPrimary!.Id;

        var selected = await catalog.UpdateLaunchProfileAsync(plannerId, LaunchProfile.Codex);
        await catalog.UpdateStatusAsync(plannerId, AgentStatus.Disconnected);
        await catalog.UpdateStatusAsync(plannerId, AgentStatus.Running);

        Assert.Equal(plannerId, selected.Id);
        Assert.Equal(LaunchProfile.Codex, catalog.SelectedPrimary!.LaunchProfile);

        var restored = new WorkspaceCatalog(fixture.Store);
        await restored.InitializeAsync(fixture.Directory, LaunchProfile.PowerShell);
        Assert.Equal(plannerId, restored.SelectedPrimary!.Id);
        Assert.Equal(LaunchProfile.Codex, restored.SelectedPrimary.LaunchProfile);
        Assert.Equal(AgentStatus.Disconnected, restored.SelectedPrimary.LastStatus);
    }

    [Fact]
    public async Task NavigationSelectionsPersistWithoutCreatingLifecycleEventsOrAlerts()
    {
        await using var fixture = new CatalogFixture(); var catalog = await fixture.CreateAsync();
        var created = await catalog.CreateSubAgentAsync("Tests", LaunchProfile.PowerShell);
        var registry = new SessionRegistry();
        foreach (var session in catalog.Sessions.OrderBy(x => x.Kind).ThenBy(x => x.DisplayOrder))
            registry.Register(new AgentSession(session.Id, session.DisplayName, session.Kind, catalog.GetParentSessionId(session.Id), session.LastStatus));

        await catalog.SelectPrimaryAsync(catalog.SelectedPrimary!.Id);
        await catalog.SelectSubAgentAsync(created.Id);

        var restored = new WorkspaceCatalog(fixture.Store); await restored.InitializeAsync(fixture.Directory, LaunchProfile.PowerShell);
        Assert.Equal(created.Id, restored.SelectedSubAgent!.Id);
        Assert.Empty(registry.Events);
        Assert.Empty(await fixture.Store.LoadPendingAlertsAsync(catalog.SelectedPrimary.Id));
    }

    private sealed class CatalogFixture : IAsyncDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "Halfway.Tests", Guid.NewGuid().ToString("N"));
        public SqliteWorkspaceStore Store { get; }
        public CatalogFixture() => Store = new SqliteWorkspaceStore(Path.Combine(Directory, "test.db"));
        public async Task<WorkspaceCatalog> CreateAsync() { var catalog = new WorkspaceCatalog(Store); await catalog.InitializeAsync(Directory, LaunchProfile.PowerShell); return catalog; }
        public async ValueTask DisposeAsync() { await Store.DisposeAsync(); if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, true); }
    }
}
