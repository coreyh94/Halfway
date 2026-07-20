using Halfway.Core;
using Halfway.Runtime;
using Halfway.Terminal;
using Halfway.Terminal.Readiness;
using Xunit;

namespace Halfway.Runtime.Tests;

public sealed class SessionCoordinatorTests
{
    [Fact]
    public async Task Sessions_start_and_stop_independently()
    {
        var factory = new FakeFactory();
        var coordinator = CreateCoordinator(factory, out var plannerId, out var runtimeId);
        await coordinator.StartAsync(Descriptor("planner", plannerId, "Planner", null), Options());
        await coordinator.StartAsync(Descriptor("runtime", runtimeId, "Runtime", plannerId), Options());
        await coordinator.StopAsync("planner");
        Assert.True(factory.Sessions[1].IsRunning);
        await coordinator.WriteAsync("runtime", "echo runtime\r");
        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task Explicit_stop_disconnects_only_the_selected_session_without_alerting()
    {
        var factory = new FakeFactory();
        var registry = new SessionRegistry();
        var plannerId = Guid.NewGuid();
        registry.Register(new AgentSession(plannerId, "Planner", AgentKind.Primary, null));
        var runtimeId = Guid.NewGuid();
        var coordinator = new SessionCoordinator(factory, registry);
        var states = new List<SessionStateChanged>();
        var alerts = new List<CompletionAlert>();
        coordinator.StateChanged += (_, state) => states.Add(state);
        coordinator.CompletionAlertReady += (_, alert) => alerts.Add(alert);

        await coordinator.StartAsync(Descriptor("runtime", runtimeId, "Runtime", plannerId), Options());
        await coordinator.StopAsync("runtime");

        Assert.Equal(AgentStatus.Disconnected, registry.Get(runtimeId).Status);
        Assert.Equal(AgentStatus.Disconnected, Assert.Single(states, item => item.Status == AgentStatus.Disconnected).Status);
        Assert.Empty(alerts);
        Assert.True(factory.Sessions[0].IsDisposed);
        Assert.Throws<KeyNotFoundException>(() => coordinator.Get("runtime"));

        await coordinator.StartAsync(Descriptor("runtime", runtimeId, "Runtime", plannerId), Options());

        Assert.Equal(AgentStatus.Running, registry.Get(runtimeId).Status);
        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task Runtime_output_is_published_with_only_the_runtime_key()
    {
        var factory = new FakeFactory();
        var coordinator = CreateCoordinator(factory, out var plannerId, out var runtimeId);
        var output = new List<SessionOutput>();
        coordinator.OutputReceived += (_, item) => output.Add(item);
        await coordinator.StartAsync(Descriptor("planner", plannerId, "Planner", null), Options());
        await coordinator.StartAsync(Descriptor("runtime", runtimeId, "Runtime", plannerId), Options());
        factory.Sessions[1].EmitOutput("runtime output");
        var item = Assert.Single(output);
        Assert.Equal("runtime", item.Key);
        Assert.Equal("runtime output", item.Text);
    }

    [Fact]
    public async Task ReadinessAndSuccessfulInputDriveWaitingAndRunningStates()
    {
        var factory = new FakeFactory(); var registry = new SessionRegistry(); var plannerId = Guid.NewGuid(); var runtimeId = Guid.NewGuid();
        registry.Register(new AgentSession(plannerId, "Planner", AgentKind.Primary, null)); var coordinator = new SessionCoordinator(factory, registry);
        var states = new List<AgentStatus>(); coordinator.StateChanged += (_, state) => states.Add(state.Status);
        await coordinator.StartAsync(Descriptor("runtime", runtimeId, "Runtime", plannerId), Options(), new ShellReadinessAdapter());

        factory.Sessions[0].EmitOutput("PS> ");
        Assert.Equal(AgentStatus.Waiting, coordinator.Get("runtime").Status);
        await coordinator.WriteAsync("runtime", "echo work\r");
        Assert.Equal(AgentStatus.Running, coordinator.Get("runtime").Status);
        factory.Sessions[0].EmitOutput("PS> ");

        Assert.Equal([AgentStatus.Queued, AgentStatus.Running, AgentStatus.Waiting, AgentStatus.Running, AgentStatus.Waiting], states);
    }

    [Fact]
    public async Task FailedInputLeavesAReadySessionWaiting()
    {
        var factory = new FakeFactory(); var registry = new SessionRegistry(); var plannerId = Guid.NewGuid(); var runtimeId = Guid.NewGuid();
        registry.Register(new AgentSession(plannerId, "Planner", AgentKind.Primary, null)); var coordinator = new SessionCoordinator(factory, registry);
        await coordinator.StartAsync(Descriptor("runtime", runtimeId, "Runtime", plannerId), Options(), new ShellReadinessAdapter());
        factory.Sessions[0].EmitOutput("PS> "); factory.Sessions[0].WriteException = new IOException("write failed");

        await Assert.ThrowsAsync<IOException>(() => coordinator.WriteAsync("runtime", "work\r"));

        Assert.Equal(AgentStatus.Waiting, coordinator.Get("runtime").Status);
    }

    [Fact]
    public async Task SubmittedInputIsSessionIsolatedAndChangesWaitingOnlyAfterSuccessfulWrite()
    {
        var factory = new FakeFactory(); var coordinator = CreateCoordinator(factory, out var plannerId, out var runtimeId);
        await coordinator.StartAsync(Descriptor("planner", plannerId, "Planner", null), Options(), new ShellReadinessAdapter());
        await coordinator.StartAsync(Descriptor("runtime", runtimeId, "Runtime", plannerId), Options(), new ShellReadinessAdapter());
        factory.Sessions[0].EmitOutput("PS> "); factory.Sessions[1].EmitOutput("PS> ");

        await Task.WhenAll(
            coordinator.SubmitUserInputAsync("planner", "planner input\r"),
            coordinator.SubmitUserInputAsync("runtime", "runtime input\r"));

        Assert.Equal(["planner input\r"], factory.Sessions[0].Inputs);
        Assert.Equal(["runtime input\r"], factory.Sessions[1].Inputs);
        Assert.Equal(AgentStatus.Running, coordinator.Get("planner").Status);
        Assert.Equal(AgentStatus.Running, coordinator.Get("runtime").Status);

        factory.Sessions[1].EmitOutput("PS> "); factory.Sessions[1].WriteException = new IOException("write failed");
        await Assert.ThrowsAsync<IOException>(() => coordinator.SubmitUserInputAsync("runtime", "failed\r"));
        Assert.Equal(AgentStatus.Waiting, coordinator.Get("runtime").Status);
    }

    [Fact]
    public async Task SubmittedInputQueueIsBoundedAndPreservesPerSessionOrder()
    {
        var factory = new FakeFactory(); var coordinator = CreateCoordinator(factory, out var plannerId, out var runtimeId);
        await coordinator.StartAsync(Descriptor("runtime", runtimeId, "Runtime", plannerId), Options());
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Sessions[0].WriteGate = release;

        var accepted = Enumerable.Range(0, SubmittedInputQueue.DefaultCapacity)
            .Select(index => coordinator.SubmitUserInputAsync("runtime", $"input-{index}\r"))
            .ToArray();
        await Assert.ThrowsAsync<SubmittedInputQueueFullException>(() => coordinator.SubmitUserInputAsync("runtime", "rejected\r"));
        release.TrySetResult(); await Task.WhenAll(accepted);

        Assert.Equal(Enumerable.Range(0, SubmittedInputQueue.DefaultCapacity).Select(index => $"input-{index}\r"), factory.Sessions[0].Inputs);
    }

    [Fact]
    public async Task ExitResolvesQueuedInputAndNeverDeliversItToReplacementOwnership()
    {
        var factory = new FakeFactory(); var registry = new SessionRegistry(); var plannerId = Guid.NewGuid(); var runtimeId = Guid.NewGuid();
        registry.Register(new AgentSession(plannerId, "Planner", AgentKind.Primary, null));
        var coordinator = new SessionCoordinator(factory, registry); var descriptor = Descriptor("runtime", runtimeId, "Runtime", plannerId);
        await coordinator.StartAsync(descriptor, Options());
        factory.Sessions[0].WriteGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = coordinator.SubmitUserInputAsync("runtime", "old-first\r");
        var second = coordinator.SubmitUserInputAsync("runtime", "old-second\r");
        var disconnected = StateSignal(coordinator, AgentStatus.Disconnected);

        factory.Sessions[0].EmitExit(0, true); await disconnected;
        await Assert.ThrowsAsync<SessionInputUnavailableException>(() => first);
        await Assert.ThrowsAsync<SessionInputUnavailableException>(() => second);
        await coordinator.StartAsync(descriptor, Options());

        Assert.Empty(factory.Sessions[1].Inputs);
        await coordinator.SubmitUserInputAsync("runtime", "new\r");
        Assert.Equal(["new\r"], factory.Sessions[1].Inputs);
    }

    [Fact]
    public async Task Runtime_launch_adapter_selects_options_before_factory_start()
    {
        var factory = new FakeFactory();
        var coordinator = CreateCoordinator(factory, out var plannerId, out var runtimeId);
        var adapter = new RecordingLaunchAdapter();
        var context = new RuntimeLaunchContext("runtime-directory", new TerminalSize(120, 40));

        await coordinator.StartAsync(
            Descriptor("runtime", runtimeId, "Runtime", plannerId),
            adapter,
            context);

        Assert.Same(context, adapter.Context);
        Assert.Equal("adapter-command.exe", factory.Options[0].FileName);
        Assert.Equal(context.WorkingDirectory, factory.Options[0].WorkingDirectory);
    }

    [Fact]
    public async Task Cancelled_runtime_launch_does_not_claim_session_ownership()
    {
        var factory = new FakeFactory();
        var coordinator = CreateCoordinator(factory, out _, out var runtimeId);
        var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => coordinator.StartAsync(
            Descriptor("runtime", runtimeId, "Runtime", Guid.NewGuid()),
            new PowerShellRuntimeLaunchAdapter(),
            new RuntimeLaunchContext(Environment.CurrentDirectory, new TerminalSize(80, 24)),
            cancellation.Token));

        Assert.Empty(factory.Options);
        Assert.Throws<KeyNotFoundException>(() => coordinator.Get("runtime"));
    }

    [Fact]
    public async Task Failed_runtime_launch_releases_ownership_and_can_be_retried()
    {
        var factory = new FakeFactory { StartException = new InvalidOperationException("launch failed") };
        var registry = new SessionRegistry();
        var plannerId = Guid.NewGuid();
        registry.Register(new AgentSession(plannerId, "Planner", AgentKind.Primary, null));
        var coordinator = new SessionCoordinator(factory, registry);
        var runtimeId = Guid.NewGuid();
        var states = new List<SessionStateChanged>();
        coordinator.StateChanged += (_, state) => states.Add(state);
        var descriptor = Descriptor("runtime", runtimeId, "Runtime", plannerId);

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.StartAsync(descriptor, Options()));

        Assert.Equal(AgentStatus.Failed, registry.Get(runtimeId).Status);
        Assert.Equal([AgentStatus.Queued, AgentStatus.Failed], states.Select(item => item.Status));
        Assert.Throws<KeyNotFoundException>(() => coordinator.Get("runtime"));

        factory.StartException = null;
        await coordinator.StartAsync(descriptor, Options());

        Assert.Equal(AgentStatus.Running, coordinator.Get("runtime").Status);
        Assert.Equal([AgentStatus.Queued, AgentStatus.Failed, AgentStatus.Queued, AgentStatus.Running], states.Select(item => item.Status));
        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task Successful_runtime_exit_completes_and_alerts_once()
    {
        var factory = new FakeFactory();
        var registry = new SessionRegistry();
        var plannerId = Guid.NewGuid();
        registry.Register(new AgentSession(plannerId, "Planner", AgentKind.Primary, null));
        var coordinator = new SessionCoordinator(factory, registry);
        var alerts = new List<CompletionAlert>();
        coordinator.CompletionAlertReady += (_, alert) => alerts.Add(alert);
        var runtimeId = Guid.NewGuid();
        var completed = StateSignal(coordinator, AgentStatus.Completed);
        await coordinator.StartAsync(Descriptor("runtime", runtimeId, "Runtime", plannerId), Options());
        factory.Sessions[0].EmitExit(0, false);
        factory.Sessions[0].EmitExit(0, false);
        await completed;
        Assert.Equal(AgentStatus.Completed, registry.Get(runtimeId).Status);
        Assert.Equal("[Halfway Alert!] Runtime completed. Continue orchestration.", Assert.Single(alerts).Message);
        Assert.Single(registry.Events, item => item.NewStatus == AgentStatus.Completed);
    }

    [Fact]
    public async Task Completed_session_releases_ownership_and_can_restart()
    {
        var factory = new FakeFactory();
        var registry = new SessionRegistry();
        var plannerId = Guid.NewGuid(); registry.Register(new AgentSession(plannerId, "Planner", AgentKind.Primary, null));
        var coordinator = new SessionCoordinator(factory, registry); var runtimeId = Guid.NewGuid();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.StateChanged += (_, state) => { if (state.Status == AgentStatus.Completed) completed.TrySetResult(); };
        var descriptor = Descriptor("runtime", runtimeId, "Runtime", plannerId);
        await coordinator.StartAsync(descriptor, Options()); factory.Sessions[0].EmitExit(0, false); await completed.Task;

        Assert.Throws<KeyNotFoundException>(() => coordinator.Get("runtime"));
        await coordinator.StartAsync(descriptor, Options());
        Assert.Equal(AgentStatus.Running, coordinator.Get("runtime").Status);
        await coordinator.DisposeAsync();
    }

    [Theory]
    [InlineData(1, false, AgentStatus.Failed)]
    [InlineData(0, true, AgentStatus.Disconnected)]
    public async Task Runtime_exit_maps_to_failure_states(int exitCode, bool cancelled, AgentStatus expected)
    {
        var factory = new FakeFactory();
        var registry = new SessionRegistry();
        var plannerId = Guid.NewGuid();
        registry.Register(new AgentSession(plannerId, "Planner", AgentKind.Primary, null));
        var coordinator = new SessionCoordinator(factory, registry);
        var runtimeId = Guid.NewGuid();
        var exited = StateSignal(coordinator, expected);
        await coordinator.StartAsync(Descriptor("runtime", runtimeId, "Runtime", plannerId), Options());
        factory.Sessions[0].EmitExit(exitCode, cancelled);
        await exited;
        Assert.Equal(expected, registry.Get(runtimeId).Status);
        Assert.DoesNotContain(registry.Events, item => item.AlertEligible);
    }

    [Theory]
    [InlineData(false, AgentStatus.Disconnected)]
    [InlineData(true, AgentStatus.Failed)]
    public async Task TerminalCompletionWithoutExitReconcilesOwnedSessionExactlyOnce(bool faulted, AgentStatus expected)
    {
        var factory = new FakeFactory(); var registry = new SessionRegistry(); var plannerId = Guid.NewGuid(); var runtimeId = Guid.NewGuid();
        registry.Register(new AgentSession(plannerId, "Planner", AgentKind.Primary, null));
        var coordinator = new SessionCoordinator(factory, registry); var reconciled = StateSignal(coordinator, expected);
        await coordinator.StartAsync(Descriptor("runtime", runtimeId, "Runtime", plannerId), Options());

        factory.Sessions[0].CompleteWithoutExit(faulted ? new IOException("terminal disappeared") : null);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => coordinator.WriteAsync("runtime", "work\r"));
        Assert.Throws<KeyNotFoundException>(() => coordinator.Resize("runtime", new TerminalSize(100, 30)));
        await reconciled;

        Assert.Equal(expected, registry.Get(runtimeId).Status);
        Assert.Single(registry.Events, item => item.NewStatus == expected);
        Assert.Throws<KeyNotFoundException>(() => coordinator.Get("runtime"));
    }

