using AriaGui.Core.Downloads;

namespace AriaGui.Application.Downloads;

public enum EngineConnectionState { Starting, Connected, Disconnected }

public sealed record DownloadEntry(DownloadJob Job, TransferAttempt Attempt,
    DownloadFile File, DownloadSnapshot Progress, PendingOperation? PendingOperation = null, string? ErrorMessage = null,
    int? QueuePosition = null, bool DesiredPaused = false, bool DesiredRemoved = false, DateTimeOffset? ObservedAt = null,
    string? ExactSourceUrl = null, bool PolicyHeld = false, VerificationStatus Verification = VerificationStatus.NotSupplied,
    int? HttpStatus = null)
{
    public TransferKind Kind { get; init; } = TransferKind.Http;
    public string? SubstateText { get; init; }
    public int? Peers { get; init; }
    public int? Seeds { get; init; }
    public long? UploadBytesPerSecond { get; init; }
    public double? Ratio { get; init; }
    public int? FileCount { get; init; }
    public int? SelectedFileCount { get; init; }
    public long? TorrentTotalBytes { get; init; }
}
public sealed record CompletionNotice(Guid AttemptId, string FileName, VerificationStatus Verification = VerificationStatus.NotSupplied);

public sealed record DownloadRuntimeSnapshot(long Revision, IReadOnlyList<DownloadEntry> Downloads,
    EngineConnectionState Connection, string? ErrorMessage = null, QueuePolicy? QueuePolicy = null,
    int? ObservedConcurrency = null, CompletionNotice? LatestCompletion = null, BandwidthPolicy? BandwidthPolicy = null,
    IReadOnlyDictionary<Guid, JobBandwidthPolicy>? JobBandwidthPolicies = null, BandwidthSchedule? Schedule = null)
{
    public IReadOnlyList<DownloadEntry> Torrents { get; init; } = [];
    public IReadOnlyList<DownloadEntry> AllTransfers => Downloads.Concat(Torrents).ToArray();
}

public sealed class DownloadRuntimeStore
{
    private readonly object _sync = new();
    private DownloadRuntimeSnapshot _current = new(0, Array.Empty<DownloadEntry>(), EngineConnectionState.Starting);

    public DownloadRuntimeSnapshot Current => Volatile.Read(ref _current);
    public event EventHandler<DownloadRuntimeSnapshot>? Changed;

    // Only application coordination publishes state; UI consumers can only observe it.
    internal void Publish(IEnumerable<DownloadEntry> downloads, EngineConnectionState connection, string? error = null,
        QueuePolicy? policy = null, int? observedConcurrency = null, CompletionNotice? completion = null,
        BandwidthPolicy? bandwidthPolicy = null, IReadOnlyDictionary<Guid, JobBandwidthPolicy>? jobBandwidthPolicies = null,
        BandwidthSchedule? schedule = null)
    {
        DownloadRuntimeSnapshot snapshot;
        lock (_sync)
        {
            var previous = Current;
            snapshot = new DownloadRuntimeSnapshot(previous.Revision + 1,
                Array.AsReadOnly(DownloadBrowsing.RetainRecent(downloads.Where(row => row.Kind == TransferKind.Http)).ToArray()),
                connection, error, policy ?? previous.QueuePolicy,
                observedConcurrency ?? previous.ObservedConcurrency, completion ?? previous.LatestCompletion,
                bandwidthPolicy ?? previous.BandwidthPolicy, jobBandwidthPolicies ?? previous.JobBandwidthPolicies,
                schedule ?? previous.Schedule) { Torrents = previous.Torrents };
            Volatile.Write(ref _current, snapshot);
        }
        Changed?.Invoke(this, snapshot);
    }

    public void PublishTorrents(IEnumerable<DownloadEntry> torrents)
    {
        DownloadRuntimeSnapshot snapshot;
        lock (_sync)
        {
            var previous = Current;
            var rows = torrents.ToArray();
            if (rows.Any(row => row.Kind != TransferKind.Torrent)
                || rows.Select(row => row.Job.Id).Distinct().Count() != rows.Length
                || rows.Any(row => previous.Downloads.Any(http => http.Job.Id == row.Job.Id)))
                throw new ArgumentException("Torrent rows must have unique torrent identities.", nameof(torrents));
            snapshot = previous with { Revision = previous.Revision + 1, Torrents = Array.AsReadOnly(rows) };
            Volatile.Write(ref _current, snapshot);
        }
        Changed?.Invoke(this, snapshot);
    }
}
