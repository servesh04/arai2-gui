namespace AriaGui.Application.Downloads;

/// <summary>Outer window bounds and work area in physical screen pixels.</summary>
public readonly record struct WindowRectangle(int X, int Y, int Width, int Height);

public static class RestoredWindowPlacement
{
    public static WindowRectangle Clamp(WindowRectangle window, WindowRectangle workArea)
    {
        if (window.Width <= 0 || window.Height <= 0) throw new ArgumentOutOfRangeException(nameof(window));
        if (workArea.Width <= 0 || workArea.Height <= 0) throw new ArgumentOutOfRangeException(nameof(workArea));
        var width = Math.Min(window.Width, workArea.Width);
        var height = Math.Min(window.Height, workArea.Height);
        // Use long endpoints: negative monitor origins and integer-edge inputs are valid.
        var x = (int)Math.Clamp((long)window.X, workArea.X, (long)workArea.X + workArea.Width - width);
        var y = (int)Math.Clamp((long)window.Y, workArea.Y, (long)workArea.Y + workArea.Height - height);
        return new(x, y, width, height);
    }
}
