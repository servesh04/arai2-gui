namespace AriaGui.Application.Downloads;

public enum ShellLayoutMode { Compact, Regular, Wide }
public enum TitleSubtitleMode { Hidden, Short, Full }

/// <summary>Logical-pixel shell breakpoints, with measured content allowed to require more space.</summary>
public static class ResponsiveShellLayoutPolicy
{
    public static ShellLayoutMode Resolve(double clientWidth, double wideRequiredWidth = 1100,
        double regularRequiredWidth = 800)
    {
        var width = Nonnegative(clientWidth);
        if (width >= Math.Max(1100, Nonnegative(wideRequiredWidth))) return ShellLayoutMode.Wide;
        if (width >= Math.Max(800, Nonnegative(regularRequiredWidth))) return ShellLayoutMode.Regular;
        return ShellLayoutMode.Compact;
    }

    public static TitleSubtitleMode ResolveTitleSubtitle(double availableWidth, double fullRequiredWidth,
        double shortRequiredWidth)
    {
        var width = Nonnegative(availableWidth);
        if (width >= Nonnegative(fullRequiredWidth)) return TitleSubtitleMode.Full;
        if (width >= Nonnegative(shortRequiredWidth)) return TitleSubtitleMode.Short;
        return TitleSubtitleMode.Hidden;
    }

    private static double Nonnegative(double value) => double.IsFinite(value) ? Math.Max(0, value) : 0;
}
