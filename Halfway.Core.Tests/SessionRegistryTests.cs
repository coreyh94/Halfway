using Halfway.Core;
using Xunit;

namespace Halfway.Core.Tests;

public sealed class SessionRegistryTests
{
    [Fact]
    public void Completion_creates_one_short_parent_alert()
    {
        var registry = new SessionRegistry();
        var parent = new AgentSession(Guid.NewGuid(), "Planner", AgentKind.Primary, null, AgentStatus.Running);
        var child = new AgentSession(Guid.NewGuid(), "Runtime", AgentKind.SubAgent, parent.Id, AgentStatus.Running);
        registry.Register(parent);
        registry.Register(child);

        var transition = registry.Transition(child.Id, AgentStatus.Completed, DateTimeOffset.UnixEpoch);

        Assert.True(transition.Changed);
        Assert.NotNull(transition.Alert);
        Assert.Equal(parent.Id, transition.Alert!.ParentSessionId);
        Assert.Equal("[Halfway Alert!] Runtime completed. Continue orchestration.", transition.Alert.Message);
        Assert.Single(registry.Events);
        Assert.True(registry.Events[0].AlertEligible);
    }

    [Fact]
    public void Repeating_the_same_status_is_ignored()
    {
        var registry = new SessionRegistry();
        var session = new AgentSession(Guid.NewGuid(), "Planner", AgentKind.Primary, null, AgentStatus.Running);
        registry.Register(session);

        var transition = registry.Transition(session.Id, AgentStatus.Running);

        Assert.False(transition.Changed);
        Assert.Empty(registry.Events);
        Assert.Equal(AgentStatus.Running, registry.Get(session.Id).Status);
    }

    [Fact]
    public void A_completed_session_does_not_create_a_second_completion_event()
    {
        var registry = new SessionRegistry();
        var parent = new AgentSession(Guid.NewGuid(), "Planner", AgentKind.Primary, null, AgentStatus.Running);
        var session = new AgentSession(Guid.NewGuid(), "Runtime", AgentKind.SubAgent, parent.Id, AgentStatus.Running);
        registry.Register(parent);
        registry.Register(session);

        var first = registry.Transition(session.Id, AgentStatus.Completed);
        var second = registry.Transition(session.Id, AgentStatus.Completed);

        Assert.NotNull(first.Alert);
        Assert.Null(second.Alert);
        Assert.Single(registry.Events);
    }

    [Theory]
    [InlineData("Runtime|UI", "[Halfway Alert!] Runtime and UI completed. Continue orchestration.")]
    [InlineData("Runtime|UI|Tests", "[Halfway Alert!] Runtime, UI and Tests completed. Continue orchestration.")]
    public void Completion_alert_formats_deterministic_batches(string joinedNames, string expected)
    {
        var alert = new CompletionAlert(Guid.NewGuid(), Guid.NewGuid(), joinedNames.Split('|'));

        Assert.Equal(expected, alert.Message);
    }

    [Theory]
    [InlineData(AgentStatus.Queued)]
    [InlineData(AgentStatus.Running)]
    [InlineData(AgentStatus.Completed)]
    [InlineData(AgentStatus.Failed)]
    [InlineData(AgentStatus.Disconnected)]
    public void Process_lifecycle_states_are_recorded(AgentStatus status)
    {
        var registry = new SessionRegistry();
        var session = new AgentSession(Guid.NewGuid(), "Planner", AgentKind.Primary, null, AgentStatus.Waiting);
        registry.Register(session);

        var transition = registry.Transition(session.Id, status);

        Assert.True(transition.Changed);
        Assert.Equal(status, registry.Get(session.Id).Status);
        Assert.Equal(status, transition.Event!.NewStatus);
    }
}
