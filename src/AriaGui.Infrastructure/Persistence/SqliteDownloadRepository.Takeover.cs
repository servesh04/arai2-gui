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
    public const int PreparedCleanupBatchSize = 64;
    public const int PreparedPerCallerInstallationLimit = 16;
    public const int PreparedPerCallerLimit = 32;
    public const int PreparedPerInstallationLimit = 64;
    public const int PreparedGlobalLimit = 256;
    private int ExpirePrepared(DownloadDbContext db)
    {
        var now = TakeoverNow;
        var rows = db.ExternalRequests.Where(row => row.TakeoverPhase == PreparedTakeoverPhase.Prepared && row.PreparedExpiresAtUtcTicks <= now.UtcTicks)
            .OrderBy(row => row.PreparedExpiresAtUtcTicks).Take(PreparedCleanupBatchSize).ToArray();
        foreach (var row in rows)
            EndPrepared(db, row, PreparedTakeoverPhase.Expired, now);
        db.SaveChanges();
        return rows.Length;
    }
    public Task<int> CleanupPreparedTakeoversAsync(CancellationToken token) => OnDatabaseThreadAsync(db =>
    {
        using var transaction = db.Database.BeginTransaction();
        var count = ExpirePrepared(db); transaction.Commit(); return count;
    }, token);
    private void ExpireTarget(DownloadDbContext db, ExternalRequestRow? row)
    {
        var now = TakeoverNow;
        if (row is { TakeoverPhase: PreparedTakeoverPhase.Prepared } && row.PreparedExpiresAtUtcTicks <= now.UtcTicks)
        { EndPrepared(db, row, PreparedTakeoverPhase.Expired, now); db.SaveChanges(); }
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
            try
            {
                var envelope = JsonSerializer.Deserialize<PreparedIntentEnvelope>(row.PreparedIntentJson);
                if (envelope?.Version != 1) throw new DownloadStorageException("Prepared intent schema is unsupported.");
                entry = envelope.Entry;
            }
            catch (JsonException) { throw new DownloadStorageException("Prepared intent is invalid."); }
            if (entry is not { Job: not null, Attempt: not null, File: not null } || entry.ExactSourceUrl is null || entry.PendingOperation is not { Action: DownloadAction.Add, Status: OperationStatus.Pending }
                || entry.Progress.Status != DownloadStatus.Queued || entry.Job.Id == Guid.Empty || entry.Attempt.Id == Guid.Empty
                || entry.Job.Id != entry.Attempt.JobId || entry.Job.Id != entry.File.JobId || entry.Job.Id != entry.PendingOperation.JobId
                || entry.Attempt.Id != entry.File.AttemptId || entry.ExactSourceUrl != entry.Job.Source.OriginalString)
                throw new DownloadStorageException("Prepared intent is invalid.");
        }
        return new(new(row.CallerKey, row.InstallationId, row.RequestId), row.PayloadDigest, row.TakeoverPhase.Value,
            row.PreparedExpiresAtUtcTicks is { } expiry ? At(expiry) : null, entry);
    }
    public Task<PreparedTakeoverInsert> PrepareTakeoverAsync(ExternalDownloadRequest request, string digest, DownloadEntry candidate, CancellationToken token) => OnDatabaseThreadAsync(db =>
    {
        using var transaction = db.Database.BeginTransaction(); // SQLite serializes competing writers before checking expiry/time.
        ExpirePrepared(db);
        if (Find(db, request.Key) is { } known) { ExpireTarget(db, known); var duplicate = ReadTakeover(known); transaction.Commit(); return new PreparedTakeoverInsert(duplicate, false); }
        var active = db.ExternalRequests.Where(row => row.TakeoverPhase == PreparedTakeoverPhase.Prepared);
        if (active.Take(PreparedGlobalLimit).Count() >= PreparedGlobalLimit
            || active.Where(row => row.CallerKey == request.Key.CallerKey).Take(PreparedPerCallerLimit).Count() >= PreparedPerCallerLimit
            || active.Where(row => row.InstallationId == request.Key.InstallationId).Take(PreparedPerInstallationLimit).Count() >= PreparedPerInstallationLimit
            || active.Where(row => row.CallerKey == request.Key.CallerKey && row.InstallationId == request.Key.InstallationId).Take(PreparedPerCallerInstallationLimit).Count() >= PreparedPerCallerInstallationLimit)
            throw new PreparedTakeoverCapacityException();
        var destination = candidate.Job.Destination.ToUpperInvariant();
        if (db.Jobs.Any(row => row.DestinationKey == destination) || db.PreparedDestinations.Any(row => row.DestinationKey == destination))
            throw new DownloadRequestException("Destination conflict.", RejectionReason.DestinationConflict);
        var json = JsonSerializer.Serialize(new PreparedIntentEnvelope(1, candidate));
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
        var row = Find(db, key);
        if (row is { TakeoverPhase: PreparedTakeoverPhase.Prepared } && row.PreparedExpiresAtUtcTicks <= TakeoverNow.UtcTicks)
        {
            using var transaction = db.Database.BeginTransaction();
            db.ChangeTracker.Clear(); row = Find(db, key); ExpireTarget(db, row); transaction.Commit();
        }
        return row?.TakeoverPhase is null ? null : ReadTakeover(row);
    }, token);
    public Task<PreparedTakeoverCommit?> CommitTakeoverAsync(ExternalRequestKey key, string digest, CancellationToken token) => OnDatabaseThreadAsync(db =>
    {
        using var transaction = db.Database.BeginTransaction();
        var row = Find(db, key); if (row?.TakeoverPhase is null) { transaction.Commit(); return null; }
        ExpireTarget(db, row);
        var record = ReadTakeover(row);
        if (row.PayloadDigest != digest || row.TakeoverPhase != PreparedTakeoverPhase.Prepared) { transaction.Commit(); return new PreparedTakeoverCommit(record, false); }
        var candidate = record.Candidate!;
        // Remove reservation and create all existing pipeline rows under this one write transaction.
        db.PreparedDestinations.Where(value => value.CallerKey == key.CallerKey && value.InstallationId == key.InstallationId && value.RequestId == key.RequestId).ExecuteDelete();
        AddIntentRows(db, candidate);
        row.Code = ResultCode.Accepted; row.JobId = candidate.Job.Id; row.TakeoverPhase = PreparedTakeoverPhase.Committed;
        row.PreparedIntentJson = null; row.PreparedIntentHash = null; row.UpdatedAtUtcTicks = TakeoverNow.UtcTicks;
        takeoverHooks?.Boundary?.Invoke("commit-before-commit"); token.ThrowIfCancellationRequested(); db.SaveChanges(); transaction.Commit();
        takeoverHooks?.Boundary?.Invoke("commit-after-commit"); return new PreparedTakeoverCommit(ReadTakeover(row), true);
    }, token);
    public Task<PreparedTakeoverRecord?> AbortTakeoverAsync(ExternalRequestKey key, CancellationToken token) => OnDatabaseThreadAsync(db =>
    {
        using var transaction = db.Database.BeginTransaction();
        var row = Find(db, key); if (row?.TakeoverPhase is null) { transaction.Commit(); return null; }
        ExpireTarget(db, row);
        if (row.TakeoverPhase == PreparedTakeoverPhase.Prepared) EndPrepared(db, row, PreparedTakeoverPhase.Aborted, TakeoverNow);
        takeoverHooks?.Boundary?.Invoke("abort-before-commit"); token.ThrowIfCancellationRequested(); db.SaveChanges(); transaction.Commit();
        takeoverHooks?.Boundary?.Invoke("abort-after-commit"); return ReadTakeover(row);
    }, token);
}
