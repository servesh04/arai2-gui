using AriaGui.Application.Downloads;
using AriaGui.Core.Downloads;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AriaGui.App.ViewModels;

public sealed class DownloadItemViewModel : ObservableObject
{
    private DownloadEntry _entry;
    private bool _connected;
    public DownloadItemViewModel(DownloadEntry entry, Func<Guid, DownloadAction, CancellationToken, Task>? execute = null,
        Func<Guid, int, CancellationToken, Task>? move = null, Func<string, bool, CancellationToken, Task>? open = null)
    {
        _entry = entry;
        OpenFileCommand = new AsyncRelayCommand(token => open is null ? Task.CompletedTask : open(Destination, false, token),
            () => open is not null && _entry.Progress.Status == DownloadStatus.Completed && _entry.PendingOperation is null);
        OpenFolderCommand = new AsyncRelayCommand(token => open is null ? Task.CompletedTask : open(Path.GetDirectoryName(Destination)!, true, token),
            () => open is not null && Path.IsPathFullyQualified(Destination));
        PauseCommand = Create(DownloadAction.Pause, () => DownloadBatch.CanExecute(_entry, _connected, DownloadAction.Pause));
        ResumeCommand = Create(DownloadAction.Resume, () => DownloadBatch.CanExecute(_entry, _connected, DownloadAction.Resume));
        RemoveCommand = Create(DownloadAction.Remove, () => DownloadBatch.CanExecute(_entry, _connected, DownloadAction.Remove));
        RetryCommand = Create(DownloadAction.Retry, () => DownloadBatch.CanExecute(_entry, _connected, DownloadAction.Retry));
        RecoverCommand = Create(DownloadAction.Recover, () => DownloadBatch.CanExecute(_entry, _connected, DownloadAction.Recover));
        MoveUpCommand = new AsyncRelayCommand(token => move is null ? Task.CompletedTask : move(Id, -1, token), () => move is not null && CanMove && _entry.QueuePosition > 0);
        MoveDownCommand = new AsyncRelayCommand(token => move is null ? Task.CompletedTask : move(Id, 1, token), () => move is not null && CanMove);
        IAsyncRelayCommand Create(DownloadAction action, Func<bool> canExecute) => new AsyncRelayCommand(
            token => execute is null ? Task.CompletedTask : execute(Id, action, token), () => execute is not null && canExecute());
    }
    public IAsyncRelayCommand PauseCommand { get; }
    public IAsyncRelayCommand ResumeCommand { get; }
    public IAsyncRelayCommand RemoveCommand { get; }
    public IAsyncRelayCommand RetryCommand { get; }
    public IAsyncRelayCommand RecoverCommand { get; }
    public IAsyncRelayCommand OpenFileCommand { get; }
    public IAsyncRelayCommand OpenFolderCommand { get; }
    public IAsyncRelayCommand MoveUpCommand { get; }
    public IAsyncRelayCommand MoveDownCommand { get; }
    public IReadOnlyList<IAsyncRelayCommand> Commands => [PauseCommand, ResumeCommand, RemoveCommand, RetryCommand, RecoverCommand, MoveUpCommand, MoveDownCommand, OpenFileCommand, OpenFolderCommand];
    private bool CanControl => _connected && _entry.PendingOperation is null;
    private bool CanMove => CanControl && _entry.Progress.Status == DownloadStatus.Queued && _entry.QueuePosition is not null;
    public string QueueLabel => _entry.Progress.Status == DownloadStatus.Queued && _entry.QueuePosition is int position ? $"Waiting · position {position + 1}" : "";
    public Guid Id => _entry.Job.Id;
    public DownloadEntry Entry => _entry;
    public long? TotalBytes => _entry.Progress.TotalBytes;
    public long DownloadedBytes => _entry.Progress.DownloadedBytes;
    public long BytesPerSecond => _entry.Progress.BytesPerSecond;
    public TimeSpan? EstimatedTimeRemaining => _entry.Progress.EstimatedTimeRemaining;
    public DownloadStatus RawStatus => _entry.Progress.Status;
    public string SourceUrl => _entry.Job.Source.AbsoluteUri;
    public string FileName => _entry.Job.DisplayName;
    public string Source => _entry.Job.Source.Host;
    public string Status => _entry.PendingOperation?.Status switch
    {
        OperationStatus.Uncertain => _entry.Progress.Status == DownloadStatus.Interrupted ? "Needs recovery" : $"Checking {_entry.PendingOperation.Action.ToString().ToLowerInvariant()}…",
        OperationStatus.Pending => _entry.PendingOperation.Action switch
        {
            DownloadAction.Pause => "Pausing…", DownloadAction.Resume => "Resuming…", DownloadAction.Remove => "Removing…",
            DownloadAction.Retry => "Retrying…", DownloadAction.Recover => "Recovering…", DownloadAction.Move => "Moving…", _ => "Adding…"
        },
        _ => _entry.Progress.Status == DownloadStatus.Interrupted ? "Needs recovery" : _entry.Progress.Status.ToString()
    };
    public string Destination => _entry.Job.Destination;
    public bool ShowPause => _entry.Progress.Status is DownloadStatus.Downloading or DownloadStatus.Queued;
    public bool ShowResume => _entry.Progress.Status == DownloadStatus.Paused;
    public bool ShowRemove => ShowPause || ShowResume;
    public bool ShowRetry => _entry.Progress.Status is DownloadStatus.Failed or DownloadStatus.Removed;
    public bool ShowRecover => _entry.Progress.Status == DownloadStatus.Interrupted || _entry.PendingOperation?.Status == OperationStatus.Uncertain;
    public bool ShowQueue => _entry.Progress.Status == DownloadStatus.Queued;
    public bool ShowOpenFile => _entry.Progress.Status == DownloadStatus.Completed;
    public string ErrorMessage => _entry.ErrorMessage ?? "";
    public double Progress => _entry.Progress.Percentage ?? 0;
    public bool IsIndeterminate => _entry.Progress.Percentage is null
        && _entry.Progress.Status is DownloadStatus.Downloading or DownloadStatus.Queued;
    public string ProgressLabel => _entry.Progress.Percentage is double percent ? $"{percent:0}%" : "Size unknown";
    public string SizeLabel => $"{FormatBytes(_entry.Progress.DownloadedBytes)} / "
        + (_entry.Progress.TotalBytes is long total ? FormatBytes(total) : "unknown");
    public string TotalSizeLabel => _entry.Progress.TotalBytes is long total ? FormatBytes(total) : "Unknown";
    public string SpeedLabel => _entry.Progress.Status == DownloadStatus.Downloading
        ? $"{FormatBytes(_entry.Progress.BytesPerSecond)}/s" : "—";
    public string EtaLabel => _entry.Progress.Status == DownloadStatus.Completed ? "Finished"
        : _entry.Progress.Status == DownloadStatus.Failed ? "Stopped"
        : _entry.Progress.Status == DownloadStatus.Interrupted ? "Awaiting recovery"
        : _entry.Progress.Status == DownloadStatus.Paused ? "Paused"
        : _entry.Progress.Status == DownloadStatus.Removed ? "Stopped · files kept"
        : _entry.Progress.EstimatedTimeRemaining is TimeSpan eta
            ? $"{Math.Ceiling(eta.TotalSeconds):0}s remaining" : "Estimating…";

