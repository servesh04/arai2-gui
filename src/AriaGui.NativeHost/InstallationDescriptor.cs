using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AriaGui.NativeHost;

public sealed record InstallationDescriptor(int SchemaVersion, string Mode, Guid InstallationId, string Channel,
    string PackageModel, string RuntimeDirectory, string HostExecutable, string GuiExecutable, string GuiSha256,
    string MinimumAppVersion, bool AllowLaunch, string[] LaunchArguments, string[] ChromiumOrigins,
    string FirefoxManifest, string[] FirefoxAddonIds)
{
    public const string FileName = "native-host.v1.json";
    public static InstallationDescriptor Load()
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess) throw new InvalidDataException();
        var path = Path.Combine(AppContext.BaseDirectory, FileName);
        VerifyPrivateFile(path, 16384);
        using var json = JsonDocument.Parse(ReadBounded(path, 16384), new JsonDocumentOptions { MaxDepth = 4 });
        Unique(json.RootElement);
        var descriptor = JsonSerializer.Deserialize<InstallationDescriptor>(json, new JsonSerializerOptions
        { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, PropertyNameCaseInsensitive = false }) ?? throw new InvalidDataException();
        descriptor.Validate(); return descriptor;
    }
    private void Validate()
    {
        if (SchemaVersion != 1 || InstallationId == Guid.Empty || Channel is not ("release" or "verification" or "fixture")
            || PackageModel is not ("unpackaged" or "synthetic-gui") || Mode is not ("installed" or "verification")
            || !Version.TryParse(MinimumAppVersion, out var version) || version.Major != 1
            || ChromiumOrigins is not { Length: > 0 and <= 8 } || FirefoxAddonIds is not { Length: > 0 and <= 8 }
            || LaunchArguments is not { Length: <= 4 } || GuiSha256 is null || !Regex.IsMatch(GuiSha256, "\\A[0-9A-Fa-f]{64}\\z")) throw new InvalidDataException();
#if !DEBUG
        if (Mode == "verification") throw new InvalidDataException();
#endif
        foreach (var value in new[] { RuntimeDirectory, HostExecutable, GuiExecutable, FirefoxManifest })
            if (value is null || value.Length > 1024 || !Path.IsPathFullyQualified(value) || value.StartsWith("\\\\", StringComparison.Ordinal)
                || value.Any(char.IsControl)) throw new InvalidDataException();
        if (!string.Equals(Path.GetFullPath(HostExecutable), Path.GetFullPath(Environment.ProcessPath!), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException();
        if (ChromiumOrigins.Any(origin => origin is null || !Regex.IsMatch(origin, "\\Achrome-extension://[a-p]{32}/\\z"))
            || ChromiumOrigins.Distinct(StringComparer.Ordinal).Count() != ChromiumOrigins.Length
            || FirefoxAddonIds.Any(id => id is null || id.Length is < 3 or > 128 || id.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)) || id.Contains('*'))
            || FirefoxAddonIds.Distinct(StringComparer.Ordinal).Count() != FirefoxAddonIds.Length) throw new InvalidDataException();
        if (Mode == "installed")
        {
            if (Channel != "release" || PackageModel != "unpackaged" || !LaunchArguments.SequenceEqual(new[] { "--browser-wakeup" })) throw new InvalidDataException();
        }
        else if (!File.Exists(Path.Combine(RuntimeDirectory, "synthetic-browser-bridge")) || Channel != "fixture"
            || LaunchArguments.Length != 3 || LaunchArguments[0] != "--bridge-auto-host" || LaunchArguments[1] != RuntimeDirectory || LaunchArguments[2] != HostExecutable)
            throw new InvalidDataException();
        VerifyPrivateFile(GuiExecutable, 256 * 1024 * 1024);
        using var executable = File.OpenRead(GuiExecutable);
        if (!Convert.ToHexString(SHA256.HashData(executable)).Equals(GuiSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException();
        VerifyPrivateFile(Path.Combine(RuntimeDirectory, "browser-installation-v1.txt"), 64);
        if (!Guid.TryParseExact(System.Text.Encoding.UTF8.GetString(ReadBounded(Path.Combine(RuntimeDirectory, "browser-installation-v1.txt"),64)).Trim(), "D", out var installation) || installation != InstallationId) throw new InvalidDataException();
    }
    public static void VerifyPrivateFile(string path, long maximumBytes)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var file = new FileInfo(path);
        if (!file.Exists || file.Length > maximumBytes || (file.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException();
        for (var directory = file.Directory; directory is not null; directory = directory.Parent)
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException();
        var sid = WindowsIdentity.GetCurrent().User ?? throw new InvalidDataException();
        var allowed = new HashSet<string> { sid.Value, "S-1-5-18", "S-1-5-32-544" };
        var security = file.GetAccessControl();
        CheckSecurity(file.Directory!.GetAccessControl(), allowed);
        CheckSecurity(security, allowed);
    }
    private static void CheckSecurity(FileSystemSecurity security, HashSet<string> allowed)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !allowed.Contains(owner.Value)) throw new InvalidDataException();
        const FileSystemRights write = FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteAttributes
            | FileSystemRights.WriteExtendedAttributes | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && (rule.FileSystemRights & write) != 0 && !allowed.Contains(rule.IdentityReference.Value)) throw new InvalidDataException();
    }
    internal static byte[] ReadBounded(string path,int maximum)
    {
        using var stream=File.OpenRead(path);var bytes=new byte[maximum+1];var total=0;
        while(total<bytes.Length){var read=stream.Read(bytes,total,bytes.Length-total);if(read==0)break;total+=read;}
        if(total>maximum)throw new InvalidDataException();return bytes[..total];
    }
    internal static void Unique(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject()) { if (!names.Add(property.Name)) throw new InvalidDataException(); Unique(property.Value); }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) Unique(item);
    }
}
