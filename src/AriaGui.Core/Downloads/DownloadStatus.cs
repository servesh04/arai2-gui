namespace AriaGui.Core.Downloads;

public enum DownloadStatus { Queued, Downloading, Paused, Completed, Failed, Removed, Interrupted }

public static class DownloadStateTransitions
{
    public static bool CanTransition(DownloadStatus from, DownloadStatus to) =>
        from == to || (from, to) switch
        {
            (DownloadStatus.Queued, DownloadStatus.Downloading or DownloadStatus.Completed or DownloadStatus.Paused or DownloadStatus.Interrupted
                or DownloadStatus.Failed or DownloadStatus.Removed) => true,
            (DownloadStatus.Downloading, DownloadStatus.Paused or DownloadStatus.Completed or DownloadStatus.Interrupted
                or DownloadStatus.Failed or DownloadStatus.Removed) => true,
            (DownloadStatus.Paused, DownloadStatus.Queued or DownloadStatus.Downloading or DownloadStatus.Completed or DownloadStatus.Interrupted
                or DownloadStatus.Failed or DownloadStatus.Removed) => true,
            (DownloadStatus.Completed or DownloadStatus.Failed, DownloadStatus.Removed) => true,
            (DownloadStatus.Interrupted, DownloadStatus.Queued or DownloadStatus.Downloading or DownloadStatus.Completed
                or DownloadStatus.Paused or DownloadStatus.Failed or DownloadStatus.Removed) => true,
            _ => false
        };
}
