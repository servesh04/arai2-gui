using AriaGui.Application.Downloads;

namespace AriaGui.Infrastructure.Aria2;

public sealed class ManagedAria2Client(Aria2ProcessManager manager) : IAria2Client
{
    public bool SupportsFreshRestart => manager.SupportsFreshRestart;
    public bool SupportsChecksumVerification => manager.SupportsChecksumVerification;
    public bool SupportsParallelHttpRanges => manager.SupportsParallelHttpRanges;
    public Task<bool> CanRestartFreshAsync(string engineId, CancellationToken token) => manager.CanRestartFreshAsync(engineId, token);
    public Task<EngineDownload> AddAsync(EngineDownloadRequest request, CancellationToken token) => manager.AddDownloadAsync(request, token);
    public Task<IReadOnlyList<EngineDownload>> GetDownloadsAsync(IReadOnlyList<string> gids, CancellationToken token) => manager.GetDownloadsAsync(gids, token);
    public Task<EngineObservation> ObserveAsync(IReadOnlyList<string> gids, CancellationToken token) => manager.ObserveDownloadsAsync(gids, token);
    public Task<EngineCommandResult> ExecuteAsync(string gid, AriaGui.Core.Downloads.DownloadAction action, int? position, CancellationToken token) =>
        manager.ExecuteDownloadAsync(gid, action, position, token);
    public Task SetConcurrencyAsync(int limit, CancellationToken token) => manager.SetConcurrencyAsync(limit, token);
    public Task<BandwidthObservation> ApplyBandwidthAsync(BandwidthProfile profile, CancellationToken token) => manager.ApplyBandwidthAsync(profile, token);
    public Task<BandwidthObservation> ObserveBandwidthAsync(CancellationToken token) => manager.ObserveBandwidthAsync(token);
    public Task<JobBandwidthObservation> ApplyJobBandwidthAsync(Guid jobId, string gid, ByteRateLimit cap, CancellationToken token) =>
        manager.ApplyJobBandwidthAsync(jobId, gid, cap, token);
    public Task<JobBandwidthObservation> ObserveJobBandwidthAsync(Guid jobId, string gid, CancellationToken token) =>
        manager.ObserveJobBandwidthAsync(jobId, gid, token);
    public Task<EngineDownload> RecoverAsync(DownloadEntry entry, CancellationToken token) => manager.RecoverDownloadAsync(entry, token);
}