    public void Update(DownloadEntry entry, bool connected = false)
    {
        if (_entry == entry && _connected == connected) return;
        _entry = entry;
        _connected = connected;
        foreach (var command in Commands) command.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(Status));
        foreach (var name in new[] { nameof(Entry), nameof(TotalBytes), nameof(DownloadedBytes), nameof(BytesPerSecond), nameof(EstimatedTimeRemaining), nameof(RawStatus), nameof(SourceUrl) }) OnPropertyChanged(name);
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(IsIndeterminate));
        OnPropertyChanged(nameof(ProgressLabel));
        OnPropertyChanged(nameof(SizeLabel));
        OnPropertyChanged(nameof(TotalSizeLabel));
        OnPropertyChanged(nameof(SpeedLabel));
        OnPropertyChanged(nameof(EtaLabel));
        OnPropertyChanged(nameof(ErrorMessage));
        OnPropertyChanged(nameof(QueueLabel));
        OnPropertyChanged(nameof(FileName)); OnPropertyChanged(nameof(Source)); OnPropertyChanged(nameof(Destination));
        foreach (var name in new[] { nameof(ShowPause), nameof(ShowResume), nameof(ShowRemove), nameof(ShowRetry), nameof(ShowRecover), nameof(ShowQueue), nameof(ShowOpenFile) }) OnPropertyChanged(name);
    }

    public static string FormatBytes(long bytes) => bytes >= 1024 * 1024
        ? $"{bytes / (1024d * 1024):0.0} MiB"
        : bytes >= 1024 ? $"{bytes / 1024d:0.0} KiB" : $"{bytes} B";
}
