using AriaGui.Application.Downloads;
using System.Runtime.InteropServices;
using System.Text;

namespace AriaGui.Infrastructure.Downloads;

internal static class ProtectedDestinationPolicy
{
    internal const string Message = "Choose a local NTFS folder without redirected links. Network folders are not supported for protected downloads.";
    internal static void Validate(string directory, bool inspectExisting = true)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(directory));
            if (root is null || root.Length != 3 || !char.IsAsciiLetter(root[0]) || root[1] != ':' || root[2] != '\\') throw new IOException();
            var drive = new DriveInfo(root);
            if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable) || !string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase)) throw new IOException();
            var mapping = new StringBuilder(32768);
            if (QueryDosDeviceW(root[..2], mapping, mapping.Capacity) == 0 || !IsVolumeMapping(mapping.ToString())) throw new IOException();
            if (inspectExisting)
            {
                var ancestor = Path.GetFullPath(directory);
                while (!Directory.Exists(ancestor)) ancestor = Path.GetDirectoryName(ancestor) ?? throw new IOException();
                using var lease = new PublicationDirectories(ancestor);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { throw new DownloadRequestException(Message, AriaGui.Browser.Contracts.RejectionReason.DestinationUnavailable); }
    }
    // SUBST and mapped-device aliases can hide unpinned ancestors. Only a local
    // volume root participates in the root-first directory lease policy.
    internal static bool IsVolumeMapping(string mapping)
    {
        const string prefix = @"\Device\HarddiskVolume";
        return mapping.StartsWith(prefix, StringComparison.Ordinal) && mapping.Length > prefix.Length
            && mapping[prefix.Length..].All(char.IsAsciiDigit);
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint QueryDosDeviceW(string device, StringBuilder target, int capacity);
}
