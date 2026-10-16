using AriaGui.Application.Downloads;
using AriaGui.Core.Downloads;
using AriaGui.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AriaGui.Infrastructure.Tests;

public sealed class UpgradeTests
{
    private const string Initial = "20261004141858_InitialDownloads";
    private const string Queue = "20261004151151_QueueCommands";

    [Theory]
    [InlineData(Initial)]
    [InlineData(Queue)]
    public async Task Old_schema_is_backed_up_with_WAL_data_before_upgrade_and_can_be_reopened(string migration)
    {
        using var runtime = new Runtime();
        using var old = runtime.Db();
        old.GetService<IMigrator>().Migrate(migration);
        old.Database.OpenConnection(); // Keep WAL alive while the online backup executes.
        old.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0;");
        var paused = Seed(old, runtime.Root, "paused", DownloadStatus.Paused);
        var resume = Seed(old, runtime.Root, "resume", DownloadStatus.Paused, DownloadAction.Resume);
        var pause = Seed(old, runtime.Root, "pause", DownloadStatus.Queued, DownloadAction.Pause);
        var removed = Seed(old, runtime.Root, "removed", DownloadStatus.Removed);
        if (migration == Queue) old.Database.ExecuteSqlRaw("INSERT INTO QueuePolicies (Id, \"Limit\", RequestedLimit, RequestStatus) VALUES (1, 4, 6, 2);");

        var repository = runtime.Repository(); await repository.InitializeAsync(default);
        var entries = await repository.LoadAsync(default);
        Assert.Equal(4, entries.Count);
        Assert.True(entries.Single(x => x.Job.Id == paused).DesiredPaused);
        Assert.False(entries.Single(x => x.Job.Id == resume).DesiredPaused);
        Assert.True(entries.Single(x => x.Job.Id == pause).DesiredPaused);
        Assert.True(entries.Single(x => x.Job.Id == removed).DesiredRemoved);
        Assert.Equal(12, entries.Single(x => x.Job.Id == pause).Progress.DownloadedBytes);
        var policy = await repository.LoadQueuePolicyAsync(default);
        Assert.Equal(migration == Queue ? 4 : 3, policy.Limit);
        if (migration == Queue) { Assert.Equal(6, policy.RequestedLimit); Assert.Equal(OperationStatus.Uncertain, policy.RequestStatus); }
        using var current = runtime.Db(); Assert.Equal(11, current.Database.GetAppliedMigrations().Count());
        Assert.False(current.Database.HasPendingModelChanges());

        var backup = Assert.Single(Directory.GetFiles(Path.Combine(runtime.Root, "Backups"), "*.db"));
        using var copy = runtime.Db(backup);
        Assert.Equal(migration == Queue ? 2 : 1, copy.Database.GetAppliedMigrations().Count());
        Assert.Equal(4, CountLegacyJobs(copy)); // Data committed to the WAL is present in the backup.
        using var connection = new SqliteConnection($"Data Source={backup};Pooling=False"); connection.Open();
        using var check = connection.CreateCommand(); check.CommandText = "PRAGMA quick_check;";
        Assert.Equal("ok", check.ExecuteScalar());
        await repository.InitializeAsync(default);
        Assert.Single(Directory.GetFiles(Path.Combine(runtime.Root, "Backups"), "*.db"));
    }

    [Fact]
    public async Task Backup_access_failure_prevents_schema_changes()
    {
        using var runtime = new Runtime(); using var old = runtime.Db(); old.GetService<IMigrator>().Migrate(Initial);
        Seed(old, runtime.Root, "preserved", DownloadStatus.Paused);
        File.WriteAllText(Path.Combine(runtime.Root, "Backups"), "blocked");
        var error = await Assert.ThrowsAsync<DownloadStorageException>(() => runtime.Repository().InitializeAsync(default));
        Assert.Contains("upgrade failed", error.Message);
        Assert.Single(old.Database.GetAppliedMigrations()); Assert.Equal(1, CountLegacyJobs(old));
        Assert.Equal("blocked", File.ReadAllText(Path.Combine(runtime.Root, "Backups")));
    }

