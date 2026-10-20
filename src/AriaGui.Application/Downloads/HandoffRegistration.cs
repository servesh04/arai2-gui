using System.Text.Json;
using Microsoft.Win32;

namespace AriaGui.Application.Downloads;

public static class HandoffRegistration
{
    public const string HostName = "com.ariagui.handoff";
    public const string ExtensionId = "fipdcdbkjacoejdioahbjajjpcadiepm";
    private const string AppKey = @"Software\AriaGUI\BrowserHandoff";
    private static readonly string[] BrowserKeys =
    [
        @"Software\Google\Chrome\NativeMessagingHosts\" + HostName,
        @"Software\Microsoft\Edge\NativeMessagingHosts\" + HostName
    ];

    public static void Register(string hostExecutable, string appExecutable)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (!Path.IsPathFullyQualified(hostExecutable) || !Path.IsPathFullyQualified(appExecutable)
            || !File.Exists(hostExecutable) || !File.Exists(appExecutable)
            || Path.GetFileName(hostExecutable) != "AriaGui.NativeHost.exe")
            throw new IOException("Browser host or app executable is missing.");
        var manifest = Path.Combine(Path.GetDirectoryName(hostExecutable)!, HostName + ".json");
        var body = JsonSerializer.Serialize(new
        {
            name = HostName, description = "AriaGUI browser download handoff",
            path = Path.GetFullPath(hostExecutable), type = "stdio",
            allowed_origins = new[] { "chrome-extension://" + ExtensionId + "/" }
        });
        File.WriteAllText(manifest, body);
        using (var app = Registry.CurrentUser.CreateSubKey(AppKey, true))
            app!.SetValue("AppPath", Path.GetFullPath(appExecutable), RegistryValueKind.String);
        foreach (var key in BrowserKeys)
            using (var browser = Registry.CurrentUser.CreateSubKey(key, true))
                browser!.SetValue(null, manifest, RegistryValueKind.String);
    }

    public static bool IsRegistered()
    {
        if (!OperatingSystem.IsWindows()) return false;
        foreach (var key in BrowserKeys)
            if (Registry.CurrentUser.OpenSubKey(key)?.GetValue(null) is not string path || !File.Exists(path)) return false;
        return true;
    }

    public static bool Unregister()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            foreach (var key in BrowserKeys) Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKeyTree(AppKey, throwOnMissingSubKey: false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    public static string? RegisteredAppPath()
    {
        if (!OperatingSystem.IsWindows()) return null;
        return Registry.CurrentUser.OpenSubKey(AppKey)?.GetValue("AppPath") as string;
    }
}
