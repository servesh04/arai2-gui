using AriaGui.Application.Downloads;

namespace AriaGui.Application.Tests;

public sealed class ResponsivePaneLayoutPolicyTests
{
    [Theory]
    [InlineData(266, 23, false)]
    [InlineData(266, 55, true)]
    [InlineData(462, 23, true)]
    [InlineData(462, 55, true)]
    public void Supported_short_budgets_reserve_the_table_before_details_and_notices(double body, double history, bool notices)
    {
        var result = ResponsivePaneLayoutPolicy.Resolve(body, history, 560, 600, 400, true, true, notices, false);
        var table = body - history - result.NoticeHeight - result.DetailsHeight - (result.DetailsHeight > 0 ? 6 : 0);
        Assert.True(table >= 180, $"Table height {table}");
        Assert.Equal(0, result.SidebarWidth);
        Assert.InRange(result.NoticeHeight, 0, 120);
    }

    [Fact]
    public void Temporary_collapse_and_sidebar_clamp_restore_preferred_values_when_space_returns()
    {
        var preferred = new DesktopLayout(SidebarWidth: 360, DetailsHeight: 420);
        var small = ResponsivePaneLayoutPolicy.Resolve(260, 50, 800, preferred.DetailsHeight, preferred.SidebarWidth, true, true, true, false);
        Assert.Equal(0, small.DetailsHeight);
        var large = ResponsivePaneLayoutPolicy.Resolve(850, 30, 1500, preferred.DetailsHeight, preferred.SidebarWidth, true, true, false, true);
        Assert.Equal(420, large.DetailsHeight);
        Assert.Equal(360, large.SidebarWidth);
        Assert.Equal(420, preferred.DetailsHeight);
    }

    [Fact]
    public void Empty_inspector_is_a_short_hint_without_replacing_expanded_preference()
    {
        var result = ResponsivePaneLayoutPolicy.Resolve(600, 30, 1000, 420, 160, true, false, false, true);
        Assert.Equal(64, result.DetailsHeight);
    }
}
