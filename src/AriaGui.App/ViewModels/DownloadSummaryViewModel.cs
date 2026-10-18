using AriaGui.App.Services;
using AriaGui.Application.Downloads;
using AriaGui.Core.Downloads;

namespace AriaGui.App.ViewModels;

// The footer observes the same store as the list. Neither ViewModel polls the engine.
public sealed class DownloadSummaryViewModel : RuntimeViewModel
{
    private string _active = "0";
    private string _completed = "0";
    private string _waiting = "0";
    private string _queueStatus = "0 active · 0 waiting";
    private string _speed = "0 B/s";
    private string _total = "0 downloads";

    public DownloadSummaryViewModel(DownloadRuntimeStore store, IUiDispatcher dispatcher) : base(store, dispatcher) => Initialize();
    public string Active { get => _active; private set => SetProperty(ref _active, value); }
    public string Completed { get => _completed; private set => SetProperty(ref _completed, value); }
    public string Waiting { get => _waiting; private set => SetProperty(ref _waiting, value); }
    public string QueueStatus { get => _queueStatus; private set => SetProperty(ref _queueStatus, value); }
    public string Speed { get => _speed; private set => SetProperty(ref _speed, value); }
    public string Total { get => _total; private set => SetProperty(ref _total, value); }

    protected override void Apply(DownloadRuntimeSnapshot snapshot)
    {
        Active = snapshot.Downloads.Count(item => item.Progress.Status == DownloadStatus.Downloading).ToString();
        Waiting = snapshot.Downloads.Count(item => item.Progress.Status == DownloadStatus.Queued).ToString();
        QueueStatus = $"{Active} active · {Waiting} waiting";
        Completed = snapshot.Downloads.Count(item => item.Progress.Status == DownloadStatus.Completed).ToString();
        Speed = $"{DownloadItemViewModel.FormatBytes(snapshot.Downloads.Sum(item => item.Progress.BytesPerSecond))}/s";
        Total = $"{snapshot.Downloads.Count} download{(snapshot.Downloads.Count == 1 ? "" : "s")}";
    }
}
