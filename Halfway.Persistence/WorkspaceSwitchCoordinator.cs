using Halfway.Core;

namespace Halfway.Persistence;

public interface IWorkspaceSwitchAdapter
{
    Guid ActiveWorkspaceId { get; }
    bool OwnsAnySession { get; }
    IReadOnlyCollection<string> PartialInputs { get; }
    Task<bool> ConfirmAsync(CancellationToken cancellationToken);
    Task StopOwnedSessionsAsync(CancellationToken cancellationToken);
    Task FlushPersistenceAsync();
    void InvalidateActivation();
    Task ActivatePresentationAsync(WorkspaceCatalog target, CancellationToken cancellationToken);
    Task StartSelectedSessionsAsync(WorkspaceCatalog target, CancellationToken cancellationToken);
}

public enum WorkspaceSwitchOutcome
{
    ActiveWorkspace,
    Cancelled,
    Activated,
}

public sealed class WorkspaceSwitchCoordinator
{
    private readonly object _queueGate = new();
    private readonly IWorkspaceStore _store;
    private readonly IWorkspaceSwitchAdapter _adapter;
    private readonly Func<string, bool> _directoryExists;
    private Task _tail = Task.CompletedTask;

    public WorkspaceSwitchCoordinator(
        IWorkspaceStore store,
        IWorkspaceSwitchAdapter adapter,
        Func<string, bool>? directoryExists = null)
    {
        _store = store;
        _adapter = adapter;
        _directoryExists = directoryExists ?? Directory.Exists;
    }

    public Task<WorkspaceSwitchOutcome> SwitchAsync(Guid targetWorkspaceId, CancellationToken cancellationToken = default)
    {
        Task<WorkspaceSwitchOutcome> queued;
        lock (_queueGate)
        {
            queued = RunAfterAsync(_tail, targetWorkspaceId, cancellationToken);
            _tail = queued;
        }
        return queued;
    }

    private async Task<WorkspaceSwitchOutcome> RunAfterAsync(Task previous, Guid targetWorkspaceId, CancellationToken cancellationToken)
    {
        try { await previous; }
        catch { }
        cancellationToken.ThrowIfCancellationRequested();
        if (targetWorkspaceId == _adapter.ActiveWorkspaceId) return WorkspaceSwitchOutcome.ActiveWorkspace;

        var target = new WorkspaceCatalog(_store);
        await target.LoadKnownAsync(targetWorkspaceId, _directoryExists, cancellationToken);
        var ownsAnySession = _adapter.OwnsAnySession;
        var requiresConfirmation = WorkspaceSelectionPolicy.RequiresConfirmation(ownsAnySession, _adapter.PartialInputs);
        if (requiresConfirmation && !await _adapter.ConfirmAsync(cancellationToken))
            return WorkspaceSwitchOutcome.Cancelled;

        Exception? stopFailure = null;
        if (ownsAnySession)
        {
            try { await _adapter.StopOwnedSessionsAsync(cancellationToken); }
            catch (Exception exception) { stopFailure = exception; }
        }

        Exception? persistenceFailure = null;
        try { await _adapter.FlushPersistenceAsync(); }
        catch (Exception exception) { persistenceFailure = exception; }

        if (stopFailure is not null || persistenceFailure is not null)
            throw new AggregateException("The current workspace could not be stopped and persisted cleanly.",
                new[] { stopFailure, persistenceFailure }.OfType<Exception>());

        _adapter.InvalidateActivation();
        await _adapter.ActivatePresentationAsync(target, cancellationToken);
        await target.MarkActiveAsync(cancellationToken);
        await _adapter.StartSelectedSessionsAsync(target, cancellationToken);
        return WorkspaceSwitchOutcome.Activated;
    }
}
