using AriaGui.Application.Downloads;
using AriaGui.Application.Notifications;
using AriaGui.Infrastructure.Aria2;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AriaGui.Infrastructure.Persistence;

// Transfer repositories stage an event inside their own state-change transaction in N2.
public static class NotificationPersistence
{
    public static long CurrentRevision(DownloadDbContext db, string sourceKey)
    {
        ValidateSourceKey(sourceKey);
        var tracked = db.ChangeTracker.Entries<NotificationSourceRevisionRow>()
            .FirstOrDefault(entry => entry.Entity.SourceKey == sourceKey);
        if (tracked is not null) return tracked.Entity.Revision;
        return db.NotificationSourceRevisions.SingleOrDefault(row => row.SourceKey == sourceKey)?.Revision ?? 0;
    }

    public static long AdvanceRevision(DownloadDbContext db, string sourceKey)
    {
        ValidateSourceKey(sourceKey);
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("A source revision must share its retry transaction.");
        var row = db.ChangeTracker.Entries<NotificationSourceRevisionRow>()
            .FirstOrDefault(entry => entry.Entity.SourceKey == sourceKey)?.Entity
            ?? db.NotificationSourceRevisions.SingleOrDefault(item => item.SourceKey == sourceKey);
        if (row is null)
        {
            db.NotificationSourceRevisions.Add(new NotificationSourceRevisionRow { SourceKey = sourceKey, Revision = 1 });
            return 1;
        }
        row.Revision = checked(row.Revision + 1);
        return row.Revision;
    }

    public static bool Stage(DownloadDbContext db, NotificationDraft draft)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(draft);
        draft.Validate();
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("A notification must share its source transaction.");
        if (db.ChangeTracker.Entries<NotificationReceiptRow>().Any(entry => entry.Entity.EventKey == draft.EventKey)
            || db.NotificationReceipts.Any(row => row.EventKey == draft.EventKey)) return false;
        db.NotificationReceipts.Add(new NotificationReceiptRow
            { EventKey = draft.EventKey, CreatedAtUtcTicks = draft.CreatedAt.UtcTicks });
        db.NotificationEvents.Add(new NotificationEventRow
        {
            EventKey = draft.EventKey, Kind = draft.Kind, TransferKind = draft.TransferKind,
            JobId = draft.JobId, AttemptId = draft.AttemptId,
            DisplayName = Clean(draft.DisplayName), Detail = Clean(draft.Detail),
            CreatedAtUtcTicks = draft.CreatedAt.UtcTicks, DeliveryState = NotificationDeliveryState.Pending
        });
        return true;
    }

    private static string Clean(string value) => new(value.Where(ch => !char.IsControl(ch)).ToArray());
    private static void ValidateSourceKey(string sourceKey)
    {
        if (string.IsNullOrWhiteSpace(sourceKey) || sourceKey.Length > 160
            || sourceKey.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch is not (':' or '-' or '_' or '.')))
            throw new ArgumentException("Invalid notification source.", nameof(sourceKey));
    }
}

