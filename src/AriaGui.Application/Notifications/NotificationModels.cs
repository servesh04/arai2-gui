using AriaGui.Core.Downloads;

namespace AriaGui.Application.Notifications;

public enum NotificationKind { TransferCompleted, TransferFailed, EngineAttention }
public enum NotificationDeliveryState { Pending, Sending, Delivered, Suppressed }

public static class NotificationEventKeys
{
    public static string Http(Guid attemptId, NotificationKind kind) =>
        attemptId == Guid.Empty ? throw new ArgumentException("An attempt ID is required.") : $"http:{attemptId:N}:{kind}";
    public static string Torrent(Guid jobId, long revision, NotificationKind kind) =>
        jobId == Guid.Empty || revision < 0 ? throw new ArgumentException("A torrent ID and revision are required.")
            : $"torrent:{jobId:N}:{revision}:{kind}";
}

// EventKey is a stable, source-owned transition identity, not a file path or URL.
public sealed record NotificationDraft(string EventKey, NotificationKind Kind, TransferKind? TransferKind,
    Guid? JobId, Guid? AttemptId, string DisplayName, string Detail, DateTimeOffset CreatedAt)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(EventKey) || EventKey.Length > 160
            || EventKey.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch is not (':' or '-' or '_' or '.'))
            || !Enum.IsDefined(Kind) || TransferKind is { } transferKind && !Enum.IsDefined(transferKind)
            || JobId == Guid.Empty || AttemptId == Guid.Empty || DisplayName is null || Detail is null
            || DisplayName.Length > 256 || Detail.Length > 512 || CreatedAt == default)
            throw new ArgumentException("Invalid notification event.");
    }
}

public sealed record NotificationEntry(long Id, NotificationKind Kind, TransferKind? TransferKind,
    Guid? JobId, Guid? AttemptId, string DisplayName, string Detail, DateTimeOffset CreatedAt,
    DateTimeOffset? ReadAt, DateTimeOffset? DismissedAt, NotificationDeliveryState DeliveryState);
