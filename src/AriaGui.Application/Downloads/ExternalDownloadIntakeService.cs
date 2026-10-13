using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AriaGui.Browser.Contracts;
using AriaGui.Core.Downloads;

namespace AriaGui.Application.Downloads;

/// <summary>Trusted application intake, not a transport listener. CallerKey must come from verified launch context.</summary>
public sealed class ExternalDownloadIntakeService(DownloadCoordinator coordinator, DownloadPreferencesService preferences,
    TimeProvider clock, CancellationToken applicationLifetime, IExternalDispatchReadiness readiness)
{
    private readonly SemaphoreSlim _initialize = new(1);
    private readonly object _dispatchLock = new();
    private readonly Dictionary<Guid, Task> _dispatch = [];
    private bool _initialized;
    public RejectionReason? LastDispatchFailure { get; private set; }
    private IExternalRequestRepository Repository => coordinator.ExternalRepository;

    public async Task InitializeAsync(CancellationToken token = default)
    {
        await _initialize.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_initialized) return;
            await coordinator.InitializeAsync(token).ConfigureAwait(false);
            var pending = await Repository.LoadNeverSentExternalJobsAsync(token).ConfigureAwait(false);
            _initialized = true;
            foreach (var job in pending) Schedule(job);
        }
        finally { _initialize.Release(); }
    }
    public async Task<ExternalDownloadReceipt> AcceptAsync(ExternalDownloadRequest request, CancellationToken token = default)
    {
        applicationLifetime.ThrowIfCancellationRequested();
        if (!ValidKey(request.Key)) return new(ResultCode.Rejected, Reason: RejectionReason.InvalidRequest);
        var parsed = Normalize(request);
        if (!parsed.Success) return new(ResultCode.Rejected, Reason: parsed.Error);
        var payload = (PublicGetPayload)parsed.Value!.Payload;
        request = request with { SourceUrl = payload.SourceUrl };
        var digest = Digest(request);
        Guid? acceptedJob = null;
        try
        {
            await InitializeAsync(token).ConfigureAwait(false);
            if (await Repository.FindExternalAsync(request.Key, token).ConfigureAwait(false) is { } known)
            {
                if (known.Code == ResultCode.Accepted && known.PayloadDigest == digest) acceptedJob = known.JobId;
                return await ResolveAsync(known, digest, true, token).ConfigureAwait(false);
            }
            var now = clock.GetUtcNow();
            if (request.CreatedAt < now.AddDays(-7) || request.CreatedAt > now.AddMinutes(5))
                return await RejectAsync(request, digest, RejectionReason.InvalidRequest, token).ConfigureAwait(false);
            if (IsBlockedLiteral(new Uri(request.SourceUrl)))
                return await RejectAsync(request, digest, RejectionReason.UnsupportedUrl, token).ConfigureAwait(false);
            string folder;
            try { folder = await preferences.ResolveDefaultFolderAsync(token).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch { return await RejectAsync(request, digest, RejectionReason.NotReady, token).ConfigureAwait(false); }
            DownloadEntry entry;
            try { entry = await coordinator.PrepareExternalAsync(new Uri(request.SourceUrl), folder, request.SuggestedFileName, token).ConfigureAwait(false); }
            catch (DownloadRequestException ex) { return await RejectAsync(request, digest, ex.Reason ?? RejectionReason.DestinationConflict, token).ConfigureAwait(false); }
            catch (ArgumentException) { return await RejectAsync(request, digest, RejectionReason.InvalidFilename, token).ConfigureAwait(false); }
            applicationLifetime.ThrowIfCancellationRequested(); token.ThrowIfCancellationRequested();
            var record = new ExternalRequestRecord(request.Key, digest, request.CreatedAt, ResultCode.Accepted, entry.Job.Id, null, now);
            ExternalInsertResult result;
            try { result = await Repository.InsertExternalAsync(record, entry, token).ConfigureAwait(false); }
            catch (DownloadRequestException) { return await RejectAsync(request, digest, RejectionReason.DestinationConflict, token).ConfigureAwait(false); }
            // Acceptance has committed. Neither response delivery nor later work uses the client's token.
            if (result.Record.Code == ResultCode.Accepted && result.Record.JobId is { } job) Schedule(job);
            if (result.Record.Code == ResultCode.Accepted && result.Record.PayloadDigest == digest) acceptedJob = result.Record.JobId;
            return await ResolveAsync(result.Record, digest, !result.Inserted, CancellationToken.None).ConfigureAwait(false);
        }
        catch (DownloadStorageException) { return new(acceptedJob is null ? ResultCode.Unknown : ResultCode.Accepted,
            acceptedJob, Reason: RejectionReason.StorageUnavailable, EngineUncertain: true); }
    }
    public async Task<ExternalDownloadReceipt> GetStatusAsync(ExternalRequestKey key, CancellationToken token = default)
    {
        if (!ValidKey(key)) return new(ResultCode.Rejected, Reason: RejectionReason.InvalidRequest);
        try
        {
            await InitializeAsync(token).ConfigureAwait(false);
            var record = await Repository.FindExternalAsync(key, token).ConfigureAwait(false);
            return record is null ? new(ResultCode.NotFound) : await ResolveAsync(record, record.PayloadDigest, false, token).ConfigureAwait(false);
        }
        catch (DownloadStorageException) { return new(ResultCode.Unavailable, Reason: RejectionReason.StorageUnavailable); }
    }
    private async Task<ExternalDownloadReceipt> RejectAsync(ExternalDownloadRequest request, string digest, RejectionReason reason, CancellationToken token)
    {
        var result = await Repository.InsertExternalAsync(new(request.Key, digest, request.CreatedAt, ResultCode.Rejected, null, reason, clock.GetUtcNow()), null, token).ConfigureAwait(false);
        return await ResolveAsync(result.Record, digest, !result.Inserted, token).ConfigureAwait(false);
    }
    private async Task<ExternalDownloadReceipt> ResolveAsync(ExternalRequestRecord record, string digest, bool duplicate, CancellationToken token)
    {
        if (record.PayloadDigest != digest) return new(ResultCode.Rejected, Reason: RejectionReason.RequestIdConflict);
        if (record.Code != ResultCode.Accepted) return new(record.Code, Duplicate: duplicate, Reason: record.Reason);
        if (record.DispatchPhase == ExternalDispatchPhase.NeverSent) Schedule(record.JobId!.Value);
        var entry = await coordinator.LoadExternalJobAsync(record.JobId!.Value, token).ConfigureAwait(false);
        if (entry is null) throw new DownloadStorageException("Accepted job is missing.");
        return new(ResultCode.Accepted, entry.Job.Id, entry.Progress.Status, duplicate, EngineUncertain:
            record.DispatchPhase == ExternalDispatchPhase.Claimed && (entry.PendingOperation?.Action == DownloadAction.Add || entry.Progress.Status == DownloadStatus.Interrupted)
            || entry.PendingOperation?.Status == OperationStatus.Uncertain);
    }
    private void Schedule(Guid job)
    {
        lock (_dispatchLock)
        {
            if (_dispatch.ContainsKey(job) || applicationLifetime.IsCancellationRequested) return;
            var task = Task.Run(async () =>
            {
                try
                {
                    await readiness.WaitForReadyAsync(applicationLifetime).ConfigureAwait(false);
                    await coordinator.DispatchExternalAsync(job, applicationLifetime).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (applicationLifetime.IsCancellationRequested) { }
                catch (DownloadStorageException) { LastDispatchFailure = RejectionReason.StorageUnavailable; }
                catch { LastDispatchFailure = RejectionReason.NotReady; /* Safe diagnostic only. Never replay a claimed Add. */ }
            }, CancellationToken.None);
            _dispatch.Add(job, task);
            _ = task.ContinueWith(_ => { lock (_dispatchLock) _dispatch.Remove(job); }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }
    public async Task DrainAsync(CancellationToken token = default)
    {
        Task[] tasks; lock (_dispatchLock) tasks = _dispatch.Values.ToArray();
        await Task.WhenAll(tasks).WaitAsync(token).ConfigureAwait(false);
    }
    internal static bool ValidKey(ExternalRequestKey key) => key.CallerKey is { Length: > 0 and <= 200 }
        && !key.CallerKey.Any(char.IsControl) && key.InstallationId != Guid.Empty && key.RequestId != Guid.Empty;
    internal static ContractParseResult<RequestEnvelope> Normalize(ExternalDownloadRequest request)
    {
        if (request.SourceUrl is null || request.SourceUrl.Length > ProtocolLimits.MaximumUrlBytes || request.SuggestedFileName?.Length > 240)
            return new(null, RejectionReason.InvalidRequest, null);
        var json = JsonSerializer.SerializeToUtf8Bytes(new { protocol = new { major = 1, minor = 0 }, requestId = request.Key.RequestId,
            action = "addPublicGet", payload = new { installationId = request.Key.InstallationId,
                createdAt = request.CreatedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", System.Globalization.CultureInfo.InvariantCulture),
                sourceUrl = request.SourceUrl, suggestedFileName = request.SuggestedFileName, trigger = request.Trigger } },
            new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
        return ContractJson.ParseRequest(json);
    }
    private static string Digest(ExternalDownloadRequest request) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
    { version = 1, createdAtTicks = request.CreatedAt.UtcTicks, sourceUrl = request.SourceUrl, suggestedFileName = request.SuggestedFileName, trigger = request.Trigger })));

    // String-only literal admission; DNS and redirect/rebinding enforcement remains a transport-level gap.
    internal static bool IsBlockedLiteral(Uri source)
    {
        if (source.IsLoopback) return true;
        if (!IPAddress.TryParse(source.DnsSafeHost.Trim('[', ']'), out var address)) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        var bytes = address.GetAddressBytes();
        if (bytes.Length == 4) return bytes[0] is 0 or 10 or 127 or >= 224 || bytes[0] == 169 && bytes[1] == 254
            || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 || bytes[0] == 192 && bytes[1] == 168;
        return address.Equals(IPAddress.IPv6Any) || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast || (bytes[0] & 0xfe) == 0xfc;
    }
}