    [Fact]
    public async Task StopAndExitRaceHasOneTerminalTransitionAndAtMostOneCompletionAlert()
    {
        var factory = new FakeFactory(); var registry = new SessionRegistry(); var plannerId = Guid.NewGuid(); var runtimeId = Guid.NewGuid();
        registry.Register(new AgentSession(plannerId, "Planner", AgentKind.Primary, null));
        var coordinator = new SessionCoordinator(factory, registry); var alerts = new List<CompletionAlert>();
        coordinator.CompletionAlertReady += (_, alert) => alerts.Add(alert);
        await coordinator.StartAsync(Descriptor("runtime", runtimeId, "Runtime", plannerId), Options());

        await Task.WhenAll(coordinator.StopAsync("runtime"), Task.Run(() => factory.Sessions[0].EmitExit(0, false)));

        Assert.Contains(registry.Get(runtimeId).Status, new[] { AgentStatus.Completed, AgentStatus.Disconnected });
        Assert.Single(registry.Events, item => item.NewStatus is AgentStatus.Completed or AgentStatus.Disconnected);
        Assert.True(alerts.Count <= 1);
        Assert.Throws<KeyNotFoundException>(() => coordinator.Get("runtime"));
    }

    private static SessionCoordinator CreateCoordinator(FakeFactory factory, out Guid plannerId, out Guid runtimeId)
    {
        var registry = new SessionRegistry();
        plannerId = Guid.NewGuid(); runtimeId = Guid.NewGuid();
        registry.Register(new AgentSession(plannerId, "Planner", AgentKind.Primary, null));
        return new SessionCoordinator(factory, registry);
    }
    private static ManagedSession Descriptor(string key, Guid id, string name, Guid? parent) => new(key, id, name, parent is null ? AgentKind.Primary : AgentKind.SubAgent, parent);
    private static TerminalLaunchOptions Options() => TerminalLaunchOptions.PowerShell(Environment.CurrentDirectory);
    private static Task StateSignal(SessionCoordinator coordinator, AgentStatus expected)
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.StateChanged += (_, state) => { if (state.Status == expected) signal.TrySetResult(); };
        return signal.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class FakeFactory : ITerminalSessionFactory
    {
        public List<FakeSession> Sessions { get; } = [];
        public List<TerminalLaunchOptions> Options { get; } = [];
        public Exception? StartException { get; set; }
        public Task<ITerminalSession> StartAsync(TerminalLaunchOptions options, CancellationToken cancellationToken = default)
        {
            if (StartException is not null) return Task.FromException<ITerminalSession>(StartException);
            Options.Add(options); var session = new FakeSession(Sessions.Count + 1); Sessions.Add(session); return Task.FromResult<ITerminalSession>(session);
        }
    }

