using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AriaGui.Infrastructure.Tests;

// A controlled source of known bytes. No external sites, credentials, or certificates.
public sealed class LocalHttpServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentBag<Task> _handlers = [];
    private readonly Task _loop;
    public byte[] Payload { get; } = Enumerable.Range(0, 256 * 1024).Select(i => (byte)(i % 251)).ToArray();
    public string Url { get; }
    public int Requests;
    private readonly ConcurrentDictionary<string, int> _routeRequests = new();
    public bool RecoverPartial;
    public LocalHttpServer()
    {
        _listener.Start();
        Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/known-file.bin";
        _loop = AcceptAsync();
    }
    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                _handlers.Add(ServeAsync(client));
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (SocketException) when (_stop.IsCancellationRequested) { }
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
                long start = 0;
                string? line;
                while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(_stop.Token)))
                    if (line.StartsWith("Range: bytes=", StringComparison.OrdinalIgnoreCase)) long.TryParse(line[13..].Split('-')[0], out start);
                Interlocked.Increment(ref Requests);
                var route = request.Split(' ').ElementAtOrDefault(1) ?? "/";
                var count = _routeRequests.AddOrUpdate(route, 1, (_, old) => old + 1);
                var partial = request.Contains("/partial", StringComparison.Ordinal) && !Volatile.Read(ref RecoverPartial);
                var missing = request.Contains("/missing", StringComparison.Ordinal) || (request.Contains("/flaky", StringComparison.Ordinal) && count == 1)
                    || (partial && count > 1);
                var empty = request.Contains("/empty", StringComparison.Ordinal);
                var chunked = request.Contains("/chunked", StringComparison.Ordinal);
                var slow = request.Contains("/slow", StringComparison.Ordinal);
                var body = missing || empty ? Array.Empty<byte>() : Payload;
                if (start < 0 || start >= body.Length) start = 0;
                var range = start > 0 && !missing;
                var headers = $"HTTP/1.1 {(missing ? "404 Not Found" : range ? "206 Partial Content" : "200 OK")}\r\nContent-Type: application/octet-stream\r\n"
                    + (range ? $"Content-Range: bytes {start}-{body.Length - 1}/{body.Length}\r\n" : "") + "Accept-Ranges: bytes\r\n"
                    + (chunked ? "Transfer-Encoding: chunked\r\n" : $"Content-Length: {body.Length - start}\r\n") + "Connection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), _stop.Token);
                if (chunked)
                {
                    for (var offset = 0; offset < body.Length; offset += 8192)
                    {
                        var size = Math.Min(8192, body.Length - offset);
                        await stream.WriteAsync(Encoding.ASCII.GetBytes($"{size:x}\r\n"), _stop.Token);
                        await stream.WriteAsync(body.AsMemory(offset, size), _stop.Token);
                        await stream.WriteAsync("\r\n"u8.ToArray(), _stop.Token);
                        await Task.Delay(30, _stop.Token);
                    }
                    await stream.WriteAsync("0\r\n\r\n"u8.ToArray(), _stop.Token);
                }
                else if (slow)
                {
                    for (var offset = (int)start; offset < body.Length; offset += 4096)
                    {
                        await stream.WriteAsync(body.AsMemory(offset, Math.Min(4096, body.Length - offset)), _stop.Token);
                        await Task.Delay(150, _stop.Token);
                    }
                }
                else if (partial && !missing) await stream.WriteAsync(body.AsMemory(0, 32768), _stop.Token);
                else await stream.WriteAsync(body.AsMemory((int)start), _stop.Token);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
        }
    }
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        await _loop;
        await Task.WhenAll(_handlers);
        _stop.Dispose();
    }
}
