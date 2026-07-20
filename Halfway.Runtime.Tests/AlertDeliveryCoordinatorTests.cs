using Halfway.Core;
using Halfway.Runtime;
using Halfway.Terminal;
using Halfway.Terminal.Readiness;
using Xunit;

namespace Halfway.Runtime.Tests;

public sealed class AlertDeliveryCoordinatorTests
{
    [Fact]
    public async Task ReservationGateRevalidatesPartialInputAndReleasesWithoutWriting()
    {
        var terminal = new FakeTerminalSession();
        var sessions = Coordinator(terminal, out var parentId);
        var readiness = new ShellReadinessAdapter();
        readiness.ObserveOutput("PS> ");
        await sessions.StartAsync(Descriptor(parentId), Options(), readiness);
        var alerts = new AlertInputCoordinator(readiness);
        var eventId = Guid.NewGuid();
        alerts.RequestAlert(eventId, parentId, "[Halfway Alert!] Runtime completed. Continue orchestration.");
        var reserveEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReserve = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = 0;
        var delivery = new AlertDeliveryCoordinator(sessions);

        var attempt = delivery.TryDeliverAsync(
            parentId,
            "planner",
            [eventId],
            alerts,
            async (_, _) =>
            {
                reserveEntered.TrySetResult();
                await releaseReserve.Task;
                return true;
            },
            (_, _) => Task.FromException<bool>(new InvalidOperationException("write must not be marked")),
            (_, _) => Task.FromException<bool>(new InvalidOperationException("write must not be committed")),
            (_, _) => { released++; return Task.FromResult(true); });

        await reserveEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        alerts.SetUserInput("partially typed");
        releaseReserve.TrySetResult();

        Assert.Equal(AlertDeliveryOutcome.RejectedBeforeWrite, await attempt);
        Assert.Empty(terminal.Inputs);
        Assert.Equal(1, released);
        Assert.True(alerts.HasQueuedAlert);
    }

    [Fact]
    public async Task SuccessfulWriteWithFailedDeliveredCommitIsNeverAutomaticallyWrittenAgain()
    {
        var terminal = new FakeTerminalSession();
        var sessions = Coordinator(terminal, out var parentId);
        var readiness = new ShellReadinessAdapter();
        readiness.ObserveOutput("PS> ");
        await sessions.StartAsync(Descriptor(parentId), Options(), readiness);
        var alerts = new AlertInputCoordinator(readiness);
        var eventId = Guid.NewGuid();
        const string message = "[Halfway Alert!] Runtime completed. Continue orchestration.";
        alerts.RequestAlert(eventId, parentId, message);
        var releases = 0;
        var delivery = new AlertDeliveryCoordinator(sessions);

        var outcome = await delivery.TryDeliverAsync(
            parentId,
            "planner",
            [eventId],
            alerts,
            (_, _) => Task.FromResult(true),
            (_, _) => Task.FromResult(true),
            (_, _) => Task.FromResult(false),
            (_, _) => { releases++; return Task.FromResult(true); });

        Assert.Equal(AlertDeliveryOutcome.CommitPending, outcome);
        Assert.Equal([message + "\r"], terminal.Inputs);
        Assert.Equal(0, releases);
        Assert.False(alerts.HasQueuedAlert);
        Assert.Equal(
            AlertDeliveryOutcome.NotReady,
            await delivery.TryDeliverAsync(
                parentId,
                "planner",
                [eventId],
                alerts,
                (_, _) => Task.FromResult(true),
                (_, _) => Task.FromResult(true),
                (_, _) => Task.FromResult(true),
                (_, _) => Task.FromResult(true)));
        Assert.Single(terminal.Inputs);
    }

