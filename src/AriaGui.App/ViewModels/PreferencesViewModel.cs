using AriaGui.Application.Downloads;
using AriaGui.Application.Notifications;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AriaGui.App.ViewModels;

public sealed class PreferencesViewModel(IUserPreferencesRepository repository, IBrowserIntegrationDiagnostics? browserDiagnostics = null) : ObservableObject
{
    private int _themeIndex; private string _defaultFolder = "", _message = "Theme and default folder apply when you save.";
    private DesktopLayout _layout = new();
    private NotificationPreferences _notificationSettings = new();
    private UserPreferences _saved = new();
    private bool _browserEnabled;
    private bool _browserAutoCapture, _browserStartPaused, _browserAltBypass = true;
    private double _browserMinimumMb;
    private string _browserFileTypes = "", _browserExcludedSites = "";
    public IBrowserHandoffStatus? HandoffStatus { get; set; }
    private string _browserInstallRoot = "", _browserInstallationId = "", _browserStatus = "Browser traffic is disabled pending release verification. Extension presence is unverified.";
    public bool BrowserEnabled { get => _browserEnabled; set => SetProperty(ref _browserEnabled, value); }
    public bool BrowserAutoCapture { get => _browserAutoCapture; set => SetProperty(ref _browserAutoCapture, value); }
    public bool BrowserStartPaused { get => _browserStartPaused; set => SetProperty(ref _browserStartPaused, value); }
    public bool BrowserAltBypass { get => _browserAltBypass; set => SetProperty(ref _browserAltBypass, value); }
    public double BrowserMinimumMb { get => _browserMinimumMb; set => SetProperty(ref _browserMinimumMb, value); }
    public string BrowserFileTypes { get => _browserFileTypes; set => SetProperty(ref _browserFileTypes, value); }
    public string BrowserExcludedSites { get => _browserExcludedSites; set => SetProperty(ref _browserExcludedSites, value); }
    public string BrowserInstallRoot { get => _browserInstallRoot; set => SetProperty(ref _browserInstallRoot, value); }
    public string BrowserInstallationId { get => _browserInstallationId; set => SetProperty(ref _browserInstallationId, value); }
    public string BrowserStatus { get => _browserStatus; private set => SetProperty(ref _browserStatus, value); }
    public bool TorrentPrivacyNoticeAccepted => _saved.TorrentPrivacyNoticeAccepted;
    public NotificationPreferences NotificationSettings
    {
        get => _notificationSettings;
        set => SetProperty(ref _notificationSettings, value);
    }
    public async Task RememberTorrentPrivacyNoticeAsync(CancellationToken token)
    {
        await _saveGate.WaitAsync(token);
        try
        {
            if (_saved.TorrentPrivacyNoticeAccepted) return;
            var updated = _saved with { TorrentPrivacyNoticeAccepted = true };
            await repository.SaveAsync(updated, token);
            _saved = updated;
            OnPropertyChanged(nameof(TorrentPrivacyNoticeAccepted));
        }
        finally { _saveGate.Release(); }
    }
    private BrowserIntegrationPreferences BrowserSettings()
    {
        Guid? installation = null;
        if (BrowserInstallationId.Length > 0)
        {
            if (!Guid.TryParseExact(BrowserInstallationId.Trim(), "D", out var id) || id == Guid.Empty)
                throw new DownloadRequestException("Choose a valid app installation UUID.");
            installation = id;
        }
        if (!double.IsFinite(BrowserMinimumMb) || BrowserMinimumMb < 0 || BrowserMinimumMb > 1_048_576)
            throw new DownloadRequestException("Choose a valid minimum size.");
        var settings = _saved.EffectiveBrowser with
        {
            Enabled = BrowserEnabled, SelectedInstallationId = installation, InstallRoot = BrowserInstallRoot.Trim(),
            AutomaticTakeoverEnabled = BrowserAutoCapture, StartPaused = BrowserStartPaused,
            AltBypass = BrowserAltBypass, MinimumSizeBytes = checked((long)(BrowserMinimumMb * 1_048_576)),
            CaptureFileTypes = SplitList(BrowserFileTypes), ExcludedSites = SplitList(BrowserExcludedSites)
        };
        settings.Validate(); return settings;
    }
    private static string[] SplitList(string value) => value.Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    private IAsyncRelayCommand? _checkBrowser;
    public IAsyncRelayCommand CheckBrowserCommand => _checkBrowser ??= new AsyncRelayCommand(async token =>
    {
        try
        {
            // Inspect selected metadata even while enablement remains disabled; no production transport is composed.
            var result = browserDiagnostics is null ? new(BrowserIntegrationDiagnosticCode.ReleaseBlocked)
                : await browserDiagnostics.CheckAsync(BrowserSettings() with { Enabled = true }, token);
            BrowserStatus = result.Code switch
            {
                BrowserIntegrationDiagnosticCode.HostMissing => "Native host is missing from the selected installation. Repair installation; extension presence is unverified.",
                BrowserIntegrationDiagnosticCode.InstallationNotSelected => "Select an app installation UUID and folder. Extension presence is unverified.",
                BrowserIntegrationDiagnosticCode.VersionMismatch => "App/host protocol version mismatch. Repair both components.",
                BrowserIntegrationDiagnosticCode.AppNotReady => "App hello could not be verified. Open AriaGUI and use the extension's connection test.",
                BrowserIntegrationDiagnosticCode.AppHelloVerified => $"App hello verified ({result.AppVersion}). Browser extension presence is still unverified.",
                _ => "Production browser traffic is disabled pending DNS, browser acceptance and installer trust verification. Registry presence is not a connection test."
            };
        }
        catch (OperationCanceledException) { }
        catch (DownloadRequestException ex) { BrowserStatus = ex.Message; }
        catch { BrowserStatus = "Browser diagnostics unavailable; no connection was verified."; }
    });
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    public Task LayoutSaveTask { get; private set; } = Task.CompletedTask;
    public DesktopLayout Layout => _layout;
    public IReadOnlyList<DownloadColumnLayout> Columns => _layout.EffectiveColumns;
    public bool CompactRows { get => _layout.CompactRows; set => UpdateLayout(_layout with { CompactRows = value }); }
    public bool SidebarVisible { get => _layout.SidebarVisible; set => UpdateLayout(_layout with { SidebarVisible = value }); }
    public bool DetailsVisible { get => _layout.DetailsVisible; set => UpdateLayout(_layout with { DetailsVisible = value }); }
    public bool AnimationsEnabled { get => _layout.AnimationsEnabled; set => UpdateLayout(_layout with { AnimationsEnabled = value }); }
    public double SidebarWidth { get => _layout.SidebarWidth; set => UpdateLayout(_layout with { SidebarWidth = value }); }
    public double DetailsHeight { get => _layout.DetailsHeight; set => UpdateLayout(_layout with { DetailsHeight = value }); }
    public double DetailsWidth { get => _layout.DetailsWidth; set => UpdateLayout(_layout with { DetailsWidth = value }); }
    public void UpdateLayout(DesktopLayout layout)
    {
        layout.Validate(); _layout = layout;
        foreach (var name in new[] { nameof(Layout), nameof(Columns), nameof(CompactRows), nameof(SidebarVisible), nameof(DetailsVisible), nameof(AnimationsEnabled), nameof(SidebarWidth), nameof(DetailsHeight), nameof(DetailsWidth) }) OnPropertyChanged(name);
    }
    public Task SaveLayoutAsync(CancellationToken token = default) => LayoutSaveTask = PersistLayoutAsync(token);
    private async Task PersistLayoutAsync(CancellationToken token)
    {
        var acquired = false;
        try { await _saveGate.WaitAsync(token); acquired = true; var preferences = _saved with { Layout = _layout }; await repository.SaveAsync(preferences, token); _saved = preferences; }
        catch (OperationCanceledException) { }
        catch (DownloadRequestException ex) { Message = ex.Message; }
        catch (Exception) { Message = "Layout could not be saved. Check the app data folder and try again."; }
        finally { if (acquired) _saveGate.Release(); }
    }
    public int ThemeIndex { get => _themeIndex; set => SetProperty(ref _themeIndex, value); }
    public string DefaultFolder { get => _defaultFolder; set => SetProperty(ref _defaultFolder, value); }
    public string Message { get => _message; private set => SetProperty(ref _message, value); }
    public event Action<UserPreferences>? Applied;
    private IAsyncRelayCommand? _save;
    public IAsyncRelayCommand SaveCommand => _save ??= new AsyncRelayCommand(async token =>
    {
        var acquired = false;
        try
        {
            await _saveGate.WaitAsync(token); acquired = true;
            var preferences = _saved with { Theme = (AppTheme)ThemeIndex, DefaultFolder = DefaultFolder.Trim(),
                Layout = _layout, Browser = BrowserSettings(), Notifications = NotificationSettings };
            await repository.SaveAsync(preferences, token); _saved = preferences; Applied?.Invoke(preferences); Message = "Saved. The folder applies to new downloads.";
        }
        catch (OperationCanceledException) { }
        catch (DownloadRequestException ex) { Message = ex.Message; }
        catch (Exception) { Message = "Preferences could not be saved. Check the app data folder and try again."; }
        finally { if (acquired) _saveGate.Release(); }
    });
    public async Task LoadAsync(CancellationToken token)
    {
        try
        {
            var preferences = await repository.LoadAsync(token); ThemeIndex = (int)preferences.Theme; DefaultFolder = preferences.DefaultFolder;
            _saved = preferences; OnPropertyChanged(nameof(TorrentPrivacyNoticeAccepted)); UpdateLayout(preferences.EffectiveLayout);
            NotificationSettings = preferences.EffectiveNotifications;
            BrowserEnabled = preferences.EffectiveBrowser.Enabled; BrowserInstallRoot = preferences.EffectiveBrowser.InstallRoot;
            BrowserAutoCapture = preferences.EffectiveBrowser.AutomaticTakeoverEnabled;
            BrowserStartPaused = preferences.EffectiveBrowser.StartPaused;
            BrowserAltBypass = preferences.EffectiveBrowser.AltBypass;
            BrowserMinimumMb = preferences.EffectiveBrowser.MinimumSizeBytes / 1_048_576d;
            BrowserFileTypes = string.Join(", ", preferences.EffectiveBrowser.EffectiveCaptureFileTypes);
            BrowserExcludedSites = string.Join(", ", preferences.EffectiveBrowser.EffectiveExcludedSites);
            BrowserInstallationId = preferences.EffectiveBrowser.SelectedInstallationId?.ToString("D") ?? "";
            Applied?.Invoke(preferences);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (DownloadRequestException ex) { Message = ex.Message; }
        catch (Exception) { Message = "Preferences are unavailable. Default settings are in use."; }
    }
}
