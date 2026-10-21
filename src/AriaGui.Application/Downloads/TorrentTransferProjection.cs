using AriaGui.Core.Downloads;

namespace AriaGui.Application.Downloads;

public sealed record TorrentTransferState(Guid JobId, string Name, string InfoHash, string DestinationRoot,
    DateTimeOffset CreatedAt, TorrentStage Stage, long TotalBytes, long SelectedBytes,
    long SelectedCompletedBytes, long DownloadBytesPerSecond, int FileCount, int SelectedFileCount,
    string? FailureReason = null, string? Activity = null, int? Peers = null, int? Seeds = null,
    long? UploadBytesPerSecond = null, double? Ratio = null, bool IsPaused = false, bool IsRemoved = false);

public static class TorrentTransferProjection
{
    public static DownloadEntry Map(TorrentTransferState torrent)
    {
        var status = torrent.IsRemoved ? DownloadStatus.Removed : torrent.IsPaused ? DownloadStatus.Paused
            : torrent.Stage switch
            {
                TorrentStage.AwaitingSelection or TorrentStage.Waiting => DownloadStatus.Queued,
                TorrentStage.Checking or TorrentStage.Downloading or TorrentStage.Verifying or TorrentStage.Publishing
                    => DownloadStatus.Downloading,
                TorrentStage.Completed => DownloadStatus.Completed,
                TorrentStage.Failed => DownloadStatus.Failed,
                TorrentStage.Stopped => DownloadStatus.Interrupted,
                _ => DownloadStatus.Interrupted
            };
        var substate = torrent.Activity switch
        {
            "Fetching metadata" or "Connecting" or "No peers yet" or "Verifying" or "Seeding" => torrent.Activity,
            _ => torrent.Stage switch
            {
                TorrentStage.Checking => "Checking files",
                TorrentStage.Downloading when torrent.Peers == 0 => "No peers yet",
                TorrentStage.Publishing => $"Publishing to {Path.GetFileName(Path.TrimEndingDirectorySeparator(torrent.DestinationRoot))}",
                TorrentStage.Verifying => "Verifying",
                _ => null
            }
        };
        if (substate is "Fetching metadata" or "Connecting" or "No peers yet" or "Verifying" or "Seeding")
            status = DownloadStatus.Downloading;
        var selected = Math.Clamp(torrent.SelectedCompletedBytes, 0, torrent.SelectedBytes);
        if (status == DownloadStatus.Completed || substate == "Seeding") selected = torrent.SelectedBytes;
        var destination = Path.Combine(torrent.DestinationRoot, torrent.Name);
        var job = new DownloadJob(torrent.JobId, torrent.Name, new Uri($"torrent://{torrent.InfoHash}/"),
            destination, torrent.CreatedAt);
        var attempt = new TransferAttempt(torrent.JobId, torrent.JobId, torrent.InfoHash, torrent.CreatedAt);
        var file = new DownloadFile(torrent.JobId, torrent.JobId, attempt.Id, torrent.Name, torrent.SelectedBytes);
        return new DownloadEntry(job, attempt, file,
            new DownloadSnapshot(status, selected, torrent.SelectedBytes, torrent.DownloadBytesPerSecond),
            ErrorMessage: torrent.FailureReason, ObservedAt: torrent.CreatedAt)
        {
            Kind = TransferKind.Torrent,
            SubstateText = substate,
            Peers = torrent.Peers,
            Seeds = torrent.Seeds,
            UploadBytesPerSecond = torrent.UploadBytesPerSecond,
            Ratio = torrent.Ratio,
            FileCount = torrent.FileCount,
            SelectedFileCount = torrent.SelectedFileCount,
            TorrentTotalBytes = torrent.TotalBytes
        };
    }
}
