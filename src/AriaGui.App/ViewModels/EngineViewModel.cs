using AriaGui.App.Services;
using AriaGui.Application.Engine;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AriaGui.App.ViewModels;

public sealed class EngineViewModel : ObservableObject, IDisposable
{
    private readonly EngineStateStore _store;
    private readonly IUiDispatcher _dispatcher;
    private long _revision = -1;
    private bool _disposed;
    private string _message = "Starting aria2…";
    private string _version = "";
    private EngineStatus _status;
    private EngineFailure? _failure;
    private readonly bool _legacyTransitionSupported;

    public EngineViewModel(EngineLifecycleService lifecycle, EngineStateStore store, IUiDispatcher dispatcher)
    {
        _store = store;
        _dispatcher = dispatcher;
        _legacyTransitionSupported = lifecycle.SupportsLegacyTransition;
        StartCommand = new AsyncRelayCommand(lifecycle.StartAsync, () => !IsBusy && !IsReady);
        CheckCommand = new AsyncRelayCommand(lifecycle.CheckHealthAsync, () => IsReady && !IsBusy);
        StopLegacyCommand = new AsyncRelayCommand(lifecycle.StopLegacyDirectAsync,
            () => _legacyTransitionSupported && !IsBusy && _status == EngineStatus.Failed && _failure == EngineFailure.AlreadyRunning);
        _store.Changed += OnChanged;
        Schedule(store.Current);
    }

    public IAsyncRelayCommand StartCommand { get; }
    public IAsyncRelayCommand CheckCommand { get; }
    public IAsyncRelayCommand StopLegacyCommand { get; }
    public string Message { get => _message; private set => SetProperty(ref _message, value); }
    public string Version { get => _version; private set => SetProperty(ref _version, value); }
    public bool IsBusy => _status is EngineStatus.Starting or EngineStatus.Stopping;
    public bool IsReady => _status == EngineStatus.Ready;
    public string StatusLabel => _status.ToString();

    private void OnChanged(object? sender, EngineState state) => Schedule(state);
    private void Schedule(EngineState state) => _dispatcher.Enqueue(() =>
    {
        if (_disposed || state.Revision <= _revision) return;
        _revision = state.Revision;
        _status = state.Status;
        _failure = state.Failure;
        Message = state.Message;
        Version = state.Version is null ? "" : $"aria2 {state.Version}";
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(IsReady));
        OnPropertyChanged(nameof(StatusLabel));
        StartCommand.NotifyCanExecuteChanged();
        CheckCommand.NotifyCanExecuteChanged();
        StopLegacyCommand.NotifyCanExecuteChanged();
    });

    public void CancelOperations() { StartCommand.Cancel(); CheckCommand.Cancel(); StopLegacyCommand.Cancel(); }

    public void Dispose()
    {
        _disposed = true;
        CancelOperations();
        _store.Changed -= OnChanged;
        GC.SuppressFinalize(this);
    }
}
