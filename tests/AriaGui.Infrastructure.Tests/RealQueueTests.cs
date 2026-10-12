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
public sealed class RealQueueTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Real_pause_resume_remove_and_retry_preserve_files_and_link_attempts(bool continuity)
    {
        using var runtime = new Runtime();
        await using var server = new LocalHttpServer();
        var options = continuity ? runtime.Options with { UseContinuityGateway = true, ContinuityTestOrigins = source => new ControlledLoopbackOrigin(source) } : runtime.Options;
        await using var manager = new Aria2ProcessManager(options);
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
        if (continuity) Assert.Contains(server.Observations, row => row.RequestedOffset > 0 && row.Status == 206);
        using var db = runtime.Db();
        Assert.Single(db.Jobs); Assert.Equal(2, db.Attempts.Count()); Assert.Equal(2, db.Files.Count());
        Assert.Equal(DownloadStatus.Removed, db.Attempts.Single(row => row.Id == entry.Attempt.Id).Status);
        Assert.All(db.Operations, operation => Assert.Equal(OperationStatus.Confirmed, operation.Status));
    }

    [Fact]
    public async Task Guarded_publication_collision_reports_failed_and_preserves_user_file()
    {
        using var runtime = new Runtime(); await using var server = new LocalHttpServer();
        await using var manager = new Aria2ProcessManager(runtime.Options with
        { UseContinuityGateway = true, ContinuityTestOrigins = source => new ControlledLoopbackOrigin(source) });
        await manager.StartAsync(default);
        var store = new DownloadRuntimeStore(); var coordinator = runtime.Coordinator(new ManagedAria2Client(manager), store);
        await coordinator.AddAsync(new Uri(new Uri(server.Url), "slow").AbsoluteUri, directory: runtime.Output, fileName: "collision.bin");
        var entry = Assert.Single(store.Current.Downloads);
        await WaitAsync(coordinator, () => store.Current.Downloads[0].Progress.DownloadedBytes > 0);
        var userBytes = new byte[] { 9, 8, 7 }; await File.WriteAllBytesAsync(entry.Job.Destination, userBytes);
        await WaitAsync(coordinator, () => store.Current.Downloads[0].Progress.Status is DownloadStatus.Completed or DownloadStatus.Failed);
        Assert.Equal(DownloadStatus.Failed, store.Current.Downloads[0].Progress.Status);
        Assert.Equal(userBytes, await File.ReadAllBytesAsync(entry.Job.Destination));
        Assert.Contains("Protected file publication", store.Current.Downloads[0].ErrorMessage);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Concurrency_and_waiting_reorder_use_the_real_engine_queue_without_preemption(bool continuity)
    {
        using var runtime = new Runtime(); await using var server = new LocalHttpServer();
        var options = continuity ? Aria2ProcessOptions.ForApplication(runtime.Options.ExecutablePath, runtime.Options.RuntimeDirectory) with { RpcPort = runtime.Options.RpcPort } : runtime.Options;
        await using var manager = new Aria2ProcessManager(options, runtime.Repository()); await manager.StartAsync(default);
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
        using var upgraded = runtime.Db(); Assert.Equal(4, upgraded.Database.GetAppliedMigrations().Count());
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
        var options = Aria2ProcessOptions.ForApplication(runtime.Options.ExecutablePath, runtime.Options.RuntimeDirectory) with { RpcPort = runtime.Options.RpcPort };
        await using var manager = new Aria2ProcessManager(options, runtime.Repository()); await manager.StartAsync(default);
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
        Assert.Contains(server.Observations, row => row.Route == "/partial" && row.RequestedOffset > 0 && row.Status == 206);
        output.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { observations = server.Observations.Select(row => new { row.Route, row.RequestedOffset, row.Status }), sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(failed.Job.Destination))) }));
        Assert.Equal(System.Security.Cryptography.SHA256.HashData(server.Payload),
            System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(failed.Job.Destination)));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Resume_adversarial_origin_must_not_report_a_mixed_completed_file(bool ignoreRange, bool changeEntity)
    {
        using var runtime = new Runtime(); await using var server = new LocalHttpServer();
        var options = Aria2ProcessOptions.ForApplication(runtime.Options.ExecutablePath, runtime.Options.RuntimeDirectory) with { RpcPort = runtime.Options.RpcPort };
        await using var manager = new Aria2ProcessManager(options, runtime.Repository()); await manager.StartAsync(default);
        var store = new DownloadRuntimeStore(); var coordinator = runtime.Coordinator(new ManagedAria2Client(manager), store);
        await coordinator.AddAsync(new Uri(new Uri(server.Url), "partial").AbsoluteUri, directory: runtime.Output, fileName: "entity.bin");
        await WaitAsync(coordinator, () => store.Current.Downloads[0].Progress.Status == DownloadStatus.Failed);
        var failed = store.Current.Downloads[0];
        server.IgnoreRange = ignoreRange; server.ChangeEntity = changeEntity; server.ChangeEntityOnlyForRange = changeEntity;
        var retainedPartial = await File.ReadAllBytesAsync(failed.Job.Destination);
        var retainedControl = await File.ReadAllBytesAsync(failed.Job.Destination + ".aria2");
        Volatile.Write(ref server.RecoverPartial, true);
        await coordinator.RetryAsync(failed.Job.Id);
        await WaitAsync(coordinator, () => store.Current.Downloads[0].Progress.Status is DownloadStatus.Completed or DownloadStatus.Failed);
        Assert.Contains(server.Observations, row => row.Route == "/partial" && row.RequestedOffset > 0 && row.Status == (ignoreRange ? 200 : 206));
        var bytes = await File.ReadAllBytesAsync(failed.Job.Destination);
        output.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { ignoreRange, changeEntity, status = store.Current.Downloads[0].Progress.Status.ToString(), observations = server.Observations.Select(row => new { row.Route, row.RequestedOffset, row.Status }), actualSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)), originalSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(server.Payload)), changedSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(server.Payload.Select(value => (byte)(value ^ 0xff)).ToArray())), oldPrefixBytes = bytes.Zip(server.Payload).TakeWhile(pair => pair.First == pair.Second).Count() }));
        Assert.True(File.Exists(failed.Job.Destination));
        if (ignoreRange || changeEntity)
        {
            Assert.Equal(DownloadStatus.Failed, store.Current.Downloads[0].Progress.Status);
            Assert.Equal(retainedPartial, bytes);
            Assert.Equal(retainedControl, await File.ReadAllBytesAsync(failed.Job.Destination + ".aria2"));
        }
        if (store.Current.Downloads[0].Progress.Status == DownloadStatus.Completed)
        {
            var expected = changeEntity ? server.Payload.Select(value => (byte)(value ^ 0xff)).ToArray() : server.Payload;
            Assert.Equal(System.Security.Cryptography.SHA256.HashData(expected), System.Security.Cryptography.SHA256.HashData(bytes));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Staged_gateway_fixture_restarts_with_metadata_and_guards_actual_range_response(bool changeEntity)
    {
        using var runtime = new Runtime(); await using var server = new LocalHttpServer();
        Directory.CreateDirectory(runtime.Output);
        var metadata = new HttpRepresentationStore(runtime.Output, Guid.NewGuid());
        var origin = new Uri(new Uri(server.Url), "partial");
        await using var manager = new Aria2ProcessManager(runtime.Options); await manager.StartAsync(default);
        var store = new DownloadRuntimeStore(); var coordinator = runtime.Coordinator(new ManagedAria2Client(manager), store);
        DownloadEntry failed;
        await using (var initialGateway = new ContinuityGatewayFixture(origin, metadata))
        {
            await coordinator.AddAsync(initialGateway.Url, directory: runtime.Output, fileName: "guarded.bin");
            await WaitAsync(coordinator, () => store.Current.Downloads[0].Progress.Status == DownloadStatus.Failed);
            failed = store.Current.Downloads[0];
            Assert.NotNull(await metadata.LoadAsync(default));
        }
        var retained = await File.ReadAllBytesAsync(failed.Job.Destination);
        var control = await File.ReadAllBytesAsync(failed.Job.Destination + ".aria2");
        var staged = await RetainedPartialStage.CreateAsync(Path.Combine(runtime.Output, "stage"), failed.Job.Destination, default);
        var stage = staged.Directory;
        server.ChangeEntity = changeEntity; Volatile.Write(ref server.RecoverPartial, true);
        await using var restartedGateway = new ContinuityGatewayFixture(origin, metadata, retained: true);
        var gid = Guid.NewGuid().ToString("N")[..16];
        await manager.AddDownloadAsync(new(gid, new(restartedGateway.Url), Directory: stage, FileName: "guarded.bin", ContinueExisting: true), default);
        EngineDownload? observed = null;
        var until = DateTimeOffset.UtcNow.AddSeconds(12);
        while (DateTimeOffset.UtcNow < until)
        {
            observed = (await manager.GetDownloadsAsync([gid], default)).Single();
            if (observed.Progress.Status is DownloadStatus.Completed or DownloadStatus.Failed) break;
            await Task.Delay(100);
        }
        output.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { changeEntity, status = observed!.Progress.Status.ToString(), decisions = restartedGateway.Decisions, origin = server.Observations.Select(row => new { row.RequestedOffset, row.Status }) }));
        Assert.Equal(changeEntity ? DownloadStatus.Failed : DownloadStatus.Completed, observed!.Progress.Status);
        Assert.Equal(retained, await File.ReadAllBytesAsync(failed.Job.Destination));
        Assert.Equal(control, await File.ReadAllBytesAsync(failed.Job.Destination + ".aria2"));
        if (changeEntity)
        {
            Assert.Contains("reject:representation", restartedGateway.Decisions);
            Assert.Equal(retained, await File.ReadAllBytesAsync(Path.Combine(stage, "guarded.bin")));
            Assert.Equal(control, await File.ReadAllBytesAsync(Path.Combine(stage, "guarded.bin.aria2")));
        }
        else
        {
            Assert.Contains(restartedGateway.Decisions, row => row.StartsWith("allow:32768:206", StringComparison.Ordinal));
            Assert.Equal(System.Security.Cryptography.SHA256.HashData(server.Payload), System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(stage, "guarded.bin"))));
        }
    }

    [Fact]
    public async Task Gateway_guards_engine_internal_reconnect_without_a_coordinator_retry()
    {
        using var runtime = new Runtime(); await using var server = new LocalHttpServer { ChangeEntityOnInternalReconnect = true };
        Directory.CreateDirectory(runtime.Output);
        var metadata = new HttpRepresentationStore(runtime.Output, Guid.NewGuid());
        await using var gateway = new ContinuityGatewayFixture(new Uri(new Uri(server.Url), "partial"), metadata);
        await using var manager = new Aria2ProcessManager(runtime.Options); await manager.StartAsync(default);
        var store = new DownloadRuntimeStore(); var coordinator = runtime.Coordinator(new ManagedAria2Client(manager), store);
        await coordinator.AddAsync(gateway.Url, directory: runtime.Output, fileName: "internal.bin");
        await WaitAsync(coordinator, () => store.Current.Downloads[0].Progress.Status is DownloadStatus.Completed or DownloadStatus.Failed);
        var observed = Assert.Single(store.Current.Downloads);
        output.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { status = observed.Progress.Status.ToString(), decisions = gateway.Decisions, origin = server.Observations.Select(row => new { row.RequestedOffset, row.Status }) }));
        Assert.Equal(DownloadStatus.Failed, observed.Progress.Status);
        Assert.Contains("reject:representation", gateway.Decisions);
        Assert.Contains(server.Observations, row => row.RequestedOffset > 0 && row.Status == 206);
        Assert.Equal(server.Payload.Take(32768), (await File.ReadAllBytesAsync(observed.Job.Destination)).Take(32768));
        Assert.True(File.Exists(observed.Job.Destination + ".aria2"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Integrated_gateway_retry_publishes_stable_entity_and_preserves_retained_pair_on_denial(bool ignoreRange, bool changeEntity)
    {
        using var runtime = new Runtime(); await using var server = new LocalHttpServer();
        var options = runtime.Options with { UseContinuityGateway = true, ContinuityTestOrigins = source => new ControlledLoopbackOrigin(source) };
        await using var manager = new Aria2ProcessManager(options, runtime.Repository()); await manager.StartAsync(default);
        var store = new DownloadRuntimeStore(); var coordinator = runtime.Coordinator(new ManagedAria2Client(manager), store);
        await coordinator.AddAsync(new Uri(new Uri(server.Url), "partial").AbsoluteUri, directory: runtime.Output, fileName: "integrated.bin");
        await WaitAsync(coordinator, () => store.Current.Downloads[0].Progress.Status == DownloadStatus.Failed);
        var failed = Assert.Single(store.Current.Downloads);
        output.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { initialStatus = failed.Progress.Status.ToString(), failed.Progress.DownloadedBytes, failed.ErrorMessage, initialOrigin = server.Observations.Select(row => new { row.RequestedOffset, row.Status }) }));
        var retained = await File.ReadAllBytesAsync(failed.Job.Destination);
        var control = await File.ReadAllBytesAsync(failed.Job.Destination + ".aria2");
        server.IgnoreRange = ignoreRange; server.ChangeEntity = changeEntity; Volatile.Write(ref server.RecoverPartial, true);
        await coordinator.RetryAsync(failed.Job.Id);
        await WaitAsync(coordinator, () => store.Current.Downloads[0].Progress.Status is DownloadStatus.Completed or DownloadStatus.Failed);
        var result = Assert.Single(store.Current.Downloads);
        output.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { ignoreRange, changeEntity, status = result.Progress.Status.ToString(), origin = server.Observations.Select(row => new { row.RequestedOffset, row.Status }) }));
        Assert.Equal(ignoreRange || changeEntity ? DownloadStatus.Failed : DownloadStatus.Completed, result.Progress.Status);
        if (ignoreRange || changeEntity)
        {
            Assert.Equal(retained, await File.ReadAllBytesAsync(failed.Job.Destination));
            Assert.Equal(control, await File.ReadAllBytesAsync(failed.Job.Destination + ".aria2"));
        }
        else
        {
            Assert.Contains(server.Observations, row => row.RequestedOffset > 0 && row.Status == 206);
            Assert.Equal(System.Security.Cryptography.SHA256.HashData(server.Payload), System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(failed.Job.Destination)));
            Assert.False(File.Exists(failed.Job.Destination + ".aria2"));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Manual_gateway_redirect_retry_resumes_exact_resource_or_preserves_pair(bool changedResource)
    {
        using var runtime = new Runtime(); await using var server = new LocalHttpServer();
        // Actual production DNS/socket transport; only the trusted manual policy allows this fixture's LAN target.
        await using var manager = new Aria2ProcessManager(runtime.Options with { UseContinuityGateway = true }, runtime.Repository()); await manager.StartAsync(default);
        var store = new DownloadRuntimeStore(); var coordinator = runtime.Coordinator(new ManagedAria2Client(manager), store);
        await coordinator.AddAsync(new Uri(new Uri(server.Url), "redirect").AbsoluteUri, directory: runtime.Output, fileName: "redirect.bin");
        await WaitAsync(coordinator, () => store.Current.Downloads[0].Progress.Status == DownloadStatus.Failed);
        var failed = Assert.Single(store.Current.Downloads); var retained = await File.ReadAllBytesAsync(failed.Job.Destination);
        var control = await File.ReadAllBytesAsync(failed.Job.Destination + ".aria2");
        server.RedirectChanged = changedResource; Volatile.Write(ref server.RecoverPartial, true);
        await coordinator.RetryAsync(failed.Job.Id);
        await WaitAsync(coordinator, () => store.Current.Downloads[0].Progress.Status is DownloadStatus.Completed or DownloadStatus.Failed);
        Assert.Equal(changedResource ? DownloadStatus.Failed : DownloadStatus.Completed, store.Current.Downloads[0].Progress.Status);
        if (changedResource)
        { Assert.Equal(retained, await File.ReadAllBytesAsync(failed.Job.Destination)); Assert.Equal(control, await File.ReadAllBytesAsync(failed.Job.Destination + ".aria2")); }
        else
        {
            Assert.Contains(server.Observations, row => row.RequestedOffset > 0 && row.Status == 206);
            Assert.Equal(System.Security.Cryptography.SHA256.HashData(server.Payload), System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(failed.Job.Destination)));
        }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Integrated_gateway_restart_keeps_durable_pair_and_validates_retry(bool changed)
    {
        using var runtime = new Runtime(); await using var server = new LocalHttpServer();
        var options = runtime.Options with { UseContinuityGateway = true, ContinuityTestOrigins = source => new ControlledLoopbackOrigin(source) };
        DownloadEntry failed;
        await using (var first = new Aria2ProcessManager(options, runtime.Repository()))
        {
            await first.StartAsync(default);
            var store = new DownloadRuntimeStore(); var coordinator = runtime.Coordinator(new ManagedAria2Client(first), store);
            await coordinator.AddAsync(new Uri(new Uri(server.Url), "partial").AbsoluteUri, directory: runtime.Output, fileName: "restart.bin");
            await WaitAsync(coordinator, () => store.Current.Downloads[0].Progress.Status == DownloadStatus.Failed);
            failed = Assert.Single(store.Current.Downloads);
        }
        var retained = await File.ReadAllBytesAsync(failed.Job.Destination);
        var control = await File.ReadAllBytesAsync(failed.Job.Destination + ".aria2");
        server.ChangeEntity = changed; Volatile.Write(ref server.RecoverPartial, true);
        var requests = server.Requests;
        await using var second = new Aria2ProcessManager(options, runtime.Repository()); await second.StartAsync(default);
        Assert.Equal(requests, server.Requests); // Startup cannot silently send retained work.
        var reopened = new DownloadRuntimeStore(); var retry = runtime.Coordinator(new ManagedAria2Client(second), reopened);
        await retry.InitializeAsync(); await retry.RetryAsync(failed.Job.Id);
        await WaitAsync(retry, () => reopened.Current.Downloads[0].Progress.Status is DownloadStatus.Completed or DownloadStatus.Failed);
        Assert.Equal(changed ? DownloadStatus.Failed : DownloadStatus.Completed, reopened.Current.Downloads[0].Progress.Status);
        if (!changed) Assert.Contains(server.Observations, row => row.RequestedOffset > 0 && row.Status == 206);
        if (changed)
        {
            Assert.Equal(retained, await File.ReadAllBytesAsync(failed.Job.Destination));
            Assert.Equal(control, await File.ReadAllBytesAsync(failed.Job.Destination + ".aria2"));
        }
        else Assert.Equal(server.Payload, await File.ReadAllBytesAsync(failed.Job.Destination));
    }

    [Fact]
    public async Task Integrated_gateway_chunked_fresh_download_publishes_only_receipted_bytes()
    {
        using var runtime = new Runtime(); await using var server = new LocalHttpServer();
        var options = runtime.Options with { UseContinuityGateway = true, ContinuityTestOrigins = source => new ControlledLoopbackOrigin(source) };
        await using var manager = new Aria2ProcessManager(options, runtime.Repository()); await manager.StartAsync(default);
        var store = new DownloadRuntimeStore(); var coordinator = runtime.Coordinator(new ManagedAria2Client(manager), store);
        await coordinator.AddAsync(new Uri(new Uri(server.Url), "chunked").AbsoluteUri, directory: runtime.Output, fileName: "fresh.bin");
        await WaitAsync(coordinator, () => store.Current.Downloads[0].Progress.Status is DownloadStatus.Completed or DownloadStatus.Failed);
        var result = Assert.Single(store.Current.Downloads);
        Assert.Equal(DownloadStatus.Completed, result.Progress.Status);
        Assert.Equal(server.Payload, await File.ReadAllBytesAsync(result.Job.Destination));
        Assert.All(server.Observations, row => Assert.Equal(0, row.RequestedOffset));
    }

    [Fact]
    public async Task Interrupted_one_shot_retry_uses_new_empty_stage_and_preserves_old_pair()
    {
        using var runtime = new Runtime(); await using var server = new LocalHttpServer { WeakValidator = true };
        var options = runtime.Options with { UseContinuityGateway = true, ContinuityTestOrigins = source => new ControlledLoopbackOrigin(source) };
        await using var manager = new Aria2ProcessManager(options, runtime.Repository()); await manager.StartAsync(default);
        var store = new DownloadRuntimeStore(); var coordinator = runtime.Coordinator(new ManagedAria2Client(manager), store);
        await coordinator.AddAsync(new Uri(new Uri(server.Url), "partial").AbsoluteUri, directory: runtime.Output, fileName: "oneshot.bin");
        await WaitAsync(coordinator, () => store.Current.Downloads[0].Progress.Status == DownloadStatus.Failed);
        var failed = Assert.Single(store.Current.Downloads);
        var journal = Path.Combine(options.RuntimeDirectory, "continuity", "transfers", failed.Attempt.EngineId + ".json");
        using var document = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(journal));
        var work = Path.Combine(document.RootElement.GetProperty("Work").GetString()!, "oneshot.bin");
        var retained = await File.ReadAllBytesAsync(work); var control = await File.ReadAllBytesAsync(work + ".aria2");
        Volatile.Write(ref server.RecoverPartial, true);
        await coordinator.RetryAsync(failed.Job.Id);
        await WaitAsync(coordinator, () => store.Current.Downloads[0].Progress.Status is DownloadStatus.Completed or DownloadStatus.Failed);
        Assert.Equal(DownloadStatus.Completed, store.Current.Downloads[0].Progress.Status);
        Assert.Equal(server.Payload, await File.ReadAllBytesAsync(failed.Job.Destination));
        Assert.Equal(retained, await File.ReadAllBytesAsync(work)); Assert.Equal(control, await File.ReadAllBytesAsync(work + ".aria2"));
        Assert.NotEqual(failed.Attempt.Id, store.Current.Downloads[0].Attempt.Id);
        Assert.All(server.Observations, row => Assert.Equal(0, row.RequestedOffset));
    }

    [Fact]
    public async Task Legacy_direct_failed_attempt_restarts_guarded_from_zero_with_durable_parent_proof()
    {
        using var runtime = new Runtime(); await using var server = new LocalHttpServer(); DownloadEntry legacy;
        await using (var direct = new Aria2ProcessManager(runtime.Options, runtime.Repository()))
        {
            await direct.StartAsync(default); var store = new DownloadRuntimeStore(); var coordinator = runtime.Coordinator(new ManagedAria2Client(direct), store);
            await coordinator.AddAsync(new Uri(new Uri(server.Url), "partial").AbsoluteUri, directory: runtime.Output, fileName: "legacy.bin");
            await WaitAsync(coordinator, () => store.Current.Downloads[0].Progress.Status == DownloadStatus.Failed);
            legacy = Assert.Single(store.Current.Downloads);
        }
        var retained = await File.ReadAllBytesAsync(legacy.Job.Destination); var control = await File.ReadAllBytesAsync(legacy.Job.Destination + ".aria2");
        var requestCount = server.Observations.Count; Volatile.Write(ref server.RecoverPartial, true);
        var options = runtime.Options with { UseContinuityGateway = true, ContinuityTestOrigins = source => new ControlledLoopbackOrigin(source) };
        await using var guarded = new Aria2ProcessManager(options, runtime.Repository()); await guarded.StartAsync(default);
        await Assert.ThrowsAsync<EngineException>(() => guarded.AddDownloadAsync(new("1234567890abcdef", legacy.Job.Source,
            Directory: runtime.Output, FileName: "legacy.bin", ContinuityJobId: legacy.Job.Id, ContinuityAttemptId: Guid.NewGuid(),
            LegacyParentEngineId: legacy.Attempt.EngineId), default));
        Assert.Equal(retained, await File.ReadAllBytesAsync(legacy.Job.Destination)); Assert.Equal(control, await File.ReadAllBytesAsync(legacy.Job.Destination + ".aria2"));
        Assert.Equal(requestCount, server.Observations.Count);
        var reopened = new DownloadRuntimeStore(); var retry = runtime.Coordinator(new ManagedAria2Client(guarded), reopened);
        await retry.RetryAsync(legacy.Job.Id);
        await WaitAsync(retry, () => reopened.Current.Downloads[0].Progress.Status is DownloadStatus.Completed or DownloadStatus.Failed);
        var completed = Assert.Single(reopened.Current.Downloads); Assert.Equal(DownloadStatus.Completed, completed.Progress.Status);
        Assert.Equal(legacy.Attempt.Id, completed.Attempt.ParentAttemptId); Assert.Equal(server.Payload, await File.ReadAllBytesAsync(legacy.Job.Destination));
        Assert.All(server.Observations.Skip(requestCount), row => Assert.Equal(0, row.RequestedOffset));
        var stage = Path.Combine(runtime.Output, ".aria-gui-continuity-" + legacy.Job.Id.ToString("N"));
        Assert.Equal(retained, await File.ReadAllBytesAsync(Path.Combine(stage, "retained-" + completed.Attempt.Id.ToString("N") + ".payload")));
        Assert.Equal(control, await File.ReadAllBytesAsync(Path.Combine(stage, "retained-" + completed.Attempt.Id.ToString("N") + ".control")));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Interrupted_legacy_explicit_fresh_restart_preserves_old_bytes_even_without_control(bool keepControl)
    {
        using var runtime = new Runtime(); await using var server = new LocalHttpServer(); DownloadEntry legacy;
        await using (var direct = new Aria2ProcessManager(runtime.Options, runtime.Repository()))
        {
            await direct.StartAsync(default); var store = new DownloadRuntimeStore(); var coordinator = runtime.Coordinator(new ManagedAria2Client(direct), store);
            await coordinator.AddAsync(new Uri(new Uri(server.Url), "slow").AbsoluteUri, directory: runtime.Output, fileName: "interrupted.bin");
            await WaitAsync(coordinator, () => store.Current.Downloads[0].Progress.DownloadedBytes > 0);
            await coordinator.PauseAsync(store.Current.Downloads[0].Job.Id);
            await WaitAsync(coordinator, () => store.Current.Downloads[0].Progress.Status == DownloadStatus.Paused && store.Current.Downloads[0].PendingOperation is null);
            legacy = Assert.Single(store.Current.Downloads);
        }
        var retained = await File.ReadAllBytesAsync(legacy.Job.Destination); var control = await File.ReadAllBytesAsync(legacy.Job.Destination + ".aria2");
        if (!keepControl) File.Delete(legacy.Job.Destination + ".aria2");
        var uncertain = new PendingOperation(Guid.NewGuid(), legacy.Job.Id, DownloadAction.Recover, OperationStatus.Uncertain, DateTimeOffset.UtcNow);
        await runtime.Repository().InsertOperationAsync(legacy with { PendingOperation = uncertain }, default);
        await runtime.Repository().SaveAsync([legacy with { Progress = new(DownloadStatus.Interrupted, legacy.Progress.DownloadedBytes, legacy.Progress.TotalBytes, 0), PendingOperation = uncertain }], default);
        var requestCount = server.Observations.Count; server.ChangeEntity = true; Volatile.Write(ref server.RecoverPartial, true);
        var options = runtime.Options with { UseContinuityGateway = true, ContinuityTestOrigins = source => new ControlledLoopbackOrigin(source) };
        await using var guarded = new Aria2ProcessManager(options, runtime.Repository()); await guarded.StartAsync(default);
        var reopened = new DownloadRuntimeStore(); var restart = runtime.Coordinator(new ManagedAria2Client(guarded), reopened);
        await restart.RefreshAsync(); Assert.Equal(DownloadStatus.Interrupted, Assert.Single(reopened.Current.Downloads).Progress.Status);
        await restart.RestartFreshAsync(legacy.Job.Id);
        await WaitAsync(restart, () => reopened.Current.Downloads[0].Progress.Status is DownloadStatus.Completed or DownloadStatus.Failed);
        var completed = Assert.Single(reopened.Current.Downloads); Assert.Equal(DownloadStatus.Completed, completed.Progress.Status);
        Assert.Equal(legacy.Attempt.Id, completed.Attempt.ParentAttemptId);
        Assert.Equal(server.Payload.Select(value => (byte)(value ^ 0xff)), await File.ReadAllBytesAsync(legacy.Job.Destination));
        Assert.All(server.Observations.Skip(requestCount), row => Assert.Equal(0, row.RequestedOffset));
        var stage = Path.Combine(runtime.Output, ".aria-gui-continuity-" + legacy.Job.Id.ToString("N"));
        Assert.Equal(retained, await File.ReadAllBytesAsync(Path.Combine(stage, "retained-" + completed.Attempt.Id.ToString("N") + ".payload")));
        if (keepControl) Assert.Equal(control, await File.ReadAllBytesAsync(Path.Combine(stage, "retained-" + completed.Attempt.Id.ToString("N") + ".control")));
        using var db = runtime.Db(); Assert.Equal(OperationStatus.Failed, db.Operations.Single(row => row.Id == uncertain.Id).Status);
        Assert.Equal(DownloadStatus.Interrupted, db.Attempts.Single(row => row.Id == legacy.Attempt.Id).Status);
    }
    [Fact]
    public async Task Real_engine_destination_tree_stays_pinned_until_owned_engine_exit()
    {
        using var runtime = new Runtime(); await using var server = new LocalHttpServer();
        var options = runtime.Options with { UseContinuityGateway = true, ContinuityTestOrigins = source => new ControlledLoopbackOrigin(source) };
        await using var manager = new Aria2ProcessManager(options, runtime.Repository()); await manager.StartAsync(default);
        var store = new DownloadRuntimeStore(); var coordinator = runtime.Coordinator(new ManagedAria2Client(manager), store);
        await coordinator.AddAsync(new Uri(new Uri(server.Url), "slow").AbsoluteUri, directory: runtime.Output, fileName: "lease.bin");
        await WaitAsync(coordinator, () => store.Current.Downloads[0].Progress.DownloadedBytes > 0);
        Assert.ThrowsAny<IOException>(() => Directory.Move(runtime.Output, runtime.Output + "-moved"));
        await coordinator.PauseAsync(store.Current.Downloads[0].Job.Id);
        await WaitAsync(coordinator, () => store.Current.Downloads[0].Progress.Status == DownloadStatus.Paused);
        var bytes = await File.ReadAllBytesAsync(Path.Combine(runtime.Output, "lease.bin"));
        Assert.ThrowsAny<IOException>(() => Directory.Move(runtime.Output, runtime.Output + "-moved"));
        await manager.StopAsync(default); Directory.Move(runtime.Output, runtime.Output + "-moved");
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(runtime.Output + "-moved", "lease.bin")));
    }

    [Fact]
    public async Task Guarded_startup_restores_owned_paused_stage_and_resumes_same_gid()
    {
        using var runtime = new Runtime(); await using var server = new LocalHttpServer();
        var options = runtime.Options with { UseContinuityGateway = true, ContinuityTestOrigins = source => new ControlledLoopbackOrigin(source) };
        DownloadEntry before;
        await using (var first = new Aria2ProcessManager(options, runtime.Repository()))
        {
            await first.StartAsync(default); var store = new DownloadRuntimeStore(); var coordinator = runtime.Coordinator(new ManagedAria2Client(first), store);
            await coordinator.AddAsync(new Uri(new Uri(server.Url), "slow").AbsoluteUri, directory: runtime.Output, fileName: "restore.bin");
            await WaitAsync(coordinator, () => store.Current.Downloads[0].Progress.DownloadedBytes > 0);
            await coordinator.PauseAsync(store.Current.Downloads[0].Job.Id);
            await WaitAsync(coordinator, () => store.Current.Downloads[0].Progress.Status == DownloadStatus.Paused);
            before = Assert.Single(store.Current.Downloads);
        }
        var requests = server.Requests;
        await using var second = new Aria2ProcessManager(options, runtime.Repository());
        var info = await second.StartAsync(default); Assert.Contains("Restored 1 guarded", info.RecoveryMessage);
        Assert.Equal(requests, server.Requests);
        var reopened = new DownloadRuntimeStore(); var resumed = runtime.Coordinator(new ManagedAria2Client(second), reopened);
        await resumed.RefreshAsync(); Assert.Equal(before.Attempt.EngineId, Assert.Single(reopened.Current.Downloads).Attempt.EngineId);
        await resumed.ResumeAsync(before.Job.Id);
        await WaitAsync(resumed, () => reopened.Current.Downloads[0].Progress.Status is DownloadStatus.Completed or DownloadStatus.Failed);
        Assert.Equal(DownloadStatus.Completed, reopened.Current.Downloads[0].Progress.Status);
        Assert.Contains(server.Observations, row => row.RequestedOffset > 0 && row.Status == 206);
        Assert.Equal(server.Payload, await File.ReadAllBytesAsync(before.Job.Destination));
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
