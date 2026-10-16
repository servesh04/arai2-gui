using System.Net;
using AriaGui.Infrastructure.Downloads;

namespace AriaGui.Infrastructure.Tests;

// Shared by the isolated owner process and the adopting test process.
internal sealed class M3ProcessOrigin(int delayMilliseconds = 2) : IContinuityOriginTransport
{
    internal static byte[] Bytes { get; } = Enumerable.Range(0, 24 * 1024 * 1024).Select(index => (byte)(index % 251)).ToArray();
    private const string Resource = "http://127.0.0.1/m3-process.bin";
    public Task<HttpResponseMessage> OpenAsync(long offset, string? ifMatch, CancellationToken token) => Task.FromResult(Response(0, Bytes.Length - 1, false));
    public Task<HttpResponseMessage> OpenRangeAsync(long start, long end, string? ifMatch, CancellationToken token) => Task.FromResult(Response(start, end, true));
    private HttpResponseMessage Response(long start, long end, bool bounded)
    {
        var response = new HttpResponseMessage(bounded ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
        { RequestMessage = new(HttpMethod.Get, Resource), Content = new StreamContent(new SlowStream(Bytes, (int)start, (int)(end - start + 1), delayMilliseconds)) };
        response.Headers.ETag = new("\"m3-process-entity\"");
        response.Content.Headers.ContentLength = end - start + 1;
        if (bounded) response.Content.Headers.ContentRange = new(start, end, Bytes.Length);
        return response;
    }
    private sealed class SlowStream(byte[] bytes, int start, int length, int delayMilliseconds) : Stream
    {
        private int _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (_position >= length) return 0;
            await Task.Delay(delayMilliseconds, token);
            var count = Math.Min(buffer.Length, Math.Min(16384, length - _position));
            bytes.AsSpan(start + _position, count).CopyTo(buffer.Span);
            _position += count;
            return count;
        }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
