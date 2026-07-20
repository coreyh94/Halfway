using Halfway.Core;
using Halfway.Terminal;
using Halfway.Terminal.Readiness;

namespace Halfway.Runtime;

public sealed class SessionCoordinator : IAsyncDisposable
{
    private readonly ITerminalSessionFactory _factory;
    private readonly SessionRegistry _registry;
    private readonly Dictionary<string, ManagedSessionState> _sessions = new(StringComparer.Ordinal);
    private readonly object _ownershipGate = new();
    private readonly SessionCoordinatorHooks? _hooks;
    private bool _disposed;

    public SessionCoordinator(ITerminalSessionFactory factory, SessionRegistry registry)
        : this(factory, registry, null)
    {
    }

    internal SessionCoordinator(ITerminalSessionFactory factory, SessionRegistry registry, SessionCoordinatorHooks? hooks)
    {
        _factory = factory;
        _registry = registry;
        _hooks = hooks;
    }

    public event EventHandler<SessionOutput>? OutputReceived;
    public event EventHandler<SessionStateChanged>? StateChanged;
    public event EventHandler<CompletionAlert>? CompletionAlertReady;
    public event EventHandler<LifecycleTransition>? LifecycleTransitioned;

    public bool OwnsAnySession
    {
        get { lock (_ownershipGate) return _sessions.Count > 0; }
    }

    public IReadOnlyList<SessionOwnership> OwnedSessions
    {
        get
        {
            lock (_ownershipGate)
                return _sessions.Values.Select(IdentityLocked).ToArray();
        }
    }

    public ManagedSession Get(string key)
    {
        lock (_ownershipGate) return GetStateLocked(key).Descriptor;
    }

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
        ManagedSessionState state;
        lock (_ownershipGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_sessions.ContainsKey(descriptor.Key))
                throw new InvalidOperationException($"Session '{descriptor.Key}' is already owned by this coordinator.");

            if (!_registry.Contains(descriptor.Id))
                _registry.Register(new AgentSession(descriptor.Id, descriptor.DisplayName, descriptor.Kind, descriptor.ParentId));

