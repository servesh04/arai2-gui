using AriaGui.Application.Downloads;

namespace AriaGui.Application.Tests;

public sealed class DownloadTableWidthResolverTests
{
    private static readonly DownloadColumnLayout[] Columns =
    [new(DownloadSortColumn.Name, 280), new(DownloadSortColumn.Size, 140),
     new(DownloadSortColumn.Progress, 150), new(DownloadSortColumn.Status, 140),
     new(DownloadSortColumn.Speed, 110), new(DownloadSortColumn.Eta, 130)];

    [Fact]
    public void FitSurplusGoesToNameAndAccountsForChrome()
    {
        var result = DownloadTableWidthResolver.Resolve(Columns, 1200, 50);
        Assert.Equal(480, result.Columns[0].Width);
        Assert.Equal(Columns.Skip(1), result.Columns.Skip(1));
        Assert.Equal(1200, result.CanvasWidth);
        Assert.Equal(280, Columns[0].Width);
    }

    [Fact]
    public void FitShrinksProportionallyTowardSemanticMinima()
    {
        var result = DownloadTableWidthResolver.Resolve(Columns, 820, 0);
        Assert.Equal(new double[] { 230, 120, 135, 125, 100, 110 }, result.Columns.Select(c => c.Width));
        Assert.Equal(820, result.CanvasWidth);
    }

    [Fact]
    public void NarrowViewportRetainsMinimaAndOverflows()
    {
        var result = DownloadTableWidthResolver.Resolve(Columns, 400, 50);
        Assert.Equal(new double[] { 180, 100, 120, 110, 90, 90 }, result.Columns.Select(c => c.Width));
        Assert.Equal(740, result.CanvasWidth);
    }

    [Theory]
    [InlineData(400, 1000)]
    [InlineData(1400, 1400)]
    public void ManualPreservesPreferredWidthsAndTrailingWorkspace(double viewport, double canvas)
    {
        var result = DownloadTableWidthResolver.Resolve(Columns, viewport, 50, DownloadTableSizingMode.Manual);
        Assert.Equal(Columns, result.Columns);
        Assert.Equal(canvas, result.CanvasWidth);
    }

    [Fact]
    public void HiddenColumnsDoNotConsumeSpaceAndOrderIsPreserved()
    {
        DownloadColumnLayout[] columns = [Columns[2], Columns[1] with { Visible = false }, Columns[0]];
        var result = DownloadTableWidthResolver.Resolve(columns, 600);
        Assert.Equal(new[] { DownloadSortColumn.Progress, DownloadSortColumn.Name }, result.Columns.Select(c => c.Column));
        Assert.Equal(new double[] { 150, 450 }, result.Columns.Select(c => c.Width));
    }

    [Fact]
    public void FitUsesMinimaWhenSavedPreferencesAreSmallerWhileManualKeepsPreferences()
    {
        DownloadColumnLayout[] columns = [new(DownloadSortColumn.Name, 60)];
        Assert.Equal(180, DownloadTableWidthResolver.Resolve(columns, 100).Columns[0].Width);
        Assert.Equal(60, DownloadTableWidthResolver.Resolve(columns, 100, mode: DownloadTableSizingMode.Manual).Columns[0].Width);
    }

    [Fact]
    public void EmptyLayoutRetainsViewportAndInvalidMeasurementsAreRejected()
    {
        Assert.Equal(400, DownloadTableWidthResolver.Resolve([], 400).CanvasWidth);
        Assert.Throws<ArgumentOutOfRangeException>(() => DownloadTableWidthResolver.Resolve(Columns, double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => DownloadTableWidthResolver.Resolve(Columns, 400, -1));
    }
}

