using AriaGui.Application.Notifications;
using AriaGui.Core.Downloads;

namespace AriaGui.Infrastructure.Persistence;

public sealed class NotificationEventRow
{
    public long Id { get; set; }
    public string EventKey { get; set; } = "";
    public NotificationKind Kind { get; set; }
    public TransferKind? TransferKind { get; set; }
    public Guid? JobId { get; set; }
    public Guid? AttemptId { get; set; }
    public string DisplayName { get; set; } = "";
    public string Detail { get; set; } = "";
    public long CreatedAtUtcTicks { get; set; }
    public long? ReadAtUtcTicks { get; set; }
    public long? DismissedAtUtcTicks { get; set; }
    public NotificationDeliveryState DeliveryState { get; set; }
    public long? DeliveryUpdatedAtUtcTicks { get; set; }
}

// Receipts survive inbox pruning, so an old poll cannot recreate an event.
public sealed class NotificationReceiptRow
{
    public string EventKey { get; set; } = "";
    public long CreatedAtUtcTicks { get; set; }
}

// Incremented by a producer in the retry transaction; initial lifecycle is revision zero.
public sealed class NotificationSourceRevisionRow
{
    public string SourceKey { get; set; } = "";
    public long Revision { get; set; }
}
