using AriaGui.Application.Notifications;
using AriaGui.Core.Downloads;
using AriaGui.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AriaGui.Infrastructure.Tests;

public sealed class NotificationStorageTests
{
    [Fact]
    public async Task N1_notifications_stage_is_atomic_and_duplicate_receipt_survives_pruning()
    {
        using var runtime = new Runtime();
        var repository = runtime.Repository();
        await repository.InitializeAsync(default);
        var draft = Draft("http:attempt-a:completed");

        using (var db = runtime.Db())
        using (var transaction = db.Database.BeginTransaction())
        {
            Assert.True(NotificationPersistence.Stage(db, draft));
            db.SaveChanges();
            transaction.Rollback();
        }
        Assert.Empty(await repository.ReadPageAsync(20, null, false, default));

        var created = await repository.TryAppendAsync(draft, default);
        Assert.NotNull(created);
        Assert.Null(await repository.TryAppendAsync(draft, default));
        Assert.Equal(1, await repository.CountUnreadAsync(default));
        await repository.MarkReadAsync(created.Id, default);
        Assert.Equal(0, await repository.CountUnreadAsync(default));
        Assert.Equal(0, await repository.PruneReadAsync(DateTimeOffset.UtcNow.AddDays(1), 20, default));
        Assert.True(await repository.TryClaimDesktopAsync(created.Id, default));
        await repository.FinishDesktopAsync(created.Id, NotificationDeliveryState.Delivered, default);
        Assert.Equal(1, await repository.PruneReadAsync(DateTimeOffset.UtcNow.AddDays(1), 20, default));
        Assert.Null(await repository.GetAsync(created.Id, default));
        Assert.Null(await repository.TryAppendAsync(draft, default));
        using var check = runtime.Db();
        Assert.Single(check.NotificationReceipts);
    }

    [Fact]
    public async Task N1_notifications_retry_revision_rolls_back_and_persists_with_source_transaction()
    {
        using var runtime = new Runtime();
        await runtime.Repository().InitializeAsync(default);
        var jobId = Guid.NewGuid();
        var source = $"torrent:{jobId:N}";
        using (var db = runtime.Db())
        using (var transaction = db.Database.BeginTransaction())
        {
            Assert.Equal(0, NotificationPersistence.CurrentRevision(db, source));
            Assert.Equal(1, NotificationPersistence.AdvanceRevision(db, source));
            Assert.Equal(1, NotificationPersistence.CurrentRevision(db, source));
            db.SaveChanges();
            transaction.Rollback();
        }
        using (var db = runtime.Db())
        using (var transaction = db.Database.BeginTransaction())
        {
            Assert.Equal(0, NotificationPersistence.CurrentRevision(db, source));
            Assert.Equal(1, NotificationPersistence.AdvanceRevision(db, source));
            db.SaveChanges();
            transaction.Commit();
        }
        using var recovered = runtime.Db();
        Assert.Equal(1, NotificationPersistence.CurrentRevision(recovered, source));
        Assert.NotEqual(NotificationEventKeys.Torrent(jobId, 0, NotificationKind.TransferFailed),
            NotificationEventKeys.Torrent(jobId, 1, NotificationKind.TransferFailed));
    }

    [Fact]
    public async Task N1_notifications_page_dismiss_and_desktop_claim_are_durable()
    {
        using var runtime = new Runtime();
        var repository = runtime.Repository();
        await repository.InitializeAsync(default);
        var first = (await repository.TryAppendAsync(Draft("http:attempt-a:failed"), default))!;
        var second = (await repository.TryAppendAsync(Draft("torrent:job-b:1:completed"), default))!;

        Assert.Equal(second.Id, Assert.Single(await repository.ReadPageAsync(1, null, false, default)).Id);
        Assert.Equal(first.Id, Assert.Single(await repository.ReadPageAsync(1, second.Id, true, default)).Id);
        Assert.True(await repository.TryClaimDesktopAsync(first.Id, default));
        Assert.False(await repository.TryClaimDesktopAsync(first.Id, default));
        await repository.FinishDesktopAsync(first.Id, NotificationDeliveryState.Delivered, default);
        Assert.Equal(NotificationDeliveryState.Delivered, (await repository.GetAsync(first.Id, default))!.DeliveryState);
        await repository.DismissAsync(first.Id, default);
        Assert.Equal(1, await repository.CountUnreadAsync(default));
        await repository.MarkAllReadAsync(default);
        Assert.Empty(await repository.ReadPageAsync(20, null, true, default));
        await repository.DismissAllAsync(default);
        Assert.Empty(await repository.ReadPageAsync(20, null, false, default));
        Assert.NotNull(await repository.GetAsync(second.Id, default));
    }

    [Fact]
    public async Task N1_notifications_distinct_simultaneous_events_are_all_recorded()
    {
        using var runtime = new Runtime();
        await runtime.Repository().InitializeAsync(default);
        var left = runtime.Repository(); var right = runtime.Repository();
        await Task.WhenAll(left.TryAppendAsync(Draft("http:attempt-a:completed"), default),
            right.TryAppendAsync(Draft("torrent:job-b:1:completed"), default));
        Assert.Equal(2, await runtime.Repository().CountUnreadAsync(default));
    }

    private static NotificationDraft Draft(string key) => new(key, NotificationKind.TransferCompleted,
        TransferKind.Http, Guid.NewGuid(), Guid.NewGuid(), "sample.zip", "Transfer finished", DateTimeOffset.UtcNow);

    private sealed class Runtime : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "AriaGUI.NotificationStorageTests", Guid.NewGuid().ToString("N"));
        private string DatabasePath => Path.Combine(Root, "downloads.db");
        public Runtime() => Directory.CreateDirectory(Root);
        public SqliteNotificationRepository Repository() => new(DatabasePath);
        public DownloadDbContext Db() => new(new DbContextOptionsBuilder<DownloadDbContext>()
            .UseSqlite(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString()).Options);
        public void Dispose()
        {
            var allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "AriaGUI.NotificationStorageTests")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(Root).StartsWith(allowed, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unsafe cleanup.");
            Directory.Delete(Root, true);
        }
    }
}
