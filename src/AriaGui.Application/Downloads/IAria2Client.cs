using AriaGui.Core.Downloads;

namespace AriaGui.Application.Downloads;

public sealed record EngineDownloadRequest(string EngineId, Uri Source, bool SimulateUnknownSize = false,
    string Directory = "", string FileName = "", bool ContinueExisting = false, bool StartPaused = false, string? ExactSourceUrl = null,
    Guid? ContinuityJobId = null, Guid? ContinuityAttemptId = null, bool RequirePublicOrigin = true, string? LegacyParentEngineId = null, bool FreshRestart = false,
    ByteRateLimit? JobDownloadLimit = null, ExpectedChecksum? ExpectedChecksum = null, int HttpConnections = 1);
public sealed record EngineDownload(string EngineId, DownloadSnapshot Progress, string? ErrorMessage = null,
    int? QueuePosition = null, VerificationStatus Verification = VerificationStatus.NotSupplied);
public sealed record EngineObservation(string Generation, IReadOnlyList<EngineDownload> Downloads, int MaxConcurrentDownloads,
    IReadOnlyList<string>? RestoredIds = null, int UnknownTransferCount = 0);
public sealed record EngineCommandResult(string Generation, EngineDownload? Download);

// Query only the IDs the application owns; unrelated engine transfers never become app jobs.
public interface IAria2Client
{
    bool SupportsFreshRestart => false;
    bool SupportsChecksumVerification => false;
    bool SupportsParallelHttpRanges => false;
    Task<bool> CanRestartFreshAsync(string engineId, CancellationToken token) => Task.FromResult(false);
    Task<EngineDownload> AddAsync(EngineDownloadRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<EngineDownload>> GetDownloadsAsync(IReadOnlyList<string> engineIds, CancellationToken cancellationToken);
    async Task<EngineObservation> ObserveAsync(IReadOnlyList<string> engineIds, CancellationToken token) =>
        new("simulation", await GetDownloadsAsync(engineIds, token), 3);
    Task<EngineCommandResult> ExecuteAsync(string gid, DownloadAction action, int? position, CancellationToken token) =>
        throw new NotSupportedException("This client does not support download controls.");
    Task SetConcurrencyAsync(int limit, CancellationToken token) => throw new NotSupportedException("This client does not support queue settings.");
    Task<BandwidthObservation> ApplyBandwidthAsync(BandwidthProfile profile, CancellationToken token) =>
        throw new NotSupportedException("This client does not support bandwidth settings.");
    Task<BandwidthObservation> ObserveBandwidthAsync(CancellationToken token) =>
        throw new NotSupportedException("This client does not support bandwidth observation.");
    Task<JobBandwidthObservation> ApplyJobBandwidthAsync(Guid jobId, string gid, ByteRateLimit cap, CancellationToken token) =>
        throw new NotSupportedException("This client does not support job bandwidth settings.");
    Task<JobBandwidthObservation> ObserveJobBandwidthAsync(Guid jobId, string gid, CancellationToken token) =>
        throw new NotSupportedException("This client does not support job bandwidth observation.");
    Task<EngineDownload> RecoverAsync(DownloadEntry entry, CancellationToken token) => throw new NotSupportedException("This client does not support recovery.");
}
