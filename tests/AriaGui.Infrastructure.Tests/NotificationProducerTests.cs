using AriaGui.Application.Downloads;
using AriaGui.Application.Notifications;
using AriaGui.Core.Downloads;
using AriaGui.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AriaGui.Infrastructure.Tests;

public sealed class NotificationProducerTests
{
    [Fact]
    public async Task N2_http_terminal_transitions_create_one_event_per_attempt_and_kind()
    {
        using var runtime = new Runtime();
        var downloads = new SqliteDownloadRepository(runtime.DatabasePath);
        await downloads.InitializeAsync(default);
        var notifications = new SqliteNotificationRepository(runtime.DatabasePath);
        var first = Entry("completed.bin");
        await downloads.InsertIntentAsync(first, default);
        Assert.Empty(await downloads.SaveAndReportCompletionsAsync([first], default));
        var completed = first with { Progress = new(DownloadStatus.Completed, 1, 1, 0) };
        Assert.Equal([first.Attempt.Id], await downloads.SaveAndReportCompletionsAsync([completed], default));
        Assert.Empty(await downloads.SaveAndReportCompletionsAsync([completed], default));

        var second = Entry("failed.bin");
        await downloads.InsertIntentAsync(second, default);
        var failed = second with { Progress = new(DownloadStatus.Failed, 0, 1, 0), ErrorMessage = "secret token" };
        await downloads.SaveAsync([failed], default);
        await downloads.SaveAsync([failed], default);
        var events = await notifications.ReadPageAsync(20, null, false, default);
        Assert.Equal(2, events.Count);
        Assert.Contains(events, item => item.AttemptId == first.Attempt.Id && item.Kind == NotificationKind.TransferCompleted);
        Assert.Contains(events, item => item.AttemptId == second.Attempt.Id && item.Kind == NotificationKind.TransferFailed
            && !item.Detail.Contains("secret token", StringComparison.Ordinal));
        using var db = runtime.Db();
        Assert.Equal(DownloadStatus.Completed, db.Attempts.Single(row => row.Id == first.Attempt.Id).Status);
        Assert.Equal(DownloadStatus.Failed, db.Attempts.Single(row => row.Id == second.Attempt.Id).Status);
    }

    [Fact]
    public async Task N2_torrent_retry_advances_revision_and_only_published_completion_notifies()
    {
        using var runtime = new Runtime();
        var torrents = new SqliteTorrentMetadataRepository(runtime.DatabasePath);
        await torrents.InitializeAsync();
        var jobId = Guid.NewGuid();
        using (var db = runtime.Db())
        {
            db.TorrentSpecs.Add(new TorrentSpecRow
            {
                JobId = jobId, Name = "local test", Stage = TorrentStage.Waiting,
                InfoHash = new string('a', 40), DestinationKey = Path.Combine(runtime.Root, "local test").ToUpperInvariant(),
                CreatedAtUtcTicks = DateTimeOffset.UtcNow.UtcTicks, UpdatedAtUtcTicks = DateTimeOffset.UtcNow.UtcTicks
            });
            db.SaveChanges();
        }
        var notifications = new SqliteNotificationRepository(runtime.DatabasePath);
        await torrents.SetStageAsync(jobId, TorrentStage.Downloading);
        Assert.Empty(await notifications.ReadPageAsync(20, null, false, default));
        await torrents.SetStageAsync(jobId, TorrentStage.Failed);
        await torrents.SetStageAsync(jobId, TorrentStage.Failed);
        await torrents.SetStageAsync(jobId, TorrentStage.Waiting);
        await torrents.SetStageAsync(jobId, TorrentStage.Failed);
        await torrents.SetStageAsync(jobId, TorrentStage.Publishing);
        Assert.Equal(2, (await notifications.ReadPageAsync(20, null, false, default)).Count);
        await torrents.SetStageAsync(jobId, TorrentStage.Completed);
        await torrents.SetStageAsync(jobId, TorrentStage.Completed);
        var events = await notifications.ReadPageAsync(20, null, false, default);
        Assert.Equal(3, events.Count);
        Assert.Equal(2, events.Count(item => item.Kind == NotificationKind.TransferFailed));
        Assert.Single(events, item => item.Kind == NotificationKind.TransferCompleted);
        Assert.Equal(3, events.Select(item => item.Id).Distinct().Count());
        using var finalDb = runtime.Db();
        Assert.Equal(TorrentStage.Completed, finalDb.TorrentSpecs.Single(row => row.JobId == jobId).Stage);
        Assert.Equal(2, finalDb.NotificationSourceRevisions.Single(row => row.SourceKey == $"torrent:{jobId:N}").Revision);
    }

    private static DownloadEntry Entry(string name)
    {
        var jobId = Guid.NewGuid();
        var attemptId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var destination = Path.Combine(Path.GetTempPath(), "AriaGUI.NotificationProducerTests", jobId.ToString("N"), name);
        return new DownloadEntry(new(jobId, name, new Uri("https://example.invalid/" + name), destination, now),
            new(attemptId, jobId, Guid.NewGuid().ToString("N")[..16], now),
            new(Guid.NewGuid(), jobId, attemptId, name, 1),
            new(DownloadStatus.Queued, 0, 1, 0),
            new PendingOperation(Guid.NewGuid(), jobId, DownloadAction.Add, OperationStatus.Pending, now));
    }

    private sealed class Runtime : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "AriaGUI.NotificationProducerTests", Guid.NewGuid().ToString("N"));
        public string DatabasePath => Path.Combine(Root, "downloads.db");
        public Runtime() => Directory.CreateDirectory(Root);
        public DownloadDbContext Db() => new(new DbContextOptionsBuilder<DownloadDbContext>()
            .UseSqlite(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString()).Options);
        public void Dispose()
        {
            var allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "AriaGUI.NotificationProducerTests")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(Root).StartsWith(allowed, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unsafe cleanup.");
            Directory.Delete(Root, true);
        }
    }
}
