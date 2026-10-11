using AriaGui.Application.Downloads;

namespace AriaGui.Application.Tests;

public sealed class SavedColumnWidthTests
{
    [Fact]
    public void Old_three_extreme_columns_cannot_recreate_the_3212_DIP_manual_canvas()
    {
        var old = new DesktopLayout(Columns: new DesktopLayout().EffectiveColumns.Select((c, i) =>
            c with { Width = i < 3 ? 1000 : 60 }).ToArray(), TableSizingMode: DownloadTableSizingMode.Manual);
        var loaded = old.NormalizeSavedColumnWidths();
        loaded.Validate();
        var table = DownloadTableWidthResolver.Resolve(loaded.EffectiveColumns, 560, 32, loaded.TableSizingMode);
        Assert.Equal(2012, table.CanvasWidth); // 3×600 + 3×60 + selection gutter.
        Assert.All(loaded.EffectiveColumns, column => Assert.InRange(column.Width, 60, 600));
    }

    [Fact]
    public void Legitimate_manual_widths_and_hidden_column_preferences_are_preserved()
    {
        var columns = new DesktopLayout().EffectiveColumns.Select((c, i) => c with { Width = 100 + i * 100,
            Visible = c.Column != DownloadSortColumn.Size }).ToArray();
        var layout = new DesktopLayout(Columns: columns, TableSizingMode: DownloadTableSizingMode.Manual);
        Assert.Equal(columns, layout.NormalizeSavedColumnWidths().EffectiveColumns);
        Assert.Equal(DownloadTableSizingMode.Manual, layout.NormalizeSavedColumnWidths().TableSizingMode);
        Assert.Equal(new DesktopLayout(), new DesktopLayout().NormalizeSavedColumnWidths());
    }
}
