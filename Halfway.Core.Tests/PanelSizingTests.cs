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

    [Fact]
    public void WorkspaceWidthExactlyAtMinimumSumUsesEveryDocumentedMinimum()
    {
        Assert.Equal(new WorkspacePanelWidths(160, 320, 280), PanelSizing.CalculateWorkspace(760, 236, 420));
    }

    [Theory]
    [InlineData(975, 236, 320, 419)]
    [InlineData(900, 236, 320, 344)]
    [InlineData(760, 160, 320, 280)]
    public void NarrowingReducesPreferredWidthsWithoutCrossingMinimums(double width, double sidebar, double primary, double detail)
    {
        Assert.Equal(new WorkspacePanelWidths(sidebar, primary, detail), PanelSizing.CalculateWorkspace(width, 236, 420));
    }

    [Fact]
    public void WidthBelowMinimumSumClampsToSafeMinimumLayout()
    {
        var result = PanelSizing.CalculateWorkspace(500, 236, 420);

        Assert.Equal(new WorkspacePanelWidths(160, 320, 280), result);
        Assert.All(new[] { result.Sidebar, result.Primary, result.Detail }, value => Assert.True(double.IsFinite(value) && value >= 0));
    }

    [Fact]
    public void ExpansionAfterClampRestoresPreferredSidebarAndDetailWidths()
    {
        _ = PanelSizing.CalculateWorkspace(760, 300, 380);

        Assert.Equal(new WorkspacePanelWidths(300, 520, 380), PanelSizing.CalculateWorkspace(1200, 300, 380));
    }

    [Fact]
    public void SplitterPreferenceAndDefaultResetRemainStableAcrossResize()
    {
        var afterDrag = PanelSizing.CalculateWorkspace(1000, 300, 350);
        var afterResize = PanelSizing.CalculateWorkspace(900, 300, 350);
        var afterReset = PanelSizing.CalculateWorkspace(1000, PanelSizing.DefaultSidebarWidth, PanelSizing.DefaultDetailWidth);

        Assert.Equal(new WorkspacePanelWidths(300, 350, 350), afterDrag);
        Assert.Equal(new WorkspacePanelWidths(300, 320, 280), afterResize);
        Assert.Equal(new WorkspacePanelWidths(236, 344, 420), afterReset);
        Assert.Equal(afterResize, PanelSizing.CalculateWorkspace(900, 300, 350));
    }

    [Fact]
    public void InvalidDimensionsCannotProduceInvalidPanelWidths()
    {
        foreach (var width in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -1 })
        {
            var result = PanelSizing.CalculateWorkspace(width, double.NaN, double.PositiveInfinity);
            Assert.All(new[] { result.Sidebar, result.Primary, result.Detail }, value => Assert.True(double.IsFinite(value) && value >= 0));
        }
    }
}
