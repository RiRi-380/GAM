using System;

namespace GmodAddonManager.UI.Services;

public enum ResponsiveLayoutKind
{
    Wide,
    Compact,
    Narrow
}

public sealed record ResponsiveLayoutState(
    ResponsiveLayoutKind Kind,
    bool UseOverlayPanes,
    double AssetPaneWidth,
    double FilterPaneWidth,
    double DetailsPaneWidth);

public static class ResponsiveLayoutPolicy
{
    public const double WideBreakpoint = 1200;
    public const double NarrowBreakpoint = 760;

    private const double WideAssetPaneWidth = 340;
    private const double CompactAssetPaneWidth = 340;
    private const double NarrowAssetPaneWidth = 320;
    private const double FilterPaneWidth = 250;
    private const double MaximumDetailsPaneWidth = 400;
    private const double NarrowViewportGutter = 32;

    public static ResponsiveLayoutState Resolve(double viewportWidth)
    {
        if (!double.IsFinite(viewportWidth) || viewportWidth <= 0)
        {
            viewportWidth = NarrowBreakpoint;
        }

        var narrowAvailableWidth = Math.Max(0, viewportWidth - NarrowViewportGutter);
        var detailsWidth = Math.Min(
            MaximumDetailsPaneWidth,
            narrowAvailableWidth);

        if (viewportWidth >= WideBreakpoint)
        {
            return new ResponsiveLayoutState(
                ResponsiveLayoutKind.Wide,
                UseOverlayPanes: false,
                WideAssetPaneWidth,
                FilterPaneWidth,
                detailsWidth);
        }

        if (viewportWidth >= NarrowBreakpoint)
        {
            return new ResponsiveLayoutState(
                ResponsiveLayoutKind.Compact,
                UseOverlayPanes: true,
                CompactAssetPaneWidth,
                FilterPaneWidth,
                detailsWidth);
        }

        return new ResponsiveLayoutState(
            ResponsiveLayoutKind.Narrow,
            UseOverlayPanes: true,
            Math.Min(NarrowAssetPaneWidth, narrowAvailableWidth),
            Math.Min(FilterPaneWidth, narrowAvailableWidth),
            detailsWidth);
    }
}
