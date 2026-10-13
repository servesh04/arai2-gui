using AriaGui.Application.Downloads;
using AriaGui.Browser.Contracts;
using AriaGui.Core.Downloads;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;

namespace AriaGui.Infrastructure.Persistence;

// Optional deterministic transaction-boundary injection, used only by isolated tests. No runtime activation.
public sealed record ExternalIntakePersistenceHooks(Action? BeforeCommit = null, Action? AfterCommit = null);

public sealed partial class SqliteDownloadRepository
{
    private static ExternalRequestRow? Find(DownloadDbContext db, ExternalRequestKey key) => db.ExternalRequests
        .SingleOrDefault(row => row.CallerKey == key.CallerKey && row.InstallationId == key.InstallationId && row.RequestId == key.RequestId);
    private static ExternalRequestRecord Read(ExternalRequestRow row) => new(new(row.CallerKey, row.InstallationId, row.RequestId),
        row.PayloadDigest, At(row.CreatedAtUtcTicks), row.Code, row.JobId, row.Reason, At(row.UpdatedAtUtcTicks),
        row.TombstonedAtUtcTicks is { } tombstone ? At(tombstone) : null, row.DispatchPhase);
    public Task<ExternalRequestRecord?> FindExternalAsync(ExternalRequestKey key, CancellationToken token) =>
        OnDatabaseThreadAsync(db => Find(db, key) is { } row ? Read(row) : null, token);

    public Task<ExternalInsertResult> InsertExternalAsync(ExternalRequestRecord record, DownloadEntry? entry, CancellationToken token) => OnDatabaseThreadAsync(db =>
    {
        using var transaction = db.Database.BeginTransaction();
        ExpirePrepared(db);
        // Reservation checks and ledger/job rows share this write transaction.
        if (Find(db, record.Key) is { } existing) { transaction.Commit(); return new ExternalInsertResult(Read(existing), false); }
        if ((record.Code == ResultCode.Accepted) != (entry is not null) || record.JobId != entry?.Job.Id)
            throw new InvalidOperationException("External intent and receipt must match.");
        if (entry is not null)
        {
            if (db.PreparedDestinations.Any(row => row.DestinationKey == entry.Job.Destination.ToUpperInvariant()))
                throw new DownloadRequestException("Destination is reserved.", RejectionReason.DestinationConflict);
            AddIntentRows(db, entry);
        }
        db.ExternalRequests.Add(new() { CallerKey = record.Key.CallerKey, InstallationId = record.Key.InstallationId,
            RequestId = record.Key.RequestId, PayloadDigest = record.PayloadDigest, CreatedAtUtcTicks = record.CreatedAt.UtcTicks,
            UpdatedAtUtcTicks = record.UpdatedAt.UtcTicks, Code = record.Code, JobId = record.JobId, Reason = record.Reason });
        try
        {
            externalHooks?.BeforeCommit?.Invoke(); token.ThrowIfCancellationRequested();
            db.SaveChanges();
            transaction.Commit();
            externalHooks?.AfterCommit?.Invoke();
            return new ExternalInsertResult(record, true);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: 19 })
        {
            transaction.Rollback();
            // Independent contexts/processes converge at the composite database key.
            db.ChangeTracker.Clear();
            if (Find(db, record.Key) is { } winner) return new ExternalInsertResult(Read(winner), false);
            if (entry is not null && db.Jobs.Any(row => row.DestinationKey == entry.Job.Destination.ToUpperInvariant()))
                throw new DownloadRequestException("Destination conflict.");
            throw;
        }
    }, token);
    public Task<IReadOnlyList<Guid>> LoadNeverSentExternalJobsAsync(CancellationToken token) => OnDatabaseThreadAsync<IReadOnlyList<Guid>>(db =>
        db.ExternalRequests.Where(row => row.Code == ResultCode.Accepted && row.JobId != null && row.DispatchPhase == ExternalDispatchPhase.NeverSent)
            .Select(row => row.JobId!.Value).ToArray(), token);
    public Task<bool> ClaimExternalDispatchAsync(Guid jobId, DateTimeOffset now, CancellationToken token) => OnDatabaseThreadAsync(db =>
        db.ExternalRequests.Where(row => row.JobId == jobId && row.Code == ResultCode.Accepted && row.DispatchPhase == ExternalDispatchPhase.NeverSent)
            .ExecuteUpdate(setters => setters.SetProperty(row => row.DispatchPhase, ExternalDispatchPhase.Claimed).SetProperty(row => row.UpdatedAtUtcTicks, now.UtcTicks)) == 1, token);
    public Task DeleteTerminalHistoryAsync(Guid jobId, DateTimeOffset now, CancellationToken token) => OnDatabaseThreadAsync(db =>
    {
        using var transaction = db.Database.BeginTransaction();
        var attempts = db.Attempts.Where(row => row.JobId == jobId).ToArray();
        if (attempts.Any(row => row.Status != DownloadStatus.Completed && row.Status != DownloadStatus.Failed && row.Status != DownloadStatus.Removed)
            || db.Operations.Any(row => row.JobId == jobId && (row.Status == OperationStatus.Pending || row.Status == OperationStatus.Uncertain)))
            throw new DownloadRequestException("Reconcile and stop this job before deleting history.");
        foreach (var row in db.ExternalRequests.Where(row => row.JobId == jobId))
        { row.JobId = null; row.Code = ResultCode.Expired; row.TombstonedAtUtcTicks = now.UtcTicks; row.UpdatedAtUtcTicks = now.UtcTicks; }
        db.SaveChanges(); // Detach the restricted FK within the SAME transaction before deleting the job.
        db.Files.Where(row => row.JobId == jobId).ExecuteDelete(); db.Operations.Where(row => row.JobId == jobId).ExecuteDelete();
        // ParentAttemptId has a restrictive self-FK; clear links before deleting all attempts.
        db.Attempts.Where(row => row.JobId == jobId).ExecuteUpdate(setters => setters.SetProperty(row => row.ParentAttemptId, (Guid?)null));
        db.Attempts.Where(row => row.JobId == jobId).ExecuteDelete(); db.Jobs.Where(row => row.Id == jobId).ExecuteDelete();
        transaction.Commit(); return true;
    }, token);
    public Task PruneExternalRecordsAsync(DateTimeOffset now, CancellationToken token) => OnDatabaseThreadAsync(db =>
    {
        db.ExternalRequests.Where(row => row.Code == ResultCode.Expired && row.TombstonedAtUtcTicks < now.AddDays(-90).UtcTicks
            || row.Code == ResultCode.Rejected && row.UpdatedAtUtcTicks < now.AddDays(-7).UtcTicks).ExecuteDelete();
        return true;
    }, token);
}
