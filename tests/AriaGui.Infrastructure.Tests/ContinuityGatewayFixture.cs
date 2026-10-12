using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using AriaGui.Infrastructure.Downloads;

namespace AriaGui.Infrastructure.Tests;

// Only the bounded HTTP listener/parser and loopback transport are fixture code.
// Before-body policy, durable metadata and stream relay are Infrastructure primitives.
internal sealed class ContinuityGatewayFixture : IAsyncDisposable, IContinuityOriginTransport
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None });
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentBag<Task> _handlers = [];
    private readonly Uri _source;
    private readonly HttpContinuityRelay _relay;
    private readonly Task _accept;
    private readonly string _route = "/" + Guid.NewGuid().ToString("N");
    public string Url { get; }
    public ConcurrentQueue<string> Decisions { get; } = new();
    public ContinuityGatewayFixture(Uri source, HttpRepresentationStore store, bool retained = false)
    {
        if (source.Scheme != "http" || !IPAddress.TryParse(source.Host, out var address) || !IPAddress.IsLoopback(address)) throw new ArgumentException();
        _source = source; _relay = new(source.OriginalString, store, this, retained, enableExperimental: true);
        _listener.Start(); Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}{_route}"; _accept = AcceptAsync();
    }
    public async Task<HttpResponseMessage> OpenAsync(long offset, string? ifMatch, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _source);
        request.Headers.AcceptEncoding.ParseAdd("identity");
        if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
        if (ifMatch is not null) request.Headers.IfMatch.ParseAdd(ifMatch);
        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
    }
    private async Task AcceptAsync()
    {
        try { while (!_stop.IsCancellationRequested) _handlers.Add(ServeAsync(await _listener.AcceptTcpClientAsync(_stop.Token))); }
        catch (Exception ex) when (_stop.IsCancellationRequested && ex is OperationCanceledException or SocketException) { }
    }
    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
        {
            deadline.CancelAfter(TimeSpan.FromSeconds(15)); var token = deadline.Token; var stream = client.GetStream();
            try
            {
                var bytes = new List<byte>(); var one = new byte[1];
                while (bytes.Count < 8192)
                {
                    if (await stream.ReadAsync(one, token) != 1) return;
                    bytes.Add(one[0]); if (bytes.Count >= 4 && bytes.TakeLast(4).SequenceEqual("\r\n\r\n"u8.ToArray())) break;
                }
                var lines = Encoding.ASCII.GetString(bytes.ToArray()).Split("\r\n");
                if (bytes.Count >= 8192 || lines[0] != $"GET {_route} HTTP/1.1") { await RejectAsync(stream, token); return; }
                var ranges = lines.Where(line => line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase)).ToArray(); long offset = 0;
                if (ranges.Length > 1 || (ranges.Length == 1 && (!RangeHeaderValue.TryParse(ranges[0][6..].Trim(), out var range)
                    || range.Unit != "bytes" || range.Ranges.Count != 1 || range.Ranges.Single().From is not long from || from < 0)))
                { await RejectAsync(stream, token); return; }
                if (ranges.Length == 1) offset = RangeHeaderValue.Parse(ranges[0][6..].Trim()).Ranges.Single().From!.Value;
                var accepted = await _relay.RelayAsync(offset, async (response, cancellation) =>
                {
                    Decisions.Enqueue($"allow:{response.Offset}:{(response.Offset > 0 ? 206 : 200)}");
                    var headers = $"HTTP/1.1 {(response.Offset > 0 ? 206 : 200)} OK\r\nContent-Length: {response.Length}\r\nETag: {response.StrongETag}\r\n"
                        + (response.Offset > 0 ? $"Content-Range: bytes {response.Offset}-{response.Total - 1}/{response.Total}\r\n" : "")
                        + "Accept-Ranges: bytes\r\nConnection: close\r\n\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), cancellation);
                }, stream, token);
                if (!accepted) await RejectAsync(stream, token);
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException) { Decisions.Enqueue("body-ended"); }
        }
    }
    private async Task RejectAsync(Stream stream, CancellationToken token)
    {
        Decisions.Enqueue("reject:representation");
        await stream.WriteAsync("HTTP/1.1 412 Precondition Failed\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), token);
    }
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel(); _listener.Stop(); await _accept; await Task.WhenAll(_handlers); _http.Dispose(); _stop.Dispose();
    }
}
