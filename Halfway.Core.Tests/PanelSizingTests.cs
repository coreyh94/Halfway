using Halfway.Core;
using Xunit;

namespace Halfway.Core.Tests;

public sealed class PanelSizingTests
{
    [Fact]
    public void ResizeMovesBoundaryAndPreservesAvailableWidth()
    {
        var result = PanelSizing.Resize(236, 700, 40, 160, 420, 320);

        Assert.Equal(276, result.Leading);
        Assert.Equal(660, result.Trailing);
        Assert.Equal(936, result.Leading + result.Trailing);
    }

    [Fact]
    public void ResizeClampsLeadingPanelToConfiguredBounds()
    {
        Assert.Equal(new PanelWidths(160, 776), PanelSizing.Resize(236, 700, -500, 160, 420, 320));
        Assert.Equal(new PanelWidths(420, 516), PanelSizing.Resize(236, 700, 500, 160, 420, 320));
    }

    [Fact]
    public void ResizeAlwaysPreservesMinimumTrailingWidth()
    {
        Assert.Equal(new PanelWidths(300, 320), PanelSizing.Resize(300, 320, 100, 160, 500, 320));
    }

    [Fact]
    public void NarrowAvailableWidthCollapsesToSafeReachableBoundary()
    {
        Assert.Equal(new PanelWidths(100, 320), PanelSizing.Resize(160, 260, 0, 160, 420, 320));
    }
}
