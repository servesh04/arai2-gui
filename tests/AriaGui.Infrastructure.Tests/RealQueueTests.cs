using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using AriaGui.Application.Downloads;
using AriaGui.Application.Engine;
using AriaGui.Core.Downloads;
using AriaGui.Infrastructure.Aria2;
using AriaGui.Infrastructure.Downloads;
using AriaGui.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AriaGui.Infrastructure.Tests;

[Collection("Engine processes")]
public sealed class RealQueueTests
{
    [Fact]
    public async Task Real_pause_resume_remove_and_retry_preserve_files_and_link_attempts()
    {
        using var runtime = new Runtime();
        await using var server = new LocalHttpServer();
        await using var manager = new Aria2ProcessManager(runtime.Options);
        await manager.StartAsync(default);
        var store = new DownloadRuntimeStore(); var coordinator = runtime.Coordinator(new ManagedAria2Client(manager), store);
        await coordinator.AddAsync(new Uri(new Uri(server.Url), "slow").AbsoluteUri, directory: runtime.Output, fileName: "keep.bin");
        var entry = Assert.Single(store.Current.Downloads);
        await WaitAsync(coordinator, () => store.Current.Downloads[0].Progress.DownloadedBytes > 0);
        await coordinator.PauseAsync(entry.Job.Id);
        await WaitAsync(coordinator, () => store.Current.Downloads[0].Progress.Status == DownloadStatus.Paused && store.Current.Downloads[0].PendingOperation is null);
        var paused = store.Current.Downloads[0];
        Assert.Equal(entry.Attempt.Id, paused.Attempt.Id);
        await coordinator.ResumeAsync(entry.Job.Id);
        await WaitAsync(coordinator, () => store.Current.Downloads[0].Progress.Status == DownloadStatus.Downloading);
        await coordinator.RemoveAsync(entry.Job.Id);
        await WaitAsync(coordinator, () => store.Current.Downloads[0].Progress.Status == DownloadStatus.Removed && store.Current.Downloads[0].PendingOperation is null);
        Assert.True(File.Exists(entry.Job.Destination));
        var prefix = (await File.ReadAllBytesAsync(entry.Job.Destination)).Take(4096).ToArray();
        Assert.Equal(server.Payload.Take(4096), prefix);
        await coordinator.RetryAsync(entry.Job.Id);
        var retry = store.Current.Downloads[0];
        Assert.Equal(entry.Attempt.Id, retry.Attempt.ParentAttemptId);
        await WaitAsync(coordinator, () => store.Current.Downloads[0].Progress.Status is DownloadStatus.Completed or DownloadStatus.Failed);
        Assert.Equal(DownloadStatus.Completed, store.Current.Downloads[0].Progress.Status);
        Assert.Equal(server.Payload, await File.ReadAllBytesAsync(entry.Job.Destination));
        using var db = runtime.Db();
        Assert.Single(db.Jobs); Assert.Equal(2, db.Attempts.Count()); Assert.Equal(2, db.Files.Count());
        Assert.Equal(DownloadStatus.Removed, db.Attempts.Single(row => row.Id == entry.Attempt.Id).Status);
        Assert.All(db.Operations, operation => Assert.Equal(OperationStatus.Confirmed, operation.Status));
    }

    [Fact]
    public async Task Concurrency_and_waiting_reorder_use_the_real_engine_queue_without_preemption()
    {
        using var runtime = new Runtime(); await using var server = new LocalHttpServer();
        await using var manager = new Aria2ProcessManager(runtime.Options); await manager.StartAsync(default);
        var store = new DownloadRuntimeStore(); var coordinator = runtime.Coordinator(new ManagedAria2Client(manager), store);
        await coordinator.SetConcurrencyAsync(1);
        for (var index = 0; index < 3; index++)
            await coordinator.AddAsync(new Uri(new Uri(server.Url), "slow?job=" + index).AbsoluteUri, directory: runtime.Output, fileName: index + ".bin");
        await WaitAsync(coordinator, () => store.Current.Downloads.Count(row => row.Progress.Status == DownloadStatus.Queued && row.QueuePosition is not null) == 2);
        var active = store.Current.Downloads.Single(row => row.Progress.Status == DownloadStatus.Downloading);
        var last = store.Current.Downloads.Single(row => row.QueuePosition == 1);
        await coordinator.MoveAsync(last.Job.Id, -1);
        await WaitAsync(coordinator, () => store.Current.Downloads.Single(row => row.Job.Id == last.Job.Id).QueuePosition == 0);
        Assert.Equal(DownloadStatus.Downloading, store.Current.Downloads.Single(row => row.Job.Id == active.Job.Id).Progress.Status);
        Assert.Equal(1, store.Current.ObservedConcurrency);
        await coordinator.RemoveAsync(active.Job.Id);
        await WaitAsync(coordinator, () => store.Current.Downloads.Single(row => row.Job.Id == last.Job.Id).Progress.Status == DownloadStatus.Downloading);
        Assert.Single(store.Current.Downloads, row => row.Progress.Status == DownloadStatus.Downloading);
        foreach (var row in store.Current.Downloads.Where(row => row.Progress.Status is DownloadStatus.Queued or DownloadStatus.Downloading).ToArray())
            await coordinator.RemoveAsync(row.Job.Id);
        var reopened = runtime.Repository();
        Assert.Equal(1, (await reopened.LoadQueuePolicyAsync(default)).Limit);
    }

