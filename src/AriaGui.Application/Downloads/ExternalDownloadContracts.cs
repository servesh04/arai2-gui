using AriaGui.Browser.Contracts;
using AriaGui.Core.Downloads;

namespace AriaGui.Application.Downloads;

public sealed record ExternalRequestKey(string CallerKey, Guid InstallationId, Guid RequestId);
public sealed record ExternalDownloadRequest(ExternalRequestKey Key, DateTimeOffset CreatedAt, string SourceUrl,
    string? SuggestedFileName = null, string Trigger = "linkContextMenu");
public enum ExternalDispatchPhase { NeverSent, Claimed }
public sealed record ExternalRequestRecord(ExternalRequestKey Key, string PayloadDigest, DateTimeOffset CreatedAt,
    ResultCode Code, Guid? JobId, RejectionReason? Reason, DateTimeOffset UpdatedAt, DateTimeOffset? TombstonedAt = null,
    ExternalDispatchPhase DispatchPhase = ExternalDispatchPhase.NeverSent);
// Domain status is kept separate from future wire adapters, preserving Removed and all existing states.
public sealed record ExternalDownloadReceipt(ResultCode Code, Guid? JobId = null, DownloadStatus? JobStatus = null,
    bool Duplicate = false, RejectionReason? Reason = null, bool EngineUncertain = false);
public sealed record ExternalInsertResult(ExternalRequestRecord Record, bool Inserted);
public interface IExternalRequestRepository
{
    Task<ExternalRequestRecord?> FindExternalAsync(ExternalRequestKey key, CancellationToken token);
    Task<ExternalInsertResult> InsertExternalAsync(ExternalRequestRecord record, DownloadEntry? entry, CancellationToken token);
    Task<IReadOnlyList<Guid>> LoadNeverSentExternalJobsAsync(CancellationToken token);
    Task<bool> ClaimExternalDispatchAsync(Guid jobId, DateTimeOffset now, CancellationToken token);
    Task DeleteTerminalHistoryAsync(Guid jobId, DateTimeOffset now, CancellationToken token);
    Task PruneExternalRecordsAsync(DateTimeOffset now, CancellationToken token);
}

/// <summary>Authoritative successful preference reads only. Failures propagate; cached defaults never hide failure.</summary>
public sealed class DownloadPreferencesService(IUserPreferencesRepository repository, string fallbackFolder)
{
    private readonly SemaphoreSlim _gate = new(1);
    public async Task<string> ResolveDefaultFolderAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var preferences = await repository.LoadAsync(token).ConfigureAwait(false); preferences.Validate();
            var folder = string.IsNullOrEmpty(preferences.DefaultFolder) ? fallbackFolder : preferences.DefaultFolder;
            if (!Path.IsPathFullyQualified(folder)) throw new DownloadRequestException("Default folder is unavailable.");
            return folder;
        }
        finally { _gate.Release(); }
    }
    public static string DefaultFallbackFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "AriaGUI");
}
