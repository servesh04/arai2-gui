using AriaGui.Application.Downloads;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AriaGui.App.ViewModels;

public sealed class PreferencesViewModel(IUserPreferencesRepository repository) : ObservableObject
{
    private int _themeIndex; private string _defaultFolder = "", _message = "Theme and default folder apply when you save.";
    private DesktopLayout _layout = new();
    private UserPreferences _saved = new();
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
    public void UpdateLayout(DesktopLayout layout)
    {
        layout.Validate(); _layout = layout;
        foreach (var name in new[] { nameof(Layout), nameof(Columns), nameof(CompactRows), nameof(SidebarVisible), nameof(DetailsVisible), nameof(AnimationsEnabled), nameof(SidebarWidth), nameof(DetailsHeight) }) OnPropertyChanged(name);
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
            var preferences = new UserPreferences((AppTheme)ThemeIndex, DefaultFolder.Trim(), _layout);
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
            _saved = preferences; UpdateLayout(preferences.EffectiveLayout);
            Applied?.Invoke(preferences);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (DownloadRequestException ex) { Message = ex.Message; }
        catch (Exception) { Message = "Preferences are unavailable. Default settings are in use."; }
    }
}
