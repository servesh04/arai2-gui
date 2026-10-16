using AriaGui.Core.Downloads;
using AriaGui.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using AriaGui.Application.Downloads;
using AriaGui.Application.Engine;
using AriaGui.Infrastructure.Aria2;
using System.Text.Json;

if (args.Length == 2 && args[1] == "--verify-guarded-pending")
{
    var profile = Path.GetFullPath(args[0]); _ = SyntheticEngineFixturePort.Read(profile);
    var identity = JsonDocument.Parse(File.ReadAllText(Path.Combine(profile, "phase-c-pending-identity.json"))).RootElement;
    var pendingRepository = new SqliteDownloadRepository(Path.Combine(profile, "downloads.db"));
    await pendingRepository.InitializeAsync(default);
    var matches = (await pendingRepository.LoadAsync(default)).Where(entry => entry.Attempt.EngineId == "abcdef1234567890").ToArray();
    if (matches.Length != 1 || matches[0].Job.Id != identity.GetProperty("JobId").GetGuid() ||
        matches[0].Attempt.Id != identity.GetProperty("AttemptId").GetGuid() || matches[0].Job.Destination != identity.GetProperty("Target").GetString() ||
        matches[0].Progress.Status != DownloadStatus.Paused || !matches[0].DesiredPaused || File.Exists(matches[0].Job.Destination))
        throw new InvalidOperationException("Synthetic pending identity/status/publication changed.");
    Console.WriteLine("One unchanged job/attempt/GID remains paused; no final payload published.");
    return;
}

if (args.Length == 2 && args[1] == "--prepare-guarded-pending")
{
    var profile = Path.GetFullPath(args[0]);
    var port = SyntheticEngineFixturePort.Read(profile);
    if (port == 16800) throw new InvalidOperationException("Private fixture port required.");
    var pendingRepository = new SqliteDownloadRepository(Path.Combine(profile, "downloads.db"));
    await pendingRepository.InitializeAsync(default);
    var pending = (await pendingRepository.LoadAsync(default)).Single(entry => entry.Attempt.EngineId == "abcdef1234567890");
    var options = Aria2ProcessOptions.ForApplication(Path.Combine(profile, "disabled-engine", "aria2c.exe"), profile) with { RpcPort = port };
    await using var manager = new Aria2ProcessManager(options, pendingRepository);
    await manager.StartAsync(default);
    var observed = await manager.AddDownloadAsync(new(pending.Attempt.EngineId, pending.Job.Source,
        Directory: Path.GetDirectoryName(pending.Job.Destination)!, FileName: pending.File.FileName,
        StartPaused: true, ContinuityJobId: pending.Job.Id, ContinuityAttemptId: pending.Attempt.Id, RequirePublicOrigin: false), default);
    if (observed.Progress.Status != DownloadStatus.Paused) throw new InvalidOperationException("Paused fixture dispatch failed.");
    await manager.StopAsync(default);
    File.WriteAllText(Path.Combine(profile, "phase-c-pending-identity.json"), JsonSerializer.Serialize(new
    { JobId = pending.Job.Id, AttemptId = pending.Attempt.Id, Gid = pending.Attempt.EngineId, Target = pending.Job.Destination, RpcPort = port }));
    Console.WriteLine("Prepared one guarded paused synthetic identity on a private RPC port; engine stopped.");
    return;
}

if (args.Length is < 1 or > 2 || args.Length == 2 && args[1] != "--paused-engine-fixture") throw new ArgumentException("Supply a new, empty synthetic profile directory and optional --paused-engine-fixture.");
var root = Path.GetFullPath(args[0]);
if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
    throw new InvalidOperationException("Refusing to overwrite any existing profile contents.");
Directory.CreateDirectory(root);
File.WriteAllText(Path.Combine(root, "synthetic-r1-fixture"), "Synthetic completed rows only; no engine executable.");
var database = Path.Combine(root, "downloads.db");
var repository = new SqliteDownloadRepository(database);
await repository.InitializeAsync(default);
using var db = new DownloadDbContext(new DbContextOptionsBuilder<DownloadDbContext>().UseSqlite($"Data Source={database};Pooling=False").Options);
for (var i = 0; i < 500; i++)
{
    var id = Guid.NewGuid(); var attempt = Guid.NewGuid(); var name = $"synthetic-{i:0000}-long-verification-name.bin";
    var time = DateTimeOffset.UtcNow.UtcTicks + i;
    var destination = Path.Combine(root, "never-created-files", name);
    db.Jobs.Add(new() { Id = id, DisplayName = name, Source = "https://example.invalid/" + name,
        Destination = destination, DestinationKey = destination.ToUpperInvariant(), CreatedAtUtcTicks = time });
    db.Attempts.Add(new() { Id = attempt, JobId = id, EngineId = (i + 1).ToString("x16"), StartedAtUtcTicks = time,
        UpdatedAtUtcTicks = time, Status = DownloadStatus.Completed, TotalBytes = i * 1024, DownloadedBytes = i * 1024 });
    db.Files.Add(new() { Id = Guid.NewGuid(), JobId = id, AttemptId = attempt, FileName = name, TotalBytes = i * 1024 });
}
if (args.Length == 2)
{
    const string gid = "abcdef1234567890";
    const string source = "http://127.0.0.1:9/phase-c-paused.bin";
    var id = Guid.NewGuid(); var attempt = Guid.NewGuid(); var time = DateTimeOffset.UtcNow.UtcTicks;
    var directory = Path.Combine(root, "paused-fixture-files"); Directory.CreateDirectory(directory);
    var destination = Path.Combine(directory, "phase-c-paused.bin");
    db.Jobs.Add(new() { Id = id, DisplayName = "phase-c-paused.bin", Source = source, Destination = destination,
        DestinationKey = destination.ToUpperInvariant(), CreatedAtUtcTicks = time });
    db.Attempts.Add(new() { Id = attempt, JobId = id, EngineId = gid, StartedAtUtcTicks = time, UpdatedAtUtcTicks = time,
        Status = DownloadStatus.Paused, DesiredPaused = true });
    db.Files.Add(new() { Id = Guid.NewGuid(), JobId = id, AttemptId = attempt, FileName = "phase-c-paused.bin" });
    var sessions = Path.Combine(root, "sessions"); Directory.CreateDirectory(sessions);
    File.WriteAllText(Path.Combine(sessions, "session-phase-c-fixture.txt"), source + "\n gid=" + gid + "\n pause=true\n");
}
await db.SaveChangesAsync();
Console.WriteLine($"Seeded 500 synthetic completed records in {root}");
