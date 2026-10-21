using System.Security.Cryptography;
using AriaGui.Infrastructure.Persistence;

namespace AriaGui.Infrastructure.Downloads;

/// <summary>Deletes only the recorded publication outputs and the app's private payload tree.</summary>
public static class TorrentOwnedFileDeletion
{
    public static async Task ValidateAsync(string runtimeRoot, StoredTorrentDraft draft,
        IReadOnlyList<TorrentFileRow> selected, CancellationToken token)
    {
        var ownedParent = Path.GetFullPath(Path.Combine(runtimeRoot, "torrent-payloads"));
        var payload = Path.GetFullPath(draft.PayloadRoot);
        if (!Inside(ownedParent, payload)
            || !string.Equals(Path.GetDirectoryName(payload), ownedParent, StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(Path.GetFileName(payload), "N", out _))
            throw new IOException("Torrent payload is outside its owned directory.");
        if (Directory.Exists(payload)) CheckTree(payload);
        foreach (var row in selected)
        {
            token.ThrowIfCancellationRequested();
            var source = Below(payload, row.RelativePath);
            var destination = Below(draft.DestinationRoot, row.RelativePath);
            var partial = destination + $".ariagui-{draft.JobId:N}-{row.FileIndex}.partial";
            CheckPath(draft.DestinationRoot, destination);
            CheckPath(draft.DestinationRoot, partial);
            if (File.Exists(partial))
            {
                if (row.PublishedBytes != -1 || !File.Exists(source) || new FileInfo(source).Length != row.Length
                    || new FileInfo(partial).Length > row.Length
                    || !await SamePrefixAsync(source, partial, token).ConfigureAwait(false))
                    throw new IOException("A torrent partial file changed; it was preserved for review.");
            }
            if (!row.Published || !File.Exists(destination)) continue;
            if (!File.Exists(source) || new FileInfo(source).Length != row.Length
                || new FileInfo(destination).Length != row.Length
                || !await SameBytesAsync(source, destination, token).ConfigureAwait(false))
                throw new IOException("A published torrent file changed; it was preserved for review.");
        }
    }

    public static async Task DeleteAsync(string runtimeRoot, StoredTorrentDraft draft,
        IReadOnlyList<TorrentFileRow> selected, CancellationToken token)
    {
        await ValidateAsync(runtimeRoot, draft, selected, token).ConfigureAwait(false);
        foreach (var row in selected)
        {
            token.ThrowIfCancellationRequested();
            var destination = Below(draft.DestinationRoot, row.RelativePath);
            var partial = destination + $".ariagui-{draft.JobId:N}-{row.FileIndex}.partial";
            if (row.Published && File.Exists(destination)) File.Delete(destination);
            if (row.PublishedBytes == -1 && File.Exists(partial)) File.Delete(partial);
        }
        if (Directory.Exists(draft.PayloadRoot)) Directory.Delete(draft.PayloadRoot, recursive: true);
    }

    private static string Below(string root, string relative)
    {
        if (!Path.IsPathFullyQualified(root) || Path.IsPathRooted(relative)) throw new IOException("Torrent path is unsafe.");
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var full = Path.GetFullPath(Path.Combine(fullRoot, relative));
        if (!Inside(fullRoot, full)) throw new IOException("Torrent path escapes its destination.");
        return full;
    }
    private static bool Inside(string root, string path) =>
        path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static void CheckPath(string root, string path)
    {
        var current = Path.GetFullPath(root);
        if (File.Exists(current) || Directory.Exists(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Torrent path contains a link.");
        foreach (var component in Path.GetRelativePath(current, path).Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, component);
            if ((File.Exists(current) || Directory.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Torrent path contains a link.");
        }
    }
    private static void CheckTree(string root)
    {
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Torrent payload contains a link.");
        foreach (var child in Directory.EnumerateFileSystemEntries(root))
        {
            var attributes = File.GetAttributes(child);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Torrent payload contains a link.");
            if ((attributes & FileAttributes.Directory) != 0) CheckTree(child);
        }
    }
    private static async Task<bool> SameBytesAsync(string first, string second, CancellationToken token)
    {
        await using var left = File.OpenRead(first);
        await using var right = File.OpenRead(second);
        var a = await SHA256.HashDataAsync(left, token).ConfigureAwait(false);
        var b = await SHA256.HashDataAsync(right, token).ConfigureAwait(false);
        return a.AsSpan().SequenceEqual(b);
    }
    private static async Task<bool> SamePrefixAsync(string source, string partial, CancellationToken token)
    {
        await using var left = File.OpenRead(source);
        await using var right = File.OpenRead(partial);
        var leftBuffer = new byte[1024 * 1024];
        var rightBuffer = new byte[leftBuffer.Length];
        while (true)
        {
            var count = await right.ReadAsync(rightBuffer, token).ConfigureAwait(false);
            if (count == 0) return true;
            await left.ReadExactlyAsync(leftBuffer.AsMemory(0, count), token).ConfigureAwait(false);
            if (!leftBuffer.AsSpan(0, count).SequenceEqual(rightBuffer.AsSpan(0, count))) return false;
        }
    }
}
