using System.Collections.Concurrent;
using System.Text.Json;
using AriaGui.Application.Downloads;
using AriaGui.Application.Engine;
using AriaGui.Browser.Contracts;
using AriaGui.Core.Downloads;
using AriaGui.Infrastructure.Downloads;
using AriaGui.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AriaGui.Infrastructure.Tests;

public sealed class ExternalIntakeTests
{
    [Fact]
    public async Task PreClaimFailureIsObservableAndRestartSafelyDispatchesNeverSent()
    {
        using var fixture = new Fixture(); var service = fixture.Service(readiness: new BrokenReadiness());
        var request = fixture.Request(); var receipt = await service.Intake.AcceptAsync(request); await service.Intake.DrainAsync();
        Assert.Equal(ResultCode.Accepted, receipt.Code); Assert.Equal(RejectionReason.NotReady, service.Intake.LastDispatchFailure);
        using (var db = fixture.Db()) Assert.Equal(ExternalDispatchPhase.NeverSent, db.ExternalRequests.Single().DispatchPhase);
        Assert.Equal(0, fixture.Engine.AddCalls);
        var restarted = fixture.Service(); await restarted.Intake.InitializeAsync(); await restarted.Intake.DrainAsync();
        Assert.Equal(1, fixture.Engine.AddCalls); Assert.Equal(receipt.JobId, (await restarted.Intake.GetStatusAsync(request.Key)).JobId);
    }
    [Fact]
    public async Task StartupOfflineNeverSentRemainsQueuedAndUnclaimedUntilReadiness()
    {
        using var fixture = new Fixture(); using var lifetime = new CancellationTokenSource(); var gate = new ReadinessGate();
        var service = fixture.Service(lifetime.Token, readiness: gate); var request = fixture.Request();
        var accepted = await service.Intake.AcceptAsync(request); await gate.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(ResultCode.Accepted, accepted.Code); Assert.Equal(DownloadStatus.Queued, accepted.JobStatus);
        using (var db = fixture.Db()) Assert.Equal(ExternalDispatchPhase.NeverSent, db.ExternalRequests.Single().DispatchPhase);
        Assert.Equal(0, fixture.Engine.AddCalls); Assert.False((await service.Intake.GetStatusAsync(request.Key)).EngineUncertain);
        lifetime.Cancel(); await service.Intake.DrainAsync(); // Shutdown while offline must not consume the claim.
        var restartedGate = new ReadinessGate(); var restarted = fixture.Service(readiness: restartedGate);
        await restarted.Intake.InitializeAsync(); await restartedGate.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(0, fixture.Engine.AddCalls);
        restartedGate.Ready.SetResult(); await restarted.Intake.DrainAsync(); Assert.Equal(1, fixture.Engine.AddCalls);
        Assert.Equal(accepted.JobId, (await restarted.Intake.GetStatusAsync(request.Key)).JobId);
    }
    [Fact]
    public async Task ActualLifecycleReadinessEventGatesDispatchAfterStartup()
    {
        using var fixture = new Fixture(); var state = new EngineStateStore(); var managed = new StartingEngine();
        using var lifecycle = new EngineLifecycleService(managed, state); var service = fixture.Service(readiness: new EngineDispatchReadiness(state));
        var receipt = await service.Intake.AcceptAsync(fixture.Request()); Assert.Equal(ResultCode.Accepted, receipt.Code);
        Assert.Equal(0, fixture.Engine.AddCalls);
        var start = lifecycle.StartAsync(); await managed.Started.Task.WaitAsync(TimeSpan.FromSeconds(3)); Assert.Equal(EngineStatus.Starting, state.Current.Status);
        using (var db = fixture.Db()) Assert.Equal(ExternalDispatchPhase.NeverSent, db.ExternalRequests.Single().DispatchPhase);
        managed.Ready.SetResult(); await start; await service.Intake.DrainAsync(); Assert.Equal(EngineStatus.Ready, state.Current.Status); Assert.Equal(1, fixture.Engine.AddCalls);
    }
    [Fact]
    public async Task D01TwentyConcurrentCallsAcrossRepositoriesConvergeAndLostReplyReturnsOriginalJob()
    {
        using var fixture = new Fixture(); var first = fixture.Service(); var second = fixture.Service();
        await first.Intake.InitializeAsync(); await second.Intake.InitializeAsync(); var request = fixture.Request();
        var receipts = await Task.WhenAll(Enumerable.Range(0, 20).Select(index => (index % 2 == 0 ? first : second).Intake.AcceptAsync(request)));
        Assert.All(receipts, result => Assert.Equal(ResultCode.Accepted, result.Code));
        Assert.Single(receipts.Select(result => result.JobId).Distinct()); Assert.Single(receipts, result => !result.Duplicate);
        await first.Intake.DrainAsync(); await second.Intake.DrainAsync();
        using var db = fixture.Db(); Assert.Single(db.ExternalRequests); Assert.Single(db.Jobs); Assert.Single(db.Attempts); Assert.Single(db.Files); Assert.Single(db.Operations);
        Assert.Equal(1, fixture.Engine.AddCalls);
        var restarted = fixture.Service(); var replay = await restarted.Intake.AcceptAsync(request); await restarted.Intake.DrainAsync();
        Assert.True(replay.Duplicate); Assert.Equal(receipts[0].JobId, replay.JobId); Assert.Equal(1, fixture.Engine.AddCalls);
        Assert.Equal(ResultCode.NotFound, (await restarted.Intake.GetStatusAsync(request.Key with { CallerKey = "another-caller" })).Code);
        Assert.Equal(ResultCode.NotFound, (await restarted.Intake.GetStatusAsync(request.Key with { InstallationId = Guid.NewGuid() })).Code);
    }
    [Fact]
    public async Task D02DifferentGesturesSameUrlAndDestinationConflict()
    {
        using var fixture = new Fixture(); var service = fixture.Service();
        var first = await service.Intake.AcceptAsync(fixture.Request("one.zip"));
        var second = await service.Intake.AcceptAsync(fixture.Request("two.zip"));
        var conflict = await service.Intake.AcceptAsync(fixture.Request("one.zip")); await service.Intake.DrainAsync();
        Assert.Equal(ResultCode.Accepted, first.Code); Assert.Equal(ResultCode.Accepted, second.Code); Assert.NotEqual(first.JobId, second.JobId);
        Assert.Equal(RejectionReason.DestinationConflict, conflict.Reason);
        using var db = fixture.Db(); Assert.Equal(2, db.Jobs.Count()); Assert.Equal(3, db.ExternalRequests.Count()); Assert.Equal(2, fixture.Engine.AddCalls);
    }
    [Fact]
    public async Task D03ChangedPayloadConflictsButFragmentsNormalizeAndSignedEncodingDoesNot()
    {
        using var fixture = new Fixture(); var service = fixture.Service(); var request = fixture.Request() with { SourceUrl = "https://example.invalid/file?sig=a%2Fb&b=2&a=1#one" };
        var accepted = await service.Intake.AcceptAsync(request); await service.Intake.DrainAsync();
        using (var savedDb = fixture.Db()) Assert.Equal(request.SourceUrl.Split('#')[0], savedDb.Jobs.Single().Source);
        Assert.Equal(accepted.JobId, (await service.Intake.AcceptAsync(request with { SourceUrl = request.SourceUrl.Replace("#one", "#two") })).JobId);
        foreach (var altered in new[] { request with { SuggestedFileName = "changed.zip" }, request with { SourceUrl = "https://example.invalid/other" },
            request with { SourceUrl = "https://example.invalid/file?sig=a%2fb&b=2&a=1" }, request with { CreatedAt = request.CreatedAt.AddSeconds(1) } })
            Assert.Equal(RejectionReason.RequestIdConflict, (await service.Intake.AcceptAsync(altered)).Reason);
        Assert.Equal(1, fixture.Engine.AddCalls); using var db = fixture.Db(); Assert.Single(db.Jobs);
    }
    [Fact]
    public async Task D04FailureInsideLedgerInsertRollsBackAllFiveRows()
    {
        using var fixture = new Fixture(); var service = fixture.Service(); await service.Intake.InitializeAsync();
        using var db = fixture.Db(); db.Database.ExecuteSqlRaw("CREATE TRIGGER FailExternal AFTER INSERT ON ExternalRequests BEGIN SELECT RAISE(ABORT, 'synthetic failure'); END;");
        var result = await service.Intake.AcceptAsync(fixture.Request()); Assert.NotEqual(ResultCode.Accepted, result.Code);
        Assert.Equal(RejectionReason.StorageUnavailable, result.Reason); Assert.Empty(db.Jobs); Assert.Empty(db.Attempts); Assert.Empty(db.Files); Assert.Empty(db.Operations); Assert.Empty(db.ExternalRequests);
        Assert.Equal(0, fixture.Engine.AddCalls);
    }
    [Fact]
    public async Task D04ReceiptLostAfterRealCommitRecoversSameDurableJob()
    {
        using var fixture = new Fixture(); var request = fixture.Request();
        var service = fixture.Service(hooks: new(AfterCommit: () => throw new DownloadStorageException("Synthetic response boundary fault.")));
        var unknown = await service.Intake.AcceptAsync(request); Assert.Equal(ResultCode.Unknown, unknown.Code); Assert.Equal(0, fixture.Engine.AddCalls);
        Guid job; using (var db = fixture.Db()) { job = db.ExternalRequests.Single().JobId!.Value; Assert.Single(db.Jobs); Assert.Single(db.Attempts); Assert.Single(db.Files); Assert.Single(db.Operations); }
        var restarted = fixture.Service(); await restarted.Intake.InitializeAsync(); await restarted.Intake.DrainAsync();
        var replay = await restarted.Intake.AcceptAsync(request); Assert.Equal(ResultCode.Accepted, replay.Code); Assert.True(replay.Duplicate); Assert.Equal(job, replay.JobId); Assert.Equal(1, fixture.Engine.AddCalls);
    }
    [Fact]
    public async Task W05InjectedSqliteFullAndCancellationBeforeCommitNeverAccept()
    {
        using var fixture = new Fixture(); var failed = fixture.Service(hooks: new(BeforeCommit: () => throw new Microsoft.Data.Sqlite.SqliteException("Synthetic SQLITE_FULL",13)));
        var result = await failed.Intake.AcceptAsync(fixture.Request()); Assert.NotEqual(ResultCode.Accepted, result.Code); Assert.Equal(RejectionReason.StorageUnavailable, result.Reason);
        using var client = new CancellationTokenSource(); var cancelled = fixture.Service(hooks: new(BeforeCommit: () => client.Cancel()));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.Intake.AcceptAsync(fixture.Request(), client.Token));
        using var db = fixture.Db(); Assert.Empty(db.Jobs); Assert.Empty(db.ExternalRequests); Assert.Equal(0, fixture.Engine.AddCalls);
    }
    [Fact]
    public async Task SignedPercentEncodingSurvivesSqliteEngineRequestAndRestartLoad()
    {
        using var fixture = new Fixture(); var service = fixture.Service();
        const string source = "https://example.invalid/file?sig=%41%7e%2f%2Fb%2Bc&z=2&a=1";
        var receipt = await service.Intake.AcceptAsync(fixture.Request() with { SourceUrl = source + "#ignored" }); await service.Intake.DrainAsync();
        using (var db = fixture.Db()) Assert.Equal(source, db.Jobs.Single().Source);
        var submitted = Assert.Single(fixture.Engine.Requests); Assert.Equal(source, submitted.ExactSourceUrl);
        Assert.NotEqual(source, submitted.Source.AbsoluteUri); // This case proves why the original Uri path was insufficient.
        var loaded = await fixture.Service().Repository.GetAsync(receipt.JobId!.Value, default); Assert.Equal(source, loaded!.ExactSourceUrl);
    }
    [Fact]
    public async Task ClaimPersistedBeforeCrashButBeforeEngineCallNeverAutomaticallyReplays()
    {
        using var fixture = new Fixture(); var initial = fixture.Service(); await initial.Intake.InitializeAsync();
        var request = fixture.Request(); var job = SeedNeverSent(fixture, request);
        Assert.True(await initial.Repository.ClaimExternalDispatchAsync(job, fixture.Clock.Now, default));
        var restarted = fixture.Service(); await restarted.Intake.InitializeAsync(); await restarted.Intake.DrainAsync(); await restarted.Coordinator.RefreshAsync();
        var status = await restarted.Intake.GetStatusAsync(request.Key); Assert.Equal(ResultCode.Accepted, status.Code); Assert.True(status.EngineUncertain); Assert.Equal(DownloadStatus.Interrupted, status.JobStatus); Assert.Equal(0, fixture.Engine.AddCalls);
    }
    [Fact]
    public async Task D04CommittedNeverSentIntentRecoversWithoutClientAndClaimedMissingIntentNeverReplays()
    {
        using var fixture = new Fixture(); var service = fixture.Service(); await service.Intake.InitializeAsync();
        var request = fixture.Request(); var job = SeedNeverSent(fixture, request);
        var restarted = fixture.Service(); await restarted.Coordinator.RefreshAsync();
        Assert.Equal(DownloadStatus.Queued, (await restarted.Repository.GetAsync(job, default))!.Progress.Status);
        await restarted.Intake.InitializeAsync(); await restarted.Intake.DrainAsync(); Assert.Equal(1, fixture.Engine.AddCalls);
        using (var db = fixture.Db()) Assert.Equal(ExternalDispatchPhase.Claimed, db.ExternalRequests.Single().DispatchPhase);
        fixture.Engine.Downloads.Clear();
        var secondRestart = fixture.Service(); await secondRestart.Intake.InitializeAsync(); await secondRestart.Intake.DrainAsync(); await secondRestart.Coordinator.RefreshAsync();
        Assert.Equal(1, fixture.Engine.AddCalls); var status = await secondRestart.Intake.GetStatusAsync(request.Key);
        Assert.Equal(job, status.JobId); Assert.Equal(DownloadStatus.Interrupted, status.JobStatus); Assert.True(status.EngineUncertain);
    }
    [Fact]
    public async Task D05EngineAcceptedButReplyLostReconcilesSameGidWithoutAdd()
    {
        using var fixture = new Fixture(); fixture.Engine.LoseReply = true; var service = fixture.Service(); var request = fixture.Request();
        var receipt = await service.Intake.AcceptAsync(request); Assert.Equal(ResultCode.Accepted, receipt.Code); await service.Intake.DrainAsync();
        Assert.True((await service.Intake.GetStatusAsync(request.Key)).EngineUncertain);
        var restarted = fixture.Service(); await restarted.Intake.InitializeAsync(); await restarted.Coordinator.RefreshAsync();
        var status = await restarted.Intake.GetStatusAsync(request.Key); Assert.Equal(receipt.JobId, status.JobId); Assert.False(status.EngineUncertain);
        Assert.Equal(1, fixture.Engine.AddCalls); using var db = fixture.Db(); Assert.Single(db.Attempts);
    }
    [Fact]
    public async Task ClientCancellationAfterReceiptCannotCancelAppDispatchAndPreferenceChangeAffectsOnlyNextRequest()
    {
        using var fixture = new Fixture(); fixture.Engine.Block = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = fixture.Service(); using var client = new CancellationTokenSource(); var request = fixture.Request();
        var receipt = await service.Intake.AcceptAsync(request, client.Token); client.Cancel();
        Assert.Equal(ResultCode.Accepted, receipt.Code); await fixture.Engine.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var folder = Path.Combine(fixture.Root, "new-output"); await fixture.Preferences.SaveAsync(new(DefaultFolder: folder), default);
        var second = await service.Intake.AcceptAsync(fixture.Request("second.zip")); fixture.Engine.Block.SetResult(); await service.Intake.DrainAsync();
        Assert.False(fixture.Engine.Cancelled); Assert.Equal(2, fixture.Engine.AddCalls);
        Assert.StartsWith(fixture.Output, (await service.Repository.GetAsync(receipt.JobId!.Value, default))!.Job.Destination);
        Assert.StartsWith(folder, (await service.Repository.GetAsync(second.JobId!.Value, default))!.Job.Destination);
    }
    [Fact]
    public async Task PreferenceFailureNeverAcceptsAndExistingReceiptRemainsQueryable()
    {
        using var fixture = new Fixture(); var service = fixture.Service(); var request = fixture.Request();
        var receipt = await service.Intake.AcceptAsync(request); await service.Intake.DrainAsync();
        File.WriteAllText(fixture.PreferencesPath, "broken");
        var rejected = fixture.Request("new.zip");
        Assert.Equal(RejectionReason.NotReady, (await service.Intake.AcceptAsync(rejected)).Reason);
        await fixture.Preferences.SaveAsync(new(DefaultFolder: fixture.Output), default);
        Assert.Equal(RejectionReason.NotReady, (await service.Intake.AcceptAsync(rejected)).Reason);
        Assert.Equal(ResultCode.Rejected, (await service.Intake.GetStatusAsync(rejected.Key)).Code);
        Assert.Equal(receipt.JobId, (await service.Intake.AcceptAsync(request)).JobId); using var db = fixture.Db(); Assert.Single(db.Jobs);
    }
    [Theory]
    [InlineData("http://127.0.0.1/file")][InlineData("http://0x7f000001/file")][InlineData("http://10.0.0.1/file")]
    [InlineData("http://172.16.1.2/file")][InlineData("http://192.168.1.2/file")][InlineData("http://169.254.1.2/file")]
    [InlineData("http://[::1]/file")][InlineData("http://[::ffff:192.168.1.2]/file")][InlineData("http://[fe80::1]/file")]
    [InlineData("http://[fd00::1]/file")][InlineData("http://[::]/file")][InlineData("http://localhost/file")]
    public async Task LiteralAndLoopbackPolicyRejectsWithoutEngineMutation(string url)
    {
        using var fixture = new Fixture(); var service = fixture.Service();
        var result = await service.Intake.AcceptAsync(fixture.Request() with { SourceUrl = url });
        Assert.Equal(ResultCode.Rejected, result.Code); Assert.Equal(RejectionReason.UnsupportedUrl, result.Reason);
        using var db = fixture.Db(); Assert.Empty(db.Jobs); Assert.Equal(0, fixture.Engine.AddCalls);
    }
    [Fact]
    public async Task D07DeletionCreatesRetainedTombstoneAndPrunedStaleMutationDoesNotRecreate()
    {
        using var fixture = new Fixture(); var service = fixture.Service(); var request = fixture.Request();
        var accepted = await service.Intake.AcceptAsync(request); await service.Intake.DrainAsync();
        fixture.Engine.Downloads.Clear();
        using (var db = fixture.Db()) { db.Attempts.ExecuteUpdate(setters => setters.SetProperty(row => row.Status, DownloadStatus.Completed)); }
        await service.Coordinator.DeleteTerminalHistoryAsync(accepted.JobId!.Value);
        await service.Coordinator.CheckpointAsync(); // Deleted history must not remain in the live store.
        Assert.Equal(ResultCode.Expired, (await service.Intake.AcceptAsync(request)).Code); Assert.Equal(ResultCode.Expired, (await service.Intake.GetStatusAsync(request.Key)).Code);
        fixture.Clock.Now = fixture.Clock.Now.AddDays(90); await service.Repository.PruneExternalRecordsAsync(fixture.Clock.Now, default);
        using (var db = fixture.Db()) Assert.Single(db.ExternalRequests);
        fixture.Clock.Now = fixture.Clock.Now.AddDays(1); await service.Repository.PruneExternalRecordsAsync(fixture.Clock.Now, default);
        using (var db = fixture.Db()) Assert.Empty(db.ExternalRequests);
        Assert.Equal(RejectionReason.InvalidRequest, (await service.Intake.AcceptAsync(request)).Reason);
        using (var db = fixture.Db()) Assert.Empty(db.Jobs); Assert.Equal(1, fixture.Engine.AddCalls);
    }
    [Fact]
    public async Task D06PreviousSchemaUpgradePreservesPendingManualJobAndCreatesVerifiedBackup()
    {
        using var fixture = new Fixture(); using var old = fixture.Db(); old.GetService<IMigrator>().Migrate("20261006050356_RecoveryIntent");
        var job = Guid.NewGuid(); var attempt = Guid.NewGuid(); var ticks = fixture.Clock.Now.UtcTicks;
        old.Database.ExecuteSqlInterpolated($"INSERT INTO Jobs VALUES ({job}, {"manual"}, {"https://example.invalid/manual"}, {Path.Combine(fixture.Output, "manual.bin")}, {"M"}, {ticks});");
        old.Database.ExecuteSqlInterpolated($"INSERT INTO Attempts (Id,JobId,EngineId,StartedAtUtcTicks,Status,DownloadedBytes,UpdatedAtUtcTicks,DesiredPaused,DesiredRemoved) VALUES ({attempt},{job},{'a'.ToString().PadLeft(16,'a')},{ticks},{(int)DownloadStatus.Queued},{12L},{ticks},{false},{false});");
        old.Database.ExecuteSqlInterpolated($"INSERT INTO Files VALUES ({Guid.NewGuid()},{job},{attempt},{"manual.bin"},{100L});");
        old.Database.ExecuteSqlInterpolated($"INSERT INTO Operations (Id,JobId,AttemptId,Action,Status,RequestedAtUtcTicks) VALUES ({Guid.NewGuid()},{job},{attempt},{(int)DownloadAction.Add},{(int)OperationStatus.Uncertain},{ticks});");
        var repository = fixture.Service().Repository; await repository.InitializeAsync(default);
        var saved = Assert.Single(await repository.LoadAsync(default)); Assert.Equal(job, saved.Job.Id); Assert.Equal(OperationStatus.Uncertain, saved.PendingOperation!.Status); Assert.Equal(12, saved.Progress.DownloadedBytes);
        Assert.Single(Directory.GetFiles(Path.Combine(fixture.Root, "Backups"), "*.db")); using var current = fixture.Db(); Assert.False(current.Database.HasPendingModelChanges()); Assert.Empty(current.ExternalRequests);
    }
    private static Guid SeedNeverSent(Fixture fixture, ExternalDownloadRequest request)
    {
        // Mimic crash after complete durable acceptance, before the persisted dispatch claim.
        var id = Guid.NewGuid(); var attempt = Guid.NewGuid(); using var db = fixture.Db(); var now = fixture.Clock.Now.UtcTicks;
        db.Jobs.Add(new() { Id = id, DisplayName = "fixture.zip", Source = request.SourceUrl, Destination = Path.Combine(fixture.Output,"fixture.zip"), DestinationKey = Path.Combine(fixture.Output,"fixture.zip").ToUpperInvariant(), CreatedAtUtcTicks = now });
        db.Attempts.Add(new() { Id = attempt, JobId = id, EngineId = Guid.NewGuid().ToString("N")[..16], StartedAtUtcTicks = now, UpdatedAtUtcTicks = now, Status = DownloadStatus.Queued });
        db.Files.Add(new() { Id = Guid.NewGuid(), JobId = id, AttemptId = attempt, FileName = "fixture.zip" });
        db.Operations.Add(new() { Id = Guid.NewGuid(), JobId = id, AttemptId = attempt, Action = DownloadAction.Add, Status = OperationStatus.Pending, RequestedAtUtcTicks = now });
        db.ExternalRequests.Add(new() { CallerKey = request.Key.CallerKey, InstallationId = request.Key.InstallationId, RequestId = request.Key.RequestId,
            PayloadDigest = "synthetic", Code = ResultCode.Accepted, JobId = id, CreatedAtUtcTicks = now, UpdatedAtUtcTicks = now });
        db.SaveChanges(); return id;
    }
    private sealed class FixedClock : TimeProvider { internal DateTimeOffset Now = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Engine : IAria2Client
    {
        internal readonly ConcurrentDictionary<string, EngineDownload> Downloads = new(); private int _adds; internal int AddCalls => _adds;
        internal readonly ConcurrentQueue<EngineDownloadRequest> Requests = new();
        internal bool LoseReply, Cancelled; internal TaskCompletionSource? Block; internal TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<EngineDownload> AddAsync(EngineDownloadRequest request, CancellationToken token)
        {
            Interlocked.Increment(ref _adds); Requests.Enqueue(request); Entered.TrySetResult();
            try { if (Block is not null) await Block.Task.WaitAsync(token); }
            catch (OperationCanceledException) { Cancelled = true; throw; }
            var result = new EngineDownload(request.EngineId, new(DownloadStatus.Queued,0,null,0)); Downloads[request.EngineId] = result;
            if (LoseReply) throw new EngineException(EngineFailure.RpcUnavailable, "Synthetic lost reply."); return result;
        }
        public Task<IReadOnlyList<EngineDownload>> GetDownloadsAsync(IReadOnlyList<string> ids, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<EngineDownload>>(Downloads.Values.Where(row => ids.Contains(row.EngineId)).ToArray());
    }
    private sealed record Services(ExternalDownloadIntakeService Intake, DownloadCoordinator Coordinator, SqliteDownloadRepository Repository);
    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "AriaGUI.ExternalTests", Guid.NewGuid().ToString("N"));
        internal string Output => Path.Combine(Root, "output"); internal string PreferencesPath => Path.Combine(Root, "preferences.json");
        internal readonly Engine Engine = new(); internal readonly FixedClock Clock = new(); internal JsonUserPreferencesRepository Preferences => new(PreferencesPath);
        internal Fixture() { Directory.CreateDirectory(Root); File.WriteAllText(PreferencesPath, JsonSerializer.Serialize(new UserPreferences(DefaultFolder: Output))); }
        internal ExternalDownloadRequest Request(string name = "fixture.zip") => new(new("synthetic-extension", Guid.Parse("7df3ed34-ea26-450b-a428-90ce4c9589ec"), Guid.NewGuid()), Clock.Now, "https://example.invalid/file", name);
        internal Services Service(CancellationToken lifetime = default, ExternalIntakePersistenceHooks? hooks = null, IExternalDispatchReadiness? readiness = null)
        {
            var repository = new SqliteDownloadRepository(Path.Combine(Root, "downloads.db"), hooks); var coordinator = new DownloadCoordinator(Engine, new(), Clock, repository, new FileDownloadDestination());
            return new(new(coordinator, new(Preferences, Output), Clock, lifetime, readiness ?? new Ready()), coordinator, repository);
        }
        internal DownloadDbContext Db() => new(new DbContextOptionsBuilder<DownloadDbContext>().UseSqlite($"Data Source={Path.Combine(Root,"downloads.db")};Pooling=False").Options);
        public void Dispose() { var prefix = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "AriaGUI.ExternalTests")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(Root).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException(); Directory.Delete(Root, true); }
    }
    private sealed class Ready : IExternalDispatchReadiness { public Task WaitForReadyAsync(CancellationToken token) => Task.CompletedTask; }
    private sealed class BrokenReadiness : IExternalDispatchReadiness
    { public Task WaitForReadyAsync(CancellationToken token) => throw new InvalidOperationException("Synthetic readiness fault."); }
    private sealed class ReadinessGate : IExternalDispatchReadiness
    {
        internal TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task WaitForReadyAsync(CancellationToken token) { Waiting.TrySetResult(); await Ready.Task.WaitAsync(token); }
    }
    private sealed class StartingEngine : IManagedAria2Engine
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public event EventHandler? UnexpectedExit { add { } remove { } }
        public async Task<EngineInfo> StartAsync(CancellationToken token) { Started.SetResult(); await Ready.Task.WaitAsync(token); return new("synthetic", "synthetic"); }
        public Task<EngineInfo> CheckHealthAsync(CancellationToken token) => Task.FromResult(new EngineInfo("synthetic", "synthetic"));
        public Task StopAsync(CancellationToken token) => Task.CompletedTask;
    }
}