    [Fact]
    public async Task Migration_failure_keeps_old_data_and_a_verified_backup()
    {
        using var runtime = new Runtime(); using var old = runtime.Db(); old.GetService<IMigrator>().Migrate(Initial);
        Seed(old, runtime.Root, "preserved", DownloadStatus.Paused);
        // A conflicting table is a real migration failure, not a mocked exception.
        old.Database.ExecuteSqlRaw("CREATE TABLE QueuePolicies (Unexpected TEXT);");
        var error = await Assert.ThrowsAsync<DownloadStorageException>(() => runtime.Repository().InitializeAsync(default));
        var backup = Assert.Single(Directory.GetFiles(Path.Combine(runtime.Root, "Backups"), "*.db"));
        Assert.Contains(backup, error.Message);
        using var copy = runtime.Db(backup); Assert.Equal(1, CountLegacyJobs(copy)); Assert.Single(copy.Database.GetAppliedMigrations());
        Assert.Equal(1, CountLegacyJobs(old)); Assert.Single(old.Database.GetAppliedMigrations());
    }

    [Fact]
    public async Task Newer_schema_is_rejected_without_migration_or_backup()
    {
        using var runtime = new Runtime(); await runtime.Repository().InitializeAsync(default);
        using var db = runtime.Db();
        db.Database.ExecuteSqlRaw("INSERT INTO __EFMigrationsHistory VALUES ('20990101000000_Future', '99.0.0');");
        var error = await Assert.ThrowsAsync<DownloadStorageException>(() => runtime.Repository().InitializeAsync(default));
        Assert.Contains("newer", error.Message); Assert.Equal(12, db.Database.GetAppliedMigrations().Count());
        Assert.False(Directory.Exists(Path.Combine(runtime.Root, "Backups")));
    }

    [Fact]
    public async Task Unreadable_database_is_preserved()
    {
        using var runtime = new Runtime(); var bytes = new byte[] { 0, 1, 2, 3, 4 }; File.WriteAllBytes(runtime.Path, bytes);
        await Assert.ThrowsAsync<DownloadStorageException>(() => runtime.Repository().InitializeAsync(default));
        Assert.Equal(bytes, File.ReadAllBytes(runtime.Path));
    }

    private static Guid Seed(DownloadDbContext db, string root, string name, DownloadStatus status, DownloadAction? action = null)
    {
        var job = Guid.NewGuid(); var attempt = Guid.NewGuid(); var file = Guid.NewGuid(); var operation = Guid.NewGuid();
        var ticks = DateTimeOffset.UtcNow.UtcTicks; var path = System.IO.Path.Combine(root, name + ".bin"); var gid = Guid.NewGuid().ToString("N")[..16];
        var source = "https://example.invalid/file";
        db.Database.ExecuteSqlInterpolated($"INSERT INTO Jobs VALUES ({job}, {name}, {source}, {path}, {path.ToUpperInvariant()}, {ticks});");
        db.Database.ExecuteSqlInterpolated($"INSERT INTO Attempts (Id, JobId, EngineId, StartedAtUtcTicks, Status, DownloadedBytes, TotalBytes, UpdatedAtUtcTicks) VALUES ({attempt}, {job}, {gid}, {ticks}, {(int)status}, {12L}, {100L}, {ticks});");
        db.Database.ExecuteSqlInterpolated($"INSERT INTO Files VALUES ({file}, {job}, {attempt}, {name + ".bin"}, {100L});");
        if (action is not null) db.Database.ExecuteSqlInterpolated($"INSERT INTO Operations (Id, JobId, AttemptId, Action, Status, RequestedAtUtcTicks) VALUES ({operation}, {job}, {attempt}, {(int)action.Value}, {(int)OperationStatus.Pending}, {ticks});");
        return job;
    }

    private static int CountLegacyJobs(DownloadDbContext db)
    {
        db.Database.OpenConnection();
        using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Jobs";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private sealed class Runtime : IDisposable
    {
        public string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AriaGUI.UpgradeTests", Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(Root, "downloads.db");
        public Runtime() => Directory.CreateDirectory(Root);
        public SqliteDownloadRepository Repository() => new(Path);
        public DownloadDbContext Db(string? path = null) => new(new DbContextOptionsBuilder<DownloadDbContext>()
            .UseSqlite(new SqliteConnectionStringBuilder { DataSource = path ?? Path, Pooling = false }.ToString()).Options);
        public void Dispose()
        {
            var allowed = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AriaGUI.UpgradeTests")) + System.IO.Path.DirectorySeparatorChar;
            if (!System.IO.Path.GetFullPath(Root).StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe cleanup.");
            Directory.Delete(Root, true);
        }
    }
}
