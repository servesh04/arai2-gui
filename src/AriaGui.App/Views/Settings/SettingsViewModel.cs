using AriaGui.App.Features;
using AriaGui.App.ViewModels;
using AriaGui.Application.Downloads;
using AriaGui.Application.Notifications;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Controls;

namespace AriaGui.App.Views.Settings;

public sealed class SettingsViewModel : ObservableObject
{
    private readonly PreferencesViewModel _preferences;
    private bool _saving;
    private string _saveStatus = string.Empty;
    private string _folderStatus = string.Empty;

    public SettingsViewModel(PreferencesViewModel preferences, FeatureFlags features)
    {
        _preferences = preferences;
        Features = features;
        ValidateFolder(preferences.DefaultFolder);
    }

    public FeatureFlags Features { get; }
    public bool BrowserHandoffAvailable => Features.BrowserHandoff;
    public bool SpeedPresetsAvailable => Features.SpeedPresets;
    public string SaveStatus { get => _saveStatus; private set => SetProperty(ref _saveStatus, value); }
    public string FolderStatus { get => _folderStatus; private set => SetProperty(ref _folderStatus, value); }
    public string BrowserStatus => (App.Current as App)?.BrowserHandoffStartupUnavailable == true
        ? "Browser handoff is unavailable; browser downloads continue normally."
        : !HandoffRegistration.IsRegistered() ? "Host not registered"
        : _preferences.HandoffStatus?.Connected == true ? "Connected to browser extension"
        : "Not connected · open the extension popup to check";
    public InfoBarSeverity BrowserStatusSeverity => (App.Current as App)?.BrowserHandoffStartupUnavailable == true
        ? InfoBarSeverity.Warning : !HandoffRegistration.IsRegistered() ? InfoBarSeverity.Error
        : _preferences.HandoffStatus?.Connected == true ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
    public bool BrowserHostRegistered => HandoffRegistration.RegisteredAppPath() is not null;
    public bool BrowserHostUnregistered => !HandoffRegistration.IsRegistered();
    public bool IsSaving { get => _saving; private set => SetProperty(ref _saving, value); }

    public bool NotificationDesktopEnabled
    {
        get => _preferences.NotificationSettings.DesktopEnabled;
        set => SetNotificationSetting(value, settings => settings.DesktopEnabled,
            (settings, enabled) => settings with { DesktopEnabled = enabled }, nameof(NotificationDesktopEnabled));
    }
    public bool NotificationCompletedEnabled
    {
        get => _preferences.NotificationSettings.CompletedEnabled;
        set => SetNotificationSetting(value, settings => settings.CompletedEnabled,
            (settings, enabled) => settings with { CompletedEnabled = enabled }, nameof(NotificationCompletedEnabled));
    }
    public bool NotificationFailedEnabled
    {
        get => _preferences.NotificationSettings.FailedEnabled;
        set => SetNotificationSetting(value, settings => settings.FailedEnabled,
            (settings, enabled) => settings with { FailedEnabled = enabled }, nameof(NotificationFailedEnabled));
    }
    public bool NotificationEngineEnabled
    {
        get => _preferences.NotificationSettings.EngineAttentionEnabled;
        set => SetNotificationSetting(value, settings => settings.EngineAttentionEnabled,
            (settings, enabled) => settings with { EngineAttentionEnabled = enabled }, nameof(NotificationEngineEnabled));
    }
    public bool NotificationHideFileNames
    {
        get => _preferences.NotificationSettings.HideFileNames;
        set => SetNotificationSetting(value, settings => settings.HideFileNames,
            (settings, enabled) => settings with { HideFileNames = enabled }, nameof(NotificationHideFileNames));
    }
    public bool NotificationOnlyWhenInactive
    {
        get => _preferences.NotificationSettings.OnlyWhenInactive;
        set => SetNotificationSetting(value, settings => settings.OnlyWhenInactive,
            (settings, enabled) => settings with { OnlyWhenInactive = enabled }, nameof(NotificationOnlyWhenInactive));
    }

