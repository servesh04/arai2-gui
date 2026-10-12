using System.Security.Principal;
using System.Text;
using AriaGui.Browser.Contracts;
using AriaGui.Infrastructure.Aria2;

namespace AriaGui.Infrastructure.Browser;

public sealed record BrowserBridgeEndpoint(Guid InstallationId, string PipeName, string RuntimeDirectory,
    string UserSid, string Channel, string PackageModel)
{
    public static BrowserBridgeEndpoint Open(string runtime, string channel, string packageModel)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Browser bridge requires Windows.");
        UserRuntimeDirectory.EnsurePrivate(runtime);
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new IOException("Bridge identity unavailable.");
        var identity = Path.Combine(runtime, "browser-installation-v1.txt");
        Guid installation;
        if (File.Exists(identity))
        {
            if ((File.GetAttributes(identity) & FileAttributes.ReparsePoint) != 0 || new FileInfo(identity).Length > 64
                || !Guid.TryParseExact(File.ReadAllText(identity).Trim(), "D", out installation) || installation == Guid.Empty)
                throw new IOException("Bridge installation identity unavailable.");
        }
        else
        {
            installation = Guid.NewGuid();
            var temporary = Path.Combine(runtime, $"browser-installation-{Guid.NewGuid():N}.tmp");
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { stream.Write(Encoding.UTF8.GetBytes(installation.ToString("D"))); stream.Flush(true); }
                File.Move(temporary, identity, overwrite: false); // Atomic publication, under primary lease/private parent ACL.
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        return new(installation, Name(runtime, channel, packageModel, sid, installation), Path.GetFullPath(runtime), sid, channel, packageModel);
    }
    public static string Name(string runtime, string channel, string packageModel, string sid, Guid installation) =>
        BridgeEndpointName.Create(runtime, channel, packageModel, sid, installation);
}
