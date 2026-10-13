using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AriaGui.Application.Downloads;
using AriaGui.Browser.Contracts;
using AriaGui.Core.Downloads;
using Microsoft.EntityFrameworkCore;

namespace AriaGui.Infrastructure.Persistence;

// Deterministic fault injection is constructor-only, never activated by request/config JSON.
public sealed record PreparedTakeoverPersistenceHooks(Action<string>? Boundary = null);
public sealed partial class SqliteDownloadRepository
{
    private DateTimeOffset TakeoverNow => (takeoverClock ?? TimeProvider.System).GetUtcNow();
    private void ExpirePrepared(DownloadDbContext db)
    {
        var now = TakeoverNow;
        foreach (var row in db.ExternalRequests.Where(row => row.TakeoverPhase == PreparedTakeoverPhase.Prepared && row.PreparedExpiresAtUtcTicks <= now.UtcTicks))
            EndPrepared(db, row, PreparedTakeoverPhase.Expired, now);
        db.SaveChanges();
    }
    private static void EndPrepared(DownloadDbContext db, ExternalRequestRow row, PreparedTakeoverPhase phase, DateTimeOffset now)
    {
        db.PreparedDestinations.Where(value => value.CallerKey == row.CallerKey && value.InstallationId == row.InstallationId && value.RequestId == row.RequestId).ExecuteDelete();
        row.TakeoverPhase = phase; row.Code = ResultCode.Expired; row.TombstonedAtUtcTicks = now.UtcTicks; row.UpdatedAtUtcTicks = now.UtcTicks;
        row.PreparedIntentJson = null; row.PreparedIntentHash = null;
    }
    private static PreparedTakeoverRecord ReadTakeover(ExternalRequestRow row)
    {
        if (row.TakeoverPhase is null) throw new DownloadRequestException("Request identity already belongs to explicit intake.", RejectionReason.RequestIdConflict);
        DownloadEntry? entry = null;
        if (row.TakeoverPhase == PreparedTakeoverPhase.Prepared)
        {
            if (row.PreparedIntentJson is null || Encoding.UTF8.GetByteCount(row.PreparedIntentJson) > 65536
                || Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(row.PreparedIntentJson))) != row.PreparedIntentHash)
                throw new DownloadStorageException("Prepared intent is invalid.");
            try { entry = JsonSerializer.Deserialize<DownloadEntry>(row.PreparedIntentJson); }
            catch (JsonException) { throw new DownloadStorageException("Prepared intent is invalid."); }
            if (entry is null || entry.ExactSourceUrl is null || entry.PendingOperation is not { Action: DownloadAction.Add, Status: OperationStatus.Pending }
                || entry.Progress.Status != DownloadStatus.Queued || entry.Job.Id != entry.Attempt.JobId || entry.Attempt.Id != entry.File.AttemptId)
                throw new DownloadStorageException("Prepared intent is invalid.");
        }
        return new(new(row.CallerKey, row.InstallationId, row.RequestId), row.PayloadDigest, row.TakeoverPhase.Value,
            row.PreparedExpiresAtUtcTicks is { } expiry ? At(expiry) : null, entry);
    }
    public Task<PreparedTakeoverInsert> PrepareTakeoverAsync(ExternalDownloadRequest request, string digest, DownloadEntry candidate, CancellationToken token) => OnDatabaseThreadAsync(db =>
    {
        using var transaction = db.Database.BeginTransaction(); // SQLite serializes competing writers before checking expiry/time.
        ExpirePrepared(db);
        if (Find(db, request.Key) is { } known) { var duplicate = ReadTakeover(known); transaction.Commit(); return new PreparedTakeoverInsert(duplicate, false); }
        var destination = candidate.Job.Destination.ToUpperInvariant();
        if (db.Jobs.Any(row => row.DestinationKey == destination) || db.PreparedDestinations.Any(row => row.DestinationKey == destination))
            throw new DownloadRequestException("Destination conflict.", RejectionReason.DestinationConflict);
        var json = JsonSerializer.Serialize(candidate);
        if (Encoding.UTF8.GetByteCount(json) > 65536) throw new DownloadRequestException("Prepared request is too large.", RejectionReason.InvalidRequest);
        var now = TakeoverNow;
        var row = new ExternalRequestRow { CallerKey = request.Key.CallerKey, InstallationId = request.Key.InstallationId, RequestId = request.Key.RequestId,
            PayloadDigest = digest, CreatedAtUtcTicks = request.CreatedAt.UtcTicks, UpdatedAtUtcTicks = now.UtcTicks, Code = ResultCode.Processing,
            TakeoverPhase = PreparedTakeoverPhase.Prepared, PreparedExpiresAtUtcTicks = now.AddSeconds(60).UtcTicks,
            PreparedIntentJson = json, PreparedIntentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))) };
        db.ExternalRequests.Add(row);
        db.PreparedDestinations.Add(new() { DestinationKey = destination, CallerKey = request.Key.CallerKey, InstallationId = request.Key.InstallationId, RequestId = request.Key.RequestId });
        takeoverHooks?.Boundary?.Invoke("prepare-before-commit"); token.ThrowIfCancellationRequested(); db.SaveChanges(); transaction.Commit();
        takeoverHooks?.Boundary?.Invoke("prepare-after-commit");
        return new PreparedTakeoverInsert(ReadTakeover(row), true);
    }, token);
    public Task<PreparedTakeoverRecord?> FindTakeoverAsync(ExternalRequestKey key, CancellationToken token) => OnDatabaseThreadAsync(db =>
    {
        using var transaction = db.Database.BeginTransaction(); ExpirePrepared(db);
        var row = Find(db, key); var result = row?.TakeoverPhase is null ? null : ReadTakeover(row);
        transaction.Commit(); return result;
    }, token);
    public Task<PreparedTakeoverRecord?> CommitTakeoverAsync(ExternalRequestKey key, string digest, CancellationToken token) => OnDatabaseThreadAsync(db =>
    {
        using var transaction = db.Database.BeginTransaction(); ExpirePrepared(db);
        var row = Find(db, key); if (row?.TakeoverPhase is null) { transaction.Commit(); return null; }
        var record = ReadTakeover(row);
        if (row.PayloadDigest != digest || row.TakeoverPhase != PreparedTakeoverPhase.Prepared) { transaction.Commit(); return record; }
        var candidate = record.Candidate!;
        // Remove reservation and create all existing pipeline rows under this one write transaction.
        db.PreparedDestinations.Where(value => value.CallerKey == key.CallerKey && value.InstallationId == key.InstallationId && value.RequestId == key.RequestId).ExecuteDelete();
        AddIntentRows(db, candidate);
        row.Code = ResultCode.Accepted; row.JobId = candidate.Job.Id; row.TakeoverPhase = PreparedTakeoverPhase.Committed;
        row.PreparedIntentJson = null; row.PreparedIntentHash = null; row.UpdatedAtUtcTicks = TakeoverNow.UtcTicks;
        takeoverHooks?.Boundary?.Invoke("commit-before-commit"); token.ThrowIfCancellationRequested(); db.SaveChanges(); transaction.Commit();
        takeoverHooks?.Boundary?.Invoke("commit-after-commit"); return ReadTakeover(row);
    }, token);
    public Task<PreparedTakeoverRecord?> AbortTakeoverAsync(ExternalRequestKey key, CancellationToken token) => OnDatabaseThreadAsync(db =>
    {
        using var transaction = db.Database.BeginTransaction(); ExpirePrepared(db);
        var row = Find(db, key); if (row?.TakeoverPhase is null) { transaction.Commit(); return null; }
        if (row.TakeoverPhase == PreparedTakeoverPhase.Prepared) EndPrepared(db, row, PreparedTakeoverPhase.Aborted, TakeoverNow);
        takeoverHooks?.Boundary?.Invoke("abort-before-commit"); token.ThrowIfCancellationRequested(); db.SaveChanges(); transaction.Commit();
        takeoverHooks?.Boundary?.Invoke("abort-after-commit"); return ReadTakeover(row);
    }, token);
}
