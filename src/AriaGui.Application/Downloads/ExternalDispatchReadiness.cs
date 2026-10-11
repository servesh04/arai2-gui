using AriaGui.Application.Engine;

namespace AriaGui.Application.Downloads;

public interface IExternalDispatchReadiness
{ Task WaitForReadyAsync(CancellationToken applicationLifetime); }

/// <summary>Uses the existing lifecycle's verified-ready event; owns no engine start or polling loop.</summary>
public sealed class EngineDispatchReadiness(EngineStateStore store) : IExternalDispatchReadiness
{
    public async Task WaitForReadyAsync(CancellationToken applicationLifetime)
    {
        if (store.Current.Status == EngineStatus.Ready) return;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(object? sender, EngineState state) { if (state.Status == EngineStatus.Ready) ready.TrySetResult(); }
        store.Changed += Changed;
        try
        {
            if (store.Current.Status == EngineStatus.Ready) ready.TrySetResult();
            await ready.Task.WaitAsync(applicationLifetime).ConfigureAwait(false);
        }
        finally { store.Changed -= Changed; }
    }
}