    [Fact]
    public async Task Failed_http_attempt_retries_under_the_same_job_and_completion_is_reported_once()
    {
        using var runtime = new Runtime(); await using var server = new LocalHttpServer();
        await using var manager = new Aria2ProcessManager(runtime.Options); await manager.StartAsync(default);
        var store = new DownloadRuntimeStore(); var coordinator = runtime.Coordinator(new ManagedAria2Client(manager), store);
        await coordinator.AddAsync(new Uri(new Uri(server.Url), "flaky").AbsoluteUri, directory: runtime.Output, fileName: "retry.bin");
        await WaitAsync(coordinator, () => store.Current.Downloads[0].Progress.Status == DownloadStatus.Failed);
        var failed = store.Current.Downloads[0];
        await coordinator.RetryAsync(failed.Job.Id);
        await WaitAsync(coordinator, () => store.Current.Downloads[0].Progress.Status is DownloadStatus.Completed or DownloadStatus.Failed);
        Assert.Equal(DownloadStatus.Completed, store.Current.Downloads[0].Progress.Status);
        Assert.Equal(failed.Job.Id, store.Current.Downloads[0].Job.Id);
        Assert.Equal(failed.Attempt.Id, store.Current.Downloads[0].Attempt.ParentAttemptId);
        Assert.Equal(2, server.Requests);
        Assert.Equal(server.Payload, await File.ReadAllBytesAsync(failed.Job.Destination));
        Assert.NotNull(store.Current.LatestCompletion);
        Assert.Empty(await runtime.Repository().SaveAndReportCompletionsAsync(store.Current.Downloads, default));
        var reopenedStore = new DownloadRuntimeStore(); await runtime.Coordinator(new ManagedAria2Client(manager), reopenedStore).RefreshAsync();
        Assert.Null(reopenedStore.Current.LatestCompletion);
        Assert.Equal(store.Current.Downloads[0].Attempt.Id, reopenedStore.Current.Downloads[0].Attempt.Id);
    }

    [Fact]
    public async Task Lost_real_pause_reply_recovers_from_SQLite_without_a_second_command()
    {
        using var runtime = new Runtime(); await using var server = new LocalHttpServer();
        await using var manager = new Aria2ProcessManager(runtime.Options); await manager.StartAsync(default);
        var client = new LostPauseReply(new ManagedAria2Client(manager));
        var store = new DownloadRuntimeStore(); var coordinator = runtime.Coordinator(client, store);
        await coordinator.AddAsync(new Uri(new Uri(server.Url), "slow").AbsoluteUri, directory: runtime.Output, fileName: "uncertain.bin");
        var id = store.Current.Downloads[0].Job.Id;
        await Assert.ThrowsAsync<EngineException>(() => coordinator.PauseAsync(id));
        Assert.Equal(OperationStatus.Uncertain, (await runtime.Repository().LoadAsync(default))[0].PendingOperation?.Status);
        var reopened = new DownloadRuntimeStore(); await runtime.Coordinator(client, reopened).RefreshAsync();
        await WaitAsync(runtime.Coordinator(client, reopened), () => reopened.Current.Downloads[0].Progress.Status == DownloadStatus.Paused);
        Assert.Null(reopened.Current.Downloads[0].PendingOperation);
        Assert.Equal(1, client.Commands);
    }

