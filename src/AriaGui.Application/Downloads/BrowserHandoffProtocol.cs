using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using AriaGui.Core.Downloads;

namespace AriaGui.Application.Downloads;

public sealed class BrowserHandoffRequest
{
    public int Version { get; }
    public Uri Url { get; }
    public Uri? Referrer { get; }
    public string? UserAgent { get; }
    public string? Cookies { get; }
    public string? Filename { get; }
    public string? Mime { get; }
    public Uri? TabSite { get; }
    public long? SizeBytes { get; }
    public string? Method { get; }
    public string? Trigger { get; }

    internal BrowserHandoffRequest(int version, Uri url, Uri? referrer, string? userAgent, string? cookies,
        string? filename, string? mime, Uri? tabSite, long? sizeBytes, string? method, string? trigger)
    {
        Version = version; Url = url; Referrer = referrer; UserAgent = userAgent; Cookies = cookies;
        Filename = filename; Mime = mime; TabSite = tabSite; SizeBytes = sizeBytes; Method = method; Trigger = trigger;
    }

    public override string ToString() => "BrowserHandoffRequest (sensitive fields redacted)";
}

public sealed record BrowserHandoffSettings(bool Enabled, bool AutoCapture, bool AltBypass, long MinimumSizeBytes,
    IReadOnlyList<string> CaptureFileTypes, IReadOnlyList<string> ExcludedSites);
public sealed record BrowserHandoffResponse(bool Ok, Guid? Id = null, string? Error = null, string? Reason = null,
    BrowserHandoffSettings? Settings = null)
{
    public byte[] ToJson() => JsonSerializer.SerializeToUtf8Bytes(new
    {
        ok = Ok, id = Id?.ToString("D"), error = Error, reason = Reason, settings = Settings
    }, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
}

public static class BrowserHandoffProtocol
{
    public const int Version = 1;
    public const int MaximumPipeBytes = 64 * 1024;
    public static string PipeName(string userSid) => "aria-gui-handoff-v1-" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(userSid)))[..24].ToLowerInvariant();
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly HashSet<string> Fields = ["version", "url", "referrer", "userAgent", "cookies",
        "filename", "mime", "tabSite", "sizeBytes", "method", "trigger"];

    public static bool IsHello(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is < 1 or > MaximumPipeBytes) return false;
        try
        {
            _ = StrictUtf8.GetCharCount(bytes);
            using var document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 2 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 2
                || !root.TryGetProperty("version", out var version) || !version.TryGetInt32(out var number) || number != Version
                || !root.TryGetProperty("action", out var action) || action.GetString() != "hello") return false;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or DecoderFallbackException or InvalidOperationException)
        { return false; }
    }

    public static bool TryParse(ReadOnlySpan<byte> bytes, out BrowserHandoffRequest? request)
    {
        request = null;
        if (bytes.Length is < 1 or > MaximumPipeBytes) return false;
        try
        {
            _ = StrictUtf8.GetCharCount(bytes);
            using var document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in root.EnumerateObject())
                if (!Fields.Contains(field.Name) || !seen.Add(field.Name)) return false;
            if (!root.TryGetProperty("version", out var version) || !version.TryGetInt32(out var number) || number != Version
                || !root.TryGetProperty("url", out var urlField) || !TryHttpUri(urlField, 16384, out var url)) return false;
            if (!OptionalUri(root, "referrer", 16384, out var referrer)
                || !OptionalUri(root, "tabSite", 2048, out var tabSite)
                || !OptionalText(root, "userAgent", 512, out var userAgent)
                || !OptionalText(root, "cookies", 8192, out var cookies)
                || !OptionalText(root, "filename", 240, out var filename)
                || !OptionalText(root, "mime", 128, out var mime)
                || !OptionalText(root, "method", 12, out var method)
                || !OptionalText(root, "trigger", 20, out var trigger)) return false;
            if (trigger is not null and not ("download" or "contextMenu")) return false;
            if (filename is not null && !WindowsFileNamePolicy.IsValid(filename)) return false;
            long? size = null;
            if (root.TryGetProperty("sizeBytes", out var sizeField))
            {
                if (sizeField.ValueKind != JsonValueKind.Number || !sizeField.TryGetInt64(out var value) || value < 0) return false;
                size = value;
            }
            request = new(number, url!, referrer, userAgent, cookies, filename, mime, tabSite, size, method, trigger);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or DecoderFallbackException or InvalidOperationException or ArgumentException)
        { return false; }
    }

    private static bool OptionalUri(JsonElement root, string name, int maximum, out Uri? value)
    {
        value = null;
        return !root.TryGetProperty(name, out var field) || TryHttpUri(field, maximum, out value);
    }

    private static bool TryHttpUri(JsonElement field, int maximum, out Uri? uri)
    {
        uri = null;
        if (field.ValueKind != JsonValueKind.String) return false;
        var source = field.GetString();
        if (source is null || source.Length is < 1 or > 16384 || StrictUtf8.GetByteCount(source) > maximum
            || source.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)) || source.Contains('\\')) return false;
        if (!Uri.TryCreate(source, UriKind.Absolute, out var parsed)
            || parsed.Scheme is not ("http" or "https") || string.IsNullOrEmpty(parsed.Host) || parsed.UserInfo.Length != 0)
            return false;
        uri = parsed;
        return true;
    }

    private static bool OptionalText(JsonElement root, string name, int maximum, out string? value)
    {
        value = null;
        if (!root.TryGetProperty(name, out var field)) return true;
        if (field.ValueKind != JsonValueKind.String) return false;
        value = field.GetString();
        return value is not null && value.Length <= maximum && !value.Any(char.IsControl);
    }
}
