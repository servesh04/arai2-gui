using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AriaGui.Infrastructure.Aria2;
using AriaGui.Infrastructure.Downloads;
using AriaGui.Application.Downloads;
using AriaGui.Core.Downloads;

namespace AriaGui.Infrastructure.Tests;

[Collection("Engine processes")]
public sealed class M3PinnedEngineProbeTests
{
    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public async Task Guarded_production_dispatch_publishes_only_verified_parallel_bytes(int connections)
    {
        using var runtime = new RealDownloadTests.DownloadTestRuntime();
        var bytes = Enumerable.Range(0, 8 * 1024 * 1024).Select(i => (byte)(i % 251)).ToArray();
        var origin = new Origin(bytes);
        var options = runtime.Options with { UseContinuityGateway = true, ParallelHttpRanges = true,
            ContinuityTestOrigins = _ => origin };
        await using var engine = new Aria2ProcessManager(options, runtime.Repository());
        await engine.StartAsync(default);
        var store = new DownloadRuntimeStore();
        var coordinator = runtime.Coordinator(new ManagedAria2Client(engine), store);
        await coordinator.AddAsync("http://127.0.0.1/synthetic.bin", directory: runtime.Output,
            fileName: "parallel.bin", expectedSha256: Convert.ToHexString(SHA256.HashData(bytes)), httpConnections: connections);
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(30))
        {
            await coordinator.RefreshAsync();
            var current = Assert.Single(store.Current.Downloads);
            if (current.Progress.Status is DownloadStatus.Completed or DownloadStatus.Failed) break;
            await Task.Delay(50);
        }
        var completed = Assert.Single(store.Current.Downloads);
        Assert.True(completed.Progress.Status == DownloadStatus.Completed, completed.ErrorMessage);
        Assert.Equal(VerificationStatus.Verified, completed.Verification);
        Assert.Equal(connections, completed.Attempt.HttpConnections);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(completed.Job.Destination));
        // The first origin body uses OpenAsync; only the additional streams use bounded ranges.
        Assert.True(origin.Bounded >= connections - 1, $"Only {origin.Bounded} bounded requests.");
        Assert.True(origin.Maximum >= 2, $"Only {origin.Maximum} concurrent origin bodies.");
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Guarded_parallel_pause_restores_from_checkpoint_and_publishes_exact_digest(bool killEngine)
    {
        using var runtime = new RealDownloadTests.DownloadTestRuntime();
        var bytes = Enumerable.Range(0, 24 * 1024 * 1024).Select(i => (byte)(i % 251)).ToArray();
        var origin = new Origin(bytes);
        var options = runtime.Options with { UseContinuityGateway = true, ParallelHttpRanges = true,
            ContinuityTestOrigins = _ => origin };
        Guid jobId;
        string oldWork;
        await using (var first = new Aria2ProcessManager(options, runtime.Repository()))
        {
            await first.StartAsync(default);
            var store = new DownloadRuntimeStore();
            var coordinator = runtime.Coordinator(new ManagedAria2Client(first), store);
            await coordinator.AddAsync("http://127.0.0.1/synthetic.bin", directory: runtime.Output,
                fileName: "restart.bin", expectedSha256: Convert.ToHexString(SHA256.HashData(bytes)), httpConnections: 4);
            var entry = Assert.Single(store.Current.Downloads); jobId = entry.Job.Id;
            await coordinator.PauseAsync(jobId);
            var pauseDeadline = Stopwatch.StartNew();
            while (pauseDeadline.Elapsed < TimeSpan.FromSeconds(15))
            {
                await coordinator.RefreshAsync();
                entry = Assert.Single(store.Current.Downloads);
                if (entry.Progress.Status == DownloadStatus.Paused) break;
                await Task.Delay(40);
            }
            Assert.Equal(DownloadStatus.Paused, entry.Progress.Status);
            var journal = Path.Combine(options.RuntimeDirectory, "continuity", "transfers", entry.Attempt.EngineId + ".json");
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(journal));
            oldWork = document.RootElement.GetProperty("Work").GetString()!;
            var snapshots = Path.Combine(options.RuntimeDirectory, "continuity", "transfers", "range-checkpoints");
            var checkpoint = await new RangeCheckpointStore(snapshots, entry.Attempt.Id).LoadAsync(default);
            Assert.NotNull(checkpoint);
            Assert.True(checkpoint.CoveredBytes > 0 && checkpoint.CoveredBytes < bytes.Length);
            if (killEngine)
            {
                using var process = Process.GetProcessById(Assert.IsType<int>(first.OwnedProcessId));
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            else await first.StopAsync(default);
        }
        // The previous private pair may be stale. Restore must use the durable
        // authenticated snapshot and leave this abandoned copy untouched.
        var stale = Path.Combine(oldWork, "restart.bin");
        var oldControl = await File.ReadAllBytesAsync(stale + ".aria2");
        await File.WriteAllBytesAsync(stale, [9, 9, 9, 9]);
        await using var second = new Aria2ProcessManager(options, runtime.Repository());
        await second.StartAsync(default);
        var reopened = new DownloadRuntimeStore();
        var resumed = runtime.Coordinator(new ManagedAria2Client(second), reopened);
        await resumed.InitializeAsync();
        await resumed.ResumeAsync(jobId);
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(40))
        {
            await resumed.RefreshAsync();
            var current = Assert.Single(reopened.Current.Downloads);
            if (current.Progress.Status is DownloadStatus.Completed or DownloadStatus.Failed) break;
            await Task.Delay(50);
        }
        var completed = Assert.Single(reopened.Current.Downloads);
        Assert.True(completed.Progress.Status == DownloadStatus.Completed, completed.ErrorMessage);
        Assert.Equal(VerificationStatus.Verified, completed.Verification);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(completed.Job.Destination));
        Assert.Equal(new byte[] { 9, 9, 9, 9 }, await File.ReadAllBytesAsync(stale));
        Assert.Equal(oldControl, await File.ReadAllBytesAsync(stale + ".aria2"));
    }
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Parallel_intake_is_held_when_gateway_or_opt_in_is_disabled(bool guarded, bool parallel)
    {
        using var runtime = new RealDownloadTests.DownloadTestRuntime();
        var options = runtime.Options with { UseContinuityGateway = guarded, ParallelHttpRanges = parallel };
        await using var manager = new Aria2ProcessManager(options, runtime.Repository());
        await manager.StartAsync(default);
        var coordinator = runtime.Coordinator(new ManagedAria2Client(manager), new DownloadRuntimeStore());
        await Assert.ThrowsAsync<DownloadRequestException>(() => coordinator.AddAsync("http://127.0.0.1/unused.bin",
            directory: runtime.Output, fileName: "rejected.bin", expectedSha256: new string('A', 64), httpConnections: 4));
        Assert.Empty(await runtime.Repository().LoadAsync(default));
        Assert.False(File.Exists(Path.Combine(runtime.Output, "rejected.bin")));
    }
    [Fact]
    public async Task Parallel_digest_mismatch_keeps_private_bytes_and_never_publishes()
    {
        using var runtime = new RealDownloadTests.DownloadTestRuntime();
        var bytes = Enumerable.Range(0, 8 * 1024 * 1024).Select(i => (byte)(i % 251)).ToArray();
        var origin = new Origin(bytes);
        var options = runtime.Options with { UseContinuityGateway = true, ParallelHttpRanges = true,
            ContinuityTestOrigins = _ => origin };
        await using var engine = new Aria2ProcessManager(options, runtime.Repository());
        await engine.StartAsync(default);
        var store = new DownloadRuntimeStore();
        var coordinator = runtime.Coordinator(new ManagedAria2Client(engine), store);
        await coordinator.AddAsync("http://127.0.0.1/synthetic.bin", directory: runtime.Output,
            fileName: "mismatch.bin", expectedSha256: new string('0', 64), httpConnections: 4);
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(30))
        {
            await coordinator.RefreshAsync();
            if (Assert.Single(store.Current.Downloads).Progress.Status == DownloadStatus.Failed) break;
            await Task.Delay(50);
        }
        var failed = Assert.Single(store.Current.Downloads);
        Assert.Equal(VerificationStatus.Mismatch, failed.Verification);
        Assert.False(File.Exists(failed.Job.Destination));
        var stage = Path.Combine(runtime.Output, ".aria-gui-continuity-" + failed.Job.Id.ToString("N"),
            failed.Attempt.Id.ToString("N"), "mismatch.bin");
        Assert.Equal(bytes, await File.ReadAllBytesAsync(stage));
    }
    [Fact]
    public async Task Disabled_parallel_restore_holds_durable_attempt_with_clear_reason()
    {
        using var runtime = new RealDownloadTests.DownloadTestRuntime();
        var bytes = Enumerable.Range(0, 24 * 1024 * 1024).Select(i => (byte)(i % 251)).ToArray();
        var origin = new Origin(bytes);
        var options = runtime.Options with { UseContinuityGateway = true, ParallelHttpRanges = true,
            ContinuityTestOrigins = _ => origin };
        await using (var first = new Aria2ProcessManager(options, runtime.Repository()))
        {
            await first.StartAsync(default);
            var store = new DownloadRuntimeStore();
            var coordinator = runtime.Coordinator(new ManagedAria2Client(first), store);
            await coordinator.AddAsync("http://127.0.0.1/synthetic.bin", directory: runtime.Output,
                fileName: "held.bin", expectedSha256: Convert.ToHexString(SHA256.HashData(bytes)), httpConnections: 4);
            var job = Assert.Single(store.Current.Downloads).Job.Id;
            await coordinator.PauseAsync(job);
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromSeconds(15))
            {
                await coordinator.RefreshAsync();
                if (Assert.Single(store.Current.Downloads).Progress.Status == DownloadStatus.Paused) break;
                await Task.Delay(40);
            }
            Assert.Equal(DownloadStatus.Paused, Assert.Single(store.Current.Downloads).Progress.Status);
            await first.StopAsync(default);
        }
        var priorRequests = origin.Bounded;
        await using var second = new Aria2ProcessManager(options with { ParallelHttpRanges = false }, runtime.Repository());
        await second.StartAsync(default);
        var store2 = new DownloadRuntimeStore();
        var coordinator2 = runtime.Coordinator(new ManagedAria2Client(second), store2);
        await coordinator2.InitializeAsync();
        await coordinator2.RefreshAsync();
        var held = Assert.Single(store2.Current.Downloads);
        Assert.Equal(DownloadStatus.Interrupted, held.Progress.Status);
        Assert.Contains("Parallel HTTP is disabled", held.ErrorMessage);
        Assert.Equal(priorRequests, origin.Bounded);
        Assert.False(File.Exists(held.Job.Destination));
    }
    [Fact]
    public async Task Changed_bounded_ETag_cannot_publish_mixed_parallel_stage()
    {
        using var runtime = new RealDownloadTests.DownloadTestRuntime();
        var bytes = Enumerable.Range(0, 8 * 1024 * 1024).Select(i => (byte)(i % 251)).ToArray();
        var origin = new Origin(bytes, changedBoundedETag: true);
        var options = runtime.Options with { UseContinuityGateway = true, ParallelHttpRanges = true,
            ContinuityTestOrigins = _ => origin };
        await using var engine = new Aria2ProcessManager(options, runtime.Repository());
        await engine.StartAsync(default);
        var store = new DownloadRuntimeStore();
        var coordinator = runtime.Coordinator(new ManagedAria2Client(engine), store);
        await coordinator.AddAsync("http://127.0.0.1/synthetic.bin", directory: runtime.Output,
            fileName: "changed.bin", expectedSha256: Convert.ToHexString(SHA256.HashData(bytes)), httpConnections: 4);
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(30))
        {
            await coordinator.RefreshAsync();
            if (Assert.Single(store.Current.Downloads).Progress.Status is DownloadStatus.Completed or DownloadStatus.Failed) break;
            await Task.Delay(50);
        }
        var result = Assert.Single(store.Current.Downloads);
        Assert.True(origin.Bounded > 0);
        if (result.Progress.Status == DownloadStatus.Completed)
        {
            Assert.Equal(VerificationStatus.Verified, result.Verification);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(result.Job.Destination));
        }
        else
        {
            Assert.Equal(DownloadStatus.Failed, result.Progress.Status);
            Assert.False(File.Exists(result.Job.Destination));
        }
    }
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Changed_bounded_resource_or_truncated_body_cannot_publish_invalid_parallel_bytes(
        bool changedResource, bool truncatedBody)
    {
        using var runtime = new RealDownloadTests.DownloadTestRuntime();
        var bytes = Enumerable.Range(0, 8 * 1024 * 1024).Select(i => (byte)(i % 251)).ToArray();
        var origin = new Origin(bytes, changedBoundedResource: changedResource, truncatedBoundedBody: truncatedBody);
        var options = runtime.Options with { UseContinuityGateway = true, ParallelHttpRanges = true,
            ContinuityTestOrigins = _ => origin };
        await using var engine = new Aria2ProcessManager(options, runtime.Repository());
        await engine.StartAsync(default);
        var store = new DownloadRuntimeStore();
        var coordinator = runtime.Coordinator(new ManagedAria2Client(engine), store);
        await coordinator.AddAsync("http://127.0.0.1/synthetic.bin", directory: runtime.Output,
            fileName: "mutated.bin", expectedSha256: Convert.ToHexString(SHA256.HashData(bytes)), httpConnections: 4);
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(25))
        {
            await coordinator.RefreshAsync();
            if (Assert.Single(store.Current.Downloads).Progress.Status is DownloadStatus.Completed or DownloadStatus.Failed) break;
            await Task.Delay(50);
        }
        var result = Assert.Single(store.Current.Downloads);
        Assert.True(origin.Bounded > 0);
        if (result.Progress.Status == DownloadStatus.Completed)
        {
            Assert.Equal(VerificationStatus.Verified, result.Verification);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(result.Job.Destination));
        }
        else
        {
            Assert.False(File.Exists(result.Job.Destination));
            var stage = Path.Combine(runtime.Output, ".aria-gui-continuity-" + result.Job.Id.ToString("N"),
                result.Attempt.Id.ToString("N"), "mutated.bin");
            Assert.True(File.Exists(stage), "Failed transfer lost its private payload.");
        }
    }
    [Fact]
    public async Task Parallel_jobs_share_guarded_upstream_bandwidth_cap_and_stop_reads_on_remove()
    {
        using var runtime = new RealDownloadTests.DownloadTestRuntime();
        var bytes = Enumerable.Range(0, 8 * 1024 * 1024).Select(i => (byte)(i % 251)).ToArray();
        var origin = new Origin(bytes);
        var options = runtime.Options with { UseContinuityGateway = true, ParallelHttpRanges = true,
            ContinuityTestOrigins = _ => origin };
        await using var engine = new Aria2ProcessManager(options, runtime.Repository());
        await engine.StartAsync(default);
        var store = new DownloadRuntimeStore();
        var coordinator = runtime.Coordinator(new ManagedAria2Client(engine), store);
        await coordinator.SetBandwidthProfileAsync(new("M3 metered", new(32768), ByteRateLimit.Unlimited));
        var digest = Convert.ToHexString(SHA256.HashData(bytes));
        await coordinator.AddAsync("http://127.0.0.1/synthetic.bin", directory: runtime.Output,
            fileName: "metered-one.bin", expectedSha256: digest, httpConnections: 4);
        await coordinator.AddAsync("http://127.0.0.1/synthetic.bin", directory: runtime.Output,
            fileName: "metered-two.bin", expectedSha256: digest, httpConnections: 4);
        var startup = Interlocked.Read(ref origin.ReadBytes);
        var timer = Stopwatch.StartNew();
        await Task.Delay(TimeSpan.FromSeconds(2));
        var later = Interlocked.Read(ref origin.ReadBytes);
        Assert.True(origin.Bounded > 0);
        Assert.True(later > 0);
        Assert.True(later - startup <= 32768 * timer.Elapsed.TotalSeconds + 32768,
            $"Parallel origin read {later - startup} bytes over {timer.Elapsed.TotalSeconds:F2}s at 32768 B/s.");
        foreach (var entry in store.Current.Downloads.ToArray()) await coordinator.RemoveAsync(entry.Job.Id);
        var stopped = Interlocked.Read(ref origin.ReadBytes);
        await Task.Delay(500);
        Assert.InRange(Interlocked.Read(ref origin.ReadBytes) - stopped, 0, 32768);
    }
    private sealed class Origin(byte[] bytes, bool changedBoundedETag = false, bool changedBoundedResource = false,
        bool truncatedBoundedBody = false) : IContinuityOriginTransport
    {
        public int Active, Maximum, Bounded;
        public long ReadBytes;
        public Task<HttpResponseMessage> OpenAsync(long offset, string? ifMatch, CancellationToken token) =>
            Task.FromResult(Make(0, bytes.Length - 1, false));
        public Task<HttpResponseMessage> OpenRangeAsync(long start, long end, string? ifMatch, CancellationToken token)
        { Interlocked.Increment(ref Bounded); return Task.FromResult(Make(start, end, true)); }
        private HttpResponseMessage Make(long start, long end, bool bounded)
        {
            var response = new HttpResponseMessage(bounded ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
            { RequestMessage = new(HttpMethod.Get, bounded && changedBoundedResource
                ? "http://127.0.0.1/redirected.bin" : "http://127.0.0.1/synthetic.bin") };
            response.Headers.ETag = new(bounded && changedBoundedETag ? "\"changed-after-initial\"" : "\"pinned-engine-synthetic\"");
            var streamLength = (int)(end - start + 1) - (bounded && truncatedBoundedBody ? 1 : 0);
            var stream = new SlowSliceStream(bytes, (int)start, streamLength, this);
            response.Content = new StreamContent(stream);
            response.Content.Headers.ContentLength = end - start + 1;
            if (bounded) response.Content.Headers.ContentRange = new(start, end, bytes.Length);
            return response;
        }
    }
    private sealed class SlowSliceStream(byte[] bytes, int start, int length, Origin owner) : Stream
    {
        private int _position, _active;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (_position >= length) return 0;
            if (Interlocked.Exchange(ref _active, 1) == 0)
            {
                var active = Interlocked.Increment(ref owner.Active);
                int prior;
                do { prior = owner.Maximum; if (prior >= active) break; }
                while (Interlocked.CompareExchange(ref owner.Maximum, active, prior) != prior);
            }
            await Task.Delay(2, token);
            var count = Math.Min(buffer.Length, Math.Min(16384, length - _position));
            bytes.AsSpan(start + _position, count).CopyTo(buffer.Span);
            _position += count;
            Interlocked.Add(ref owner.ReadBytes, count);
            return count;
        }
        protected override void Dispose(bool disposing)
        { if (Interlocked.Exchange(ref _active, 0) == 1) Interlocked.Decrement(ref owner.Active); base.Dispose(disposing); }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    [Fact]
    public async Task Pinned_engine_assembles_simultaneous_v2_ranges_exactly_without_publication()
    {
        var root = Path.Combine(Path.GetTempPath(), "AriaGUI.M3PinnedProbe", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var rpcPort = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            var options = new Aria2ProcessOptions(Path.Combine(AppContext.BaseDirectory, "Engine", "aria2c.exe"), Path.Combine(root, "engine")) { RpcPort = rpcPort };
            await using var engine = new Aria2ProcessManager(options);
            await engine.StartAsync(default);
            var bytes = Enumerable.Range(0, 8 * 1024 * 1024).Select(i => (byte)(i % 251)).ToArray();
            var origin = new Origin(bytes);
            await using var gateway = new ContinuityLoopbackGateway(Path.Combine(root, "gateway"), () => engine.OwnedProcessId, _ => origin, true);
            await gateway.StartAsync(default);
            var job = Guid.NewGuid(); var attempt = Guid.NewGuid();
            var address = await gateway.RegisterRangeExperimentalAsync(job, attempt, "http://127.0.0.1/synthetic.bin", default, false);
            var secret = (await File.ReadAllLinesAsync(Path.Combine(options.RuntimeDirectory, "aria2.conf")))
                .Single(line => line.StartsWith("rpc-secret=", StringComparison.Ordinal))["rpc-secret=".Length..];
            using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
            var endpoint = $"http://127.0.0.1:{rpcPort}/jsonrpc";
            async Task<JsonElement> RpcAsync(string method, object[] parameters)
            {
                var input = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = Guid.NewGuid().ToString("N"), method, @params = new object[] { "token:" + secret }.Concat(parameters).ToArray() });
                using var reply = await http.PostAsync(endpoint, new StringContent(input, Encoding.UTF8, "application/json"));
                reply.EnsureSuccessStatusCode();
                using var document = JsonDocument.Parse(await reply.Content.ReadAsStringAsync());
                Assert.False(document.RootElement.TryGetProperty("error", out var error), error.ToString());
                return document.RootElement.GetProperty("result").Clone();
            }
            var gid = Guid.NewGuid().ToString("N")[..16];
            var work = Path.Combine(root, "work"); Directory.CreateDirectory(work);
            var addOptions = new Dictionary<string, string>
            {
                ["gid"] = gid, ["dir"] = work, ["out"] = "payload.bin", ["split"] = "4", ["max-connection-per-server"] = "4",
                ["min-split-size"] = "1M", ["file-allocation"] = "none", ["allow-overwrite"] = "false", ["auto-file-renaming"] = "false",
                ["follow-torrent"] = "false", ["follow-metalink"] = "false"
            };
            var added = await RpcAsync("aria2.addUri", [new[] { address.AbsoluteUri }, addOptions]);
            Assert.Equal(gid, added.GetString());
            var overlapDeadline = Stopwatch.StartNew();
            while (origin.Maximum < 2 && overlapDeadline.Elapsed < TimeSpan.FromSeconds(8)) await Task.Delay(10);
            Assert.True(origin.Maximum >= 2, "aria2 did not overlap origin range bodies.");
            Assert.Equal(gid, (await RpcAsync("aria2.pause", [gid])).GetString());
            var pauseDeadline = Stopwatch.StartNew(); string? paused = null;
            while (pauseDeadline.Elapsed < TimeSpan.FromSeconds(8))
            {
                paused = (await RpcAsync("aria2.tellStatus", [gid])).GetProperty("status").GetString();
                if (paused == "paused" && File.Exists(Path.Combine(work, "payload.bin.aria2"))) break;
                await Task.Delay(25);
            }
            Assert.Equal("paused", paused);
            var checkpointStore = new RangeCheckpointStore(Path.Combine(root, "checkpoints"), Guid.NewGuid());
            var pausedStatus = await RpcAsync("aria2.tellStatus", [gid]);
            var observedBytes = long.Parse(pausedStatus.GetProperty("completedLength").GetString()!);
            var checkpoint = await checkpointStore.CaptureAsync(Path.Combine(work, "payload.bin"), default, observedBytes);
            Assert.Equal(1, checkpoint.Revision);
            Assert.Equal(checkpoint, await checkpointStore.LoadAsync(default));
            var control = await File.ReadAllBytesAsync(Path.Combine(work, "payload.bin.aria2"));
            Assert.Equal(control, await File.ReadAllBytesAsync(Path.Combine(root, "checkpoints", checkpoint.Attempt.ToString("N"), checkpoint.SnapshotDirectory, "payload.bin.aria2")));
            var coverage = Aria2ControlCoverageReader.Parse(control);
            Assert.Equal(bytes.Length, coverage.TotalLength);
            Assert.Equal(observedBytes, coverage.CoveredBytes);
            await engine.StopAsync(default);
            Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(root, "checkpoints", checkpoint.Attempt.ToString("N"), checkpoint.SnapshotDirectory, "payload.bin")),
                await File.ReadAllBytesAsync(Path.Combine(work, "payload.bin")));
            Assert.Equal(control, await File.ReadAllBytesAsync(Path.Combine(work, "payload.bin.aria2")));
            Assert.Equal(checkpoint, await checkpointStore.LoadAsync(default));
            await engine.StartAsync(default);
            secret = (await File.ReadAllLinesAsync(Path.Combine(options.RuntimeDirectory, "aria2.conf")))
                .Single(line => line.StartsWith("rpc-secret=", StringComparison.Ordinal))["rpc-secret=".Length..];
            addOptions["continue"] = "true";
            addOptions["always-resume"] = "true";
            Assert.Equal(gid, (await RpcAsync("aria2.addUri", [new[] { address.AbsoluteUri }, addOptions])).GetString());
            var deadline = Stopwatch.StartNew(); string? status = null;
            while (deadline.Elapsed < TimeSpan.FromSeconds(25))
            {
                var result = await RpcAsync("aria2.tellStatus", [gid]);
                status = result.GetProperty("status").GetString();
                if (status is "complete" or "error" or "removed") break;
                await Task.Delay(50);
            }
            Assert.Equal("complete", status);
            Assert.True(origin.Bounded >= 2, $"Only {origin.Bounded} bounded origin requests.");
            Assert.True(origin.Maximum >= 2, $"Maximum concurrent origin bodies was {origin.Maximum}.");
            var saved = await File.ReadAllBytesAsync(Path.Combine(work, "payload.bin"));
            Assert.Equal(bytes, saved);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), Convert.ToHexString(SHA256.HashData(saved)));
            Assert.Equal(3, (await new HttpRepresentationStore(Path.Combine(root, "gateway", "continuity"), job, attempt).LoadAsync(default))?.Version);
            await engine.StopAsync(default);
        }
        finally
        {
            var allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "AriaGUI.M3PinnedProbe")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe probe cleanup");
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
