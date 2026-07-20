using Halfway.Core;
using Halfway.Terminal;
using Halfway.Terminal.Readiness;

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
    public event EventHandler<LifecycleTransition>? LifecycleTransitioned;

    public ManagedSession Get(string key) => GetState(key).Descriptor;

    public async Task StartAsync(
        ManagedSession descriptor,
        TerminalLaunchOptions options,
        CancellationToken cancellationToken = default)
    {
        await StartAsync(descriptor, options, null, cancellationToken).ConfigureAwait(false);
    }

    public async Task StartAsync(
        ManagedSession descriptor,
        TerminalLaunchOptions options,
        IProcessReadinessAdapter? readiness,
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
        var state = new ManagedSessionState(descriptor, readiness);
        state.Owner = this;
        _sessions.Add(descriptor.Key, state);
        Transition(state, AgentStatus.Queued);

        try
        {
            var session = await _factory.StartAsync(options, cancellationToken).ConfigureAwait(false);
            state.Terminal = session;
            session.OutputReceived += state.OutputHandler;
            session.Exited += state.ExitHandler;
            lock (state.OwnershipGate)
            {
                if (!session.Completion.IsCompleted) Transition(state, AgentStatus.Running);
                state.CompletionWatcher = WatchCompletionAsync(state, session);
            }
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

    public Task StartAsync(
        ManagedSession descriptor,
        IRuntimeLaunchAdapter launchAdapter,
        RuntimeLaunchContext launchContext,
        IProcessReadinessAdapter readiness,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(launchAdapter);
        ArgumentNullException.ThrowIfNull(readiness);
        var options = launchAdapter.CreateOptions(launchContext, cancellationToken);
        return StartAsync(descriptor, options, readiness, cancellationToken);
    }

    public async Task WriteAsync(string key, string input, CancellationToken cancellationToken = default)
    {
        var state = GetState(key); var terminal = state.Terminal;
        if (terminal is null) throw new InvalidOperationException($"Session '{key}' is not running.");
        if (terminal.Completion.IsCompleted)
        {
            await ReconcileTerminalAsync(state, terminal, CompletionStatus(terminal.Completion)).ConfigureAwait(false);
            throw StaleOwnership(key);
        }
        try
        {
            await terminal.WriteAsync(input, cancellationToken).ConfigureAwait(false);
        }
        catch when (terminal.Completion.IsCompleted)
        {
            await ReconcileTerminalAsync(state, terminal, CompletionStatus(terminal.Completion)).ConfigureAwait(false);
            throw StaleOwnership(key);
        }
        state.Readiness?.ObserveInputSubmitted();
        if (state.Descriptor.Status == AgentStatus.Waiting) Transition(state, AgentStatus.Running);
    }

    public void Resize(string key, TerminalSize size)
    {
        var state = GetState(key); var terminal = state.Terminal;
        if (terminal is null) throw new InvalidOperationException($"Session '{key}' is not running.");
        if (terminal.Completion.IsCompleted)
        {
            _ = ReconcileTerminalAsync(state, terminal, CompletionStatus(terminal.Completion));
            throw StaleOwnership(key);
        }
        try
        {
            terminal.Resize(size);
        }
        catch when (terminal.Completion.IsCompleted)
        {
            _ = ReconcileTerminalAsync(state, terminal, CompletionStatus(terminal.Completion));
            throw StaleOwnership(key);
        }
    }

    public async Task StopAsync(string key, CancellationToken cancellationToken = default)
    {
        var state = GetState(key);
        var terminal = Interlocked.Exchange(ref state.Terminal, null);
        if (terminal is null)
        {
            if (IsActive(state.Descriptor.Status))
            {
                Transition(state, AgentStatus.Disconnected);
            }

            _sessions.Remove(key);
            return;
        }

        terminal.OutputReceived -= state.OutputHandler;
        terminal.Exited -= state.ExitHandler;
        try
        {
            await terminal.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            if (IsActive(state.Descriptor.Status))
            {
                Transition(state, AgentStatus.Disconnected);
            }

            _sessions.Remove(key);
        }
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

    private static bool IsActive(AgentStatus status) =>
        status is AgentStatus.Queued or AgentStatus.Running or AgentStatus.Waiting;

    private void HandleOutput(ManagedSessionState state, string output)
    {
        state.Readiness?.ObserveOutput(output);
        if (state.Descriptor.Status == AgentStatus.Running && state.Readiness?.IsReadyForInput == true)
            Transition(state, AgentStatus.Waiting);
        OutputReceived?.Invoke(this, new SessionOutput(state.Descriptor.Key, output));
    }

    private async void HandleExit(ManagedSessionState state, ITerminalSession terminal, TerminalExit exit)
    {
        var status = exit.WasCancelled
            ? AgentStatus.Disconnected
            : exit.ExitCode == 0 ? AgentStatus.Completed : AgentStatus.Failed;
        await ReconcileTerminalAsync(state, terminal, status).ConfigureAwait(false);
    }

    private async Task WatchCompletionAsync(ManagedSessionState state, ITerminalSession terminal)
    {
        AgentStatus status;
        try
        {
            await terminal.Completion.ConfigureAwait(false);
            status = AgentStatus.Disconnected;
        }
        catch (OperationCanceledException)
        {
            status = AgentStatus.Disconnected;
        }
        catch
        {
            status = AgentStatus.Failed;
        }

        await ReconcileTerminalAsync(state, terminal, status).ConfigureAwait(false);
    }

    private async Task ReconcileTerminalAsync(ManagedSessionState state, ITerminalSession terminal, AgentStatus status)
    {
        LifecycleTransition transition;
        lock (state.OwnershipGate)
        {
            if (!ReferenceEquals(Interlocked.CompareExchange(ref state.Terminal, null, terminal), terminal)) return;
            terminal.OutputReceived -= state.OutputHandler;
            terminal.Exited -= state.ExitHandler;
            transition = Transition(state, status);
            _sessions.Remove(state.Descriptor.Key);
        }
        if (transition.Alert is not null)
        {
            CompletionAlertReady?.Invoke(this, transition.Alert);
        }

        if (!terminal.Completion.IsCompleted)
        {
            await terminal.Completion.ConfigureAwait(false);
        }

        try
        {
            await terminal.DisposeAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException) when (terminal.Completion.IsCompleted)
        {
            // The exact owned terminal already ended; duplicate native close is a known teardown race.
        }
        catch (IOException) when (terminal.Completion.IsCompleted)
        {
            // Closed ConPTY pipes can race cleanup after process completion.
        }
    }

    private static AgentStatus CompletionStatus(Task completion) =>
        completion.IsFaulted ? AgentStatus.Failed : AgentStatus.Disconnected;

    private static KeyNotFoundException StaleOwnership(string key) =>
        new($"Session '{key}' no longer has live terminal ownership.");

    private LifecycleTransition Transition(ManagedSessionState state, AgentStatus status)
    {
        var transition = _registry.Transition(state.Descriptor.Id, status);
        state.Descriptor = state.Descriptor with { Status = transition.Session.Status };
        if (transition.Changed) LifecycleTransitioned?.Invoke(this, transition);
        StateChanged?.Invoke(this, new SessionStateChanged(state.Descriptor.Key, state.Descriptor.Status));
        return transition;
    }

    private sealed class ManagedSessionState
    {
        public ManagedSessionState(ManagedSession descriptor, IProcessReadinessAdapter? readiness)
        {
            Descriptor = descriptor;
            Readiness = readiness;
            OutputHandler = (_, output) => Owner?.HandleOutput(this, output);
            ExitHandler = (sender, exit) =>
            {
                if (sender is ITerminalSession terminal) Owner?.HandleExit(this, terminal, exit);
            };
        }

        public SessionCoordinator? Owner { get; set; }
        public ManagedSession Descriptor { get; set; }
        public ITerminalSession? Terminal;
        public object OwnershipGate { get; } = new();
        public IProcessReadinessAdapter? Readiness { get; }
        public Task CompletionWatcher { get; set; } = Task.CompletedTask;
        public EventHandler<string> OutputHandler { get; }
        public EventHandler<TerminalExit> ExitHandler { get; }
    }
}

public sealed record SessionOutput(string Key, string Text);

public sealed record SessionStateChanged(string Key, AgentStatus Status);
