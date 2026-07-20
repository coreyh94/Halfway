namespace Halfway.Core;

public sealed record WorkspaceSelectorItem(
    Guid Id,
    string DisplayText,
    string WorkingDirectory,
    bool IsActive,
    bool IsAvailable,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset CreatedAtUtc);

public static class WorkspaceSelectionPolicy
{
    public static IReadOnlyList<WorkspaceSelectorItem> Create(
        IEnumerable<WorkspaceMetadata> workspaces,
        Guid activeWorkspaceId,
        Func<string, bool>? directoryExists = null)
    {
        ArgumentNullException.ThrowIfNull(workspaces);
        directoryExists ??= Directory.Exists;
        var ordered = workspaces
            .OrderByDescending(item => item.UpdatedAtUtc)
            .ThenByDescending(item => item.CreatedAtUtc)
            .ThenBy(item => item.Id)
            .GroupBy(item => CanonicalPath(item.WorkingDirectory), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        var duplicateNames = ordered
            .GroupBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return ordered.Select(item => new WorkspaceSelectorItem(
            item.Id,
            duplicateNames.Contains(item.DisplayName) ? $"{item.DisplayName} — {item.WorkingDirectory}" : item.DisplayName,
            item.WorkingDirectory,
            item.Id == activeWorkspaceId,
            IsAvailable(item.WorkingDirectory, directoryExists),
            item.UpdatedAtUtc,
            item.CreatedAtUtc)).ToArray();
    }

    public static bool RequiresConfirmation(bool ownsAnySession, IEnumerable<string?> partialInputs) =>
        ownsAnySession || partialInputs.Any(input => !string.IsNullOrEmpty(input));

    private static bool IsAvailable(string path, Func<string, bool> directoryExists)
    {
        try { return directoryExists(Path.GetFullPath(path)); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    private static string CanonicalPath(string path)
    {
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)).ToUpperInvariant(); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant();
        }
    }
}

public sealed class WorkspaceActivationGeneration
{
    private long _value = 1;
    public long Current => Interlocked.Read(ref _value);
    public long Advance() => Interlocked.Increment(ref _value);
    public bool IsCurrent(long value) => Current == value;
}