            state = new ManagedSessionState(descriptor, readiness);
            state.Owner = this;
            state.UserInput = new SubmittedInputQueue((input, token) => WriteSubmittedInputAsync(state, input, token));
            _sessions.Add(descriptor.Key, state);
            TransitionLocked(state, AgentStatus.Queued);
        }

        try
        {
            var terminal = await _factory.StartAsync(options, cancellationToken).ConfigureAwait(false);
            var accepted = false;
            lock (_ownershipGate)
            {
                if (OwnsStateLocked(state))
                {
                    state.Terminal = terminal;
                    terminal.OutputReceived += state.OutputHandler;
                    terminal.Exited += state.ExitHandler;
                    if (!terminal.Completion.IsCompleted) TransitionLocked(state, AgentStatus.Running);
                    state.CompletionWatcher = WatchCompletionAsync(state, terminal);
                    accepted = true;
                }
            }
            if (!accepted)
            {
                await terminal.DisposeAsync().ConfigureAwait(false);
                throw StaleOwnership(descriptor.Key);
            }
        }
        catch (OperationCanceledException)
        {
            await ReleaseFailedStartAsync(state, AgentStatus.Disconnected).ConfigureAwait(false);
            throw;
        }
        catch
        {
            await ReleaseFailedStartAsync(state, AgentStatus.Failed).ConfigureAwait(false);
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
        return StartAsync(descriptor, launchAdapter.CreateOptions(launchContext, cancellationToken), cancellationToken);
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
        return StartAsync(descriptor, launchAdapter.CreateOptions(launchContext, cancellationToken), readiness, cancellationToken);
    }

    public async Task WriteAsync(string key, string input, CancellationToken cancellationToken = default)
    {
        SessionOwnership ownership;
        lock (_ownershipGate)
        {
            var state = GetStateLocked(key);
            ownership = OwnershipLocked(state);
        }

        var outcome = await WriteOwnedAsync(ownership, input, requireReady: false, static () => true, suppressWriteExceptions: false, cancellationToken).ConfigureAwait(false);
        if (outcome != OwnedWriteOutcome.Succeeded) throw StaleOwnership(key);
    }

    public SessionOwnership CaptureOwnership(string key, Guid expectedSessionId)
    {
        lock (_ownershipGate)
        {
            var state = GetStateLocked(key);
            if (state.Descriptor.Id != expectedSessionId || state.Descriptor.Kind != AgentKind.Primary) throw StaleOwnership(key);
            return OwnershipLocked(state);
        }
    }

    public bool IsCurrentOwnership(string key, Guid generation)
    {
        lock (_ownershipGate)
        {
            return _sessions.TryGetValue(key, out var state) &&
                state.Generation == generation &&
                IsWritableLocked(state);
        }
    }

    public Task<OwnedWriteOutcome> TryWriteAlertAsync(
        SessionOwnership ownership,
        string input,
        Func<bool> isStillSafe,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(isStillSafe);
        return WriteOwnedAsync(ownership, input, requireReady: true, isStillSafe, suppressWriteExceptions: true, cancellationToken);
    }

    public SubmittedInputAcceptance SubmitUserInput(string key, string input, CancellationToken cancellationToken = default)
    {
        lock (_ownershipGate)
        {
            var state = GetStateLocked(key);
            if (!IsWritableLocked(state)) throw new SessionInputUnavailableException(key);
            return state.UserInput.Accept(input, cancellationToken);
        }
    }

    public Task SubmitUserInputAsync(string key, string input, CancellationToken cancellationToken = default) =>
        SubmitUserInput(key, input, cancellationToken).Completion;

    public void Resize(string key, TerminalSize size)
    {
        ITerminalSession terminal;
        lock (_ownershipGate)
        {
            var state = GetStateLocked(key);
            terminal = state.Terminal ?? throw StaleOwnership(key);
            if (terminal.Completion.IsCompleted) throw StaleOwnership(key);
            terminal.Resize(size);
        }
    }

    public async Task StopAsync(string key, CancellationToken cancellationToken = default)
    {
        SessionOwnership ownership;
        lock (_ownershipGate) ownership = IdentityLocked(GetStateLocked(key));
        await StopOwnedAsync(ownership, cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAllAsync(CancellationToken cancellationToken = default)
    {
        var ownerships = OwnedSessions;
        var failures = new List<Exception>();
        foreach (var ownership in ownerships)
        {
            try { await StopOwnedAsync(ownership, cancellationToken).ConfigureAwait(false); }
            catch (KeyNotFoundException) { }
            catch (Exception exception) { failures.Add(exception); }
        }
        if (failures.Count > 0) throw new AggregateException("One or more terminal sessions failed to stop cleanly.", failures);
    }

    private async Task StopOwnedAsync(SessionOwnership ownership, CancellationToken cancellationToken)
    {
        ITerminalSession? terminal;
        ManagedSessionState state;
        lock (_ownershipGate)
        {
            state = GetStateLocked(ownership.Key);
            if (state.Descriptor.Id != ownership.SessionId || state.Generation != ownership.Generation) throw StaleOwnership(ownership.Key);
            _sessions.Remove(ownership.Key);
            terminal = state.Terminal;
            state.Terminal = null;
            state.UserInput.Close(new SessionInputUnavailableException(ownership.Key));
            if (terminal is not null)
            {
                terminal.OutputReceived -= state.OutputHandler;
                terminal.Exited -= state.ExitHandler;
            }
            if (IsActive(state.Descriptor.Status)) TransitionLocked(state, AgentStatus.Disconnected);
        }

        if (terminal is not null) await terminal.DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        string[] keys;
        lock (_ownershipGate)
        {
            if (_disposed) return;
            _disposed = true;
            keys = _sessions.Keys.ToArray();
        }

        var failures = new List<Exception>();
        foreach (var key in keys)
        {
            try { await StopAsync(key).ConfigureAwait(false); }
            catch (KeyNotFoundException) { }
            catch (Exception exception) { failures.Add(exception); }
        }
        if (failures.Count > 0) throw new AggregateException("One or more terminal sessions failed to stop cleanly.", failures);
    }

    private ManagedSessionState GetStateLocked(string key) =>
        _sessions.TryGetValue(key, out var state)
            ? state
            : throw new KeyNotFoundException($"Session '{key}' is not owned by this coordinator.");

    private bool OwnsStateLocked(ManagedSessionState state) =>
        _sessions.TryGetValue(state.Descriptor.Key, out var current) && ReferenceEquals(current, state);

    private bool OwnsTerminalLocked(ManagedSessionState state, ITerminalSession terminal) =>
        OwnsStateLocked(state) && ReferenceEquals(state.Terminal, terminal);

    private SessionOwnership OwnershipLocked(ManagedSessionState state)
    {
        if (!IsWritableLocked(state)) throw StaleOwnership(state.Descriptor.Key);
        return new SessionOwnership(state.Descriptor.Key, state.Descriptor.Id, state.Generation);
    }

    private static SessionOwnership IdentityLocked(ManagedSessionState state) =>
        new(state.Descriptor.Key, state.Descriptor.Id, state.Generation);

    private static bool IsWritableLocked(ManagedSessionState state) =>
        state.Terminal is { } terminal &&
        !terminal.Completion.IsCompleted &&
        state.Descriptor.Status is AgentStatus.Running or AgentStatus.Waiting;

    private async Task ReleaseFailedStartAsync(ManagedSessionState state, AgentStatus status)
    {
        ITerminalSession? terminal;
        lock (_ownershipGate)
        {
            if (OwnsStateLocked(state))
            {
                if (IsActive(state.Descriptor.Status)) TransitionLocked(state, status);
                _sessions.Remove(state.Descriptor.Key);
            }
            state.UserInput.Close(new SessionInputUnavailableException(state.Descriptor.Key));
            terminal = state.Terminal;
            state.Terminal = null;
            if (terminal is not null)
            {
                terminal.OutputReceived -= state.OutputHandler;
                terminal.Exited -= state.ExitHandler;
            }
        }

        if (terminal is null) return;
        try { await terminal.DisposeAsync().ConfigureAwait(false); }
        catch { }
    }

    private static bool IsActive(AgentStatus status) =>
        status is AgentStatus.Queued or AgentStatus.Running or AgentStatus.Waiting;

    private static KeyNotFoundException StaleOwnership(string key) =>
        new($"Session '{key}' no longer has live terminal ownership.");

    private void HandleOutput(ManagedSessionState state, ITerminalSession terminal, string output)
    {
        var shouldWait = false;
        lock (_ownershipGate)
        {
            if (!OwnsTerminalLocked(state, terminal) || !IsActive(state.Descriptor.Status) || terminal.Completion.IsCompleted) return;
            state.Readiness?.ObserveOutput(output);
            shouldWait = state.Descriptor.Status == AgentStatus.Running && state.Readiness?.IsReadyForInput == true;
        }

        _hooks?.OutputClassified?.Invoke();

        SessionOutput item;
        lock (_ownershipGate)
        {
            if (!OwnsTerminalLocked(state, terminal) || !IsActive(state.Descriptor.Status) || terminal.Completion.IsCompleted) return;
            if (shouldWait && state.Descriptor.Status == AgentStatus.Running) TransitionLocked(state, AgentStatus.Waiting);
            item = new SessionOutput(state.Descriptor.Key, output, state.Generation);
        }
        OutputReceived?.Invoke(this, item);
    }

    private async void HandleExit(ManagedSessionState state, ITerminalSession terminal, TerminalExit exit)
    {
        var status = exit.WasCancelled ? AgentStatus.Disconnected : exit.ExitCode == 0 ? AgentStatus.Completed : AgentStatus.Failed;
        await ReconcileTerminalAsync(state, terminal, status).ConfigureAwait(false);
    }

    private async Task WatchCompletionAsync(ManagedSessionState state, ITerminalSession terminal)
    {
        AgentStatus status;
        try { await terminal.Completion.ConfigureAwait(false); status = AgentStatus.Disconnected; }
        catch (OperationCanceledException) { status = AgentStatus.Disconnected; }
        catch { status = AgentStatus.Failed; }
        await ReconcileTerminalAsync(state, terminal, status).ConfigureAwait(false);
    }

    private async Task ReconcileTerminalAsync(ManagedSessionState state, ITerminalSession terminal, AgentStatus status)
    {
        LifecycleTransition transition;
        lock (_ownershipGate)
        {
            if (!OwnsTerminalLocked(state, terminal)) return;
            _sessions.Remove(state.Descriptor.Key);
            state.Terminal = null;
            state.UserInput.Close(new SessionInputUnavailableException(state.Descriptor.Key));
            terminal.OutputReceived -= state.OutputHandler;
            terminal.Exited -= state.ExitHandler;
            transition = TransitionLocked(state, status);
        }

        if (transition.Alert is not null) CompletionAlertReady?.Invoke(this, transition.Alert);
        if (!terminal.Completion.IsCompleted) await terminal.Completion.ConfigureAwait(false);
        try { await terminal.DisposeAsync().ConfigureAwait(false); }
        catch (ObjectDisposedException) when (terminal.Completion.IsCompleted) { }
        catch (IOException) when (terminal.Completion.IsCompleted) { }
    }

    private async Task WriteSubmittedInputAsync(ManagedSessionState state, string input, CancellationToken cancellationToken)
    {
        await state.InputGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ITerminalSession terminal;
            ValueTask write;
            lock (_ownershipGate)
            {
                if (!OwnsStateLocked(state) || !IsWritableLocked(state)) throw new SessionInputUnavailableException(state.Descriptor.Key);
                terminal = state.Terminal!;
                write = terminal.WriteAsync(input, cancellationToken);
            }

            await write.ConfigureAwait(false);
            lock (_ownershipGate)
            {
                if (!OwnsTerminalLocked(state, terminal) || !IsActive(state.Descriptor.Status))
                    throw new SessionInputUnavailableException(state.Descriptor.Key);
                state.Readiness?.ObserveInputSubmitted();
                if (state.Descriptor.Status == AgentStatus.Waiting) TransitionLocked(state, AgentStatus.Running);
            }
        }
        finally { state.InputGate.Release(); }
    }

    private async Task<OwnedWriteOutcome> WriteOwnedAsync(
        SessionOwnership ownership,
        string input,
        bool requireReady,
        Func<bool> isStillSafe,
        bool suppressWriteExceptions,
        CancellationToken cancellationToken)
    {
        ManagedSessionState state;
        lock (_ownershipGate)
        {
            if (!_sessions.TryGetValue(ownership.Key, out state!) || state.Descriptor.Id != ownership.SessionId || state.Generation != ownership.Generation)
                return OwnedWriteOutcome.RejectedBeforeWrite;
        }

        await state.InputGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _hooks?.BeforeDirectWrite?.Invoke();
            ITerminalSession terminal;
            ValueTask write;
            lock (_ownershipGate)
            {
                if (!OwnsStateLocked(state) || state.Descriptor.Id != ownership.SessionId || state.Generation != ownership.Generation || !IsWritableLocked(state))
                    return OwnedWriteOutcome.RejectedBeforeWrite;
                if (requireReady && state.Readiness?.IsReadyForInput != true) return OwnedWriteOutcome.RejectedBeforeWrite;
                if (!isStillSafe()) return OwnedWriteOutcome.RejectedBeforeWrite;
                terminal = state.Terminal!;
                write = terminal.WriteAsync(input, cancellationToken);
            }

            try { await write.ConfigureAwait(false); }
            catch when (suppressWriteExceptions) { return OwnedWriteOutcome.IndeterminateFailure; }

            lock (_ownershipGate)
            {
                if (OwnsTerminalLocked(state, terminal) && IsActive(state.Descriptor.Status))
                {
                    state.Readiness?.ObserveInputSubmitted();
                    if (state.Descriptor.Status == AgentStatus.Waiting) TransitionLocked(state, AgentStatus.Running);
                }
            }
            return OwnedWriteOutcome.Succeeded;
        }
        finally { state.InputGate.Release(); }
    }

    private LifecycleTransition TransitionLocked(ManagedSessionState state, AgentStatus status)
    {
        var transition = _registry.Transition(state.Descriptor.Id, status);
        state.Descriptor = state.Descriptor with { Status = transition.Session.Status };
        if (transition.Changed) LifecycleTransitioned?.Invoke(this, transition);
        StateChanged?.Invoke(this, new SessionStateChanged(state.Descriptor.Key, state.Descriptor.Status) { Generation = state.Generation });
        return transition;
    }

    private sealed class ManagedSessionState
    {
        public ManagedSessionState(ManagedSession descriptor, IProcessReadinessAdapter? readiness)
        {
            Descriptor = descriptor;
            Readiness = readiness;
            Generation = Guid.NewGuid();
            OutputHandler = (sender, output) =>
            {
                if (sender is ITerminalSession terminal) Owner?.HandleOutput(this, terminal, output);
            };
            ExitHandler = (sender, exit) =>
            {
                if (sender is ITerminalSession terminal) Owner?.HandleExit(this, terminal, exit);
            };
        }

        public SessionCoordinator? Owner { get; set; }
        public ManagedSession Descriptor { get; set; }
        public Guid Generation { get; }
        public ITerminalSession? Terminal { get; set; }
        public SemaphoreSlim InputGate { get; } = new(1, 1);
        public IProcessReadinessAdapter? Readiness { get; }
        public Task CompletionWatcher { get; set; } = Task.CompletedTask;
        public SubmittedInputQueue UserInput { get; set; } = null!;
        public EventHandler<string> OutputHandler { get; }
        public EventHandler<TerminalExit> ExitHandler { get; }
    }
}

public sealed record SessionOwnership(string Key, Guid SessionId, Guid Generation);

public enum OwnedWriteOutcome
{
    Succeeded,
    RejectedBeforeWrite,
    IndeterminateFailure,
}

internal sealed class SessionCoordinatorHooks
{
    public Action? OutputClassified { get; init; }
    public Action? BeforeDirectWrite { get; init; }
}

public sealed record SessionOutput(string Key, string Text, Guid Generation);

public sealed record SessionStateChanged(string Key, AgentStatus Status)
{
    public Guid Generation { get; init; }
}
