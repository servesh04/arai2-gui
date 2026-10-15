using AriaGui.Application.Downloads;
using AriaGui.Core.Downloads;
using AriaGui.Infrastructure.Downloads;
using AriaGui.Infrastructure.Aria2;
using System.Text.Json;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using AriaGui.Infrastructure.Persistence;

namespace AriaGui.Infrastructure.Tests;

public sealed class ContinuityPublicationTests
{
    [Theory]
    [InlineData("ancestor")]
    [InlineData("filename")]
    public async Task Private_stage_metadata_cannot_select_an_unrelated_tree_or_filename(string mutation)
    {
        var root = Path.Combine(Path.GetTempPath(), "AriaGUI.PublicationTests", Guid.NewGuid().ToString("N"));
        var output = Path.Combine(root, "output"); Directory.CreateDirectory(output);
        var other = Path.Combine(root, "unrelated"); Directory.CreateDirectory(other);
        try
        {
            await using var gateway = new ContinuityLoopbackGateway(root, () => Environment.ProcessId, true); await gateway.StartAsync(default);
            using var ledger = new ContinuityTransferLedger(root, gateway);
            var request = new EngineDownloadRequest("1234567890abcdef", new("https://example.invalid/file"), Directory: output,
                FileName: "payload.bin", ContinuityJobId: Guid.NewGuid(), ContinuityAttemptId: Guid.NewGuid());
            await ledger.PrepareAsync(request, default);
            ledger.Dispose();
            var journal = Path.Combine(root, "continuity", "transfers", request.EngineId + ".json");
            var state = JsonSerializer.Deserialize<ContinuityTransferLedger.State>(await File.ReadAllTextAsync(journal))!;
            state = mutation == "ancestor" ? state with { StageBaseDirectory = other } : state with { WorkFileName = "../escape.bin" };
            await File.WriteAllTextAsync(journal, JsonSerializer.Serialize(state));
            using var reload = new ContinuityTransferLedger(root, gateway);
            await Assert.ThrowsAsync<IOException>(() => reload.LoadAsync(default));
            Assert.Empty(Directory.EnumerateFileSystemEntries(other));
            Assert.False(File.Exists(Path.Combine(output, "payload.bin")));
        }
        finally
        {
            var allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "AriaGUI.PublicationTests")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe test cleanup");
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Published_history_has_no_route_or_tree_lease_and_reload_survives_removed_destination(bool remove)
    {
        var root = Path.Combine(Path.GetTempPath(), "AriaGUI.PublicationTests", Guid.NewGuid().ToString("N"));
        var output = Path.Combine(root, "output"); Directory.CreateDirectory(output);
        try
        {
            await using var gateway = new ContinuityLoopbackGateway(root, () => Environment.ProcessId, true); await gateway.StartAsync(default);
            using var ledger = new ContinuityTransferLedger(root, gateway);
            var job = Guid.NewGuid(); var attempt = Guid.NewGuid(); const string source = "https://example.invalid/file";
            var request = new EngineDownloadRequest("1234567890abcdef", new(source), Directory: output, FileName: "payload.bin", ContinuityJobId: job, ContinuityAttemptId: attempt);
            var routed = await ledger.PrepareAsync(request, default); var bytes = new byte[] { 1, 2, 3, 4 };
            var metadata = new HttpRepresentationStore(Path.Combine(root, "continuity"), job, attempt);
            await metadata.SaveBeforeBodyAsync(new(2, source, "", 4, "identity", false), default);
            await File.WriteAllBytesAsync(Path.Combine(routed.Directory, routed.FileName), bytes);
            await metadata.SaveCompletionAsync(new(1, 4, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))), default);
            var completed = new EngineDownload(request.EngineId, new(DownloadStatus.Completed, 4, 4, 0));
            await ledger.ObserveAsync(completed, default);
            Assert.False(ledger.CanResume(request.EngineId));
            await Assert.ThrowsAsync<IOException>(() => ledger.PrepareAsync(request, default));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(output, "payload.bin")));
            using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
            using var response = await http.GetAsync(gateway.GetAddress(attempt));
            Assert.Equal(System.Net.HttpStatusCode.Forbidden, response.StatusCode);
            var moved = output + "-moved";
            if (!Path.GetFullPath(moved).StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe fixture move");
            Directory.Move(output, moved);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(moved, "payload.bin")));
            if (remove) Directory.Delete(moved, true); // Checked synthetic child of root.
            using var reloaded = new ContinuityTransferLedger(root, gateway); await reloaded.LoadAsync(default);
            Assert.Equal(DownloadStatus.Completed, (await reloaded.ObserveAsync(completed, default)).Progress.Status);
            await Assert.ThrowsAsync<IOException>(() => reloaded.PrepareAsync(request, default));
            await Assert.ThrowsAsync<IOException>(() => reloaded.ObserveAsync(completed with { Progress = new(DownloadStatus.Downloading, 4, 4, 1) }, default));
            var entry = new DownloadEntry(new(job, "payload.bin", new(source), Path.Combine(output, "payload.bin"), DateTimeOffset.UtcNow),
                new(attempt, job, request.EngineId, DateTimeOffset.UtcNow), new(Guid.NewGuid(), job, attempt, "payload.bin", 4), new(DownloadStatus.Queued, 0, 4, 0));
            using var handler = new TerminalOwnershipHandler(request.EngineId, gateway.GetAddress(attempt).AbsoluteUri, Path.Combine(routed.Directory, routed.FileName));
            using var rpcHttp = new HttpClient(handler);
            var rpc = new Aria2RpcClient(rpcHttp, new("http://127.0.0.1/jsonrpc"), "synthetic", TimeSpan.FromSeconds(3));
            Assert.True(await reloaded.VerifySurvivorAsync(rpc, [entry], default)); // Publication may precede the SQLite Completed commit.
            handler.Active = true;
            Assert.False(await reloaded.VerifySurvivorAsync(rpc, [entry], default));
            Assert.Empty((await reloaded.PrepareRestoreAsync(root, [], default)).Entries);
        }
        finally
        {
            var allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "AriaGUI.PublicationTests")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe fixture cleanup");
            Directory.Delete(root, true);
        }
    }
    private sealed class TerminalOwnershipHandler(string gid, string source, string path) : HttpMessageHandler
    {
        internal bool Active;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var root = json.RootElement; Assert.Equal("token:synthetic", root.GetProperty("params")[0].GetString());
            object[] ids = [new { gid }];
            object result = root.GetProperty("method").GetString() switch
            {
                "aria2.tellActive" => Active ? ids : Array.Empty<object>(),
                "aria2.tellWaiting" => Array.Empty<object>(),
                "aria2.tellStopped" => Active ? Array.Empty<object>() : ids,
                "aria2.tellStatus" => new { gid, status = Active ? "active" : "complete", totalLength = "4", completedLength = "4", downloadSpeed = "0", files = new[] { new { path, uris = new[] { new { uri = source } } } } },
                _ => throw new InvalidOperationException("Unexpected synthetic RPC")
            };
            return new(System.Net.HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = root.GetProperty("id").GetString(), result }), System.Text.Encoding.UTF8, "application/json") };
        }
    }
    [Theory]
    [InlineData(@"\Device\HarddiskVolume3", true)]
    [InlineData(@"\??\C:\hidden-parent", false)]
    [InlineData(@"\Device\LanmanRedirector\server\share", false)]
    [InlineData(@"\Device\HarddiskVolume3\alias", false)]
    public void Protected_destination_requires_a_direct_local_volume_mapping(string mapping, bool valid) =>
        Assert.Equal(valid, ProtectedDestinationPolicy.IsVolumeMapping(mapping));
    [Theory]
    [InlineData("\\\\synthetic.invalid\\share\\downloads")]
    [InlineData("\\\\?\\C:\\downloads")]
    public void Protected_destination_refuses_network_and_device_names_before_IO(string path)
    {
        var error = Assert.Throws<DownloadRequestException>(() => ProtectedDestinationPolicy.Validate(path));
        Assert.Equal(ProtectedDestinationPolicy.Message, error.Message);
    }
    [Fact]
    public async Task Existing_gid_cannot_dispatch_after_its_unleased_work_directory_is_replaced()
    {
        var root = Path.Combine(Path.GetTempPath(), "AriaGUI.PublicationTests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            await using var gateway = new ContinuityLoopbackGateway(root, () => Environment.ProcessId, true); await gateway.StartAsync(default);
            using var ledger = new ContinuityTransferLedger(root, gateway);
            var request = new EngineDownloadRequest("1234567890abcdef", new("https://example.invalid/file"), Directory: root, FileName: "payload.bin",
                ContinuityJobId: Guid.NewGuid(), ContinuityAttemptId: Guid.NewGuid());
            var routed = await ledger.PrepareAsync(request, default);
            await File.WriteAllBytesAsync(Path.Combine(routed.Directory, routed.FileName), new byte[] { 1, 2, 3 });
            ledger.Dispose(); // Simulate a state whose prior lease has been released.
            var moved = routed.Directory + "-moved";
            var allowed = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(routed.Directory).StartsWith(allowed, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFullPath(moved).StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe fixture move");
            Directory.Move(routed.Directory, moved); Directory.CreateDirectory(routed.Directory);
            await Assert.ThrowsAsync<IOException>(() => ledger.PrepareAsync(request, default));
            Assert.Empty(Directory.EnumerateFiles(routed.Directory));
            Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(Path.Combine(moved, routed.FileName)));
        }
        finally
        {
            var allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "AriaGUI.PublicationTests")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe fixture cleanup");
            Directory.Delete(root, true);
        }
    }
    [Fact]
    public async Task Transfer_lease_pins_tree_between_observations_until_owner_stops()
    {
        var root = Path.Combine(Path.GetTempPath(), "AriaGUI.PublicationTests", Guid.NewGuid().ToString("N"));
        var output = Path.Combine(root, "output"); Directory.CreateDirectory(output);
        try
        {
            await using var gateway = new ContinuityLoopbackGateway(root, () => Environment.ProcessId, true); await gateway.StartAsync(default);
            using var ledger = new ContinuityTransferLedger(root, gateway);
            var request = new EngineDownloadRequest("1234567890abcdef", new("https://example.invalid/file"), Directory: output, FileName: "payload.bin",
                ContinuityJobId: Guid.NewGuid(), ContinuityAttemptId: Guid.NewGuid());
            var routed = await ledger.PrepareAsync(request, default);
            Assert.ThrowsAny<IOException>(() => Directory.Move(output, output + "-moved"));
            Assert.ThrowsAny<IOException>(() => Directory.Move(routed.Directory, routed.Directory + "-moved"));
            ledger.Dispose(); Directory.Move(output, output + "-moved");
            Assert.True(Directory.Exists(output + "-moved"));
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public void Publication_directory_chain_blocks_ancestor_replacement()
    {
        var root = Path.Combine(Path.GetTempPath(), "AriaGUI.PublicationTests", Guid.NewGuid().ToString("N"));
        var leaf = Path.Combine(root, "ancestor", "destination"); Directory.CreateDirectory(leaf);
        try
        {
            using var lease = new PublicationDirectories(leaf);
            Assert.ThrowsAny<IOException>(() => Directory.Move(Path.Combine(root, "ancestor"), Path.Combine(root, "moved")));
            Assert.ThrowsAny<IOException>(() => Directory.Move(leaf, leaf + "-moved"));
            Assert.NotEmpty(lease.Identity(leaf));
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public async Task Publication_directory_chain_refuses_an_existing_junction()
    {
        var root = Path.Combine(Path.GetTempPath(), "AriaGUI.PublicationTests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var target = Path.Combine(root, "retained"); Directory.CreateDirectory(target); var junction = Path.Combine(root, "junction");
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-Command");
            start.ArgumentList.Add($"New-Item -ItemType Junction -Path '{junction.Replace("'", "''")}' -Target '{target.Replace("'", "''")}' -ErrorAction Stop | Out-Null");
            using var process = System.Diagnostics.Process.Start(start)!; using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await process.WaitForExitAsync(deadline.Token); }
            finally { if (!process.HasExited) process.Kill(true); }
            Assert.Equal(0, process.ExitCode);
            Assert.ThrowsAny<IOException>(() => new PublicationDirectories(junction));
            Assert.Throws<DownloadRequestException>(() => ProtectedDestinationPolicy.Validate(junction));
            Assert.Empty(Directory.GetFiles(target));
        }
        finally { if (Directory.Exists(junction)) Directory.Delete(junction); Directory.Delete(root, true); }
    }
    [Fact]
    public void Pinned_child_prevents_in_place_ancestor_junction_conversion()
    {
        var root = Path.Combine(Path.GetTempPath(), "AriaGUI.PublicationTests", Guid.NewGuid().ToString("N"));
        var ancestor = Path.Combine(root, "ancestor"); var child = Path.Combine(ancestor, "pinned"); var other = Path.Combine(root, "other");
        Directory.CreateDirectory(child); Directory.CreateDirectory(other);
        try
        {
            using var lease = new PublicationDirectories(child);
            using var writer = CreateFileW(ancestor, 0x40000000, 7, IntPtr.Zero, 3, 0x02000000 | 0x00200000, IntPtr.Zero);
            Assert.False(writer.IsInvalid);
            var substitute = System.Text.Encoding.Unicode.GetBytes("\\??\\" + other); var print = System.Text.Encoding.Unicode.GetBytes(other);
            var buffer = new byte[16 + substitute.Length + 2 + print.Length + 2];
            BitConverter.GetBytes(0xA0000003u).CopyTo(buffer, 0); BitConverter.GetBytes((ushort)(buffer.Length - 8)).CopyTo(buffer, 4);
            BitConverter.GetBytes((ushort)substitute.Length).CopyTo(buffer, 10); BitConverter.GetBytes((ushort)(substitute.Length + 2)).CopyTo(buffer, 12);
            BitConverter.GetBytes((ushort)print.Length).CopyTo(buffer, 14); substitute.CopyTo(buffer, 16); print.CopyTo(buffer, 18 + substitute.Length);
            Assert.False(DeviceIoControl(writer, 0x900A4, buffer, buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero));
            Assert.Equal(145, Marshal.GetLastWin32Error()); // ERROR_DIR_NOT_EMPTY; a pinned child cannot be removed.
        }
        finally { Directory.Delete(root, true); }
    }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, int inputSize, IntPtr output, int outputSize, out uint returned, IntPtr overlapped);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [Fact]
    public async Task Verified_legacy_retry_starts_empty_and_keeps_original_pair_on_failure()
    {
        var root = Path.Combine(Path.GetTempPath(), "AriaGUI.PublicationTests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            await using var gateway = new ContinuityLoopbackGateway(root, () => Environment.ProcessId, true); await gateway.StartAsync(default);
            var target = Path.Combine(root, "payload.bin"); var payload = new byte[] { 1, 2 }; var control = new byte[] { 9, 8 };
            await File.WriteAllBytesAsync(target, payload); await File.WriteAllBytesAsync(target + ".aria2", control);
            using var ledger = new ContinuityTransferLedger(root, gateway);
            var request = new EngineDownloadRequest("1234567890abcdef", new("https://example.invalid/file"), Directory: root, FileName: "payload.bin",
                ContinueExisting: true, ContinuityJobId: Guid.NewGuid(), ContinuityAttemptId: Guid.NewGuid(), LegacyParentEngineId: "abcdef1234567890");
            await Assert.ThrowsAnyAsync<IOException>(() => ledger.PrepareAsync(request, default));
            var routed = await ledger.PrepareAsync(request, default, verifiedLegacyFresh: true);
            Assert.False(routed.ContinueExisting); Assert.Empty(Directory.GetFiles(routed.Directory));
            await ledger.ObserveAsync(new(request.EngineId, new(DownloadStatus.Failed, 0, null, 0)), default);
            Assert.Equal(payload, await File.ReadAllBytesAsync(target)); Assert.Equal(control, await File.ReadAllBytesAsync(target + ".aria2"));
        }
        finally { Directory.Delete(root, true); }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Crash_replay_refuses_replaced_destination_directory_even_with_identical_content(bool requiredChecksum)
    {
        var root = Path.Combine(Path.GetTempPath(), "AriaGUI.PublicationTests", Guid.NewGuid().ToString("N"));
        var targetDirectory = Path.Combine(root, "destination"); Directory.CreateDirectory(targetDirectory);
        try
        {
            await using var gateway = new ContinuityLoopbackGateway(root, () => Environment.ProcessId, true); await gateway.StartAsync(default);
            var job = Guid.NewGuid(); var attempt = Guid.NewGuid(); using var ledger = new ContinuityTransferLedger(root, gateway, phase =>
            { if (phase == "completion-prepared") throw new IOException("Crash"); });
            var bytes = new byte[] { 1, 2, 3, 4 };
            var request = new EngineDownloadRequest("1234567890abcdef", new("https://example.invalid/file"), Directory: targetDirectory, FileName: "payload.bin",
                ContinuityJobId: job, ContinuityAttemptId: attempt,
                ExpectedChecksum: requiredChecksum ? ExpectedChecksum.FromManualInput(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))) : null);
            var routed = await ledger.PrepareAsync(request, default);
            await new HttpRepresentationStore(Path.Combine(root, "continuity"), job, attempt).SaveBeforeBodyAsync(new(1, request.Source.AbsoluteUri, "\"strong\"", 4, "identity"), default);
            await File.WriteAllBytesAsync(Path.Combine(routed.Directory, "payload.bin"), bytes);
            await Assert.ThrowsAsync<IOException>(() => ObserveSettledAsync(ledger, request.EngineId));
            ledger.Dispose(); Directory.Move(targetDirectory, targetDirectory + "-retained"); Directory.CreateDirectory(targetDirectory);
            var oldStage = Path.Combine(targetDirectory + "-retained", ".aria-gui-continuity-" + job.ToString("N"));
            var newWork = Path.Combine(targetDirectory, ".aria-gui-continuity-" + job.ToString("N"), attempt.ToString("N")); Directory.CreateDirectory(newWork);
            await File.WriteAllBytesAsync(Path.Combine(newWork, "payload.bin"), bytes);
            await Assert.ThrowsAnyAsync<IOException>(() => new ContinuityTransferLedger(root, gateway).LoadAsync(default));
            Assert.False(File.Exists(Path.Combine(targetDirectory, "payload.bin")));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(oldStage, attempt.ToString("N"), "payload.bin")));
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public async Task Identified_rename_moves_the_locked_object_to_exact_destination()
    {
        var root = Path.Combine(Path.GetTempPath(), "AriaGUI.PublicationTests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "source.bin"); var target = Path.Combine(root, "target.bin");
            await File.WriteAllBytesAsync(source, new byte[] { 1 });
            using var directories = new PublicationDirectories(root);
            using (var file = new IdentifiedFile(source)) file.RenameWithoutReplacement(target);
            Assert.True(File.Exists(target), string.Join(",", Directory.GetFiles(root).Select(Path.GetFileName)));
            Assert.False(File.Exists(source));
        }
        finally
        {
            var allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "AriaGUI.PublicationTests")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe fixture cleanup");
            Directory.Delete(root, true);
        }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Publication_blocks_object_mutation_and_preserves_name_collision(bool collision)
    {
        var root = Path.Combine(Path.GetTempPath(), "AriaGUI.PublicationTests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            await using var gateway = new ContinuityLoopbackGateway(root, () => Environment.ProcessId, true); await gateway.StartAsync(default);
            var job = Guid.NewGuid(); var attempt = Guid.NewGuid(); var target = Path.Combine(root, "payload.bin"); var injected = false;
            using var ledger = new ContinuityTransferLedger(root, gateway, phase =>
            {
                if (phase == "completion-checked")
                {
                    Assert.ThrowsAny<IOException>(() => File.WriteAllBytes(target, new byte[] { 9 }));
                    Assert.ThrowsAny<IOException>(() => File.Move(target, target + ".other"));
                    injected = true;
                }
                if (collision && phase == "completion-retained-moved") File.WriteAllBytes(target, new byte[] { 9 });
            });
            var request = new EngineDownloadRequest("1234567890abcdef", new("https://example.invalid/file"), Directory: root, FileName: "payload.bin", ContinuityJobId: job, ContinuityAttemptId: attempt);
            var routed = await ledger.PrepareAsync(request, default); var work = Path.Combine(routed.Directory, routed.FileName);
            await new HttpRepresentationStore(Path.Combine(root, "continuity"), job, attempt).SaveBeforeBodyAsync(new(1, request.Source.AbsoluteUri, "\"strong\"", 4, "identity"), default);
            await File.WriteAllBytesAsync(work, new byte[] { 1, 2 }); await File.WriteAllBytesAsync(work + ".aria2", new byte[] { 7, 8 });
            await ledger.ObserveAsync(new(request.EngineId, new(DownloadStatus.Paused, 2, 4, 0)), default);
            await File.WriteAllBytesAsync(work, new byte[] { 1, 2, 3, 4 });
            var complete = new EngineDownload(request.EngineId, new(DownloadStatus.Completed, 4, 4, 0));
            if (collision)
            {
                await Assert.ThrowsAnyAsync<IOException>(() => ledger.ObserveAsync(complete, default));
                Assert.Equal(new byte[] { 9 }, await File.ReadAllBytesAsync(target));
                Assert.Equal(new byte[] { 1, 2 }, await File.ReadAllBytesAsync(Path.Combine(Path.GetDirectoryName(target)!, ".aria-gui-continuity-" + job.ToString("N"), "retained-" + attempt.ToString("N") + ".payload")));
                await Assert.ThrowsAnyAsync<IOException>(() => new ContinuityTransferLedger(root, gateway).LoadAsync(default));
                Assert.Equal(new byte[] { 9 }, await File.ReadAllBytesAsync(target));
            }
            else { Assert.Equal(DownloadStatus.Completed, (await ledger.ObserveAsync(complete, default)).Progress.Status); Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(target)); }
            Assert.True(injected);
        }
        finally
        {
            var allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "AriaGUI.PublicationTests")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe fixture cleanup");
            Directory.Delete(root, true);
        }
    }
    [Fact]
    public async Task Guarded_restore_reapplies_pending_move_and_never_imports_legacy_source()
    {
        var root = Path.Combine(Path.GetTempPath(), "AriaGUI.PublicationTests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            await using var gateway = new ContinuityLoopbackGateway(root, () => Environment.ProcessId, true); await gateway.StartAsync(default);
            using var ledger = new ContinuityTransferLedger(root, gateway); var entries = new List<DownloadEntry>();
            for (var index = 1; index <= 3; index++)
            {
                var job = Guid.NewGuid(); var attempt = Guid.NewGuid(); var gid = index.ToString("x16"); var name = index + ".bin";
                var source = new Uri("https://example.invalid/" + name); var destination = Path.Combine(root, name);
                var entry = new DownloadEntry(new(job, name, source, destination, DateTimeOffset.UtcNow), new(attempt, job, gid, DateTimeOffset.UtcNow),
                    new(Guid.NewGuid(), job, attempt, name, null), new(DownloadStatus.Queued, 0, null, 0),
                    index == 2 ? new(Guid.NewGuid(), job, DownloadAction.Move, OperationStatus.Pending, DateTimeOffset.UtcNow, 0) : null);
                entries.Add(entry);
                if (index < 3) await ledger.PrepareAsync(new(gid, source, Directory: root, FileName: name, ContinuityJobId: job, ContinuityAttemptId: attempt), default);
            }
            var plan = await ledger.PrepareRestoreAsync(root, entries, default);
            Assert.Equal(new[] { entries[1].Attempt.EngineId, entries[0].Attempt.EngineId }, plan.Entries.Select(row => row.Attempt.EngineId));
            var input = await File.ReadAllLinesAsync(plan.InputFile!);
            Assert.Equal(plan.Entries.Select(row => " gid=" + row.Attempt.EngineId), input.Where(line => line.StartsWith(" gid=", StringComparison.Ordinal)));
            Assert.DoesNotContain(input, line => line.StartsWith("https://", StringComparison.Ordinal));
            Assert.All(input.Where(line => line.StartsWith("http://", StringComparison.Ordinal)), line => Assert.StartsWith("http://127.0.0.1:", line));
        }
        finally
        {
            var allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "AriaGUI.PublicationTests")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe fixture cleanup");
            Directory.Delete(root, true);
        }
    }
    [Theory]
    [InlineData("completion-prepared", false)]
    [InlineData("completion-moved", false)]
    [InlineData("control-cleaned", false)]
    [InlineData("completion-prepared", true)]
    [InlineData("completion-moved", true)]
    [InlineData("control-cleaned", true)]
    [InlineData("completion-retained-moved", true)]
    public async Task Durable_completion_replays_after_each_publication_boundary(string phase, bool retained)
    {
        var root = Path.Combine(Path.GetTempPath(), "AriaGUI.PublicationTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var gateway = new ContinuityLoopbackGateway(root, () => Environment.ProcessId, true);
            await gateway.StartAsync(default);
            var job = Guid.NewGuid(); var attempt = Guid.NewGuid(); const string source = "https://example.invalid/file";
            using var ledger = new ContinuityTransferLedger(root, gateway, point => { if (point == phase) throw new IOException("Injected crash boundary"); });
            var request = new EngineDownloadRequest("1234567890abcdef", new(source), Directory: root, FileName: "payload.bin", ContinuityJobId: job, ContinuityAttemptId: attempt);
            var routed = await ledger.PrepareAsync(request, default);
            var bytes = new byte[] { 1, 2, 3, 4 };
            var work = Path.Combine(routed.Directory, routed.FileName);
            var metadata = new HttpRepresentationStore(Path.Combine(root, "continuity"), job, attempt);
            if (retained)
            {
                await metadata.SaveBeforeBodyAsync(new(1, source, "\"strong\"", 4, "identity"), default);
                await File.WriteAllBytesAsync(work, bytes[..2]);
                await File.WriteAllBytesAsync(work + ".aria2", new byte[] { 7, 8 });
                await ledger.ObserveAsync(new(request.EngineId, new(DownloadStatus.Paused, 2, 4, 0)), default);
                Assert.Equal(bytes[..2], await File.ReadAllBytesAsync(Path.Combine(root, "payload.bin")));
            }
            else await metadata.SaveBeforeBodyAsync(new(2, source, "", 4, "identity", false), default);
            await File.WriteAllBytesAsync(work, bytes);
            await metadata.SaveCompletionAsync(new(1, 4, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))), default);
            await Assert.ThrowsAsync<IOException>(() => ledger.ObserveAsync(new(request.EngineId, new(DownloadStatus.Completed, 4, 4, 0)), default));
            using var recovered = new ContinuityTransferLedger(root, gateway); await recovered.LoadAsync(default);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(root, "payload.bin")));
            Assert.False(File.Exists(Path.Combine(root, "payload.bin.aria2")));
            var observed = await recovered.ObserveAsync(new(request.EngineId, new(DownloadStatus.Completed, 4, 4, 0)), default);
            Assert.Equal(DownloadStatus.Completed, observed.Progress.Status);
        }
        finally
        {
            var allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "AriaGUI.PublicationTests")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe fixture cleanup");
            Directory.Delete(root, true);
        }
    }
    [Fact]
    public async Task Required_checksum_mismatch_keeps_retained_payload_and_control()
    {
        var root = Path.Combine(Path.GetTempPath(), "AriaGUI.PublicationTests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            await using var gateway = new ContinuityLoopbackGateway(root, () => Environment.ProcessId, true); await gateway.StartAsync(default);
            var job = Guid.NewGuid(); var attempt = Guid.NewGuid();
            using var ledger = new ContinuityTransferLedger(root, gateway);
            var bytes = new byte[] { 1, 2, 3, 4 }; var expectedBytes = new byte[] { 1, 2, 3, 5 };
            var request = new EngineDownloadRequest("1234567890abcdef", new("https://example.invalid/file"), Directory: root, FileName: "payload.bin",
                ContinuityJobId: job, ContinuityAttemptId: attempt,
                ExpectedChecksum: ExpectedChecksum.FromManualInput(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(expectedBytes))));
            var routed = await ledger.PrepareAsync(request, default);
            var control = new byte[] { 9, 8 };
            await new HttpRepresentationStore(Path.Combine(root, "continuity"), job, attempt).SaveBeforeBodyAsync(new(1, request.Source.AbsoluteUri, "\"strong\"", 4, "identity"), default);
            await File.WriteAllBytesAsync(Path.Combine(routed.Directory, routed.FileName), bytes);
            var target = Path.Combine(root, "payload.bin"); await File.WriteAllBytesAsync(target, new byte[] { 7 }); await File.WriteAllBytesAsync(target + ".aria2", control);
            var observed = await ObserveSettledAsync(ledger, request.EngineId);
            Assert.Equal(DownloadStatus.Failed, observed.Progress.Status); Assert.Equal(VerificationStatus.Mismatch, observed.Verification);
            Assert.Equal(new byte[] { 7 }, await File.ReadAllBytesAsync(target)); Assert.Equal(control, await File.ReadAllBytesAsync(target + ".aria2"));
            using var recovered = new ContinuityTransferLedger(root, gateway); await recovered.LoadAsync(default);
            var replay = await recovered.ObserveAsync(new(request.EngineId, new(DownloadStatus.Completed, 4, 4, 0)), default);
            Assert.Equal(DownloadStatus.Failed, replay.Progress.Status); Assert.Equal(VerificationStatus.Mismatch, replay.Verification);
        }
        finally { Directory.Delete(root, true); }
    }
    [Theory]
    [InlineData("verification-confirmed")]
    [InlineData("completion-prepared")]
    [InlineData("completion-moved")]
    public async Task Required_checksum_replays_verification_and_publication_crashes(string phase)
    {
        var root = Path.Combine(Path.GetTempPath(), "AriaGUI.PublicationTests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            await using var gateway = new ContinuityLoopbackGateway(root, () => Environment.ProcessId, true); await gateway.StartAsync(default);
            var job = Guid.NewGuid(); var attempt = Guid.NewGuid(); var bytes = new byte[] { 1, 2, 3, 4 };
            using var ledger = new ContinuityTransferLedger(root, gateway, point => { if (point == phase) throw new IOException("Crash"); });
            var request = new EngineDownloadRequest("1234567890abcdef", new("https://example.invalid/file"), Directory: root, FileName: "payload.bin",
                ContinuityJobId: job, ContinuityAttemptId: attempt,
                ExpectedChecksum: ExpectedChecksum.FromManualInput(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))));
            var routed = await ledger.PrepareAsync(request, default);
            await new HttpRepresentationStore(Path.Combine(root, "continuity"), job, attempt).SaveBeforeBodyAsync(new(1, request.Source.AbsoluteUri, "\"strong\"", 4, "identity"), default);
            await File.WriteAllBytesAsync(Path.Combine(routed.Directory, routed.FileName), bytes);
            await Assert.ThrowsAsync<IOException>(() => ObserveSettledAsync(ledger, request.EngineId));
            ledger.Dispose();
            using var recovered = new ContinuityTransferLedger(root, gateway); await recovered.LoadAsync(default);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(root, "payload.bin")));
            var observed = await recovered.ObserveAsync(new(request.EngineId, new(DownloadStatus.Completed, 4, 4, 0)), default);
            Assert.Equal(DownloadStatus.Completed, observed.Progress.Status); Assert.Equal(VerificationStatus.Verified, observed.Verification);
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public async Task Verified_receipt_cannot_publish_replaced_stage_even_with_identical_bytes()
    {
        var root = Path.Combine(Path.GetTempPath(), "AriaGUI.PublicationTests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            await using var gateway = new ContinuityLoopbackGateway(root, () => Environment.ProcessId, true); await gateway.StartAsync(default);
            var job = Guid.NewGuid(); var attempt = Guid.NewGuid(); var bytes = new byte[] { 1, 2, 3, 4 };
            using var ledger = new ContinuityTransferLedger(root, gateway, point => { if (point == "verification-confirmed") throw new IOException("Crash"); });
            var request = new EngineDownloadRequest("1234567890abcdef", new("https://example.invalid/file"), Directory: root, FileName: "payload.bin",
                ContinuityJobId: job, ContinuityAttemptId: attempt,
                ExpectedChecksum: ExpectedChecksum.FromManualInput(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))));
            var routed = await ledger.PrepareAsync(request, default);
            await new HttpRepresentationStore(Path.Combine(root, "continuity"), job, attempt).SaveBeforeBodyAsync(new(1, request.Source.AbsoluteUri, "\"strong\"", 4, "identity"), default);
            var work = Path.Combine(routed.Directory, routed.FileName); await File.WriteAllBytesAsync(work, bytes);
            await Assert.ThrowsAsync<IOException>(() => ObserveSettledAsync(ledger, request.EngineId));
            ledger.Dispose(); File.Move(work, work + ".retained"); await File.WriteAllBytesAsync(work, bytes);
            await Assert.ThrowsAnyAsync<IOException>(() => new ContinuityTransferLedger(root, gateway).LoadAsync(default));
            Assert.False(File.Exists(Path.Combine(root, "payload.bin")));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(work + ".retained"));
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public async Task Startup_replay_refuses_journal_whose_required_digest_conflicts_with_database()
    {
        var root = Path.Combine(Path.GetTempPath(), "AriaGUI.PublicationTests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            await using var gateway = new ContinuityLoopbackGateway(root, () => Environment.ProcessId, true); await gateway.StartAsync(default);
            var job = Guid.NewGuid(); var attempt = Guid.NewGuid(); var gid = "1234567890abcdef"; var now = DateTimeOffset.UtcNow;
            var bytes = new byte[] { 1, 2, 3, 4 }; var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
            var repository = new SqliteDownloadRepository(Path.Combine(root, "downloads.db")); await repository.InitializeAsync(default);
            await repository.InsertIntentAsync(new(new(job, "payload.bin", new("https://example.invalid/file"), Path.Combine(root, "payload.bin"), now,
                    ExpectedChecksum.FromManualInput(new string('0', 64))),
                new(attempt, job, gid, now), new(Guid.NewGuid(), job, attempt, "payload.bin", 4),
                new(DownloadStatus.Queued, 0, 4, 0), new(Guid.NewGuid(), job, DownloadAction.Add, OperationStatus.Pending, now),
                Verification: VerificationStatus.Pending), default);
            using var ledger = new ContinuityTransferLedger(root, gateway, point => { if (point == "completion-prepared") throw new IOException("Crash"); });
            var request = new EngineDownloadRequest(gid, new("https://example.invalid/file"), Directory: root, FileName: "payload.bin",
                ContinuityJobId: job, ContinuityAttemptId: attempt, ExpectedChecksum: ExpectedChecksum.FromManualInput(hash));
            var routed = await ledger.PrepareAsync(request, default);
            await new HttpRepresentationStore(Path.Combine(root, "continuity"), job, attempt).SaveBeforeBodyAsync(new(1, request.Source.AbsoluteUri, "\"strong\"", 4, "identity"), default);
            await File.WriteAllBytesAsync(Path.Combine(routed.Directory, routed.FileName), bytes);
            await Assert.ThrowsAsync<IOException>(() => ObserveSettledAsync(ledger, gid));
            ledger.Dispose();
            await Assert.ThrowsAnyAsync<IOException>(() => new ContinuityTransferLedger(root, gateway).LoadAsync(default, repository));
            Assert.False(File.Exists(Path.Combine(root, "payload.bin")));
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public async Task Moved_receipt_cannot_accept_same_byte_replacement_before_commit()
    {
        var root = Path.Combine(Path.GetTempPath(), "AriaGUI.PublicationTests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            await using var gateway = new ContinuityLoopbackGateway(root, () => Environment.ProcessId, true); await gateway.StartAsync(default);
            var job = Guid.NewGuid(); var attempt = Guid.NewGuid(); var bytes = new byte[] { 1, 2, 3, 4 };
            using var ledger = new ContinuityTransferLedger(root, gateway, point => { if (point == "completion-moved") throw new IOException("Crash"); });
            var request = new EngineDownloadRequest("1234567890abcdef", new("https://example.invalid/file"), Directory: root, FileName: "payload.bin",
                ContinuityJobId: job, ContinuityAttemptId: attempt,
                ExpectedChecksum: ExpectedChecksum.FromManualInput(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))));
            var routed = await ledger.PrepareAsync(request, default);
            await new HttpRepresentationStore(Path.Combine(root, "continuity"), job, attempt).SaveBeforeBodyAsync(new(1, request.Source.AbsoluteUri, "\"strong\"", 4, "identity"), default);
            await File.WriteAllBytesAsync(Path.Combine(routed.Directory, routed.FileName), bytes);
            await Assert.ThrowsAsync<IOException>(() => ObserveSettledAsync(ledger, request.EngineId));
            ledger.Dispose(); var target = Path.Combine(root, "payload.bin"); File.Move(target, target + ".retained"); await File.WriteAllBytesAsync(target, bytes);
            await Assert.ThrowsAnyAsync<IOException>(() => new ContinuityTransferLedger(root, gateway).LoadAsync(default));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(target + ".retained"));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(target));
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public async Task Digest_mismatch_after_retained_partial_repair_starts_empty_and_keeps_old_pair()
    {
        var root = Path.Combine(Path.GetTempPath(), "AriaGUI.PublicationTests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            await using var gateway = new ContinuityLoopbackGateway(root, () => Environment.ProcessId, true); await gateway.StartAsync(default);
            var job = Guid.NewGuid(); var first = Guid.NewGuid(); var next = Guid.NewGuid();
            using var ledger = new ContinuityTransferLedger(root, gateway);
            var expected = ExpectedChecksum.FromManualInput(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(new byte[] { 1, 2, 3, 5 })));
            var original = new EngineDownloadRequest("1234567890abcdef", new("https://example.invalid/file"), Directory: root, FileName: "payload.bin",
                ContinuityJobId: job, ContinuityAttemptId: first, ExpectedChecksum: expected);
            var routed = await ledger.PrepareAsync(original, default); var work = Path.Combine(routed.Directory, routed.FileName);
            await new HttpRepresentationStore(Path.Combine(root, "continuity"), job, first).SaveBeforeBodyAsync(new(1, original.Source.AbsoluteUri, "\"strong\"", 4, "identity"), default);
            await File.WriteAllBytesAsync(work, new byte[] { 1, 2 }); await File.WriteAllBytesAsync(work + ".aria2", new byte[] { 7, 8 });
            await ledger.ObserveAsync(new(original.EngineId, new(DownloadStatus.Paused, 2, 4, 0)), default);
            var target = Path.Combine(root, "payload.bin"); var oldPayload = await File.ReadAllBytesAsync(target); var oldControl = await File.ReadAllBytesAsync(target + ".aria2");
            await File.WriteAllBytesAsync(work, new byte[] { 1, 2, 3, 4 });
            var failed = await ObserveSettledAsync(ledger, original.EngineId);
            Assert.Equal(VerificationStatus.Mismatch, failed.Verification);
            var retry = original with { EngineId = "abcdef1234567890", ContinuityAttemptId = next };
            var fresh = await ledger.PrepareAsync(retry, default);
            Assert.False(fresh.ContinueExisting); Assert.Empty(Directory.GetFiles(fresh.Directory));
            Assert.Equal(oldPayload, await File.ReadAllBytesAsync(target)); Assert.Equal(oldControl, await File.ReadAllBytesAsync(target + ".aria2"));
        }
        finally { Directory.Delete(root, true); }
    }
    [Theory]
    [InlineData("verification-confirmed")]
    [InlineData("completion-prepared")]
    public async Task Disabled_verification_holds_required_publication_checkpoint(string phase)
    {
        var root = Path.Combine(Path.GetTempPath(), "AriaGUI.PublicationTests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            await using var gateway = new ContinuityLoopbackGateway(root, () => Environment.ProcessId, true); await gateway.StartAsync(default);
            var job = Guid.NewGuid(); var attempt = Guid.NewGuid(); var bytes = new byte[] { 1, 2, 3, 4 };
            using var ledger = new ContinuityTransferLedger(root, gateway, point => { if (point == phase) throw new IOException("Crash"); });
            var request = new EngineDownloadRequest("1234567890abcdef", new("https://example.invalid/file"), Directory: root, FileName: "payload.bin",
                ContinuityJobId: job, ContinuityAttemptId: attempt,
                ExpectedChecksum: ExpectedChecksum.FromManualInput(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))));
            var routed = await ledger.PrepareAsync(request, default);
            await new HttpRepresentationStore(Path.Combine(root, "continuity"), job, attempt).SaveBeforeBodyAsync(new(1, request.Source.AbsoluteUri, "\"strong\"", 4, "identity"), default);
            await File.WriteAllBytesAsync(Path.Combine(routed.Directory, routed.FileName), bytes);
            await Assert.ThrowsAsync<IOException>(() => ObserveSettledAsync(ledger, request.EngineId));
            ledger.Dispose();
            using (var disabled = new ContinuityTransferLedger(root, gateway))
            {
                await disabled.LoadAsync(default, enableExpectedChecksumVerification: false);
                var held = await disabled.ObserveAsync(new(request.EngineId, new(DownloadStatus.Completed, 4, 4, 0)), default);
                Assert.Equal(DownloadStatus.Interrupted, held.Progress.Status);
                Assert.Equal(VerificationStatus.Pending, held.Verification);
            }
            Assert.False(File.Exists(Path.Combine(root, "payload.bin")));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(routed.Directory, routed.FileName)));
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public async Task Large_required_hash_is_visible_responsive_and_cancellable_without_publication()
    {
        var root = Path.Combine(Path.GetTempPath(), "AriaGUI.PublicationTests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var bytes = new byte[32 * 1024 * 1024]; Random.Shared.NextBytes(bytes);
            var reachedChunk = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var callbacks = 0;
            Task HoldFirstChunk(long _, CancellationToken token)
            {
                if (Interlocked.Increment(ref callbacks) != 1) return Task.CompletedTask;
                reachedChunk.TrySetResult();
                return Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            await using var gateway = new ContinuityLoopbackGateway(root, () => Environment.ProcessId, true); await gateway.StartAsync(default);
            using var ledger = new ContinuityTransferLedger(root, gateway, hashProgress: HoldFirstChunk);
            var job = Guid.NewGuid(); var attempt = Guid.NewGuid();
            var request = new EngineDownloadRequest("1234567890abcdef", new("https://example.invalid/large"), Directory: root, FileName: "payload.bin",
                ContinuityJobId: job, ContinuityAttemptId: attempt,
                ExpectedChecksum: ExpectedChecksum.FromManualInput(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))));
            var routed = await ledger.PrepareAsync(request, default);
            await new HttpRepresentationStore(Path.Combine(root, "continuity"), job, attempt).SaveBeforeBodyAsync(new(1, request.Source.AbsoluteUri, "\"strong\"", bytes.Length, "identity"), default);
            await File.WriteAllBytesAsync(Path.Combine(routed.Directory, routed.FileName), bytes);
            using var cancellation = new CancellationTokenSource();
            var completed = new EngineDownload(request.EngineId, new(DownloadStatus.Completed, bytes.Length, bytes.Length, 0));
            var first = await ledger.ObserveAsync(completed, cancellation.Token);
            Assert.Equal(VerificationStatus.Verifying, first.Verification);
            await reachedChunk.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var concurrent = await ledger.ObserveAsync(completed, default);
            stopwatch.Stop();
            Assert.Equal(VerificationStatus.Verifying, concurrent.Verification);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(250), $"Observation blocked for {stopwatch.Elapsed}.");
            cancellation.Cancel();
            Assert.False(File.Exists(Path.Combine(root, "payload.bin")));
            Assert.True(File.Exists(Path.Combine(routed.Directory, routed.FileName)));
            var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
            EngineDownload result;
            do
            {
                result = await ledger.ObserveAsync(completed, default);
                if (result.Verification == VerificationStatus.Verifying) await Task.Delay(10);
            } while (result.Verification == VerificationStatus.Verifying && DateTimeOffset.UtcNow < deadline);
            Assert.Equal(VerificationStatus.Verified, result.Verification);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(root, "payload.bin")));
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public async Task Cancel_before_required_hash_keeps_stage_and_allows_later_verification()
    {
        var root = Path.Combine(Path.GetTempPath(), "AriaGUI.PublicationTests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            await using var gateway = new ContinuityLoopbackGateway(root, () => Environment.ProcessId, true); await gateway.StartAsync(default);
            using var ledger = new ContinuityTransferLedger(root, gateway);
            var bytes = new byte[] { 1, 2, 3, 4 }; var job = Guid.NewGuid(); var attempt = Guid.NewGuid();
            var request = new EngineDownloadRequest("1234567890abcdef", new("https://example.invalid/file"), Directory: root, FileName: "payload.bin",
                ContinuityJobId: job, ContinuityAttemptId: attempt,
                ExpectedChecksum: ExpectedChecksum.FromManualInput(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))));
            var routed = await ledger.PrepareAsync(request, default);
            await new HttpRepresentationStore(Path.Combine(root, "continuity"), job, attempt).SaveBeforeBodyAsync(new(1, request.Source.AbsoluteUri, "\"strong\"", bytes.Length, "identity"), default);
            await File.WriteAllBytesAsync(Path.Combine(routed.Directory, routed.FileName), bytes);
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            try { await ledger.ObserveAsync(new(request.EngineId, new(DownloadStatus.Completed, 4, 4, 0)), cancellation.Token); }
            catch (OperationCanceledException) { }
            Assert.False(File.Exists(Path.Combine(root, "payload.bin")));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(routed.Directory, routed.FileName)));
            var completed = await ObserveSettledAsync(ledger, request.EngineId);
            Assert.Equal(VerificationStatus.Verified, completed.Verification);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(root, "payload.bin")));
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public async Task Storage_failure_before_verification_checkpoint_keeps_stage_and_can_retry()
    {
        var root = Path.Combine(Path.GetTempPath(), "AriaGUI.PublicationTests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var faults = 0;
            await using var gateway = new ContinuityLoopbackGateway(root, () => Environment.ProcessId, true); await gateway.StartAsync(default);
            using var ledger = new ContinuityTransferLedger(root, gateway, point =>
            {
                if (point == "verification-before-checkpoint" && Interlocked.Increment(ref faults) == 1)
                    throw new IOException("Injected storage exhaustion before journal checkpoint.");
            });
            var bytes = new byte[] { 1, 2, 3, 4 }; var job = Guid.NewGuid(); var attempt = Guid.NewGuid();
            var request = new EngineDownloadRequest("1234567890abcdef", new("https://example.invalid/file"), Directory: root, FileName: "payload.bin",
                ContinuityJobId: job, ContinuityAttemptId: attempt,
                ExpectedChecksum: ExpectedChecksum.FromManualInput(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))));
            var routed = await ledger.PrepareAsync(request, default);
            await new HttpRepresentationStore(Path.Combine(root, "continuity"), job, attempt).SaveBeforeBodyAsync(new(1, request.Source.AbsoluteUri, "\"strong\"", bytes.Length, "identity"), default);
            await File.WriteAllBytesAsync(Path.Combine(routed.Directory, routed.FileName), bytes);
            await Assert.ThrowsAsync<IOException>(() => ObserveSettledAsync(ledger, request.EngineId));
            Assert.False(File.Exists(Path.Combine(root, "payload.bin")));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(routed.Directory, routed.FileName)));
            var completed = await ObserveSettledAsync(ledger, request.EngineId);
            Assert.Equal(VerificationStatus.Verified, completed.Verification);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(root, "payload.bin")));
        }
        finally { Directory.Delete(root, true); }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Hash_read_fault_keeps_stage_and_can_retry_same_attempt(bool accessDenied)
    {
        var root = Path.Combine(Path.GetTempPath(), "AriaGUI.PublicationTests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var faults = 0;
            Task FaultOnce(long _, CancellationToken __)
            {
                if (Interlocked.Increment(ref faults) == 1)
                {
                    if (accessDenied) throw new UnauthorizedAccessException("Injected read denial.");
                    throw new IOException("Injected storage read failure.");
                }
                return Task.CompletedTask;
            }
            await using var gateway = new ContinuityLoopbackGateway(root, () => Environment.ProcessId, true); await gateway.StartAsync(default);
            using var ledger = new ContinuityTransferLedger(root, gateway, hashProgress: FaultOnce);
            var job = Guid.NewGuid(); var attempt = Guid.NewGuid(); var bytes = new byte[] { 1, 2, 3, 4 };
            var request = new EngineDownloadRequest("1234567890abcdef", new("https://example.invalid/file"), Directory: root, FileName: "payload.bin",
                ContinuityJobId: job, ContinuityAttemptId: attempt,
                ExpectedChecksum: ExpectedChecksum.FromManualInput(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))));
            var routed = await ledger.PrepareAsync(request, default);
            await new HttpRepresentationStore(Path.Combine(root, "continuity"), job, attempt).SaveBeforeBodyAsync(new(1, request.Source.AbsoluteUri, "\"strong\"", 4, "identity"), default);
            await File.WriteAllBytesAsync(Path.Combine(routed.Directory, routed.FileName), bytes);
            if (accessDenied) await Assert.ThrowsAsync<UnauthorizedAccessException>(() => ObserveSettledAsync(ledger, request.EngineId));
            else await Assert.ThrowsAsync<IOException>(() => ObserveSettledAsync(ledger, request.EngineId));
            Assert.False(File.Exists(Path.Combine(root, "payload.bin")));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(routed.Directory, routed.FileName)));
            var recovered = await ObserveSettledAsync(ledger, request.EngineId);
            Assert.Equal(VerificationStatus.Verified, recovered.Verification);
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public async Task Synthetic_published_receipt_requires_bound_attempt_and_original_public_file()
    {
        var root = Path.Combine(Path.GetTempPath(), "AriaGUI.PublicationTests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            await using var gateway = new ContinuityLoopbackGateway(root, () => Environment.ProcessId, true); await gateway.StartAsync(default);
            using var ledger = new ContinuityTransferLedger(root, gateway);
            var job = Guid.NewGuid(); var attempt = Guid.NewGuid(); var bytes = new byte[] { 1, 2, 3, 4 }; var now = DateTimeOffset.UtcNow;
            var expected = ExpectedChecksum.FromManualInput(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)));
            var request = new EngineDownloadRequest("1234567890abcdef", new("https://example.invalid/file"), Directory: root, FileName: "payload.bin",
                ContinuityJobId: job, ContinuityAttemptId: attempt, ExpectedChecksum: expected);
            var routed = await ledger.PrepareAsync(request, default);
            await new HttpRepresentationStore(Path.Combine(root, "continuity"), job, attempt).SaveBeforeBodyAsync(new(1, request.Source.AbsoluteUri, "\"strong\"", 4, "identity"), default);
            await File.WriteAllBytesAsync(Path.Combine(routed.Directory, routed.FileName), bytes);
            Assert.Equal(DownloadStatus.Completed, (await ObserveSettledAsync(ledger, request.EngineId)).Progress.Status);
            var entry = new DownloadEntry(new(job, "payload.bin", request.Source, Path.Combine(root, "payload.bin"), now, expected),
                new(attempt, job, request.EngineId, now), new(Guid.NewGuid(), job, attempt, "payload.bin", 4),
                new(DownloadStatus.Downloading, 4, 4, 0), Verification: VerificationStatus.Verifying);
            Assert.NotNull(await ledger.PublishedReceiptAsync(request.EngineId, entry, default));
            Assert.Null(await ledger.PublishedReceiptAsync(request.EngineId, entry with { Job = entry.Job with { ExpectedChecksum = ExpectedChecksum.FromManualInput(new string('0', 64)) } }, default));
            var target = entry.Job.Destination; File.Move(target, target + ".retained"); await File.WriteAllBytesAsync(target, bytes);
            Assert.Null(await ledger.PublishedReceiptAsync(request.EngineId, entry, default));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(target + ".retained"));
        }
        finally { Directory.Delete(root, true); }
    }
    private static async Task<EngineDownload> ObserveSettledAsync(ContinuityTransferLedger ledger, string gid)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var observed = await ledger.ObserveAsync(new(gid, new(DownloadStatus.Completed, 4, 4, 0)), default);
            if (observed.Verification != VerificationStatus.Verifying) return observed;
            await Task.Delay(5);
        }
        throw new TimeoutException("Required checksum verification did not settle.");
    }
}
