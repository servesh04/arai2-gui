using System.Text;
using AriaGui.Application.Downloads;
using AriaGui.Core.Downloads;
using AriaGui.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AriaGui.Infrastructure.Tests;

public sealed class TorrentStorageTests
{
    [Fact]
    public async Task UpgradesLegacyHistoryAndRoundTripsSelectedMetadata()
    {
        using var runtime = new Runtime();
        using (var old = runtime.Db())
        {
            old.GetService<IMigrator>().Migrate("20261018090000_FailureHttpStatus");
            old.Jobs.Add(new JobRow
            {
                Id = Guid.NewGuid(), DisplayName = "legacy", Source = "https://example.invalid/file",
                Destination = Path.Combine(runtime.Root, "legacy.bin"),
                DestinationKey = Path.Combine(runtime.Root, "legacy.bin").ToUpperInvariant(),
                CreatedAtUtcTicks = DateTimeOffset.UtcNow.UtcTicks
            });
            old.SaveChanges();
        }

        var store = new SqliteTorrentMetadataRepository(runtime.DatabasePath);
        await store.InitializeAsync();
        var metainfo = Metainfo();
        var jobId = await store.CreateDraftAsync(metainfo, [2], Path.Combine(runtime.Root, "private"),
            Path.Combine(runtime.Root, "public"));
        var recovered = await new SqliteTorrentMetadataRepository(runtime.DatabasePath).LoadDraftAsync(jobId);
        Assert.Contains(jobId, await store.LoadDraftIdsAsync());
        Assert.NotNull(recovered);
        Assert.Equal(metainfo, recovered.Metainfo);
        Assert.Equal([2], recovered.Selection.FileIndices);
        Assert.Equal(3, recovered.Selection.SelectedBytes);
        Assert.Equal(TorrentStage.AwaitingSelection, recovered.Stage);
        using var current = runtime.Db();
        Assert.False(current.Database.HasPendingModelChanges());
        Assert.Equal(13, current.Database.GetAppliedMigrations().Count());
        Assert.Single(current.Jobs);
        Assert.Equal(2, current.TorrentFiles.Count());
        Assert.True(Directory.Exists(Path.Combine(runtime.Root, "Backups")));
    }

    [Fact]
    public async Task DuplicateDestinationAndMismatchedMetadataFailClosed()
    {
        using var runtime = new Runtime();
        var store = new SqliteTorrentMetadataRepository(runtime.DatabasePath);
        await store.InitializeAsync();
        var payload = Metainfo();
        var privateRoot = Path.Combine(runtime.Root, "private");
        var destination = Path.Combine(runtime.Root, "public");
        var jobId = await store.CreateDraftAsync(payload, [1], privateRoot, destination);
        await Assert.ThrowsAsync<IOException>(() => store.CreateDraftAsync(payload, [1],
            Path.Combine(runtime.Root, "other-private"), destination));
        using (var db = runtime.Db())
        {
            var row = db.TorrentFiles.Single(file => file.JobId == jobId && file.FileIndex == 1);
            row.RelativePath = "changed.bin";
            db.SaveChanges();
        }
        await Assert.ThrowsAsync<DownloadStorageException>(() => store.LoadDraftAsync(jobId));
    }

    [Fact]
    public async Task RejectsPrivatePayloadRootOverlap()
    {
        using var runtime = new Runtime();
        var store = new SqliteTorrentMetadataRepository(runtime.DatabasePath);
        await store.InitializeAsync();
        var privateRoot = Path.Combine(runtime.Root, "private");
        await store.CreateDraftAsync(Metainfo(), [1], privateRoot, Path.Combine(runtime.Root, "public-a"));
        await Assert.ThrowsAsync<IOException>(() => store.CreateDraftAsync(Metainfo(), [1],
            Path.Combine(privateRoot, "child"), Path.Combine(runtime.Root, "public-b")));
    }

    private static byte[] Metainfo()
    {
        var files = List(
            Dict(("length", Integer(2)), ("path", List(Bytes("first.bin")))),
            Dict(("length", Integer(3)), ("path", List(Bytes("second.bin")))));
        var info = Dict(("files", files), ("name", Bytes("bundle")),
            ("piece length", Integer(4)), ("pieces", ByteString(new byte[40])));
        return Dict(("announce", Bytes("http://127.0.0.1:53623/announce")), ("info", info));
    }
    private static byte[] Bytes(string value) => ByteString(Encoding.UTF8.GetBytes(value));
    private static byte[] ByteString(byte[] value) => [.. Encoding.ASCII.GetBytes(value.Length + ":"), .. value];
    private static byte[] Integer(long value) => Encoding.ASCII.GetBytes($"i{value}e");
    private static byte[] List(params byte[][] values) => [(byte)'l', .. values.SelectMany(value => value), (byte)'e'];
    private static byte[] Dict(params (string Key, byte[] Value)[] entries) =>
        [(byte)'d', .. entries.OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .SelectMany(entry => Bytes(entry.Key).Concat(entry.Value)), (byte)'e'];

    private sealed class Runtime : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "AriaGUI.TorrentStorageTests", Guid.NewGuid().ToString("N"));
        public string DatabasePath => Path.Combine(Root, "downloads.db");
        public Runtime() => Directory.CreateDirectory(Root);
        public DownloadDbContext Db() => new(new DbContextOptionsBuilder<DownloadDbContext>()
            .UseSqlite(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString()).Options);
        public void Dispose()
        {
            var allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "AriaGUI.TorrentStorageTests")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(Root).StartsWith(allowed, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unsafe test cleanup.");
            Directory.Delete(Root, true);
        }
    }
}