    private sealed class RecordingLaunchAdapter : IRuntimeLaunchAdapter
    {
        public RuntimeLaunchContext? Context { get; private set; }

        public TerminalLaunchOptions CreateOptions(RuntimeLaunchContext context, CancellationToken cancellationToken = default)
        {
            Context = context;
            cancellationToken.ThrowIfCancellationRequested();
            return new TerminalLaunchOptions("adapter-command.exe", [], context.WorkingDirectory, context.InitialSize);
        }
    }

    private sealed class FakeSession(int processId) : ITerminalSession
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsRunning { get; private set; } = true;
        public bool IsDisposed { get; private set; }
        public Exception? WriteException { get; set; }
        public TaskCompletionSource? WriteGate { get; set; }
        public List<string> Inputs { get; } = [];
        public event EventHandler<string>? OutputReceived;
        public event EventHandler<TerminalExit>? Exited;
        public int ProcessId => processId;
        public Task Completion => _completion.Task;
        public async ValueTask WriteAsync(string input, CancellationToken cancellationToken = default)
        {
            if (WriteException is not null) throw WriteException;
            Inputs.Add(input);
            if (WriteGate is not null) await WriteGate.Task.WaitAsync(cancellationToken);
        }
        public void Resize(TerminalSize size) { }
        public Task StopAsync(CancellationToken cancellationToken = default) { EmitExit(0, true); return Task.CompletedTask; }
        public ValueTask DisposeAsync() { IsRunning = false; IsDisposed = true; _completion.TrySetResult(); return ValueTask.CompletedTask; }
        public void EmitOutput(string output) => OutputReceived?.Invoke(this, output);
        public void EmitExit(int code, bool cancelled) { IsRunning = false; Exited?.Invoke(this, new TerminalExit(code, cancelled)); _completion.TrySetResult(); }
        public void CompleteWithoutExit(Exception? failure = null) { IsRunning = false; if (failure is null) _completion.TrySetResult(); else _completion.TrySetException(failure); }
    }
}
