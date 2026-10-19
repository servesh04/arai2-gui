using System.Text.Json;
using AriaGui.Application.Downloads;
using AriaGui.Infrastructure.Aria2;

namespace AriaGui.Infrastructure.Persistence;

public sealed class JsonUserPreferencesRepository(string path) : IUserPreferencesRepository
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public async Task<UserPreferences> LoadAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                if (!File.Exists(path)) return new UserPreferences();
                if (new FileInfo(path).Length > 16384) throw new JsonException();
                var preferences = JsonSerializer.Deserialize<UserPreferences>(File.ReadAllText(path)) ?? throw new JsonException();
                if (preferences.Layout is { } layout) preferences = preferences with
                {
                    Layout = layout.NormalizeSavedColumnWidths().NormalizeSavedPaneSizes()
                };
                preferences.Validate(); return preferences;
            }, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or DownloadRequestException)
        { throw new DownloadRequestException("Saved preferences could not be read. Defaults are in use; the existing file was kept."); }
        finally { _gate.Release(); }
    }
    public async Task SaveAsync(UserPreferences preferences, CancellationToken token)
    {
        preferences.Validate(); await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                UserRuntimeDirectory.EnsurePrivate(Path.GetDirectoryName(Path.GetFullPath(path))!);
                var temporary = path + ".tmp";
                try
                {
                    var json = JsonSerializer.Serialize(preferences);
                    if (System.Text.Encoding.UTF8.GetByteCount(json) > 16384) throw new DownloadRequestException("The saved folder path is too long for preferences.");
                    File.WriteAllText(temporary, json); token.ThrowIfCancellationRequested();
                    File.Move(temporary, path, true);
                }
                finally { File.Delete(temporary); }
            }, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or AriaGui.Application.Engine.EngineException)
        { throw new DownloadRequestException("Preferences could not be saved. Check access to the private app data folder."); }
        finally { _gate.Release(); }
    }
}
