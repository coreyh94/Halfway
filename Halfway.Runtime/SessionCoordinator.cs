using Halfway.Core;
using Halfway.Terminal;

namespace Halfway.Runtime;

public sealed class SessionCoordinator : IAsyncDisposable
{
    private readonly ITerminalSessionFactory _factory;
    private readonly SessionRegistry _registry;
    private readonly Dictionary<string, ManagedSessionState> _sessions = new(StringComparer.Ordinal);
    private bool _disposed;

    public SessionCoordinator(ITerminalSessionFactory factory, SessionRegistry registry)
    {
        _factory = factory;
        _registry = registry;
    }

    public event EventHandler<SessionOutput>? OutputReceived;
    public event EventHandler<SessionStateChanged>? StateChanged;
    public event EventHandler<CompletionAlert>? CompletionAlertReady;

    public ManagedSession Get(string key) => GetState(key).Descriptor;

    public async Task StartAsync(
        ManagedSession descriptor,
        TerminalLaunchOptions options,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_sessions.ContainsKey(descriptor.Key))
        {
            throw new InvalidOperationException($"Session '{descriptor.Key}' is already owned by this coordinator.");
        }

        if (!_registry.Contains(descriptor.Id))
        {
            _registry.Register(new AgentSession(descriptor.Id, descriptor.DisplayName, descriptor.Kind, descriptor.ParentId));
        }
        var state = new ManagedSessionState(descriptor);
        state.Owner = this;
        _sessions.Add(descriptor.Key, state);
        Transition(state, AgentStatus.Queued);

        try
        {
            var session = await _factory.StartAsync(options, cancellationToken).ConfigureAwait(false);
            state.Terminal = session;
            session.OutputReceived += state.OutputHandler;
            session.Exited += state.ExitHandler;
            Transition(state, AgentStatus.Running);
        }
        catch (OperationCanceledException)
        {
            await ReleaseFailedStartAsync(state).ConfigureAwait(false);
            Transition(state, AgentStatus.Disconnected);
            throw;
        }
        catch
        {
            await ReleaseFailedStartAsync(state).ConfigureAwait(false);
            Transition(state, AgentStatus.Failed);
            throw;
        }
    }

    public Task StartAsync(
        ManagedSession descriptor,
        IRuntimeLaunchAdapter launchAdapter,
        RuntimeLaunchContext launchContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(launchAdapter);
        var options = launchAdapter.CreateOptions(launchContext, cancellationToken);
        return StartAsync(descriptor, options, cancellationToken);
    }

    public Task WriteAsync(string key, string input, CancellationToken cancellationToken = default)
    {
        var terminal = GetState(key).Terminal;
        return terminal is null
            ? Task.FromException(new InvalidOperationException($"Session '{key}' is not running."))
            : terminal.WriteAsync(input, cancellationToken).AsTask();
    }

    public void Resize(string key, TerminalSize size) => GetState(key).Terminal?.Resize(size);

    public async Task StopAsync(string key, CancellationToken cancellationToken = default)
    {
        var state = GetState(key);
        var terminal = Interlocked.Exchange(ref state.Terminal, null);
        if (terminal is null)
        {
            return;
        }

        terminal.OutputReceived -= state.OutputHandler;
        terminal.Exited -= state.ExitHandler;
        await terminal.DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var key in _sessions.Keys.ToArray())
        {
            await StopAsync(key).ConfigureAwait(false);
        }
    }

    private ManagedSessionState GetState(string key) =>
        _sessions.TryGetValue(key, out var state)
            ? state
            : throw new KeyNotFoundException($"Session '{key}' is not owned by this coordinator.");

    private async Task ReleaseFailedStartAsync(ManagedSessionState state)
    {
        var terminal = Interlocked.Exchange(ref state.Terminal, null);
        if (terminal is not null)
        {
            terminal.OutputReceived -= state.OutputHandler;
            terminal.Exited -= state.ExitHandler;
            try
            {
                await terminal.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Preserve the original launch failure while still releasing ownership.
            }
        }

        _sessions.Remove(state.Descriptor.Key);
    }

    private void HandleOutput(ManagedSessionState state, string output)
    {
        OutputReceived?.Invoke(this, new SessionOutput(state.Descriptor.Key, output));
    }

    private async void HandleExit(ManagedSessionState state, TerminalExit exit)
    {
        var terminal = Interlocked.Exchange(ref state.Terminal, null);
        if (terminal is not null)
        {
            terminal.OutputReceived -= state.OutputHandler;
            terminal.Exited -= state.ExitHandler;
            await terminal.DisposeAsync().ConfigureAwait(false);
        }

        var status = exit.WasCancelled
            ? AgentStatus.Disconnected
            : exit.ExitCode == 0 ? AgentStatus.Completed : AgentStatus.Failed;
        var transition = Transition(state, status);
        if (transition.Alert is not null)
        {
            CompletionAlertReady?.Invoke(this, transition.Alert);
        }
    }

    private LifecycleTransition Transition(ManagedSessionState state, AgentStatus status)
    {
        var transition = _registry.Transition(state.Descriptor.Id, status);
        state.Descriptor = state.Descriptor with { Status = transition.Session.Status };
        StateChanged?.Invoke(this, new SessionStateChanged(state.Descriptor.Key, state.Descriptor.Status));
        return transition;
    }

    private sealed class ManagedSessionState
    {
        public ManagedSessionState(ManagedSession descriptor)
        {
            Descriptor = descriptor;
            OutputHandler = (_, output) => Owner?.HandleOutput(this, output);
            ExitHandler = (_, exit) => Owner?.HandleExit(this, exit);
        }

        public SessionCoordinator? Owner { get; set; }
        public ManagedSession Descriptor { get; set; }
        public ITerminalSession? Terminal;
        public EventHandler<string> OutputHandler { get; }
        public EventHandler<TerminalExit> ExitHandler { get; }
    }
}

public sealed record SessionOutput(string Key, string Text);

public sealed record SessionStateChanged(string Key, AgentStatus Status);
