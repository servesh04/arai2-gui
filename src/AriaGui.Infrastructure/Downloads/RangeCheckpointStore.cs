using System.Security.Cryptography;
using System.Text.Json;
using AriaGui.Infrastructure.Aria2;
using AriaGui.Core.Downloads;

namespace AriaGui.Infrastructure.Downloads;

// M3 quiescent-pair checkpoint. Coverage comes from aria2's control bitmap and
// authenticated RPC count, not from bytes the relay merely sent downstream.
internal sealed record RangeCheckpoint(int Version, Guid Attempt, long Revision, string SnapshotDirectory,
    string FileName, long PayloadLength, string PayloadSha256, long ControlLength, string ControlSha256,
    long TotalLength, long CoveredBytes, int PieceLength);

internal sealed class RangeCheckpointStore
{
    private readonly string _root;
    private readonly string _path;
    private readonly Guid _attempt;
    internal RangeCheckpointStore(string privateRoot, Guid attempt)
    {
        if (!Path.IsPathFullyQualified(privateRoot) || attempt == Guid.Empty) throw new ArgumentException();
        // Isolate each attempt so revision cleanup cannot touch another job's
        // snapshot, even after a crash between pointer commit and pruning.
        _root = Path.Combine(Path.GetFullPath(privateRoot), attempt.ToString("N"));
        UserRuntimeDirectory.EnsurePrivate(_root);
        _attempt = attempt;
        _path = Path.Combine(_root, "checkpoint.json");
    }
    internal async Task<RangeCheckpoint?> LoadAsync(CancellationToken token)
    {
        if (!File.Exists(_path)) return null;
        if ((File.GetAttributes(_path) & FileAttributes.ReparsePoint) != 0 || new FileInfo(_path).Length > 4096)
            throw new IOException("Invalid range checkpoint.");
        var checkpoint = JsonSerializer.Deserialize<RangeCheckpoint>(await File.ReadAllTextAsync(_path, token).ConfigureAwait(false));
        if (checkpoint is not { Version: 1, Revision: > 0 } || checkpoint.Attempt != _attempt
            || !Guid.TryParseExact(checkpoint.SnapshotDirectory, "N", out _)
            || !WindowsFileNamePolicy.IsValid(checkpoint.FileName)
            || checkpoint.PayloadLength < 0 || checkpoint.ControlLength is < 0 or > 4 * 1024 * 1024 || checkpoint.TotalLength <= 0
            || checkpoint.PayloadLength > checkpoint.TotalLength
            || checkpoint.CoveredBytes < 0 || checkpoint.CoveredBytes > checkpoint.TotalLength || checkpoint.PieceLength < 16384
            || !ValidHash(checkpoint.PayloadSha256) || !ValidHash(checkpoint.ControlSha256))
            throw new IOException("Invalid range checkpoint.");
        var pair = Path.Combine(_root, checkpoint.SnapshotDirectory);
        if (!Directory.Exists(pair) || (File.GetAttributes(pair) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Range checkpoint snapshot missing.");
        foreach (var (path, length, hash) in new[]
        {
            (Path.Combine(pair, checkpoint.FileName), checkpoint.PayloadLength, checkpoint.PayloadSha256),
            (Path.Combine(pair, checkpoint.FileName + ".aria2"), checkpoint.ControlLength, checkpoint.ControlSha256)
        })
        {
            if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0
                || new FileInfo(path).Length != length || await HashAsync(path, token).ConfigureAwait(false) != hash)
                throw new IOException("Range checkpoint snapshot changed.");
        }
        var control = Aria2ControlCoverageReader.Parse(await File.ReadAllBytesAsync(Path.Combine(pair, checkpoint.FileName + ".aria2"), token).ConfigureAwait(false));
        if (control.TotalLength != checkpoint.TotalLength || control.CoveredBytes != checkpoint.CoveredBytes
            || control.PieceLength != checkpoint.PieceLength) throw new IOException("Range checkpoint coverage changed.");
        return checkpoint;
    }
    internal async Task<RangeCheckpoint> CaptureAsync(string quiescentPayload, CancellationToken token, long? observedCompletedBytes = null)
    {
        if (!WindowsFileNamePolicy.IsValid(Path.GetFileName(quiescentPayload))) throw new IOException("Unexpected range stage name.");
        var prior = await LoadAsync(token).ConfigureAwait(false);
        // The existing copy primitive opens both files without write/delete sharing.
        // It fails closed if aria2 still owns either writer.
        var snapshot = await RetainedPartialStage.CreateAsync(_root, quiescentPayload, token).ConfigureAwait(false);
        var payload = Path.Combine(snapshot.Directory, snapshot.FileName);
        var control = payload + ".aria2";
        if (new FileInfo(control).Length > 4 * 1024 * 1024) throw new IOException("Oversized aria2 control checkpoint.");
        var payloadHash = await HashAsync(payload, token).ConfigureAwait(false);
        var controlHash = await HashAsync(control, token).ConfigureAwait(false);
        var coverage = Aria2ControlCoverageReader.Parse(await File.ReadAllBytesAsync(control, token).ConfigureAwait(false));
        if (observedCompletedBytes is not null && coverage.CoveredBytes != observedCompletedBytes)
            throw new IOException("aria2 control coverage differs from the quiescent engine observation.");
        var payloadLength = new FileInfo(payload).Length;
        var controlLength = new FileInfo(control).Length;
        if (payloadLength > coverage.TotalLength) throw new IOException("Sparse range stage exceeds control total.");
        if (prior is not null && prior.PayloadLength == payloadLength && prior.ControlLength == controlLength
            && prior.PayloadSha256 == payloadHash && prior.ControlSha256 == controlHash)
        {
            var ownedPrefix = _root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(snapshot.Directory).StartsWith(ownedPrefix, StringComparison.OrdinalIgnoreCase)
                || !Guid.TryParseExact(Path.GetFileName(snapshot.Directory), "N", out _))
                throw new IOException("Unexpected checkpoint snapshot path.");
            Directory.Delete(snapshot.Directory, true);
            return prior;
        }
        var next = new RangeCheckpoint(1, _attempt, (prior?.Revision ?? 0) + 1,
            Path.GetFileName(snapshot.Directory), snapshot.FileName, payloadLength, payloadHash, controlLength, controlHash,
            coverage.TotalLength, coverage.CoveredBytes, coverage.PieceLength);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".new";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { await JsonSerializer.SerializeAsync(stream, next, cancellationToken: token).ConfigureAwait(false); await stream.FlushAsync(token).ConfigureAwait(false); stream.Flush(true); }
            File.Move(temporary, _path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        PruneSnapshots(next.SnapshotDirectory);
        return next;
    }
    internal async Task<RetainedPartialStage?> RestoreCopyAsync(string destinationStageRoot, CancellationToken token)
    {
        var checkpoint = await LoadAsync(token).ConfigureAwait(false);
        if (checkpoint is null) return null;
        PruneSnapshots(checkpoint.SnapshotDirectory);
        // The checkpoint is immutable evidence. Resume in a new private directory;
        // an abandoned engine work pair is never overwritten or trusted.
        var source = Path.Combine(_root, checkpoint.SnapshotDirectory, checkpoint.FileName);
        return await RetainedPartialStage.CreateAsync(destinationStageRoot, source, token).ConfigureAwait(false);
    }
    private void PruneSnapshots(string current)
    {
        var prefix = _root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var directory in Directory.EnumerateDirectories(_root))
        {
            var full = Path.GetFullPath(directory);
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                || !Guid.TryParseExact(Path.GetFileName(full), "N", out _)
                || Path.GetFileName(full) == current) continue;
            try
            {
                if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) == 0)
                    Directory.Delete(full, recursive: true);
            }
            catch (IOException) { /* A pinned old snapshot can be pruned on the next checkpoint/restore. */ }
            catch (UnauthorizedAccessException) { /* Preserve the current durable pointer. */ }
        }
    }
    private static bool ValidHash(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
    private static async Task<string> HashAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 32768, true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
    }
}
