using System.Security.Cryptography;
using System.Text.Json;
using AriaGui.Browser.Contracts;

namespace AriaGui.Application.Downloads;

/// <summary>Default-off reservation boundary. No browser API/transport adapter is enabled by this service.</summary>
public sealed class PreparedTakeoverService(DownloadCoordinator coordinator, ExternalDownloadIntakeService intake,
    IUserPreferencesRepository preferences, DownloadPreferencesService folders, TimeProvider clock,
    AutomaticTakeoverPolicy policy, CancellationToken applicationLifetime)
{
    private readonly HashSet<string> _grants = (policy.Grants ?? []).Select(grant => grant.ExactSourceUrl).ToHashSet(StringComparer.Ordinal);
    private IPreparedTakeoverRepository Repository => coordinator.ExternalRepository as IPreparedTakeoverRepository
        ?? throw new NotSupportedException("Prepared takeover requires durable reservations.");
    public async Task<PreparedTakeoverReceipt> PrepareAsync(PrepareTakeoverRequest request, CancellationToken token = default)
    {
        var normalized = Normalize(request);
        if (normalized is null || request.BrowserItem.Phase != BrowserItemPhase.InProgress)
            return Rejected(RejectionReason.InvalidRequest);
        var digest = Digest(normalized);
        try
        {
            applicationLifetime.ThrowIfCancellationRequested();
            await coordinator.InitializeAsync(token).ConfigureAwait(false);
            if (await Repository.FindTakeoverAsync(normalized.Download.Key, token).ConfigureAwait(false) is { } known)
                return await ReceiptAsync(known, digest, true, token).ConfigureAwait(false);
            if (!await EligibleAsync(normalized, token).ConfigureAwait(false)) return Rejected(RejectionReason.NotReady);
            var folder = await folders.ResolveDefaultFolderAsync(token).ConfigureAwait(false);
            var candidate = await coordinator.PrepareExternalAsync(new(normalized.Download.SourceUrl), folder, normalized.Download.SuggestedFileName, token).ConfigureAwait(false);
            applicationLifetime.ThrowIfCancellationRequested(); token.ThrowIfCancellationRequested();
            var result = await Repository.PrepareTakeoverAsync(normalized.Download, digest, candidate, token).ConfigureAwait(false);
            return await ReceiptAsync(result.Record, digest, !result.Inserted, CancellationToken.None).ConfigureAwait(false);
        }
        catch (PreparedTakeoverCapacityException) { return new(PreparedTakeoverPhase.Busy, Retryable: true); }
        catch (DownloadRequestException ex) { return Rejected(ex.Reason ?? RejectionReason.DestinationConflict); }
        catch (DownloadStorageException) { return new(PreparedTakeoverPhase.Unknown, Reason: RejectionReason.StorageUnavailable); }
    }
    public async Task<PreparedTakeoverReceipt> CommitAsync(PrepareTakeoverRequest cancelledItem, CancellationToken token = default)
    {
        var normalized = Normalize(cancelledItem);
        // A future trusted browser adapter must re-read cancellation; a fulfilled cancel call alone is not evidence.
        if (normalized is null || cancelledItem.BrowserItem.Phase != BrowserItemPhase.Cancelled) return Rejected(RejectionReason.InvalidRequest);
        var digest = Digest(normalized);
        try
        {
            await coordinator.InitializeAsync(token).ConfigureAwait(false);
            var known = await Repository.FindTakeoverAsync(normalized.Download.Key, token).ConfigureAwait(false);
            if (known is null) return new(PreparedTakeoverPhase.NotFound);
            if (known.PayloadDigest != digest || known.Phase != PreparedTakeoverPhase.Prepared)
                return await ReceiptAsync(known, digest, true, token).ConfigureAwait(false);
            applicationLifetime.ThrowIfCancellationRequested();
            if (!await EligibleAsync(normalized, token).ConfigureAwait(false)) return Rejected(RejectionReason.NotReady);
            // Filesystem admission repeats at commit, using the reserved destination, not subsequently changed preferences.
            var candidate = known.Candidate ?? throw new DownloadStorageException("Prepared intent is missing.");
            await coordinator.PrepareExternalAsync(new(normalized.Download.SourceUrl), Path.GetDirectoryName(candidate.Job.Destination)!, candidate.File.FileName, token).ConfigureAwait(false);
            applicationLifetime.ThrowIfCancellationRequested(); token.ThrowIfCancellationRequested();
            var committed = await Repository.CommitTakeoverAsync(normalized.Download.Key, digest, token).ConfigureAwait(false);
            return committed is null ? new(PreparedTakeoverPhase.NotFound) : await ReceiptAsync(committed.Record, digest, !committed.Changed, CancellationToken.None).ConfigureAwait(false);
        }
        catch (DownloadRequestException ex) { return Rejected(ex.Reason ?? RejectionReason.DestinationConflict); }
        catch (DownloadStorageException) { return new(PreparedTakeoverPhase.Unknown, Reason: RejectionReason.StorageUnavailable); }
    }
    public async Task<PreparedTakeoverReceipt> AbortAsync(ExternalRequestKey key, CancellationToken token = default) =>
        await LookupAsync(key, true, token).ConfigureAwait(false);
    public async Task<PreparedTakeoverReceipt> GetStatusAsync(ExternalRequestKey key, CancellationToken token = default) =>
        await LookupAsync(key, false, token).ConfigureAwait(false);
    private async Task<PreparedTakeoverReceipt> LookupAsync(ExternalRequestKey key, bool abort, CancellationToken token)
    {
        if (!ExternalDownloadIntakeService.ValidKey(key)) return Rejected(RejectionReason.InvalidRequest);
        try
        {
            await coordinator.InitializeAsync(token).ConfigureAwait(false);
            var record = abort ? await Repository.AbortTakeoverAsync(key, token).ConfigureAwait(false) : await Repository.FindTakeoverAsync(key, token).ConfigureAwait(false);
            return record is null ? new(PreparedTakeoverPhase.NotFound) : await ReceiptAsync(record, record.PayloadDigest, false, token).ConfigureAwait(false);
        }
        catch (DownloadStorageException) { return new(PreparedTakeoverPhase.Unavailable, Reason: RejectionReason.StorageUnavailable); }
    }
    private async Task<PreparedTakeoverReceipt> ReceiptAsync(PreparedTakeoverRecord record, string digest, bool duplicate, CancellationToken token)
    {
        if (record.PayloadDigest != digest) return Rejected(RejectionReason.RequestIdConflict);
        if (record.Phase == PreparedTakeoverPhase.Committed)
        {
            var accepted = await intake.GetStatusAsync(record.Key, token).ConfigureAwait(false);
            // Logical takeover state stays committed; tombstoned history has no live accepted job.
            return new(record.Phase, record.ExpiresAt, accepted, duplicate);
        }
        return new(record.Phase, record.ExpiresAt, Duplicate: duplicate);
    }
    private async Task<bool> EligibleAsync(PrepareTakeoverRequest request, CancellationToken token)
    {
        if (!policy.Enabled || !_grants.Contains(request.Download.SourceUrl) || request.Download.CreatedAt < clock.GetUtcNow().AddDays(-7)
            || request.Download.CreatedAt > clock.GetUtcNow().AddMinutes(5)) return false;
        var value = await preferences.LoadAsync(token).ConfigureAwait(false); value.Validate();
        return value.EffectiveBrowser is { Enabled: true, AutomaticTakeoverEnabled: true };
    }
    private static PrepareTakeoverRequest? Normalize(PrepareTakeoverRequest request)
    {
        if (!ExternalDownloadIntakeService.ValidKey(request.Download.Key) || request.BrowserItem.ItemId < 0
            || request.BrowserItem.ProfileKey is not { Length: 64 } || !request.BrowserItem.ProfileKey.All(Uri.IsHexDigit)
            || request.BrowserItem.StartedAt != request.Download.CreatedAt || !Enum.IsDefined(request.BrowserItem.Phase)
            || request.BrowserItem.PrivateContext || request.BrowserItem.InitiatingExtension is not null) return null;
        var parsed = ExternalDownloadIntakeService.Normalize(request.Download with { Trigger = "linkContextMenu" });
        if (!parsed.Success) return null;
        var source = ((PublicGetPayload)parsed.Value!.Payload).SourceUrl;
        if (ExternalDownloadIntakeService.IsBlockedLiteral(new(source))) return null;
        return request with { Download = request.Download with { SourceUrl = source, Trigger = "linkContextMenu" } };
    }
    private static string Digest(PrepareTakeoverRequest request) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
    { version = 1, source = request.Download.SourceUrl, name = request.Download.SuggestedFileName,
        started = request.BrowserItem.StartedAt.UtcTicks, item = request.BrowserItem.ItemId, profile = request.BrowserItem.ProfileKey })));
    private static PreparedTakeoverReceipt Rejected(RejectionReason reason) => new(PreparedTakeoverPhase.Rejected, Reason: reason);
}
