using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AriaGui.NativeHost;

public enum BrowserInvocationKind { Chromium, Firefox }
public sealed record BrowserInvocation(BrowserInvocationKind Kind, string Caller)
{
    public static BrowserInvocation Validate(string[] args, InstallationDescriptor descriptor)
    {
        if (args.Length == 2 && args[0].StartsWith("chrome-extension://", StringComparison.Ordinal))
        {
            if (!descriptor.ChromiumOrigins.Contains(args[0], StringComparer.Ordinal) || !args[1].StartsWith("--parent-window=", StringComparison.Ordinal)
                || !ulong.TryParse(args[1].AsSpan(16), NumberStyles.None, CultureInfo.InvariantCulture, out var window) || window > long.MaxValue) throw new InvalidDataException();
            return new(BrowserInvocationKind.Chromium, args[0]); // HWND (including 0) is syntax only, never authorization.
        }
        if (args.Length == 2 && Path.IsPathFullyQualified(args[0]) && !args[0].Any(char.IsControl)
            && string.Equals(Path.GetFullPath(args[0]), Path.GetFullPath(descriptor.FirefoxManifest), StringComparison.OrdinalIgnoreCase)
            && descriptor.FirefoxAddonIds.Contains(args[1], StringComparer.Ordinal))
        {
            InstallationDescriptor.VerifyPrivateFile(descriptor.FirefoxManifest, 16384);
            using var manifest = JsonDocument.Parse(InstallationDescriptor.ReadBounded(descriptor.FirefoxManifest,16384),new JsonDocumentOptions{MaxDepth=4});
            var root = manifest.RootElement;
            InstallationDescriptor.Unique(root);
            if (root.GetProperty("type").GetString() != "stdio" || !string.Equals(root.GetProperty("path").GetString(), descriptor.HostExecutable, StringComparison.OrdinalIgnoreCase)
                || !root.GetProperty("allowed_extensions").EnumerateArray().Any(item => item.GetString() == args[1])) throw new InvalidDataException();
            return new(BrowserInvocationKind.Firefox, args[1]);
        }
        throw new InvalidDataException();
    }
}
