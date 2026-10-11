using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AriaGui.Core.Downloads;

namespace AriaGui.Browser.Contracts;

/// <summary>Closed v1 schema: exact case, unique properties, no engine options or arbitrary extension bags.</summary>
public static partial class ContractJson
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    public static ContractParseResult<RequestEnvelope> ParseRequest(ReadOnlyMemory<byte> bytes) => Parse(bytes, root =>
    {
        Fields(root, ["protocol", "requestId", "action", "payload"]);
        var protocol = Version(root.GetProperty("protocol"));
        var id = Id(root.GetProperty("requestId"));
        var action = ReadEnum<BrowserAction>(root.GetProperty("action"));
        var payload = root.GetProperty("payload");
        RequestPayload value;
        switch (action)
        {
            case BrowserAction.Hello:
                Fields(payload, ["installationId", "extensionVersion"]);
                value = new HelloPayload(Id(payload.GetProperty("installationId")), VersionText(payload.GetProperty("extensionVersion")));
                break;
            case BrowserAction.AddPublicGet:
                Fields(payload, ["installationId", "createdAt", "sourceUrl", "trigger"], ["suggestedFileName"]);
                var timestamp = Text(payload.GetProperty("createdAt"));
                if (!UtcTimestamp().IsMatch(timestamp) || !DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var created)) throw Invalid();
                var source = ValidateUrl(Text(payload.GetProperty("sourceUrl")));
                var name = OptionalText(payload, "suggestedFileName");
                if (name is not null && !WindowsFileNamePolicy.IsValid(name)) throw Invalid(RejectionReason.InvalidFilename);
                if (Text(payload.GetProperty("trigger")) != "linkContextMenu") throw Invalid();
                value = new PublicGetPayload(Id(payload.GetProperty("installationId")), created, source, name);
                break;
            case BrowserAction.GetRequestStatus:
                Fields(payload, ["installationId", "targetRequestId"]);
                value = new StatusPayload(Id(payload.GetProperty("installationId")), Id(payload.GetProperty("targetRequestId")));
                break;
            default: throw Invalid();
        }
        return new RequestEnvelope(protocol, id, action, value);
    });

    public static ContractParseResult<ResponseEnvelope> ParseResponse(ReadOnlyMemory<byte> bytes) => Parse(bytes, root =>
    {
        Fields(root, ["protocol", "requestId", "result"]);
        var protocol = Version(root.GetProperty("protocol"));
        var id = Id(root.GetProperty("requestId"));
        var result = root.GetProperty("result");
        var code = ReadEnum<ResultCode>(result.GetProperty("code"));
        ResponseResult value;
        switch (code)
        {
            case ResultCode.Ready:
                Fields(result, ["code", "appVersion", "capabilities", "maxMessageBytes"]);
                var array = result.GetProperty("capabilities");
                if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > 2) throw Invalid();
                var capabilities = array.EnumerateArray().Select(ReadEnum<Capability>).ToArray();
                if (capabilities.Distinct().Count() != capabilities.Length) throw Invalid();
                value = new(code, VersionText(result.GetProperty("appVersion")), capabilities, Integer(result.GetProperty("maxMessageBytes"), 1, ProtocolLimits.MaximumMessageBytes));
                break;
            case ResultCode.Accepted:
                Fields(result, ["code", "jobId", "state", "duplicate"]);
                var duplicate = result.GetProperty("duplicate");
                if (duplicate.ValueKind is not JsonValueKind.True and not JsonValueKind.False) throw Invalid();
                value = new(code, JobId: Id(result.GetProperty("jobId")), State: ReadEnum<JobIntakeState>(result.GetProperty("state")), Duplicate: duplicate.GetBoolean());
                break;
            case ResultCode.Rejected:
                Fields(result, ["code", "reason"]);
                value = new(code, Reason: ReadEnum<RejectionReason>(result.GetProperty("reason")));
                break;
            case ResultCode.Processing:
                Fields(result, ["code", "retryAfterMs"]);
                value = new(code, RetryAfterMs: Integer(result.GetProperty("retryAfterMs"), 1, 10000));
                break;
            default:
                Fields(result, ["code"]);
                value = new(code);
                break;
        }
        return new ResponseEnvelope(protocol, id, value);
    });

    private static ContractParseResult<T> Parse<T>(ReadOnlyMemory<byte> bytes, Func<JsonElement, T> parse) where T : class
    {
        Guid? correlation = null;
        try
        {
            if (bytes.Length is < 1 or > ProtocolLimits.MaximumMessageBytes) throw Invalid();
            _ = Utf8.GetCharCount(bytes.Span); // Reject malformed UTF-8 before System.Text.Json's replacement behavior.
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = ProtocolLimits.MaximumDepth });
            UniqueProperties(document.RootElement);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("requestId", out var requestId)) correlation = Id(requestId);
            return new(parse(root), null, correlation);
        }
        catch (InvalidContractException ex) { return new(null, ex.Reason, correlation); }
        catch (Exception ex) when (ex is JsonException or DecoderFallbackException or InvalidOperationException or FormatException or KeyNotFoundException)
        { return new(null, RejectionReason.InvalidRequest, correlation); }
    }
    private static void UniqueProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw Invalid();
                UniqueProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) UniqueProperties(item);
    }
    private static void Fields(JsonElement element, string[] required, string[]? optional = null)
    {
        if (element.ValueKind != JsonValueKind.Object) throw Invalid();
        foreach (var field in required) if (!element.TryGetProperty(field, out _)) throw Invalid();
        foreach (var property in element.EnumerateObject())
            if (!required.Contains(property.Name) && !(optional?.Contains(property.Name) ?? false)) throw Invalid();
    }
    private static ProtocolVersion Version(JsonElement value)
    {
        Fields(value, ["major", "minor"]);
        var major = Integer(value.GetProperty("major"), 0, int.MaxValue);
        var minor = Integer(value.GetProperty("minor"), 0, int.MaxValue);
        if (major != ProtocolLimits.Major) throw Invalid(RejectionReason.UnsupportedProtocol);
        return new(major, minor);
    }
    private static int Integer(JsonElement value, int min, int max) => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var integer)
        && integer >= min && integer <= max ? integer : throw Invalid();
    private static Guid Id(JsonElement value)
    {
        var text = Text(value);
        return Guid.TryParseExact(text, "D", out var id) && id != Guid.Empty && id.ToString("D") == text ? id : throw Invalid();
    }
    private static string Text(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString()! : throw Invalid();
    private static string? OptionalText(JsonElement element, string name) => element.TryGetProperty(name, out var value) ? Text(value) : null;
    private static string VersionText(JsonElement value)
    {
        var version = Text(value);
        return version.Length <= 64 && VersionString().IsMatch(version) ? version : throw Invalid();
    }
    private static string ValidateUrl(string source)
    {
        if (Utf8.GetByteCount(source) > ProtocolLimits.MaximumUrlBytes) throw Invalid();
        if (source.Length < 1 || source.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)) || source.Contains('\\'))
            throw Invalid(RejectionReason.UnsupportedUrl);
        for (var index = 0; index < source.Length; index++)
        {
            if (source[index] != '%') continue;
            if (index + 2 >= source.Length || !byte.TryParse(source.AsSpan(index + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var encoded)
                || encoded < 32 || encoded == 127) throw Invalid(RejectionReason.UnsupportedUrl);
            index += 2;
        }
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) || uri.Scheme is not "http" and not "https"
            || string.IsNullOrEmpty(uri.Host) || uri.UserInfo.Length != 0 || !source.Contains("://", StringComparison.Ordinal))
            throw Invalid(RejectionReason.UnsupportedUrl);
        var authorityStart = source.IndexOf("://", StringComparison.Ordinal) + 3;
        var authorityEnd = source.IndexOfAny(['/', '?', '#'], authorityStart);
        if (source.AsSpan(authorityStart, (authorityEnd < 0 ? source.Length : authorityEnd) - authorityStart).Contains('@'))
            throw Invalid(RejectionReason.UnsupportedUrl);
        var fragment = source.IndexOf('#');
        return fragment < 0 ? source : source[..fragment]; // Preserve signed query bytes/order, without URI reserialization.
    }
    internal static string WireName<T>(T value) where T : struct, Enum => value switch
    {
        BrowserAction.Hello => "hello", BrowserAction.AddPublicGet => "addPublicGet", BrowserAction.GetRequestStatus => "getRequestStatus",
        Capability.AddPublicGet => "addPublicGet", Capability.GetRequestStatus => "getRequestStatus",
        ResultCode.NotFound => "not_found", JobIntakeState.EngineUncertain => "engine_uncertain",
        RejectionReason.UnsupportedProtocol => "unsupported_protocol", RejectionReason.InvalidRequest => "invalid_request",
        RejectionReason.UnsupportedUrl => "unsupported_url", RejectionReason.InvalidFilename => "invalid_filename",
        RejectionReason.DestinationConflict => "destination_conflict", RejectionReason.DestinationUnavailable => "destination_unavailable",
        RejectionReason.IntegrationDisabled => "integration_disabled", RejectionReason.NotReady => "not_ready",
        RejectionReason.ShuttingDown => "shutting_down", RejectionReason.StorageUnavailable => "storage_unavailable",
        RejectionReason.RequestIdConflict => "request_id_conflict",
        _ when Enum.IsDefined(value) => value.ToString().ToLowerInvariant(),
        _ => throw Invalid()
    };
    private static T ReadEnum<T>(JsonElement element) where T : struct, Enum
    {
        var name = Text(element);
        foreach (var value in Enum.GetValues<T>()) if (WireName(value) == name) return value;
        throw Invalid();
    }
    private sealed class InvalidContractException(RejectionReason reason) : Exception("Invalid browser contract.")
    { internal RejectionReason Reason { get; } = reason; }
    private static InvalidContractException Invalid(RejectionReason reason = RejectionReason.InvalidRequest) => new(reason);
    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?Z$", RegexOptions.CultureInvariant)]
    private static partial Regex UtcTimestamp();
    [GeneratedRegex(@"^[0-9]+\.[0-9]+\.[0-9]+(?:[-+][A-Za-z0-9.-]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionString();

    public static byte[] Serialize(RequestEnvelope envelope)
    {
        // Bound typed input before serialization as well; it may come from a caller, not ParseRequest.
        ValidateEnvelope(envelope.Protocol, envelope.RequestId);
        switch (envelope.Payload)
        {
            case HelloPayload hello when envelope.Action == BrowserAction.Hello:
                ValidateIdentifier(hello.InstallationId); ValidateVersionText(hello.ExtensionVersion); break;
            case StatusPayload status when envelope.Action == BrowserAction.GetRequestStatus:
                ValidateIdentifier(status.InstallationId); ValidateIdentifier(status.TargetRequestId); break;
            case PublicGetPayload get when envelope.Action == BrowserAction.AddPublicGet:
                ValidateIdentifier(get.InstallationId);
                if (get.SourceUrl is null || get.SourceUrl.Length > ProtocolLimits.MaximumUrlBytes || get.Trigger != "linkContextMenu"
                    || get.SuggestedFileName is { } name && !WindowsFileNamePolicy.IsValid(name)) throw new ArgumentException("Invalid browser contract.");
                break;
            default: throw new ArgumentException("Invalid browser contract.");
        }
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            Start(writer, envelope.Protocol, envelope.RequestId);
            writer.WriteString("action", WireName(envelope.Action));
            writer.WritePropertyName("payload");
            if (envelope.Payload is PublicGetPayload get)
            {
                writer.WriteStartObject(); writer.WriteString("installationId", get.InstallationId);
                writer.WriteString("createdAt", get.CreatedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture));
                writer.WriteString("sourceUrl", ValidateUrl(get.SourceUrl));
                if (get.SuggestedFileName is not null) writer.WriteString("suggestedFileName", get.SuggestedFileName);
                writer.WriteString("trigger", get.Trigger); writer.WriteEndObject();
            }
            else JsonSerializer.Serialize(writer, envelope.Payload, envelope.Payload.GetType());
            writer.WriteEndObject();
        }
        var bytes = buffer.ToArray();
        var parsed = ParseRequest(bytes);
        if (!parsed.Success || parsed.Value!.Action != envelope.Action) throw new ArgumentException("Invalid browser contract.");
        return bytes;
    }
    public static byte[] Serialize(ResponseEnvelope envelope)
    {
        ValidateEnvelope(envelope.Protocol, envelope.RequestId);
        if (envelope.Result.AppVersion is { } version) ValidateVersionText(version);
        if (envelope.Result.Capabilities is { Count: > 2 }) throw new ArgumentException("Invalid browser contract.");
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            Start(writer, envelope.Protocol, envelope.RequestId);
            writer.WriteStartObject("result"); var result = envelope.Result;
            writer.WriteString("code", WireName(result.Code));
            if (result.AppVersion is not null) writer.WriteString("appVersion", result.AppVersion);
            if (result.Capabilities is not null) { writer.WriteStartArray("capabilities"); foreach (var capability in result.Capabilities) writer.WriteStringValue(WireName(capability)); writer.WriteEndArray(); }
            if (result.MaxMessageBytes is { } max) writer.WriteNumber("maxMessageBytes", max);
            if (result.JobId is { } job) writer.WriteString("jobId", job);
            if (result.State is { } state) writer.WriteString("state", WireName(state));
            if (result.Duplicate is { } duplicate) writer.WriteBoolean("duplicate", duplicate);
            if (result.Reason is { } reason) writer.WriteString("reason", WireName(reason));
            if (result.RetryAfterMs is { } retry) writer.WriteNumber("retryAfterMs", retry);
            writer.WriteEndObject(); writer.WriteEndObject();
        }
        var bytes = buffer.ToArray();
        if (!ParseResponse(bytes).Success) throw new ArgumentException("Invalid browser contract.");
        return bytes;
    }
    private static void Start(Utf8JsonWriter writer, ProtocolVersion version, Guid id)
    {
        writer.WriteStartObject(); writer.WriteStartObject("protocol"); writer.WriteNumber("major", version.Major);
        writer.WriteNumber("minor", version.Minor); writer.WriteEndObject(); writer.WriteString("requestId", id);
    }
    private static void ValidateEnvelope(ProtocolVersion protocol, Guid id)
    {
        ValidateIdentifier(id);
        if (protocol.Major != ProtocolLimits.Major || protocol.Minor < 0) throw new ArgumentException("Invalid browser contract.");
    }
    private static void ValidateIdentifier(Guid id) { if (id == Guid.Empty) throw new ArgumentException("Invalid browser contract."); }
    private static void ValidateVersionText(string version)
    { if (version is null || version.Length > 64 || !VersionString().IsMatch(version)) throw new ArgumentException("Invalid browser contract."); }
}
