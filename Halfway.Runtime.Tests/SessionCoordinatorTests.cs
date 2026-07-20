using Halfway.Core;
using Halfway.Runtime;
using Halfway.Terminal;
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
        await coordinator.StartAsync(Descriptor("runtime", runtimeId, "Runtime", plannerId), Options());
        factory.Sessions[0].EmitExit(0, false);
        factory.Sessions[0].EmitExit(0, false);
        await Task.Delay(20);
        Assert.Equal(AgentStatus.Completed, registry.Get(runtimeId).Status);
        Assert.Equal("[Halfway Alert!] Runtime completed. Continue orchestration.", Assert.Single(alerts).Message);
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
        await coordinator.StartAsync(Descriptor("runtime", runtimeId, "Runtime", plannerId), Options());
        factory.Sessions[0].EmitExit(exitCode, cancelled);
        await Task.Delay(20);
        Assert.Equal(expected, registry.Get(runtimeId).Status);
        Assert.DoesNotContain(registry.Events, item => item.AlertEligible);
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

    private sealed class FakeFactory : ITerminalSessionFactory
    {
        public List<FakeSession> Sessions { get; } = [];
        public Task<ITerminalSession> StartAsync(TerminalLaunchOptions options, CancellationToken cancellationToken = default)
        { var session = new FakeSession(Sessions.Count + 1); Sessions.Add(session); return Task.FromResult<ITerminalSession>(session); }
    }

    private sealed class FakeSession(int processId) : ITerminalSession
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsRunning { get; private set; } = true;
        public event EventHandler<string>? OutputReceived;
        public event EventHandler<TerminalExit>? Exited;
        public int ProcessId => processId;
        public Task Completion => _completion.Task;
        public ValueTask WriteAsync(string input, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public void Resize(TerminalSize size) { }
        public Task StopAsync(CancellationToken cancellationToken = default) { EmitExit(0, true); return Task.CompletedTask; }
        public ValueTask DisposeAsync() { IsRunning = false; _completion.TrySetResult(); return ValueTask.CompletedTask; }
        public void EmitOutput(string output) => OutputReceived?.Invoke(this, output);
        public void EmitExit(int code, bool cancelled) { IsRunning = false; _completion.TrySetResult(); Exited?.Invoke(this, new TerminalExit(code, cancelled)); }
    }
}
