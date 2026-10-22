using AriaGui.Core.Downloads;

namespace AriaGui.Application.Notifications;

public sealed record NotificationPreferences(
    bool DesktopEnabled = true,
    bool CompletedEnabled = true,
    bool FailedEnabled = true,
    bool EngineAttentionEnabled = true,
    bool HideFileNames = true,
    bool OnlyWhenInactive = true);

public enum DesktopDecision { Deliver, Defer, Suppress }

public sealed record NotificationDesktopMessage(string Title, string Body, string ActivationArgument);

public interface INotificationDesktopSink
{
    // False means the operating system could not accept the notification.
    Task<bool> ShowAsync(NotificationDesktopMessage message, CancellationToken token);
}

public static class NotificationPolicy
{
    public static readonly TimeSpan BurstWindow = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan StartupFreshness = TimeSpan.FromMinutes(5);

    public static DesktopDecision Decide(NotificationEntry entry, NotificationPreferences preferences,
        bool isAppActive, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(preferences);
        if (entry.DeliveryState != NotificationDeliveryState.Pending || entry.DismissedAt is not null)
            return DesktopDecision.Suppress;
        var age = now - entry.CreatedAt;
        if (age > StartupFreshness) return DesktopDecision.Suppress;
        if (!preferences.DesktopEnabled || preferences.OnlyWhenInactive && isAppActive
            || (entry.Kind switch
            {
                NotificationKind.TransferCompleted => !preferences.CompletedEnabled,
                NotificationKind.TransferFailed => !preferences.FailedEnabled,
                NotificationKind.EngineAttention => !preferences.EngineAttentionEnabled,
                _ => true
            })) return DesktopDecision.Suppress;
        return age < BurstWindow ? DesktopDecision.Defer : DesktopDecision.Deliver;
    }

    public static NotificationDesktopMessage Compose(IReadOnlyList<NotificationEntry> events,
        NotificationPreferences preferences)
    {
        if (events.Count == 0) throw new ArgumentException("Choose at least one event.", nameof(events));
        var kind = events[0].Kind;
        if (events.Any(entry => entry.Kind != kind)) throw new ArgumentException("A burst must use one category.", nameof(events));
        var title = events.Count > 1 ? kind switch
        {
            NotificationKind.TransferCompleted => $"{events.Count} transfers completed",
            NotificationKind.TransferFailed => $"{events.Count} transfers failed",
            _ => $"{events.Count} engine alerts"
        } : kind switch
        {
            NotificationKind.TransferCompleted => events[0].TransferKind == TransferKind.Torrent ? "Torrent completed" : "Download completed",
            NotificationKind.TransferFailed => events[0].TransferKind == TransferKind.Torrent ? "Torrent failed" : "Download failed",
            _ => "Download engine needs attention"
        };
        var body = events.Count > 1 || preferences.HideFileNames
            ? "Open AriaGUI to view details."
            : events[0].DisplayName;
        return new NotificationDesktopMessage(title, body,
            events.Count > 1 ? NotificationActivation.InboxArgument : NotificationActivation.ForEvent(events[0].Id));
    }
}
