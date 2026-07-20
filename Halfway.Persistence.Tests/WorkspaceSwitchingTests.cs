using Halfway.Core;

namespace Halfway.Persistence.Tests;

public sealed class WorkspaceSwitchingTests : IAsyncDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Halfway.Switch.Tests", Guid.NewGuid().ToString("N"));
    private readonly List<SqliteWorkspaceStore> _stores = [];
    private string Database => Path.Combine(_root, "data", "test.db");

    [Fact]
    public async Task EnumerationIsReadOnlyAndUsesDeterministicRecencyOrder()
    {
        Directory.CreateDirectory(_root);
        await using var store = new SqliteWorkspaceStore(Database); await store.InitializeAsync();
        var stamp = DateTimeOffset.UnixEpoch;
        var ids = new[]
        {
            Guid.Parse("00000000-0000-0000-0000-000000000002"),
            Guid.Parse("00000000-0000-0000-0000-000000000001"),
            Guid.Parse("00000000-0000-0000-0000-000000000003"),
        };
        foreach (var id in ids)
            await store.InsertWorkspaceAsync(new WorkspaceMetadata(id, id.ToString(), Path.Combine(_root, id.ToString()), null, null, stamp, stamp));

        var before = await store.LoadWorkspacesAsync();
        var after = await store.LoadWorkspacesAsync();

        Assert.Equal(new[] { ids[1], ids[0], ids[2] }, before.Select(item => item.Id));
        Assert.Equal(before, after);
        Assert.All(after, item => Assert.Equal(stamp, item.UpdatedAtUtc));
        Assert.Empty(await store.LoadApplicationRunsAsync());
    }

    [Fact]
    public async Task ActiveSelectionIsNoOpAndCancellationPreservesRecencyAndRuntimeFacts()
    {
        var fixture = await CreateTwoAsync();
        var adapter = new FakeAdapter(fixture.A.Workspace.Id) { Owns = true, Partial = ["unsent"], Confirmation = false };
        var coordinator = new WorkspaceSwitchCoordinator(fixture.Store, adapter);
        var before = await fixture.Store.LoadWorkspacesAsync();

        Assert.Equal(WorkspaceSwitchOutcome.ActiveWorkspace, await coordinator.SwitchAsync(fixture.A.Workspace.Id));
        Assert.Equal(WorkspaceSwitchOutcome.Cancelled, await coordinator.SwitchAsync(fixture.B.Workspace.Id));

        Assert.Equal(["confirm"], adapter.Events);
        Assert.Equal(before, await fixture.Store.LoadWorkspacesAsync());
        Assert.Equal(fixture.A.Workspace.Id, adapter.ActiveWorkspaceId);
    }

    [Fact]
    public async Task ConfirmedSwitchStopsThenFlushesActivatesMarksRecentAndStartsSelections()
    {
        var fixture = await CreateTwoAsync();
        await fixture.B.UpdateStatusAsync(fixture.B.SelectedPrimary!.Id, AgentStatus.Running);
        var adapter = new FakeAdapter(fixture.A.Workspace.Id) { Owns = true };
        var coordinator = new WorkspaceSwitchCoordinator(fixture.Store, adapter);

        var outcome = await coordinator.SwitchAsync(fixture.B.Workspace.Id);

        Assert.Equal(WorkspaceSwitchOutcome.Activated, outcome);
        Assert.Equal(["confirm", "stop", "flush", "invalidate", "activate", "start"], adapter.Events);
        Assert.Equal(fixture.B.Workspace.Id, adapter.ActiveWorkspaceId);
        Assert.Equal(AgentStatus.Disconnected, adapter.ActivatedCatalog!.SelectedPrimary!.LastStatus);
        Assert.Equal(new[] { fixture.B.SelectedPrimary.Id, fixture.B.SelectedSubAgent!.Id }, adapter.StartedIds);
        Assert.Equal(fixture.B.Workspace.Id, (await fixture.Store.LoadWorkspacesAsync())[0].Id);
    }

    [Fact]
    public async Task InactiveSwitchNeedsNoConfirmationAndStopsNoTerminal()
    {
        var fixture = await CreateTwoAsync();
        var adapter = new FakeAdapter(fixture.A.Workspace.Id);
        var coordinator = new WorkspaceSwitchCoordinator(fixture.Store, adapter);

        Assert.Equal(WorkspaceSwitchOutcome.Activated, await coordinator.SwitchAsync(fixture.B.Workspace.Id));

        Assert.Equal(["flush", "invalidate", "activate", "start"], adapter.Events);
    }

    [Fact]
    public async Task PartialInputWithoutOwnershipStillRequiresConfirmation()
    {
        var fixture = await CreateTwoAsync();
        var adapter = new FakeAdapter(fixture.A.Workspace.Id) { Partial = ["unsent"], Confirmation = false };
        var coordinator = new WorkspaceSwitchCoordinator(fixture.Store, adapter);

        Assert.Equal(WorkspaceSwitchOutcome.Cancelled, await coordinator.SwitchAsync(fixture.B.Workspace.Id));

        Assert.Equal(["confirm"], adapter.Events);
    }

    [Fact]
    public async Task MissingAndPrevalidationFailuresLeaveCurrentWorkspaceUntouched()
    {
        var fixture = await CreateTwoAsync();
        Directory.Delete(fixture.B.Workspace.WorkingDirectory, true);
        var adapter = new FakeAdapter(fixture.A.Workspace.Id);
        var coordinator = new WorkspaceSwitchCoordinator(fixture.Store, adapter);

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => coordinator.SwitchAsync(fixture.B.Workspace.Id));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => coordinator.SwitchAsync(Guid.NewGuid()));

        Assert.Empty(adapter.Events);
        Assert.Equal(fixture.A.Workspace.Id, adapter.ActiveWorkspaceId);
        Assert.Equal(2, (await fixture.Store.LoadWorkspacesAsync()).Count);
    }

    [Fact]
    public async Task StopFailureStillFlushesAndStartsNoTargetProcess()
    {
        var fixture = await CreateTwoAsync();
        var adapter = new FakeAdapter(fixture.A.Workspace.Id) { Owns = true, StopFailure = new IOException("stop") };
        var coordinator = new WorkspaceSwitchCoordinator(fixture.Store, adapter);

        await Assert.ThrowsAsync<AggregateException>(() => coordinator.SwitchAsync(fixture.B.Workspace.Id));

        Assert.Equal(["confirm", "stop", "flush"], adapter.Events);
        Assert.Equal(fixture.A.Workspace.Id, adapter.ActiveWorkspaceId);
    }

    [Fact]
    public async Task PersistenceFailureAfterStopAbortsActivationAndStartsNoTargetProcess()
    {
        var fixture = await CreateTwoAsync();
        var adapter = new FakeAdapter(fixture.A.Workspace.Id) { Owns = true, FlushFailure = new IOException("persist") };
        var coordinator = new WorkspaceSwitchCoordinator(fixture.Store, adapter);

        await Assert.ThrowsAsync<AggregateException>(() => coordinator.SwitchAsync(fixture.B.Workspace.Id));

        Assert.Equal(["confirm", "stop", "flush"], adapter.Events);
        Assert.Equal(fixture.A.Workspace.Id, adapter.ActiveWorkspaceId);
        Assert.DoesNotContain("start", adapter.Events);
    }

    [Fact]
    public async Task ActivationFailureAfterInvalidationFailsClosedAndStartsNoTargetProcess()
    {
        var fixture = await CreateTwoAsync();
        var adapter = new FakeAdapter(fixture.A.Workspace.Id) { ActivationFailure = new InvalidOperationException("ui") };
        var coordinator = new WorkspaceSwitchCoordinator(fixture.Store, adapter);

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.SwitchAsync(fixture.B.Workspace.Id));

        Assert.Equal(["flush", "invalidate", "activate"], adapter.Events);
        Assert.DoesNotContain("start", adapter.Events);
    }

    [Fact]
    public async Task ConcurrentSameTargetRequestsStopAndStartOnlyOnce()
    {
        var fixture = await CreateTwoAsync();
        var stopEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var adapter = new FakeAdapter(fixture.A.Workspace.Id) { Owns = true, StopEntered = stopEntered, ReleaseStop = releaseStop };
        var coordinator = new WorkspaceSwitchCoordinator(fixture.Store, adapter);

        var first = coordinator.SwitchAsync(fixture.B.Workspace.Id);
        await stopEntered.Task;
        var second = coordinator.SwitchAsync(fixture.B.Workspace.Id);
        releaseStop.SetResult();

        Assert.Equal(WorkspaceSwitchOutcome.Activated, await first);
        Assert.Equal(WorkspaceSwitchOutcome.ActiveWorkspace, await second);
        Assert.Equal(1, adapter.Events.Count(item => item == "stop"));
        Assert.Equal(1, adapter.Events.Count(item => item == "start"));
    }

    private async Task<(SqliteWorkspaceStore Store, WorkspaceCatalog A, WorkspaceCatalog B)> CreateTwoAsync()
    {
        var aDirectory = Path.Combine(_root, "A");
        var bDirectory = Path.Combine(_root, "B");
        Directory.CreateDirectory(aDirectory);
        Directory.CreateDirectory(bDirectory);
        var store = new SqliteWorkspaceStore(Database);
        _stores.Add(store);
        var a = new WorkspaceCatalog(store); await a.InitializeAsync(aDirectory, LaunchProfile.PowerShell);
        var b = new WorkspaceCatalog(store); await b.InitializeAsync(bDirectory, LaunchProfile.Codex);
        return (store, a, b);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var store in _stores) await store.DisposeAsync();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed class FakeAdapter(Guid activeWorkspaceId) : IWorkspaceSwitchAdapter
    {
        public Guid ActiveWorkspaceId { get; private set; } = activeWorkspaceId;
        public bool Owns { get; set; }
        public string[] Partial { get; set; } = [];
        public bool Confirmation { get; set; } = true;
        public Exception? StopFailure { get; set; }
        public Exception? FlushFailure { get; set; }
        public Exception? ActivationFailure { get; set; }
        public TaskCompletionSource? StopEntered { get; set; }
        public TaskCompletionSource? ReleaseStop { get; set; }
        public List<string> Events { get; } = [];
        public WorkspaceCatalog? ActivatedCatalog { get; private set; }
        public Guid[] StartedIds { get; private set; } = [];
        public bool OwnsAnySession => Owns;
        public IReadOnlyCollection<string> PartialInputs => Partial;
        public Task<bool> ConfirmAsync(CancellationToken cancellationToken) { Events.Add("confirm"); return Task.FromResult(Confirmation); }
        public async Task StopOwnedSessionsAsync(CancellationToken cancellationToken)
        {
            Events.Add("stop"); StopEntered?.SetResult();
            if (ReleaseStop is not null) await ReleaseStop.Task;
            if (StopFailure is not null) throw StopFailure;
            Owns = false;
        }
        public Task FlushPersistenceAsync() { Events.Add("flush"); return FlushFailure is null ? Task.CompletedTask : Task.FromException(FlushFailure); }
        public void InvalidateActivation() => Events.Add("invalidate");
        public Task ActivatePresentationAsync(WorkspaceCatalog target, CancellationToken cancellationToken)
        {
            Events.Add("activate");
            if (ActivationFailure is not null) throw ActivationFailure;
            ActivatedCatalog = target; ActiveWorkspaceId = target.Workspace.Id; return Task.CompletedTask;
        }
        public Task StartSelectedSessionsAsync(WorkspaceCatalog target, CancellationToken cancellationToken)
        {
            Events.Add("start"); StartedIds = new[] { target.SelectedPrimary!.Id, target.SelectedSubAgent!.Id }; return Task.CompletedTask;
        }
    }
}
