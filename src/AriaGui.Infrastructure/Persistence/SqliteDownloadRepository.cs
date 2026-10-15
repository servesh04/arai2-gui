using AriaGui.Application.Downloads;
using AriaGui.Core.Downloads;
using AriaGui.Infrastructure.Aria2;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace AriaGui.Infrastructure.Persistence;

public sealed partial class SqliteDownloadRepository(string databasePath, ExternalIntakePersistenceHooks? externalHooks = null,
    TimeProvider? takeoverClock = null, PreparedTakeoverPersistenceHooks? takeoverHooks = null) : IDownloadRepository, IExternalRequestRepository, IPreparedTakeoverRepository
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DownloadDbContext CreateContext() => new(new DbContextOptionsBuilder<DownloadDbContext>()
        .UseSqlite(new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false, DefaultTimeout = 5 }.ToString()).Options);

    public Task InitializeAsync(CancellationToken token) => OnDatabaseThreadAsync(db =>
    {
        UserRuntimeDirectory.EnsurePrivate(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        DatabaseSchemaUpgrade.Apply(db, databasePath);
        db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
        using var transaction = db.Database.BeginTransaction();
        ExpirePrepared(db); transaction.Commit(); // Bounded startup cleanup, independent of new intake.
        return true;
    }, token);

    public Task<IReadOnlyList<DownloadEntry>> LoadAsync(CancellationToken token) => OnDatabaseThreadAsync<IReadOnlyList<DownloadEntry>>(db =>
    {
        var jobs = db.Jobs.AsNoTracking().OrderBy(row => row.CreatedAtUtcTicks).ToArray();
        var attempts = db.Attempts.AsNoTracking().ToArray();
        var files = db.Files.AsNoTracking().ToArray();
        var operations = db.Operations.AsNoTracking().ToArray();
        var externalJobs = db.ExternalRequests.Where(row => row.JobId != null).Select(row => row.JobId!.Value).ToHashSet();
        return jobs.Select(job =>
        {
            var attempt = attempts.Where(row => row.JobId == job.Id).MaxBy(row => row.StartedAtUtcTicks)
                ?? throw new DownloadStorageException("Saved history contains an incomplete job. Keep the database for diagnosis.");
            var file = files.Single(row => row.AttemptId == attempt.Id);
            var operation = operations.Where(row => row.AttemptId == attempt.Id && row.Status is OperationStatus.Pending or OperationStatus.Uncertain)
                .MaxBy(row => row.RequestedAtUtcTicks);
            return new DownloadEntry(new(job.Id, job.DisplayName, new Uri(job.Source), job.Destination, At(job.CreatedAtUtcTicks),
                    job.ExpectedSha256 is null ? null : new ExpectedChecksum(job.ExpectedSha256, job.ChecksumProvenance ?? 0)),
                new(attempt.Id, job.Id, attempt.EngineId, At(attempt.StartedAtUtcTicks), attempt.ParentAttemptId),
                new(file.Id, job.Id, attempt.Id, file.FileName, file.TotalBytes),
                new(attempt.Status, attempt.DownloadedBytes, attempt.TotalBytes, 0),
                operation is null ? null : new(operation.Id, job.Id, operation.Action, operation.Status, At(operation.RequestedAtUtcTicks), operation.QueuePosition),
                attempt.ErrorMessage, DesiredPaused: attempt.DesiredPaused, DesiredRemoved: attempt.DesiredRemoved, ObservedAt: At(attempt.UpdatedAtUtcTicks),
                ExactSourceUrl: externalJobs.Contains(job.Id) ? job.Source : null, PolicyHeld: attempt.PolicyHeld,
                Verification: attempt.Verification);
        }).ToArray();
    }, token);

    public Task InsertIntentAsync(DownloadEntry entry, CancellationToken token) => OnDatabaseThreadAsync(db =>
    {
        using var transaction = db.Database.BeginTransaction();
        ExpirePrepared(db);
        if (db.PreparedDestinations.Any(row => row.DestinationKey == entry.Job.Destination.ToUpperInvariant()))
            throw new DownloadRequestException("This destination is reserved by a prepared browser handoff.", AriaGui.Browser.Contracts.RejectionReason.DestinationConflict);
        AddIntentRows(db, entry);
        // Reservation admission and all four inserts commit atomically.
        db.SaveChanges();
        transaction.Commit();
        return true;
    }, token);

    private static void AddIntentRows(DownloadDbContext db, DownloadEntry entry)
    {
        var operation = entry.PendingOperation ?? throw new InvalidOperationException("An Add intent is required.");
        db.Jobs.Add(new() { Id = entry.Job.Id, DisplayName = entry.Job.DisplayName, Source = entry.ExactSourceUrl ?? entry.Job.Source.AbsoluteUri,
            Destination = entry.Job.Destination, DestinationKey = entry.Job.Destination.ToUpperInvariant(), CreatedAtUtcTicks = entry.Job.CreatedAt.UtcTicks,
            ExpectedSha256 = entry.Job.ExpectedChecksum?.Sha256, ChecksumProvenance = entry.Job.ExpectedChecksum?.Provenance });
        db.Attempts.Add(new() { Id = entry.Attempt.Id, JobId = entry.Job.Id, EngineId = entry.Attempt.EngineId,
            StartedAtUtcTicks = entry.Attempt.StartedAt.UtcTicks, ParentAttemptId = entry.Attempt.ParentAttemptId,
            Status = entry.Progress.Status, Verification = entry.Verification, UpdatedAtUtcTicks = entry.Attempt.StartedAt.UtcTicks, PolicyHeld = entry.PolicyHeld });
        db.Files.Add(new() { Id = entry.File.Id, JobId = entry.Job.Id, AttemptId = entry.Attempt.Id, FileName = entry.File.FileName });
        db.Operations.Add(new() { Id = operation.Id, JobId = entry.Job.Id, AttemptId = entry.Attempt.Id, Action = operation.Action,
            Status = operation.Status, RequestedAtUtcTicks = operation.RequestedAt.UtcTicks });
    }

    public async Task SaveAsync(IReadOnlyList<DownloadEntry> entries, CancellationToken token) =>
        _ = await SaveAndReportCompletionsAsync(entries, token).ConfigureAwait(false);

    public Task<IReadOnlyList<Guid>> SaveAndReportCompletionsAsync(IReadOnlyList<DownloadEntry> entries, CancellationToken token) => OnDatabaseThreadAsync<IReadOnlyList<Guid>>(db =>
    {
        var completed = new List<Guid>();
        var ids = entries.Select(entry => entry.Attempt.Id).ToArray();
        var attempts = db.Attempts.Where(row => ids.Contains(row.Id)).ToDictionary(row => row.Id);
        var files = db.Files.Where(row => ids.Contains(row.AttemptId)).ToDictionary(row => row.Id);
        var pending = db.Operations.Where(row => ids.Contains(row.AttemptId)
            && (row.Status == OperationStatus.Pending || row.Status == OperationStatus.Uncertain)).ToArray().ToLookup(row => row.AttemptId);
        foreach (var entry in entries)
        {
            var attempt = attempts[entry.Attempt.Id];
            // The attempt's persisted terminal transition is the notification receipt.
            if (attempt.Status != DownloadStatus.Completed && entry.Progress.Status == DownloadStatus.Completed) completed.Add(attempt.Id);
            attempt.Status = entry.Progress.Status;
            attempt.Verification = entry.Verification;
            attempt.DownloadedBytes = entry.Progress.DownloadedBytes;
            attempt.TotalBytes = entry.Progress.TotalBytes;
            attempt.ErrorMessage = entry.ErrorMessage;
            attempt.DesiredPaused = entry.DesiredPaused;
            attempt.DesiredRemoved = entry.DesiredRemoved;
            attempt.PolicyHeld = entry.PolicyHeld;
            attempt.UpdatedAtUtcTicks = entry.ObservedAt?.UtcTicks ?? DateTimeOffset.UtcNow.UtcTicks;
            files[entry.File.Id].TotalBytes = entry.File.TotalBytes;
            foreach (var operation in pending[entry.Attempt.Id])
                operation.Status = entry.PendingOperation?.Status ?? OperationStatus.Confirmed;
        }
        db.SaveChanges();
        return completed;
    }, token);

    public Task InsertOperationAsync(DownloadEntry entry, CancellationToken token) => OnDatabaseThreadAsync(db =>
    {
        var operation = entry.PendingOperation ?? throw new InvalidOperationException("Command intent is required.");
        if (db.Operations.Any(row => row.AttemptId == entry.Attempt.Id && (row.Status == OperationStatus.Pending || row.Status == OperationStatus.Uncertain)))
            throw new DownloadRequestException("Resolve the previous command before issuing another.");
        db.Operations.Add(new() { Id = operation.Id, JobId = entry.Job.Id, AttemptId = entry.Attempt.Id,
            Action = operation.Action, Status = operation.Status, RequestedAtUtcTicks = operation.RequestedAt.UtcTicks,
            QueuePosition = operation.QueuePosition });
        var attempt = db.Attempts.Single(row => row.Id == entry.Attempt.Id);
        attempt.DesiredPaused = entry.DesiredPaused;
        attempt.DesiredRemoved = entry.DesiredRemoved;
        attempt.PolicyHeld = entry.PolicyHeld;
        db.SaveChanges();
        return true;
    }, token);

    public Task InsertRecoveryOperationAsync(DownloadEntry entry, CancellationToken token) => OnDatabaseThreadAsync(db =>
    {
        foreach (var previous in db.Operations.Where(row => row.AttemptId == entry.Attempt.Id &&
            (row.Status == OperationStatus.Pending || row.Status == OperationStatus.Uncertain))) previous.Status = OperationStatus.Failed;
        var operation = entry.PendingOperation!;
        db.Operations.Add(new() { Id = operation.Id, JobId = entry.Job.Id, AttemptId = entry.Attempt.Id,
            Action = DownloadAction.Recover, Status = OperationStatus.Pending, RequestedAtUtcTicks = operation.RequestedAt.UtcTicks,
            QueuePosition = operation.QueuePosition });
        db.SaveChanges();
        return true;
    }, token);

    public Task InsertRetryIntentAsync(DownloadEntry previous, DownloadEntry retry, CancellationToken token) => OnDatabaseThreadAsync(db =>
    {
        var old = db.Attempts.Single(row => row.Id == previous.Attempt.Id);
        if (old.Status is not (DownloadStatus.Failed or DownloadStatus.Removed)
            || db.Operations.Any(row => row.JobId == previous.Job.Id && (row.Status == OperationStatus.Pending || row.Status == OperationStatus.Uncertain)))
            throw new DownloadRequestException("The previous attempt must be stopped and reconciled before retrying.");
        var operation = retry.PendingOperation!;
        db.Attempts.Add(new() { Id = retry.Attempt.Id, JobId = retry.Job.Id, EngineId = retry.Attempt.EngineId,
            StartedAtUtcTicks = retry.Attempt.StartedAt.UtcTicks, ParentAttemptId = previous.Attempt.Id,
            Status = DownloadStatus.Queued, Verification = retry.Verification, UpdatedAtUtcTicks = retry.Attempt.StartedAt.UtcTicks, PolicyHeld = retry.PolicyHeld });
        db.Files.Add(new() { Id = retry.File.Id, JobId = retry.Job.Id, AttemptId = retry.Attempt.Id, FileName = retry.File.FileName });
        db.Operations.Add(new() { Id = operation.Id, JobId = retry.Job.Id, AttemptId = retry.Attempt.Id,
            Action = DownloadAction.Retry, Status = OperationStatus.Pending, RequestedAtUtcTicks = operation.RequestedAt.UtcTicks });
        db.SaveChanges();
        return true;
    }, token);

    public Task InsertFreshRestartIntentAsync(DownloadEntry previous, DownloadEntry retry, CancellationToken token) => OnDatabaseThreadAsync(db =>
    {
        var old = db.Attempts.Single(row => row.Id == previous.Attempt.Id);
        if (old.Status != DownloadStatus.Interrupted || retry.Attempt.ParentAttemptId != old.Id
            || db.Attempts.Any(row => row.JobId == old.JobId && row.StartedAtUtcTicks > old.StartedAtUtcTicks)
            || db.Operations.Any(row => row.JobId == old.JobId && (row.Status == OperationStatus.Pending
                || row.Status == OperationStatus.Uncertain && row.AttemptId != old.Id)))
            throw new DownloadRequestException("The interrupted attempt must be checked before restarting.");
        // The coordinator and engine independently prove no old live writer.
        // Supersession and the new intent commit together; retain old attempt history.
        foreach (var operation in db.Operations.Where(row => row.AttemptId == old.Id && row.Status == OperationStatus.Uncertain))
            operation.Status = OperationStatus.Failed;
        var intent = retry.PendingOperation!;
        db.Attempts.Add(new() { Id = retry.Attempt.Id, JobId = retry.Job.Id, EngineId = retry.Attempt.EngineId,
            StartedAtUtcTicks = retry.Attempt.StartedAt.UtcTicks, ParentAttemptId = old.Id,
            Status = DownloadStatus.Queued, Verification = retry.Verification, UpdatedAtUtcTicks = retry.Attempt.StartedAt.UtcTicks, PolicyHeld = retry.PolicyHeld });
        db.Files.Add(new() { Id = retry.File.Id, JobId = retry.Job.Id, AttemptId = retry.Attempt.Id, FileName = retry.File.FileName });
        db.Operations.Add(new() { Id = intent.Id, JobId = retry.Job.Id, AttemptId = retry.Attempt.Id,
            Action = DownloadAction.Retry, Status = OperationStatus.Pending, RequestedAtUtcTicks = intent.RequestedAt.UtcTicks });
        db.SaveChanges(); return true;
    }, token);

    public Task<QueuePolicy> LoadQueuePolicyAsync(CancellationToken token) => OnDatabaseThreadAsync(db =>
    {
        var row = db.QueuePolicies.AsNoTracking().SingleOrDefault(row => row.Id == 1);
        return row is null ? new QueuePolicy() : new(row.Limit, row.RequestedLimit, row.OperationId, row.RequestStatus);
    }, token);

    public Task SaveQueuePolicyAsync(QueuePolicy policy, CancellationToken token) => OnDatabaseThreadAsync(db =>
    {
        var row = db.QueuePolicies.SingleOrDefault(row => row.Id == 1);
        if (row is null) { row = new() { Id = 1 }; db.QueuePolicies.Add(row); }
        row.Limit = policy.Limit;
        row.RequestedLimit = policy.RequestedLimit;
        row.OperationId = policy.OperationId;
        row.RequestStatus = policy.RequestStatus;
        db.SaveChanges();
        return true;
    }, token);

    public Task<BandwidthPolicy> LoadBandwidthPolicyAsync(CancellationToken token) => OnDatabaseThreadAsync(db =>
    {
        var row = db.BandwidthPolicies.AsNoTracking().SingleOrDefault(row => row.Id == 1);
        if (row is null) return BandwidthPolicy.Default;
        var policy = new BandwidthPolicy(row.RequestedRevision, row.ConfirmedRevision,
            new(row.RequestedName, new(row.RequestedDownloadBytesPerSecond), new(row.RequestedUploadBytesPerSecond)),
            new(row.ConfirmedName, new(row.ConfirmedDownloadBytesPerSecond), new(row.ConfirmedUploadBytesPerSecond)),
            row.ConfirmedGeneration, row.RequestStatus);
        policy.Validate();
        return policy;
    }, token);

    public Task SaveBandwidthPolicyAsync(BandwidthPolicy policy, CancellationToken token) => OnDatabaseThreadAsync(db =>
    {
        policy.Validate();
        var row = db.BandwidthPolicies.SingleOrDefault(row => row.Id == 1);
        if (row is null) { row = new() { Id = 1 }; db.BandwidthPolicies.Add(row); }
        row.RequestedRevision = policy.RequestedRevision;
        row.ConfirmedRevision = policy.ConfirmedRevision;
        row.RequestedName = policy.Requested.Name;
        row.RequestedDownloadBytesPerSecond = policy.Requested.Download.BytesPerSecond;
        row.RequestedUploadBytesPerSecond = policy.Requested.Upload.BytesPerSecond;
        row.ConfirmedName = policy.Confirmed.Name;
        row.ConfirmedDownloadBytesPerSecond = policy.Confirmed.Download.BytesPerSecond;
        row.ConfirmedUploadBytesPerSecond = policy.Confirmed.Upload.BytesPerSecond;
        row.ConfirmedGeneration = policy.ConfirmedGeneration;
        row.RequestStatus = policy.RequestStatus;
        db.SaveChanges();
        return true;
    }, token);

    public Task<BandwidthSchedule> LoadBandwidthScheduleAsync(CancellationToken token) => OnDatabaseThreadAsync(db =>
    {
        var json = db.BandwidthPolicies.AsNoTracking().SingleOrDefault(row => row.Id == 1)?.ScheduleJson;
        if (json is null) return BandwidthSchedule.Disabled;
        var schedule = JsonSerializer.Deserialize<BandwidthSchedule>(json)
            ?? throw new DownloadStorageException("Saved bandwidth schedule is invalid.");
        schedule.Validate();
        return schedule;
    }, token);

    public Task SaveBandwidthScheduleAsync(BandwidthSchedule schedule, CancellationToken token) => OnDatabaseThreadAsync(db =>
    {
        schedule.Validate();
        var row = db.BandwidthPolicies.SingleOrDefault(row => row.Id == 1);
        if (row is null) { row = new() { Id = 1 }; db.BandwidthPolicies.Add(row); }
        row.ScheduleJson = JsonSerializer.Serialize(schedule);
        db.SaveChanges();
        return true;
    }, token);

    public Task<JobBandwidthPolicy> LoadJobBandwidthPolicyAsync(Guid jobId, CancellationToken token) => OnDatabaseThreadAsync(db =>
    {
        var row = db.JobBandwidthPolicies.AsNoTracking().SingleOrDefault(row => row.JobId == jobId);
        if (row is null) return JobBandwidthPolicy.Default(jobId);
        var policy = new JobBandwidthPolicy(row.JobId, row.RequestedRevision, row.ConfirmedRevision,
            new(row.RequestedDownloadBytesPerSecond), new(row.ConfirmedDownloadBytesPerSecond),
            row.ConfirmedGeneration, row.RequestStatus);
        policy.Validate();
        return policy;
    }, token);

    public Task SaveJobBandwidthPolicyAsync(JobBandwidthPolicy policy, CancellationToken token) => OnDatabaseThreadAsync(db =>
    {
        policy.Validate();
        var row = db.JobBandwidthPolicies.SingleOrDefault(row => row.JobId == policy.JobId);
        if (row is null) { row = new() { JobId = policy.JobId }; db.JobBandwidthPolicies.Add(row); }
        row.RequestedRevision = policy.RequestedRevision;
        row.ConfirmedRevision = policy.ConfirmedRevision;
        row.RequestedDownloadBytesPerSecond = policy.Requested.BytesPerSecond;
        row.ConfirmedDownloadBytesPerSecond = policy.Confirmed.BytesPerSecond;
        row.ConfirmedGeneration = policy.ConfirmedGeneration;
        row.RequestStatus = policy.RequestStatus;
        db.SaveChanges();
        return true;
    }, token);

    private static DateTimeOffset At(long ticks) => new(ticks, TimeSpan.Zero);

    // SQLite's async methods are synchronous. One serialized background boundary keeps
    // database work off the UI thread; each unit of work owns a fresh DbContext.
    private async Task<T> OnDatabaseThreadAsync<T>(Func<DownloadDbContext, T> work, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                using var db = CreateContext();
                return work(db);
            }, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SqliteException or DbUpdateException or IOException or UnauthorizedAccessException)
        { throw new DownloadStorageException("Download history could not be read or saved. Check access to the private app data folder."); }
        finally { _gate.Release(); }
    }
}
