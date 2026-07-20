using Halfway.Core;
using Xunit;

namespace Halfway.Core.Tests;

public sealed class FailureNotificationPolicyTests
{
    private readonly Guid _sessionId = Guid.NewGuid();

    [Fact]
    public void BackgroundFailureCreatesOneDeterministicNotification()
    {
        var policy = new FailureNotificationPolicy(); var transition = Transition(AgentStatus.Running, AgentStatus.Failed);

        var notification = policy.Evaluate(transition, true, Guid.NewGuid());

        Assert.NotNull(notification);
        Assert.Equal("Halfway session failed", notification.Title);
        Assert.Equal("Runtime failed. Return to Halfway to review the terminal.", notification.Message);
        Assert.Null(policy.Evaluate(transition, true, Guid.NewGuid()));
    }

    [Fact]
    public void InactiveWindowFailureNotifiesEvenForFocusedSession()
    {
        Assert.NotNull(new FailureNotificationPolicy().Evaluate(Transition(AgentStatus.Waiting, AgentStatus.Failed), false, _sessionId));
    }

    [Fact]
    public void VisibleFocusedFailureDoesNotNotify()
    {
        Assert.Null(new FailureNotificationPolicy().Evaluate(Transition(AgentStatus.Running, AgentStatus.Failed), true, _sessionId));
    }

    [Fact]
    public void RestoredFailedMetadataWithoutLifecycleEventDoesNotNotify()
    {
        var restored = new LifecycleTransition(new AgentSession(_sessionId, "Runtime", AgentKind.SubAgent, Guid.NewGuid(), AgentStatus.Failed));

        Assert.Null(new FailureNotificationPolicy().Evaluate(restored, false, null));
    }

    [Theory]
    [InlineData(AgentStatus.Completed)]
    [InlineData(AgentStatus.Disconnected)]
    [InlineData(AgentStatus.Waiting)]
    public void NonFailureTransitionDoesNotNotify(AgentStatus status)
    {
        Assert.Null(new FailureNotificationPolicy().Evaluate(Transition(AgentStatus.Running, status), false, null));
    }

    private LifecycleTransition Transition(AgentStatus previous, AgentStatus current)
    {
        var item = new LifecycleEvent(Guid.NewGuid(), _sessionId, Guid.NewGuid(), previous, current, DateTimeOffset.UtcNow, false);
        return new LifecycleTransition(new AgentSession(_sessionId, "Runtime", AgentKind.SubAgent, item.ParentSessionId, current), item);
    }
}
