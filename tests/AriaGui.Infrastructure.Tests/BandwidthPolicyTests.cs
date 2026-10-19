using System.Net;
using System.Net.Sockets;
using AriaGui.Application.Downloads;
using AriaGui.Infrastructure.Aria2;
using AriaGui.Infrastructure.Downloads;
using AriaGui.Infrastructure.Persistence;
using AriaGui.Core.Downloads;
using Microsoft.EntityFrameworkCore;
using System.Diagnostics;

namespace AriaGui.Infrastructure.Tests;

[Collection("Engine processes")]
public sealed class BandwidthPolicyTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public async Task Measure_two_direct_jobs_origin_socket_bytes_and_cancellation()
    {
        using var runtime = new Runtime();
        await using var source = new LocalHttpServer(4 * 1024 * 1024);
        await using var counted = new CountingTcpRelay(source.Url);
        var repository = runtime.Repository();
        await using var manager = new Aria2ProcessManager(runtime.Options with { UseContinuityGateway = false }, repository);
        await manager.StartAsync(default);
        var store = new DownloadRuntimeStore();
        var coordinator = new DownloadCoordinator(new ManagedAria2Client(manager), store, TimeProvider.System,
            repository, new FileDownloadDestination());
        await coordinator.SetBandwidthProfileAsync(new("Direct", new(32768), ByteRateLimit.Unlimited));
        var outputFolder = Path.Combine(Path.GetDirectoryName(runtime.Options.RuntimeDirectory)!, "direct-downloads");
        Directory.CreateDirectory(outputFolder);
        await coordinator.AddAsync(counted.Url + "?job=one", directory: outputFolder, fileName: "one.bin");
        await coordinator.AddAsync(counted.Url + "?job=two", directory: outputFolder, fileName: "two.bin");
        var startup = counted.OriginBytes;
        var timer = Stopwatch.StartNew();
        await Task.Delay(TimeSpan.FromSeconds(2));
        var first = counted.OriginBytes;
        var firstSeconds = timer.Elapsed.TotalSeconds;
        await Task.Delay(TimeSpan.FromSeconds(2));
        var second = counted.OriginBytes;
        var secondSeconds = timer.Elapsed.TotalSeconds - firstSeconds;
        output.WriteLine($"direct origin socket bytes: startup={startup}, first={first - startup}/{firstSeconds:F2}s, second={second - first}/{secondSeconds:F2}s");
        Assert.True(source.Requests >= 2);
        // Direct mode has no relay backpressure. Startup read-ahead is reported,
        // not presented as an upstream cap; the application uses guarded mode.
        Assert.True(second > 0); // Startup read-ahead also proves origin bytes were read; windows are diagnostic.
        foreach (var job in store.Current.Downloads.ToArray()) await coordinator.RemoveAsync(job.Job.Id);
        var cancelled = counted.OriginBytes;
        await Task.Delay(500);
        output.WriteLine($"direct post-cancel origin bytes: {counted.OriginBytes - cancelled}");
        Assert.InRange(counted.OriginBytes - cancelled, 0, 65536);
    }

    [Fact]
    public async Task Measure_two_guarded_jobs_upstream_read_ahead_and_cancellation()
    {
        using var runtime = new Runtime();
        await using var source = new LocalHttpServer(4 * 1024 * 1024);
        var repository = runtime.Repository();
        long originBytes = 0;
        var options = runtime.Options with { UseContinuityGateway = true,
            ContinuityTestOrigins = url => new ControlledLoopbackOrigin(url, count => Interlocked.Add(ref originBytes, count)) };
        await using var manager = new Aria2ProcessManager(options, repository);
        await manager.StartAsync(default);
        var store = new DownloadRuntimeStore();
        var coordinator = new DownloadCoordinator(new ManagedAria2Client(manager), store, TimeProvider.System,
            repository, new FileDownloadDestination());
        await coordinator.SetBandwidthProfileAsync(new("Measured", new(32768), ByteRateLimit.Unlimited));
        var outputFolder = Path.Combine(Path.GetDirectoryName(runtime.Options.RuntimeDirectory)!, "downloads");
        Directory.CreateDirectory(outputFolder);
        await coordinator.AddAsync(source.Url + "?job=one", directory: outputFolder, fileName: "one.bin");
        await coordinator.AddAsync(source.Url + "?job=two", directory: outputFolder, fileName: "two.bin");
        var timer = Stopwatch.StartNew();
        var startup = Interlocked.Read(ref originBytes);
        await Task.Delay(TimeSpan.FromSeconds(2));
        var steady = Interlocked.Read(ref originBytes);
        var firstSeconds = timer.Elapsed.TotalSeconds;
        await Task.Delay(TimeSpan.FromSeconds(2));
        var later = Interlocked.Read(ref originBytes);
        var secondSeconds = timer.Elapsed.TotalSeconds - firstSeconds;
        output.WriteLine($"upstream socket bytes: startup={startup}, first={steady - startup}/{firstSeconds:F2}s, second={later - steady}/{secondSeconds:F2}s; cap=32768 B/s");
        Assert.True(source.Requests >= 2);
        Assert.InRange(startup, 0, 64 * 1024); // HTTP headers and bounded pre-body socket read-ahead.
        Assert.True(steady - startup <= 32768 * firstSeconds + 32768);
        Assert.True(later - steady <= 32768 * secondSeconds + 32768);
        await coordinator.SetBandwidthProfileAsync(new("Raised", new(65536), ByteRateLimit.Unlimited));
        var raisedStart = Interlocked.Read(ref originBytes);
        var raisedTimer = Stopwatch.StartNew();
        await Task.Delay(TimeSpan.FromSeconds(2));
        var raised = Interlocked.Read(ref originBytes);
        output.WriteLine($"raised cap socket bytes: {raised - raisedStart}/{raisedTimer.Elapsed.TotalSeconds:F2}s; cap=65536 B/s");
        Assert.True(raised - raisedStart <= 65536 * raisedTimer.Elapsed.TotalSeconds + 32768);
        Assert.True(raised - raisedStart >= 32768);
        foreach (var job in store.Current.Downloads.ToArray()) await coordinator.RemoveAsync(job.Job.Id);
        var atCancel = Interlocked.Read(ref originBytes);
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        var postCancel = Interlocked.Read(ref originBytes);
        output.WriteLine($"cancel bytes: at={atCancel}, after 500ms={postCancel}, excess={postCancel - atCancel}");
        Assert.InRange(postCancel - atCancel, 0, 32768);
    }

    [Fact]
    public async Task Schedule_timezone_override_and_policy_hold_round_trip_through_migration()
    {
        using var runtime = new Runtime();
        var repository = runtime.Repository();
        await repository.InitializeAsync(default);
        var schedule = new BandwidthSchedule("India Standard Time", BandwidthProfile.Unlimited,
            [new(DayOfWeek.Monday, 540, 600, ScheduleAction.Block)],
            new(ScheduleAction.Profile, new("Temporary", new(32768), ByteRateLimit.Unlimited),
                new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero)), true,
            SavedProfiles: [new("Metered", new(32768), ByteRateLimit.Unlimited),
                new("Fast", new(262144), new(65536))]);
        await repository.SaveBandwidthScheduleAsync(schedule, default);
        var jobId = Guid.NewGuid(); var attemptId = Guid.NewGuid(); var gid = Guid.NewGuid().ToString("N")[..16];
        var destination = Path.Combine(runtime.Options.RuntimeDirectory, "held.bin");
        var entry = new DownloadEntry(new(jobId, "held.bin", new Uri("https://example.com/held.bin"), destination, DateTimeOffset.UtcNow),
            new(attemptId, jobId, gid, DateTimeOffset.UtcNow), new(Guid.NewGuid(), jobId, attemptId, "held.bin", null),
            new(DownloadStatus.Queued, 0, null, 0), new(Guid.NewGuid(), jobId, DownloadAction.Add, OperationStatus.Pending, DateTimeOffset.UtcNow),
            PolicyHeld: true);
        await repository.InsertIntentAsync(entry, default);
        var reopened = runtime.Repository();
        await reopened.InitializeAsync(default);
        var loaded = await reopened.LoadBandwidthScheduleAsync(default);
        Assert.Equal(schedule.TimeZoneId, loaded.TimeZoneId);
        Assert.True(loaded.Enabled);
        Assert.Equal(schedule.Intervals[0], Assert.Single(loaded.Intervals));
        Assert.Equal(schedule.Override, loaded.Override);
        Assert.Equal(schedule.SavedProfiles, loaded.SavedProfiles);
        Assert.True((await reopened.GetAsync(jobId, default))!.PolicyHeld);
        using var db = runtime.Db(); Assert.Equal(12, db.Database.GetAppliedMigrations().Count());
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task Scheduled_profile_is_staged_before_a_new_engine_starts()
    {
        using var runtime = new Runtime();
        var repository = runtime.Repository();
        await repository.InitializeAsync(default);
        var metered = new BandwidthProfile("Startup", new(65536), ByteRateLimit.Unlimited);
        await repository.SaveBandwidthScheduleAsync(new("UTC", metered, [], Enabled: true), default);
        await using var engine = new Aria2ProcessManager(runtime.Options, repository);
        await engine.StartAsync(default);
        Assert.Equal(metered.Download, (await engine.ObserveBandwidthAsync(default)).Download);
        var staged = await repository.LoadBandwidthPolicyAsync(default);
        Assert.Equal(metered, staged.Requested);
        Assert.Equal(OperationStatus.Pending, staged.RequestStatus);
        var store = new DownloadRuntimeStore();
        await new DownloadCoordinator(new ManagedAria2Client(engine), store, TimeProvider.System,
            repository, new FileDownloadDestination()).RefreshAsync();
        Assert.Equal(staged.RequestedRevision, (await repository.LoadBandwidthPolicyAsync(default)).ConfirmedRevision);
    }

    [Fact]
    public async Task Enabled_schedule_keeps_restored_job_paused_until_runtime_evaluation()
    {
        using var runtime = new Runtime();
        var repository = runtime.Repository();
        await repository.InitializeAsync(default);
        await repository.SaveBandwidthScheduleAsync(new("UTC", BandwidthProfile.Unlimited,
            Enum.GetValues<DayOfWeek>().Select(day => new WeeklyBandwidthInterval(day, 0, 1440, ScheduleAction.Block)).ToArray(),
            Enabled: true), default);
        var jobId = Guid.NewGuid(); var attemptId = Guid.NewGuid(); var gid = Guid.NewGuid().ToString("N")[..16];
        var folder = Path.Combine(runtime.Options.RuntimeDirectory, "scheduled-output"); Directory.CreateDirectory(folder);
        var source = new Uri("http://127.0.0.1:54321/never-requested");
        var entry = new DownloadEntry(new(jobId, "held.bin", source, Path.Combine(folder, "held.bin"), DateTimeOffset.UtcNow),
            new(attemptId, jobId, gid, DateTimeOffset.UtcNow), new(Guid.NewGuid(), jobId, attemptId, "held.bin", null),
            new(DownloadStatus.Queued, 0, null, 0), new(Guid.NewGuid(), jobId, DownloadAction.Add, OperationStatus.Pending, DateTimeOffset.UtcNow));
        await repository.InsertIntentAsync(entry, default);
        await using (var engine = new Aria2ProcessManager(runtime.Options, repository))
        {
            await engine.StartAsync(default);
            await engine.AddDownloadAsync(new(gid, source, Directory: folder, FileName: "held.bin", StartPaused: true,
                ContinuityJobId: jobId, ContinuityAttemptId: attemptId), default);
            await engine.StopAsync(default);
        }
        var reopened = runtime.Repository();
        await using var restored = new Aria2ProcessManager(runtime.Options, reopened);
        await restored.StartAsync(default);
        Assert.Equal(DownloadStatus.Paused, (await restored.GetDownloadsAsync([gid], default)).Single().Progress.Status);
        Assert.True((await reopened.GetAsync(jobId, default))!.PolicyHeld);
        Assert.False((await reopened.GetAsync(jobId, default))!.DesiredPaused);
    }

    [Fact]
    public async Task Owned_paused_job_cap_is_applied_and_restored_before_unpause()
    {
        using var runtime = new Runtime();
        var repository = runtime.Repository();
        await repository.InitializeAsync(default);
        var jobId = Guid.NewGuid(); var attemptId = Guid.NewGuid();
        var gid = Guid.NewGuid().ToString("N")[..16];
        var folder = Path.Combine(runtime.Options.RuntimeDirectory, "owned-output");
        Directory.CreateDirectory(folder);
        var source = new Uri("http://127.0.0.1:54321/synthetic-never-requested");
        var entry = new DownloadEntry(new(jobId, "paused.bin", source, Path.Combine(folder, "paused.bin"), DateTimeOffset.UtcNow),
            new(attemptId, jobId, gid, DateTimeOffset.UtcNow), new(Guid.NewGuid(), jobId, attemptId, "paused.bin", null),
            new(DownloadStatus.Queued, 0, null, 0), new(Guid.NewGuid(), jobId, DownloadAction.Add, OperationStatus.Pending, DateTimeOffset.UtcNow),
            DesiredPaused: true);
        await repository.InsertIntentAsync(entry, default);
        await repository.SaveAsync([entry], default); // Persist the deliberate user pause after Add intent admission.
        await repository.SaveJobBandwidthPolicyAsync(new(jobId, 1, 0, new(32768), ByteRateLimit.Unlimited,
            RequestStatus: OperationStatus.Uncertain), default);
        await using (var engine = new Aria2ProcessManager(runtime.Options, repository))
        {
            await engine.StartAsync(default);
            await engine.AddDownloadAsync(new(gid, source, Directory: folder, FileName: "paused.bin", StartPaused: true,
                ContinuityJobId: jobId, ContinuityAttemptId: attemptId), default);
            await Assert.ThrowsAsync<AriaGui.Application.Engine.EngineException>(() =>
                engine.ApplyJobBandwidthAsync(Guid.NewGuid(), gid, new(32768), default));
            Assert.Equal(new ByteRateLimit(32768), (await engine.ObserveJobBandwidthAsync(jobId, gid, default)).Download);
            var coordinator = new DownloadCoordinator(new ManagedAria2Client(engine), new(), TimeProvider.System,
                repository, new FileDownloadDestination());
            await coordinator.RefreshAsync(); // Confirm the saved cap used by addUri, without replaying changeOption.
            await coordinator.SetJobBandwidthAsync(jobId, new(65536));
            Assert.Equal(new ByteRateLimit(65536), (await engine.ObserveJobBandwidthAsync(jobId, gid, default)).Download);
            await engine.StopAsync(default);
        }
        var reopened = runtime.Repository();
        await using var restarted = new Aria2ProcessManager(runtime.Options, reopened);
        await restarted.StartAsync(default);
        var restored = await restarted.ObserveJobBandwidthAsync(jobId, gid, default);
        Assert.Equal(new ByteRateLimit(65536), restored.Download);
        Assert.Equal(DownloadStatus.Paused, (await restarted.GetDownloadsAsync([gid], default)).Single().Progress.Status);
        using var db = runtime.Db(); Assert.Equal(12, db.Database.GetAppliedMigrations().Count());
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task Uncertain_saved_rate_is_present_when_a_new_engine_starts()
    {
        using var runtime = new Runtime();
        var repository = runtime.Repository();
        await repository.InitializeAsync(default);
        var profile = new BandwidthProfile("Pending", new(262144), ByteRateLimit.Unlimited);
        await repository.SaveBandwidthPolicyAsync(new(1, 0, profile, BandwidthProfile.Unlimited,
            RequestStatus: AriaGui.Core.Downloads.OperationStatus.Uncertain), default);
        await using var engine = new Aria2ProcessManager(runtime.Options, repository);
        await engine.StartAsync(default);
        var observed = await engine.ObserveBandwidthAsync(default);
        Assert.Equal(profile.Download, observed.Download);
        Assert.Equal(profile.Upload, observed.Upload);
        Assert.Equal(AriaGui.Core.Downloads.OperationStatus.Uncertain,
            (await repository.LoadBandwidthPolicyAsync(default)).RequestStatus);
        var coordinator = new DownloadCoordinator(new ManagedAria2Client(engine), new(), TimeProvider.System,
            repository, new FileDownloadDestination());
        await coordinator.RefreshAsync();
        Assert.Equal(1, (await repository.LoadBandwidthPolicyAsync(default)).ConfirmedRevision);
    }

    [Fact]
    public async Task Aggregate_profile_is_persisted_observed_and_applied_before_a_new_engine_session()
    {
        using var runtime = new Runtime();
        var repository = runtime.Repository();
        var profile = new BandwidthProfile("Metered", new(131072), new(65536));
        await using (var engine = new Aria2ProcessManager(runtime.Options, repository))
        {
            await engine.StartAsync(default);
            var coordinator = new DownloadCoordinator(new ManagedAria2Client(engine), new(), TimeProvider.System,
                repository, new FileDownloadDestination());
            await coordinator.SetBandwidthProfileAsync(profile);
            var applied = await engine.ObserveBandwidthAsync(default);
            Assert.Equal(profile.Download, applied.Download);
            Assert.Equal(profile.Upload, applied.Upload);
            Assert.Equal(applied.Generation, (await repository.LoadBandwidthPolicyAsync(default)).ConfirmedGeneration);
            await engine.StopAsync(default);
        }

        using (var db = runtime.Db())
        {
            Assert.Equal(12, db.Database.GetAppliedMigrations().Count());
            Assert.False(db.Database.HasPendingModelChanges());
        }
        var reopened = runtime.Repository();
        await using var restarted = new Aria2ProcessManager(runtime.Options, reopened);
        var info = await restarted.StartAsync(default);
        var restored = await restarted.ObserveBandwidthAsync(default);
        Assert.Equal(info.SessionId, restored.Generation);
        Assert.Equal(profile.Download, restored.Download);
        Assert.Equal(profile.Upload, restored.Upload);
        var store = new DownloadRuntimeStore();
        var coordinatorAfterRestart = new DownloadCoordinator(new ManagedAria2Client(restarted), store,
            TimeProvider.System, reopened, new FileDownloadDestination());
        await coordinatorAfterRestart.RefreshAsync();
        Assert.Equal(info.SessionId, (await reopened.LoadBandwidthPolicyAsync(default)).ConfirmedGeneration);
        Assert.Null(store.Current.BandwidthPolicy?.RequestStatus);
    }

    private sealed class Runtime : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "AriaGUI.BandwidthTests", Guid.NewGuid().ToString("N"));
        private string Database => Path.Combine(_root, "data", "downloads.db");
        public Aria2ProcessOptions Options { get; }

        public Runtime()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            Options = new(Path.Combine(AppContext.BaseDirectory, "Engine", "aria2c.exe"), Path.Combine(_root, "data"))
                { RpcPort = port };
        }

        public SqliteDownloadRepository Repository() => new(Database);
        public DownloadDbContext Db() => new(new DbContextOptionsBuilder<DownloadDbContext>()
            .UseSqlite($"Data Source={Database};Pooling=False").Options);

        public void Dispose()
        {
            var allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "AriaGUI.BandwidthTests")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(_root).StartsWith(allowed, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unsafe test cleanup path.");
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
    }
}
