using AriaGui.Infrastructure.Downloads;

namespace AriaGui.Infrastructure.Tests;

public sealed class RangeCheckpointStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AriaGUI.RangeCheckpointTests", Guid.NewGuid().ToString("N"));
    [Fact]
    public async Task Same_length_sparse_and_control_changes_advance_durable_revision_without_changing_old_snapshot()
    {
        Directory.CreateDirectory(_root);
        var payload = Path.Combine(_root, "payload.bin");
        await File.WriteAllBytesAsync(payload, new byte[4096]);
        await File.WriteAllBytesAsync(payload + ".aria2", M3ControlFixture.OnePiece(4096, false));
        var store = new RangeCheckpointStore(Path.Combine(_root, "snapshots"), Guid.NewGuid());
        var first = await store.CaptureAsync(payload, default);
        Assert.Equal(1, first.Revision);
        Assert.Equal(first, await store.CaptureAsync(payload, default));
        Assert.Single(Directory.EnumerateDirectories(Path.Combine(_root, "snapshots", first.Attempt.ToString("N"))));
        using (var stream = new FileStream(payload, FileMode.Open, FileAccess.Write))
        { stream.Position = 2048; stream.WriteByte(0x7f); }
        var second = await store.CaptureAsync(payload, default);
        Assert.Equal(2, second.Revision);
        Assert.Equal(first.PayloadLength, second.PayloadLength);
        Assert.NotEqual(first.PayloadSha256, second.PayloadSha256);
        Assert.False(Directory.Exists(Path.Combine(_root, "snapshots", first.Attempt.ToString("N"), first.SnapshotDirectory)));
        Assert.Single(Directory.EnumerateDirectories(Path.Combine(_root, "snapshots", first.Attempt.ToString("N"))));
        await File.WriteAllBytesAsync(payload + ".aria2", M3ControlFixture.OnePiece(4096, true));
        var third = await store.CaptureAsync(payload, default);
        Assert.Equal(3, third.Revision);
        Assert.Equal(second.PayloadSha256, third.PayloadSha256);
        Assert.NotEqual(second.ControlSha256, third.ControlSha256);
        Assert.Equal(third, await new RangeCheckpointStore(Path.Combine(_root, "snapshots"), third.Attempt).LoadAsync(default));
        Assert.Single(Directory.EnumerateDirectories(Path.Combine(_root, "snapshots", third.Attempt.ToString("N"))));
    }
    [Fact]
    public async Task Missing_or_changed_snapshot_fails_closed_without_repairing_from_live_stage()
    {
        Directory.CreateDirectory(_root);
        var payload = Path.Combine(_root, "payload.bin");
        await File.WriteAllBytesAsync(payload, [1, 2, 3]);
        await File.WriteAllBytesAsync(payload + ".aria2", M3ControlFixture.OnePiece(3, false));
        var store = new RangeCheckpointStore(Path.Combine(_root, "snapshots"), Guid.NewGuid());
        var saved = await store.CaptureAsync(payload, default);
        await File.WriteAllBytesAsync(Path.Combine(_root, "snapshots", saved.Attempt.ToString("N"), saved.SnapshotDirectory, "payload.bin"), [1, 2, 4]);
        await Assert.ThrowsAsync<IOException>(() => store.LoadAsync(default));
        await Assert.ThrowsAsync<IOException>(() => store.CaptureAsync(payload, default));
    }
    [Fact]
    public async Task Restart_keeps_last_committed_pointer_and_prunes_uncommitted_or_old_revision_directories()
    {
        Directory.CreateDirectory(_root);
        var payload = Path.Combine(_root, "payload.bin");
        await File.WriteAllBytesAsync(payload, new byte[4096]);
        await File.WriteAllBytesAsync(payload + ".aria2", M3ControlFixture.OnePiece(4096, false));
        var attempt = Guid.NewGuid();
        var snapshotRoot = Path.Combine(_root, "snapshots");
        var store = new RangeCheckpointStore(snapshotRoot, attempt);
        var first = await store.CaptureAsync(payload, default);
        var attemptRoot = Path.Combine(snapshotRoot, attempt.ToString("N"));
        // Crash after copying a prospective revision but before its durable pointer:
        // the old pointer is authoritative and the orphan may be discarded.
        var orphan = Path.Combine(attemptRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(orphan);
        await File.WriteAllBytesAsync(Path.Combine(orphan, "payload.bin"), [99]);
        Assert.Equal(first, await new RangeCheckpointStore(snapshotRoot, attempt).LoadAsync(default));
        var restored = await new RangeCheckpointStore(snapshotRoot, attempt).RestoreCopyAsync(Path.Combine(_root, "restore"), default);
        Assert.NotNull(restored);
        Assert.False(Directory.Exists(orphan));
        Assert.Equal(new byte[4096], await File.ReadAllBytesAsync(Path.Combine(restored.Directory, restored.FileName)));
        // Crash after pointer replacement but before cleanup leaves an old directory;
        // recovery keeps the new revision and prunes the obsolete one.
        using (var stream = new FileStream(payload, FileMode.Open, FileAccess.Write))
        { stream.Position = 2048; stream.WriteByte(0x7f); }
        var second = await store.CaptureAsync(payload, default);
        var obsolete = Path.Combine(attemptRoot, first.SnapshotDirectory);
        Directory.CreateDirectory(obsolete);
        await File.WriteAllBytesAsync(Path.Combine(obsolete, "payload.bin"), [88]);
        var resumed = await new RangeCheckpointStore(snapshotRoot, attempt).RestoreCopyAsync(Path.Combine(_root, "restore"), default);
        Assert.NotNull(resumed);
        Assert.False(Directory.Exists(obsolete));
        Assert.Equal(second, await store.LoadAsync(default));
        Assert.Equal((byte)0x7f, (await File.ReadAllBytesAsync(Path.Combine(resumed.Directory, resumed.FileName)))[2048]);
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