    [Fact]
    public async Task UserSubmissionWhileReservationIsBlockedWinsSerializationAndRejectsAlertWrite()
    {
        var terminal = new FakeTerminalSession();
        var sessions = Coordinator(terminal, out var parentId);
        var readiness = new ShellReadinessAdapter();
        readiness.ObserveOutput("PS> ");
        await sessions.StartAsync(Descriptor(parentId), Options(), readiness);
        var alerts = new AlertInputCoordinator(readiness);
        var eventId = Guid.NewGuid();
        const string alertMessage = "[Halfway Alert!] Runtime completed. Continue orchestration.";
        alerts.RequestAlert(eventId, parentId, alertMessage);
        var reserveEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReserve = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = 0;
        var delivery = new AlertDeliveryCoordinator(sessions);

        var attempt = delivery.TryDeliverAsync(
            parentId,
            "planner",
            [eventId],
            alerts,
            async (_, _) =>
            {
                reserveEntered.TrySetResult();
                await releaseReserve.Task;
                return true;
            },
            (_, _) => Task.FromResult(true),
            (_, _) => Task.FromResult(true),
            (_, _) => { released++; return Task.FromResult(true); });

        await reserveEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await sessions.SubmitUserInputAsync("planner", "user input\r");
        releaseReserve.TrySetResult();

        Assert.Equal(AlertDeliveryOutcome.RejectedBeforeWrite, await attempt);
        Assert.Equal(["user input\r"], terminal.Inputs);
        Assert.DoesNotContain(alertMessage + "\r", terminal.Inputs);
        Assert.Equal(1, released);
    }

    [Fact]
    public async Task AlertForDifferentParentIsRejectedBeforeDurableReservation()
    {
        var terminal = new FakeTerminalSession();
        var sessions = Coordinator(terminal, out var parentId);
        var readiness = new ShellReadinessAdapter();
        readiness.ObserveOutput("PS> ");
        await sessions.StartAsync(Descriptor(parentId), Options(), readiness);
        var alerts = new AlertInputCoordinator(readiness);
        var eventId = Guid.NewGuid();
        alerts.RequestAlert(eventId, Guid.NewGuid(), "[Halfway Alert!] Runtime completed. Continue orchestration.");
        var reserveCalls = 0;

        var outcome = await new AlertDeliveryCoordinator(sessions).TryDeliverAsync(
            parentId,
            "planner",
            [eventId],
            alerts,
            (_, _) => { reserveCalls++; return Task.FromResult(true); },
            (_, _) => Task.FromResult(true),
            (_, _) => Task.FromResult(true),
            (_, _) => Task.FromResult(true));

        Assert.Equal(AlertDeliveryOutcome.TargetRejected, outcome);
        Assert.Equal(0, reserveCalls);
        Assert.Empty(terminal.Inputs);
    }

    private static SessionCoordinator Coordinator(FakeTerminalSession terminal, out Guid parentId)
    {
        parentId = Guid.NewGuid();
        var registry = new SessionRegistry();
        registry.Register(new AgentSession(parentId, "Planner", AgentKind.Primary, null));
        return new SessionCoordinator(new FakeFactory(terminal), registry);
    }

    private static ManagedSession Descriptor(Guid id) => new("planner", id, "Planner", AgentKind.Primary, null);
    private static TerminalLaunchOptions Options() => TerminalLaunchOptions.PowerShell(Environment.CurrentDirectory);

    private sealed class FakeFactory(FakeTerminalSession terminal) : ITerminalSessionFactory
    {
        public Task<ITerminalSession> StartAsync(TerminalLaunchOptions options, CancellationToken cancellationToken = default) =>
            Task.FromResult<ITerminalSession>(terminal);
    }

    private sealed class FakeTerminalSession : ITerminalSession
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> Inputs { get; } = [];
        public event EventHandler<string>? OutputReceived { add { } remove { } }
        public event EventHandler<TerminalExit>? Exited { add { } remove { } }
        public int ProcessId => 1;
        public Task Completion => _completion.Task;
        public ValueTask WriteAsync(string input, CancellationToken cancellationToken = default)
        {
            Inputs.Add(input);
            return ValueTask.CompletedTask;
        }
        public void Resize(TerminalSize size) { }
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() { _completion.TrySetResult(); return ValueTask.CompletedTask; }
    }
}
