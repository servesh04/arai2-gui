using AriaGui.Application.Downloads;

namespace AriaGui.Application.Tests;

public sealed class RestoredWindowPlacementTests
{
    [Fact]
    public void Valid_user_placement_is_preserved()
    {
        var window = new WindowRectangle(100, 80, 800, 600);
        Assert.Equal(window, RestoredWindowPlacement.Clamp(window, new(0, 40, 1920, 1040)));
    }

    [Theory]
    [InlineData(2200, 1400, 0, 40, 1920, 1040, 1120, 480)]
    [InlineData(-3000, -700, -1920, -200, 1920, 1040, -1920, -200)]
    [InlineData(-100, -100, 0, 40, 1920, 1040, 0, 40)]
    public void Reopen_keeps_all_edges_inside_the_selected_work_area(int x, int y, int wx, int wy, int ww, int wh, int expectedX, int expectedY)
    {
        Assert.Equal(new(expectedX, expectedY, 800, 600), RestoredWindowPlacement.Clamp(new(x, y, 800, 600), new(wx, wy, ww, wh)));
    }

    [Fact]
    public void A_smaller_monitor_overrides_preferred_size_without_losing_the_caption()
    {
        Assert.Equal(new(0, 20, 480, 300), RestoredWindowPlacement.Clamp(new(900, 700, 1220, 960), new(0, 20, 480, 300)));
    }

    [Fact]
    public void Integer_edge_monitor_coordinates_do_not_overflow()
    {
        var work = new WindowRectangle(int.MaxValue - 100, int.MinValue, 500, 400);
        Assert.Equal(new(int.MaxValue - 100, int.MinValue, 500, 400), RestoredWindowPlacement.Clamp(new(0, 0, 1000, 1000), work));
    }

    [Fact]
    public void Invalid_work_areas_and_dimensions_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RestoredWindowPlacement.Clamp(new(0, 0, 0, 600), new(0, 0, 1920, 1080)));
        Assert.Throws<ArgumentOutOfRangeException>(() => RestoredWindowPlacement.Clamp(new(0, 0, 800, 600), new(0, 0, 0, 1080)));
    }
}
