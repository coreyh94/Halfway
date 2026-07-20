using Halfway.Core;
using Xunit;

namespace Halfway.Core.Tests;

public sealed class SessionAttentionTrackerTests
{
    [Fact]
    public void BackgroundActivityMarksSessionUnreadOnlyOnce()
    {
        var tracker = new SessionAttentionTracker(); var focused = Guid.NewGuid(); var background = Guid.NewGuid();
        tracker.Focus(focused);

        Assert.True(tracker.RecordActivity(background));
        Assert.False(tracker.RecordActivity(background));
        Assert.True(tracker.IsUnread(background));
    }

    [Fact]
    public void FocusedActivityNeverMarksSessionUnread()
    {
        var tracker = new SessionAttentionTracker(); var session = Guid.NewGuid(); tracker.Focus(session);

        Assert.False(tracker.RecordActivity(session));
        Assert.False(tracker.IsUnread(session));
    }

    [Fact]
    public void FocusingSessionClearsOnlyItsUnreadState()
    {
        var tracker = new SessionAttentionTracker(); var first = Guid.NewGuid(); var second = Guid.NewGuid();
        tracker.RecordActivity(first); tracker.RecordActivity(second);

        Assert.True(tracker.Focus(first));
        Assert.False(tracker.IsUnread(first));
        Assert.True(tracker.IsUnread(second));
        Assert.False(tracker.Focus(first));
    }
}
