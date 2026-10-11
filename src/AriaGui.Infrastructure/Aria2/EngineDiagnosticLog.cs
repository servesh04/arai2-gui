using System.Text.RegularExpressions;

namespace AriaGui.Infrastructure.Aria2;

internal sealed partial class EngineDiagnosticLog(string path)
{
    private readonly object _sync = new();
    public string Secret { private get; set; } = "";

    public void Write(string category, string message)
    {
        lock (_sync)
        {
            try
            {
                var safe = string.IsNullOrEmpty(Secret) ? message : message.Replace(Secret, "[redacted]", StringComparison.Ordinal);
                safe = UrlPattern().Replace(safe, "[URL redacted]").Replace('\r', ' ').Replace('\n', ' ');
                if (safe.Length > 1024) safe = safe[..1024];
                if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024)
                    File.Move(path, path + ".previous", overwrite: true);
                File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O} [{category}] {safe}{Environment.NewLine}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Logging must not break shutdown. */ }
        }
    }

    [GeneratedRegex(@"(?:https?|ftp|sftp)://[^\s]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlPattern();
}
