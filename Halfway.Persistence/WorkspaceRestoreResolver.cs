namespace Halfway.Persistence;

public sealed class WorkspaceRestoreResolver
{
    private readonly IWorkspaceStore _store;
    private readonly Func<string, bool> _directoryExists;

    public WorkspaceRestoreResolver(IWorkspaceStore store, Func<string, bool>? directoryExists = null)
    {
        _store = store;
        _directoryExists = directoryExists ?? Directory.Exists;
    }

    public async Task<string> ResolveAsync(string? configuredDirectory, string currentDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentDirectory);
        var current = Path.GetFullPath(currentDirectory);
        if (!string.IsNullOrWhiteSpace(configuredDirectory))
            return _directoryExists(configuredDirectory) ? Path.GetFullPath(configuredDirectory) : current;
        if (await _store.FindWorkspaceAsync(current, cancellationToken) is not null) return current;
        var recent = await _store.FindMostRecentWorkspaceAsync(cancellationToken);
        return recent is not null && _directoryExists(recent.WorkingDirectory) ? Path.GetFullPath(recent.WorkingDirectory) : current;
    }
}
