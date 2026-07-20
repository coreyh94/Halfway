using Halfway.Core;
using Xunit;

namespace Halfway.Core.Tests;

public sealed class StatusPresentationTests
{
    [Theory]
    [InlineData(AgentStatus.Running, "●")]
    [InlineData(AgentStatus.Waiting, "◐")]
    [InlineData(AgentStatus.Completed, "✓")]
    [InlineData(AgentStatus.Failed, "!")]
    [InlineData(AgentStatus.Disconnected, "!")]
    [InlineData(AgentStatus.Queued, "○")]
    public void Glyphs_are_centralized(AgentStatus status, string glyph) => Assert.Equal(glyph, StatusPresentation.Glyph(status));

    [Theory]
    [InlineData(AgentStatus.Running, "RunningBrush")]
    [InlineData(AgentStatus.Waiting, "WaitingBrush")]
    [InlineData(AgentStatus.Completed, "CompletedBrush")]
    [InlineData(AgentStatus.Failed, "ErrorBrush")]
    [InlineData(AgentStatus.Disconnected, "MutedTextBrush")]
    [InlineData(AgentStatus.Queued, "MutedTextBrush")]
    public void Color_keys_are_centralized(AgentStatus status, string brushKey) => Assert.Equal(brushKey, StatusPresentation.ColorKey(status));
}
