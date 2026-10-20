namespace AriaGui.Application.Downloads;

public sealed record BrowserIntegrationPreferences(bool Enabled = false, Guid? SelectedInstallationId = null, string InstallRoot = "", bool AutomaticTakeoverEnabled = false,
    bool StartPaused = false, bool AltBypass = true, long MinimumSizeBytes = 0, string[]? CaptureFileTypes = null, string[]? ExcludedSites = null)
{
    public IReadOnlyList<string> EffectiveCaptureFileTypes => CaptureFileTypes ?? [];
    public IReadOnlyList<string> EffectiveExcludedSites => ExcludedSites ?? [];
    public void Validate()
    {
        if (SelectedInstallationId == Guid.Empty || InstallRoot is null || InstallRoot.Length > 1024
            || (InstallRoot.Length > 0 && (!Path.IsPathFullyQualified(InstallRoot) || InstallRoot.StartsWith("\\\\", StringComparison.Ordinal)
                || InstallRoot.Any(char.IsControl)))
            || MinimumSizeBytes is < 0 or > 1_099_511_627_776
            || CaptureFileTypes is { Length: > 64 } || ExcludedSites is { Length: > 128 }
            || EffectiveCaptureFileTypes.Any(type => type is null || type.Length is < 2 or > 20 || type[0] != '.'
                || !type.AsSpan(1).ToString().All(char.IsLetterOrDigit))
            || EffectiveExcludedSites.Any(site => site is null || site.Length is < 1 or > 255
                || Uri.CheckHostName(site) is not (UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6)))
            throw new DownloadRequestException("Check the browser integration folder, file types, site exclusions, and size.");
    }
}
public enum BrowserIntegrationDiagnosticCode { Disabled, InstallationNotSelected, HostMissing, ReleaseBlocked, AppNotReady, VersionMismatch, AppHelloVerified }
public sealed record BrowserIntegrationDiagnostic(BrowserIntegrationDiagnosticCode Code, string? AppVersion = null);
public interface IBrowserIntegrationDiagnostics
{
    Task<BrowserIntegrationDiagnostic> CheckAsync(BrowserIntegrationPreferences preferences, CancellationToken token);
}
