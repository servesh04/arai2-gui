using AriaGui.Application.Downloads;
using AriaGui.Browser.Contracts;

namespace AriaGui.Infrastructure.Browser;

// Production composition supplies no hello transport. Registry presence is never connectivity evidence.
// A later verified installer may supply a trusted hello transport; tests inject closed contract replies only.
public sealed class BrowserIntegrationDiagnostics(Func<Guid, Guid, CancellationToken, Task<ResponseEnvelope>>? hello = null) : IBrowserIntegrationDiagnostics
{
    public async Task<BrowserIntegrationDiagnostic> CheckAsync(BrowserIntegrationPreferences preferences, CancellationToken token)
    {
        preferences.Validate();
        if (!preferences.Enabled) return new(BrowserIntegrationDiagnosticCode.Disabled);
        if (preferences.SelectedInstallationId is null || preferences.InstallRoot.Length == 0) return new(BrowserIntegrationDiagnosticCode.InstallationNotSelected);
        if (!File.Exists(Path.Combine(preferences.InstallRoot, "host", "AriaGui.NativeHost.exe"))) return new(BrowserIntegrationDiagnosticCode.HostMissing);
        if (hello is null) return new(BrowserIntegrationDiagnosticCode.ReleaseBlocked);
        var requestId = Guid.NewGuid();
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(5));
            var response = await hello(requestId, preferences.SelectedInstallationId.Value, deadline.Token).WaitAsync(TimeSpan.FromSeconds(5), token);
            if (response.RequestId != requestId || response.Protocol.Major != 1) return new(BrowserIntegrationDiagnosticCode.VersionMismatch);
            // Use the same strict closed response parser as the host, even for an injected transport.
            var parsed = ContractJson.ParseResponse(ContractJson.Serialize(response));
            if (!parsed.Success) return new(BrowserIntegrationDiagnosticCode.VersionMismatch);
            var result = parsed.Value!.Result;
            if (result.Code != ResultCode.Ready) return new(BrowserIntegrationDiagnosticCode.AppNotReady);
            if (!Version.TryParse(result.AppVersion, out var version) || version.Major != 1 || result.MaxMessageBytes != ProtocolLimits.MaximumMessageBytes
                || result.Capabilities is null || !result.Capabilities.Contains(Capability.AddPublicGet) || !result.Capabilities.Contains(Capability.GetRequestStatus))
                return new(BrowserIntegrationDiagnosticCode.VersionMismatch);
            return new(BrowserIntegrationDiagnosticCode.AppHelloVerified, result.AppVersion);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { return new(BrowserIntegrationDiagnosticCode.AppNotReady); }
    }
}
