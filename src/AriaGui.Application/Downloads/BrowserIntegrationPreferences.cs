namespace AriaGui.Application.Downloads;

public sealed record BrowserIntegrationPreferences(bool Enabled = false, Guid? SelectedInstallationId = null, string InstallRoot = "", bool AutomaticTakeoverEnabled = false)
{
    public void Validate()
    {
        if (SelectedInstallationId == Guid.Empty || InstallRoot is null || InstallRoot.Length > 1024
            || (InstallRoot.Length > 0 && (!Path.IsPathFullyQualified(InstallRoot) || InstallRoot.StartsWith("\\\\", StringComparison.Ordinal)
                || InstallRoot.Any(char.IsControl))))
            throw new DownloadRequestException("Choose a valid app installation UUID and absolute local installation folder.");
    }
}
public enum BrowserIntegrationDiagnosticCode { Disabled, InstallationNotSelected, HostMissing, ReleaseBlocked, AppNotReady, VersionMismatch, AppHelloVerified }
public sealed record BrowserIntegrationDiagnostic(BrowserIntegrationDiagnosticCode Code, string? AppVersion = null);
public interface IBrowserIntegrationDiagnostics
{
    Task<BrowserIntegrationDiagnostic> CheckAsync(BrowserIntegrationPreferences preferences, CancellationToken token);
}
