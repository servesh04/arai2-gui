using AriaGui.Application.Downloads;
using AriaGui.Core.Downloads;
using AriaGui.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AriaGui.Browser.Spikes;

// An experimental table in a disposable database only; not a production migration.
internal static class ReceiptSpike
{
    private static DownloadDbContext Context(string path) => new(new DbContextOptionsBuilder<DownloadDbContext>()
        .UseSqlite(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, DefaultTimeout = 5 }.ToString()).Options);
    private static Guid? Lookup(DownloadDbContext db, string request, string digest)
    {
        using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT JobId, Digest FROM SpikeReceipts WHERE Caller='synthetic-extension' AND Installation='synthetic-profile' AND Request=$request";
        var parameter = command.CreateParameter(); parameter.ParameterName = "$request"; parameter.Value = request; command.Parameters.Add(parameter);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        if (reader.GetString(1) != digest) throw new InvalidOperationException("request_id_conflict");
        return Guid.Parse(reader.GetString(0));
    }
    private static Guid Insert(string path, string request, string digest, string destination, bool crashBeforeCommit = false)
    {
        using var db = Context(path); db.Database.OpenConnection();
        if (Lookup(db, request, digest) is { } existing) return existing;
        var job = Guid.NewGuid(); var attempt = Guid.NewGuid(); var time = DateTimeOffset.UtcNow.UtcTicks;
        try
        {
            using var transaction = db.Database.BeginTransaction();
            db.Database.ExecuteSqlInterpolated($"INSERT INTO SpikeReceipts VALUES ('synthetic-extension','synthetic-profile',{request},{digest},{job.ToString("D")},'accepted')");
            db.Jobs.Add(new()
            {
                Id = job,
                DisplayName = "synthetic.bin",
                Source = "https://example.invalid/synthetic.bin",
                Destination = destination,
                DestinationKey = destination.ToUpperInvariant(),
                CreatedAtUtcTicks = time
            });
            db.Attempts.Add(new()
            {
                Id = attempt,
                JobId = job,
                EngineId = Guid.NewGuid().ToString("N")[..16],
                StartedAtUtcTicks = time,
                UpdatedAtUtcTicks = time,
                Status = DownloadStatus.Queued
            });
            db.Files.Add(new() { Id = Guid.NewGuid(), JobId = job, AttemptId = attempt, FileName = "synthetic.bin" });
            db.Operations.Add(new()
            {
                Id = Guid.NewGuid(),
                JobId = job,
                AttemptId = attempt,
                Action = DownloadAction.Add,
                Status = OperationStatus.Pending,
                RequestedAtUtcTicks = time
            });
            db.SaveChanges();
            if (crashBeforeCommit) throw new IOException("Injected precommit failure.");
            transaction.Commit(); return job;
        }
        catch (Exception ex) when (ex is SqliteException { SqliteErrorCode: 19 } || ex is DbUpdateException { InnerException: SqliteException { SqliteErrorCode: 19 } })
        {
            using var readback = Context(path); readback.Database.OpenConnection();
            return Lookup(readback, request, digest) ?? throw new InvalidOperationException("destination_conflict");
        }
    }
    internal static async Task<object> ProbeAsync(string root)
    {
        var path = Path.Combine(root, "receipt-spike", "downloads.db");
        var repository = new SqliteDownloadRepository(path); await repository.InitializeAsync(default);
        using (var db = Context(path)) db.Database.ExecuteSqlRaw("CREATE TABLE SpikeReceipts(Caller TEXT NOT NULL, Installation TEXT NOT NULL, Request TEXT NOT NULL, Digest TEXT NOT NULL, JobId TEXT NOT NULL, State TEXT NOT NULL, PRIMARY KEY(Caller,Installation,Request));");
        var destination = Path.Combine(root, "synthetic.bin");
        try { Insert(path, "rollback", "digest-A", destination, true); throw new InvalidOperationException("Failure injection not reached"); }
        catch (IOException) { }
        Program.Check((await repository.LoadAsync(default)).Count == 0, "No job after rollback");
        using (var db = Context(path))
        {
            db.Database.OpenConnection(); Program.Check(Lookup(db, "rollback", "digest-A") is null, "No accepted receipt after rollback");
        }
        var receipts = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => Insert(path, "duplicate", "digest-A", destination))));
        Program.Check(receipts.Distinct().Count() == 1 && (await repository.LoadAsync(default)).Count == 1, "Concurrent unique constraint convergence");
        Program.Check(Insert(path, "duplicate", "digest-A", destination) == receipts[0], "Lost reply restart lookup");
        var conflict = false;
        try { Insert(path, "duplicate", "changed-payload", destination); }
        catch (InvalidOperationException ex) when (ex.Message == "request_id_conflict") { conflict = true; }
        Program.Check(conflict, "Digest conflict");
        var collision = false;
        try { Insert(path, "other-request", "digest-B", destination); }
        catch (InvalidOperationException ex) when (ex.Message == "destination_conflict") { collision = true; }
        Program.Check(collision && (await repository.LoadAsync(default)).Count == 1, "Destination uniqueness");
        using (var db = Context(path))
        {
            db.Database.OpenConnection(); Program.Check(Lookup(db, "other-request", "digest-B") is null, "Collision cannot leave accepted receipt");
        }
        // The actual existing coordinator sees committed-but-unsent intent as missing/uncertain.
        var engine = new EmptyEngine(); var store = new DownloadRuntimeStore();
        var coordinator = new DownloadCoordinator(engine, store, TimeProvider.System, repository, new NeverDestination());
        await coordinator.RefreshAsync();
        var row = store.Current.Downloads.Single();
        Program.Check(row.Job.Id == receipts[0] && row.Progress.Status == DownloadStatus.Interrupted
            && row.PendingOperation?.Status == OperationStatus.Uncertain && engine.AddCount == 0, "No blind replay after restart");
        return new
        {
            checks = 8,
            concurrentRequests = 4,
            durableJobs = 1,
            engineAdds = engine.AddCount,
            acceptedButUnsent = "interrupted/uncertain; explicit recovery required",
            productionSchemaChanged = false
        };
    }
    private sealed class NeverDestination : IDownloadDestination
    {
        public Task<DownloadTarget> ValidateAsync(Uri source, string directory, string? filename, CancellationToken token) => throw new NotSupportedException();
    }
    private sealed class EmptyEngine : IAria2Client
    {
        internal int AddCount;
        public Task<EngineDownload> AddAsync(EngineDownloadRequest request, CancellationToken token) { AddCount++; throw new NotSupportedException(); }
        public Task<IReadOnlyList<EngineDownload>> GetDownloadsAsync(IReadOnlyList<string> ids, CancellationToken token) => Task.FromResult<IReadOnlyList<EngineDownload>>([]);
        public Task<EngineObservation> ObserveAsync(IReadOnlyList<string> ids, CancellationToken token) => Task.FromResult(new EngineObservation("spike-restarted-engine", [], 3));
    }
}