    private void SetNotificationSetting(bool value, Func<NotificationPreferences, bool> read,
        Func<NotificationPreferences, bool, NotificationPreferences> update, string property)
    {
        var old = _preferences.NotificationSettings;
        if (read(old) == value) return;
        _preferences.NotificationSettings = update(old, value);
        OnPropertyChanged(property);
        _ = SaveOrRevertAsync(() => _preferences.NotificationSettings = old, property);
    }

    public int ThemeIndex
    {
        get => _preferences.ThemeIndex;
        set { if (value == _preferences.ThemeIndex) return; var old = _preferences.ThemeIndex; _preferences.ThemeIndex = value; OnPropertyChanged(); _ = SaveOrRevertAsync(() => _preferences.ThemeIndex = old, nameof(ThemeIndex)); }
    }

    public bool CompactRows
    {
        get => _preferences.CompactRows;
        set { if (value == _preferences.CompactRows) return; var old = _preferences.CompactRows; _preferences.CompactRows = value; OnPropertyChanged(); _ = SaveOrRevertAsync(() => _preferences.CompactRows = old, nameof(CompactRows)); }
    }

    public bool AnimationsEnabled
    {
        get => _preferences.AnimationsEnabled;
        set { if (value == _preferences.AnimationsEnabled) return; var old = _preferences.AnimationsEnabled; _preferences.AnimationsEnabled = value; OnPropertyChanged(); _ = SaveOrRevertAsync(() => _preferences.AnimationsEnabled = old, nameof(AnimationsEnabled)); }
    }

    public string DefaultFolder
    {
        get => _preferences.DefaultFolder;
        set
        {
            if (value == _preferences.DefaultFolder) return;
            var old = _preferences.DefaultFolder;
            _preferences.DefaultFolder = value;
            OnPropertyChanged();
            if (ValidateFolder(value)) _ = SaveOrRevertAsync(() => _preferences.DefaultFolder = old, nameof(DefaultFolder));
        }
    }

    public bool BrowserEnabled
    {
        get => _preferences.BrowserEnabled;
        set { if (!BrowserHandoffAvailable || value == _preferences.BrowserEnabled) return; var old = _preferences.BrowserEnabled; _preferences.BrowserEnabled = value; OnPropertyChanged(); _ = SaveOrRevertAsync(() => _preferences.BrowserEnabled = old, nameof(BrowserEnabled)); }
    }

    public bool BrowserAutoCapture
    {
        get => _preferences.BrowserAutoCapture;
        set { if (value == _preferences.BrowserAutoCapture) return; var old = _preferences.BrowserAutoCapture; _preferences.BrowserAutoCapture = value; OnPropertyChanged(); _ = SaveOrRevertAsync(() => _preferences.BrowserAutoCapture = old, nameof(BrowserAutoCapture)); }
    }
    public bool BrowserAltBypass
    {
        get => _preferences.BrowserAltBypass;
        set { if (value == _preferences.BrowserAltBypass) return; var old = _preferences.BrowserAltBypass; _preferences.BrowserAltBypass = value; OnPropertyChanged(); _ = SaveOrRevertAsync(() => _preferences.BrowserAltBypass = old, nameof(BrowserAltBypass)); }
    }
    public bool BrowserStartPaused
    {
        get => _preferences.BrowserStartPaused;
        set { if (value == _preferences.BrowserStartPaused) return; var old = _preferences.BrowserStartPaused; _preferences.BrowserStartPaused = value; OnPropertyChanged(); _ = SaveOrRevertAsync(() => _preferences.BrowserStartPaused = old, nameof(BrowserStartPaused)); }
    }
    public double BrowserMinimumMb
    {
        get => _preferences.BrowserMinimumMb;
        set { if (value == _preferences.BrowserMinimumMb) return; var old = _preferences.BrowserMinimumMb; _preferences.BrowserMinimumMb = value; OnPropertyChanged(); _ = SaveOrRevertAsync(() => _preferences.BrowserMinimumMb = old, nameof(BrowserMinimumMb)); }
    }
    public string BrowserFileTypes
    {
        get => _preferences.BrowserFileTypes;
        set { if (value == _preferences.BrowserFileTypes) return; var old = _preferences.BrowserFileTypes; _preferences.BrowserFileTypes = value; OnPropertyChanged(); _ = SaveOrRevertAsync(() => _preferences.BrowserFileTypes = old, nameof(BrowserFileTypes)); }
    }
    public string BrowserExcludedSites
    {
        get => _preferences.BrowserExcludedSites;
        set { if (value == _preferences.BrowserExcludedSites) return; var old = _preferences.BrowserExcludedSites; _preferences.BrowserExcludedSites = value; OnPropertyChanged(); _ = SaveOrRevertAsync(() => _preferences.BrowserExcludedSites = old, nameof(BrowserExcludedSites)); }
    }

