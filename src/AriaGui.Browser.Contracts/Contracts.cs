using System.Text.Json.Serialization;

namespace AriaGui.Browser.Contracts;

public static class ProtocolLimits
{
    public const int Major = 1, Minor = 0, MaximumMessageBytes = 65536, MaximumDepth = 8, MaximumUrlBytes = 16384;
    public static readonly TimeSpan FrameDeadline = TimeSpan.FromSeconds(5);
}
public sealed record ProtocolVersion(
    [property: JsonPropertyName("major")] int Major,
    [property: JsonPropertyName("minor")] int Minor);
public enum BrowserAction { Hello, AddPublicGet, GetRequestStatus }
public enum ResultCode { Ready, Accepted, Rejected, Processing, NotFound, Expired, Unavailable, Unknown }
public enum RejectionReason { UnsupportedProtocol, InvalidRequest, UnsupportedUrl, InvalidFilename, DestinationConflict,
    DestinationUnavailable, IntegrationDisabled, NotReady, ShuttingDown, Busy, StorageUnavailable, RequestIdConflict }
public enum JobIntakeState { Queued, Active, Paused, Completed, Failed, Interrupted, EngineUncertain, Removed }
public enum Capability { AddPublicGet, GetRequestStatus }
public abstract record RequestPayload;
public sealed record HelloPayload(
    [property: JsonPropertyName("installationId")] Guid InstallationId,
    [property: JsonPropertyName("extensionVersion")] string ExtensionVersion) : RequestPayload;
public sealed record PublicGetPayload(
    [property: JsonPropertyName("installationId")] Guid InstallationId,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("sourceUrl")] string SourceUrl,
    [property: JsonPropertyName("suggestedFileName")] string? SuggestedFileName,
    [property: JsonPropertyName("trigger")] string Trigger = "linkContextMenu") : RequestPayload;
public sealed record StatusPayload(
    [property: JsonPropertyName("installationId")] Guid InstallationId,
    [property: JsonPropertyName("targetRequestId")] Guid TargetRequestId) : RequestPayload;
public sealed record RequestEnvelope(ProtocolVersion Protocol, Guid RequestId, BrowserAction Action, RequestPayload Payload);
public sealed record ResponseResult(ResultCode Code, string? AppVersion = null, IReadOnlyList<Capability>? Capabilities = null,
    int? MaxMessageBytes = null, Guid? JobId = null, JobIntakeState? State = null, bool? Duplicate = null,
    RejectionReason? Reason = null, int? RetryAfterMs = null);
public sealed record ResponseEnvelope(ProtocolVersion Protocol, Guid RequestId, ResponseResult Result);
/// <summary>Only a fully parsed envelope with unique properties can supply a trustworthy correlation ID.</summary>
public sealed record ContractParseResult<T>(T? Value, RejectionReason? Error, Guid? CorrelationId) where T : class
{
    public bool Success => Value is not null;
}
