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
        Assert.Equal(4, db.Database.GetAppliedMigrations().Count());
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
        if (route == "missing") Assert.Equal("The server did not find this file.", saved.ErrorMessage);
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
    private sealed class NoMutationClient : IAria2Client
    {
        public int AddCount;
        public Task<EngineDownload> AddAsync(EngineDownloadRequest request, CancellationToken token) { AddCount++; throw new InvalidOperationException("Unexpected Add."); }
        public Task<IReadOnlyList<EngineDownload>> GetDownloadsAsync(IReadOnlyList<string> ids, CancellationToken token) => Task.FromResult<IReadOnlyList<EngineDownload>>([]);
    }

    private sealed class DownloadTestRuntime : IDisposable
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
