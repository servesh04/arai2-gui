using System.Security.AccessControl;
using System.Security.Principal;
using AriaGui.Application.Engine;

namespace AriaGui.Infrastructure.Aria2;

public static class UserRuntimeDirectory
{
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AriaGUI", "Engine");

    public static void EnsurePrivate(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("AriaGUI manages a Windows engine.");
        try
        {
            if (!Path.IsPathFullyQualified(path) || path.IndexOfAny(['\r', '\n']) >= 0)
                throw new ArgumentException("An absolute runtime path is required.");
            var directory = Directory.CreateDirectory(path);
            if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("The runtime directory must not be a symbolic link.");
            var currentUser = WindowsIdentity.GetCurrent().User
                ?? throw new IOException("The current Windows user could not be identified.");
            var permissions = new DirectorySecurity();
            permissions.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            permissions.AddAccessRule(new FileSystemAccessRule(currentUser, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            permissions.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
            directory.SetAccessControl(permissions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { throw new EngineException(EngineFailure.StorageUnavailable, "The engine's private data folder is unavailable. Check folder access."); }
    }
}
