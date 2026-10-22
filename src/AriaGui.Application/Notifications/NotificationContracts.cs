namespace AriaGui.Application.Notifications;

public interface INotificationRepository
{
    Task InitializeAsync(CancellationToken token);
    Task<NotificationEntry?> TryAppendAsync(NotificationDraft draft, CancellationToken token);
    Task<NotificationEntry?> GetAsync(long id, CancellationToken token);
    Task<IReadOnlyList<NotificationEntry>> ReadPageAsync(int pageSize, long? beforeId, bool unreadOnly, CancellationToken token);
    Task<int> CountUnreadAsync(CancellationToken token);
    Task MarkReadAsync(long id, CancellationToken token);
    Task MarkAllReadAsync(CancellationToken token);
    Task DismissAsync(long id, CancellationToken token);
    Task DismissAllAsync(CancellationToken token);
    Task<bool> TryClaimDesktopAsync(long id, CancellationToken token);
    Task FinishDesktopAsync(long id, NotificationDeliveryState result, CancellationToken token);
    Task<int> PruneReadAsync(DateTimeOffset olderThan, int maxVisible, CancellationToken token);
}