    public void RefreshBrowserStatus()
    {
        OnPropertyChanged(nameof(BrowserStatus));
        OnPropertyChanged(nameof(BrowserStatusSeverity));
        OnPropertyChanged(nameof(BrowserHostRegistered));
        OnPropertyChanged(nameof(BrowserHostUnregistered));
    }
    public Task RegisterBrowserHostAsync()
    {
        try
        {
            HandoffRegistration.Register(Path.Combine(AppContext.BaseDirectory, "host", "AriaGui.NativeHost.exe"),
                Environment.ProcessPath ?? throw new IOException("App path unavailable."));
            SaveStatus = "Browser host registered";
        }
        catch { SaveStatus = "Browser host registration failed. Check the app installation folder."; }
        RefreshBrowserStatus();
        return Task.CompletedTask;
    }
    public Task UnregisterBrowserHostAsync()
    {
        SaveStatus = HandoffRegistration.Unregister() ? "Browser host unregistered" : "Browser host could not be unregistered";
        RefreshBrowserStatus();
        return Task.CompletedTask;
    }

    public async Task ResetSectionAsync(string section)
    {
        switch (section)
        {
            case "General": DefaultFolder = string.Empty; break;
            case "Appearance": ThemeIndex = 0; CompactRows = false; AnimationsEnabled = true; break;
            case "Integration" when BrowserHandoffAvailable: BrowserEnabled = false; BrowserAutoCapture = false;
                BrowserAltBypass = true; BrowserStartPaused = false; BrowserMinimumMb = 0;
                BrowserFileTypes = string.Empty; BrowserExcludedSites = string.Empty; break;
            case "Notifications":
                var old = _preferences.NotificationSettings;
                _preferences.NotificationSettings = new NotificationPreferences();
                NotifyNotificationSettings();
                await SaveOrRevertAsync(() => { _preferences.NotificationSettings = old; NotifyNotificationSettings(); },
                    nameof(NotificationDesktopEnabled));
                break;
        }
        await Task.CompletedTask;
    }

    private bool ValidateFolder(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            FolderStatus = "The app default Downloads folder will be used.";
            return true;
        }
        if (!Directory.Exists(value))
        {
            FolderStatus = "Choose an existing, writable folder.";
            return false;
        }
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(value));
            var free = root is null ? 0 : new DriveInfo(root).AvailableFreeSpace;
            FolderStatus = $"Writable · {free / 1_073_741_824d:0.#} GB free";
            return true;
        }
        catch
        {
            FolderStatus = "The folder could not be checked.";
            return false;
        }
    }

    private void NotifyNotificationSettings()
    {
        foreach (var property in new[] { nameof(NotificationDesktopEnabled), nameof(NotificationCompletedEnabled),
            nameof(NotificationFailedEnabled), nameof(NotificationEngineEnabled), nameof(NotificationHideFileNames),
            nameof(NotificationOnlyWhenInactive) }) OnPropertyChanged(property);
    }

    private async Task SaveOrRevertAsync(Action revert, string property)
    {
        IsSaving = true;
        SaveStatus = "Saving…";
        await _preferences.SaveCommand.ExecuteAsync(null);
        IsSaving = false;
        if (_preferences.Message.StartsWith("Saved", StringComparison.OrdinalIgnoreCase)) SaveStatus = "Saved";
        else
        {
            revert();
            OnPropertyChanged(property);
            SaveStatus = _preferences.Message;
        }
    }
}
