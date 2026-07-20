namespace Halfway.Core;

public static class PanelSizing
{
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
}

public readonly record struct PanelWidths(double Leading, double Trailing);
