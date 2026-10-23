using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using AriaGui.Application.Downloads;
using AriaGui.Application.Engine;
using AriaGui.Core.Downloads;
using AriaGui.Infrastructure.Aria2;
using AriaGui.Infrastructure.Downloads;
using AriaGui.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AriaGui.Infrastructure.Tests;

[Collection("Engine processes")]
public sealed class RealDownloadTests
{
    [Fact]
    public async Task Published_verified_receipt_converges_after_sqlite_completion_commit_is_lost()
    {
        using var runtime = new DownloadTestRuntime();
        await using var server = new LocalHttpServer();
        var repository = runtime.Repository();
        await using var engine = new Aria2ProcessManager(runtime.Options with { UseContinuityGateway = true }, repository);
        await engine.StartAsync(CancellationToken.None);
        var store = new DownloadRuntimeStore();
        var faulting = new FaultingCompletionRepository(repository);
        var coordinator = new DownloadCoordinator(new ManagedAria2Client(engine), store, TimeProvider.System, faulting, new FileDownloadDestination());
        await coordinator.AddAsync(server.Url, directory: runtime.Output, fileName: "sqlite-gap.bin",
            expectedSha256: Convert.ToHexString(SHA256.HashData(server.Payload)));
        var deadline = Stopwatch.StartNew();
        while (!faulting.Failed && deadline.Elapsed < TimeSpan.FromSeconds(12))
        {
            try { await coordinator.RefreshAsync(); }
            catch (IOException) when (faulting.Failed) { }
            if (!faulting.Failed) await Task.Delay(40);
        }
        Assert.True(faulting.Failed, "Completion commit boundary was not reached.");
        var stale = Assert.Single(await repository.LoadAsync(default));
        Assert.NotEqual(DownloadStatus.Completed, stale.Progress.Status);
        Assert.Equal(server.Payload, await File.ReadAllBytesAsync(stale.Job.Destination));
        await engine.StopAsync(CancellationToken.None);
        await engine.StartAsync(CancellationToken.None);
        var reopenedStore = new DownloadRuntimeStore();
        var reopened = runtime.Coordinator(new ManagedAria2Client(engine), reopenedStore);
        await reopened.InitializeAsync(); await reopened.RefreshAsync();
        var completed = Assert.Single(reopenedStore.Current.Downloads);
        Assert.Equal(DownloadStatus.Completed, completed.Progress.Status);
        Assert.Equal(VerificationStatus.Verified, completed.Verification);
        Assert.Equal(completed.Attempt.Id, reopenedStore.Current.LatestCompletion?.AttemptId);
        Assert.Equal(DownloadStatus.Completed, Assert.Single(await repository.LoadAsync(default)).Progress.Status);
    }
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Required_checksum_intake_is_rejected_without_guarded_enabled_verification(bool guarded, bool enabled)
    {
        using var runtime = new DownloadTestRuntime();
        await using var server = new LocalHttpServer();
        await using var engine = new Aria2ProcessManager(runtime.Options with
        { UseContinuityGateway = guarded, EnableExpectedChecksumVerification = enabled }, runtime.Repository());
        await engine.StartAsync(CancellationToken.None);
        var coordinator = runtime.Coordinator(new ManagedAria2Client(engine), new DownloadRuntimeStore());
        await Assert.ThrowsAsync<DownloadRequestException>(() => coordinator.AddAsync(server.Url, directory: runtime.Output,
            fileName: "blocked.bin", expectedSha256: new string('0', 64)));
        Assert.False(File.Exists(Path.Combine(runtime.Output, "blocked.bin")));
        Assert.Empty(await runtime.Repository().LoadAsync(default));
    }
    [Fact]
    public async Task Checksum_mismatch_repair_uses_fresh_attempt_and_keeps_failed_stage()
    {
        using var runtime = new DownloadTestRuntime();
        await using var server = new LocalHttpServer { RecoverPartial = true, ChangeEntity = true };
        await using var engine = new Aria2ProcessManager(runtime.Options with { UseContinuityGateway = true }, runtime.Repository());
        await engine.StartAsync(CancellationToken.None);
        var store = new DownloadRuntimeStore(); var coordinator = runtime.Coordinator(new ManagedAria2Client(engine), store);
        var expected = Convert.ToHexString(SHA256.HashData(server.Payload));
        await coordinator.AddAsync(server.Url, directory: runtime.Output, fileName: "repair.bin", expectedSha256: expected);
        var failed = await WaitForTerminalAsync(coordinator, store);
        Assert.Equal(VerificationStatus.Mismatch, failed.Verification);
        var oldStage = Path.Combine(runtime.Output, ".aria-gui-continuity-" + failed.Job.Id.ToString("N"),
            failed.Attempt.Id.ToString("N"), "repair.bin");
        var oldBytes = await File.ReadAllBytesAsync(oldStage);
        Assert.NotEqual(server.Payload, oldBytes);
        server.ChangeEntity = false;
        await coordinator.RetryAsync(failed.Job.Id);
        var repaired = await WaitForTerminalAsync(coordinator, store);
        Assert.True(repaired.Progress.Status == DownloadStatus.Completed, repaired.ErrorMessage);
        Assert.Equal(VerificationStatus.Verified, repaired.Verification);
        Assert.Equal(failed.Attempt.Id, repaired.Attempt.ParentAttemptId);
        Assert.Equal(expected, repaired.Job.ExpectedChecksum?.Sha256);
        Assert.Equal(oldBytes, await File.ReadAllBytesAsync(oldStage));
        Assert.Equal(server.Payload, await File.ReadAllBytesAsync(repaired.Job.Destination));
    }
    [Fact]
    public async Task Checksum_repair_collision_keeps_existing_public_payload_and_control()
    {
        using var runtime = new DownloadTestRuntime();
        await using var server = new LocalHttpServer { RecoverPartial = true, ChangeEntity = true };
        await using var engine = new Aria2ProcessManager(runtime.Options with { UseContinuityGateway = true }, runtime.Repository());
        await engine.StartAsync(CancellationToken.None);
        var store = new DownloadRuntimeStore(); var coordinator = runtime.Coordinator(new ManagedAria2Client(engine), store);
        await coordinator.AddAsync(server.Url, directory: runtime.Output, fileName: "collision.bin",
            expectedSha256: Convert.ToHexString(SHA256.HashData(server.Payload)));
        var failed = await WaitForTerminalAsync(coordinator, store);
        Assert.Equal(VerificationStatus.Mismatch, failed.Verification);
        var stage = Path.Combine(runtime.Output, ".aria-gui-continuity-" + failed.Job.Id.ToString("N"),
            failed.Attempt.Id.ToString("N"), "collision.bin");
        var stageBytes = await File.ReadAllBytesAsync(stage);
        var originalPayload = new byte[] { 42, 43, 44, 45 };
        var originalControl = new byte[] { 6, 7, 8, 9 };
        await File.WriteAllBytesAsync(failed.Job.Destination, originalPayload);
        await File.WriteAllBytesAsync(failed.Job.Destination + ".aria2", originalControl);
        server.ChangeEntity = false;
        await coordinator.RetryAsync(failed.Job.Id);
        var retried = await WaitForTerminalAsync(coordinator, store);
        Assert.Equal(DownloadStatus.Failed, retried.Progress.Status);
        Assert.Equal(failed.Attempt.Id, retried.Attempt.ParentAttemptId);
        Assert.Equal(originalPayload, await File.ReadAllBytesAsync(failed.Job.Destination));
        Assert.Equal(originalControl, await File.ReadAllBytesAsync(failed.Job.Destination + ".aria2"));
        Assert.Equal(stageBytes, await File.ReadAllBytesAsync(stage));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pinned_engine_synthetic_file_verifies_exact_sha256_or_rejects_changed_digest(bool mismatch)
    {
        var fixture = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "ChecksumFixture", "m2-synthetic.bin"));
        Assert.Equal(131072, fixture.Length);
        const string knownDigest = "821BA32C43E025C60AC06EB3BA0F9E716D4857D10065AB3A87F952E2881C244D";
        Assert.Equal(knownDigest, Convert.ToHexString(SHA256.HashData(fixture)));
        var suppliedDigest = mismatch ? "0" + knownDigest[1..] : knownDigest;