    [Fact]
    public async Task Phase_three_database_upgrades_without_losing_history_and_without_pending_model_changes()
    {
        using var runtime = new Runtime();
        Directory.CreateDirectory(Path.GetDirectoryName(runtime.Database)!);
        using (var old = runtime.Db())
        {
            old.GetService<IMigrator>().Migrate("20261004141858_InitialDownloads");
            var jobId = Guid.NewGuid(); var attemptId = Guid.NewGuid(); var time = DateTimeOffset.UtcNow.UtcTicks;
            old.Jobs.Add(new() { Id = jobId, DisplayName = "history.bin", Source = "https://example.com/history.bin", Destination = @"C:\Downloads\history.bin", DestinationKey = @"C:\DOWNLOADS\HISTORY.BIN", CreatedAtUtcTicks = time });
            old.SaveChanges();
            old.Database.ExecuteSqlInterpolated($"INSERT INTO Attempts (Id, JobId, EngineId, StartedAtUtcTicks, ParentAttemptId, Status, DownloadedBytes, TotalBytes, UpdatedAtUtcTicks, ErrorMessage) VALUES ({attemptId}, {jobId}, {"0123456789abcdef"}, {time}, NULL, {3}, {0}, NULL, {time}, NULL)");
            old.Files.Add(new() { Id = Guid.NewGuid(), JobId = jobId, AttemptId = attemptId, FileName = "history.bin" });
            old.SaveChanges();
        }
        await runtime.Repository().InitializeAsync(default);
        var saved = Assert.Single(await runtime.Repository().LoadAsync(default)); Assert.Equal(DownloadStatus.Completed, saved.Progress.Status);
        using var upgraded = runtime.Db(); Assert.Equal(3, upgraded.Database.GetAppliedMigrations().Count());
        Assert.False(upgraded.Database.HasPendingModelChanges());
    }

    [Theory]
    [InlineData(DownloadStatus.Paused, null, OperationStatus.Confirmed, true, false)]
    [InlineData(DownloadStatus.Downloading, DownloadAction.Pause, OperationStatus.Pending, true, false)]
    [InlineData(DownloadStatus.Paused, DownloadAction.Resume, OperationStatus.Pending, false, false)]
    [InlineData(DownloadStatus.Downloading, DownloadAction.Remove, OperationStatus.Uncertain, false, true)]
    [InlineData(DownloadStatus.Downloading, DownloadAction.Pause, OperationStatus.Failed, false, false)]
    [InlineData(DownloadStatus.Removed, null, OperationStatus.Confirmed, false, true)]
    public async Task Phase_four_upgrade_preserves_latest_pause_resume_remove_intent(DownloadStatus observed,
        DownloadAction? latest, OperationStatus operationStatus, bool paused, bool removed)
    {
        using var runtime = new Runtime(); Directory.CreateDirectory(Path.GetDirectoryName(runtime.Database)!);
        var jobId = Guid.NewGuid(); var attemptId = Guid.NewGuid(); var time = DateTimeOffset.UtcNow.UtcTicks;
        using (var old = runtime.Db())
        {
            old.GetService<IMigrator>().Migrate("20261004151151_QueueCommands");
            old.Jobs.Add(new() { Id = jobId, DisplayName = "intent.bin", Source = "https://example.com/intent.bin",
                Destination = @"C:\Downloads\intent.bin", DestinationKey = @"C:\DOWNLOADS\INTENT.BIN", CreatedAtUtcTicks = time });
            old.SaveChanges();
            old.Database.ExecuteSqlInterpolated($"INSERT INTO Attempts (Id, JobId, EngineId, StartedAtUtcTicks, ParentAttemptId, Status, DownloadedBytes, TotalBytes, UpdatedAtUtcTicks, ErrorMessage) VALUES ({attemptId}, {jobId}, {"0123456789abcdef"}, {time}, NULL, {(int)observed}, {4096}, NULL, {time}, NULL)");
            old.Files.Add(new() { Id = Guid.NewGuid(), JobId = jobId, AttemptId = attemptId, FileName = "intent.bin" });
            if (latest == DownloadAction.Resume)
                old.Operations.Add(new() { Id = Guid.NewGuid(), JobId = jobId, AttemptId = attemptId, Action = DownloadAction.Pause,
                    Status = OperationStatus.Confirmed, RequestedAtUtcTicks = time - 1 });
            if (latest is { } action)
                old.Operations.Add(new() { Id = Guid.NewGuid(), JobId = jobId, AttemptId = attemptId, Action = action,
                    Status = operationStatus, RequestedAtUtcTicks = time });
            old.SaveChanges();
        }
        var repository = runtime.Repository(); await repository.InitializeAsync(default); var saved = Assert.Single(await repository.LoadAsync(default));
        Assert.Equal(paused, saved.DesiredPaused); Assert.Equal(removed, saved.DesiredRemoved);
        Assert.Equal(attemptId, saved.Attempt.Id); Assert.Equal(4096, saved.Progress.DownloadedBytes); Assert.Equal(observed, saved.Progress.Status);
    }

