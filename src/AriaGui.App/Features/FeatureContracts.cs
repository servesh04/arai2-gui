namespace AriaGui.App.Features;

public enum EngineControlState { Ready, Reconnecting, Stopped }
public sealed record SpeedLimitPreset(string Name, long? BytesPerSecond);
public sealed record HandoffAddedEventArgs(Guid ItemId, string Name, bool AddedPaused);

public interface IEngineControl
{
    EngineControlState State { get; }
    event EventHandler? StateChanged { add { } remove { } }
    Task<bool> RestartAsync(CancellationToken cancellationToken = default);
}

public interface ISpeedLimitService
{
    IReadOnlyList<SpeedLimitPreset> Presets { get; }
    SpeedLimitPreset? Current { get; }
    Task<bool> SetAsync(SpeedLimitPreset preset, CancellationToken cancellationToken = default);
}

public interface IHandoffNotifier
{
    event EventHandler<HandoffAddedEventArgs>? AddedFromBrowser;
    Task<bool> UndoAsync(Guid itemId, CancellationToken cancellationToken = default);
}

public sealed record FeatureFlags(bool SpeedPresets, bool EngineRestart, bool FailureReasons, bool BrowserHandoff)
{
    public static FeatureFlags Unsupported { get; } = new(false, false, false, false);
    public static FeatureFlags BackendImplemented { get; } = new(true, true, true, true);
}

public sealed class UnsupportedEngineControl : IEngineControl
{
    public EngineControlState State => EngineControlState.Stopped;
    public Task<bool> RestartAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
}

public sealed class UnsupportedSpeedLimitService : ISpeedLimitService
{
    public IReadOnlyList<SpeedLimitPreset> Presets => [];
    public SpeedLimitPreset? Current => null;
    public Task<bool> SetAsync(SpeedLimitPreset preset, CancellationToken cancellationToken = default) => Task.FromResult(false);
}

public sealed class UnsupportedHandoffNotifier : IHandoffNotifier
{
    public event EventHandler<HandoffAddedEventArgs>? AddedFromBrowser { add { } remove { } }
    public Task<bool> UndoAsync(Guid itemId, CancellationToken cancellationToken = default) => Task.FromResult(false);
}
