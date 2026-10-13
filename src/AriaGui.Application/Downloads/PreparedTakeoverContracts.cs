using AriaGui.Browser.Contracts;

namespace AriaGui.Application.Downloads;

// Internal application phase, deliberately absent from the advertised v1 wire schema.
public enum PreparedTakeoverPhase { Prepared, Committed, Aborted, Expired, Rejected, NotFound, Unknown, Unavailable, Busy }
public enum BrowserItemPhase { InProgress, Complete, Cancelled, Interrupted }
public sealed record BrowserItemEvidence(long ItemId, DateTimeOffset StartedAt, string ProfileKey,
    BrowserItemPhase Phase, bool PrivateContext = false, string? InitiatingExtension = null);
public sealed record PrepareTakeoverRequest(ExternalDownloadRequest Download, BrowserItemEvidence BrowserItem);
public sealed record PreparedTakeoverReceipt(PreparedTakeoverPhase Phase, DateTimeOffset? ExpiresAt = null,
    ExternalDownloadReceipt? Accepted = null, bool Duplicate = false, RejectionReason? Reason = null, bool Retryable = false);
public sealed record PreparedTakeoverRecord(ExternalRequestKey Key, string PayloadDigest, PreparedTakeoverPhase Phase,
    DateTimeOffset? ExpiresAt, DownloadEntry? Candidate = null);
public sealed record PreparedTakeoverInsert(PreparedTakeoverRecord Record, bool Inserted);
public sealed record PreparedTakeoverCommit(PreparedTakeoverRecord Record, bool Changed);
public sealed class PreparedTakeoverCapacityException : Exception;
// Internal persistence envelope. Unknown/legacy versions cannot authorize a commit.
public sealed record PreparedIntentEnvelope(int Version, DownloadEntry Entry);
// Grants are trusted application configuration, never a method/auth claim supplied by request JSON.
// Exact resource matching preserves signed query strings and excludes blanket file-extension rules.
public sealed record ReplayablePublicGetGrant(string ExactSourceUrl);
public sealed record AutomaticTakeoverPolicy(bool Enabled = false, IReadOnlyList<ReplayablePublicGetGrant>? Grants = null);
public interface IPreparedTakeoverRepository
{
    Task<PreparedTakeoverInsert> PrepareTakeoverAsync(ExternalDownloadRequest request, string digest, DownloadEntry candidate, CancellationToken token);
    Task<PreparedTakeoverRecord?> FindTakeoverAsync(ExternalRequestKey key, CancellationToken token);
    Task<PreparedTakeoverCommit?> CommitTakeoverAsync(ExternalRequestKey key, string digest, CancellationToken token);
    Task<PreparedTakeoverRecord?> AbortTakeoverAsync(ExternalRequestKey key, CancellationToken token);
    Task<int> CleanupPreparedTakeoversAsync(CancellationToken token);
}
