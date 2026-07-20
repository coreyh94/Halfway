using Halfway.Core;

namespace Halfway.Persistence;

public sealed class WorkspaceCatalog
{
    private readonly IWorkspaceStore _store;
    private readonly List<SessionMetadata> _sessions = [];

    public WorkspaceCatalog(IWorkspaceStore store) => _store = store;

    public WorkspaceMetadata Workspace { get; private set; } = null!;
    public IReadOnlyList<SessionMetadata> Sessions => _sessions;
    public IEnumerable<SessionMetadata> PrimarySessions => _sessions.Where(x => x.Kind == AgentKind.Primary);
    public IEnumerable<SessionMetadata> SubAgentSessions => _sessions.Where(x => x.Kind == AgentKind.SubAgent);
    public SessionMetadata? SelectedPrimary => _sessions.FirstOrDefault(x => x.Id == Workspace.SelectedPrimarySessionId);
    public SessionMetadata? SelectedSubAgent => _sessions.FirstOrDefault(x => x.Id == Workspace.SelectedSubAgentSessionId);

    public event EventHandler? Changed;

    public async Task InitializeAsync(string workingDirectory, LaunchProfile runtimeProfile, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        await _store.InitializeAsync(cancellationToken);
        var path = Path.GetFullPath(workingDirectory);
        Workspace = await _store.FindWorkspaceAsync(path, cancellationToken) ?? await SeedAsync(path, runtimeProfile, cancellationToken);
        _sessions.Clear();
        _sessions.AddRange(await _store.LoadSessionsAsync(Workspace.Id, cancellationToken));
        await EnsureValidSelectionsAsync(cancellationToken);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task<SessionMetadata> CreateSubAgentAsync(string displayName, LaunchProfile profile, CancellationToken cancellationToken = default)
    {
        var name = displayName?.Trim() ?? string.Empty;
        if (name.Length == 0) throw new ArgumentException("Display name is required.", nameof(displayName));
        if (SubAgentSessions.Any(x => string.Equals(x.DisplayName, name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"A sub-agent named '{name}' already exists.");
        var parent = SelectedPrimary ?? throw new InvalidOperationException("A primary session must be selected.");
        var id = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        var session = new SessionMetadata(id, Workspace.Id, $"session-{id:N}", name, AgentKind.SubAgent, parent.Id, profile,
            _sessions.Where(x => x.Kind == AgentKind.SubAgent).Select(x => x.DisplayOrder).DefaultIfEmpty(-1).Max() + 1,
            AgentStatus.Queued, now, now);
        await _store.InsertSessionAsync(session, cancellationToken);
        _sessions.Add(session);
        await SelectSubAgentAsync(session.Id, cancellationToken);
        return session;
    }

    public Task SelectPrimaryAsync(Guid id, CancellationToken cancellationToken = default) => SelectAsync(id, AgentKind.Primary, cancellationToken);
    public Task SelectSubAgentAsync(Guid id, CancellationToken cancellationToken = default) => SelectAsync(id, AgentKind.SubAgent, cancellationToken);

    public async Task UpdateStatusAsync(Guid id, AgentStatus status, CancellationToken cancellationToken = default)
    {
        var index = _sessions.FindIndex(x => x.Id == id);
        if (index < 0) throw new KeyNotFoundException($"Session {id} is not in the catalog.");
        _sessions[index] = _sessions[index] with { LastStatus = status, UpdatedAtUtc = DateTimeOffset.UtcNow };
        await _store.UpdateStatusAsync(id, status, cancellationToken);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task SelectAsync(Guid id, AgentKind kind, CancellationToken token)
    {
        if (!_sessions.Any(x => x.Id == id && x.Kind == kind)) throw new ArgumentException("Session is not a selectable member of this workspace.", nameof(id));
        Workspace = kind == AgentKind.Primary
            ? Workspace with { SelectedPrimarySessionId = id, UpdatedAtUtc = DateTimeOffset.UtcNow }
            : Workspace with { SelectedSubAgentSessionId = id, UpdatedAtUtc = DateTimeOffset.UtcNow };
        await _store.UpdateSelectionsAsync(Workspace.Id, Workspace.SelectedPrimarySessionId, Workspace.SelectedSubAgentSessionId, token);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task<WorkspaceMetadata> SeedAsync(string path, LaunchProfile runtimeProfile, CancellationToken token)
    {
        var now = DateTimeOffset.UtcNow; var workspaceId = Guid.NewGuid(); var plannerId = Guid.NewGuid(); var runtimeId = Guid.NewGuid();
        var name = new DirectoryInfo(path).Name; if (string.IsNullOrWhiteSpace(name)) name = path;
        var workspace = new WorkspaceMetadata(workspaceId, name, path, plannerId, runtimeId, now, now);
        var planner = new SessionMetadata(plannerId, workspaceId, $"session-{plannerId:N}", "Planner", AgentKind.Primary, null, LaunchProfile.PowerShell, 0, AgentStatus.Queued, now, now);
        var runtime = new SessionMetadata(runtimeId, workspaceId, $"session-{runtimeId:N}", "Runtime", AgentKind.SubAgent, plannerId, runtimeProfile, 0, AgentStatus.Queued, now, now);
        await _store.InsertInitialWorkspaceAsync(workspace, [planner, runtime], token);
        return workspace;
    }

    private async Task EnsureValidSelectionsAsync(CancellationToken token)
    {
        var primary = SelectedPrimary ?? PrimarySessions.OrderBy(x => x.DisplayOrder).FirstOrDefault();
        var sub = SelectedSubAgent ?? SubAgentSessions.OrderBy(x => x.DisplayOrder).FirstOrDefault();
        if (primary?.Id != Workspace.SelectedPrimarySessionId || sub?.Id != Workspace.SelectedSubAgentSessionId)
        {
            Workspace = Workspace with { SelectedPrimarySessionId = primary?.Id, SelectedSubAgentSessionId = sub?.Id, UpdatedAtUtc = DateTimeOffset.UtcNow };
            await _store.UpdateSelectionsAsync(Workspace.Id, primary?.Id, sub?.Id, token);
        }
    }
}
