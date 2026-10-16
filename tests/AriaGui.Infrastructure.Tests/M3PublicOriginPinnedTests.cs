using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using AriaGui.Application.Downloads;
using AriaGui.Core.Downloads;
using AriaGui.Infrastructure.Aria2;
using AriaGui.Infrastructure.Downloads;

namespace AriaGui.Infrastructure.Tests;

[Collection("Engine processes")]
public sealed class M3PublicOriginPinnedTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Pinned_guarded_public_transport_preserves_stage_on_changed_redirect_or_truncated_range(
        bool changedRedirect, bool truncatedRange)
    {
        using var runtime = new RealDownloadTests.DownloadTestRuntime();
        var bytes = Enumerable.Range(0, 8 * 1024 * 1024).Select(index => (byte)(index % 251)).ToArray();
        await using var origin = new RangedOrigin(bytes, changedRedirect, truncatedRange).Start();
        var approved = IPAddress.Parse("8.8.8.8");
        var url = $"http://public.fixture.invalid:{origin.Port}/start";
        var options = runtime.Options with { UseContinuityGateway = true, ParallelHttpRanges = true,
            ContinuityTestOrigins = source => new PublicHttpOriginTransport(source,
                (_, _) => Task.FromResult(new[] { approved }),
                (endpoint, token) => origin.ConnectAsync(endpoint, approved, token), null) };
        await using var manager = new Aria2ProcessManager(options, runtime.Repository());
        await manager.StartAsync(default);
        var store = new DownloadRuntimeStore();
        var coordinator = runtime.Coordinator(new ManagedAria2Client(manager), store);
        await coordinator.AddAsync(url, directory: runtime.Output, fileName: "public-ranges.bin",
            expectedSha256: Convert.ToHexString(SHA256.HashData(bytes)), httpConnections: 4);
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(25))
        {
            await coordinator.RefreshAsync();
            if (Assert.Single(store.Current.Downloads).Progress.Status is DownloadStatus.Completed or DownloadStatus.Failed) break;
            await Task.Delay(50);
        }
        var entry = Assert.Single(store.Current.Downloads);
        Assert.True(origin.BoundedRequests > 0, "Pinned aria2 did not request a bounded range through the public transport.");
        if (entry.Progress.Status == DownloadStatus.Completed)
        {
            Assert.Equal(VerificationStatus.Verified, entry.Verification);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(entry.Job.Destination));
        }
        else
        {
            Assert.False(File.Exists(entry.Job.Destination));
            var stage = Path.Combine(runtime.Output, ".aria-gui-continuity-" + entry.Job.Id.ToString("N"),
                entry.Attempt.Id.ToString("N"), "public-ranges.bin");
            Assert.True(File.Exists(stage), "Failure lost the private staged payload.");
        }
    }

    private sealed class RangedOrigin(byte[] bytes, bool changedRedirect, bool truncatedRange) : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentBag<Task> _handlers = [];
        private Task? _loop;
        public int Port { get; private set; }
        public int BoundedRequests;
        public RangedOrigin Start()
        {
            _listener.Start(); Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _loop = Task.Run(AcceptAsync);
            return this;
        }
        public async ValueTask<Stream> ConnectAsync(IPEndPoint endpoint, IPAddress approved, CancellationToken token)
        {
            Assert.Equal(approved, endpoint.Address);
            Assert.Equal(Port, endpoint.Port);
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try { await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, Port), token); return new NetworkStream(socket, ownsSocket: true); }
            catch { socket.Dispose(); throw; }
        }
        private async Task AcceptAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                    _handlers.Add(ServeAsync(await _listener.AcceptTcpClientAsync(_stop.Token)));
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
        }
        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                    var request = await reader.ReadLineAsync(_stop.Token) ?? "";
                    var path = request.Split(' ').ElementAtOrDefault(1) ?? "/";
                    string? line; long start = 0, end = bytes.Length - 1; var bounded = false;
                    while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(_stop.Token)))
                    {
                        if (!line.StartsWith("Range: bytes=", StringComparison.OrdinalIgnoreCase)) continue;
                        var parts = line[13..].Split('-');
                        bounded = parts.Length == 2 && long.TryParse(parts[0], out start) && long.TryParse(parts[1], out end);
                    }
                    if (bounded) Interlocked.Increment(ref BoundedRequests);
                    if (path == "/start")
                    {
                        var destination = changedRedirect && bounded ? "/changed" : "/stable";
                        var redirect = $"HTTP/1.1 302 Found\r\nLocation: http://public.fixture.invalid:{Port}{destination}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(redirect), _stop.Token);
                        return;
                    }
                    if (start < 0 || end >= bytes.Length || end < start) return;
                    var declared = end - start + 1;
                    var actual = declared - (bounded && truncatedRange ? 1 : 0);
                    var status = bounded ? "206 Partial Content" : "200 OK";
                    var headers = $"HTTP/1.1 {status}\r\nETag: \"strong\"\r\nContent-Length: {declared}\r\n"
                        + (bounded ? $"Content-Range: bytes {start}-{end}/{bytes.Length}\r\n" : "")
                        + "Content-Type: application/octet-stream\r\nConnection: close\r\n\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), _stop.Token);
                    for (var offset = start; offset < start + actual; offset += 16384)
                    {
                        var count = (int)Math.Min(16384, start + actual - offset);
                        await stream.WriteAsync(bytes.AsMemory((int)offset, count), _stop.Token);
                        await Task.Delay(2, _stop.Token);
                    }
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException or ObjectDisposedException) { }
            }
        }
        public async ValueTask DisposeAsync()
        {
            _stop.Cancel(); _listener.Stop();
            if (_loop is not null) await _loop;
            await Task.WhenAll(_handlers.ToArray());
            _stop.Dispose();
        }
    }
}
