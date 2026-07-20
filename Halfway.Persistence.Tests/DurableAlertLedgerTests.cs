using Halfway.Core;

namespace Halfway.Persistence.Tests;

public sealed class DurableAlertLedgerTests : IAsyncDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Halfway.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task EligibleCompletionIsDurableOnceAndConcurrentReservationHasOneWinner()
    {
        await using var store = new SqliteWorkspaceStore(Path.Combine(_directory, "ledger.db")); var catalog = new WorkspaceCatalog(store); await catalog.InitializeAsync(_directory, LaunchProfile.PowerShell);
        var ledger = new DurableAlertLedger(store); var registry = Registry(catalog); var child = catalog.SelectedSubAgent!;
        var transition = registry.Transition(child.Id, AgentStatus.Completed); var repeated = registry.Transition(child.Id, AgentStatus.Completed);
        var eventId = transition.Event!.Id;
        await ledger.RecordAsync(transition); await ledger.RecordAsync(transition); await ledger.RecordAsync(repeated);
        Assert.Single(await ledger.LoadPendingAsync(catalog.SelectedPrimary!.Id));
        var attempts = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => ledger.ReserveAsync(eventId)));
        Assert.Equal(1, attempts.Count(x => x));
        Assert.True(await ledger.CommitAsync(eventId)); Assert.False(await ledger.CommitAsync(eventId));
        Assert.Empty(await ledger.LoadPendingAsync(catalog.SelectedPrimary.Id));
    }

    [Fact]
    public async Task FailedDeliveryReleasesAndRestartKeepsPendingWithoutRequeueingDelivered()
    {
        var database = Path.Combine(_directory, "restart.db"); Guid parentId, pendingId, deliveredId;
        await using (var store = new SqliteWorkspaceStore(database))
        {
            var catalog = new WorkspaceCatalog(store); await catalog.InitializeAsync(_directory, LaunchProfile.PowerShell); var ledger = new DurableAlertLedger(store); parentId = catalog.SelectedPrimary!.Id;
            var first = Completion(catalog.SelectedSubAgent!); pendingId = first.Event!.Id; await ledger.RecordAsync(first); Assert.True(await ledger.ReserveAsync(pendingId)); Assert.True(await ledger.ReleaseAsync(pendingId));
            var second = Completion(catalog.SelectedSubAgent!); deliveredId = second.Event!.Id; await ledger.RecordAsync(second); Assert.True(await ledger.ReserveAsync(deliveredId)); Assert.True(await ledger.CommitAsync(deliveredId));
        }
        await using var reopened = new SqliteWorkspaceStore(database); await reopened.InitializeAsync(); var restored = new DurableAlertLedger(reopened); await restored.RecoverAsync();
        Assert.Equal(pendingId, Assert.Single(await restored.LoadPendingAsync(parentId)).EventId);
        Assert.Equal(AlertDeliveryState.Delivered, (await reopened.FindAlertDeliveryAsync(deliveredId))!.State);
    }

    [Fact]
    public async Task NonCompletionTransitionsAndMetadataRestoreCreateNoAlerts()
    {
        await using var store = new SqliteWorkspaceStore(Path.Combine(_directory, "facts.db")); var catalog = new WorkspaceCatalog(store); await catalog.InitializeAsync(_directory, LaunchProfile.PowerShell); var ledger = new DurableAlertLedger(store); var child = catalog.SelectedSubAgent!;
        foreach (var status in new[] { AgentStatus.Failed, AgentStatus.Disconnected, AgentStatus.Queued })
        {
            var item = new LifecycleEvent(Guid.NewGuid(), child.Id, child.ParentSessionId, AgentStatus.Running, status, DateTimeOffset.UtcNow, false);
            await ledger.RecordAsync(new LifecycleTransition(new AgentSession(child.Id, child.DisplayName, child.Kind, child.ParentSessionId, status), item));
        }
        Assert.Empty(await ledger.LoadPendingAsync(catalog.SelectedPrimary!.Id));
        var restored = new WorkspaceCatalog(store); await restored.InitializeAsync(_directory, LaunchProfile.PowerShell);
        Assert.Empty(await ledger.LoadPendingAsync(catalog.SelectedPrimary.Id));
    }

    private static LifecycleTransition Completion(SessionMetadata child)
    {
        var item = new LifecycleEvent(Guid.NewGuid(), child.Id, child.ParentSessionId, AgentStatus.Running, AgentStatus.Completed, DateTimeOffset.UtcNow, true);
        return new LifecycleTransition(new AgentSession(child.Id, child.DisplayName, child.Kind, child.ParentSessionId, AgentStatus.Completed), item, new CompletionAlert(item.Id, child.ParentSessionId!.Value, [child.DisplayName]));
    }

    private static SessionRegistry Registry(WorkspaceCatalog catalog)
    {
        var registry = new SessionRegistry(); var parent = catalog.SelectedPrimary!; var child = catalog.SelectedSubAgent!;
        registry.Register(new AgentSession(parent.Id, parent.DisplayName, parent.Kind, null, AgentStatus.Running));
        registry.Register(new AgentSession(child.Id, child.DisplayName, child.Kind, child.ParentSessionId, AgentStatus.Running));
        return registry;
    }

    public ValueTask DisposeAsync() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); return ValueTask.CompletedTask; }
}
