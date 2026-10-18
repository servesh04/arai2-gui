using AriaGui.App.Services;
using AriaGui.Application.Downloads;
using AriaGui.Application.Engine;

namespace AriaGui.App.Features;

public sealed class EngineControlService : IEngineControl, IDisposable
{
    private readonly EngineLifecycleService _lifecycle;
    private readonly EngineStateStore _store;
    private readonly DownloadCoordinator _downloads;
    private readonly IUiDispatcher _dispatcher;

    public EngineControlService(EngineLifecycleService lifecycle, EngineStateStore store,
        DownloadCoordinator downloads, IUiDispatcher dispatcher)
    {
        _lifecycle = lifecycle; _store = store; _downloads = downloads; _dispatcher = dispatcher;
        _store.Changed += StoreChanged;
    }

    public EngineControlState State => EngineControlStateMachine.Derive(_store.Current) switch
    {
        ObservableEngineState.Ready => EngineControlState.Ready,
        ObservableEngineState.Reconnecting => EngineControlState.Reconnecting,
        _ => EngineControlState.Stopped
    };

    public event EventHandler? StateChanged;

    public async Task<bool> RestartAsync(CancellationToken cancellationToken = default)
    {
        if (!await _lifecycle.RestartAsync(cancellationToken).ConfigureAwait(false)) return false;
        await _downloads.RefreshAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private void StoreChanged(object? sender, EngineState state) => _dispatcher.Enqueue(() => StateChanged?.Invoke(this, EventArgs.Empty));
    public void Dispose() => _store.Changed -= StoreChanged;
}

public sealed class SpeedLimitService : ISpeedLimitService, IDisposable
{
    private readonly DownloadCoordinator _downloads;
    private readonly DownloadRuntimeStore _store;
    private readonly IUserPreferencesRepository _preferences;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SpeedLimitPreferences _settings = new();
    private IReadOnlyList<SpeedLimitPreset> _presets;

    public SpeedLimitService(DownloadCoordinator downloads, DownloadRuntimeStore store, IUserPreferencesRepository preferences)
    {
        _downloads = downloads; _store = store; _preferences = preferences;
        _presets = BuildPresets(_settings);
        _store.Changed += StoreChanged;
    }

    public IReadOnlyList<SpeedLimitPreset> Presets => _presets;
    public SpeedLimitPreset? Current
    {
        get
        {
            var actual = _store.Current.BandwidthPolicy?.Confirmed.Download.BytesPerSecond;
            if (actual is null && _settings.SelectedPreset is "Unlimited" or "Standard")
                return _presets.FirstOrDefault(value => value.Name == _settings.SelectedPreset);
            return _presets.FirstOrDefault(value => value.BytesPerSecond == actual)
                ?? new SpeedLimitPreset("Custom", actual);
        }
    }

    public async Task<bool> SetAsync(SpeedLimitPreset preset, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preset);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var preferences = await _preferences.LoadAsync(cancellationToken).ConfigureAwait(false);
            var settings = preferences.EffectiveSpeedLimits;
            var available = SpeedLimitPresetPolicy.Build(settings);
            var selected = available.SingleOrDefault(value => value.Name == preset.Name)
                ?? throw new ArgumentException("Choose a known speed preset.", nameof(preset));
            var rate = preset.Name == "Custom" ? preset.BytesPerSecond ?? settings.CustomBytesPerSecond : selected.BytesPerSecond;
            SpeedLimitPresetPolicy.ValidateRate(rate);
            await _downloads.SetBandwidthProfileAsync(new(preset.Name, new(rate), ByteRateLimit.Unlimited), cancellationToken).ConfigureAwait(false);
            settings = settings with { SelectedPreset = preset.Name,
                CustomBytesPerSecond = preset.Name == "Custom" ? rate!.Value : settings.CustomBytesPerSecond };
            await _preferences.SaveAsync(preferences with { SpeedLimits = settings }, cancellationToken).ConfigureAwait(false);
            _settings = settings; _presets = BuildPresets(settings);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (ArgumentException) { throw; }
        catch { return false; }
        finally { _gate.Release(); }
    }

    private static IReadOnlyList<SpeedLimitPreset> BuildPresets(SpeedLimitPreferences settings) =>
        SpeedLimitPresetPolicy.Build(settings).Select(value => new SpeedLimitPreset(value.Name, value.BytesPerSecond)).ToArray();
    private void StoreChanged(object? sender, DownloadRuntimeSnapshot state) { }
    public void Dispose() { _store.Changed -= StoreChanged; _gate.Dispose(); }
}