    [Fact]
    public async Task Retry_of_a_failed_partial_transfer_continues_using_its_control_file()
    {
        using var runtime = new Runtime(); await using var server = new LocalHttpServer();
        await using var manager = new Aria2ProcessManager(runtime.Options); await manager.StartAsync(default);
        var store = new DownloadRuntimeStore(); var coordinator = runtime.Coordinator(new ManagedAria2Client(manager), store);
        await coordinator.AddAsync(new Uri(new Uri(server.Url), "partial").AbsoluteUri, directory: runtime.Output, fileName: "partial.bin");
        await WaitAsync(coordinator, () => store.Current.Downloads[0].Progress.Status == DownloadStatus.Failed);
        var failed = store.Current.Downloads[0];
        Assert.True(File.Exists(failed.Job.Destination));
        Assert.True(File.Exists(failed.Job.Destination + ".aria2"));
        Volatile.Write(ref server.RecoverPartial, true);
        await coordinator.RetryAsync(failed.Job.Id);
        await WaitAsync(coordinator, () => store.Current.Downloads[0].Progress.Status is DownloadStatus.Completed or DownloadStatus.Failed);
        Assert.Equal(DownloadStatus.Completed, store.Current.Downloads[0].Progress.Status);
        Assert.Equal(server.Payload, await File.ReadAllBytesAsync(failed.Job.Destination));
    }

    [Fact]
    public async Task Retry_does_not_overwrite_an_existing_file_without_a_control_file()
    {
        using var runtime = new Runtime(); Directory.CreateDirectory(runtime.Output);
        var file = Path.Combine(runtime.Output, "keep.bin"); await File.WriteAllTextAsync(file, "preserve these bytes");
        await Assert.ThrowsAsync<DownloadRequestException>(() => new FileDownloadDestination().ValidateRetryAsync(new("https://example.com/file"), runtime.Output, "keep.bin", default));
        Assert.Equal("preserve these bytes", await File.ReadAllTextAsync(file));
    }

    private static async Task WaitAsync(DownloadCoordinator coordinator, Func<bool> condition)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(35))
        {
            await coordinator.RefreshAsync();
            if (condition()) return;
            await Task.Delay(100);
        }
        Assert.Fail("The expected real-engine state was not observed within 35 seconds.");
    }
    private sealed class LostPauseReply(IAria2Client inner) : IAria2Client
    {
        public int Commands;
        public Task<EngineDownload> AddAsync(EngineDownloadRequest request, CancellationToken token) => inner.AddAsync(request, token);
        public Task<IReadOnlyList<EngineDownload>> GetDownloadsAsync(IReadOnlyList<string> ids, CancellationToken token) => inner.GetDownloadsAsync(ids, token);
        public Task<EngineObservation> ObserveAsync(IReadOnlyList<string> ids, CancellationToken token) => inner.ObserveAsync(ids, token);
        public Task SetConcurrencyAsync(int limit, CancellationToken token) => inner.SetConcurrencyAsync(limit, token);
        public async Task<EngineCommandResult> ExecuteAsync(string gid, DownloadAction action, int? position, CancellationToken token)
        { Commands++; await inner.ExecuteAsync(gid, action, position, token); throw new EngineException(EngineFailure.RpcUnavailable, "Accepted reply lost."); }
    }
    private sealed class Runtime : IDisposable
    {
        private string Root { get; } = Path.Combine(Path.GetTempPath(), "AriaGUI.QueueTests", Guid.NewGuid().ToString("N"));
        public string Database => Path.Combine(Root, "data", "downloads.db");
        public string Output => Path.Combine(Root, "downloads");
        public Aria2ProcessOptions Options { get; }
        public Runtime()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            Options = new(Path.Combine(AppContext.BaseDirectory, "Engine", "aria2c.exe"), Path.Combine(Root, "data")) { RpcPort = port };
        }
        public SqliteDownloadRepository Repository() => new(Database);
        public DownloadDbContext Db() => new(new DbContextOptionsBuilder<DownloadDbContext>().UseSqlite($"Data Source={Database};Pooling=False").Options);
        public DownloadCoordinator Coordinator(IAria2Client client, DownloadRuntimeStore store) => new(client, store, TimeProvider.System, Repository(), new FileDownloadDestination());
        public void Dispose()
        {
            var allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "AriaGUI.QueueTests")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(Root).StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe test cleanup path.");
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }
}
