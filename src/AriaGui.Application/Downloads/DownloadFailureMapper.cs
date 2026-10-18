using System.Text.RegularExpressions;

namespace AriaGui.Application.Downloads;

public sealed record DownloadFailureInfo(string Reason, int? HttpStatus);

public static partial class DownloadFailureMapper
{
    public static DownloadFailureInfo Map(string? errorCode, string? engineMessage)
    {
        var code = int.TryParse(errorCode, out var parsed) ? parsed : 0;
        var http = ExtractHttpStatus(engineMessage);
        if (http is not null) return new(HttpReason(http.Value, engineMessage), http);
        return new(code switch
        {
            2 => "Download timed out",
            3 => "File not found",
            1 or 5 or 6 or 19 or 29 => "Network problem",
            7 or 8 => "Resume is not supported by the server",
            9 or 14 or 15 or 16 or 17 or 18 => "Disk full or destination write error",
            11 or 12 or 13 => "File already exists",
            24 => "Authentication required",
            31 or 32 => "Checksum mismatch",
            _ => $"Download failed (code {code})"
        }, null);
    }

    private static int? ExtractHttpStatus(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;
        var match = HttpStatusPattern().Match(message);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var value)) return null;
        return value is 401 or 403 or 404 or 407 or >= 500 and <= 599 ? value : null;
    }

    private static string HttpReason(int status, string? message) => status switch
    {
        401 => "HTTP 401 - authentication required",
        403 when message?.Contains("expired", StringComparison.OrdinalIgnoreCase) == true => "HTTP 403 - link expired",
        403 => "HTTP 403 - access denied",
        404 => "HTTP 404 - file not found",
        407 => "HTTP 407 - proxy authentication required",
        _ => $"HTTP {status} - server error"
    };

    [GeneratedRegex(@"(?:HTTP(?:/\d(?:\.\d)?)?\s*|status(?:\s+code)?\s*[:=]?\s*)(401|403|404|407|5\d\d)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HttpStatusPattern();
}
