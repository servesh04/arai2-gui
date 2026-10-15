using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using AriaGui.Infrastructure.Downloads;

namespace AriaGui.Infrastructure.Tests;

internal sealed class ControlledLoopbackOrigin : IContinuityOriginTransport, IDisposable
{
    private readonly Uri _source;
    private readonly HttpClient _http;
    public ControlledLoopbackOrigin(string source, Action<int>? countSocketRead = null)
    {
        _source = new(source);
        if (_source.Scheme != "http" || !IPAddress.TryParse(_source.Host, out var address) || !IPAddress.IsLoopback(address)) throw new ArgumentException();
        var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None };
        if (countSocketRead is not null)
            handler.ConnectCallback = async (context, token) =>
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(IPAddress.Loopback, context.DnsEndPoint.Port, token);
                    return new CountedSocketStream(new NetworkStream(socket, ownsSocket: true), countSocketRead);
                }
                catch { socket.Dispose(); throw; }
            };
        _http = new(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }
    public async Task<HttpResponseMessage> OpenAsync(long offset, string? ifMatch, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _source); request.Headers.AcceptEncoding.ParseAdd("identity");
        if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
        if (ifMatch is not null) request.Headers.IfMatch.ParseAdd(ifMatch);
        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
    }
    public void Dispose() => _http.Dispose();

    private sealed class CountedSocketStream(Stream inner, Action<int> count) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken token) => inner.FlushAsync(token);
        public override int Read(byte[] buffer, int offset, int size)
        { var read = inner.Read(buffer, offset, size); count(read); return read; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        { var read = await inner.ReadAsync(buffer, token); count(read); return read; }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int size, CancellationToken token) =>
            ReadAsync(buffer.AsMemory(offset, size), token).AsTask();
        public override void Write(byte[] buffer, int offset, int size) => inner.Write(buffer, offset, size);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default) => inner.WriteAsync(buffer, token);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
        public override ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
