using System.Security.Cryptography;
using System.Text;

namespace AriaGui.Browser.Contracts;

/// <summary>Shared installation endpoint algorithm. Inputs come only from trusted local installation context.</summary>
public static class BridgeEndpointName
{
    public static string Create(string runtime, string channel, string packageModel, string sid, Guid installation) =>
        "AriaGUI.browser.v1." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n',
            "1", channel, packageModel, Path.TrimEndingDirectorySeparator(Path.GetFullPath(runtime)).ToUpperInvariant(), sid, installation.ToString("D")))));
}
