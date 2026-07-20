using Halfway.Core;

namespace Halfway.Persistence.Tests;

public sealed class WorkspaceRestoreResolverTests : IAsyncDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Halfway.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ValidExplicitDirectoryRetainsHighestPriority()
    {
        var current = CreateDirectory("current"); var configured = CreateDirectory("configured");
        await using var store = await CreateStoreAsync();
        await InsertWorkspaceAsync(store, current, DateTimeOffset.UnixEpoch.AddDays(1));

        Assert.Equal(Path.GetFullPath(configured), await new WorkspaceRestoreResolver(store).ResolveAsync(configured, current));
    }

    [Fact]
    public async Task InvalidExplicitDirectoryContinuesToKnownCurrentWorkspaceWithWindowsPathIdentity()
    {
        var current = CreateDirectory("KnownCurrent");
        await using var store = await CreateStoreAsync();
        await InsertWorkspaceAsync(store, current.ToUpperInvariant(), DateTimeOffset.UnixEpoch);

        var result = await new WorkspaceRestoreResolver(store).ResolveAsync(Path.Combine(_root, "missing"), current.ToLowerInvariant());

        Assert.Equal(Path.GetFullPath(current.ToLowerInvariant()), result, ignoreCase: true);
    }

    [Fact]
    public async Task InvalidExplicitAndUnknownCurrentContinueToValidRecentWorkspace()
    {
        var current = CreateDirectory("current"); var recent = CreateDirectory("Recent");
        await using var store = await CreateStoreAsync();
        await InsertWorkspaceAsync(store, recent.ToUpperInvariant(), DateTimeOffset.UnixEpoch.AddDays(1));

        var result = await new WorkspaceRestoreResolver(store).ResolveAsync(Path.Combine(_root, "missing-explicit"), current);

        Assert.Equal(Path.GetFullPath(recent), result, ignoreCase: true);
    }

    [Fact]
    public async Task InvalidExplicitAndRecentUseCurrentDirectoryAsFinalFallbackWithoutCreatingWorkspace()
    {
        var current = CreateDirectory("current"); var recent = CreateDirectory("recent");
        await using var store = await CreateStoreAsync();
        await InsertWorkspaceAsync(store, recent, DateTimeOffset.UnixEpoch.AddDays(1));
        Directory.Delete(recent);

        var result = await new WorkspaceRestoreResolver(store).ResolveAsync(Path.Combine(_root, "missing-explicit"), current);

        Assert.Equal(Path.GetFullPath(current), result);
        Assert.Null(await store.FindWorkspaceAsync(current));
    }

    [Fact]
    public async Task KnownCurrentDirectoryTakesPriorityOverMostRecentWorkspace()
    {
        var current = CreateDirectory("current"); var recent = CreateDirectory("recent");
        await using var store = await CreateStoreAsync();
        await InsertWorkspaceAsync(store, current, DateTimeOffset.UnixEpoch);
        await InsertWorkspaceAsync(store, recent, DateTimeOffset.UnixEpoch.AddDays(1));

        Assert.Equal(Path.GetFullPath(current), await new WorkspaceRestoreResolver(store).ResolveAsync(null, current));
    }

    [Fact]
    public async Task MostRecentValidWorkspaceRestoresOtherwiseCurrentDirectoryIsUsed()
    {
        var current = CreateDirectory("current"); var older = CreateDirectory("older"); var recent = CreateDirectory("recent");
        await using var store = await CreateStoreAsync();
        await InsertWorkspaceAsync(store, older, DateTimeOffset.UnixEpoch);
        await InsertWorkspaceAsync(store, recent, DateTimeOffset.UnixEpoch.AddDays(1));
        var resolver = new WorkspaceRestoreResolver(store);

        Assert.Equal(Path.GetFullPath(recent), await resolver.ResolveAsync(null, current));
        Directory.Delete(recent);
        Assert.Equal(Path.GetFullPath(current), await resolver.ResolveAsync(null, current));
    }

    [Fact]
    public async Task OpeningWorkspaceMarksItMostRecentWithoutCreatingLifecycleOrAlertFacts()
    {
        var first = CreateDirectory("first"); var second = CreateDirectory("second");
        await using var store = await CreateStoreAsync();
        var firstCatalog = new WorkspaceCatalog(store); await firstCatalog.InitializeAsync(first, LaunchProfile.PowerShell);
        var secondCatalog = new WorkspaceCatalog(store); await secondCatalog.InitializeAsync(second, LaunchProfile.PowerShell);
        await firstCatalog.InitializeAsync(first, LaunchProfile.PowerShell);

        Assert.Equal(firstCatalog.Workspace.Id, (await store.FindMostRecentWorkspaceAsync())!.Id);
        Assert.Empty(await store.LoadPendingAlertsAsync(firstCatalog.SelectedPrimary!.Id));
    }

    private async Task<SqliteWorkspaceStore> CreateStoreAsync()
    {
        var store = new SqliteWorkspaceStore(Path.Combine(_root, "restore.db")); await store.InitializeAsync(); return store;
    }

    private static Task InsertWorkspaceAsync(SqliteWorkspaceStore store, string directory, DateTimeOffset timestamp)
    {
        var id = Guid.NewGuid();
        return store.InsertWorkspaceAsync(new WorkspaceMetadata(id, new DirectoryInfo(directory).Name, directory, null, null, timestamp, timestamp));
    }

    private string CreateDirectory(string name) { var path = Path.Combine(_root, name); Directory.CreateDirectory(path); return path; }
    public ValueTask DisposeAsync() { if (Directory.Exists(_root)) Directory.Delete(_root, true); return ValueTask.CompletedTask; }
}