public sealed class SqliteNotificationRepository(string databasePath) : INotificationRepository
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DownloadDbContext CreateContext() => new(new DbContextOptionsBuilder<DownloadDbContext>()
        .UseSqlite(new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false, DefaultTimeout = 5 }.ToString()).Options);

    public Task InitializeAsync(CancellationToken token) => OnDatabaseThreadAsync(db =>
    {
        UserRuntimeDirectory.EnsurePrivate(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        DatabaseSchemaUpgrade.Apply(db, databasePath);
        db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
        return true;
    }, token);

    public Task<NotificationEntry?> TryAppendAsync(NotificationDraft draft, CancellationToken token) => OnDatabaseThreadAsync(db =>
    {
        using var transaction = db.Database.BeginTransaction();
        if (!NotificationPersistence.Stage(db, draft)) return null;
        db.SaveChanges();
        transaction.Commit();
        return Map(db.NotificationEvents.AsNoTracking().Single(row => row.EventKey == draft.EventKey));
    }, token);

    public Task<NotificationEntry?> GetAsync(long id, CancellationToken token) => OnDatabaseThreadAsync(db =>
    {
        var row = db.NotificationEvents.AsNoTracking().SingleOrDefault(item => item.Id == id);
        return row is null ? null : Map(row);
    }, token);

    public Task<IReadOnlyList<NotificationEntry>> ReadPageAsync(int pageSize, long? beforeId, bool unreadOnly, CancellationToken token)
    {
        if (pageSize is < 1 or > 100 || beforeId is <= 0) throw new ArgumentOutOfRangeException(nameof(pageSize));
        return OnDatabaseThreadAsync<IReadOnlyList<NotificationEntry>>(db =>
        {
            var query = db.NotificationEvents.AsNoTracking().Where(row => row.DismissedAtUtcTicks == null);
            if (beforeId is long cursor) query = query.Where(row => row.Id < cursor);
            if (unreadOnly) query = query.Where(row => row.ReadAtUtcTicks == null);
            return query.OrderByDescending(row => row.Id).Take(pageSize).ToArray().Select(Map).ToArray();
        }, token);
    }

    public Task<int> CountUnreadAsync(CancellationToken token) => OnDatabaseThreadAsync(db =>
        db.NotificationEvents.Count(row => row.ReadAtUtcTicks == null && row.DismissedAtUtcTicks == null), token);

    public Task MarkReadAsync(long id, CancellationToken token) => OnDatabaseThreadAsync(db =>
    {
        var row = db.NotificationEvents.SingleOrDefault(item => item.Id == id);
        if (row is { ReadAtUtcTicks: null, DismissedAtUtcTicks: null })
        { row.ReadAtUtcTicks = DateTimeOffset.UtcNow.UtcTicks; db.SaveChanges(); }
        return true;
    }, token);

    public Task MarkAllReadAsync(CancellationToken token) => OnDatabaseThreadAsync(db =>
    {
        var now = DateTimeOffset.UtcNow.UtcTicks;
        foreach (var row in db.NotificationEvents.Where(item => item.ReadAtUtcTicks == null && item.DismissedAtUtcTicks == null))
            row.ReadAtUtcTicks = now;
        db.SaveChanges();
        return true;
    }, token);

    public Task DismissAsync(long id, CancellationToken token) => OnDatabaseThreadAsync(db =>
    {
        var row = db.NotificationEvents.SingleOrDefault(item => item.Id == id);
        if (row is { DismissedAtUtcTicks: null })
        { row.DismissedAtUtcTicks = DateTimeOffset.UtcNow.UtcTicks; db.SaveChanges(); }
        return true;
    }, token);

    public Task DismissAllAsync(CancellationToken token) => OnDatabaseThreadAsync(db =>
    {
        var now = DateTimeOffset.UtcNow.UtcTicks;
        foreach (var row in db.NotificationEvents.Where(item => item.DismissedAtUtcTicks == null))
            row.DismissedAtUtcTicks = now;
        db.SaveChanges();
        return true;
    }, token);

    public Task<bool> TryClaimDesktopAsync(long id, CancellationToken token) => OnDatabaseThreadAsync(db =>
    {
        var row = db.NotificationEvents.SingleOrDefault(item => item.Id == id);
        if (row is not { DeliveryState: NotificationDeliveryState.Pending, DismissedAtUtcTicks: null }) return false;
        row.DeliveryState = NotificationDeliveryState.Sending;
        row.DeliveryUpdatedAtUtcTicks = DateTimeOffset.UtcNow.UtcTicks;
        db.SaveChanges();
        return true;
    }, token);

    public Task FinishDesktopAsync(long id, NotificationDeliveryState result, CancellationToken token)
    {
        if (result is not (NotificationDeliveryState.Delivered or NotificationDeliveryState.Suppressed))
            throw new ArgumentOutOfRangeException(nameof(result));
        return OnDatabaseThreadAsync(db =>
        {
            var row = db.NotificationEvents.SingleOrDefault(item => item.Id == id);
            if (row?.DeliveryState == NotificationDeliveryState.Sending)
            { row.DeliveryState = result; row.DeliveryUpdatedAtUtcTicks = DateTimeOffset.UtcNow.UtcTicks; db.SaveChanges(); }
            return true;
        }, token);
    }

    public Task<int> PruneReadAsync(DateTimeOffset olderThan, int maxVisible, CancellationToken token)
    {
        if (maxVisible < 1) throw new ArgumentOutOfRangeException(nameof(maxVisible));
        return OnDatabaseThreadAsync(db =>
        {
            var visible = db.NotificationEvents.AsNoTracking().Where(row => row.DismissedAtUtcTicks == null)
                .OrderByDescending(row => row.Id).Select(row => row.Id).ToArray();
            var keep = visible.Take(maxVisible).ToHashSet();
            var cutoff = olderThan.UtcTicks;
            var rows = db.NotificationEvents.Where(row => (row.ReadAtUtcTicks != null || row.DismissedAtUtcTicks != null)
                    && row.DeliveryState != NotificationDeliveryState.Pending
                    && row.DeliveryState != NotificationDeliveryState.Sending)
                .AsEnumerable().Where(row => row.CreatedAtUtcTicks < cutoff || !keep.Contains(row.Id)).ToArray();
            db.NotificationEvents.RemoveRange(rows);
            db.SaveChanges();
            return rows.Length;
        }, token);
    }

    private static NotificationEntry Map(NotificationEventRow row) => new(row.Id, row.Kind, row.TransferKind,
        row.JobId, row.AttemptId, row.DisplayName, row.Detail, At(row.CreatedAtUtcTicks),
        row.ReadAtUtcTicks is long read ? At(read) : null,
        row.DismissedAtUtcTicks is long dismissed ? At(dismissed) : null, row.DeliveryState);
    private static DateTimeOffset At(long ticks) => new(ticks, TimeSpan.Zero);

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
        { throw new DownloadStorageException("Notifications could not be read or saved; their existing records were preserved."); }
        finally { _gate.Release(); }
    }
}
