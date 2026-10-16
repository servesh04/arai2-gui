using AriaGui.Infrastructure.Downloads;

namespace AriaGui.Infrastructure.Tests;

public sealed class GatewayByteRangeTests
{
    [Theory]
    [InlineData(null, 0, false, true)]
    [InlineData("bytes=0-", 0, true, true)]
    [InlineData("bytes=42-", 42, true, true)]
    [InlineData("bytes=0-99", 0, true, false)]
    [InlineData("bytes=42-99", 42, true, false)]
    public void Parses_exact_request_shape_without_dropping_end(string? header, long start, bool isRange, bool sequential)
    {
        Assert.True(GatewayByteRange.TryParse(header, out var request));
        Assert.Equal(start, request.Start);
        Assert.Equal(isRange, request.IsRange);
        Assert.Equal(sequential, request.IsSupportedBySequentialRoute);
    }

    [Theory]
    [InlineData("bytes=-50")]
    [InlineData("bytes=0-9,20-29")]
    [InlineData("bytes=20-10")]
    [InlineData("items=0-9")]
    [InlineData("bytes=9223372036854775808-")]
    public void Rejects_unsupported_or_malformed_intervals(string header) =>
        Assert.False(GatewayByteRange.TryParse(header, out _));
}
