using AriaGui.App.Features;
using AriaGui.App.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;

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
    public string BrowserStatus => _preferences.BrowserStatus;
    public bool IsSaving { get => _saving; private set => SetProperty(ref _saving, value); }

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
        set { if (!Features.BrowserHandoff || value == _preferences.BrowserEnabled) return; var old = _preferences.BrowserEnabled; _preferences.BrowserEnabled = value; OnPropertyChanged(); _ = SaveOrRevertAsync(() => _preferences.BrowserEnabled = old, nameof(BrowserEnabled)); }
    }

    public async Task CheckBrowserAsync()
    {
        if (_preferences.CheckBrowserCommand.CanExecute(null)) await _preferences.CheckBrowserCommand.ExecuteAsync(null);
        OnPropertyChanged(nameof(BrowserStatus));
    }

    public async Task ResetSectionAsync(string section)
    {
        switch (section)
        {
            case "General": DefaultFolder = string.Empty; break;
            case "Appearance": ThemeIndex = 0; CompactRows = false; break;
            case "Integration" when Features.BrowserHandoff: BrowserEnabled = false; break;
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
