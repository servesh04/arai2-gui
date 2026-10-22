using AriaGui.Application.Notifications;
using System.Security.Principal;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace AriaGui.App.Services;

/// <summary>Windows desktop delivery. The notification center is stored separately.</summary>
public sealed class WindowsNotificationService : INotificationDesktopSink, IDisposable
{
    private const string RouteKey = "route";
    private AppNotificationManager? _manager;
    private bool _registered;
    private bool _disposed;

    // Raised by Windows on a background thread; the shell must dispatch UI work.
    public event Action<NotificationActivationTarget>? Activated;
    public bool IsAvailable => _registered && !_disposed && !IsElevated() && NotificationsEnabled();

    public bool Start()
    {
        if (_disposed) return false;
        if (_registered) return IsAvailable;
        try
        {
            _manager = AppNotificationManager.Default;
            _manager.NotificationInvoked += OnNotificationInvoked;
            _manager.Register();
            _registered = true;
            return IsAvailable;
        }
        catch
        {
            if (_manager is not null) _manager.NotificationInvoked -= OnNotificationInvoked;
            _manager = null;
            return false;
        }
    }

    public NotificationActivationTarget? CurrentActivation()
    {
        if (!_registered) return null;
        try
        {
            var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
            return activation.Kind == ExtendedActivationKind.AppNotification
                && activation.Data is AppNotificationActivatedEventArgs args ? Parse(args) : null;
        }
        catch { return null; }
    }

    public Task<bool> ShowAsync(NotificationDesktopMessage message, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(message);
        token.ThrowIfCancellationRequested();
        if (!NotificationActivation.TryParse(message.ActivationArgument, out _) || !IsAvailable)
            return Task.FromResult(false);
        try
        {
            var notification = new AppNotificationBuilder()
                .AddArgument(RouteKey, message.ActivationArgument)
                .AddText(message.Title)
                .AddText(message.Body)
                .BuildNotification();
            _manager!.Show(notification);
            return Task.FromResult(true);
        }
        catch { return Task.FromResult(false); }
    }

    private bool NotificationsEnabled()
    {
        try { return _manager?.Setting == AppNotificationSetting.Enabled; }
        catch { return false; }
    }

    private static bool IsElevated()
    {
        try { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); }
        catch { return true; }
    }

    private static NotificationActivationTarget? Parse(AppNotificationActivatedEventArgs args) =>
        args.Arguments.ContainsKey(RouteKey)
            && NotificationActivation.TryParse(args.Arguments[RouteKey], out var target) ? target : null;

    private void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        var target = Parse(args);
        if (target is null) return;
        try { Activated?.Invoke(target); }
        catch { /* Activation failures must not crash the Windows callback. */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (!_registered) return;
        _manager!.NotificationInvoked -= OnNotificationInvoked;
        try { _manager.Unregister(); }
        catch { /* App shutdown must continue if Windows registration is unavailable. */ }
        _registered = false;
        _manager = null;
    }
}
