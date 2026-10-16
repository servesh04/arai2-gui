using System.Net.Http.Headers;

namespace AriaGui.Infrastructure.Downloads;

// The v1 route supports a full response or an open-ended resume only. Keep the
// requested end explicit so a bounded aria2 range cannot silently become EOF.
internal readonly record struct GatewayByteRange(long Start, long? End, bool IsRange)
{
    public static bool TryParse(string? header, out GatewayByteRange requested)
    {
        requested = default;
        if (header is null) return true;
        if (!RangeHeaderValue.TryParse(header, out var parsed) || parsed.Unit != "bytes" || parsed.Ranges.Count != 1)
            return false;
        var interval = parsed.Ranges.Single();
        if (interval.From is not long start || start < 0 || interval.To is long end && end < start)
            return false;
        requested = new(start, interval.To, true);
        return true;
    }

    public bool IsSupportedBySequentialRoute => End is null;
}
