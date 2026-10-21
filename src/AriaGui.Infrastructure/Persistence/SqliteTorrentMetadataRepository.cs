using AriaGui.Core.Downloads;
using AriaGui.Application.Downloads;
using AriaGui.Infrastructure.Aria2;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AriaGui.Infrastructure.Persistence;

public sealed record StoredTorrentDraft(Guid JobId, TorrentManifest Manifest, TorrentSelection Selection,
    byte[] Metainfo, string PayloadRoot, string DestinationRoot, TorrentStage Stage);
public sealed record TorrentEngineBinding(Guid JobId, string Generation, string EngineId, string InfoHash);

/// <summary>Durable local metadata and selection; no RPC or payload filesystem writes.</summary>
public sealed class SqliteTorrentMetadataRepository(string databasePath)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DownloadDbContext CreateContext() => new(new DbContextOptionsBuilder<DownloadDbContext>()
        .UseSqlite(new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false, DefaultTimeout = 5 }.ToString()).Options);

    public Task InitializeAsync(CancellationToken token = default) => OnDatabaseThreadAsync(db =>
    {
        UserRuntimeDirectory.EnsurePrivate(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        DatabaseSchemaUpgrade.Apply(db, databasePath);
        db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
        return true;
    }, token);

    public async Task<Guid> CreateDraftAsync(byte[] metainfo, IEnumerable<int> fileIndices,
        string payloadRoot, string destinationRoot, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(metainfo);
        ArgumentNullException.ThrowIfNull(fileIndices);
        var manifest = await TorrentManifestParser.ParseAsync(new MemoryStream(metainfo, writable: false), token).ConfigureAwait(false);
        var selection = manifest.SelectFiles(fileIndices);
        var privateRoot = AbsoluteRoot(payloadRoot);
        var publicRoot = AbsoluteRoot(destinationRoot);
        var target = Path.GetFullPath(Path.Combine(publicRoot, manifest.Name));
        if (Overlaps(privateRoot, publicRoot) || File.Exists(target) || Directory.Exists(target))
            throw new IOException("Torrent destination collides with existing files or private payload storage.");
        var jobId = Guid.NewGuid();
        await OnDatabaseThreadAsync(db =>
        {
            using var transaction = db.Database.BeginTransaction();
            if (db.TorrentSpecs.AsNoTracking().Select(row => new { row.DestinationKey, row.PayloadRoot })
                    .AsEnumerable().Any(existing => Overlaps(existing.DestinationKey, target)
                        || Overlaps(existing.PayloadRoot, privateRoot) || Overlaps(existing.PayloadRoot, target)
                        || Overlaps(existing.DestinationKey, privateRoot))
                || db.Jobs.AsNoTracking().Select(row => row.DestinationKey).AsEnumerable()
                    .Any(existing => Overlaps(existing, target) || Overlaps(existing, privateRoot)))
                throw new IOException("Torrent destination or private payload root is already used by download history.");
            var now = DateTimeOffset.UtcNow.UtcTicks;
            db.TorrentSpecs.Add(new TorrentSpecRow
            {
                JobId = jobId, InfoHash = manifest.InfoHash, Name = manifest.Name,
                Metainfo = metainfo.ToArray(), TotalBytes = manifest.TotalBytes, PieceLength = manifest.PieceLength,
                IsPrivate = manifest.IsPrivate, PayloadRoot = privateRoot, DestinationRoot = publicRoot,
                DestinationKey = target.ToUpperInvariant(), SelectedBytes = selection.SelectedBytes,
                Stage = TorrentStage.AwaitingSelection, CreatedAtUtcTicks = now, UpdatedAtUtcTicks = now
            });
            foreach (var file in manifest.Files)
                db.TorrentFiles.Add(new TorrentFileRow
                {
                    JobId = jobId, FileIndex = file.Index, RelativePath = file.RelativePath,
                    Length = file.Length, Selected = selection.FileIndices.Contains(file.Index)
                });
            db.SaveChanges();
            transaction.Commit();
            return true;
        }, token).ConfigureAwait(false);
        return jobId;
    }

    public Task<StoredTorrentDraft?> LoadDraftAsync(Guid jobId, CancellationToken token = default) => OnDatabaseThreadAsync(db =>
    {
        var spec = db.TorrentSpecs.AsNoTracking().SingleOrDefault(row => row.JobId == jobId);
        if (spec is null) return null;
        TorrentManifest manifest;
        try { manifest = TorrentManifestParser.Parse(spec.Metainfo, token); }
        catch (InvalidDataException) { throw new DownloadStorageException("Saved torrent metadata is invalid; the draft was preserved."); }
        var rows = db.TorrentFiles.AsNoTracking().Where(row => row.JobId == jobId).OrderBy(row => row.FileIndex).ToArray();
        if (manifest.InfoHash != spec.InfoHash || manifest.Name != spec.Name
            || manifest.TotalBytes != spec.TotalBytes || manifest.PieceLength != spec.PieceLength
            || manifest.IsPrivate != spec.IsPrivate || rows.Length != manifest.Files.Count
            || rows.Where((row, index) => row.FileIndex != index + 1
                || row.RelativePath != manifest.Files[index].RelativePath || row.Length != manifest.Files[index].Length).Any())
            throw new DownloadStorageException("Saved torrent manifest does not match its file records; the draft was preserved.");
        TorrentSelection selection;
        try { selection = manifest.SelectFiles(rows.Where(row => row.Selected).Select(row => row.FileIndex)); }
        catch (ArgumentException) { throw new DownloadStorageException("Saved torrent selection is invalid; the draft was preserved."); }
        if (selection.SelectedBytes != spec.SelectedBytes)
            throw new DownloadStorageException("Saved torrent selection size is inconsistent; the draft was preserved.");
        return new StoredTorrentDraft(spec.JobId, manifest, selection, spec.Metainfo.ToArray(),
            spec.PayloadRoot, spec.DestinationRoot, spec.Stage);
    }, token);

    public Task<IReadOnlyList<Guid>> LoadUnfinishedIdsAsync(CancellationToken token = default) => OnDatabaseThreadAsync<IReadOnlyList<Guid>>(db =>
        db.TorrentSpecs.AsNoTracking().Where(row => row.Stage != TorrentStage.AwaitingSelection
            && row.Stage != TorrentStage.Completed && row.Stage != TorrentStage.Stopped && row.Stage != TorrentStage.Failed)
            .Select(row => row.JobId).ToArray(), token);

    public Task<IReadOnlyList<Guid>> LoadDraftIdsAsync(CancellationToken token = default) => OnDatabaseThreadAsync<IReadOnlyList<Guid>>(db =>
        db.TorrentSpecs.AsNoTracking().OrderByDescending(row => row.CreatedAtUtcTicks)
            .Select(row => row.JobId).ToArray(), token);

    public Task<TorrentEngineBinding?> LoadBindingAsync(Guid jobId, CancellationToken token = default) => OnDatabaseThreadAsync(db =>
    {
        var row = db.TorrentEngines.AsNoTracking().SingleOrDefault(item => item.JobId == jobId && item.Role == TorrentEngineRole.Payload);
        return row is null ? null : new TorrentEngineBinding(row.JobId, row.Generation, row.EngineId, row.InfoHash);
    }, token);

    public Task<IReadOnlyList<TorrentEngineBinding>> LoadBindingsAsync(CancellationToken token = default) => OnDatabaseThreadAsync<IReadOnlyList<TorrentEngineBinding>>(db =>
        db.TorrentEngines.AsNoTracking().Where(row => row.Role == TorrentEngineRole.Payload)
            .Select(row => new TorrentEngineBinding(row.JobId, row.Generation, row.EngineId, row.InfoHash)).ToArray(), token);

    public Task<TorrentEngineBinding> PrepareDispatchAsync(Guid jobId, string generation, string gid, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(generation) || generation.Length > 128 || gid.Length != 16
            || gid == "0000000000000000" || !gid.All(Uri.IsHexDigit))
            throw new ArgumentException("Invalid engine identity.");
        return OnDatabaseThreadAsync(db =>
        {
            using var transaction = db.Database.BeginTransaction();
            var spec = db.TorrentSpecs.SingleOrDefault(row => row.JobId == jobId)
                ?? throw new DownloadStorageException("Torrent draft is missing.");
            if (spec.Stage is TorrentStage.Completed or TorrentStage.Stopped)
                throw new DownloadStorageException("Finished torrent cannot be dispatched again.");
            var row = db.TorrentEngines.SingleOrDefault(item => item.JobId == jobId && item.Role == TorrentEngineRole.Payload);
            if (row is null)
            {
                row = new TorrentEngineRow { JobId = jobId, Role = TorrentEngineRole.Payload, EngineId = gid,
                    Generation = generation, InfoHash = spec.InfoHash, RegisteredAtUtcTicks = DateTimeOffset.UtcNow.UtcTicks };
                db.TorrentEngines.Add(row);
            }
            else if (row.EngineId != gid || row.InfoHash != spec.InfoHash)
                throw new DownloadStorageException("Saved torrent engine identity changed.");
            else row.Generation = generation;
            if (spec.Stage == TorrentStage.AwaitingSelection) spec.Stage = TorrentStage.Waiting;
            spec.UpdatedAtUtcTicks = DateTimeOffset.UtcNow.UtcTicks;
            db.SaveChanges();
            transaction.Commit();
            return new TorrentEngineBinding(jobId, row.Generation, row.EngineId, row.InfoHash);
        }, token);
    }

    public Task SetStageAsync(Guid jobId, TorrentStage stage, CancellationToken token = default) => OnDatabaseThreadAsync(db =>
    {
        var row = db.TorrentSpecs.SingleOrDefault(item => item.JobId == jobId)
            ?? throw new DownloadStorageException("Torrent draft is missing.");
        if (row.Stage == TorrentStage.Completed && stage != TorrentStage.Completed)
            throw new DownloadStorageException("Completed torrent cannot become active again.");
        row.Stage = stage;
        row.UpdatedAtUtcTicks = DateTimeOffset.UtcNow.UtcTicks;
        db.SaveChanges();
        return true;
    }, token);

    public Task<IReadOnlyList<TorrentFileRow>> LoadSelectedFilesAsync(Guid jobId, CancellationToken token = default) => OnDatabaseThreadAsync<IReadOnlyList<TorrentFileRow>>(db =>
        db.TorrentFiles.AsNoTracking().Where(row => row.JobId == jobId && row.Selected).OrderBy(row => row.FileIndex).ToArray(), token);

    public Task MarkPublishedAsync(Guid jobId, int fileIndex, long length, CancellationToken token = default) => OnDatabaseThreadAsync(db =>
    {
        var row = db.TorrentFiles.SingleOrDefault(item => item.JobId == jobId && item.FileIndex == fileIndex && item.Selected)
            ?? throw new DownloadStorageException("Selected torrent file is missing.");
        if (row.Length != length) throw new DownloadStorageException("Published torrent file length changed.");
        row.Published = true;
        row.PublishedBytes = length;
        db.SaveChanges();
        return true;
    }, token);

    public Task MarkPublishingAsync(Guid jobId, int fileIndex, CancellationToken token = default) => OnDatabaseThreadAsync(db =>
    {
        var row = db.TorrentFiles.SingleOrDefault(item => item.JobId == jobId && item.FileIndex == fileIndex && item.Selected)
            ?? throw new DownloadStorageException("Selected torrent file is missing.");
        if (row.Published) throw new DownloadStorageException("Torrent file was already published.");
        row.PublishedBytes = -1; // Durable file-move intent; length is recorded only after publication.
        db.SaveChanges();
        return true;
    }, token);

    private static string AbsoluteRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("Choose an absolute torrent directory.", nameof(path));
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }
    private static bool Overlaps(string first, string second)
    {
        var left = Path.TrimEndingDirectorySeparator(first);
        var right = Path.TrimEndingDirectorySeparator(second);
        return left.Equals(right, StringComparison.OrdinalIgnoreCase)
            || left.StartsWith(right + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || right.StartsWith(left + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<T> OnDatabaseThreadAsync<T>(Func<DownloadDbContext, T> work, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                using var db = CreateContext();
                return work(db);
            }, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SqliteException or DbUpdateException)
        { throw new DownloadStorageException("Torrent metadata could not be read or saved; its existing records were preserved."); }
        finally { _gate.Release(); }
    }
}
