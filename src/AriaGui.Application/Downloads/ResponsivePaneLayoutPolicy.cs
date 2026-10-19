namespace AriaGui.Application.Downloads;

public sealed record EffectivePaneLayout(double NoticeHeight, double DetailsHeight, double SidebarWidth);

/// <summary>Allocates presentation space without modifying preferred dimensions.</summary>
public static class ResponsivePaneLayoutPolicy
{
    public const double MinimumTableHeight = 180;
    public const double MinimumBottomDockTableHeight = 80;
    public static EffectivePaneLayout Resolve(double bodyHeight, double historyHeight, double workspaceWidth,
        double preferredDetailsHeight, double preferredSidebarWidth, bool detailsVisible, bool selected,
        bool noticesVisible, bool sidebarDocked, double minimumTableHeight = MinimumTableHeight)
    {
        // Reserve the workspace border and fractional physical-pixel rounding too.
        var available = Math.Max(0, bodyHeight - historyHeight - minimumTableHeight - 2);
        var notice = noticesVisible ? Math.Min(120, available) : 0;
        var remaining = Math.Max(0, available - notice - 6);
        var details = !detailsVisible ? 0 : selected
            ? remaining >= 120 ? Math.Min(preferredDetailsHeight, remaining) : 0
            : remaining >= 56 ? Math.Min(64, remaining) : 0;
        var sidebar = sidebarDocked ? Math.Min(preferredSidebarWidth, Math.Max(120, workspaceWidth - 420)) : 0;
        return new(notice, details, sidebar);
    }
}
