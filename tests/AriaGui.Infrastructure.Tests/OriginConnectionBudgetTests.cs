using AriaGui.Infrastructure.Downloads;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AriaGui.Infrastructure.Tests;

public sealed class OriginConnectionBudgetTests
{
    [Fact]
    public async Task Actual_origin_socket_budget_is_shared_across_transport_instances()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var stop = new CancellationTokenSource();
        var clients = new System.Collections.Concurrent.ConcurrentDictionary<int, TcpClient>();
        var accepted = 0;
        var server = Task.Run(async () =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var client = await listener.AcceptTcpClientAsync(stop.Token);
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var stream = client.GetStream();
                            var buffer = new byte[4096]; var count = 0;
                            while (count < buffer.Length && !Encoding.ASCII.GetString(buffer, 0, count).Contains("\r\n\r\n"))
                            {
                                var read = await stream.ReadAsync(buffer.AsMemory(count), stop.Token);
                                if (read == 0) return;
                                count += read;
                            }
                            var number = Interlocked.Increment(ref accepted);
                            clients[number] = client;
                            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                                "HTTP/1.1 206 Partial Content\r\nETag: \"stable\"\r\nContent-Range: bytes 0-0/1\r\nContent-Length: 1\r\nConnection: close\r\n\r\n"), stop.Token);
                            await Task.Delay(Timeout.InfiniteTimeSpan, stop.Token);
                        }
                        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
                        finally { client.Dispose(); }
                    });
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
        });
        var approved = IPAddress.Parse("8.8.8.8");
        var host = Guid.NewGuid().ToString("N") + ".fixture.invalid";
        ValueTask<Stream> Connect(IPEndPoint endpoint, CancellationToken token)
        {
            Assert.Equal(approved, endpoint.Address);
            return ConnectLoopbackAsync(port, token);
        }
        using var first = new PublicHttpOriginTransport($"http://{host}:{port}/one",
            (_, _) => Task.FromResult(new[] { approved }), Connect, null);
        using var second = new PublicHttpOriginTransport($"http://{host}:{port}/two",
            (_, _) => Task.FromResult(new[] { approved }), Connect, null);
        var responses = new List<HttpResponseMessage>();
        var extraTransports = new List<PublicHttpOriginTransport>();
        try
        {
            for (var i = 0; i < 4; i++) responses.Add(await (i % 2 == 0 ? first : second).OpenRangeAsync(0, 0, null, default));
            Assert.Equal(4, Volatile.Read(ref accepted));
            using var blocked = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second.OpenRangeAsync(0, 0, null, blocked.Token));
            Assert.Equal(4, Volatile.Read(ref accepted));
            await clients[1].GetStream().WriteAsync(new byte[] { 7 });
            clients[1].Dispose(); // Honor Connection: close once the one-byte response completes.
            Assert.Equal(new byte[] { 7 }, await responses[0].Content.ReadAsByteArrayAsync());
            responses[0].Dispose();
            using var replacementDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            responses.Add(await second.OpenRangeAsync(0, 0, null, replacementDeadline.Token));
            Assert.Equal(5, Volatile.Read(ref accepted));
            // Four distinct actual hostnames each consume four responses on the
            // same physical test listener. A fifth host is blocked globally.
            for (var group = 0; group < 3; group++)
            {
                var additionalHost = Guid.NewGuid().ToString("N") + ".fixture.invalid";
                var transport = new PublicHttpOriginTransport($"http://{additionalHost}:{port}/group-{group}",
                    (_, _) => Task.FromResult(new[] { approved }), Connect, null);
                extraTransports.Add(transport);
                for (var index = 0; index < 4; index++) responses.Add(await transport.OpenRangeAsync(0, 0, null, default));
            }
            var globalHost = Guid.NewGuid().ToString("N") + ".fixture.invalid";
            using var global = new PublicHttpOriginTransport($"http://{globalHost}:{port}/global",
                (_, _) => Task.FromResult(new[] { approved }), Connect, null);
            var atGlobalLimit = Volatile.Read(ref accepted);
            using var globalBlocked = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => global.OpenRangeAsync(0, 0, null, globalBlocked.Token));
            Assert.Equal(atGlobalLimit, Volatile.Read(ref accepted));
            await clients[2].GetStream().WriteAsync(new byte[] { 8 });
            clients[2].Dispose();
            Assert.Equal(new byte[] { 8 }, await responses[1].Content.ReadAsByteArrayAsync());
            responses[1].Dispose();
            using var globalDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            responses.Add(await global.OpenRangeAsync(0, 0, null, globalDeadline.Token));
            Assert.Equal(atGlobalLimit + 1, Volatile.Read(ref accepted));
        }
        finally
        {
            foreach (var response in responses) response.Dispose();
            foreach (var transport in extraTransports) transport.Dispose();
            stop.Cancel(); listener.Stop();
            foreach (var client in clients.Values) client.Dispose();
            await server;
        }
    }
    private static async ValueTask<Stream> ConnectLoopbackAsync(int port, CancellationToken token)
    {
        var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try { await client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), token); return new NetworkStream(client, ownsSocket: true); }
        catch { client.Dispose(); throw; }
    }
    [Fact]
    public async Task Same_actual_origin_is_bounded_and_cancellation_releases_global_slot()
    {
        var host = Guid.NewGuid().ToString("N") + ".example.invalid";
        var held = new List<IDisposable>();
        try
        {
            for (var index = 0; index < 4; index++) held.Add(await OriginConnectionBudget.AcquireAsync(host, 443, default));
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => OriginConnectionBudget.AcquireAsync(host, 443, cancel.Token));
            using var different = await OriginConnectionBudget.AcquireAsync(host, 80, default);
            held[0].Dispose();
            using var released = await OriginConnectionBudget.AcquireAsync(host, 443, default);
        }
        finally { foreach (var lease in held) lease.Dispose(); }
    }
}
