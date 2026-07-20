namespace Halfway.Core;

public static class PanelSizing
{
    public const double DefaultSidebarWidth = 236;
    public const double DefaultDetailWidth = 420;
    public const double MinimumSidebarWidth = 160;
    public const double MaximumSidebarWidth = 420;
    public const double MinimumPrimaryWidth = 320;
    public const double MinimumDetailWidth = 280;

    public static PanelWidths Resize(
        double leadingWidth,
        double trailingWidth,
        double delta,
        double minimumLeadingWidth,
        double maximumLeadingWidth,
        double minimumTrailingWidth)
    {
        var total = Math.Max(0, leadingWidth) + Math.Max(0, trailingWidth);
        var maximum = Math.Min(maximumLeadingWidth, Math.Max(0, total - minimumTrailingWidth));
        var minimum = Math.Min(Math.Max(0, minimumLeadingWidth), maximum);
        var leading = Math.Clamp(leadingWidth + delta, minimum, maximum);
        return new PanelWidths(leading, total - leading);
    }

    public static WorkspacePanelWidths CalculateWorkspace(
        double availableWidth,
        double preferredSidebarWidth,
        double preferredDetailWidth)
    {
        var minimumTotal = MinimumSidebarWidth + MinimumPrimaryWidth + MinimumDetailWidth;
        var allocatedWidth = Math.Max(Sanitize(availableWidth), minimumTotal);
        var sidebar = Math.Clamp(Sanitize(preferredSidebarWidth, DefaultSidebarWidth), MinimumSidebarWidth, MaximumSidebarWidth);
        var detail = Math.Max(MinimumDetailWidth, Sanitize(preferredDetailWidth, DefaultDetailWidth));
        var primary = allocatedWidth - sidebar - detail;

        if (primary < MinimumPrimaryWidth)
        {
            var deficit = MinimumPrimaryWidth - primary;
            var detailReduction = Math.Min(deficit, detail - MinimumDetailWidth);
            detail -= detailReduction;
            deficit -= detailReduction;
            sidebar -= Math.Min(deficit, sidebar - MinimumSidebarWidth);
            primary = allocatedWidth - sidebar - detail;
        }

        return new WorkspacePanelWidths(sidebar, primary, detail);
    }

    private static double Sanitize(double value, double fallback = 0) =>
        double.IsFinite(value) && value >= 0 ? value : fallback;
}

public readonly record struct PanelWidths(double Leading, double Trailing);
public readonly record struct WorkspacePanelWidths(double Sidebar, double Primary, double Detail);
