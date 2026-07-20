using Halfway.Core;
using Halfway.Persistence;
using Halfway.Runtime;
using Halfway.Terminal;
using Halfway.Terminal.Readiness;
using Xunit;

namespace Halfway.Reliability.Tests;

public sealed class ReliabilityLifecycleTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"halfway-reliability-{Guid.NewGuid():N}");
    private string Database => Path.Combine(_directory, "halfway.db");

    [Fact]
    public async Task ReadinessAndUserInputFollowCompleteRunningWaitingSequences()
    {
        var (coordinator, factory, _, runtime) = Coordinator();
        var states = new List<AgentStatus>();
        coordinator.StateChanged += (_, state) => states.Add(state.Status);
        await coordinator.StartAsync(Descriptor(runtime), Options(), new ShellReadinessAdapter());

        factory.Single.EmitOutput("PS> ");
        factory.Single.WriteException = new IOException("write failed");
        await Assert.ThrowsAsync<IOException>(() => coordinator.SubmitUserInputAsync(runtime.SessionKey, "failed\r"));
        Assert.Equal(AgentStatus.Waiting, coordinator.Get(runtime.SessionKey).Status);

        factory.Single.WriteException = null;
        await coordinator.SubmitUserInputAsync(runtime.SessionKey, "successful\r");
        Assert.Equal(AgentStatus.Running, coordinator.Get(runtime.SessionKey).Status);
        factory.Single.EmitOutput("PS> ");

        Assert.Equal(
            [AgentStatus.Queued, AgentStatus.Running, AgentStatus.Waiting, AgentStatus.Running, AgentStatus.Waiting],
            states);
        Assert.Equal(["successful\r"], factory.Single.Inputs);
        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task ChildCompletionsPersistOnceAndBatchDeterministically()
    {
        await using var store = await StoreAsync();
        var catalog = new WorkspaceCatalog(store); await catalog.InitializeAsync(_directory, LaunchProfile.PowerShell);
        var first = catalog.SelectedSubAgent!;
        var second = await catalog.CreateSubAgentAsync("Tests", LaunchProfile.PowerShell);
        var registry = Registry(catalog); var factory = new FakeFactory();
        await using var coordinator = new SessionCoordinator(factory, registry);
        var ledger = new DurableAlertLedger(store); var writes = new List<Task>();
        coordinator.LifecycleTransitioned += (_, transition) => writes.Add(ledger.RecordAsync(transition));

        await coordinator.StartAsync(Descriptor(first), Options());
        await coordinator.StartAsync(Descriptor(second), Options());
        var firstCompleted = StateSignal(coordinator, first.SessionKey, AgentStatus.Completed);
        var secondCompleted = StateSignal(coordinator, second.SessionKey, AgentStatus.Completed);
        factory.Sessions[0].EmitExit(0, false); factory.Sessions[0].EmitExit(0, false);
        factory.Sessions[1].EmitExit(0, false);
        await Task.WhenAll(firstCompleted, secondCompleted);
        await Task.WhenAll(writes);

        Assert.Single(registry.Events, item => item.SessionId == first.Id && item.NewStatus == AgentStatus.Completed);
        Assert.Single(registry.Events, item => item.SessionId == second.Id && item.NewStatus == AgentStatus.Completed);
        Assert.Equal(2, (await ledger.LoadPendingAsync(catalog.SelectedPrimary!.Id)).Count);
        var batch = await ledger.CreatePendingBatchAsync(catalog.SelectedPrimary!.Id);
        Assert.NotNull(batch);
        Assert.Equal(2, batch.EventIds.Count);
        Assert.Equal("[Halfway Alert!] Runtime and Tests completed. Continue orchestration.", batch.Message);
    }

    [Fact]
    public async Task AlertReservationFailureReleaseRetryAndCommitIsRestartSafe()
    {
        await using (var store = await StoreAsync())
        {
            var catalog = new WorkspaceCatalog(store); await catalog.InitializeAsync(_directory, LaunchProfile.PowerShell);
            var ledger = new DurableAlertLedger(store); var transition = CompletedTransition(catalog.SelectedSubAgent!);
            await ledger.RecordAsync(transition);
            var batch = await ledger.CreatePendingBatchAsync(catalog.SelectedPrimary!.Id);
            Assert.NotNull(batch); Assert.True(await ledger.ReserveAsync(batch.EventIds));

            var readiness = new ShellReadinessAdapter(); readiness.ObserveOutput("PS> ");
            var alerts = new AlertInputCoordinator(readiness); alerts.SetUserInput("partial"); alerts.RequestAlert(batch.ReservationId, batch.Message);
            Assert.Null(alerts.TakeReadyAlertReservation());
            alerts.SetUserInput(string.Empty);
            Assert.Equal(batch.ReservationId, alerts.TakeReadyAlertReservation()!.EventId);

            var registry = Registry(catalog); var factory = new FakeFactory();
            await using var coordinator = new SessionCoordinator(factory, registry);
            await coordinator.StartAsync(Descriptor(catalog.SelectedPrimary), Options(), readiness);
            factory.Single.WriteException = new IOException("alert write failed");
            await Assert.ThrowsAsync<IOException>(() => coordinator.WriteAsync(catalog.SelectedPrimary.SessionKey, batch.Message + "\r"));
            await ledger.ReleaseAsync(batch.EventIds); alerts.ReleaseAlertDelivery();
            Assert.Equal(batch.ReservationId, alerts.TakeReadyAlertReservation()!.EventId);
            Assert.True(await ledger.ReserveAsync(batch.EventIds));
            factory.Single.WriteException = null;
            await coordinator.WriteAsync(catalog.SelectedPrimary.SessionKey, batch.Message + "\r");
            Assert.True(await ledger.CommitAsync(batch.EventIds)); alerts.CommitAlertDelivery();
            Assert.Null(alerts.TakeReadyAlertReservation());
        }

        await using var reopened = await StoreAsync();
        var restored = new WorkspaceCatalog(reopened); await restored.InitializeAsync(_directory, LaunchProfile.PowerShell);
        var recovered = new DurableAlertLedger(reopened); Assert.Equal(0, await recovered.RecoverAsync());
        Assert.Null(await recovered.CreatePendingBatchAsync(restored.SelectedPrimary!.Id));
    }

    [Fact]
    public async Task ReservedAlertRecoversPendingAfterRestart()
    {
        Guid parentId; Guid eventId;
        await using (var store = await StoreAsync())
        {
            var catalog = new WorkspaceCatalog(store); await catalog.InitializeAsync(_directory, LaunchProfile.PowerShell);
            parentId = catalog.SelectedPrimary!.Id; var ledger = new DurableAlertLedger(store);
            var transition = CompletedTransition(catalog.SelectedSubAgent!); eventId = transition.Event!.Id;
            await ledger.RecordAsync(transition); Assert.True(await ledger.ReserveAsync(eventId));
        }

        await using var reopened = await StoreAsync(); var recovered = new DurableAlertLedger(reopened);
        Assert.Equal(1, await recovered.RecoverAsync());
        Assert.Equal(eventId, Assert.Single(await recovered.LoadPendingAsync(parentId)).EventId);
    }

    [Fact]
    public async Task StopFailureAndStaleOwnershipRemainProcessDrivenAndIdempotent()
    {
        var (coordinator, factory, registry, runtime) = Coordinator();
        var notifications = new FailureNotificationPolicy(); var notificationFacts = new List<FailureNotification>();
        coordinator.LifecycleTransitioned += (_, transition) =>
        {
            if (notifications.Evaluate(transition, false, null) is { } fact) notificationFacts.Add(fact);
        };

        await coordinator.StartAsync(Descriptor(runtime), Options());
        await coordinator.StopAsync(runtime.SessionKey);
        Assert.Equal(AgentStatus.Disconnected, registry.Get(runtime.Id).Status);
        Assert.DoesNotContain(registry.Events, item => item.AlertEligible);

        await coordinator.StartAsync(Descriptor(runtime), Options());
        var failed = StateSignal(coordinator, runtime.SessionKey, AgentStatus.Failed);
        factory.Sessions[1].CompleteWithoutExit(new IOException("lost terminal"));
        await failed;
        factory.Sessions[1].CompleteWithoutExit(new IOException("duplicate"));

        Assert.Single(registry.Events, item => item.NewStatus == AgentStatus.Failed);
        Assert.Single(notificationFacts);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => coordinator.WriteAsync(runtime.SessionKey, "stale\r"));
        Assert.Throws<KeyNotFoundException>(() => coordinator.Resize(runtime.SessionKey, new TerminalSize(80, 24)));
    }

    [Fact]
    public async Task NonzeroExitFailsOnceAndCreatesAtMostOneNotificationFact()
    {
        var (coordinator, factory, registry, runtime) = Coordinator();
        var policy = new FailureNotificationPolicy(); var notifications = new List<FailureNotification>();
        coordinator.LifecycleTransitioned += (_, transition) =>
        {
            if (policy.Evaluate(transition, false, null) is { } fact) notifications.Add(fact);
        };
        await coordinator.StartAsync(Descriptor(runtime), Options());
        var failed = StateSignal(coordinator, runtime.SessionKey, AgentStatus.Failed);

        factory.Single.EmitExit(7, false); factory.Single.EmitExit(7, false);
        await failed;

        Assert.Equal(AgentStatus.Failed, registry.Get(runtime.Id).Status);
        Assert.Single(registry.Events, item => item.NewStatus == AgentStatus.Failed);
        Assert.Single(notifications);
        Assert.DoesNotContain(registry.Events, item => item.AlertEligible);
    }

    [Fact]
    public async Task QueuedInputNeverCrossesSessionOrReplacementOwnership()
    {
        var (coordinator, factory, _, runtime) = Coordinator();
        await coordinator.StartAsync(Descriptor(runtime), Options());
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Single.WriteGate = gate;
        var first = coordinator.SubmitUserInputAsync(runtime.SessionKey, "old-first\r");
        var second = coordinator.SubmitUserInputAsync(runtime.SessionKey, "old-second\r");
        var disconnected = StateSignal(coordinator, runtime.SessionKey, AgentStatus.Disconnected);
        factory.Single.EmitExit(0, true); await disconnected;
        await Assert.ThrowsAsync<SessionInputUnavailableException>(() => first);
        await Assert.ThrowsAsync<SessionInputUnavailableException>(() => second);

        await coordinator.StartAsync(Descriptor(runtime), Options());
        Assert.Empty(factory.Sessions[1].Inputs);
        await coordinator.SubmitUserInputAsync(runtime.SessionKey, "new\r");
        Assert.Equal(["new\r"], factory.Sessions[1].Inputs);
        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task RestoreAndUncleanRunCreateNoLifecycleDeliveryNotificationOrUnreadFacts()
    {
        Guid workspaceId; Guid runtimeId;
        await using (var store = await StoreAsync())
        {
            var catalog = new WorkspaceCatalog(store); await catalog.InitializeAsync(_directory, LaunchProfile.PowerShell);
            workspaceId = catalog.Workspace.Id; runtimeId = catalog.SelectedSubAgent!.Id;
            await catalog.UpdateStatusAsync(runtimeId, AgentStatus.Running);
            await store.StartApplicationRunAsync(new ApplicationRun(Guid.NewGuid(), DateTimeOffset.UnixEpoch, null, "test"));
        }

        await using var reopened = await StoreAsync();
        var start = await reopened.StartApplicationRunAsync(new ApplicationRun(Guid.NewGuid(), DateTimeOffset.UnixEpoch.AddMinutes(1), null, "test"));
        var restored = new WorkspaceCatalog(reopened); await restored.InitializeAsync(_directory, LaunchProfile.PowerShell);
        var registry = Registry(restored); var notifications = new FailureNotificationPolicy(); var attention = new SessionAttentionTracker();

        Assert.True(start.PreviousRunWasUnclean);
        Assert.Equal(workspaceId, restored.Workspace.Id);
        Assert.Equal(AgentStatus.Disconnected, restored.Sessions.Single(item => item.Id == runtimeId).LastStatus);
        Assert.Empty(registry.Events);
        Assert.Null(notifications.Evaluate(new LifecycleTransition(registry.Get(runtimeId)), false, null));
        Assert.False(attention.IsUnread(runtimeId));
        Assert.Empty(await reopened.LoadPendingAlertsAsync(restored.SelectedPrimary!.Id));
    }

    [Fact]
    public async Task DiagnosticsExportLifecycleFactsWithoutPrivateContentOrSideEffects()
    {
        await using var store = await StoreAsync();
        var catalog = new WorkspaceCatalog(store); await catalog.InitializeAsync(_directory, LaunchProfile.PowerShell);
        var registry = Registry(catalog); var ledger = new DurableAlertLedger(store); var diagnostics = new DiagnosticBuffer();
        var beforeEvents = registry.Events.Count; var beforePending = await ledger.LoadPendingAsync(catalog.SelectedPrimary!.Id);
        diagnostics.Record("lifecycle", "transitioned", DateTimeOffset.UnixEpoch, new Dictionary<string, string>
        {
            ["sessionId"] = catalog.SelectedSubAgent!.Id.ToString(), ["newState"] = AgentStatus.Completed.ToString(),
            ["terminalOutput"] = "transcript token=private", ["prompt"] = "secret prompt",
            ["partialInput"] = "partial", ["submittedInput"] = "submitted", ["environment"] = "TOKEN=private"
        });
        var path = Path.Combine(_directory, "diagnostics.json");
        await new DiagnosticExporter().ExportAsync(path, diagnostics.Snapshot());
        var json = await File.ReadAllTextAsync(path);

        Assert.Contains("sessionId", json); Assert.Contains("Completed", json);
        Assert.DoesNotContain("transcript", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("prompt", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("partial", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("submitted", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("environment", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TOKEN", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(beforeEvents, registry.Events.Count);
        Assert.Equal(beforePending, await ledger.LoadPendingAsync(catalog.SelectedPrimary.Id));
    }

    public Task InitializeAsync() { Directory.CreateDirectory(_directory); return Task.CompletedTask; }
    public Task DisposeAsync() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); return Task.CompletedTask; }

    private async Task<SqliteWorkspaceStore> StoreAsync()
    {
        var store = new SqliteWorkspaceStore(Database); await store.InitializeAsync(); return store;
    }

    private static (SessionCoordinator Coordinator, FakeFactory Factory, SessionRegistry Registry, SessionMetadata Runtime) Coordinator()
    {
        var registry = new SessionRegistry(); var parentId = Guid.NewGuid(); var runtimeId = Guid.NewGuid();
        registry.Register(new AgentSession(parentId, "Planner", AgentKind.Primary, null));
        var runtime = new SessionMetadata(runtimeId, Guid.NewGuid(), "runtime", "Runtime", AgentKind.SubAgent, parentId, LaunchProfile.PowerShell, 0, AgentStatus.Disconnected, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        var factory = new FakeFactory();
        return (new SessionCoordinator(factory, registry), factory, registry, runtime);
    }

    private static SessionRegistry Registry(WorkspaceCatalog catalog)
    {
        var registry = new SessionRegistry();
        foreach (var item in catalog.Sessions.OrderBy(item => item.Kind).ThenBy(item => item.DisplayOrder))
            registry.Register(new AgentSession(item.Id, item.DisplayName, item.Kind, catalog.GetParentSessionId(item.Id), item.LastStatus));
        return registry;
    }

    private static ManagedSession Descriptor(SessionMetadata item) => new(item.SessionKey, item.Id, item.DisplayName, item.Kind, item.ParentSessionId);
    private static TerminalLaunchOptions Options() => TerminalLaunchOptions.PowerShell(Environment.CurrentDirectory);
    private static LifecycleTransition CompletedTransition(SessionMetadata child)
    {
        var item = new LifecycleEvent(Guid.NewGuid(), child.Id, child.ParentSessionId, AgentStatus.Running, AgentStatus.Completed, DateTimeOffset.UtcNow, true);
        return new LifecycleTransition(new AgentSession(child.Id, child.DisplayName, child.Kind, child.ParentSessionId, AgentStatus.Completed), item, new CompletionAlert(item.Id, child.ParentSessionId!.Value, [child.DisplayName]));
    }

    private static Task StateSignal(SessionCoordinator coordinator, string key, AgentStatus status)
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.StateChanged += (_, item) => { if (item.Key == key && item.Status == status) signal.TrySetResult(); };
        return signal.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class FakeFactory : ITerminalSessionFactory
    {
        public List<FakeTerminalSession> Sessions { get; } = [];
        public FakeTerminalSession Single => Assert.Single(Sessions);
        public Task<ITerminalSession> StartAsync(TerminalLaunchOptions options, CancellationToken cancellationToken = default)
        {
            var session = new FakeTerminalSession(Sessions.Count + 1); Sessions.Add(session); return Task.FromResult<ITerminalSession>(session);
        }
    }

    private sealed class FakeTerminalSession(int processId) : ITerminalSession
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public event EventHandler<string>? OutputReceived;
        public event EventHandler<TerminalExit>? Exited;
        public int ProcessId { get; } = processId;
        public Task Completion => _completion.Task;
        public List<string> Inputs { get; } = [];
        public Exception? WriteException { get; set; }
        public TaskCompletionSource? WriteGate { get; set; }

        public async ValueTask WriteAsync(string input, CancellationToken cancellationToken = default)
        {
            if (WriteGate is not null) await WriteGate.Task.WaitAsync(cancellationToken);
            if (WriteException is not null) throw WriteException;
            Inputs.Add(input);
        }
        public void Resize(TerminalSize size) { }
        public Task StopAsync(CancellationToken cancellationToken = default) { _completion.TrySetCanceled(cancellationToken); return Task.CompletedTask; }
        public ValueTask DisposeAsync() { _completion.TrySetResult(); return ValueTask.CompletedTask; }
        public void EmitOutput(string output) => OutputReceived?.Invoke(this, output);
        public void EmitExit(int code, bool cancelled) { Exited?.Invoke(this, new TerminalExit(code, cancelled)); _completion.TrySetResult(); }
        public void CompleteWithoutExit(Exception? exception = null)
        {
            if (exception is null) _completion.TrySetResult(); else _completion.TrySetException(exception);
        }
    }
}
