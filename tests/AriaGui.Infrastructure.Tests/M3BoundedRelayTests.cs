using System.Net;
using System.Net.Http.Headers;
using AriaGui.Infrastructure.Downloads;

namespace AriaGui.Infrastructure.Tests;

public sealed class M3BoundedRelayTests : IDisposable
{
    private const string Resource = "https://example.invalid/payload.bin";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AriaGUI.M3RangeTests", Guid.NewGuid().ToString("N"));
    private sealed class Origin(Func<long, long, HttpResponseMessage> response) : IContinuityOriginTransport
    {
        public int Calls;
        public Task<HttpResponseMessage> OpenAsync(long offset, string? ifMatch, CancellationToken token) => throw new Xunit.Sdk.XunitException("Unexpected sequential request.");
        public Task<HttpResponseMessage> OpenRangeAsync(long start, long end, string? ifMatch, CancellationToken token)
        { Interlocked.Increment(ref Calls); return Task.FromResult(response(start, end)); }
    }
    private sealed class RacingOrigin : IContinuityOriginTransport
    {
        private readonly TaskCompletionSource _both = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;
        public Task<HttpResponseMessage> OpenAsync(long offset, string? ifMatch, CancellationToken token) => throw new Xunit.Sdk.XunitException("Unexpected sequential request.");
        public async Task<HttpResponseMessage> OpenRangeAsync(long start, long end, string? ifMatch, CancellationToken token)
        {
            if (Interlocked.Increment(ref _calls) == 2) _both.TrySetResult();
            await _both.Task.WaitAsync(token);
            var response = Response(start, end, 100, Enumerable.Repeat((byte)(start == 0 ? 1 : 2), 50).ToArray());
            if (start != 0) response.Headers.ETag = new EntityTagHeaderValue("\"other\"");
            return response;
        }
    }
    private static HttpResponseMessage Response(long start, long end, long total, byte[] body)
    {
        var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
        { RequestMessage = new(HttpMethod.Get, Resource), Content = new ByteArrayContent(body) };
        response.Headers.ETag = new EntityTagHeaderValue("\"entity\"");
        response.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, total);
        return response;
    }
    [Fact]
    public async Task Bounded_zero_and_disjoint_second_range_validate_exact_bytes_and_can_overlap()
    {
        var bytes = Enumerable.Range(0, 1024).Select(i => (byte)i).ToArray();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        var origin = new Origin((start, end) =>
        {
            var payload = bytes[(int)start..((int)end + 1)];
            var response = Response(start, end, bytes.Length, payload);
            response.Content = new StreamContent(new GatedStream(payload, () =>
            {
                if (Interlocked.Increment(ref reads) == 2) entered.TrySetResult();
                return release.Task;
            }));
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, bytes.Length);
            response.Content.Headers.ContentLength = payload.Length;
            return response;
        });
        var store = new HttpRepresentationStore(_root, Guid.NewGuid());
        var relay = new HttpContinuityRelay(Resource, store, origin, false, true);
        using var first = new MemoryStream(); using var second = new MemoryStream();
        var a = relay.RelayBoundedAsync(0, 511, (_, _) => Task.CompletedTask, first, default);
        await WaitUntilAsync(() => Volatile.Read(ref reads) == 1);
        var b = relay.RelayBoundedAsync(512, 1023, (_, _) => Task.CompletedTask, second, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        release.TrySetResult();
        Assert.True(await a); Assert.True(await b);
        Assert.Equal(2, origin.Calls);
        Assert.Equal(bytes, first.ToArray().Concat(second.ToArray()).ToArray());
        Assert.Equal(3, (await store.LoadAsync(default))!.Version);
        using var sequential = new MemoryStream();
        Assert.False(await relay.RelayAsync(0, (_, _) => Task.CompletedTask, sequential, default));
    }
    [Fact]
    public async Task Cancelling_one_owned_range_does_not_cancel_a_sibling()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var origin = new Origin((start, end) =>
        {
            var payload = Enumerable.Repeat((byte)(start == 0 ? 1 : 2), 50).ToArray();
            var response = Response(start, end, 100, payload);
            if (start == 0)
            {
                response.Content = new StreamContent(new GatedStream(payload, () => { entered.TrySetResult(); return release.Task; }));
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, 100);
                response.Content.Headers.ContentLength = payload.Length;
            }
            return response;
        });
        var relay = new HttpContinuityRelay(Resource, new HttpRepresentationStore(_root, Guid.NewGuid()), origin, false, true);
        using var cancelled = new CancellationTokenSource();
        using var first = new MemoryStream(); using var sibling = new MemoryStream();
        var pending = relay.RelayBoundedAsync(0, 49, (_, _) => Task.CompletedTask, first, cancelled.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var other = relay.RelayBoundedAsync(50, 99, (_, _) => Task.CompletedTask, sibling, default);
        Assert.True(await other.WaitAsync(TimeSpan.FromSeconds(5)));
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Empty(first.ToArray());
        Assert.Equal(Enumerable.Repeat((byte)2, 50), sibling.ToArray());
        release.TrySetResult();
    }
    [Fact]
    public async Task Duplicate_and_overlapping_active_ranges_are_rejected_without_origin_contact_or_bytes()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var origin = new Origin((start, end) =>
        {
            var payload = Enumerable.Repeat((byte)7, (int)(end - start + 1)).ToArray();
            var response = Response(start, end, 100, payload);
            if (start == 0)
            {
                response.Content = new StreamContent(new GatedStream(payload, () => { entered.TrySetResult(); return release.Task; }));
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, 100);
                response.Content.Headers.ContentLength = payload.Length;
            }
            return response;
        });
        var relay = new HttpContinuityRelay(Resource, new HttpRepresentationStore(_root, Guid.NewGuid()), origin, false, true);
        using var first = new MemoryStream(); using var duplicate = new MemoryStream();
        using var overlap = new MemoryStream(); using var disjoint = new MemoryStream();
        var pending = relay.RelayBoundedAsync(0, 49, (_, _) => Task.CompletedTask, first, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(await relay.RelayBoundedAsync(0, 49, (_, _) => throw new Xunit.Sdk.XunitException("Duplicate exposed headers."), duplicate, default));
        Assert.False(await relay.RelayBoundedAsync(25, 74, (_, _) => throw new Xunit.Sdk.XunitException("Overlap exposed headers."), overlap, default));
        Assert.True(await relay.RelayBoundedAsync(50, 99, (_, _) => Task.CompletedTask, disjoint, default));
        Assert.Equal(2, origin.Calls);
        Assert.Empty(duplicate.ToArray()); Assert.Empty(overlap.ToArray());
        release.TrySetResult();
        Assert.True(await pending.WaitAsync(TimeSpan.FromSeconds(5)));
        using var after = new MemoryStream();
        Assert.True(await relay.RelayBoundedAsync(0, 49, (_, _) => Task.CompletedTask, after, default));
        Assert.Equal(3, origin.Calls);
    }
    [Theory]
    [InlineData(100, 1000)]
    [InlineData(1000, 100)]
    public async Task Idle_or_total_deadline_cancels_buffered_range_without_exposing_bytes(int idleMilliseconds, int totalMilliseconds)
    {
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var origin = new Origin((start, end) =>
        {
            var payload = new byte[(int)(end - start + 1)];
            var response = Response(start, end, 100, payload);
            response.Content = new StreamContent(new GatedStream(payload, () => never.Task));
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, 100);
            response.Content.Headers.ContentLength = payload.Length;
            return response;
        });
        var limits = new ContinuityResourceLimits
        {
            HeaderTimeout = TimeSpan.FromSeconds(2),
            IdleTimeout = TimeSpan.FromMilliseconds(idleMilliseconds),
            MaximumDuration = TimeSpan.FromMilliseconds(totalMilliseconds)
        };
        var relay = new HttpContinuityRelay(Resource, new HttpRepresentationStore(_root, Guid.NewGuid()), origin, false, true, limits);
        using var destination = new MemoryStream(); var headers = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => relay.RelayBoundedAsync(0, 49,
            (_, _) => { headers++; return Task.CompletedTask; }, destination, default));
        Assert.Equal(0, headers);
        Assert.Empty(destination.ToArray());
    }
    [Fact]
    public async Task Racing_initial_responses_commit_only_one_representation()
    {
        var origin = new RacingOrigin();
        var store = new HttpRepresentationStore(_root, Guid.NewGuid());
        var relay = new HttpContinuityRelay(Resource, store, origin, false, true);
        using var first = new MemoryStream(); using var second = new MemoryStream();
        var a = relay.RelayBoundedAsync(0, 49, (_, _) => Task.CompletedTask, first, default);
        var b = relay.RelayBoundedAsync(50, 99, (_, _) => Task.CompletedTask, second, default);
        var results = await Task.WhenAll(a, b).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(results, value => value);
        Assert.Equal(50, first.Length + second.Length);
        Assert.Contains((await store.LoadAsync(default))!.StrongETag, new[] { "\"entity\"", "\"other\"" });
    }
    [Theory]
    [InlineData("200")]
    [InlineData("weak")]
    [InlineData("missing-etag")]
    [InlineData("changed-etag")]
    [InlineData("changed-resource")]
    [InlineData("encoded")]
    [InlineData("multipart")]
    [InlineData("bad-start")]
    [InlineData("bad-end")]
    [InlineData("bad-total")]
    [InlineData("bad-length")]
    [InlineData("truncated")]
    public async Task Invalid_bounded_origin_cannot_expose_headers_or_bytes(string mutation)
    {
        var store = new HttpRepresentationStore(_root, Guid.NewGuid());
        await store.SaveBeforeBodyAsync(new(3, Resource, "\"entity\"", 100, "identity"), default);
        var origin = new Origin((start, end) =>
        {
            var response = Response(start, end, 100, new byte[20]);
            switch (mutation)
            {
                case "200": response.StatusCode = HttpStatusCode.OK; break;
                case "weak": response.Headers.ETag = new EntityTagHeaderValue("\"entity\"", true); break;
                case "missing-etag": response.Headers.ETag = null; break;
                case "changed-etag": response.Headers.ETag = new EntityTagHeaderValue("\"other\""); break;
                case "changed-resource": response.RequestMessage = new(HttpMethod.Get, "https://example.invalid/other"); break;
                case "encoded": response.Content.Headers.ContentEncoding.Add("gzip"); break;
                case "multipart": response.Content.Headers.ContentType = new MediaTypeHeaderValue("multipart/byteranges"); break;
                case "bad-start": response.Content.Headers.ContentRange = new ContentRangeHeaderValue(start + 1, end, 100); break;
                case "bad-end": response.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, end + 1, 100); break;
                case "bad-total": response.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, 101); break;
                case "bad-length": response.Content.Headers.ContentLength = 19; break;
                case "truncated": response.Content = new ByteArrayContent(new byte[19]); response.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, 100); response.Content.Headers.ContentLength = 20; break;
            }
            return response;
        });
        var relay = new HttpContinuityRelay(Resource, store, origin, true, true);
        using var body = new MemoryStream(); var headers = 0;
        Assert.False(await relay.RelayBoundedAsync(20, 39, (_, _) => { headers++; return Task.CompletedTask; }, body, default));
        Assert.Equal(0, headers); Assert.Empty(body.ToArray());
    }
    private sealed class GatedStream(byte[] bytes, Func<Task> wait) : MemoryStream(bytes, writable: false)
    {
        private int _first;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _first, 1) == 0) await wait().WaitAsync(cancellationToken);
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var end = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition() && DateTime.UtcNow < end) await Task.Delay(10);
        Assert.True(condition());
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
