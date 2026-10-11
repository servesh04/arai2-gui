namespace AriaGui.Application.Engine;

public sealed class EngineStateStore
{
    private readonly object _sync = new();
    private EngineState _current = new(0, EngineStatus.Stopped, "Engine stopped");
    public EngineState Current => Volatile.Read(ref _current);
    public event EventHandler<EngineState>? Changed;

    internal void Publish(EngineStatus status, string message, string? version = null, EngineFailure? failure = null)
    {
        EngineState snapshot;
        lock (_sync)
        {
            snapshot = new(_current.Revision + 1, status, message, version, failure);
            Volatile.Write(ref _current, snapshot);
        }
        Changed?.Invoke(this, snapshot);
    }
}
