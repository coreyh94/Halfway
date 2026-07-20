using Halfway.Core;
using Xunit;

namespace Halfway.Core.Tests;

public sealed class WorkspaceSelectionPolicyTests
{
    [Fact]
    public void SelectorOrdersDisambiguatesCollapsesWindowsIdentityAndMarksAvailability()
    {
        var first = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var second = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var older = Guid.Parse("00000000-0000-0000-0000-000000000003");
        var created = DateTimeOffset.UnixEpoch;
        var workspaces = new[]
        {
            Workspace(older, "Other", @"C:\Other", created, created),
            Workspace(second, "Repo", @"C:\Second", created.AddMinutes(1), created.AddHours(1)),
            Workspace(first, "Repo", @"C:\First", created.AddMinutes(1), created.AddHours(1)),
            Workspace(Guid.NewGuid(), "case duplicate", @"c:\FIRST\", created, created),
        };

        var items = WorkspaceSelectionPolicy.Create(workspaces, second, path => !path.Contains("Other", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(3, items.Count);
        Assert.Equal(first, items[0].Id);
        Assert.Equal(second, items[1].Id);
        Assert.Contains(@"C:\First", items[0].DisplayText);
        Assert.Contains(@"C:\Second", items[1].DisplayText);
        Assert.True(items[1].IsActive);
        Assert.False(items[2].IsAvailable);
    }

    [Theory]
    [InlineData(false, "", false)]
    [InlineData(true, "", true)]
    [InlineData(false, "partial", true)]
    public void ConfirmationUsesExactOwnershipOrNonemptyPartialInput(bool owns, string partial, bool expected) =>
        Assert.Equal(expected, WorkspaceSelectionPolicy.RequiresConfirmation(owns, new[] { partial }));

    [Fact]
    public void ActivationGenerationInvalidatesOldWork()
    {
        var generation = new WorkspaceActivationGeneration();
        var old = generation.Current;
        Assert.True(generation.IsCurrent(old));
        Assert.NotEqual(old, generation.Advance());
        Assert.False(generation.IsCurrent(old));
    }

    private static WorkspaceMetadata Workspace(Guid id, string name, string path, DateTimeOffset created, DateTimeOffset updated) =>
        new(id, name, path, null, null, created, updated);
}
