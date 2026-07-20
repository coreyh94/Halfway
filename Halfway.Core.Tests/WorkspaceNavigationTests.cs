using Halfway.Core;
using Xunit;

namespace Halfway.Core.Tests;

public sealed class WorkspaceNavigationTests
{
    private readonly Guid _first = Guid.NewGuid();
    private readonly Guid _second = Guid.NewGuid();

    [Fact]
    public void MovesNextPreviousAndWraps()
    {
        Guid[] ids = [_first, _second];
        Assert.Equal(_second, WorkspaceNavigation.Move(ids, _first, 1));
        Assert.Equal(_first, WorkspaceNavigation.Move(ids, _second, -1));
        Assert.Equal(_first, WorkspaceNavigation.Move(ids, _second, 1));
        Assert.Equal(_second, WorkspaceNavigation.Move(ids, _first, -1));
    }

    [Fact]
    public void EmptyAndSingleCollectionsAreDeterministic()
    {
        Assert.Null(WorkspaceNavigation.Move([], null, 1));
        Assert.Equal(_first, WorkspaceNavigation.Move([_first], _first, 1));
        Assert.Equal(_first, WorkspaceNavigation.Move([_first], _first, -1));
    }

    [Fact]
    public void ReselectingCurrentSessionWithZeroOffsetKeepsItSelected()
    {
        Assert.Equal(_first, WorkspaceNavigation.Move([_first, _second], _first, 0));
    }

    [Fact]
    public void SidebarOrdersPrimariesBeforeSubAgentsAndUsesDisplayOrder()
    {
        var workspace = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        var primaryLater = Session(_first, workspace, AgentKind.Primary, 2, now);
        var primaryFirst = Session(_second, workspace, AgentKind.Primary, 1, now);
        var subFirst = Session(Guid.NewGuid(), workspace, AgentKind.SubAgent, 0, now);
        var subLater = Session(Guid.NewGuid(), workspace, AgentKind.SubAgent, 3, now);

        Assert.Equal([primaryFirst.Id, primaryLater.Id, subFirst.Id, subLater.Id], WorkspaceNavigation.SidebarOrder([subLater, primaryLater, subFirst, primaryFirst]));
    }

    [Fact]
    public void SelectsPrimaryAndSubAgentTargets()
    {
        Assert.Equal(_first, WorkspaceNavigation.SelectTarget(WorkspaceFocusTarget.Primary, _first, _second));
        Assert.Equal(_second, WorkspaceNavigation.SelectTarget(WorkspaceFocusTarget.SubAgent, _first, _second));
    }

    private static SessionMetadata Session(Guid id, Guid workspace, AgentKind kind, int order, DateTimeOffset now) =>
        new(id, workspace, id.ToString("N"), id.ToString("N"), kind, kind == AgentKind.SubAgent ? Guid.NewGuid() : null, LaunchProfile.PowerShell, order, AgentStatus.Disconnected, now, now);
}
