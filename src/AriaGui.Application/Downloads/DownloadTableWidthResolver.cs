namespace AriaGui.Application.Downloads;

public enum DownloadTableSizingMode { Fit, Manual }

public sealed record DownloadTableWidths(IReadOnlyList<DownloadColumnLayout> Columns, double CanvasWidth);

/// <summary>Resolves display widths without changing persisted preferred widths.</summary>
public static class DownloadTableWidthResolver
{
    public static double MinimumWidth(DownloadSortColumn column) => column switch
    {
        DownloadSortColumn.Name => 180,
        DownloadSortColumn.Size => 100,
        DownloadSortColumn.Progress => 120,
        DownloadSortColumn.Status => 110,
        DownloadSortColumn.Speed or DownloadSortColumn.Eta => 90,
        _ => 60
    };

    public static DownloadTableWidths Resolve(IReadOnlyList<DownloadColumnLayout> columns,
        double viewportWidth, double chromeWidth = 0, DownloadTableSizingMode mode = DownloadTableSizingMode.Fit)
    {
        ArgumentNullException.ThrowIfNull(columns);
        if (!double.IsFinite(viewportWidth) || viewportWidth < 0)
            throw new ArgumentOutOfRangeException(nameof(viewportWidth));
        if (!double.IsFinite(chromeWidth) || chromeWidth < 0)
            throw new ArgumentOutOfRangeException(nameof(chromeWidth));
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (columns.Any(c => c is null || !double.IsFinite(c.Width) || c.Width < 60))
            throw new ArgumentException("Column widths must be finite and at least 60.", nameof(columns));

        var visible = columns.Where(c => c.Visible).ToArray();
        if (mode == DownloadTableSizingMode.Fit && visible.Length > 0)
        {
            var available = Math.Max(0, viewportWidth - chromeWidth);
            var minima = visible.Select(c => MinimumWidth(c.Column)).ToArray();
            var preferred = visible.Select((c, i) => Math.Max(c.Width, minima[i])).ToArray();
            var total = preferred.Sum();
            if (available < total)
            {
                var minimumTotal = minima.Sum();
                var fraction = total > minimumTotal
                    ? Math.Clamp((available - minimumTotal) / (total - minimumTotal), 0, 1) : 0;
                for (var i = 0; i < visible.Length; i++)
                    visible[i] = visible[i] with { Width = minima[i] + (preferred[i] - minima[i]) * fraction };
            }
            else
            {
                for (var i = 0; i < visible.Length; i++)
                    visible[i] = visible[i] with { Width = preferred[i] };
                var nameIndex = Array.FindIndex(visible, c => c.Column == DownloadSortColumn.Name);
                if (nameIndex >= 0)
                    visible[nameIndex] = visible[nameIndex] with { Width = visible[nameIndex].Width + available - total };
            }
        }
        return new(Array.AsReadOnly(visible), Math.Max(viewportWidth, visible.Sum(c => c.Width) + chromeWidth));
    }
}
