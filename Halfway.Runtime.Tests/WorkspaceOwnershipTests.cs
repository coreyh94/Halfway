using Halfway.Core;
using Halfway.Runtime;
using Halfway.Terminal;
using Xunit;

namespace Halfway.Runtime.Tests;

public sealed class WorkspaceOwnershipTests
{
    [Fact]
    public async Task QueuedOwnershipIsVisibleAndExactStopRejectsLateTerminalStart()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new Factory { StartEntered = entered, ReleaseStart = release };
        var registry = new SessionRegistry();
        var id = Guid.NewGuid();
        registry.Register(new AgentSession(id, "Planner", AgentKind.Primary, null));
        var coordinator = new SessionCoordinator(factory, registry);
        var start = coordinator.StartAsync(new ManagedSession("planner", id, "Planner", AgentKind.Primary, null), Options());
        await entered.Task;

        Assert.True(coordinator.OwnsAnySession);
        Assert.Single(coordinator.OwnedSessions);
        await coordinator.StopAllAsync();
        release.SetResult();

        await Assert.ThrowsAsync<KeyNotFoundException>(() => start);
        Assert.False(coordinator.OwnsAnySession);
        Assert.Equal(1, Assert.Single(factory.Sessions).DisposeCount);
    }

    [Fact]
    public async Task StopAllStopsEveryExactGenerationOnceAndAllowsFreshOwnership()
    {
        var factory = new Factory();
        var registry = new SessionRegistry();
        var plannerId = Guid.NewGuid();
        var childId = Guid.NewGuid();
        registry.Register(new AgentSession(plannerId, "Planner", AgentKind.Primary, null));
        registry.Register(new AgentSession(childId, "Runtime", AgentKind.SubAgent, plannerId));
        var coordinator = new SessionCoordinator(factory, registry);
        var planner = new ManagedSession("planner", plannerId, "Planner", AgentKind.Primary, null);
        var child = new ManagedSession("child", childId, "Runtime", AgentKind.SubAgent, plannerId);
        await coordinator.StartAsync(planner, Options());
        await coordinator.StartAsync(child, Options());
        var oldOwnership = coordinator.OwnedSessions.OrderBy(item => item.Key).ToArray();

        await coordinator.StopAllAsync();

        Assert.False(coordinator.OwnsAnySession);
        Assert.All(factory.Sessions, terminal => Assert.Equal(1, terminal.DisposeCount));
        Assert.Empty(coordinator.OwnedSessions);

        await coordinator.StartAsync(planner, Options());
        await coordinator.StartAsync(child, Options());
        var freshOwnership = coordinator.OwnedSessions.OrderBy(item => item.Key).ToArray();

        Assert.Equal(oldOwnership.Select(item => item.SessionId), freshOwnership.Select(item => item.SessionId));
        Assert.All(freshOwnership, item => Assert.DoesNotContain(oldOwnership, old => old.Generation == item.Generation));
        Assert.Equal(4, factory.Sessions.Count);
        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task AcceptedOldInputAndOutputNeverReplayToReplacementOwnership()
    {
        var factory = new Factory();
        var registry = new SessionRegistry();
        var id = Guid.NewGuid();
        registry.Register(new AgentSession(id, "Planner", AgentKind.Primary, null));
        var coordinator = new SessionCoordinator(factory, registry);
        var descriptor = new ManagedSession("same-key", id, "Planner", AgentKind.Primary, null);
        var outputs = new List<string>();
        coordinator.OutputReceived += (_, output) => outputs.Add(output.Text);
        await coordinator.StartAsync(descriptor, Options());
        var old = factory.Sessions[0];

        await coordinator.StopAllAsync();
        await coordinator.StartAsync(descriptor, Options());
        old.Emit("stale");
        await coordinator.SubmitUserInputAsync("same-key", "fresh\r");

        Assert.Empty(outputs);
        Assert.Empty(old.Inputs);
        Assert.Equal(["fresh\r"], factory.Sessions[1].Inputs);
        await coordinator.DisposeAsync();
    }

    private static TerminalLaunchOptions Options() => new("cmd.exe", ["/d", "/q"], Environment.CurrentDirectory, new TerminalSize(80, 24));

    private sealed class Factory : ITerminalSessionFactory
    {
        public List<Terminal> Sessions { get; } = [];
        public TaskCompletionSource? StartEntered { get; init; }
        public TaskCompletionSource? ReleaseStart { get; init; }
        public async Task<ITerminalSession> StartAsync(TerminalLaunchOptions options, CancellationToken cancellationToken = default)
        {
            StartEntered?.SetResult();
            if (ReleaseStart is not null) await ReleaseStart.Task.WaitAsync(cancellationToken);
            var terminal = new Terminal(); Sessions.Add(terminal); return terminal;
        }
    }

    private sealed class Terminal : ITerminalSession
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public event EventHandler<string>? OutputReceived;
        public event EventHandler<TerminalExit>? Exited { add { } remove { } }
        public int ProcessId => 1;
        public Task Completion => _completion.Task;
        public List<string> Inputs { get; } = [];
        public int DisposeCount { get; private set; }
        public ValueTask WriteAsync(string input, CancellationToken cancellationToken = default) { Inputs.Add(input); return ValueTask.CompletedTask; }
        public void Resize(TerminalSize size) { }
        public Task StopAsync(CancellationToken cancellationToken = default) { _completion.TrySetResult(); return Task.CompletedTask; }
        public ValueTask DisposeAsync() { DisposeCount++; _completion.TrySetResult(); return ValueTask.CompletedTask; }
        public void Emit(string output) => OutputReceived?.Invoke(this, output);
    }
}
