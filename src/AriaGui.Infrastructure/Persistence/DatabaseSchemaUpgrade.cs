using AriaGui.Application.Downloads;
using AriaGui.Infrastructure.Aria2;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AriaGui.Infrastructure.Persistence;

internal static class DatabaseSchemaUpgrade
{
    public static void Apply(DownloadDbContext db, string databasePath)
    {
        var existing = File.Exists(databasePath) && new FileInfo(databasePath).Length > 0;
        if (db.Database.HasPendingModelChanges())
            throw new DownloadStorageException("This app's history model has no matching migration. Keep the database and use a compatible build.");
        var known = db.Database.GetMigrations().ToArray();
        var applied = db.Database.GetAppliedMigrations().ToArray();
        // An older executable must not silently open data written by a newer schema.
        if (applied.Except(known, StringComparer.Ordinal).Any())
            throw new DownloadStorageException("This history was upgraded by a newer AriaGUI version. Use that version; the database was preserved.");
        if (known.SequenceEqual(applied, StringComparer.Ordinal)) return;

        string? backup = null;
        var backupVerified = false;
        try
        {
            // SQLite's online backup includes committed WAL data; copying downloads.db alone does not.
            if (existing)
            {
                var backupDirectory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(databasePath))!, "Backups");
                UserRuntimeDirectory.EnsurePrivate(backupDirectory);
                backup = Path.Combine(backupDirectory, $"downloads-before-upgrade-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}.db");
                using var source = new SqliteConnection(db.Database.GetConnectionString());
                using var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backup, Pooling = false }.ToString());
                source.Open(); target.Open(); source.BackupDatabase(target);
                using var check = target.CreateCommand(); check.CommandText = "PRAGMA quick_check;";
                if (!string.Equals(check.ExecuteScalar()?.ToString(), "ok", StringComparison.Ordinal))
                    throw new IOException("The pre-upgrade backup could not be verified.");
                backupVerified = true;
            }
            db.Database.Migrate();
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or InvalidOperationException or AriaGui.Application.Engine.EngineException)
        {
            var recovery = !backupVerified ? "The existing database was preserved; no verified backup was available."
                : $"Keep the database and pre-upgrade backup at {backup}.";
            throw new DownloadStorageException($"History upgrade failed. {recovery} Close AriaGUI before attempting recovery.");
        }
    }
}
