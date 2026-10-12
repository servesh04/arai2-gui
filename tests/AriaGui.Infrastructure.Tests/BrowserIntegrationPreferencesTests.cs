using AriaGui.Application.Downloads;
using AriaGui.Browser.Contracts;
using AriaGui.Infrastructure.Browser;
using AriaGui.Infrastructure.Persistence;

namespace AriaGui.Infrastructure.Tests;

public sealed class BrowserIntegrationPreferencesTests
{
    [Fact]
    public async Task LegacyJsonDefaultsDisabledAndNewMetadataRoundTripsWithoutSecrets()
    {
        var root = Path.Combine(Path.GetTempPath(), "AriaGUI.BrowserPrefsTests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "preferences.json"); await File.WriteAllTextAsync(path, "{\"Theme\":2,\"DefaultFolder\":\"\"}");
            var repository = new JsonUserPreferencesRepository(path); var old = await repository.LoadAsync(default);
            Assert.False(old.EffectiveBrowser.Enabled); Assert.Null(old.EffectiveBrowser.SelectedInstallationId);
            var browser = new BrowserIntegrationPreferences(false, Guid.NewGuid(), Path.Combine(root, "space 日本語"));
            await repository.SaveAsync(old with { Browser = browser }, default); var saved = await repository.LoadAsync(default);
            Assert.Equal(browser, saved.EffectiveBrowser); Assert.Equal(old.Theme, saved.Theme); Assert.Equal(old.DefaultFolder, saved.DefaultFolder);
            Assert.Throws<DownloadRequestException>(() => (browser with { SelectedInstallationId = Guid.Empty }).Validate());
            Assert.Throws<DownloadRequestException>(() => (browser with { InstallRoot = "relative" }).Validate());
            Assert.Throws<DownloadRequestException>(() => (browser with { InstallRoot = @"\\server\share" }).Validate());
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public async Task RegistryOrExecutablePresenceNeverMeansConnectedAndProductionProbeIsBlocked()
    {
        var root = Path.Combine(Path.GetTempPath(), "AriaGUI.BrowserPrefsTests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Path.Combine(root, "host"));
        try
        {
            var service = new BrowserIntegrationDiagnostics(); var settings = new BrowserIntegrationPreferences(true, Guid.NewGuid(), root);
            Assert.Equal(BrowserIntegrationDiagnosticCode.HostMissing, (await service.CheckAsync(settings, default)).Code);
            await File.WriteAllTextAsync(Path.Combine(root, "host", "AriaGui.NativeHost.exe"), "not executed");
            Assert.Equal(BrowserIntegrationDiagnosticCode.ReleaseBlocked, (await service.CheckAsync(settings, default)).Code);
            Assert.Equal(BrowserIntegrationDiagnosticCode.Disabled, (await service.CheckAsync(settings with { Enabled = false }, default)).Code);
            Assert.Equal(BrowserIntegrationDiagnosticCode.InstallationNotSelected, (await service.CheckAsync(settings with { SelectedInstallationId = null }, default)).Code);
        }
        finally { Directory.Delete(root, true); }
    }
    [Theory]
    [InlineData("ready", BrowserIntegrationDiagnosticCode.AppHelloVerified)]
    [InlineData("unavailable", BrowserIntegrationDiagnosticCode.AppNotReady)]
    [InlineData("version", BrowserIntegrationDiagnosticCode.VersionMismatch)]
    [InlineData("correlation", BrowserIntegrationDiagnosticCode.VersionMismatch)]
    [InlineData("caps", BrowserIntegrationDiagnosticCode.VersionMismatch)]
    public async Task ReadyRequiresCorrelatedCompatibleClosedHello(string outcome, BrowserIntegrationDiagnosticCode expected)
    {
        var root = Path.Combine(Path.GetTempPath(), "AriaGUI.BrowserPrefsTests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Path.Combine(root, "host"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "host", "AriaGui.NativeHost.exe"), "not executed");
            var selectedInstallation = Guid.NewGuid();
            var service = new BrowserIntegrationDiagnostics((id, installation, _) => { Assert.Equal(selectedInstallation, installation); return Task.FromResult(new ResponseEnvelope(new(1, 0), outcome == "correlation" ? Guid.NewGuid() : id,
                outcome == "unavailable" ? new(ResultCode.Unavailable) : new(ResultCode.Ready, AppVersion: outcome == "version" ? "2.0.0" : "1.0.0",
                    MaxMessageBytes: 65536, Capabilities: outcome == "caps" ? [Capability.AddPublicGet] : [Capability.AddPublicGet, Capability.GetRequestStatus]))); });
            Assert.Equal(expected, (await service.CheckAsync(new(true, selectedInstallation, root), default)).Code);
        }
        finally { Directory.Delete(root, true); }
    }
}
