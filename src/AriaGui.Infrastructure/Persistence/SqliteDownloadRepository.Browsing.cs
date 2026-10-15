using AriaGui.Application.Downloads;
using AriaGui.Core.Downloads;
using Microsoft.EntityFrameworkCore;

namespace AriaGui.Infrastructure.Persistence;

public sealed partial class SqliteDownloadRepository
{
    private static IQueryable<AttemptRow> Latest(DownloadDbContext db) => db.Attempts.AsNoTracking()
        .Where(row => !db.Attempts.Any(next => next.JobId == row.JobId && next.StartedAtUtcTicks > row.StartedAtUtcTicks));
    public Task<IReadOnlyList<DownloadEntry>> LoadCurrentAsync(CancellationToken token) => OnDatabaseThreadAsync<IReadOnlyList<DownloadEntry>>(db =>
    {
        if (db.Jobs.Any(job => !db.Attempts.Any(row => row.JobId == job.Id)))
            throw new DownloadStorageException("Saved history contains an incomplete job. Keep the database for diagnosis.");
        var latest = Latest(db);
        var live = latest.Where(row => (row.Status != DownloadStatus.Completed && row.Status != DownloadStatus.Failed && row.Status != DownloadStatus.Removed)
            || db.Operations.Any(op => op.AttemptId == row.Id && (op.Status == OperationStatus.Pending || op.Status == OperationStatus.Uncertain))).ToArray();
        var recent = latest.Where(row => row.Status == DownloadStatus.Completed || row.Status == DownloadStatus.Failed || row.Status == DownloadStatus.Removed)
            .OrderByDescending(row => row.UpdatedAtUtcTicks).Take(DownloadBrowsing.RecentLimit).ToArray();
        return ReadEntries(db, live.Concat(recent).DistinctBy(row => row.Id).ToArray()).OrderBy(row => row.Job.CreatedAt).ToArray();
    }, token);
    public Task<DownloadEntry?> GetAsync(Guid id, CancellationToken token) => OnDatabaseThreadAsync(db =>
        ReadEntries(db, Latest(db).Where(row => row.JobId == id).ToArray()).SingleOrDefault(), token);
    public Task<DownloadEntry?> GetAttemptAsync(string engineId, CancellationToken token) => OnDatabaseThreadAsync(db =>
        ReadEntries(db, db.Attempts.AsNoTracking().Where(row => row.EngineId == engineId).ToArray()).SingleOrDefault(), token);
    public Task<bool> ContainsDestinationAsync(string path, CancellationToken token) => OnDatabaseThreadAsync(db =>
        db.Jobs.Any(row => row.DestinationKey == path.ToUpperInvariant()), token);
    public Task<IReadOnlyList<string>> LoadEngineIdsAsync(CancellationToken token) => OnDatabaseThreadAsync<IReadOnlyList<string>>(db =>
        db.Attempts.AsNoTracking().Select(row => row.EngineId).ToArray(), token);
    public Task<HistoryPage> LoadPageAsync(HistoryQuery query, CancellationToken token)
    {
        query.Validate();
        return OnDatabaseThreadAsync(db =>
        {
            using var transaction = db.Database.BeginTransaction();
            var search = query.Search.Trim().ToUpperInvariant();
            var rows = from attempt in Latest(db) join job in db.Jobs.AsNoTracking() on attempt.JobId equals job.Id
                where (attempt.Status == DownloadStatus.Completed || attempt.Status == DownloadStatus.Failed || attempt.Status == DownloadStatus.Removed)
                    && (job.DisplayName.ToUpper().Contains(search) || job.Source.ToUpper().Contains(search) || job.Destination.ToUpper().Contains(search))
                select new { Attempt = attempt, Created = job.CreatedAtUtcTicks, JobId = job.Id, Name = job.DisplayName.ToUpper(),
                    Progress = attempt.TotalBytes > 0 ? (double?)((double)attempt.DownloadedBytes / attempt.TotalBytes.Value)
                        : attempt.TotalBytes == 0 && attempt.Status == DownloadStatus.Completed ? 1d : null };
            if (query.Filter != DownloadFilter.All)
            {
                var status = query.Filter switch { DownloadFilter.Active => DownloadStatus.Downloading, DownloadFilter.Waiting => DownloadStatus.Queued,
                    DownloadFilter.Paused => DownloadStatus.Paused, DownloadFilter.Recovery => DownloadStatus.Interrupted,
                    DownloadFilter.Completed => DownloadStatus.Completed, DownloadFilter.Failed => DownloadStatus.Failed, _ => DownloadStatus.Removed };
                rows = rows.Where(row => row.Attempt.Status == status);
            }
            var count = rows.Count();
            var sorted = query.SortColumn switch
            {
                DownloadSortColumn.Name => query.SortDescending ? rows.OrderByDescending(row => row.Name) : rows.OrderBy(row => row.Name),
                DownloadSortColumn.Size => query.SortDescending ? rows.OrderByDescending(row => row.Attempt.TotalBytes) : rows.OrderBy(row => row.Attempt.TotalBytes),
                DownloadSortColumn.Progress => query.SortDescending ? rows.OrderByDescending(row => row.Progress) : rows.OrderBy(row => row.Progress),
                DownloadSortColumn.Status => query.SortDescending ? rows.OrderByDescending(row => row.Attempt.Status) : rows.OrderBy(row => row.Attempt.Status),
                // Historical transfers have no current speed or ETA. Keep their stable creation order.
                _ => rows.OrderByDescending(row => row.Created)
            };
            var selected = sorted.ThenBy(row => row.JobId)
                .Skip(checked(query.Page * query.PageSize)).Take(query.PageSize).Select(row => row.Attempt).ToArray();
            return new HistoryPage(ReadEntries(db, selected), count, query.Page, query.PageSize);
        }, token);
    }
    private static IReadOnlyList<DownloadEntry> ReadEntries(DownloadDbContext db, AttemptRow[] attempts)
    {
        if (attempts.Length == 0) return [];
        var jobIds = attempts.Select(row => row.JobId).ToArray(); var ids = attempts.Select(row => row.Id).ToArray();
        var jobs = db.Jobs.AsNoTracking().Where(row => jobIds.Contains(row.Id)).ToDictionary(row => row.Id);
        var externalJobs = db.ExternalRequests.Where(row => row.JobId != null && jobIds.Contains(row.JobId.Value)).Select(row => row.JobId!.Value).ToHashSet();
        var files = db.Files.AsNoTracking().Where(row => ids.Contains(row.AttemptId)).ToDictionary(row => row.AttemptId);
        var operations = db.Operations.AsNoTracking().Where(row => ids.Contains(row.AttemptId)
            && (row.Status == OperationStatus.Pending || row.Status == OperationStatus.Uncertain)).ToArray()
            .GroupBy(row => row.AttemptId).ToDictionary(group => group.Key, group => group.MaxBy(row => row.RequestedAtUtcTicks)!);
        return attempts.Select(attempt =>
        {
            var job = jobs[attempt.JobId]; var file = files[attempt.Id]; operations.TryGetValue(attempt.Id, out var op);
            return new DownloadEntry(new(job.Id, job.DisplayName, new Uri(job.Source), job.Destination, At(job.CreatedAtUtcTicks)),
                new(attempt.Id, job.Id, attempt.EngineId, At(attempt.StartedAtUtcTicks), attempt.ParentAttemptId),
                new(file.Id, job.Id, attempt.Id, file.FileName, file.TotalBytes), new(attempt.Status, attempt.DownloadedBytes, attempt.TotalBytes, 0),
                op is null ? null : new(op.Id, job.Id, op.Action, op.Status, At(op.RequestedAtUtcTicks), op.QueuePosition),
                attempt.ErrorMessage, DesiredPaused: attempt.DesiredPaused, DesiredRemoved: attempt.DesiredRemoved, ObservedAt: At(attempt.UpdatedAtUtcTicks),
                ExactSourceUrl: externalJobs.Contains(job.Id) ? job.Source : null, PolicyHeld: attempt.PolicyHeld);
        }).ToArray();
    }
}
