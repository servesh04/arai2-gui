using System.Net;
using System.Net.Sockets;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AriaGui.Application.Engine;
using AriaGui.Core.Downloads;
using AriaGui.Infrastructure.Aria2;
using AriaGui.Infrastructure.Downloads;
using AriaGui.Infrastructure.Persistence;

namespace AriaGui.Infrastructure.Tests;

public sealed class TorrentLifecycleTests
{
    [Fact]
    public async Task FailedTorrentRetryRemovesOnlyItsEngineResult()
    {
        const string gid = "1234567890abcdef";
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var command = body.RootElement;
            Assert.Equal("aria2.removeDownloadResult", command.GetProperty("method").GetString());
            Assert.Equal("token:secret", command.GetProperty("params")[0].GetString());
            Assert.Equal(gid, command.GetProperty("params")[1].GetString());
            return Json(new { jsonrpc = "2.0", id = command.GetProperty("id").GetString(), result = "OK" });
        }));
        var rpc = new Aria2RpcClient(http, new("http://127.0.0.1:16800/jsonrpc"), "secret", TimeSpan.FromSeconds(1));
        await rpc.RemoveFailedTorrentResultAsync(gid, default);
        await Assert.ThrowsAsync<ArgumentException>(() => rpc.RemoveFailedTorrentResultAsync("invalid", default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RpcDispatchUsesRestrictedOptionsAndRejectsChangedFileIdentity(bool changedPath)
    {
        var metainfo = Metainfo();
        var manifest = TorrentManifestParser.Parse(metainfo);
        var selection = manifest.SelectFiles([2]);
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "AriaGUI.TorrentRpcTests", "private"));
        const string gid = "1234567890abcdef";
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var command = body.RootElement;
            var method = command.GetProperty("method").GetString();
            var id = command.GetProperty("id").GetString();
            Assert.Equal("token:secret", command.GetProperty("params")[0].GetString());
            if (method == "aria2.addTorrent")
            {
                Assert.Equal(Convert.ToBase64String(metainfo), command.GetProperty("params")[1].GetString());
                var options = command.GetProperty("params")[3];
                Assert.Equal("2", options.GetProperty("select-file").GetString());
                Assert.Equal("0", options.GetProperty("seed-time").GetString());
                Assert.Equal("true", options.GetProperty("check-integrity").GetString());
                Assert.Equal("false", options.GetProperty("enable-peer-exchange").GetString());
                return Json(new { jsonrpc = "2.0", id, result = gid });
            }
            Assert.Equal("aria2.tellStatus", method);
            return Json(new { jsonrpc = "2.0", id, result = new
            {
                gid, status = "waiting", infoHash = manifest.InfoHash, dir = root,
                totalLength = "3", completedLength = "0", downloadSpeed = "0",
                files = new[]
                {
                    new { index = "1", path = Path.Combine(root, "bundle", "first.bin"), length = "2", selected = "false" },
                    new { index = "2", path = changedPath ? Path.Combine(root, "other.bin") : Path.Combine(root, "bundle", "second.bin"), length = "3", selected = "true" }
                }
            } });
        }));
        var rpc = new Aria2RpcClient(http, new("http://127.0.0.1:16800/jsonrpc"), "secret", TimeSpan.FromSeconds(1));
        if (changedPath)
            Assert.Equal(EngineFailure.RequestRejected, (await Assert.ThrowsAsync<EngineException>(() =>
                rpc.AddTorrentAsync(metainfo, manifest, selection, root, gid, default))).Failure);
        else Assert.Equal(TorrentStage.Waiting,
            (await rpc.AddTorrentAsync(metainfo, manifest, selection, root, gid, default)).Stage);
    }

    [Fact]
    public async Task VerifiedSelectedFilePublishesButCollisionRequiresReview()
    {
        using var runtime = new Runtime();
        var store = new SqliteTorrentMetadataRepository(runtime.DatabasePath);
        await store.InitializeAsync();
        var privateRoot = Path.Combine(runtime.Root, "private");
        var publicRoot = Path.Combine(runtime.Root, "public");
        var id = await store.CreateDraftAsync(Metainfo(), [2], privateRoot, publicRoot);
        const string gid = "1234567890abcdef";
        await store.PrepareDispatchAsync(id, "session", gid);
        var draft = (await store.LoadDraftAsync(id))!;
        var source = Path.Combine(privateRoot, "bundle", "second.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        await File.WriteAllTextAsync(source, "abc");
        var observed = new TorrentEngineObservation(gid, TorrentStage.Publishing, 3, 3, 0, 3);
        var destination = Path.Combine(publicRoot, "bundle", "second.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await File.WriteAllTextAsync(destination, "user file");
        var publisher = new TorrentPublisher(store);
        await Assert.ThrowsAsync<IOException>(() => publisher.PublishAsync(draft, observed, default));
        Assert.Equal("user file", await File.ReadAllTextAsync(destination));
        File.Delete(destination);
        await publisher.PublishAsync(draft, observed, default);
        Assert.Equal("abc", await File.ReadAllTextAsync(destination));
        Assert.False(File.Exists(Path.Combine(publicRoot, "bundle", "first.bin")));
        Assert.Equal(TorrentStage.Completed, (await store.LoadDraftAsync(id))!.Stage);
        Assert.True(Assert.Single(await store.LoadSelectedFilesAsync(id)).Published);
        File.Delete(destination);
        var publishedDraft = (await store.LoadDraftAsync(id))!;
        await Assert.ThrowsAsync<IOException>(() => publisher.PublishAsync(publishedDraft, observed, default));
    }

    [Fact]
    public async Task MoveBeforeDatabaseCommitConvergesOnlyForMatchingOwnedBytes()
    {
        using var runtime = new Runtime();
        var store = new SqliteTorrentMetadataRepository(runtime.DatabasePath);
        await store.InitializeAsync();
        var privateRoot = Path.Combine(runtime.Root, "private");
        var publicRoot = Path.Combine(runtime.Root, "public");
        var id = await store.CreateDraftAsync(Metainfo(), [2], privateRoot, publicRoot);
        const string gid = "1234567890abcdef";
        await store.PrepareDispatchAsync(id, "session", gid);
        var source = Path.Combine(privateRoot, "bundle", "second.bin");
        var destination = Path.Combine(publicRoot, "bundle", "second.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await File.WriteAllTextAsync(source, "abc");
        await store.MarkPublishingAsync(id, 2);
        await File.WriteAllTextAsync(destination, "abc");
        var observed = new TorrentEngineObservation(gid, TorrentStage.Publishing, 3, 3, 0, 3);
        await new TorrentPublisher(store).PublishAsync((await store.LoadDraftAsync(id))!, observed, default);
        Assert.Equal(TorrentStage.Completed, (await store.LoadDraftAsync(id))!.Stage);
        Assert.True(Assert.Single(await store.LoadSelectedFilesAsync(id)).Published);
    }

    [Theory]
    [InlineData("a", true)]
    [InlineData("x", false)]
    public async Task PublicationResumesOnlyAStartedPartialMatchingVerifiedPayload(string partialBytes, bool matches)
    {
        using var runtime = new Runtime();
        var store = new SqliteTorrentMetadataRepository(runtime.DatabasePath);
        await store.InitializeAsync();
        var privateRoot = Path.Combine(runtime.Root, "private");
        var publicRoot = runtime.PublicRoot;
        var id = await store.CreateDraftAsync(Metainfo(), [2], privateRoot, publicRoot);
        const string gid = "1234567890abcdef";
        await store.PrepareDispatchAsync(id, "session", gid);
        await store.MarkPublishingAsync(id, 2);
        var source = Path.Combine(privateRoot, "bundle", "second.bin");
        var destination = Path.Combine(publicRoot, "bundle", "second.bin");
        var partial = destination + $".ariagui-{id:N}-2.partial";
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await File.WriteAllTextAsync(source, "abc");
        await File.WriteAllTextAsync(partial, partialBytes);
        var observed = new TorrentEngineObservation(gid, TorrentStage.Publishing, 3, 3, 0, 3);
        var publisher = new TorrentPublisher(store);
        if (matches)
        {
            await publisher.PublishAsync((await store.LoadDraftAsync(id))!, observed, default);
            Assert.Equal("abc", await File.ReadAllTextAsync(destination));
            Assert.False(File.Exists(partial));
            Assert.Equal(TorrentStage.Completed, (await store.LoadDraftAsync(id))!.Stage);
        }
        else
        {
            var draft = (await store.LoadDraftAsync(id))!;
            await Assert.ThrowsAsync<IOException>(() => publisher.PublishAsync(draft, observed, default));
            Assert.False(File.Exists(destination));
            Assert.Equal(partialBytes, await File.ReadAllTextAsync(partial));
        }
    }

    [Fact]
    public async Task RealEngineTorrentDispatchIsPausedAndOwnedWithoutPublicPeers()
    {
        using var runtime = new Runtime();
        var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var options = new Aria2ProcessOptions(Path.Combine(AppContext.BaseDirectory, "Engine", "aria2c.exe"), runtime.Root)
            { RpcPort = port, StartupTimeout = TimeSpan.FromSeconds(5) };
        await using var engine = new Aria2ProcessManager(options);
        await engine.StartAsync(default);
        var config = await File.ReadAllTextAsync(Path.Combine(runtime.Root, "aria2.conf"));
        var secret = config.Split('\n').Single(line => line.StartsWith("rpc-secret=", StringComparison.Ordinal))[11..].Trim();
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        var rpc = new Aria2RpcClient(http, new($"http://127.0.0.1:{port}/jsonrpc"), secret, TimeSpan.FromSeconds(3));
        var metainfo = Metainfo();
        var manifest = TorrentManifestParser.Parse(metainfo);
        var root = Path.Combine(runtime.Root, "private");
        Directory.CreateDirectory(root);
        const string gid = "1234567890abcdef";
        var observed = await rpc.AddTorrentAsync(metainfo, manifest, manifest.SelectFiles([2]), root, gid, default);
        Assert.Equal(gid, observed.Gid);
        Assert.Equal(TorrentStage.Stopped, observed.Stage);
        Assert.Equal(3, observed.TotalBytes);
        Assert.Contains("seed-time=0", config);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocalTrackerTransferRecoversAndPublishesOnlySelectedFile(bool publicationCollision)
    {
        using var runtime = new Runtime();
        var seedPort = FreePort();
        await using var tracker = new LocalTracker(seedPort);
        var first = Encoding.ASCII.GetBytes("ab");
        var second = Encoding.ASCII.GetBytes("cde");
        var metainfo = MetainfoWithPieces(tracker.Port, first, second);
        var seedRoot = Path.Combine(runtime.Root, "seed");
        var publicRoot = runtime.PublicRoot;
        Directory.CreateDirectory(Path.Combine(seedRoot, "bundle"));
        Directory.CreateDirectory(publicRoot);
        await File.WriteAllBytesAsync(Path.Combine(seedRoot, "bundle", "first.bin"), first);
        await File.WriteAllBytesAsync(Path.Combine(seedRoot, "bundle", "second.bin"), second);
        var torrentPath = Path.Combine(runtime.Root, "local.torrent");
        await File.WriteAllBytesAsync(torrentPath, metainfo);
        var executable = Path.Combine(AppContext.BaseDirectory, "Engine", "aria2c.exe");
        var seedStart = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = runtime.Root };
        foreach (var argument in new[] { $"--dir={seedRoot}", $"--listen-port={seedPort}", "--interface=127.0.0.1",
            "--disable-ipv6=true", "--enable-dht=false", "--enable-dht6=false", "--enable-peer-exchange=false",
            "--bt-enable-lpd=false", "--seed-time=2", "--check-integrity=true", "--file-allocation=none",
            "--quiet=true", torrentPath }) seedStart.ArgumentList.Add(argument);
        using var seed = Process.Start(seedStart) ?? throw new InvalidOperationException("Local seeder did not start.");
        try
        {
            var repository = new SqliteTorrentMetadataRepository(runtime.DatabasePath);
            var options = new Aria2ProcessOptions(executable, runtime.Root) { RpcPort = FreePort(), StartupTimeout = TimeSpan.FromSeconds(8) };
            Guid id;
            await using (var engine = new Aria2ProcessManager(options, torrentRepository: repository))
            {
                await engine.StartAsync(default);
                id = await engine.CreateTorrentDraftAsync(metainfo, [2], publicRoot, true, default);
                if (publicationCollision)
                {
                    var collision = Path.Combine(publicRoot, "bundle", "second.bin");
                    Directory.CreateDirectory(Path.GetDirectoryName(collision)!);
                    await File.WriteAllTextAsync(collision, "user file");
                }
                await engine.DispatchTorrentAsync(id, default);
                await engine.StopAsync(default);
            }
            await using (var recovered = new Aria2ProcessManager(options, torrentRepository: repository))
            {
                await recovered.StartAsync(default);
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
                while ((await repository.LoadDraftAsync(id, deadline.Token))!.Stage !=
                    (publicationCollision ? TorrentStage.Failed : TorrentStage.Completed))
                {
                    try
                    {
                        var current = await recovered.ObserveTorrentAsync(id, deadline.Token);
                        Assert.NotNull(current);
                        if (!publicationCollision) Assert.NotEqual(TorrentStage.Failed, current.Stage);
                    }
                    catch (IOException) when (publicationCollision) { }
                    await Task.Delay(250, deadline.Token);
                }
                if (publicationCollision)
                {
                    var collision = Path.Combine(publicRoot, "bundle", "second.bin");
                    Assert.Equal("user file", await File.ReadAllTextAsync(collision, deadline.Token));
                    File.Delete(collision);
                    Assert.Equal(TorrentStage.Completed,
                        (await recovered.RetryFailedTorrentAsync(id, deadline.Token)).Stage);
                }
                await recovered.StopAsync(default);
            }
            Assert.Equal(second, await File.ReadAllBytesAsync(Path.Combine(publicRoot, "bundle", "second.bin")));
            Assert.False(File.Exists(Path.Combine(publicRoot, "bundle", "first.bin")));
            Assert.Equal(TorrentStage.Completed, (await repository.LoadDraftAsync(id))!.Stage);
        }
        finally
        {
            if (!seed.HasExited) { seed.Kill(entireProcessTree: true); await seed.WaitForExitAsync(); }
        }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static byte[] MetainfoWithPieces(int trackerPort, byte[] first, byte[] second)
    {
        static byte[] S(byte[] value) => [.. Encoding.ASCII.GetBytes(value.Length + ":"), .. value];
        static byte[] B(string value) => S(Encoding.UTF8.GetBytes(value));
        static byte[] I(int value) => Encoding.ASCII.GetBytes($"i{value}e");
        static byte[] L(params byte[][] values) => [(byte)'l', .. values.SelectMany(value => value), (byte)'e'];
        static byte[] D(params (string Key, byte[] Value)[] entries) =>
            [(byte)'d', .. entries.OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .SelectMany(entry => B(entry.Key).Concat(entry.Value)), (byte)'e'];
        var payload = first.Concat(second).ToArray();
        var pieces = payload.Chunk(4).SelectMany(chunk => SHA1.HashData(chunk)).ToArray();
        var files = L(D(("length", I(first.Length)), ("path", L(B("first.bin")))),
            D(("length", I(second.Length)), ("path", L(B("second.bin")))));
        return D(("announce", B($"http://127.0.0.1:{trackerPort}/announce")),
            ("info", D(("files", files), ("name", B("bundle")), ("piece length", I(4)), ("pieces", S(pieces)))));
    }

    private sealed class LocalTracker : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _server;
        private readonly int _peerPort;
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public LocalTracker(int peerPort)
        {
            _peerPort = peerPort;
            _listener.Start();
            _server = ServeAsync();
        }
        private async Task ServeAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    await using var stream = client.GetStream();
                    var request = new byte[4096];
                    _ = await stream.ReadAsync(request, _stop.Token);
                    byte[] body = [.. Encoding.ASCII.GetBytes("d8:intervali1e5:peers6:"), 127, 0, 0, 1,
                        (byte)(_peerPort >> 8), (byte)_peerPort, (byte)'e'];
                    var headers = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(headers, _stop.Token);
                    await stream.WriteAsync(body, _stop.Token);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (SocketException) when (_stop.IsCancellationRequested) { }
        }
        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            await _server;
            _stop.Dispose();
        }
    }

    private static byte[] Metainfo()
    {
        static byte[] B(string value) => S(Encoding.UTF8.GetBytes(value));
        static byte[] S(byte[] value) => [.. Encoding.ASCII.GetBytes(value.Length + ":"), .. value];
        static byte[] I(int value) => Encoding.ASCII.GetBytes($"i{value}e");
        static byte[] L(params byte[][] values) => [(byte)'l', .. values.SelectMany(value => value), (byte)'e'];
        static byte[] D(params (string Key, byte[] Value)[] entries) =>
            [(byte)'d', .. entries.OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .SelectMany(entry => B(entry.Key).Concat(entry.Value)), (byte)'e'];
        var files = L(D(("length", I(2)), ("path", L(B("first.bin")))),
            D(("length", I(3)), ("path", L(B("second.bin")))));
        return D(("announce", B("http://127.0.0.1:53623/announce")),
            ("info", D(("files", files), ("name", B("bundle")),
                ("piece length", I(4)), ("pieces", S(new byte[40])))));
    }
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => action(request, token);
    }
    private sealed class Runtime : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "AriaGUI.TorrentLifecycleTests", Guid.NewGuid().ToString("N"));
        public string PublicRoot => Path.Combine(Path.GetTempPath(), "AriaGUI.TorrentLifecyclePublished", Path.GetFileName(Root));
        public string DatabasePath => Path.Combine(Root, "downloads.db");
        public Runtime() => Directory.CreateDirectory(Root);
        public void Dispose()
        {
            var allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "AriaGUI.TorrentLifecycleTests")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(Root).StartsWith(allowed, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unsafe test cleanup.");
            Directory.Delete(Root, recursive: true);
            var publicAllowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "AriaGUI.TorrentLifecyclePublished")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(PublicRoot).StartsWith(publicAllowed, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unsafe published-test cleanup.");
            if (Directory.Exists(PublicRoot)) Directory.Delete(PublicRoot, recursive: true);
        }
    }
}
