using AriaGui.Core.Downloads;

namespace AriaGui.Application.Downloads;

public sealed record EngineDownloadRequest(string EngineId, Uri Source, bool SimulateUnknownSize = false,
    string Directory = "", string FileName = "", bool ContinueExisting = false, bool StartPaused = false, string? ExactSourceUrl = null);
public sealed record EngineDownload(string EngineId, DownloadSnapshot Progress, string? ErrorMessage = null,
    int? QueuePosition = null);
public sealed record EngineObservation(string Generation, IReadOnlyList<EngineDownload> Downloads, int MaxConcurrentDownloads,
    IReadOnlyList<string>? RestoredIds = null, int UnknownTransferCount = 0);
public sealed record EngineCommandResult(string Generation, EngineDownload? Download);

// Query only the IDs the application owns; unrelated engine transfers never become app jobs.
public interface IAria2Client
{
    Task<EngineDownload> AddAsync(EngineDownloadRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<EngineDownload>> GetDownloadsAsync(IReadOnlyList<string> engineIds, CancellationToken cancellationToken);
    async Task<EngineObservation> ObserveAsync(IReadOnlyList<string> engineIds, CancellationToken token) =>
        new("simulation", await GetDownloadsAsync(engineIds, token), 3);
    Task<EngineCommandResult> ExecuteAsync(string gid, DownloadAction action, int? position, CancellationToken token) =>
        throw new NotSupportedException("This client does not support download controls.");
    Task SetConcurrencyAsync(int limit, CancellationToken token) => throw new NotSupportedException("This client does not support queue settings.");
    Task<EngineDownload> RecoverAsync(DownloadEntry entry, CancellationToken token) => throw new NotSupportedException("This client does not support recovery.");
}
