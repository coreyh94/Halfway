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
        if (TryResolveExistingDirectory(configuredDirectory, out var configured)) return configured;
        if (await _store.FindWorkspaceAsync(current, cancellationToken) is not null) return current;
        var recent = await _store.FindMostRecentWorkspaceAsync(cancellationToken);
        return TryResolveExistingDirectory(recent?.WorkingDirectory, out var restored) ? restored : current;
    }

    private bool TryResolveExistingDirectory(string? candidate, out string resolved)
    {
        resolved = string.Empty;
        if (string.IsNullOrWhiteSpace(candidate)) return false;
        try
        {
            var fullPath = Path.GetFullPath(candidate);
            if (!_directoryExists(fullPath)) return false;
            resolved = fullPath;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
