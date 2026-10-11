using AriaGui.Core.Downloads;
using AriaGui.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

if (args.Length != 1) throw new ArgumentException("Supply a new, empty synthetic profile directory.");
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
await db.SaveChangesAsync();
Console.WriteLine($"Seeded 500 synthetic completed records in {root}");
