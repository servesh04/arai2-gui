using AriaGui.Application.Notifications;
using Microsoft.Win32;
using System.Runtime.InteropServices;
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
    private bool _shellIdentityReady;
    private bool _disposed;

    // Raised by Windows on a background thread; the shell must dispatch UI work.
    public event Action<NotificationActivationTarget>? Activated;
    public bool IsAvailable => _registered && _shellIdentityReady && !_disposed && !IsElevated() && NotificationsEnabled();

    public bool Start()
    {
        if (_disposed) return false;
        if (_registered)
        {
            if (!_shellIdentityReady) _shellIdentityReady = NotificationShortcut.TryEnsure();
            return IsAvailable;
        }
        try
        {
            _manager = AppNotificationManager.Default;
            _manager.NotificationInvoked += OnNotificationInvoked;
            _manager.Register();
            _registered = true;
            _shellIdentityReady = NotificationShortcut.TryEnsure();
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
        _shellIdentityReady = false;
        _manager = null;
    }

    private static class NotificationShortcut
    {
        private const string OwnedDescription = "AriaGUI desktop notification identity";
        private static readonly Guid AppUserModelFormat = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");

        public static bool TryEnsure()
        {
            try
            {
                var executable = Environment.ProcessPath;
                if (string.IsNullOrEmpty(executable) || !Path.IsPathFullyQualified(executable)) return false;
                var escapedPath = executable.Replace('\\', '.').Replace('/', '.');
                using var pathKey = Registry.CurrentUser.OpenSubKey($@"Software\Classes\AppUserModelId\{escapedPath}");
                if (!Guid.TryParse(pathKey?.GetValue("NotificationGUID") as string, out var appId)) return false;
                using var appKey = Registry.CurrentUser.OpenSubKey($@"Software\Classes\AppUserModelId\{appId:B}");
                if (!Guid.TryParse(appKey?.GetValue("CustomActivator") as string, out var activator)) return false;
                using var serverKey = Registry.CurrentUser.OpenSubKey($@"Software\Classes\CLSID\{activator:B}\LocalServer32");
                var server = serverKey?.GetValue(null) as string;
                if (server is null || !server.StartsWith($"\"{executable}\" ----AppNotificationActivated:", StringComparison.OrdinalIgnoreCase))
                    return false;

                var programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
                if (string.IsNullOrEmpty(programs) || !Directory.Exists(programs)) return false;
#if DEBUG
                const string shortcutName = "AriaGUI (Debug).lnk";
#else
                const string shortcutName = "AriaGUI.lnk";
#endif
                var shortcutPath = Path.Combine(programs, shortcutName);
                if (File.Exists(shortcutPath) && !IsOwned(shortcutPath)) return false;
                Create(shortcutPath, executable, appId, activator);
                return true;
            }
            catch (Exception)
            {
                // In-app notifications still work when the shell identity cannot be installed.
                return false;
            }
        }

        private static bool IsOwned(string path)
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return false;
            var shell = Activator.CreateInstance(shellType)!;
            try
            {
                dynamic shortcut = ((dynamic)shell).CreateShortcut(path);
                try { return (string)shortcut.Description == OwnedDescription; }
                finally { Marshal.FinalReleaseComObject(shortcut); }
            }
            finally { Marshal.FinalReleaseComObject(shell); }
        }

        private static void Create(string path, string executable, Guid appId, Guid activator)
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Windows shortcut service is unavailable.");
            var shell = Activator.CreateInstance(shellType)!;
            try
            {
                dynamic shortcut = ((dynamic)shell).CreateShortcut(path);
                try
                {
                    shortcut.TargetPath = executable;
                    shortcut.WorkingDirectory = Path.GetDirectoryName(executable)!;
                    shortcut.Description = OwnedDescription;
                    shortcut.Save();
                }
                finally { Marshal.FinalReleaseComObject(shortcut); }
            }
            finally { Marshal.FinalReleaseComObject(shell); }

            var propertyStoreId = typeof(IPropertyStore).GUID;
            Marshal.ThrowExceptionForHR(SHGetPropertyStoreFromParsingName(path, IntPtr.Zero, 2, ref propertyStoreId, out var store));
            try
            {
                SetProperty(store, 5, 31, Marshal.StringToCoTaskMemUni(appId.ToString("B")));
                var activatorPointer = Marshal.AllocCoTaskMem(16);
                Marshal.StructureToPtr(activator, activatorPointer, false);
                SetProperty(store, 26, 72, activatorPointer);
                Marshal.ThrowExceptionForHR(store.Commit());
            }
            finally { Marshal.ReleaseComObject(store); }
        }

        private static void SetProperty(IPropertyStore store, uint propertyId, ushort type, IntPtr value)
        {
            var key = new PropertyKey(AppUserModelFormat, propertyId);
            var variant = new PropVariant { Type = type, Value = value };
            try { Marshal.ThrowExceptionForHR(store.SetValue(ref key, ref variant)); }
            finally { PropVariantClear(ref variant); }
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHGetPropertyStoreFromParsingName(string path, IntPtr bindContext, uint flags,
            ref Guid interfaceId, out IPropertyStore store);

        [DllImport("ole32.dll")]
        private static extern int PropVariantClear(ref PropVariant variant);

        [StructLayout(LayoutKind.Sequential)]
        private struct PropertyKey(Guid formatId, uint propertyId)
        {
            public Guid FormatId = formatId;
            public uint PropertyId = propertyId;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct PropVariant
        {
            [FieldOffset(0)] public ushort Type;
            [FieldOffset(8)] public IntPtr Value;
        }

        [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int GetAt(uint index, out PropertyKey key);
            [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
            [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
            [PreserveSig] int Commit();
        }
    }
}
