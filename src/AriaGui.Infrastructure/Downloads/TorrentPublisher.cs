using AriaGui.Core.Downloads;
using System.Security.Cryptography;
using AriaGui.Infrastructure.Aria2;
using AriaGui.Infrastructure.Persistence;

namespace AriaGui.Infrastructure.Downloads;

public sealed record TorrentPublicationProgress(Guid JobId, long PublishedBytes, long TotalBytes);

/// <summary>Publishes aria2-verified selected files. A crash-window collision remains for explicit review.</summary>
public sealed class TorrentPublisher(SqliteTorrentMetadataRepository repository)
{
    public async Task PublishAsync(StoredTorrentDraft draft, TorrentEngineObservation observed, CancellationToken token,
        IProgress<TorrentPublicationProgress>? progress = null)
    {
        if (observed.Stage != TorrentStage.Publishing || draft.Stage is TorrentStage.AwaitingSelection or TorrentStage.Stopped)
            throw new InvalidOperationException("Torrent payload is not verified and ready to publish.");
        var binding = await repository.LoadBindingAsync(draft.JobId, token).ConfigureAwait(false);
        if (binding is null || binding.EngineId != observed.Gid || binding.InfoHash != draft.Manifest.InfoHash)
            throw new InvalidOperationException("Torrent publication does not match durable engine ownership.");
        var files = await repository.LoadSelectedFilesAsync(draft.JobId, token).ConfigureAwait(false);
        if (files.Count != draft.Selection.FileIndices.Count) throw new IOException("Saved file selection changed.");
        var unpublished = files.Where(row => !row.Published).ToArray();
        foreach (var row in files.Where(row => row.Published))
        {
            var destination = PublicPath(draft, row.RelativePath);
            if (IsReparsePath(draft.DestinationRoot, destination) || !File.Exists(destination)
                || new FileInfo(destination).Length != row.Length
                || !await SameBytesAsync(PrivatePath(draft, row.RelativePath), destination, token).ConfigureAwait(false))
                throw new IOException("A previously published torrent file changed; review it before continuing.");
        }
        var published = files.Where(row => row.Published).Aggregate(0L, (sum, row) => checked(sum + row.Length));
        var outstanding = 0L;
        foreach (var row in unpublished)
        {
            var temp = TemporaryPath(draft, row);
            if (Directory.Exists(temp) || IsReparsePath(draft.DestinationRoot, temp))
                throw new IOException("Torrent publication path changed; review it before continuing.");
            var partial = File.Exists(temp) ? new FileInfo(temp).Length : 0;
            if (partial > row.Length || File.Exists(temp) && row.PublishedBytes != -1)
                throw new IOException("Torrent publication checkpoint does not match its partial file.");
            if (!File.Exists(PublicPath(draft, row.RelativePath)))
                outstanding = checked(outstanding + row.Length - partial);
        }
        CheckSpace(draft.DestinationRoot, outstanding);
        await repository.SetStageAsync(draft.JobId, TorrentStage.Publishing, token).ConfigureAwait(false);
        progress?.Report(new(draft.JobId, published, draft.Selection.SelectedBytes));
        foreach (var row in unpublished)
        {
            token.ThrowIfCancellationRequested();
            var source = PrivatePath(draft, row.RelativePath);
            var destination = PublicPath(draft, row.RelativePath);
            var temp = TemporaryPath(draft, row);
            if (IsReparsePath(draft.PayloadRoot, source) || IsReparsePath(draft.DestinationRoot, destination)
                || !File.Exists(source) || new FileInfo(source).Length != row.Length
                || Directory.Exists(destination) || Directory.Exists(temp) || IsReparsePath(draft.DestinationRoot, temp))
                throw new IOException("Torrent source or destination changed; publication needs review.");
            if (File.Exists(destination))
            {
                if (File.Exists(temp) || row.PublishedBytes != -1 || new FileInfo(destination).Length != row.Length
                    || !await SameBytesAsync(source, destination, token).ConfigureAwait(false))
                    throw new IOException("Torrent destination already exists; publication needs review.");
                await repository.MarkPublishedAsync(draft.JobId, row.FileIndex, row.Length, token).ConfigureAwait(false);
                published = checked(published + row.Length);
                progress?.Report(new(draft.JobId, published, draft.Selection.SelectedBytes));
                continue;
            }
            var parent = Path.GetDirectoryName(destination)!;
            Directory.CreateDirectory(parent);
            if (IsReparsePath(draft.DestinationRoot, destination) || IsReparsePath(draft.DestinationRoot, temp))
                throw new IOException("Torrent destination path changed during publication.");
            await repository.MarkPublishingAsync(draft.JobId, row.FileIndex, token).ConfigureAwait(false);
            await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
                1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using (var output = new FileStream(temp, File.Exists(temp) ? FileMode.Open : FileMode.CreateNew,
                FileAccess.ReadWrite, FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                if (input.Length != row.Length || output.Length > row.Length
                    || !await SamePrefixAsync(input, output, output.Length, token).ConfigureAwait(false))
                    throw new IOException("Torrent partial file differs from its verified payload; review it before continuing.");
                input.Position = output.Length;
                output.Position = output.Length;
                published = checked(published + output.Length);
                progress?.Report(new(draft.JobId, published, draft.Selection.SelectedBytes));
                var buffer = new byte[1024 * 1024];
                while (true)
                {
                    var count = await input.ReadAsync(buffer, token).ConfigureAwait(false);
                    if (count == 0) break;
                    await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                    published = checked(published + count);
                    progress?.Report(new(draft.JobId, published, draft.Selection.SelectedBytes));
                }
                await output.FlushAsync(token).ConfigureAwait(false);
            }
            if (input.Length != row.Length || new FileInfo(temp).Length != row.Length
                || IsReparsePath(draft.DestinationRoot, destination) || IsReparsePath(draft.DestinationRoot, temp))
                throw new IOException("Torrent file changed while it was being published.");
            File.Move(temp, destination, overwrite: false);
            await repository.MarkPublishedAsync(draft.JobId, row.FileIndex, row.Length, token).ConfigureAwait(false);
        }
        await repository.SetStageAsync(draft.JobId, TorrentStage.Completed, token).ConfigureAwait(false);
        progress?.Report(new(draft.JobId, draft.Selection.SelectedBytes, draft.Selection.SelectedBytes));
    }

    private static string TemporaryPath(StoredTorrentDraft draft, TorrentFileRow row) =>
        PublicPath(draft, row.RelativePath) + $".ariagui-{draft.JobId:N}-{row.FileIndex}.partial";

    private static string PrivatePath(StoredTorrentDraft draft, string relative) => Beneath(draft.PayloadRoot, relative);
    private static string PublicPath(StoredTorrentDraft draft, string relative) => Beneath(draft.DestinationRoot, relative);
    private static string Beneath(string root, string relative)
    {
        if (!Path.IsPathFullyQualified(root) || Path.IsPathRooted(relative)) throw new IOException("Torrent path is unsafe.");
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var full = Path.GetFullPath(Path.Combine(fullRoot, relative));
        if (!full.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Torrent path escapes its selected folder.");
        return full;
    }
    private static bool IsReparsePath(string root, string path)
    {
        var current = Path.GetFullPath(root);
        if (File.Exists(current) || Directory.Exists(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
        var relative = Path.GetRelativePath(current, path);
        foreach (var component in relative.Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, component);
            if ((File.Exists(current) || Directory.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
        }
        return false;
    }
    private static void CheckSpace(string destinationRoot, long required)
    {
        var drive = Path.GetPathRoot(Path.GetFullPath(destinationRoot));
        if (drive is null || new DriveInfo(drive).AvailableFreeSpace < required)
            throw new IOException("Not enough free space to publish selected torrent files.");
    }
    private static async Task<bool> SameBytesAsync(string first, string second, CancellationToken token)
    {
        await using var left = new FileStream(first, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var right = new FileStream(second, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (left.Length != right.Length) return false;
        var leftHash = await SHA256.HashDataAsync(left, token).ConfigureAwait(false);
        var rightHash = await SHA256.HashDataAsync(right, token).ConfigureAwait(false);
        return leftHash.AsSpan().SequenceEqual(rightHash);
    }

    private static async Task<bool> SamePrefixAsync(Stream source, Stream partial, long length, CancellationToken token)
    {
        var sourceBuffer = new byte[1024 * 1024];
        var partialBuffer = new byte[sourceBuffer.Length];
        while (length > 0)
        {
            var count = (int)Math.Min(length, sourceBuffer.Length);
            await source.ReadExactlyAsync(sourceBuffer.AsMemory(0, count), token).ConfigureAwait(false);
            await partial.ReadExactlyAsync(partialBuffer.AsMemory(0, count), token).ConfigureAwait(false);
            if (!sourceBuffer.AsSpan(0, count).SequenceEqual(partialBuffer.AsSpan(0, count))) return false;
            length -= count;
        }
        return true;
    }
}
