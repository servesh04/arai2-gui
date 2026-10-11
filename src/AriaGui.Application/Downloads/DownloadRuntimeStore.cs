using AriaGui.Core.Downloads;

namespace AriaGui.Application.Downloads;

public enum EngineConnectionState { Starting, Connected, Disconnected }

public sealed record DownloadEntry(DownloadJob Job, TransferAttempt Attempt,
    DownloadFile File, DownloadSnapshot Progress, PendingOperation? PendingOperation = null, string? ErrorMessage = null,
    int? QueuePosition = null, bool DesiredPaused = false, bool DesiredRemoved = false, DateTimeOffset? ObservedAt = null);
public sealed record CompletionNotice(Guid AttemptId, string FileName);

public sealed record DownloadRuntimeSnapshot(long Revision, IReadOnlyList<DownloadEntry> Downloads,
    EngineConnectionState Connection, string? ErrorMessage = null, QueuePolicy? QueuePolicy = null,
    int? ObservedConcurrency = null, CompletionNotice? LatestCompletion = null);

public sealed class DownloadRuntimeStore
{
    private DownloadRuntimeSnapshot _current = new(0, Array.Empty<DownloadEntry>(), EngineConnectionState.Starting);

    public DownloadRuntimeSnapshot Current => Volatile.Read(ref _current);
    public event EventHandler<DownloadRuntimeSnapshot>? Changed;

    // Only application coordination publishes state; UI consumers can only observe it.
    internal void Publish(IEnumerable<DownloadEntry> downloads, EngineConnectionState connection, string? error = null,
        QueuePolicy? policy = null, int? observedConcurrency = null, CompletionNotice? completion = null)
    {
        var snapshot = new DownloadRuntimeSnapshot(Current.Revision + 1,
            Array.AsReadOnly(DownloadBrowsing.RetainRecent(downloads).ToArray()), connection, error, policy ?? Current.QueuePolicy,
            observedConcurrency ?? Current.ObservedConcurrency, completion ?? Current.LatestCompletion);
        Volatile.Write(ref _current, snapshot);
        Changed?.Invoke(this, snapshot);
    }
}
