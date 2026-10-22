using System.Globalization;

namespace AriaGui.Application.Notifications;

public sealed record NotificationActivationTarget(long? EventId);

public static class NotificationActivation
{
    public const string InboxArgument = "notifications";
    private const string EventPrefix = "notifications/";

    public static string ForEvent(long id) => id > 0
        ? EventPrefix + id.ToString(CultureInfo.InvariantCulture)
        : throw new ArgumentOutOfRangeException(nameof(id));

    public static bool TryParse(string? argument, out NotificationActivationTarget? target)
    {
        if (argument == InboxArgument)
        {
            target = new NotificationActivationTarget(null);
            return true;
        }
        if (argument is not null && argument.StartsWith(EventPrefix, StringComparison.Ordinal)
            && long.TryParse(argument.AsSpan(EventPrefix.Length), NumberStyles.None,
                CultureInfo.InvariantCulture, out var id) && id > 0)
        {
            target = new NotificationActivationTarget(id);
            return true;
        }
        target = null;
        return false;
    }
}