        using var runtime = new DownloadTestRuntime();
        await using var server = new LocalHttpServer(fixture.Length);
        fixture.CopyTo(server.Payload, 0);
        await using var engine = new Aria2ProcessManager(runtime.Options with { UseContinuityGateway = true }, runtime.Repository());
        await engine.StartAsync(CancellationToken.None);
        var store = new DownloadRuntimeStore();
        var coordinator = runtime.Coordinator(new ManagedAria2Client(engine), store);
        await coordinator.AddAsync(server.Url, directory: runtime.Output, fileName: "m2-synthetic.bin", expectedSha256: suppliedDigest);

        var terminal = await WaitForTerminalAsync(coordinator, store);
        Assert.Equal(mismatch ? DownloadStatus.Failed : DownloadStatus.Completed, terminal.Progress.Status);
        Assert.Equal(mismatch ? VerificationStatus.Mismatch : VerificationStatus.Verified, terminal.Verification);
        Assert.Equal(suppliedDigest, terminal.Job.ExpectedChecksum?.Sha256);
        Assert.Equal(!mismatch, File.Exists(terminal.Job.Destination));
        if (mismatch)
        {
            var stage = Path.Combine(runtime.Output, ".aria-gui-continuity-" + terminal.Job.Id.ToString("N"),
                terminal.Attempt.Id.ToString("N"), "m2-synthetic.bin");
            Assert.Equal(fixture, await File.ReadAllBytesAsync(stage));
        }
        else
        {
            var downloaded = await File.ReadAllBytesAsync(terminal.Job.Destination);
            Assert.Equal(fixture, downloaded);
            Assert.Equal(knownDigest, Convert.ToHexString(SHA256.HashData(downloaded)));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pinned_engine_required_sha256_verifies_before_publication_or_retains_mismatch(bool mismatch)
    {
        using var runtime = new DownloadTestRuntime();
        await using var server = new LocalHttpServer();
        await using var engine = new Aria2ProcessManager(runtime.Options with { UseContinuityGateway = true }, runtime.Repository());
        await engine.StartAsync(CancellationToken.None);
        var store = new DownloadRuntimeStore(); var coordinator = runtime.Coordinator(new ManagedAria2Client(engine), store);
        var digest = Convert.ToHexString(SHA256.HashData(server.Payload));
        await coordinator.AddAsync(server.Url, directory: runtime.Output, fileName: "required.bin",
            expectedSha256: mismatch ? new string('0', 64) : digest);
        var terminal = await WaitForTerminalAsync(coordinator, store);
        Assert.Equal(mismatch ? DownloadStatus.Failed : DownloadStatus.Completed, terminal.Progress.Status);
        Assert.Equal(mismatch ? VerificationStatus.Mismatch : VerificationStatus.Verified, terminal.Verification);
        Assert.Equal(mismatch ? new string('0', 64) : digest, terminal.Job.ExpectedChecksum?.Sha256);
        Assert.Equal(ChecksumProvenance.UserSupplied, terminal.Job.ExpectedChecksum?.Provenance);
        Assert.Equal(!mismatch, File.Exists(terminal.Job.Destination));
        if (!mismatch) Assert.Equal(server.Payload, await File.ReadAllBytesAsync(terminal.Job.Destination));
        var saved = Assert.Single(await runtime.Repository().LoadAsync(default));
        Assert.Equal(terminal.Verification, saved.Verification);
        await engine.StopAsync(CancellationToken.None);
        await engine.StartAsync(CancellationToken.None);
        var reopenedStore = new DownloadRuntimeStore();
        var reopened = runtime.Coordinator(new ManagedAria2Client(engine), reopenedStore);
        await reopened.InitializeAsync(); await reopened.RefreshAsync();
        var afterRestart = Assert.Single(reopenedStore.Current.Downloads);
        Assert.Equal(terminal.Verification, afterRestart.Verification);
        Assert.Equal(terminal.Progress.Status, afterRestart.Progress.Status);
    }
    [Theory]
    [InlineData(false, 0, false)]
    [InlineData(true, 0, false)]
    [InlineData(false, 90, false)]
    [InlineData(true, 90, false)]
    [InlineData(true, 0, true)]
    [InlineData(true, 90, true)]
    public async Task Ordinary_long_filename_survives_private_stage_overhead(bool continuity, int depth, bool retry)
    {
        using var runtime = new DownloadTestRuntime();
        await using var server = new LocalHttpServer();
        var options = runtime.Options with { UseContinuityGateway = continuity };
        await using var engine = new Aria2ProcessManager(options, runtime.Repository());
        await engine.StartAsync(CancellationToken.None);
        var store = new DownloadRuntimeStore();
        var coordinator = runtime.Coordinator(new ManagedAria2Client(engine), store);
        var destination = depth == 0 ? runtime.Output : Path.Combine(runtime.Output, new string('d', depth));
        var source = retry ? new Uri(new Uri(server.Url), "partial").AbsoluteUri : server.Url;
        await coordinator.AddAsync(source, directory: destination, fileName: new string('a', depth == 0 ? 120 : 20) + ".bin");
        if (retry)
        {
            var failed = await WaitForTerminalAsync(coordinator, store);
            Assert.Equal(DownloadStatus.Failed, failed.Progress.Status);
            await engine.StopAsync(CancellationToken.None);
            await engine.StartAsync(CancellationToken.None); // Reload private name/ancestor ownership before reuse.
            server.RecoverPartial = true;
            await coordinator.RetryAsync(failed.Job.Id);
        }
        var completed = await WaitForTerminalAsync(coordinator, store);
        Assert.True(completed.Progress.Status == DownloadStatus.Completed, completed.ErrorMessage);
        Assert.Equal(server.Payload, await File.ReadAllBytesAsync(completed.Job.Destination));
        if (retry) Assert.Contains(server.Observations, row => row.RequestedOffset > 0 && row.Status == 206);
    }

    [Fact]
    public async Task Downloaded_bytes_and_completed_history_survive_a_fresh_engine_and_repository()
    {
        using var runtime = new DownloadTestRuntime();
        await using var server = new LocalHttpServer();
        Guid jobId;
        await using (var engine = new Aria2ProcessManager(runtime.Options))
        {
            await engine.StartAsync(CancellationToken.None);
            var store = new DownloadRuntimeStore();
            var coordinator = runtime.Coordinator(new ManagedAria2Client(engine), store);
            await coordinator.AddAsync(server.Url, directory: runtime.Output, fileName: "verified.bin");
            var completed = await WaitForTerminalAsync(coordinator, store);
            Assert.Equal(DownloadStatus.Completed, completed.Progress.Status);
            Assert.Equal(server.Payload.Length, completed.Progress.TotalBytes);
            Assert.Equal(SHA256.HashData(server.Payload), SHA256.HashData(await File.ReadAllBytesAsync(completed.Job.Destination)));
            jobId = completed.Job.Id;
            await engine.StopAsync(CancellationToken.None);
        }
        await using var restartedEngine = new Aria2ProcessManager(runtime.Options);
        await restartedEngine.StartAsync(CancellationToken.None);
        var restartedStore = new DownloadRuntimeStore();
        var reopened = runtime.Coordinator(new ManagedAria2Client(restartedEngine), restartedStore);
        await reopened.InitializeAsync();
        await reopened.RefreshAsync();
        var saved = Assert.Single(restartedStore.Current.Downloads);
        Assert.Equal(jobId, saved.Job.Id);
        Assert.Equal(DownloadStatus.Completed, saved.Progress.Status);
        Assert.Null(saved.PendingOperation);
        Assert.Equal(1, server.Requests);
        using var db = runtime.Db();
        Assert.Single(db.Jobs);
        Assert.Single(db.Attempts);
        Assert.Single(db.Files);
        Assert.Equal(OperationStatus.Confirmed, Assert.Single(db.Operations).Status);
        Assert.Equal(db.Database.GetMigrations().Count(), db.Database.GetAppliedMigrations().Count());
        Assert.False(db.Database.HasPendingModelChanges());
        db.Database.OpenConnection();
        using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT sqlite_version()";
        Assert.True(Version.Parse((string)command.ExecuteScalar()!) >= new Version(3, 50, 2));
    }

    [Fact]
    public async Task Lost_real_add_reply_reconciles_the_durable_gid_without_a_duplicate()
    {
        using var runtime = new DownloadTestRuntime();
        await using var server = new LocalHttpServer();
        await using var engine = new Aria2ProcessManager(runtime.Options);
        await engine.StartAsync(CancellationToken.None);
        var lossy = new LostReplyClient(new ManagedAria2Client(engine));
        var store = new DownloadRuntimeStore();
        var coordinator = runtime.Coordinator(lossy, store);
        await Assert.ThrowsAsync<EngineException>(() => coordinator.AddAsync(server.Url, directory: runtime.Output, fileName: "lost-reply.bin"));
        Assert.Equal(OperationStatus.Uncertain, Assert.Single((await runtime.Repository().LoadAsync(CancellationToken.None))).PendingOperation?.Status);
        // Recreate the app coordinator from the database while retaining the owned engine.
        var reopenedStore = new DownloadRuntimeStore();
        var reopened = runtime.Coordinator(lossy, reopenedStore);
        var entry = await WaitForTerminalAsync(reopened, reopenedStore);
        Assert.Equal(DownloadStatus.Completed, entry.Progress.Status);
        Assert.Null(entry.PendingOperation);
        Assert.Equal(1, lossy.AddCount);
        Assert.Equal(1, server.Requests);
    }

    [Theory]
    [InlineData("missing", DownloadStatus.Failed)]
    [InlineData("empty", DownloadStatus.Completed)]
    [InlineData("chunked", DownloadStatus.Completed)]
    public async Task Http_outcomes_and_unknown_totals_are_saved(string route, DownloadStatus expected)
    {
        using var runtime = new DownloadTestRuntime();
        await using var server = new LocalHttpServer();
        await using var engine = new Aria2ProcessManager(runtime.Options);
        await engine.StartAsync(CancellationToken.None);
        var store = new DownloadRuntimeStore();
        var coordinator = runtime.Coordinator(new ManagedAria2Client(engine), store);
        await coordinator.AddAsync(new Uri(new Uri(server.Url), route).AbsoluteUri, directory: runtime.Output, fileName: route + ".bin");
        var sawUnknownTotal = false;
        var entry = await WaitForTerminalAsync(coordinator, store, observation =>
        {
            if (observation.Progress.DownloadedBytes > 0 && observation.Progress.TotalBytes is null) sawUnknownTotal = true;
        });
        Assert.Equal(expected, entry.Progress.Status);
        var saved = Assert.Single(await runtime.Repository().LoadAsync(CancellationToken.None));
        Assert.Equal(expected, saved.Progress.Status);
        if (route == "missing") Assert.Equal("File not found", saved.ErrorMessage);
        if (route == "empty") { Assert.Equal(0, saved.Progress.TotalBytes); Assert.Equal(100, saved.Progress.Percentage); }
        if (route == "chunked")
        {
            Assert.True(sawUnknownTotal);
            Assert.Equal(server.Payload, await File.ReadAllBytesAsync(saved.Job.Destination));
        }
    }

    [Fact]
    public async Task Existing_file_is_preserved_and_no_intent_or_engine_mutation_is_created()
    {
        using var runtime = new DownloadTestRuntime();
        Directory.CreateDirectory(runtime.Output);
        var existing = Path.Combine(runtime.Output, "keep.bin");
        await File.WriteAllTextAsync(existing, "user-owned bytes");
        var client = new NoMutationClient();
        var coordinator = runtime.Coordinator(client, new());
        await Assert.ThrowsAsync<DownloadRequestException>(() => coordinator.AddAsync("https://example.com/file", directory: runtime.Output, fileName: "keep.bin"));
        Assert.Equal("user-owned bytes", await File.ReadAllTextAsync(existing));
        Assert.Equal(0, client.AddCount);
        Assert.Empty(await runtime.Repository().LoadAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("../escape.bin")]
    [InlineData("sub\\escape.bin")]
    [InlineData("CON.txt")]
    [InlineData("LPT1")]
    [InlineData("file:stream")]
    [InlineData("trailing.")]
    public async Task Unsafe_filenames_are_rejected_before_engine_or_history_mutations(string fileName)
    {
        using var runtime = new DownloadTestRuntime();
        var client = new NoMutationClient();
        await Assert.ThrowsAsync<ArgumentException>(() => runtime.Coordinator(client, new()).AddAsync(
            "https://example.com/file", directory: runtime.Output, fileName: fileName));
        Assert.Equal(0, client.AddCount);
        Assert.Empty(await runtime.Repository().LoadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Denied_destination_is_a_readable_error_and_no_engine_request_is_sent()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var runtime = new DownloadTestRuntime();
        var folder = Directory.CreateDirectory(runtime.Output);
        var original = folder.GetAccessControl();
        var denied = folder.GetAccessControl();
        denied.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.WriteData,
            InheritanceFlags.None, PropagationFlags.None, AccessControlType.Deny));
        folder.SetAccessControl(denied);
        try
        {
            var client = new NoMutationClient();
            var ex = await Assert.ThrowsAsync<DownloadRequestException>(() => runtime.Coordinator(client, new()).AddAsync(
                "https://example.com/file", directory: runtime.Output, fileName: "denied.bin"));
            Assert.Contains("not writable", ex.Message);
            Assert.Equal(0, client.AddCount);
        }
        finally { folder.SetAccessControl(original); }
    }

    [Fact]
    public async Task Corrupt_database_is_reported_and_never_deleted_or_reset()
    {
        using var runtime = new DownloadTestRuntime();
        Directory.CreateDirectory(Path.GetDirectoryName(runtime.Database)!);
        await File.WriteAllTextAsync(runtime.Database, "existing corrupt database");
        await Assert.ThrowsAsync<DownloadStorageException>(() => runtime.Repository().InitializeAsync(CancellationToken.None));
        Assert.Equal("existing corrupt database", await File.ReadAllTextAsync(runtime.Database));
    }

    [Fact]
    public async Task Duplicate_engine_id_rolls_back_the_entire_intent_transaction()
    {
        using var runtime = new DownloadTestRuntime();
        var repository = runtime.Repository();
        await repository.InitializeAsync(CancellationToken.None);
        DownloadEntry Intent(string name)
        {
            var now = DateTimeOffset.UtcNow;
            var job = Guid.NewGuid();
            var attempt = Guid.NewGuid();
            return new(new(job, name, new("https://example.com/file"), Path.Combine(runtime.Output, name), now),
                new(attempt, job, "1234567890abcdef", now), new(Guid.NewGuid(), job, attempt, name, null),
                new(DownloadStatus.Queued, 0, null, 0), new(Guid.NewGuid(), job, DownloadAction.Add, OperationStatus.Pending, now));
        }
        await repository.InsertIntentAsync(Intent("first.bin"), CancellationToken.None);
        await Assert.ThrowsAsync<DownloadStorageException>(() => repository.InsertIntentAsync(Intent("second.bin"), CancellationToken.None));
        using var db = runtime.Db();
        Assert.Single(db.Jobs);
        Assert.Single(db.Attempts);
        Assert.Single(db.Files);
        Assert.Single(db.Operations);
    }

    private static async Task<DownloadEntry> WaitForTerminalAsync(DownloadCoordinator coordinator, DownloadRuntimeStore store,
        Action<DownloadEntry>? observe = null)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(12))
        {
            await coordinator.RefreshAsync();
            if (store.Current.Downloads.SingleOrDefault() is { } current) observe?.Invoke(current);
            if (store.Current.Downloads.SingleOrDefault() is { Progress.Status: DownloadStatus.Completed or DownloadStatus.Failed } terminal) return terminal;
            await Task.Delay(40);
        }
        throw new TimeoutException("Controlled local download did not reach a terminal state.");
    }

    private sealed class LostReplyClient(IAria2Client inner) : IAria2Client
    {
        public int AddCount;
        public async Task<EngineDownload> AddAsync(EngineDownloadRequest request, CancellationToken token)
        {
            Interlocked.Increment(ref AddCount);
            await inner.AddAsync(request, token);
            throw new EngineException(EngineFailure.RpcUnavailable, "Injected lost Add reply.");
        }
        public Task<IReadOnlyList<EngineDownload>> GetDownloadsAsync(IReadOnlyList<string> ids, CancellationToken token) => inner.GetDownloadsAsync(ids, token);
    }
    private sealed class FaultingCompletionRepository(SqliteDownloadRepository inner) : IDownloadRepository
    {
        public bool Failed { get; private set; }
        public Task InitializeAsync(CancellationToken token) => inner.InitializeAsync(token);
        public Task<IReadOnlyList<DownloadEntry>> LoadAsync(CancellationToken token) => inner.LoadAsync(token);
        public Task<IReadOnlyList<DownloadEntry>> LoadCurrentAsync(CancellationToken token) => inner.LoadCurrentAsync(token);
        public Task InsertIntentAsync(DownloadEntry entry, CancellationToken token) => inner.InsertIntentAsync(entry, token);
        public Task SaveAsync(IReadOnlyList<DownloadEntry> entries, CancellationToken token) => inner.SaveAsync(entries, token);
        public Task<IReadOnlyList<Guid>> SaveAndReportCompletionsAsync(IReadOnlyList<DownloadEntry> entries, CancellationToken token)
        {
            if (!Failed && entries.Any(entry => entry.Progress.Status == DownloadStatus.Completed))
            { Failed = true; throw new IOException("Injected crash before SQLite completion commit."); }
            return inner.SaveAndReportCompletionsAsync(entries, token);
        }
    }
    private sealed class NoMutationClient : IAria2Client
    {
        public int AddCount;
        public Task<EngineDownload> AddAsync(EngineDownloadRequest request, CancellationToken token) { AddCount++; throw new InvalidOperationException("Unexpected Add."); }
        public Task<IReadOnlyList<EngineDownload>> GetDownloadsAsync(IReadOnlyList<string> ids, CancellationToken token) => Task.FromResult<IReadOnlyList<EngineDownload>>([]);
    }

    internal sealed class DownloadTestRuntime : IDisposable
    {
        private string Root { get; } = Path.Combine(Path.GetTempPath(), "AriaGUI.DownloadTests", Guid.NewGuid().ToString("N"));
        public string Database => Path.Combine(Root, "data", "downloads.db");
        public string Output => Path.Combine(Root, "downloads");
        public Aria2ProcessOptions Options { get; }
        public DownloadTestRuntime()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            Options = new(Path.Combine(AppContext.BaseDirectory, "Engine", "aria2c.exe"), Path.Combine(Root, "data")) { RpcPort = port };
        }
        public SqliteDownloadRepository Repository() => new(Database);
        public DownloadDbContext Db() => new(new DbContextOptionsBuilder<DownloadDbContext>().UseSqlite($"Data Source={Database};Pooling=False").Options);
        public DownloadCoordinator Coordinator(IAria2Client client, DownloadRuntimeStore store) => new(client, store, TimeProvider.System, Repository(), new FileDownloadDestination());
        public void Dispose()
        {
            var allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "AriaGUI.DownloadTests")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(Root).StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe test cleanup path.");
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
