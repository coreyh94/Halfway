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
}
