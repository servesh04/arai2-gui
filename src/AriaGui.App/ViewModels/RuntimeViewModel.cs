using AriaGui.App.Services;
using AriaGui.Application.Downloads;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AriaGui.App.ViewModels;

public abstract class RuntimeViewModel : ObservableObject, IDisposable
{
    private readonly DownloadRuntimeStore _store;
    private readonly IUiDispatcher _dispatcher;
    private long _revision = -1;
    private bool _disposed;
    private readonly object _sync = new();
    private DownloadRuntimeSnapshot? _pending;
    private bool _scheduled;

    protected RuntimeViewModel(DownloadRuntimeStore store, IUiDispatcher dispatcher)
    {
        _store = store;
        _dispatcher = dispatcher;
        _store.Changed += OnStoreChanged;
    }

    protected void Initialize() => Schedule(_store.Current);
    private void OnStoreChanged(object? sender, DownloadRuntimeSnapshot snapshot) => Schedule(snapshot);

    private void Schedule(DownloadRuntimeSnapshot snapshot)
    {
        lock (_sync)
        {
            if (_disposed || snapshot.Revision <= _revision || snapshot.Revision <= (_pending?.Revision ?? -1)) return;
            _pending = snapshot;
            if (_scheduled) return;
            _scheduled = true;
        }
        _dispatcher.Enqueue(() =>
        {
            DownloadRuntimeSnapshot? latest;
            lock (_sync)
            {
                latest = _pending; _pending = null; _scheduled = false;
                if (_disposed || latest is null || latest.Revision <= _revision) return;
                _revision = latest.Revision;
            }
            Apply(latest);
        });
    }

    protected abstract void Apply(DownloadRuntimeSnapshot snapshot);

    public virtual void Dispose()
    {
        lock (_sync) { _disposed = true; _pending = null; }
        _store.Changed -= OnStoreChanged;
        GC.SuppressFinalize(this);
    }
}
