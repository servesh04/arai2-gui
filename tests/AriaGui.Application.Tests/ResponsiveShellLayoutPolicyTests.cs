using AriaGui.Application.Downloads;

namespace AriaGui.Application.Tests;

public sealed class ResponsiveShellLayoutPolicyTests
{
    [Theory]
    [InlineData(799, ShellLayoutMode.Compact)]
    [InlineData(800, ShellLayoutMode.Regular)]
    [InlineData(801, ShellLayoutMode.Regular)]
    [InlineData(1099, ShellLayoutMode.Regular)]
    [InlineData(1100, ShellLayoutMode.Wide)]
    [InlineData(1101, ShellLayoutMode.Wide)]
    public void ClientDipBoundariesAreInclusive(double width, ShellLayoutMode expected) =>
        Assert.Equal(expected, ResponsiveShellLayoutPolicy.Resolve(width));

    [Fact]
    public void LargeTextCanRequireBothGroupsToReflowAndRecoversOnEnlarge()
    {
        var widths = new double[] { 1400, 1200, 900, 560, 900, 1200, 1400 };
        var modes = widths.Select(w => ResponsiveShellLayoutPolicy.Resolve(w, 1300, 950));
        Assert.Equal(new[] { ShellLayoutMode.Wide, ShellLayoutMode.Regular, ShellLayoutMode.Compact,
            ShellLayoutMode.Compact, ShellLayoutMode.Compact, ShellLayoutMode.Regular, ShellLayoutMode.Wide }, modes);
    }

    [Fact]
    public void SmallMeasuredContentDoesNotBypassSupportedBreakpoints()
    {
        Assert.Equal(ShellLayoutMode.Compact, ResponsiveShellLayoutPolicy.Resolve(799, 300, 200));
        Assert.Equal(ShellLayoutMode.Regular, ResponsiveShellLayoutPolicy.Resolve(1099, 300, 200));
    }

    [Theory]
    [InlineData(240, TitleSubtitleMode.Full)]
    [InlineData(239, TitleSubtitleMode.Short)]
    [InlineData(100, TitleSubtitleMode.Short)]
    [InlineData(99, TitleSubtitleMode.Hidden)]
    public void SubtitleOnlyUsesAnArrangementThatFits(double width, TitleSubtitleMode expected) =>
        Assert.Equal(expected, ResponsiveShellLayoutPolicy.ResolveTitleSubtitle(width, 240, 100));

    [Fact]
    public void UnmeasuredOrInvalidClientWidthUsesSafeCompactState()
    {
        foreach (var width in new[] { double.NaN, double.PositiveInfinity, -1d, 0d })
            Assert.Equal(ShellLayoutMode.Compact, ResponsiveShellLayoutPolicy.Resolve(width));
    }
}
